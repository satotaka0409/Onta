using System.Numerics;

namespace Onta.Core;

/// <summary>
/// リアルタイム PCM を逐次取り込み、段階デコードするセッションです。
/// 消費済み先頭は随時捨て、WAV 化や無制限蓄積は行いません。
/// </summary>
public sealed class RealtimeDecodeSession : IDisposable
{
    /// <summary>FH 取得前に保持する最大秒数（超えたら先頭を捨てて再同期）。</summary>
    private const int MaxPreHeaderSeconds = 20;

    /// <summary>カーソル後方に残すルックバック秒数。</summary>
    private const double CompactLookbackSeconds = 0.5;

    private readonly FileWavCodec _codec;
    private readonly object _sync = new();
    private readonly int _sampleRate;
    private readonly DecodeRuntimeTuning _tuning;
    private readonly TimeSpan _pollInterval;
    private readonly int _minAttemptSamples;
    private readonly ProgressiveDecodeState _progressive = new();

    private Complex[] _left = Array.Empty<Complex>();
    private Complex[] _right = Array.Empty<Complex>();
    private int _count;
    private long _streamBase;
    private bool _stereo;

    private CancellationTokenSource? _cts;
    private Task? _worker;
    private int _lastAttemptCount;
    private bool _disposed;
    private bool _inputCompleted;
    private int _postInputStallCount;
    private long _lastPostInputCursor;
    private int _lastPostInputBuffered;
    private RealtimeDecodeSnapshot _snapshot = RealtimeDecodeSnapshot.Idle;

    /// <summary>
    /// デコードセッションを初期化します。
    /// </summary>
    public RealtimeDecodeSession(
        FileWavCodec codec,
        int sampleRate,
        ChannelMode channelMode,
        DecodeRuntimeTuning? tuning = null,
        TimeSpan? pollInterval = null,
        int minAttemptSeconds = 2)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _sampleRate = Math.Max(1, sampleRate);
        _tuning = tuning ?? DecodeRuntimeTuning.Default;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        _minAttemptSamples = _sampleRate * Math.Max(1, minAttemptSeconds);
        _stereo = channelMode == ChannelMode.Stereo;
    }

    /// <summary>
    /// バックグラウンドのデコードループを開始します。
    /// </summary>
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
            _snapshot = _snapshot with { IsRunning = true, LastError = null };
        }
    }

    /// <summary>
    /// デコードループを停止します。
    /// </summary>
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

        cts?.Cancel();
        cts?.Dispose();
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

    /// <summary>
    /// PCM チャンクを追記します（リング上書きではなく、消費後に先頭圧縮します）。
    /// </summary>
    public void AppendSamples(ReadOnlySpan<Complex> left, ReadOnlySpan<Complex> right)
    {
        ThrowIfDisposed();
        if (left.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            if (_stereo)
            {
                if (right.Length != left.Length)
                {
                    return;
                }
            }

            EnsureCapacity(_count + left.Length);
            left.CopyTo(_left.AsSpan(_count, left.Length));
            if (_stereo)
            {
                right.CopyTo(_right.AsSpan(_count, right.Length));
            }

            _count += left.Length;

            // FH 前の暴走蓄積を防ぐ（ライブ無信号時のみ。ファイル逐次は背圧で抑える）
            var maxPre = _sampleRate * MaxPreHeaderSeconds;
            if (!_inputCompleted && !_progressive.HeaderReady && _count > maxPre)
            {
                var drop = _count - (maxPre / 2);
                DropFront(drop);
                _progressive.Reset();
                _progressive.StatusBoard.BeginRun("(リアルタイム受信 / 再同期)");
                _lastAttemptCount = 0;
            }

            _snapshot = _snapshot with
            {
                BufferedSamples = _count,
                LastError = null
            };
        }
    }

    /// <summary>
    /// 入力側の供給が終了したことを通知します（WAV EOF 等）。
    /// </summary>
    public void NotifyInputCompleted()
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            _inputCompleted = true;
            _postInputStallCount = 0;
            _lastPostInputCursor = _progressive.WarpedCursor;
            _lastPostInputBuffered = _count;
        }
    }

    /// <summary>
    /// 現在バッファ内のサンプル数です（背圧制御用）。
    /// </summary>
    public int BufferedSampleCount
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// 現在の実行スナップショットを返します。
    /// </summary>
    public RealtimeDecodeSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot with { BufferedSamples = _count };
        }
    }

    /// <summary>
    /// UI 向け実行状態を返します。
    /// </summary>
    public CoreExecutionStatus QueryExecutionStatus()
    {
        lock (_sync)
        {
            return _progressive.QueryExecutionStatus();
        }
    }

    /// <summary>
    /// 内部の段階デコード状態です（完了ペイロード参照用）。
    /// </summary>
    public ProgressiveDecodeState ProgressiveState
    {
        get
        {
            lock (_sync)
            {
                return _progressive;
            }
        }
    }

    /// <summary>
    /// 復元済みバイト列がある場合に1回だけ取り出します。
    /// </summary>
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
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
                ProgressiveDecodeState progressive;

                var inputDone = false;
                var allowIncomplete = true;
                lock (_sync)
                {
                    progressive = _progressive;
                    inputDone = _inputCompleted;
                    if (progressive.Completed)
                    {
                        shouldTry = false;
                    }
                    else if (_count >= _minAttemptSamples
                             && _count >= _lastAttemptCount + Math.Max(1, _sampleRate / 10))
                    {
                        shouldTry = true;
                    }
                    else if (inputDone && !progressive.Completed && _count > 0)
                    {
                        // EOF 後は増分条件を緩めて残バッファを吐き切る。
                        // カーソルが微動してもバッファが増えなければ最終試行へ進める。
                        shouldTry = true;
                        allowIncomplete = _postInputStallCount < 5;
                    }
                    else if (inputDone && !progressive.Completed && _count <= 0)
                    {
                        progressive.LastError ??= "Incomplete PCM stream for decode.";
                        progressive.StatusBoard.Complete(faulted: true, progressive.LastError);
                        _snapshot = _snapshot with
                        {
                            IsRunning = false,
                            LastError = progressive.LastError
                        };
                    }

                    if (shouldTry)
                    {
                        left = new Complex[_count];
                        Array.Copy(_left, 0, left, 0, _count);
                        if (_stereo)
                        {
                            right = new Complex[_count];
                            Array.Copy(_right, 0, right, 0, _count);
                        }
                        else
                        {
                            right = Array.Empty<Complex>();
                        }

                        progressive.StreamSampleBase = _streamBase;
                        _lastAttemptCount = _count;
                    }
                }

                if (shouldTry && left is not null && right is not null)
                {
                    var status = _codec.DecodePcmSamplesProgressive(
                        left,
                        right,
                        progressive,
                        correctWow: true,
                        wowParams: null,
                        tuning: _tuning,
                        allowIncomplete: allowIncomplete);

                    lock (_sync)
                    {
                        if (status == ProgressiveDecodeStatus.Completed
                            && progressive.CompletedFile is not null)
                        {
                            _snapshot = _snapshot with
                            {
                                DecodedBytes = progressive.CompletedFile,
                                LastDecodedAtUtc = DateTime.UtcNow,
                                DecodeAttemptCount = _snapshot.DecodeAttemptCount + 1,
                                LastError = null,
                                IsRunning = false
                            };
                            progressive.StatusBoard.Complete(faulted: false);
                        }
                        else if (status == ProgressiveDecodeStatus.Failed)
                        {
                            _snapshot = _snapshot with
                            {
                                DecodeAttemptCount = _snapshot.DecodeAttemptCount + 1,
                                LastError = progressive.LastError,
                                IsRunning = !allowIncomplete ? false : _snapshot.IsRunning
                            };
                            if (!allowIncomplete)
                            {
                                progressive.StatusBoard.Complete(
                                    faulted: true,
                                    progressive.LastError ?? "Decode failed.");
                            }
                            else if (progressive.HeaderReady && !inputDone)
                            {
                                CompactLocked();
                            }
                        }
                        else
                        {
                            _snapshot = _snapshot with
                            {
                                DecodeAttemptCount = _snapshot.DecodeAttemptCount + 1,
                                LastError = null
                            };
                            if (inputDone)
                            {
                                var cursor = progressive.WarpedCursor;
                                if (cursor == _lastPostInputCursor && _count == _lastPostInputBuffered)
                                {
                                    _postInputStallCount++;
                                }
                                else
                                {
                                    _postInputStallCount = 0;
                                    _lastPostInputCursor = cursor;
                                    _lastPostInputBuffered = _count;
                                }
                            }

                            // EOF 後に Compact すると末尾 BD に必要なサンプルを落としうる
                            if (!inputDone)
                            {
                                CompactLocked();
                            }
                        }

                        _snapshot = _snapshot with { BufferedSamples = _count };
                    }
                }
                else if (inputDone)
                {
                    lock (_sync)
                    {
                        if (!_progressive.Completed && _snapshot.IsRunning && _postInputStallCount >= 8)
                        {
                            var err = _progressive.LastError ?? "Incomplete PCM stream for decode.";
                            _progressive.LastError = err;
                            _progressive.StatusBoard.Complete(faulted: true, err);
                            _snapshot = _snapshot with { IsRunning = false, LastError = err };
                        }
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

            try
            {
                await Task.Delay(_pollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void CompactLocked()
    {
        if (_count <= 0)
        {
            return;
        }

        var lookback = (int)(_sampleRate * CompactLookbackSeconds);
        var cursor = Math.Clamp(_progressive.WarpedCursor, 0, _count);
        // 未処理サンプルは絶対に捨てない（高速 WAV 供給時の飛び越し防止）
        var drop = Math.Max(0, cursor - lookback);

        // 小さすぎる圧縮は頻度だけ増えるのでスキップ
        if (drop < _sampleRate / 20)
        {
            return;
        }

        DropFront(drop);
        _progressive.WarpedCursor = Math.Max(0, _progressive.WarpedCursor - drop);
        _progressive.SourceLength = _count;
        _progressive.StreamSampleBase = _streamBase;
        _lastAttemptCount = Math.Max(0, _lastAttemptCount - drop);
    }

    private void DropFront(int drop)
    {
        if (drop <= 0)
        {
            return;
        }

        drop = Math.Min(drop, _count);
        var remain = _count - drop;
        if (remain > 0)
        {
            Array.Copy(_left, drop, _left, 0, remain);
            if (_stereo)
            {
                Array.Copy(_right, drop, _right, 0, remain);
            }
        }

        _count = remain;
        _streamBase += drop;
    }

    private void EnsureCapacity(int needed)
    {
        if (_left.Length >= needed)
        {
            return;
        }

        var newSize = Math.Max(needed, Math.Max(1024, _left.Length * 2));
        Array.Resize(ref _left, newSize);
        if (_stereo)
        {
            Array.Resize(ref _right, newSize);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RealtimeDecodeSession));
        }
    }
}

/// <summary>
/// リアルタイムデコードの公開スナップショットです。
/// </summary>
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
