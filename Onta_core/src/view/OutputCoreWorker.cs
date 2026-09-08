using Onta.Core;

namespace Onta.View;

/// <summary>
/// 送信コア処理を UI スレッドと分離して実行し、問い合わせ時に進捗を返します。
/// </summary>
internal sealed class OutputCoreWorker
{
    private readonly object _sync = new();
    private readonly Queue<ErrorRateFrameKind> _frameEvents = [];
    private CoreProgressSnapshot _snapshot = CoreProgressSnapshot.Idle;
    private CoreCompletionResult? _completion;

    public bool TryStart(SendSettingsSnapshot settings, string outputWavPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputWavPath);

        lock (_sync)
        {
            if (_snapshot.IsRunning)
            {
                return false;
            }

            var fileName = string.IsNullOrWhiteSpace(settings.InputFilePath)
                ? "(未選択)"
                : Path.GetFileName(settings.InputFilePath);
            _snapshot = new CoreProgressSnapshot(
                IsRunning: true,
                IsCompleted: false,
                IsFaulted: false,
                ProgressPercent: 0,
                Stage: "待機",
                ErrorFrameKind: ErrorRateFrameKind.Fh,
                InputFileName: fileName,
                FileSizeText: "-",
                BlockCountText: "-",
                WowLeftPercent: 0,
                WowRightPercent: 0,
                ErrorRatePercent: 0,
                OutputWavPath: outputWavPath,
                StartedAtUtc: DateTime.UtcNow);
            _completion = null;
            _frameEvents.Clear();
        }

        _ = Task.Run(() => RunCore(settings, outputWavPath));
        return true;
    }

    public CoreProgressSnapshot GetProgress()
    {
        CoreProgressSnapshot raw;
        lock (_sync)
        {
            raw = _snapshot;
        }

        if (!raw.IsRunning)
        {
            return raw;
        }

        // 送信中はワウ・フラッター／エラー率の擬似値を出さない（受信パネル用）。
        return raw;
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

    private void RunCore(SendSettingsSnapshot settings, string outputWavPath)
    {
        try
        {
            UpdateSnapshot(5, "入力読み込み", ErrorRateFrameKind.Fh, false, false, "-", "-");
            var bytes = File.ReadAllBytes(settings.InputFilePath);
            var blockCount = Math.Max(1, (bytes.Length + 4095) / 4096);
            var fileSizeText = $"{bytes.Length:N0} bytes";
            var blockCountText = blockCount.ToString();

            UpdateSnapshot(20, "プロファイル構築", ErrorRateFrameKind.Fh, false, false, fileSizeText, blockCountText);
            var profile = new FileWavCodecProfile(
                ActiveSubcarriers: settings.ActiveSubcarriers,
                ModulationScheme: settings.ModulationScheme,
                ChannelMode: settings.ChannelMode,
                BlockInterleaveFactor: settings.BlockInterleaveFactor);

            UpdateSnapshot(45, "ヘッダー処理", ErrorRateFrameKind.Bh, false, false, fileSizeText, blockCountText);
            var codec = new FileWavCodec(profile);
            var inputInfo = new FileInfo(settings.InputFilePath);
            var (left, right) = codec.EncodeFileToSamples(bytes, inputInfo, OnCoreFrameTransmitted);

            UpdateSnapshot(80, "データ処理", ErrorRateFrameKind.Bd, false, false, fileSizeText, blockCountText);
            WavWriter.WritePcm16(outputWavPath, profile.SampleRate, left, right, profile.SamplePeak, profile.ChannelMode);

            UpdateSnapshot(100, "完了", ErrorRateFrameKind.Bd, true, false, fileSizeText, blockCountText);
            lock (_sync)
            {
                _completion = new CoreCompletionResult(
                    IsSuccess: true,
                    Message: "出力が完了しました。",
                    OutputWavPath: outputWavPath,
                    InputFileName: inputInfo.Name,
                    FileSizeText: fileSizeText,
                    BlockCountText: blockCountText,
                    Settings: settings);
            }
        }
        catch (Exception ex)
        {
            UpdateSnapshot(100, "失敗", ErrorRateFrameKind.Bd, true, true, "-", "-");
            lock (_sync)
            {
                _completion = new CoreCompletionResult(
                    IsSuccess: false,
                    Message: ex.Message,
                    OutputWavPath: outputWavPath,
                    InputFileName: string.IsNullOrWhiteSpace(settings.InputFilePath) ? "(未選択)" : Path.GetFileName(settings.InputFilePath),
                    FileSizeText: "-",
                    BlockCountText: "-",
                    Settings: settings);
            }
        }
    }

    private void UpdateSnapshot(
        double progressPercent,
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
    SendSettingsSnapshot Settings);