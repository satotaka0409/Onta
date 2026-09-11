using Onta.Core;
using Onta.History;

namespace Onta.View.Core;

/// <summary>
/// WAV入力の段階的デコードをバックグラウンドで実行するワーカーです。
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
    /// ファイルヘッダー（名前/サイズ/ブロック数）確定時に通知します。
    /// </summary>
    public event Action<string, string, int>? FileHeaderReady;

    /// <summary>
    /// 現在デコード実行中かどうかを返します。
    /// </summary>
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

    /// <summary>
    /// WAVデコード処理を開始します。
    /// </summary>
    /// <param name="wavPath">入力WAVパス。</param>
    /// <param name="profile">デコードに使用するプロファイル。</param>
    /// <param name="outputDirectory">復元ファイル出力先。null時は既定フォルダー。</param>
    /// <returns>開始に成功した場合 true。</returns>
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
            // 初期状態を受信待ちとして公開する。
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

            // 長時間処理をUIスレッドから分離して実行する。
            _worker = Task.Factory.StartNew(
                () => RunWavDecode(wavPath, profile, state, outDir),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            return true;
        }
    }

    /// <summary>
    /// 現在の実行状態を取得します。
    /// </summary>
    /// <returns>Core 実行状態。</returns>
    public CoreExecutionStatus QueryExecutionStatus()
    {
        lock (_sync)
        {
            return _state?.QueryExecutionStatus() ?? CoreExecutionStatus.Idle;
        }
    }

    /// <summary>
    /// 完了結果を1回だけ取り出します。
    /// </summary>
    /// <param name="success">成功した場合 true。</param>
    /// <param name="message">完了メッセージ。</param>
    /// <param name="outputPath">出力ファイルパス。</param>
    /// <returns>完了結果が存在した場合 true。</returns>
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
                ? "Receive completed successfully."
                : (_lastError ?? "Receive failed.");
            outputPath = _lastDecodedPath;
            return true;
        }
    }

    /// <summary>
    /// 収集済み orphan 情報をスナップショットとして返します。
    /// </summary>
    /// <returns>orphan 履歴配列。</returns>
    public ReceiveOrphanHistory[] CaptureOrphans()
    {
        lock (_sync)
        {
            if (_state is null)
            {
                return Array.Empty<ReceiveOrphanHistory>();
            }

            var result = new List<ReceiveOrphanHistory>(_state.OrphanPayloadByHash.Count);
            foreach (var pair in _state.OrphanPayloadByHash)
            {
                var detail = _state.OrphanDetailByHash.TryGetValue(pair.Key, out var text)
                    ? text
                    : string.Empty;
                result.Add(new ReceiveOrphanHistory(pair.Key, detail, pair.Value.ToArray()));
            }

            return result.ToArray();
        }
    }

    /// <summary>
    /// 復元済みペイロードのコピーを返します。
    /// </summary>
    /// <returns>復元バイト列。未完了時は null。</returns>
    public byte[]? CaptureDecodedPayload()
    {
        lock (_sync)
        {
            return _decodedBytes?.ToArray();
        }
    }

    /// <summary>
    /// WAVを読み込み、段階的デコードして結果を保存します。
    /// </summary>
    /// <param name="wavPath">入力WAVパス。</param>
    /// <param name="profile">デコードプロファイル。</param>
    /// <param name="state">進行状態オブジェクト。</param>
    /// <param name="outputDirectory">出力フォルダー。</param>
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

            // 1回のフルデコードで結果確定まで処理する。
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
                // ファイル名として無効な文字を除去する。
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




