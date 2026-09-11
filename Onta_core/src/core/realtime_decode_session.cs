using System.Numerics;

namespace Onta.Core;

/// <summary>
/// リアルタイムで受信PCMを蓄積し、段階的デコードを継続実行するセッションです。
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
    private readonly ProgressiveDecodeState _progressive = new();
    private Complex[] _leftSnapshot = Array.Empty<Complex>();
    private Complex[] _rightSnapshot = Array.Empty<Complex>();

    private CancellationTokenSource? _cts;
    private Task? _worker;
    private int _lastAttemptSamples;
    private bool _disposed;

    private RealtimeDecodeSnapshot _snapshot = RealtimeDecodeSnapshot.Idle;

    /// <summary>
    /// デコードセッションを初期化します。
    /// </summary>
    /// <param name="codec">段階的デコードを実行するコーデック。</param>
    /// <param name="sampleRate">入力サンプルレート。</param>
    /// <param name="tuning">復号時のランタイム調整値。null の場合は既定値。</param>
    /// <param name="pollInterval">ワーカーループのポーリング間隔。null の場合は既定値。</param>
    /// <param name="minAttemptSeconds">デコード試行を開始する最小蓄積秒数。</param>
    /// <param name="maxBufferSeconds">内部リングバッファの最大保持秒数。</param>
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
            _progressive.StatusBoard.BeginRun("(リアルタイム受信)");
            _snapshot = _snapshot with
            {
                IsRunning = true,
                LastError = null
            };
        }
    }

    /// <summary>
    /// デコードループを停止し、実行中タスクを終了します。
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

    /// <summary>
    /// 新しい複素サンプル列をバッファへ追記します。
    /// </summary>
    /// <param name="left">左チャンネルの複素サンプル列。</param>
    /// <param name="right">右チャンネルの複素サンプル列。</param>
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

    /// <summary>
    /// 現在の実行スナップショットを返します。
    /// </summary>
    /// <returns>現在の実行スナップショット。</returns>
    public RealtimeDecodeSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    /// <summary>
    /// 現在の詳細実行状態を返します。
    /// </summary>
    /// <returns>UI表示向けの実行状態。</returns>
    public CoreExecutionStatus QueryExecutionStatus()
    {
        lock (_sync)
        {
            return _progressive.QueryExecutionStatus();
        }
    }

    /// <summary>
    /// 復元済みバイト列がある場合に1回だけ取り出します。
    /// </summary>
    /// <param name="decoded">成功時に取り出した復元バイト列。</param>
    /// <returns>取り出しに成功した場合 true。</returns>
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

    /// <summary>
    /// バッファ量に応じて段階的デコードを試行するワーカーループです。
    /// </summary>
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
                    if (_progressive.Completed)
                    {
                        shouldTry = false;
                    }
                    else
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
                }

                if (shouldTry && left is not null && right is not null)
                {
                    ProgressiveDecodeState progressive;
                    lock (_sync)
                    {
                        if (left.Length < _progressive.SourceLength)
                        {
                            _progressive.Reset();
                        }

                        progressive = _progressive;
                    }

                    var status = _codec.DecodePcmSamplesProgressive(
                        left,
                        right,
                        progressive,
                        correctWow: true,
                        wowParams: null,
                        tuning: _tuning);

                    lock (_sync)
                    {
                        if (status == ProgressiveDecodeStatus.Completed && progressive.CompletedFile is not null)
                        {
                            _snapshot = _snapshot with
                            {
                                DecodedBytes = progressive.CompletedFile,
                                LastDecodedAtUtc = DateTime.UtcNow,
                                DecodeAttemptCount = _snapshot.DecodeAttemptCount + 1,
                                LastError = null
                            };
                        }
                        else if (status == ProgressiveDecodeStatus.Failed)
                        {
                            _snapshot = _snapshot with
                            {
                                DecodeAttemptCount = _snapshot.DecodeAttemptCount + 1,
                                LastError = progressive.LastError
                            };
                            progressive.Reset();
                            _lastAttemptSamples = 0;
                        }
                        else
                        {
                            _snapshot = _snapshot with
                            {
                                DecodeAttemptCount = _snapshot.DecodeAttemptCount + 1,
                                LastError = null
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _progressive.Reset();
                    _lastAttemptSamples = 0;
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

    /// <summary>
    /// 破棄済みなら例外を送出します。
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RealtimeDecodeSession));
        }
    }

    /// <summary>
    /// セッションを停止して関連リソースを解放します。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }

    /// <summary>
    /// スナップショット用配列の容量を現在サンプル数に合わせます。
    /// </summary>
    /// <param name="sampleCount">sampleCount を指定します。</param>
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

    /// <summary>
    /// 複素サンプルを固定長で保持するリングバッファです。
    /// </summary>
    private sealed class ComplexRingBuffer
    {
        private readonly Complex[] _buffer;
        private int _head;
        private int _count;

        /// <summary>
        /// 指定容量でバッファを作成します。
        /// </summary>
        public ComplexRingBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _buffer = new Complex[capacity];
        }

        /// <summary>
        /// 現在バッファされているサンプル数です。
        /// </summary>
        public int Count => _count;

        /// <summary>
        /// サンプル列を書き込み、容量超過時は最古データを上書きします。
        /// </summary>
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

        /// <summary>
        /// 現在バッファされている内容を時系列順にコピーします。
        /// </summary>
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

        /// <summary>
        /// バッファ全体が連続領域として参照できる場合にその配列を返します。
        /// </summary>
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

/// <summary>
/// リアルタイムデコード処理の公開スナップショットです。
/// </summary>
public readonly record struct RealtimeDecodeSnapshot(
    bool IsRunning,
    int BufferedSamples,
    int DecodeAttemptCount,
    DateTime? LastDecodedAtUtc,
    byte[]? DecodedBytes,
    string? LastError)
{
    /// <summary>
    /// 停止状態の初期スナップショットです。
    /// </summary>
    public static RealtimeDecodeSnapshot Idle { get; } = new(
        IsRunning: false,
        BufferedSamples: 0,
        DecodeAttemptCount: 0,
        LastDecodedAtUtc: null,
        DecodedBytes: null,
        LastError: null);
}

