using Onta.Core;

namespace Onta.View.Core;

/// <summary>
/// 送信エンコード処理の進捗管理と中断制御を行うワーカーです。
/// </summary>
internal sealed class OutputCoreWorker
{
    private readonly object _sync = new();
    private readonly Queue<ErrorRateFrameKind> _frameEvents = [];
    private CoreProgressSnapshot _snapshot = CoreProgressSnapshot.Idle;
    private CoreCompletionResult? _completion;
    private RealtimePcmPlayer? _player;
    private CancellationTokenSource? _cts;
    /// <summary>再生側へ投入済みの総サンプル数です。</summary>
    private long _emittedSamples;
    private long _totalSamples;
    private int _sampleRate = 44100;
    private double _totalAudioSeconds;

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
    /// 現在の送信進捗スナップショットを返します。
    /// </summary>
    /// <returns>送信進捗。</returns>
    public CoreProgressSnapshot GetProgress()
    {
        lock (_sync)
        {
            // 進捗算出に必要な情報が欠ける場合は直近スナップショットを返す。
            if (!_snapshot.IsRunning || _player is null || _player.IsDisposed || _totalSamples <= 0)
            {
                return _snapshot;
            }

            var elapsed = ResolveElapsedSeconds(_emittedSamples, _player, _sampleRate, _totalSamples);
            var total = Math.Max(_totalAudioSeconds, 1e-9);
            return _snapshot with
            {
                ElapsedAudioSeconds = elapsed,
                ProgressPercent = Math.Clamp(100.0 * elapsed / total, 0.0, 100.0)
            };
        }
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
            var blockCount = Math.Max(1, (bytes.Length + 4095) / 4096);
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
                    profile.SamplePeak);
                lock (_sync)
                {
                    _player = player;
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
            _ = codec.EncodeFileToSamples(
                bytes,
                inputInfo,
                OnCoreFrameTransmitted,
                onPcmChunk: (leftChunk, rightChunk) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    wavWriter?.WriteChunk(leftChunk, rightChunk);
                    if (player is not null)
                    {
                        // キュー投入時点のサンプル数から実時間進捗を近似する。
                        player.AddSamples(leftChunk, rightChunk, samplesQueued =>
                        {
                            emittedSamples += samplesQueued;
                            PublishEmittedSamples(emittedSamples);
                            var liveElapsed = ResolveElapsedSeconds(
                                emittedSamples, player, profile.SampleRate, totalSamples);
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
                        });
                    }
                    else
                    {
                        emittedSamples += leftChunk.Length;
                        PublishEmittedSamples(emittedSamples);
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
                    var elapsed = ResolveElapsedSeconds(emittedSamples, player, profile.SampleRate, totalSamples);
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
    /// 再生キュー残量を考慮して経過秒を推定します。
    /// </summary>
    /// <param name="emittedSamples">投入済みサンプル数。</param>
    /// <param name="player">再生プレイヤー。</param>
    /// <param name="sampleRate">サンプルレート。</param>
    /// <param name="totalSamples">総サンプル数。</param>
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
        var played = Math.Max(0L, emittedSamples - player.BufferedSampleFrames);
        return Math.Min(played, totalSamples) / (double)rate;
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




