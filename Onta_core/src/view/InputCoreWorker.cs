using Onta.Core;

namespace Onta.View;

/// <summary>
/// 受信コア処理を UI スレッドと分離して実行し、問い合わせ時に進捗を返します。
/// </summary>
internal sealed class InputCoreWorker
{
    private readonly object _sync = new();
    private ProgressiveDecodeState? _state;
    private Task? _worker;
    private string? _lastDecodedPath;
    private byte[]? _decodedBytes;
    private bool _completionPending;
    private string? _lastError;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _worker is { IsCompleted: false };
            }
        }
    }

    public bool TryStartWavDecode(string wavPath, FileWavCodecProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        ArgumentNullException.ThrowIfNull(profile);

        lock (_sync)
        {
            if (_worker is { IsCompleted: false })
            {
                return false;
            }

            var fileName = Path.GetFileName(wavPath);
            var state = new ProgressiveDecodeState();
            state.StatusBoard.BeginRun(fileName);
            _state = state;
            _decodedBytes = null;
            _lastDecodedPath = null;
            _lastError = null;
            _completionPending = false;
            _worker = Task.Run(() => RunWavDecode(wavPath, profile, state));
            return true;
        }
    }

    /// <summary>
    /// 1.進捗 2.エラー率 3.I-Q グラフ情報をまとめて問い合わせます。
    /// </summary>
    public CoreExecutionStatus QueryExecutionStatus()
    {
        lock (_sync)
        {
            return _state?.QueryExecutionStatus() ?? CoreExecutionStatus.Idle;
        }
    }

    public bool TryConsumeCompletion(out bool success, out string message, out string? outputPath)
    {
        lock (_sync)
        {
            if (!_completionPending)
            {
                success = false;
                message = string.Empty;
                outputPath = null;
                return false;
            }

            _completionPending = false;
            success = _decodedBytes is not null && _lastError is null;
            message = success
                ? "受信復号が完了しました。"
                : (_lastError ?? "受信復号に失敗しました。");
            outputPath = _lastDecodedPath;
            return true;
        }
    }

    private void RunWavDecode(string wavPath, FileWavCodecProfile profile, ProgressiveDecodeState state)
    {
        try
        {
            var codec = new FileWavCodec(profile);
            var (left, right) = WavReader.ReadPcm16(wavPath);
            var fileInfo = new FileInfo(wavPath);
            state.StatusBoard.SetFileInfo(fileInfo.Name, $"{fileInfo.Length:N0} bytes", "-");

            var status = codec.DecodePcmSamplesProgressive(
                left,
                right,
                state,
                correctWow: true,
                wowParams: null,
                tuning: DecodeRuntimeTuning.Default,
                allowIncomplete: false);

            if (status == ProgressiveDecodeStatus.Completed && state.CompletedFile is not null)
            {
                var outPath = Path.Combine(
                    AppPaths.OutputDir,
                    Path.GetFileNameWithoutExtension(wavPath) + "_rx.bin");
                Directory.CreateDirectory(AppPaths.OutputDir);
                File.WriteAllBytes(outPath, state.CompletedFile);
                lock (_sync)
                {
                    _decodedBytes = state.CompletedFile;
                    _lastDecodedPath = outPath;
                    _lastError = null;
                    _completionPending = true;
                }

                state.StatusBoard.Complete(faulted: false);
                return;
            }

            var error = state.LastError ?? "Decode failed.";
            state.StatusBoard.Complete(faulted: true, error);
            lock (_sync)
            {
                _decodedBytes = null;
                _lastDecodedPath = null;
                _lastError = error;
                _completionPending = true;
            }
        }
        catch (Exception ex)
        {
            state.StatusBoard.Complete(faulted: true, ex.Message);
            lock (_sync)
            {
                _decodedBytes = null;
                _lastDecodedPath = null;
                _lastError = ex.Message;
                _completionPending = true;
            }
        }
    }
}
