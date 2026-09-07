using System.Numerics;

namespace Onta.Core;

/// <summary>
/// 受信 PCM を逐次投入し、別スレッドで一定間隔ごとに復号を試みるセッションです。
/// </summary>
public sealed class RealtimeDecodeSession : IDisposable
{
    private readonly FileWavCodec _codec;
    private readonly object _sync = new();
    private readonly List<Complex> _left = [];
    private readonly List<Complex> _right = [];
    private readonly int _sampleRate;
    private readonly DecodeRuntimeTuning _tuning;
    private readonly TimeSpan _pollInterval;
    private readonly int _minAttemptSamples;

    private CancellationTokenSource? _cts;
    private Task? _worker;
    private int _lastAttemptSamples;
    private bool _disposed;

    private RealtimeDecodeSnapshot _snapshot = RealtimeDecodeSnapshot.Idle;

    public RealtimeDecodeSession(
        FileWavCodec codec,
        int sampleRate,
        DecodeRuntimeTuning? tuning = null,
        TimeSpan? pollInterval = null,
        int minAttemptSeconds = 2)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _sampleRate = Math.Max(1, sampleRate);
        _tuning = tuning ?? DecodeRuntimeTuning.Default;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        _minAttemptSamples = _sampleRate * Math.Max(1, minAttemptSeconds);
    }

    public void Start()
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            if (_worker is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => WorkerLoop(_cts.Token), _cts.Token);
            _snapshot = _snapshot with
            {
                IsRunning = true,
                LastError = null
            };
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? worker;
        lock (_sync)
        {
            cts = _cts;
            worker = _worker;
            _cts = null;
            _worker = null;
            _snapshot = _snapshot with { IsRunning = false };
        }

        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (worker is not null)
        {
            try
            {
                worker.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // no-op
            }
        }
    }

    public void AppendSamples(ReadOnlySpan<Complex> left, ReadOnlySpan<Complex> right)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            for (var i = 0; i < left.Length; i++)
            {
                _left.Add(left[i]);
            }

            for (var i = 0; i < right.Length; i++)
            {
                _right.Add(right[i]);
            }

            _snapshot = _snapshot with
            {
                BufferedSamples = _left.Count,
                LastError = null
            };
        }
    }

    public RealtimeDecodeSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    public bool TryConsumeDecoded(out byte[] decoded)
    {
        lock (_sync)
        {
            if (_snapshot.DecodedBytes is null)
            {
                decoded = Array.Empty<byte>();
                return false;
            }

            decoded = _snapshot.DecodedBytes;
            _snapshot = _snapshot with { DecodedBytes = null };
            return true;
        }
    }

    private async Task WorkerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Complex[] left;
                Complex[] right;
                var shouldTry = false;
                lock (_sync)
                {
                    if (_left.Count >= _minAttemptSamples && _left.Count >= _lastAttemptSamples + (_sampleRate / 2))
                    {
                        left = _left.ToArray();
                        right = _right.ToArray();
                        _lastAttemptSamples = _left.Count;
                        shouldTry = true;
                    }
                    else
                    {
                        left = Array.Empty<Complex>();
                        right = Array.Empty<Complex>();
                    }
                }

                if (shouldTry)
                {
                    var decoded = _codec.DecodePcmSamplesToFileBytes(left, right, correctWow: true, wowParams: null, tuning: _tuning);
                    lock (_sync)
                    {
                        _snapshot = _snapshot with
                        {
                            DecodedBytes = decoded,
                            LastDecodedAtUtc = DateTime.UtcNow,
                            DecodeAttemptCount = _snapshot.DecodeAttemptCount + 1,
                            LastError = null
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _snapshot = _snapshot with
                    {
                        DecodeAttemptCount = _snapshot.DecodeAttemptCount + 1,
                        LastError = ex.Message
                    };
                }
            }

            await Task.Delay(_pollInterval, token).ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RealtimeDecodeSession));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }
}

public readonly record struct RealtimeDecodeSnapshot(
    bool IsRunning,
    int BufferedSamples,
    int DecodeAttemptCount,
    DateTime? LastDecodedAtUtc,
    byte[]? DecodedBytes,
    string? LastError)
{
    public static RealtimeDecodeSnapshot Idle { get; } = new(
        IsRunning: false,
        BufferedSamples: 0,
        DecodeAttemptCount: 0,
        LastDecodedAtUtc: null,
        DecodedBytes: null,
        LastError: null);
}
