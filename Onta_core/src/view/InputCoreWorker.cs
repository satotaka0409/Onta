using Onta.Core;

namespace Onta.View;

/// <summary>
/// 受信コア処理を UI スレッドと分離して実行し、問い合わせ時に進捗を返します。
/// 送信用 CoreBackgroundHost とは別スレッドで動かし、送信待ちで受信が詰まらないようにします。
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

    /// <summary>
    /// FH 確定時（ファイル名, サイズ表示文字列, ブロック数）。
    /// ワーカースレッドから発火するため、購読側で UI スレッドへマーシャリングすること。
    /// </summary>
    public event Action<string, string, int>? FileHeaderReady;

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

    public bool TryStartWavDecode(string wavPath, FileWavCodecProfile profile, string? outputDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        ArgumentNullException.ThrowIfNull(profile);

        var outDir = string.IsNullOrWhiteSpace(outputDirectory)
            ? AppPaths.OutputDir
            : Path.GetFullPath(outputDirectory);

        lock (_sync)
        {
            if (_worker is { IsCompleted: false })
            {
                return false;
            }

            var state = new ProgressiveDecodeState();
            // ファイル情報は FH 確定まで未受信のまま（WAV 名は出さない）。
            state.StatusBoard.BeginRun("(未受信)");
            state.StatusBoard.SetProgress(new CoreProgressInfo(
                CurrentFrame: CoreFrameKind.Fh,
                CurrentBlockIndex: -1,
                PassIndex: 0,
                AcceptedBlockCount: 0,
                TotalBlockCount: 0,
                ProgressPercent: 1.0));
            state.FileHeaderReady = (fileName, fileSize, blockCount) =>
            {
                FileHeaderReady?.Invoke(fileName, $"{fileSize:N0} bytes", blockCount);
            };
            _state = state;
            _decodedBytes = null;
            _lastDecodedPath = null;
            _lastError = null;
            _completionPending = false;

            // 送信ホストとは独立（LongRunning）。送信の符号化／再生待ちで受信がブロックされない。
            _worker = Task.Factory.StartNew(
                () => RunWavDecode(wavPath, profile, state, outDir),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
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

    private void RunWavDecode(
        string wavPath,
        FileWavCodecProfile profile,
        ProgressiveDecodeState state,
        string outputDirectory)
    {
        try
        {
            state.StatusBoard.SetProgress(new CoreProgressInfo(
                CurrentFrame: CoreFrameKind.Fh,
                CurrentBlockIndex: -1,
                PassIndex: 0,
                AcceptedBlockCount: 0,
                TotalBlockCount: 0,
                ProgressPercent: 2.0));

            var codec = new FileWavCodec(profile);
            var (left, right) = WavReader.ReadPcm16(wavPath);
            state.StatusBoard.SetProgress(new CoreProgressInfo(
                CurrentFrame: CoreFrameKind.Fh,
                CurrentBlockIndex: -1,
                PassIndex: 0,
                AcceptedBlockCount: 0,
                TotalBlockCount: 0,
                ProgressPercent: 3.0));

            // テスト往復と同じく、一括 WAV 受信はまず無補正で FH を素早く確定する。
            // （correctWow=true だと FH 前のワウ全探索で UI が数分固まる）
            var status = codec.DecodePcmSamplesProgressive(
                left,
                right,
                state,
                correctWow: false,
                wowParams: null,
                tuning: DecodeRuntimeTuning.Default,
                allowIncomplete: false);

            if (status == ProgressiveDecodeStatus.Completed && state.CompletedFile is not null)
            {
                Directory.CreateDirectory(outputDirectory);
                var outName = !string.IsNullOrWhiteSpace(state.ReceivedFileName)
                    ? Path.GetFileName(state.ReceivedFileName)
                    : Path.GetFileNameWithoutExtension(wavPath) + "_rx.bin";
                // FH 名に拡張子が無い／危険なパス要素がある場合は安全なファイル名へ。
                outName = string.Join("_", outName.Split(Path.GetInvalidFileNameChars()));
                if (string.IsNullOrWhiteSpace(outName))
                {
                    outName = Path.GetFileNameWithoutExtension(wavPath) + "_rx.bin";
                }

                var outPath = Path.Combine(outputDirectory, outName);
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
