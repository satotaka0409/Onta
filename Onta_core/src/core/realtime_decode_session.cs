using System.Numerics;

namespace Onta.Core;

/// <summary>
/// 受信 PCM を逐次投入し、別スレッドで一定間隔ごとに増分復号を試みるセッションです。
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
    /// コーデックとサンプリング条件を指定してリアルタイム復号セッションを生成します。
    /// </summary>
    /// <param name="codec">WAV/PCM 復号に用いるコーデック。</param>
    /// <param name="sampleRate">入力サンプルレート（Hz）。</param>
    /// <param name="tuning">復号ランタイム調整。省略時は既定値。</param>
    /// <param name="pollInterval">ワーカーのポーリング間隔。省略時は 500 ms。</param>
    /// <param name="minAttemptSeconds">初回復号試行に必要な最小バッファ秒数。</param>
    /// <param name="maxBufferSeconds">リングバッファの最大保持秒数。</param>
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
    /// バックグラウンド復号ワーカーを開始します。
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
    /// 復号ワーカーを停止し、キャンセルを待ちます。
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
    /// L/R PCM サンプルをリングバッファへ追記します。
    /// </summary>
    /// <param name="left">L チャンネル複素サンプル列。</param>
    /// <param name="right">R チャンネル複素サンプル列。</param>
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
    /// セッション状態のスナップショットを返します。
    /// </summary>
    /// <returns>実行中フラグ・バッファ量・復号結果などを含む状態。</returns>
    public RealtimeDecodeSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    /// <summary>
    /// 画面から実行状況（進捗 / エラー率 / I-Q）を問い合わせます。
    /// </summary>
    /// <returns>進捗・エラー率・I-Q を含む実行状況。</returns>
    public CoreExecutionStatus QueryExecutionStatus()
    {
        lock (_sync)
        {
            return _progressive.QueryExecutionStatus();
        }
    }

    /// <summary>
    /// 完了済み復号バイト列があれば消費して返します。
    /// </summary>
    /// <param name="decoded">取得できた復号データ。無い場合は空配列。</param>
    /// <returns>復号データを返した場合は <see langword="true"/>。</returns>
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
    /// バッファ増分を監視し、条件を満たしたら増分復号を試行するワーカーループです。
    /// </summary>
    /// <param name="token">キャンセル用トークン。</param>
    /// <returns>キャンセルまたは終了で完了するタスク。</returns>
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
    /// 破棄済みなら <see cref="ObjectDisposedException"/> を投げます。
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RealtimeDecodeSession));
        }
    }

    /// <summary>
    /// ワーカーを停止し、セッションを破棄します。
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
    /// 復号試行用スナップショット配列の長さを必要サンプル数に合わせます。
    /// </summary>
    /// <param name="sampleCount">必要なサンプル数。</param>
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
    /// 固定容量の複素サンプルリングバッファです。
    /// </summary>
    private sealed class ComplexRingBuffer
    {
        private readonly Complex[] _buffer;
        private int _head;
        private int _count;

        /// <summary>
        /// 指定容量のリングバッファを生成します。
        /// </summary>
        /// <param name="capacity">保持できる最大サンプル数。</param>
        public ComplexRingBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _buffer = new Complex[capacity];
        }

        /// <summary>
        /// 現在保持しているサンプル数です。
        /// </summary>
        public int Count => _count;

        /// <summary>
        /// サンプル列を追記します。満杯時は最古を上書きします。
        /// </summary>
        /// <param name="source">追記する複素サンプル列。</param>
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
        /// 保持中サンプルを先頭から連続領域へコピーします。
        /// </summary>
        /// <param name="destination">コピー先（保持件数以上の長さが必要）。</param>
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
        /// バッファが先頭から満杯で連続している場合、内部配列をそのまま返します。
        /// </summary>
        /// <param name="buffer">連続窓として使える内部配列。失敗時は空配列。</param>
        /// <param name="count">有効サンプル数。失敗時は 0。</param>
        /// <returns>連続満杯窓を返せた場合は <see langword="true"/>。</returns>
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
/// リアルタイム復号セッションの公開状態スナップショットです。
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
    /// 未開始時の既定スナップショットです。
    /// </summary>
    public static RealtimeDecodeSnapshot Idle { get; } = new(
        IsRunning: false,
        BufferedSamples: 0,
        DecodeAttemptCount: 0,
        LastDecodedAtUtc: null,
        DecodedBytes: null,
        LastError: null);
}
