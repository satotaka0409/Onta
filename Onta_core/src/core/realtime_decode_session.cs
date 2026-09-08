using System.Numerics;

namespace Onta.Core;

/// <summary>
/// 受信 PCM を逐次投入し、別スレッドで一定間隔ごとに復号を試みるセッションです。
/// </summary>
public sealed class RealtimeDecodeSession : IDisposable
{
    private readonly FileWavCodec _codec;
    private readonly object _sync = new();
    private readonly ComplexRingBuffer _left;
    private readonly ComplexRingBuffer _right;
    private readonly int _sampleRate;
    private readonly DecodeRuntimeTuning _tuning;
    private readonly TimeSpan _pollInterval;
    private readonly int _minAttemptSamples;
    private Complex[] _leftSnapshot = Array.Empty<Complex>();
    private Complex[] _rightSnapshot = Array.Empty<Complex>();

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
        int minAttemptSeconds = 2,
        int maxBufferSeconds = 30)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _sampleRate = Math.Max(1, sampleRate);
        _tuning = tuning ?? DecodeRuntimeTuning.Default;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        _minAttemptSamples = _sampleRate * Math.Max(1, minAttemptSeconds);
        var maxBufferedSamples = _sampleRate * Math.Max(Math.Max(1, maxBufferSeconds), Math.Max(1, minAttemptSeconds));
        _left = new ComplexRingBuffer(maxBufferedSamples);
        _right = new ComplexRingBuffer(maxBufferedSamples);
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
            _left.Write(left);
            _right.Write(right);
            var buffered = Math.Min(_left.Count, _right.Count);

            _snapshot = _snapshot with
            {
                BufferedSamples = buffered,
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
                Complex[]? left = null;
                Complex[]? right = null;
                var shouldTry = false;
                lock (_sync)
                {
                    var buffered = Math.Min(_left.Count, _right.Count);
                    if (buffered >= _minAttemptSamples && buffered >= _lastAttemptSamples + (_sampleRate / 2))
                    {
                        if (_left.TryGetContiguousWindow(out var leftWindow, out var leftCount)
                            && _right.TryGetContiguousWindow(out var rightWindow, out var rightCount)
                            && leftCount == rightCount)
                        {
                            left = leftWindow;
                            right = rightWindow;
                            buffered = leftCount;
                        }
                        else
                        {
                            EnsureSnapshotCapacity(buffered);
                            _left.CopyTo(_leftSnapshot.AsSpan(0, buffered));
                            _right.CopyTo(_rightSnapshot.AsSpan(0, buffered));
                            left = _leftSnapshot;
                            right = _rightSnapshot;
                        }

                        _lastAttemptSamples = buffered;
                        shouldTry = true;
                    }
                }

                if (shouldTry && left is not null && right is not null)
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

    private void EnsureSnapshotCapacity(int sampleCount)
    {
        if (_leftSnapshot.Length != sampleCount)
        {
            _leftSnapshot = new Complex[sampleCount];
        }

        if (_rightSnapshot.Length != sampleCount)
        {
            _rightSnapshot = new Complex[sampleCount];
        }
    }

    private sealed class ComplexRingBuffer
    {
        private readonly Complex[] _buffer;
        private int _head;
        private int _count;

        public ComplexRingBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _buffer = new Complex[capacity];
        }

        public int Count => _count;

        public void Write(ReadOnlySpan<Complex> source)
        {
            var capacity = _buffer.Length;
            for (var i = 0; i < source.Length; i++)
            {
                var tail = (_head + _count) % capacity;
                _buffer[tail] = source[i];
                if (_count < capacity)
                {
                    _count++;
                }
                else
                {
                    _head = (_head + 1) % capacity;
                }
            }
        }

        public void CopyTo(Span<Complex> destination)
        {
            if (destination.Length < _count)
            {
                throw new ArgumentException("Destination span is smaller than buffered sample count.", nameof(destination));
            }

            if (_count == 0)
            {
                return;
            }

            var firstLength = Math.Min(_count, _buffer.Length - _head);
            _buffer.AsSpan(_head, firstLength).CopyTo(destination);
            var secondLength = _count - firstLength;
            if (secondLength > 0)
            {
                _buffer.AsSpan(0, secondLength).CopyTo(destination.Slice(firstLength));
            }
        }

        public bool TryGetContiguousWindow(out Complex[] buffer, out int count)
        {
            if (_head == 0 && _count == _buffer.Length)
            {
                buffer = _buffer;
                count = _count;
                return true;
            }

            buffer = Array.Empty<Complex>();
            count = 0;
            return false;
        }
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
