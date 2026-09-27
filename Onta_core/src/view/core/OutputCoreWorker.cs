using System.Numerics;
using Onta.Core;
using Onta.Performance;

namespace Onta.View.Core;

/// <summary>
/// 送信エンコード処理の進捗管理と中断制御を行うワーカーです。
/// </summary>
internal sealed class OutputCoreWorker
{
    private readonly object _sync = new();
    private readonly Queue<ErrorRateFrameKind> _frameEvents = [];
    private readonly CoreExecutionStatusBoard _vizBoard = new();
    private CoreProgressSnapshot _snapshot = CoreProgressSnapshot.Idle;
    private CoreCompletionResult? _completion;
    private RealtimePcmPlayer? _player;
    private CancellationTokenSource? _cts;
    /// <summary>再生側へ投入済みの総サンプル数です。</summary>
    private long _emittedSamples;
    private long _totalSamples;
    private int _sampleRate = 44100;
    private double _totalAudioSeconds;
    /// <summary>音声出力開始時刻（メーター用壁時計の起点）。</summary>
    private DateTime _audioProgressAnchorUtc;

    /// <summary>
    /// 送信中 FFT など可視化用の共有状態です。
    /// </summary>
    public CoreExecutionStatusBoard SharedVizStatus => _vizBoard;

    /// <summary>
    /// 送信ワーカーを開始します。
    /// </summary>
    /// <param name="settings">送信設定。</param>
    /// <param name="outputWavPath">WAV出力先パス。</param>
    /// <returns>開始に成功した場合 true。</returns>
    public bool TryStart(SendSettingsSnapshot settings, string? outputWavPath)
    {
        if (settings.WriteWav && string.IsNullOrWhiteSpace(outputWavPath))
        {
            throw new ArgumentException("WAV output path is required.", nameof(outputWavPath));
        }

        CancellationTokenSource cts;
        lock (_sync)
        {
            if (_snapshot.IsRunning)
            {
                return false;
            }

            _cts?.Dispose();
            cts = new CancellationTokenSource();
            _cts = cts;

            var fileName = string.IsNullOrWhiteSpace(settings.InputFilePath)
                ? "(未選択)"
                : Path.GetFileName(settings.InputFilePath);
            _snapshot = new CoreProgressSnapshot(
                IsRunning: true,
                IsCompleted: false,
                IsFaulted: false,
                ProgressPercent: 0,
                ElapsedAudioSeconds: 0,
                TotalAudioSeconds: 0,
                Stage: "Preparing",
                ErrorFrameKind: ErrorRateFrameKind.Fh,
                InputFileName: fileName,
                FileSizeText: "-",
                BlockCountText: "-",
                WowLeftPercent: 0,
                WowRightPercent: 0,
                ErrorRatePercent: 0,
                OutputWavPath: outputWavPath ?? string.Empty,
                StartedAtUtc: DateTime.UtcNow);
            _completion = null;
            _frameEvents.Clear();
            _emittedSamples = 0;
            _totalSamples = 0;
            _sampleRate = 44100;
            _totalAudioSeconds = 0;
            _player = null;
            _audioProgressAnchorUtc = default;
            _vizBoard.BeginRun(fileName);
        }

        _ = CoreBackgroundHost.RunAsync(_ => RunCore(settings, outputWavPath, cts.Token), cts.Token);
        return true;
    }

    /// <summary>
    /// 実行中ワーカーへ停止要求を送ります。
    /// </summary>
    /// <returns>停止要求を受理した場合 true。</returns>
    public bool RequestStop()
    {
        RealtimePcmPlayer? player;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            if (!_snapshot.IsRunning)
            {
                return false;
            }

            cts = _cts;
            player = _player;
            _snapshot = _snapshot with { Stage = "中断要求中" };
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 既に解放済みなら停止要求のみで終了する。
        }

        // 再生待機中のAddSamplesを早期解除するため先に破棄する。
        player?.Dispose();
        return true;
    }

    /// <summary>
    /// コアが書き込む送信進捗の共有スナップショットです。画面は定期読み取りします。
    /// </summary>
    public CoreProgressSnapshot SharedProgress => GetProgress();

    /// <summary>
    /// 現在の送信進捗スナップショットを返します。
    /// </summary>
    /// <returns>送信進捗。</returns>
    public CoreProgressSnapshot GetProgress()
    {
        CoreProgressSnapshot snapshot;
        RealtimePcmPlayer? player;
        long emittedSamples;
        long totalSamples;
        int sampleRate;
        double totalAudioSeconds;
        DateTime audioAnchorUtc;
        lock (_sync)
        {
            snapshot = _snapshot;
            if (!snapshot.IsRunning || _totalSamples <= 0)
            {
                return snapshot;
            }

            // WAV のみはエンコード側 UpdateSnapshot をそのまま返す。
            if (_player is null || _player.IsDisposed)
            {
                return snapshot;
            }

            player = _player;
            emittedSamples = _emittedSamples;
            totalSamples = _totalSamples;
            sampleRate = _sampleRate;
            totalAudioSeconds = _totalAudioSeconds;
            audioAnchorUtc = _audioProgressAnchorUtc;
        }

        // プレイヤー状態はロック外で読み、UI ポーリングと再生待機の競合を避ける。
        var elapsed = ResolveMeterElapsedSeconds(
            emittedSamples,
            player,
            sampleRate,
            totalSamples,
            totalAudioSeconds,
            audioAnchorUtc);
        var total = Math.Max(totalAudioSeconds, 1e-9);
        return snapshot with
        {
            ElapsedAudioSeconds = elapsed,
            ProgressPercent = Math.Clamp(100.0 * elapsed / total, 0.0, 100.0)
        };
    }

    /// <summary>
    /// フレームイベントを取り出して内部キューを空にします。
    /// </summary>
    /// <returns>取り出したイベント配列。</returns>
    public ErrorRateFrameKind[] ConsumeFrameEvents()
    {
        lock (_sync)
        {
            if (_frameEvents.Count == 0)
            {
                return [];
            }

            var events = _frameEvents.ToArray();
            _frameEvents.Clear();
            return events;
        }
    }

    /// <summary>
    /// 完了結果を1回だけ取り出します。
    /// </summary>
    /// <param name="completion">完了結果。</param>
    /// <returns>取り出せた場合 true。</returns>
    public bool TryConsumeCompletion(out CoreCompletionResult completion)
    {
        lock (_sync)
        {
            if (_completion is null)
            {
                completion = default;
                return false;
            }

            completion = _completion.Value;
            _completion = null;
            return true;
        }
    }

    /// <summary>
    /// Core のエンコード送信本体を実行します。
    /// </summary>
    /// <param name="settings">送信設定。</param>
    /// <param name="outputWavPath">WAV出力先。</param>
    /// <param name="cancellationToken">中断トークン。</param>
    private void RunCore(SendSettingsSnapshot settings, string? outputWavPath, CancellationToken cancellationToken)
    {
        RealtimePcmPlayer? player = null;
        WavWriter.StreamingPcm16Writer? wavWriter = null;
        try
        {
            UpdateSnapshot(0, 0, 0, "入力読込中", ErrorRateFrameKind.Fh, false, false, "-", "-");
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = File.ReadAllBytes(settings.InputFilePath);
            var blockCount = Math.Max(1, (bytes.Length + 8191) / 8192);
            var fileSizeText = $"{bytes.Length:N0} bytes";
            var blockCountText = blockCount.ToString();

            var profile = new FileWavCodecProfile(
                ActiveSubcarriers: settings.ActiveSubcarriers,
                ModulationScheme: settings.ModulationScheme,
                ChannelMode: settings.ChannelMode,
                BlockInterleaveFactor: settings.BlockInterleaveFactor);
            var estimate = FileWavCodec.EstimateTransmissionDuration(profile, bytes.Length);
            var totalSamples = Math.Max(1L, estimate.TotalSamples);
            var totalSeconds = estimate.TotalSeconds;
            long emittedSamples = 0;
            lock (_sync)
            {
                _emittedSamples = 0;
                _totalSamples = totalSamples;
                _sampleRate = profile.SampleRate;
                _totalAudioSeconds = totalSeconds;
            }

            UpdateSnapshot(0, 0, totalSeconds, "Profiling", ErrorRateFrameKind.Fh, false, false, fileSizeText, blockCountText);
            cancellationToken.ThrowIfCancellationRequested();

            if (settings.PlayAudio)
            {
                player = new RealtimePcmPlayer();
                player.Start(
                    settings.AudioDeviceNumber,
                    profile.SampleRate,
                    profile.ChannelMode,
                    settings.AudioVolume);
                lock (_sync)
                {
                    _player = player;
                    _audioProgressAnchorUtc = DateTime.UtcNow;
                }
            }

            if (settings.WriteWav)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(outputWavPath);
                wavWriter = WavWriter.CreateStreamingPcm16(
                    outputWavPath,
                    profile.SampleRate,
                    profile.SamplePeak,
                    profile.ChannelMode);
            }

            var stageLabel = settings.WriteWav
                ? (settings.PlayAudio ? "Encoding (WAV + Audio)" : "Encoding (WAV)")
                : "Encoding (Audio)";
            UpdateSnapshot(0, 0, totalSeconds, stageLabel, ErrorRateFrameKind.Bh, false, false, fileSizeText, blockCountText);
            var codec = new FileWavCodec(profile);
            var inputInfo = new FileInfo(settings.InputFilePath);
            var spectrumPublisher = new TxPcmSpectrumPublisher(
                _vizBoard,
                profile,
                blockCount);
            _ = codec.EncodeFileToSamples(
                bytes,
                inputInfo,
                frameKind =>
                {
                    spectrumPublisher.MarkFrameEnd(frameKind);
                    OnCoreFrameTransmitted(frameKind);
                },
                onPcmChunk: (leftChunk, rightChunk) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    spectrumPublisher.Push(leftChunk, rightChunk);
                    wavWriter?.WriteChunk(leftChunk, rightChunk);
                    if (player is not null)
                    {
                        void SyncPlayheadViz()
                        {
                            DateTime audioAnchorUtc;
                            lock (_sync)
                            {
                                audioAnchorUtc = _audioProgressAnchorUtc;
                            }

                            var liveElapsed = ResolveMeterElapsedSeconds(
                                emittedSamples,
                                player,
                                profile.SampleRate,
                                totalSamples,
                                totalSeconds,
                                audioAnchorUtc);
                            spectrumPublisher.PublishPlayhead(
                                ResolvePlayedSamples(emittedSamples, player));
                            UpdateSnapshot(
                                100.0 * liveElapsed / Math.Max(totalSeconds, 1e-9),
                                liveElapsed,
                                totalSeconds,
                                stageLabel,
                                ErrorRateFrameKind.Bd,
                                false,
                                false,
                                fileSizeText,
                                blockCountText);
                        }

                        // キュー投入時点のサンプル数から実時間進捗を近似する。
                        player.AddSamples(
                            leftChunk,
                            rightChunk,
                            onSamplesQueued: samplesQueued =>
                            {
                                emittedSamples += samplesQueued;
                                PublishEmittedSamples(emittedSamples);
                                SyncPlayheadViz();
                            },
                            onBufferWait: () =>
                            {
                                // バッファ待ち中も再生ヘッドに合わせて FFT/進捗を進める。
                                PublishEmittedSamples(emittedSamples);
                                SyncPlayheadViz();
                            });
                    }
                    else
                    {
                        emittedSamples += leftChunk.Length;
                        PublishEmittedSamples(emittedSamples);
                        // WAV のみ: 再生が無いのでエンコード位置をそのまま可視化する。
                        spectrumPublisher.PublishPlayhead(emittedSamples);
                        var elapsed = ResolveElapsedSeconds(
                            emittedSamples, player, profile.SampleRate, totalSamples);
                        UpdateSnapshot(
                            100.0 * elapsed / Math.Max(totalSeconds, 1e-9),
                            elapsed,
                            totalSeconds,
                            stageLabel,
                            ErrorRateFrameKind.Bd,
                            false,
                            false,
                            fileSizeText,
                            blockCountText);
                    }
                },
                retainAllSamples: false,
                cancellationToken: cancellationToken);

            wavWriter?.Dispose();
            wavWriter = null;

            if (player is not null)
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromHours(2);
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var buffered = player.BufferedSampleFrames;
                    if (buffered <= 0)
                    {
                        break;
                    }

                    PublishEmittedSamples(emittedSamples);
                    spectrumPublisher.PublishPlayhead(ResolvePlayedSamples(emittedSamples, player));
                    DateTime audioAnchorUtc;
                    lock (_sync)
                    {
                        audioAnchorUtc = _audioProgressAnchorUtc;
                    }

                    var elapsed = ResolveMeterElapsedSeconds(
                        emittedSamples,
                        player,
                        profile.SampleRate,
                        totalSamples,
                        totalSeconds,
                        audioAnchorUtc);
                    UpdateSnapshot(
                        100.0 * elapsed / Math.Max(totalSeconds, 1e-9),
                        elapsed,
                        totalSeconds,
                        "音声再生中",
                        ErrorRateFrameKind.Bd,
                        false,
                        false,
                        fileSizeText,
                        blockCountText);
                    Thread.Sleep(20);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            UpdateSnapshot(100, totalSeconds, totalSeconds, "Completed", ErrorRateFrameKind.Bd, true, false, fileSizeText, blockCountText);
            lock (_sync)
            {
                _completion = new CoreCompletionResult(
                    IsSuccess: true,
                    Message: "Transmission completed successfully.",
                    OutputWavPath: settings.WriteWav ? (outputWavPath ?? string.Empty) : string.Empty,
                    InputFileName: inputInfo.Name,
                    FileSizeText: fileSizeText,
                    BlockCountText: blockCountText,
                    Settings: settings,
                    PlayedRealtime: settings.PlayAudio,
                    WasCancelled: false);
            }
        }
        catch (OperationCanceledException)
        {
            var elapsed = 0.0;
            var total = 0.0;
            lock (_sync)
            {
                total = _snapshot.TotalAudioSeconds;
                elapsed = _player is not null && !_player.IsDisposed && _totalSamples > 0
                    ? ResolveElapsedSeconds(_emittedSamples, _player, _sampleRate, _totalSamples)
                    : _snapshot.ElapsedAudioSeconds;
            }

            UpdateSnapshot(elapsed > 0 && total > 0 ? 100.0 * elapsed / total : 0, elapsed, total, "中断", ErrorRateFrameKind.Bd, true, false, "-", "-");
            lock (_sync)
            {
                _completion = new CoreCompletionResult(
                    IsSuccess: false,
                    Message: "Transmission cancelled.",
                    OutputWavPath: settings.WriteWav ? (outputWavPath ?? string.Empty) : string.Empty,
                    InputFileName: string.IsNullOrWhiteSpace(settings.InputFilePath) ? "(未選択)" : Path.GetFileName(settings.InputFilePath),
                    FileSizeText: "-",
                    BlockCountText: "-",
                    Settings: settings,
                    PlayedRealtime: false,
                    WasCancelled: true);
            }
        }
        catch (Exception ex)
        {
            UpdateSnapshot(100, 0, 0, "Failed", ErrorRateFrameKind.Bd, true, true, "-", "-");
            lock (_sync)
            {
                _completion = new CoreCompletionResult(
                    IsSuccess: false,
                    Message: ex.Message,
                    OutputWavPath: settings.WriteWav ? (outputWavPath ?? string.Empty) : string.Empty,
                    InputFileName: string.IsNullOrWhiteSpace(settings.InputFilePath) ? "(未選択)" : Path.GetFileName(settings.InputFilePath),
                    FileSizeText: "-",
                    BlockCountText: "-",
                    Settings: settings,
                    PlayedRealtime: false,
                    WasCancelled: false);
            }
        }
        finally
        {
            wavWriter?.Dispose();
            lock (_sync)
            {
                _player = null;
                if (_cts is not null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
            }

            player?.Dispose();
        }
    }

    /// <summary>
    /// 再生キュー残量を差し引いた実再生サンプル位置を返します。
    /// </summary>
    /// <param name="emittedSamples">再生側へ投入済みの総サンプル数。</param>
    /// <param name="player">再生プレイヤー。</param>
    /// <returns>実再生済みサンプル数（破棄済みプレイヤー時は emittedSamples）。</returns>
    private static long ResolvePlayedSamples(long emittedSamples, RealtimePcmPlayer player)
    {
        if (player.IsDisposed)
        {
            return Math.Max(0L, emittedSamples);
        }

        return Math.Max(0L, emittedSamples - player.BufferedSampleFrames);
    }

    /// <summary>
    /// 再生キュー残量を考慮して経過秒を推定します。
    /// </summary>
    /// <param name="emittedSamples">投入済みサンプル数。</param>
    /// <param name="player">再生プレイヤー。null／破棄済みなら投入位置をそのまま使う。</param>
    /// <param name="sampleRate">サンプルレート。</param>
    /// <param name="totalSamples">総サンプル数（上限クリップ用）。</param>
    /// <returns>推定経過秒。</returns>
    private static double ResolveElapsedSeconds(
        long emittedSamples,
        RealtimePcmPlayer? player,
        int sampleRate,
        long totalSamples)
    {
        var rate = Math.Max(1, sampleRate);
        if (player is null || player.IsDisposed)
        {
            return Math.Min(emittedSamples, totalSamples) / (double)rate;
        }

        // 出力済み総数から未再生バッファを差し引いて実再生数を推定する。
        var played = ResolvePlayedSamples(emittedSamples, player);
        return Math.Min(played, totalSamples) / (double)rate;
    }

    /// <summary>
    /// 送信詳細メーター用の経過秒を返します。
    /// 再生ヘッドが止まっても壁時計で進むようにします。
    /// </summary>
    /// <param name="emittedSamples">投入済みサンプル数。</param>
    /// <param name="player">再生プレイヤー。</param>
    /// <param name="sampleRate">サンプルレート。</param>
    /// <param name="totalSamples">総サンプル数。</param>
    /// <param name="totalAudioSeconds">総音声秒数（上限クリップ用）。</param>
    /// <param name="audioAnchorUtc">音声出力開始時刻（壁時計起点）。</param>
    /// <returns>再生ヘッドと壁時計の大きい方を総秒でクリップした経過秒。</returns>
    private static double ResolveMeterElapsedSeconds(
        long emittedSamples,
        RealtimePcmPlayer? player,
        int sampleRate,
        long totalSamples,
        double totalAudioSeconds,
        DateTime audioAnchorUtc)
    {
        var playhead = ResolveElapsedSeconds(emittedSamples, player, sampleRate, totalSamples);
        if (player is null || player.IsDisposed || audioAnchorUtc == default)
        {
            return playhead;
        }

        var wall = Math.Max(0.0, (DateTime.UtcNow - audioAnchorUtc).TotalSeconds);
        var total = Math.Max(totalAudioSeconds, 1e-9);
        return Math.Clamp(Math.Max(playhead, wall), 0.0, total);
    }

    /// <summary>
    /// 投入済みサンプル数を共有状態へ反映します。
    /// </summary>
    /// <param name="emittedSamples">投入済みサンプル数。</param>
    private void PublishEmittedSamples(long emittedSamples)
    {
        lock (_sync)
        {
            _emittedSamples = Math.Max(0L, emittedSamples);
        }
    }

    /// <summary>
    /// 進捗スナップショットを更新します。
    /// </summary>
    /// <param name="progressPercent">進捗率。</param>
    /// <param name="elapsedAudioSeconds">経過秒。</param>
    /// <param name="totalAudioSeconds">総秒数。</param>
    /// <param name="stage">処理ステージ表示。</param>
    /// <param name="errorFrameKind">直近イベント種別。</param>
    /// <param name="isCompleted">完了フラグ。</param>
    /// <param name="isFaulted">失敗フラグ。</param>
    /// <param name="fileSizeText">表示用ファイルサイズ。</param>
    /// <param name="blockCountText">表示用ブロック数。</param>
    private void UpdateSnapshot(
        double progressPercent,
        double elapsedAudioSeconds,
        double totalAudioSeconds,
        string stage,
        ErrorRateFrameKind errorFrameKind,
        bool isCompleted,
        bool isFaulted,
        string fileSizeText,
        string blockCountText)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                IsRunning = !isCompleted,
                IsCompleted = isCompleted,
                IsFaulted = isFaulted,
                ProgressPercent = Math.Clamp(progressPercent, 0.0, 100.0),
                ElapsedAudioSeconds = Math.Max(0.0, elapsedAudioSeconds),
                TotalAudioSeconds = Math.Max(0.0, totalAudioSeconds),
                Stage = stage,
                ErrorFrameKind = errorFrameKind,
                FileSizeText = fileSizeText,
                BlockCountText = blockCountText
            };
        }
    }

    /// <summary>
    /// 送信フレーム種別を UI 用イベントへ変換してキューします。
    /// </summary>
    /// <param name="frameKind">送信フレーム種別。</param>
    private void OnCoreFrameTransmitted(TransmissionFrameKind frameKind)
    {
        lock (_sync)
        {
            _frameEvents.Enqueue(frameKind switch
            {
                TransmissionFrameKind.Fh => ErrorRateFrameKind.Fh,
                TransmissionFrameKind.Bh => ErrorRateFrameKind.Bh,
                _ => ErrorRateFrameKind.Bd
            });
        }
    }

    /// <summary>
    /// 送信 PCM をリングに保持し、再生ヘッド位置の FFT / I-Q を間引き更新します。
    /// </summary>
    private sealed class TxPcmSpectrumPublisher
    {
        /// <summary>
        /// 表示用解析 FFT 長。OFDM 合成 FFT(256) と分離し、キャリア間の空ビン鋸歯を抑える。
        /// </summary>
        private const int FftSize = 2048;
        private const int MinPublishIntervalMs = 33;
        /// <summary>再生バッファ（3秒）＋余白を覆うリング長（秒）。</summary>
        private const double RingSeconds = 4.0;
        /// <summary>ヘッダー（GROUP A/B）のデータキャリア数（パイロット除く 6 本 × 2 グループ）。</summary>
        private const int HeaderDataCarrierCount = 12;
        /// <summary>I-Q 点の最大数（L/R 各 64 SC のデータキャリア合計以上）。</summary>
        private const int MaxIqPoints = 128;
        /// <summary>I-Q 同期探索に使う最大サンプル数（CP=32 の 3 シンボル分）。</summary>
        private const int IqCaptureMaxSamples = (OfdmConfig.FixedFftSize + 32) * 3;

        private readonly CoreExecutionStatusBoard _board;
        private readonly int _sampleRate;
        private readonly bool _stereo;
        private readonly int _headerCp;
        private readonly int _dataCp;
        private readonly float[] _leftRing;
        private readonly float[] _rightRing;
        private readonly int _capacity;
        private readonly Complex[] _leftWindow = new Complex[FftSize];
        private readonly Complex[] _rightWindow = new Complex[FftSize];
        private readonly Complex[] _fftWork = new Complex[FftSize];
        private readonly double[] _iqPcmLeft = new double[IqCaptureMaxSamples];
        private readonly double[] _iqPcmRight = new double[IqCaptureMaxSamples];
        private readonly Complex[] _iqTime = new Complex[OfdmConfig.FixedFftSize];
        private readonly Complex[] _iqFft = new Complex[OfdmConfig.FixedFftSize];
        private readonly Complex[] _iqPoints = new Complex[MaxIqPoints];
        private readonly byte[] _iqGroups = new byte[MaxIqPoints];
        private readonly object _sync = new();

        /// <summary>送信順のフレーム区間変調情報（エンコーダの送出順と同じ）。</summary>
        private readonly List<TxIqSegmentInfo> _frames;

        /// <summary>送出済み区間（開始／終了サンプルと変調情報）。</summary>
        private readonly List<(long Start, long End, TxIqSegmentInfo Info)> _closedSegments = [];

        private int _frameCursor;
        private long _openSegmentStart;
        private long _writeTotal;
        private long _lastPublishMs = -1;
        private long _lastPublishedPlayhead = -1;
        private TxIqSegmentInfo? _lastIqInfo;

        /// <summary>
        /// 再生ヘッド同期 FFT / I-Q パブリッシャを初期化します。
        /// </summary>
        /// <param name="board">FFT / I-Q を書き込む共有状態ボード。</param>
        /// <param name="profile">送信プロファイル（SC・変調・CP・インターリーブ）。</param>
        /// <param name="blockCount">送信ブロック数。</param>
        public TxPcmSpectrumPublisher(CoreExecutionStatusBoard board, FileWavCodecProfile profile, int blockCount)
        {
            _board = board;
            _sampleRate = Math.Max(1, profile.SampleRate);
            _stereo = profile.ChannelMode == ChannelMode.Stereo;
            _headerCp = profile.HeaderCyclicPrefixLength;
            _dataCp = profile.DataCyclicPrefixLength;
            _capacity = Math.Max(FftSize * 2, (int)(_sampleRate * RingSeconds));
            _leftRing = new float[_capacity];
            _rightRing = new float[_capacity];
            _frames = BuildFrameSequence(profile, Math.Max(1, blockCount));
            _board.SetFftStereoMode(_stereo);
        }

        /// <summary>
        /// エンコーダと同じ順序で FH / BH / BD の変調情報列を作ります。
        /// </summary>
        /// <param name="profile">送信プロファイル。</param>
        /// <param name="blockCount">ブロック数。</param>
        /// <returns>送出順のフレーム変調情報。</returns>
        private static List<TxIqSegmentInfo> BuildFrameSequence(FileWavCodecProfile profile, int blockCount)
        {
            var frames = new List<TxIqSegmentInfo>((blockCount * 2 * Math.Max(1, profile.BlockInterleaveFactor)) + 4);
            var openingHeader = TxIqSegmentInfo.Header(profile.ActiveSubcarriers);
            frames.Add(openingHeader);
            for (var pass = 0; pass < profile.BlockInterleaveFactor; pass++)
            {
                var (passSc, passMod) = FileWavCodec.ResolveInterleavePassModulation(
                    pass,
                    profile.ActiveSubcarriers,
                    profile.ModulationScheme);
                var passHeader = TxIqSegmentInfo.Header(passSc);
                var passData = TxIqSegmentInfo.Data(passSc, passMod);
                for (var local = 0; local < blockCount; local++)
                {
                    if (local > 0 && (local % FileWavCodec.FileHeaderRepeatIntervalBlocks) == 0)
                    {
                        frames.Add(passHeader);
                    }

                    frames.Add(passHeader);
                    frames.Add(passData);
                }
            }

            frames.Add(openingHeader);
            return frames;
        }

        /// <summary>
        /// エンコーダが 1 フレームを送出し終えた時点で呼び、直前区間の変調情報を確定します。
        /// </summary>
        /// <param name="frameKind">送出し終えたフレーム種別。</param>
        public void MarkFrameEnd(TransmissionFrameKind frameKind)
        {
            lock (_sync)
            {
                if (_frameCursor >= _frames.Count)
                {
                    return;
                }

                var info = _frames[_frameCursor];
                // 予測順とずれた場合は I-Q を誤表示しないよう以降を打ち切る。
                if (info.IsHeader != (frameKind != TransmissionFrameKind.Bd))
                {
                    _frameCursor = _frames.Count;
                    return;
                }

                _closedSegments.Add((_openSegmentStart, _writeTotal, info));
                _openSegmentStart = _writeTotal;
                _frameCursor++;
                var oldest = _writeTotal - _capacity;
                var drop = 0;
                while (drop < _closedSegments.Count && _closedSegments[drop].End < oldest)
                {
                    drop++;
                }

                if (drop > 0)
                {
                    _closedSegments.RemoveRange(0, drop);
                }
            }
        }

        /// <summary>
        /// エンコード済み PCM をリングへ追記します（FFT は PublishPlayhead で行う）。
        /// </summary>
        /// <param name="left">L チャネル複素サンプル（実部を使用）。</param>
        /// <param name="right">R チャネル複素サンプル（ステレオ時のみ参照）。</param>
        public void Push(ReadOnlySpan<Complex> left, ReadOnlySpan<Complex> right)
        {
            if (left.Length == 0)
            {
                return;
            }

            lock (_sync)
            {
                for (var i = 0; i < left.Length; i++)
                {
                    var idx = (int)(_writeTotal % _capacity);
                    _leftRing[idx] = (float)left[i].Real;
                    _rightRing[idx] = _stereo && i < right.Length ? (float)right[i].Real : 0f;
                    _writeTotal++;
                }
            }
        }

        /// <summary>
        /// 指定再生サンプル位置の直前窓で FFT を更新します。
        /// </summary>
        /// <param name="playedSamples">実再生済みサンプル数（バッファ差し引き後）。</param>
        public void PublishPlayhead(long playedSamples)
        {
            if (playedSamples < FftSize)
            {
                return;
            }

            var now = Environment.TickCount64;
            if (_lastPublishMs >= 0 && now - _lastPublishMs < MinPublishIntervalMs)
            {
                return;
            }

            // 再生ヘッドが進んでいないときは再計算しない（約 1/8 窓ぶん）。
            if (_lastPublishedPlayhead >= 0 && playedSamples - _lastPublishedPlayhead < FftSize / 8)
            {
                return;
            }

            TxIqSegmentInfo? iqInfo;
            int iqSamples;
            lock (_sync)
            {
                var end = Math.Min(playedSamples, _writeTotal);
                if (end < FftSize)
                {
                    return;
                }

                var start = end - FftSize;
                // リングから上書き済みなら可視化できない。
                if (_writeTotal - start > _capacity)
                {
                    start = _writeTotal - _capacity;
                    end = start + FftSize;
                    if (end > _writeTotal)
                    {
                        return;
                    }
                }

                for (var i = 0; i < FftSize; i++)
                {
                    var sampleIndex = start + i;
                    var ringIndex = (int)(sampleIndex % _capacity);
                    _leftWindow[i] = new Complex(_leftRing[ringIndex], 0.0);
                    if (_stereo)
                    {
                        _rightWindow[i] = new Complex(_rightRing[ringIndex], 0.0);
                    }
                }

                iqInfo = CaptureIqWindowUnlocked(end, out iqSamples);
            }

            if (iqInfo is { } info)
            {
                PublishIq(info, iqSamples);
            }

            // ヘッダーは L/R 同一波形。窓内がほぼ一致なら L のみ（緑）で表示する。
            var publishStereo = _stereo && !AreWindowsNearlyIdentical(_leftWindow, _rightWindow);
            _board.SetFftStereoMode(publishStereo);

            OfdmGenerator.ComputeForwardSpectrumFromRealPcm(_leftWindow, _fftWork);
            _board.SetFftFrame(_fftWork, isRightChannel: false, sampleRate: _sampleRate);
            if (publishStereo)
            {
                OfdmGenerator.ComputeForwardSpectrumFromRealPcm(_rightWindow, _fftWork);
                _board.SetFftFrame(_fftWork, isRightChannel: true, sampleRate: _sampleRate);
            }

            _lastPublishMs = now;
            _lastPublishedPlayhead = playedSamples;
        }

        /// <summary>
        /// 再生位置直前の I-Q 抽出用 PCM を切り出し、その区間の変調情報を返します。
        /// </summary>
        /// <param name="end">切り出し終端サンプル位置（排他的）。</param>
        /// <param name="samples">切り出したサンプル数。</param>
        /// <returns>単一区間に収まる場合はその変調情報、収まらない・不明なら null。</returns>
        private TxIqSegmentInfo? CaptureIqWindowUnlocked(long end, out int samples)
        {
            samples = 0;
            if (!TryResolveSegmentUnlocked(end - 1, out var segStart, out var info))
            {
                return null;
            }

            var cp = info.IsHeader ? _headerCp : _dataCp;
            var count = Math.Min(IqCaptureMaxSamples, (OfdmConfig.FixedFftSize + cp) * 3);
            var start = end - count;
            // 区間境界をまたぐ窓は SC/変調が混在するので捨てる。
            if (start < segStart || start < 0 || _writeTotal - start > _capacity)
            {
                return null;
            }

            for (var i = 0; i < count; i++)
            {
                var ringIndex = (int)((start + i) % _capacity);
                _iqPcmLeft[i] = _leftRing[ringIndex];
                _iqPcmRight[i] = _rightRing[ringIndex];
            }

            samples = count;
            return info;
        }

        /// <summary>
        /// サンプル位置が属する送出区間（確定済み、または送出中）を探します。
        /// </summary>
        /// <param name="position">サンプル位置。</param>
        /// <param name="segmentStart">区間開始サンプル位置。</param>
        /// <param name="info">区間の変調情報。</param>
        /// <returns>見つかった場合 true。</returns>
        private bool TryResolveSegmentUnlocked(long position, out long segmentStart, out TxIqSegmentInfo info)
        {
            for (var i = _closedSegments.Count - 1; i >= 0; i--)
            {
                var seg = _closedSegments[i];
                if (position >= seg.Start && position < seg.End)
                {
                    segmentStart = seg.Start;
                    info = seg.Info;
                    return true;
                }
            }

            if (position >= _openSegmentStart && _frameCursor < _frames.Count)
            {
                segmentStart = _openSegmentStart;
                info = _frames[_frameCursor];
                return true;
            }

            segmentStart = 0;
            info = default;
            return false;
        }

        /// <summary>
        /// 切り出した PCM から等化後 I-Q を抽出して共有ボードへ載せます。
        /// </summary>
        /// <param name="info">区間の変調情報。</param>
        /// <param name="samples">切り出しサンプル数。</param>
        private void PublishIq(TxIqSegmentInfo info, int samples)
        {
            var cp = info.IsHeader ? _headerCp : _dataCp;
            var leftLimit = info.IsHeader ? HeaderDataCarrierCount : MaxIqPoints;
            var leftCount = PerformanceIqExtractor.ExtractEqualized(
                _iqPcmLeft.AsSpan(0, samples),
                info.ExtractSubcarriers,
                useRightCarriers: false,
                _iqTime,
                _iqFft,
                _iqPoints.AsSpan(0, leftLimit),
                _iqGroups.AsSpan(0, leftLimit),
                cp);
            var count = leftCount;
            // ヘッダーは L/R 同一波形のモノラル扱いなので R 搬送波は抽出しない。
            if (_stereo && !info.IsHeader && leftCount < MaxIqPoints)
            {
                count += PerformanceIqExtractor.ExtractEqualized(
                    _iqPcmRight.AsSpan(0, samples),
                    info.ExtractSubcarriers,
                    useRightCarriers: true,
                    _iqTime,
                    _iqFft,
                    _iqPoints.AsSpan(leftCount),
                    _iqGroups.AsSpan(leftCount),
                    cp);
            }

            if (count <= 0 || IsUnmodulated(_iqPoints.AsSpan(0, count)))
            {
                return;
            }

            if (_lastIqInfo != info)
            {
                _board.BeginIqCapture(info.Subcarriers, info.Modulation);
                _lastIqInfo = info;
            }

            _board.AppendIqFrame(_iqPoints.AsSpan(0, count), _iqGroups.AsSpan(0, count));
        }

        /// <summary>
        /// 全点が無変調キャリア（パイロット比 1+0j）かを判定します（プリアンブル・ヘッダー先頭無変調区間の除外用）。
        /// </summary>
        /// <param name="points">等化後の点。</param>
        /// <returns>全点が 1+0j 近傍なら true。</returns>
        private static bool IsUnmodulated(ReadOnlySpan<Complex> points)
        {
            foreach (var p in points)
            {
                var dr = p.Real - 1.0;
                if ((dr * dr) + (p.Imaginary * p.Imaginary) > 0.0025)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// L/R 窓が実質同一か判定します（ヘッダー／無音のモノラル扱い用）。
        /// </summary>
        /// <param name="left">L 窓サンプル。</param>
        /// <param name="right">R 窓サンプル。</param>
        /// <returns>相対二乗誤差が閾値以下、または双方ほぼ無音なら true。</returns>
        private static bool AreWindowsNearlyIdentical(Complex[] left, Complex[] right)
        {
            double sumSqDiff = 0.0;
            double sumSq = 0.0;
            var n = Math.Min(left.Length, right.Length);
            for (var i = 0; i < n; i++)
            {
                var l = left[i].Real;
                var r = right[i].Real;
                var d = l - r;
                sumSqDiff += d * d;
                sumSq += (l * l) + (r * r);
            }

            if (sumSq <= 1e-18)
            {
                return true;
            }

            return (sumSqDiff / sumSq) <= 1e-8;
        }
    }

    /// <summary>
    /// 送信区間（FH/BH または BD）の I-Q 表示用変調情報です。
    /// </summary>
    /// <param name="Subcarriers">表示用サブキャリア数（ヘッダーは 16）。</param>
    /// <param name="Modulation">変調方式（ヘッダーは QPSK）。</param>
    /// <param name="IsHeader">ヘッダー区間なら true（CP=32・モノラル・GROUP A/B のみ）。</param>
    /// <param name="ExtractSubcarriers">抽出に使う搬送波グリッドの SC 数（SC-24 族ヘッダーは 24 の先頭 2 グループを使う）。</param>
    private readonly record struct TxIqSegmentInfo(
        int Subcarriers,
        ModulationScheme Modulation,
        bool IsHeader,
        int ExtractSubcarriers)
    {
        /// <summary>
        /// データ部区間の変調情報を作ります。
        /// </summary>
        /// <param name="subcarriers">サブキャリア数。</param>
        /// <param name="modulation">変調方式。</param>
        /// <returns>データ部区間情報。</returns>
        public static TxIqSegmentInfo Data(int subcarriers, ModulationScheme modulation) =>
            new(subcarriers, modulation, IsHeader: false, ExtractSubcarriers: subcarriers);

        /// <summary>
        /// データ部 SC の周波数族に合わせたヘッダー区間の変調情報を作ります。
        /// </summary>
        /// <param name="dataSubcarriers">同じパスのデータ部 SC 数。</param>
        /// <returns>ヘッダー区間情報。</returns>
        public static TxIqSegmentInfo Header(int dataSubcarriers) =>
            new(
                16,
                ModulationScheme.Qpsk,
                IsHeader: true,
                ExtractSubcarriers: OfdmConfig.ResolveCarrierGrid(dataSubcarriers) == OfdmCarrierGrid.Sc8Family ? 16 : 24);
    }
}

internal readonly record struct CoreProgressSnapshot(
    bool IsRunning,
    bool IsCompleted,
    bool IsFaulted,
    double ProgressPercent,
    double ElapsedAudioSeconds,
    double TotalAudioSeconds,
    string Stage,
    ErrorRateFrameKind ErrorFrameKind,
    string InputFileName,
    string FileSizeText,
    string BlockCountText,
    double WowLeftPercent,
    double WowRightPercent,
    double ErrorRatePercent,
    string OutputWavPath,
    DateTime StartedAtUtc)
{
    public static CoreProgressSnapshot Idle => new(
        IsRunning: false,
        IsCompleted: false,
        IsFaulted: false,
        ProgressPercent: 0,
        ElapsedAudioSeconds: 0,
        TotalAudioSeconds: 0,
        Stage: "Preparing",
        ErrorFrameKind: ErrorRateFrameKind.Fh,
        InputFileName: "(未選択)",
        FileSizeText: "-",
        BlockCountText: "-",
        WowLeftPercent: 0,
        WowRightPercent: 0,
        ErrorRatePercent: 0,
        OutputWavPath: string.Empty,
        StartedAtUtc: DateTime.UtcNow);
}

internal readonly record struct CoreCompletionResult(
    bool IsSuccess,
    string Message,
    string OutputWavPath,
    string InputFileName,
    string FileSizeText,
    string BlockCountText,
    SendSettingsSnapshot Settings,
    bool PlayedRealtime = false,
    bool WasCancelled = false);




