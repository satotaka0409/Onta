using Onta.Core;

namespace Onta.View;

/// <summary>
/// 送信コア処理を UI スレッドと分離して実行し、問い合わせ時に進捗を返します。
/// 音声出力選択時は符号化と並行してリアルタイム再生します。
/// </summary>
internal sealed class OutputCoreWorker
{
    private readonly object _sync = new();
    private readonly Queue<ErrorRateFrameKind> _frameEvents = [];
    private CoreProgressSnapshot _snapshot = CoreProgressSnapshot.Idle;
    private CoreCompletionResult? _completion;
    private RealtimePcmPlayer? _player;
    private CancellationTokenSource? _cts;

    public bool TryStart(SendSettingsSnapshot settings, string? outputWavPath)
    {
        if (settings.WriteWav && string.IsNullOrWhiteSpace(outputWavPath))
        {
            throw new ArgumentException("WAV 出力パスが必要です。", nameof(outputWavPath));
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
                Stage: "待機",
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
        }

        _ = CoreBackgroundHost.RunAsync(_ => RunCore(settings, outputWavPath, cts.Token), cts.Token);
        return true;
    }

    /// <summary>実行中の送信を停止要求します。</summary>
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
            _snapshot = _snapshot with { Stage = "停止中" };
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 完了直後の競合は無視。
        }

        // バッファ待ちを解除するため再生を即停止。
        player?.Dispose();
        return true;
    }

    public CoreProgressSnapshot GetProgress()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    /// <summary>
    /// コア側で発生した FH/BH/BD 通知をまとめて取り出します。
    /// </summary>
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

    private void RunCore(SendSettingsSnapshot settings, string? outputWavPath, CancellationToken cancellationToken)
    {
        RealtimePcmPlayer? player = null;
        WavWriter.StreamingPcm16Writer? wavWriter = null;
        try
        {
            UpdateSnapshot(0, 0, 0, "入力読み込み", ErrorRateFrameKind.Fh, false, false, "-", "-");
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

            UpdateSnapshot(0, 0, totalSeconds, "プロファイル構築", ErrorRateFrameKind.Fh, false, false, fileSizeText, blockCountText);
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
                ? (settings.PlayAudio ? "符号化＋WAV／音声出力" : "符号化＋WAV逐次出力")
                : "符号化＋音声出力";
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
                    player?.AddSamples(leftChunk, rightChunk);
                    emittedSamples += leftChunk.Length;
                    var elapsed = ResolveElapsedSeconds(emittedSamples, player, profile.SampleRate, totalSamples);
                    var pct = 100.0 * elapsed / Math.Max(totalSeconds, 1e-9);
                    UpdateSnapshot(
                        pct,
                        elapsed,
                        totalSeconds,
                        stageLabel,
                        ErrorRateFrameKind.Bd,
                        false,
                        false,
                        fileSizeText,
                        blockCountText);
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

                    var elapsed = ResolveElapsedSeconds(emittedSamples, player, profile.SampleRate, totalSamples);
                    UpdateSnapshot(
                        100.0 * elapsed / Math.Max(totalSeconds, 1e-9),
                        elapsed,
                        totalSeconds,
                        "音声再生完了待ち",
                        ErrorRateFrameKind.Bd,
                        false,
                        false,
                        fileSizeText,
                        blockCountText);
                    Thread.Sleep(40);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            UpdateSnapshot(100, totalSeconds, totalSeconds, "完了", ErrorRateFrameKind.Bd, true, false, fileSizeText, blockCountText);
            lock (_sync)
            {
                _completion = new CoreCompletionResult(
                    IsSuccess: true,
                    Message: "出力が完了しました。",
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
                elapsed = _snapshot.ElapsedAudioSeconds;
                total = _snapshot.TotalAudioSeconds;
            }

            UpdateSnapshot(elapsed > 0 && total > 0 ? 100.0 * elapsed / total : 0, elapsed, total, "停止", ErrorRateFrameKind.Bd, true, false, "-", "-");
            lock (_sync)
            {
                _completion = new CoreCompletionResult(
                    IsSuccess: false,
                    Message: "送信を停止しました。",
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
            UpdateSnapshot(100, 0, 0, "失敗", ErrorRateFrameKind.Bd, true, true, "-", "-");
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

        // 再生側の経過 = 投入済み − バッファ残（リアルタイム進行に合わせる）。
        var played = Math.Max(0L, emittedSamples - player.BufferedSampleFrames);
        return Math.Min(played, totalSamples) / (double)rate;
    }

    private static double ResolveProgressPercent(long emittedSamples, RealtimePcmPlayer? player, long totalSamples)
    {
        var done = player is null || player.IsDisposed
            ? emittedSamples
            : Math.Max(0L, emittedSamples - player.BufferedSampleFrames);
        return 100.0 * Math.Clamp(done, 0L, totalSamples) / Math.Max(1L, totalSamples);
    }

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
        Stage: "待機",
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
