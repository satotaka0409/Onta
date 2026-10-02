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

    /// <summary>アンカー位置合わせでずらさずに済ませる誤差（サンプル）。</summary>
    private const int AnchorToleranceSamples = 16;

    /// <summary>アンカー後、FH 変調部が揃ってからこの秒数 FH が確定しなければアンカーを捨てる。</summary>
    private const int AnchorGiveUpSeconds = 2;

    private readonly FileWavCodec _codec;
    private readonly object _sync = new();
    private readonly int _sampleRate;
    private readonly DecodeRuntimeTuning _tuning;
    private readonly TimeSpan _pollInterval;
    private readonly int _minAttemptSamples;
    private readonly ProgressiveDecodeState _progressive;

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
    private readonly PreambleAnchorDetector _anchorDetector;
    private readonly int _fhDataOffset;
    private readonly int _fhModulatedSamples;
    private bool _anchored;
    private int _anchorPos;
    private TapeSpeedResampler? _resampler;
    private bool _inverted;
    private readonly InputLowCutFilter _lowCutLeft;
    private readonly InputLowCutFilter _lowCutRight;

    /// <summary>
    /// デコードセッションを初期化します。
    /// </summary>
    /// <param name="codec">段階デコード本体。</param>
    /// <param name="sampleRate">入力 PCM のサンプルレート。</param>
    /// <param name="channelMode">入力チャネル構成。</param>
    /// <param name="tuning">復号探索・反復回数の調整値。</param>
    /// <param name="pollInterval">ワーカーループのポーリング間隔。</param>
    /// <param name="minAttemptSeconds">復号試行を開始する最小蓄積秒数。</param>
    /// <param name="sharedStatus">画面と共有する状態メモリ。省略時は専用ボードを生成します。</param>
    public RealtimeDecodeSession(
        FileWavCodec codec,
        int sampleRate,
        ChannelMode channelMode,
        DecodeRuntimeTuning? tuning = null,
        TimeSpan? pollInterval = null,
        int minAttemptSeconds = 2,
        CoreExecutionStatusBoard? sharedStatus = null)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _sampleRate = Math.Max(1, sampleRate);
        _tuning = tuning ?? DecodeRuntimeTuning.Default;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        _minAttemptSamples = _sampleRate * Math.Max(1, minAttemptSeconds);
        _stereo = channelMode == ChannelMode.Stereo;
        _progressive = new ProgressiveDecodeState(sharedStatus);
        _fhDataOffset = codec.FileHeaderDataOffsetSamples;
        _fhModulatedSamples = codec.FileHeaderModulatedSamples;
        _anchorDetector = codec.CreateAnchorDetector();
        _lowCutLeft = new InputLowCutFilter(_sampleRate);
        _lowCutRight = new InputLowCutFilter(_sampleRate);
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
    /// <param name="left">追記する L チャネル複素 PCM（Imag=0 可）。</param>
    /// <param name="right">追記する R チャネル複素 PCM。ステレオ時は left と同長、モノラル時は参照しません。</param>
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

            var inputLeft = left.ToArray();
            var inputRight = _stereo ? right.ToArray() : Array.Empty<Complex>();
            _lowCutLeft.ProcessInPlace(inputLeft);
            _lowCutRight.ProcessInPlace(inputRight);

            if (_resampler is not null)
            {
                _resampler.Process(inputLeft, inputRight, out var correctedLeft, out var correctedRight);
                if (_inverted)
                {
                    NegateInPlace(correctedLeft);
                    NegateInPlace(correctedRight);
                }

                AppendBufferLocked(correctedLeft, correctedRight);
            }
            else
            {
                if (_inverted)
                {
                    NegateInPlace(inputLeft);
                    NegateInPlace(inputRight);
                }

                AppendBufferLocked(inputLeft, inputRight);
            }

            // FH 前の暴走蓄積を防ぐ（ライブ無信号時のみ。ファイル逐次は背圧で抑える）
            var maxPre = _sampleRate * MaxPreHeaderSeconds;
            if (!_inputCompleted && !_progressive.HeaderReady && _count > maxPre)
            {
                var drop = _count - (maxPre / 2);
                DropFront(drop);
                _anchored = false;
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
    /// <returns>バッファ量・試行回数・復元バイト・エラー等を含む公開スナップショット。</returns>
    public RealtimeDecodeSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot with { BufferedSamples = _count };
        }
    }

    /// <summary>
    /// 共有状態メモリのスナップショットを読み取ります（セッションロックは取りません）。
    /// </summary>
    /// <returns>画面反映用のコア実行状態（進捗・エラー率・I-Q 等）。</returns>
    public CoreExecutionStatus ReadExecutionStatus() => _progressive.ReadExecutionStatus();

    /// <summary>
    /// <see cref="ReadExecutionStatus"/> の互換エイリアスです。
    /// </summary>
    /// <returns>画面反映用のコア実行状態（進捗・エラー率・I-Q 等）。</returns>
    public CoreExecutionStatus QueryExecutionStatus() => ReadExecutionStatus();

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
    /// <param name="decoded">復元済みバイト列。未復元時は空配列。</param>
    /// <returns>取り出せた場合 true。未復元または既に取り出し済みの場合 false。</returns>
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
    /// デコードループを停止し、ワーカーとキャンセルトークンを解放します。
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
    /// バッファ監視と段階復号を繰り返すバックグラウンド処理です。
    /// </summary>
    /// <param name="token">停止要求トークン。</param>
    /// <returns>停止要求まで処理を続けるタスク。</returns>
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
                    var waitingForAnchor = false;
                    if (!inputDone && !progressive.HeaderReady && !progressive.Completed)
                    {
                        waitingForAnchor = !UpdateAnchorLocked();
                    }

                    if (progressive.Completed)
                    {
                        shouldTry = false;
                    }
                    else if (!waitingForAnchor
                             && _count >= _minAttemptSamples
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

    /// <summary>
    /// 消費済み先頭サンプルを圧縮してメモリ使用量を抑えます。
    /// </summary>
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

    /// <summary>
    /// FH 確定前に、FH 手前の無変調区間の終端（アンカー）を探してバッファを送信先頭基準へ揃えます。
    /// </summary>
    /// <returns>アンカーで位置合わせ済みで復号を試せる場合 true。探索中は false。</returns>
    /// <remarks>FH 復号はバッファ先頭からの固定オフセットを前提にするため、録音開始とテープ再生のずれをここで吸収する。</remarks>
    private bool UpdateAnchorLocked()
    {
        if (_anchored)
        {
            if (!IsFileHeaderBufferedLocked())
            {
                return false;
            }

            if (_count <= _anchorPos + _fhModulatedSamples + (_sampleRate * AnchorGiveUpSeconds))
            {
                return true;
            }

            // FH 変調部が揃っても確定しなかった → このアンカーは捨て、以降から次の無変調区間を探す
            DropFront(Math.Max(0, _anchorPos));
            _anchored = false;
            _progressive.Reset();
            _progressive.StatusBoard.BeginRun("(リアルタイム受信 / 再同期)");
            _lastAttemptCount = 0;
        }

        if (!_anchorDetector.TryAdvance(_left.AsSpan(0, _count), out var anchor))
        {
            return false;
        }

        var step = _anchorDetector.LastPeriod / _anchorDetector.NominalPeriod;
        if (Math.Abs(step - 1.0) > TapeSpeedResampler.NegligibleDeviation)
        {
            ApplySpeedCorrectionLocked(step);
            anchor = (int)Math.Round(anchor / step);
        }

        if (_anchorDetector.LastPolarityInverted)
        {
            // 経路で極性が反転している → 以降の入力も含めて戻す
            NegateInPlace(_left.AsSpan(0, _count));
            if (_stereo)
            {
                NegateInPlace(_right.AsSpan(0, _count));
            }

            _inverted = !_inverted;
        }

        var shift = anchor - _fhDataOffset;
        if (shift > AnchorToleranceSamples)
        {
            DropFront(shift);
        }
        else if (shift < -AnchorToleranceSamples)
        {
            PrependSilenceLocked(-shift);
        }

        _anchorDetector.Reset();
        _streamBase = 0;
        _anchored = true;
        _anchorPos = _fhDataOffset;
        _lastAttemptCount = 0;
        return IsFileHeaderBufferedLocked();
    }

    /// <summary>
    /// アンカー以降に FH 変調部がすべて溜まったかを返します。
    /// </summary>
    /// <returns>FH 全体を復号に渡せる場合 true。</returns>
    /// <remarks>FH が途中までの試行はワウ探索が空振りして長時間かかり、その間に入力が溜まりすぎるため待つ。</remarks>
    private bool IsFileHeaderBufferedLocked() =>
        _count >= _anchorPos + _fhModulatedSamples + (_sampleRate / 5);

    /// <summary>
    /// 測定したテープ速度比でバッファを公称速度へ戻し、以降の入力も同じ比で補正するようにします。
    /// </summary>
    /// <param name="step">公称 1 サンプルあたりの入力サンプル数（テープが速いと 1 より大きい）。</param>
    /// <remarks>既に補正中なら、補正後バッファに残った差分だけを掛け足す。</remarks>
    private void ApplySpeedCorrectionLocked(double step)
    {
        Complex[] correctedLeft;
        Complex[] correctedRight;
        if (_resampler is null)
        {
            _resampler = new TapeSpeedResampler(step, _stereo);
            _resampler.Process(
                _left.AsSpan(0, _count),
                _stereo ? _right.AsSpan(0, _count) : ReadOnlySpan<Complex>.Empty,
                out correctedLeft,
                out correctedRight);
        }
        else
        {
            correctedLeft = TapeSpeedResampler.Resample(_left, _count, step);
            correctedRight = _stereo ? TapeSpeedResampler.Resample(_right, _count, step) : Array.Empty<Complex>();
            _resampler.Step *= step;
        }

        _count = 0;
        AppendBufferLocked(correctedLeft, correctedRight);
    }

    /// <summary>
    /// サンプルの符号を反転します（極性反転の補正用）。
    /// </summary>
    /// <param name="samples">反転する PCM。</param>
    private static void NegateInPlace(Span<Complex> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = -samples[i];
        }
    }

    /// <summary>
    /// バッファ末尾へ PCM を追記します。
    /// </summary>
    /// <param name="left">L PCM。</param>
    /// <param name="right">R PCM（モノラル時は参照しない）。</param>
    private void AppendBufferLocked(ReadOnlySpan<Complex> left, ReadOnlySpan<Complex> right)
    {
        if (left.Length == 0)
        {
            return;
        }

        EnsureCapacity(_count + left.Length);
        left.CopyTo(_left.AsSpan(_count, left.Length));
        if (_stereo)
        {
            var n = Math.Min(left.Length, right.Length);
            right[..n].CopyTo(_right.AsSpan(_count, n));
        }

        _count += left.Length;
    }

    /// <summary>
    /// バッファ先頭へ無音を挿入します（無変調区間の途中から録音が始まった場合の位置合わせ用）。
    /// </summary>
    /// <param name="samples">挿入する無音サンプル数。</param>
    private void PrependSilenceLocked(int samples)
    {
        if (samples <= 0)
        {
            return;
        }

        EnsureCapacity(_count + samples);
        Array.Copy(_left, 0, _left, samples, _count);
        Array.Clear(_left, 0, samples);
        if (_stereo)
        {
            Array.Copy(_right, 0, _right, samples, _count);
            Array.Clear(_right, 0, samples);
        }

        _count += samples;
    }

    /// <summary>
    /// 先頭から指定サンプル数を破棄してバッファを前詰めします。
    /// </summary>
    /// <param name="drop">破棄する先頭サンプル数。</param>
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
        _anchorPos -= drop;
        _anchorDetector.OnFrontDropped(drop);
    }

    /// <summary>
    /// 入力追記に必要なバッファ容量を確保します。
    /// </summary>
    /// <param name="needed">必要サンプル数。</param>
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

    /// <summary>
    /// 破棄済みインスタンス操作を検出して例外を送出します。
    /// </summary>
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
/// <param name="IsRunning">デコードワーカーが稼働中かどうか。</param>
/// <param name="BufferedSamples">内部バッファに保持している PCM サンプル数。</param>
/// <param name="DecodeAttemptCount">これまでに行った段階復号の試行回数。</param>
/// <param name="LastDecodedAtUtc">最後にファイル復元へ成功した UTC 時刻。未成功時は null。</param>
/// <param name="DecodedBytes">取り出し待ちの復元バイト列。<see cref="RealtimeDecodeSession.TryConsumeDecoded"/> で消費します。</param>
/// <param name="LastError">直近のエラーメッセージ。正常時は null。</param>
public readonly record struct RealtimeDecodeSnapshot(
    bool IsRunning,
    int BufferedSamples,
    int DecodeAttemptCount,
    DateTime? LastDecodedAtUtc,
    byte[]? DecodedBytes,
    string? LastError)
{
    /// <summary>
    /// 待機状態の初期スナップショットです。
    /// </summary>
    public static RealtimeDecodeSnapshot Idle { get; } = new(
        IsRunning: false,
        BufferedSamples: 0,
        DecodeAttemptCount: 0,
        LastDecodedAtUtc: null,
        DecodedBytes: null,
        LastError: null);
}

