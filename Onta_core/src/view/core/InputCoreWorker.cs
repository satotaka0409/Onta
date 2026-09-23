using Onta.Core;
using Onta.History;

namespace Onta.View.Core;

/// <summary>
/// WAV / 音声入力の段階的デコードをバックグラウンドで実行するワーカーです。
/// </summary>
internal sealed class InputCoreWorker : IDisposable
{
    private readonly object _sync = new();
    /// <summary>コアが書き込み、画面が定期 Read する共有状態。</summary>
    private readonly CoreExecutionStatusBoard _sharedStatus = new();
    private ProgressiveDecodeState? _state;
    private Task? _worker;
    private RealtimeDecodeSession? _liveSession;
    private RealtimePcmCapture? _capture;
    private string? _lastDecodedPath;
    private byte[]? _decodedBytes;
    private bool _completionPending;
    private string? _lastError;
    private bool _liveMode;
    /// <summary>実行世代。停止要求で進め、古いワーカーの完了を無視する。</summary>
    private int _runGeneration;

    /// <summary>
    /// コアが進捗・グラフ用データを書き込む共有状態です。画面は問い合わせせず定期的に Read します。
    /// </summary>
    public CoreExecutionStatusBoard SharedStatus => _sharedStatus;

    /// <summary>
    /// ファイルヘッダー（名前/サイズ/ブロック数）確定時に通知します。
    /// </summary>
    public event Action<string, string, int, DateTime?, DateTime?>? FileHeaderReady;

    /// <summary>
    /// 現在デコード実行中かどうかを返します。
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                if (_liveMode)
                {
                    return _liveSession is not null
                           && (_capture?.IsRunning == true || !_completionPending);
                }

                return _worker is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// WAVデコード処理を開始します（バックグラウンドで一括復号。UI は SharedStatus を定期読み取り）。
    /// 逐次ストリーミングは音声入力専用（ファイル WAV では不安定だったため）。
    /// </summary>
    /// <param name="wavPath">入力 WAV ファイルパス。</param>
    /// <param name="profile">復号用コーデックプロファイル。</param>
    /// <param name="outputDirectory">未使用の出力先（履歴保存のみ。省略時は AppPaths.OutputDir）。</param>
    /// <returns>開始できた場合 true。既に実行中なら false。</returns>
    public bool TryStartWavDecode(string wavPath, FileWavCodecProfile profile, string? outputDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        ArgumentNullException.ThrowIfNull(profile);

        var outDir = string.IsNullOrWhiteSpace(outputDirectory)
            ? AppPaths.OutputDir
            : Path.GetFullPath(outputDirectory);

        lock (_sync)
        {
            if (IsBusyLocked())
            {
                return false;
            }

            StopLiveLocked();

            var label = Path.GetFileName(wavPath);
            var state = CreateState(string.IsNullOrWhiteSpace(label) ? "(WAV受信)" : label);
            state.StatusBoard.SetProgress(new CoreProgressInfo(
                CurrentFrame: CoreFrameKind.Fh,
                CurrentBlockIndex: -1,
                PassIndex: 0,
                AcceptedBlockCount: 0,
                TotalBlockCount: 0,
                ProgressPercent: 1.0));

            _state = state;
            _liveSession = null;
            _capture = null;
            _liveMode = false;
            _decodedBytes = null;
            _lastDecodedPath = null;
            _lastError = null;
            _completionPending = false;
            var runGeneration = ++_runGeneration;

            _worker = Task.Factory.StartNew(
                () => RunBatchWavDecode(wavPath, profile, state, outDir, runGeneration),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            return true;
        }
    }

    /// <summary>
    /// 音声入力デバイスからリアルタイム受信を開始します。
    /// </summary>
    /// <param name="deviceNumber">入力デバイス番号。</param>
    /// <param name="profile">復号用コーデックプロファイル。</param>
    /// <param name="outputDirectory">未使用の出力先（履歴保存のみ。省略時は AppPaths.OutputDir）。</param>
    /// <param name="inputGain">キャプチャ入力ゲイン（既定 0.8）。</param>
    /// <returns>開始できた場合 true。既に実行中、またはキャプチャ開始失敗時は false。</returns>
    public bool TryStartAudioDecode(
        int deviceNumber,
        FileWavCodecProfile profile,
        string? outputDirectory = null,
        double inputGain = 0.8)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var outDir = string.IsNullOrWhiteSpace(outputDirectory)
            ? AppPaths.OutputDir
            : Path.GetFullPath(outputDirectory);

        lock (_sync)
        {
            if (IsBusyLocked())
            {
                return false;
            }

            StopLiveLocked();

            var codec = new FileWavCodec(profile);
            var session = new RealtimeDecodeSession(
                codec,
                profile.SampleRate,
                profile.ChannelMode,
                DecodeRuntimeTuning.Default,
                pollInterval: TimeSpan.FromMilliseconds(100),
                minAttemptSeconds: 2,
                sharedStatus: _sharedStatus);
            var state = session.ProgressiveState;
            state.StatusBoard.BeginRun("(音声入力)");
            state.StatusBoard.SetProgress(new CoreProgressInfo(
                CurrentFrame: CoreFrameKind.Fh,
                CurrentBlockIndex: -1,
                PassIndex: 0,
                AcceptedBlockCount: 0,
                TotalBlockCount: 0,
                ProgressPercent: 1.0));
            state.FileHeaderReady = (fileName, fileSize, blockCount, createdAtUtc, updatedAtUtc) =>
            {
                FileHeaderReady?.Invoke(fileName, $"{fileSize:N0} bytes", blockCount, createdAtUtc, updatedAtUtc);
            };

            var capture = new RealtimePcmCapture();
            capture.SamplesAvailable += (left, right) =>
            {
                try
                {
                    session.AppendSamples(left, right);
                }
                catch (Exception ex)
                {
                    FailLive(ex.Message, outDir);
                }
            };
            capture.CaptureFailed += message => FailLive(message, outDir);

            _state = state;
            _liveSession = session;
            _capture = capture;
            _liveMode = true;
            _decodedBytes = null;
            _lastDecodedPath = null;
            _lastError = null;
            _completionPending = false;
            var runGeneration = ++_runGeneration;

            session.Start();
            try
            {
                capture.Start(deviceNumber, profile.ChannelMode, profile.SampleRate, inputGain);
            }
            catch (Exception ex)
            {
                StopLiveLocked();
                _liveMode = false;
                _lastError = ex.Message;
                _completionPending = true;
                return false;
            }

            // 完了監視（デコード成功/失敗）
            _worker = Task.Factory.StartNew(
                () => WatchLiveCompletion(session, outDir, runGeneration),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            return true;
        }
    }

    /// <summary>
    /// 受信中のデコードを中断します。
    /// </summary>
    /// <returns>中断要求を受理した場合 true。</returns>
    public bool RequestStop()
    {
        lock (_sync)
        {
            var busy = (_liveMode && _liveSession is not null)
                || _worker is { IsCompleted: false };
            if (!busy && !_completionPending)
            {
                return false;
            }

            _runGeneration++;
            StopLiveLocked();
            _liveMode = false;
            _worker = null;

            if (!_completionPending)
            {
                _lastError = "受信を中断しました。";
                _decodedBytes = null;
                _lastDecodedPath = null;
                _completionPending = true;
                _state?.StatusBoard.Complete(faulted: true, _lastError);
            }

            return true;
        }
    }

    /// <summary>
    /// 共有状態メモリを読み取ります（コアへの問い合わせではありません）。
    /// </summary>
    /// <returns>現在の実行状態スナップショット。</returns>
    public CoreExecutionStatus ReadExecutionStatus() => _sharedStatus.Read();

    /// <summary>
    /// <see cref="ReadExecutionStatus"/> の互換エイリアスです。
    /// </summary>
    /// <returns>現在の実行状態スナップショット。</returns>
    public CoreExecutionStatus QueryExecutionStatus() => ReadExecutionStatus();

    /// <summary>
    /// 完了結果を1回だけ取り出します。
    /// </summary>
    /// <param name="success">復号成功なら true。</param>
    /// <param name="message">成功／失敗メッセージ。</param>
    /// <param name="outputPath">出力パス（現状は常に null。履歴へ保持）。</param>
    /// <returns>完了が保留中で取り出せた場合 true。未完了なら false。</returns>
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
    /// <returns>ペイロード付き不明ブロックの配列。状態が無ければ空配列。</returns>
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
                // 不明ブロックはペイロード受信成功分のみ。
                if (pair.Value is not { Length: > 0 })
                {
                    continue;
                }

                var detail = _state.OrphanDetailByHash.TryGetValue(pair.Key, out var text)
                    ? text
                    : string.Empty;
                var dataModulation = _state.OrphanDataModulationByHash.TryGetValue(pair.Key, out var dm)
                    && dm is { Length: > 0 }
                    ? dm.ToArray()
                    : new byte[4];
                result.Add(new ReceiveOrphanHistory(pair.Key, detail, pair.Value.ToArray(), dataModulation));
            }

            return result.ToArray();
        }
    }

    /// <summary>
    /// FH/BH から得たファイル全体ハッシュ（SHA-512 16進）を返します。
    /// </summary>
    /// <returns>ファイルハッシュの 16 進文字列。未受信時は空文字。</returns>
    public string CaptureFileHashHex()
    {
        lock (_sync)
        {
            return _state?.ReceivedFileHashHex ?? string.Empty;
        }
    }

    /// <summary>
    /// 復元済みペイロードのコピーを返します。
    /// </summary>
    /// <returns>復号バイト列のコピー。未完了／失敗時は null。</returns>
    public byte[]? CaptureDecodedPayload()
    {
        lock (_sync)
        {
            return _decodedBytes?.ToArray();
        }
    }

    /// <summary>
    /// 受信済みブロックの履歴保存用スナップショットを返します。
    /// </summary>
    /// <returns>ブロック番号 → 変調・ハッシュ・ペイロード。未受信スロットは含めない。</returns>
    public IReadOnlyDictionary<int, ReceiveCapturedBlockInfo> CaptureReceivedBlocks()
    {
        lock (_sync)
        {
            if (_state is null)
            {
                return new Dictionary<int, ReceiveCapturedBlockInfo>();
            }

            var result = new Dictionary<int, ReceiveCapturedBlockInfo>();
            var slots = _state.OutputSlots;
            if (slots is null)
            {
                return result;
            }

            for (var i = 0; i < slots.Length; i++)
            {
                var payload = slots[i];
                if (payload is null || payload.Length == 0)
                {
                    continue;
                }

                var dataModulation = _state.BlockDataModulationByIndex.TryGetValue(i, out var dm)
                    ? NormalizeDataModulation(dm)
                    : new byte[4];
                var contentHash = _state.BlockExpectedHashByIndex.TryGetValue(i, out var hash)
                    ? NormalizeHash32(hash)
                    : Hash.ComputeSha256(payload);

                result[i] = new ReceiveCapturedBlockInfo(
                    dataModulation,
                    contentHash,
                    payload.ToArray());
            }

            return result;
        }
    }

    /// <summary>
    /// BH 受信済みブロックのメタ（変調・宣言サイズ）を返します。BD 未受信でも含みます。
    /// </summary>
    /// <returns>ブロック番号 → 変調・ハッシュ・宣言サイズ。</returns>
    public IReadOnlyDictionary<int, ReceiveCapturedBlockHeaderInfo> CaptureReceivedBlockHeaders()
    {
        lock (_sync)
        {
            if (_state is null)
            {
                return new Dictionary<int, ReceiveCapturedBlockHeaderInfo>();
            }

            var result = new Dictionary<int, ReceiveCapturedBlockHeaderInfo>();
            foreach (var pair in _state.BlockDataModulationByIndex)
            {
                var index = pair.Key;
                if (index < 0)
                {
                    continue;
                }

                var dataModulation = NormalizeDataModulation(pair.Value);
                var contentHash = _state.BlockExpectedHashByIndex.TryGetValue(index, out var hash)
                    ? NormalizeHash32(hash)
                    : new byte[32];
                var blockSize = _state.BlockSizeByIndex.TryGetValue(index, out var size)
                    ? Math.Max(0, size)
                    : 0;
                result[index] = new ReceiveCapturedBlockHeaderInfo(dataModulation, contentHash, blockSize);
            }

            return result;
        }
    }

    /// <summary>
    /// BD 処理済みブロックの成否（true=OK / false=NG）を返します。
    /// </summary>
    /// <returns>ブロック番号 → BD 成否。</returns>
    public IReadOnlyDictionary<int, bool> CaptureBlockBdOutcomes()
    {
        lock (_sync)
        {
            if (_state is null)
            {
                return new Dictionary<int, bool>();
            }

            return new Dictionary<int, bool>(_state.BlockBdOutcomeByIndex);
        }
    }

    /// <summary>
    /// データ部変調方式バイト列を長さ 4 に正規化します。
    /// </summary>
    /// <param name="source">元の変調方式バイト列。</param>
    /// <returns>先頭最大 4 バイトをコピーした長さ 4 の配列。不足分は 0。</returns>
    private static byte[] NormalizeDataModulation(byte[] source)
    {
        var normalized = new byte[4];
        if (source is { Length: > 0 })
        {
            Buffer.BlockCopy(source, 0, normalized, 0, Math.Min(4, source.Length));
        }

        return normalized;
    }

    /// <summary>
    /// ブロックハッシュを長さ 32 に正規化します。
    /// </summary>
    /// <param name="source">元のハッシュバイト列。</param>
    /// <returns>先頭最大 32 バイトをコピーした長さ 32 の配列。不足分は 0。</returns>
    private static byte[] NormalizeHash32(byte[] source)
    {
        var normalized = new byte[32];
        if (source is { Length: > 0 })
        {
            Buffer.BlockCopy(source, 0, normalized, 0, Math.Min(32, source.Length));
        }

        return normalized;
    }

    /// <summary>
    /// ライブ受信（キャプチャ／セッション）を停止しリソースを解放します。
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            StopLiveLocked();
        }
    }

    /// <summary>
    /// 共有 StatusBoard 付きの ProgressiveDecodeState を生成し、開始進捗と FH 通知を設定します。
    /// </summary>
    /// <param name="runLabel">実行ラベル（StatusBoard.BeginRun 用）。</param>
    /// <returns>初期化済みの復号状態。</returns>
    private ProgressiveDecodeState CreateState(string runLabel)
    {
        var state = new ProgressiveDecodeState(_sharedStatus);
        state.StatusBoard.BeginRun(runLabel);
        state.StatusBoard.SetProgress(new CoreProgressInfo(
            CurrentFrame: CoreFrameKind.Fh,
            CurrentBlockIndex: -1,
            PassIndex: 0,
            AcceptedBlockCount: 0,
            TotalBlockCount: 0,
            ProgressPercent: 1.0));
        state.FileHeaderReady = (fileName, fileSize, blockCount, createdAtUtc, updatedAtUtc) =>
        {
            FileHeaderReady?.Invoke(fileName, $"{fileSize:N0} bytes", blockCount, createdAtUtc, updatedAtUtc);
        };
        return state;
    }

    /// <summary>
    /// ロック保持中に、ライブ受信またはバッチワーカーが稼働中かを判定します。
    /// </summary>
    /// <returns>新規開始を拒否すべき場合 true。</returns>
    private bool IsBusyLocked()
    {
        if (_liveMode && _liveSession is not null && !_completionPending)
        {
            return true;
        }

        return _worker is { IsCompleted: false };
    }

    /// <summary>
    /// ロック保持中にキャプチャとライブセッションを停止・破棄します。
    /// </summary>
    private void StopLiveLocked()
    {
        try
        {
            _capture?.Stop();
            _capture?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            _liveSession?.Stop();
            _liveSession?.Dispose();
        }
        catch
        {
            // ignore
        }

        _capture = null;
        _liveSession = null;
    }

    /// <summary>
    /// ライブ復号の完了／失敗を監視し、成功または失敗処理へ分岐します。
    /// </summary>
    /// <param name="session">監視対象のリアルタイム復号セッション。</param>
    /// <param name="outputDirectory">完了処理へ渡す出力ディレクトリ（現状未使用）。</param>
    /// <param name="runGeneration">この監視が属する実行世代。</param>
    private void WatchLiveCompletion(RealtimeDecodeSession session, string outputDirectory, int runGeneration)
    {
        while (true)
        {
            Thread.Sleep(100);
            lock (_sync)
            {
                if (runGeneration != _runGeneration || _completionPending)
                {
                    return;
                }
            }

            if (session.TryConsumeDecoded(out var decoded) && decoded.Length > 0)
            {
                CompleteLiveSuccess(decoded, outputDirectory, runGeneration);
                return;
            }

            var snap = session.GetSnapshot();
            if (!snap.IsRunning)
            {
                lock (_sync)
                {
                    if (runGeneration != _runGeneration || _completionPending || _liveSession is null)
                    {
                        return;
                    }

                    if (_state is { Completed: true, CompletedFile: not null } done)
                    {
                        CompleteLiveSuccess(done.CompletedFile, outputDirectory, runGeneration);
                        return;
                    }

                    if (snap.DecodedBytes is null)
                    {
                        FailLive(
                            _state?.LastError ?? snap.LastError ?? "Receive failed.",
                            outputDirectory,
                            runGeneration);
                        return;
                    }
                }
            }

            lock (_sync)
            {
                if (runGeneration != _runGeneration || _completionPending || _liveSession is null)
                {
                    return;
                }

                if (_state is { Completed: true, CompletedFile: not null } state)
                {
                    CompleteLiveSuccess(state.CompletedFile, outputDirectory, runGeneration);
                    return;
                }

                if (_state is { Completed: true } failed && failed.CompletedFile is null)
                {
                    FailLive(failed.LastError ?? "Receive failed.", outputDirectory, runGeneration);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// ライブ／バッチ復号成功時にペイロードを保持し、完了フラグを立ててライブ資源を解放します。
    /// </summary>
    /// <param name="decoded">復元済みペイロード。</param>
    /// <param name="outputDirectory">未使用（履歴へ保持するため out_files へは書かない）。</param>
    /// <param name="runGeneration">この完了が属する実行世代。</param>
    private void CompleteLiveSuccess(byte[] decoded, string outputDirectory, int runGeneration)
    {
        lock (_sync)
        {
            if (_completionPending || runGeneration != _runGeneration)
            {
                return;
            }

            try
            {
                // 復号結果は履歴（Onta_history.bin）に保持する。out_files への自動ダンプはしない
                //（必要なときだけ履歴画面のダウンロードから保存する）。
                _ = outputDirectory;
                _decodedBytes = decoded;
                _lastDecodedPath = null;
                _lastError = null;
                _completionPending = true;
                _state?.StatusBoard.Complete(faulted: false);
            }
            catch (Exception ex)
            {
                _decodedBytes = null;
                _lastDecodedPath = null;
                _lastError = ex.Message;
                _completionPending = true;
                _state?.StatusBoard.Complete(faulted: true, ex.Message);
            }
            finally
            {
                StopLiveLocked();
                _liveMode = false;
            }
        }
    }

    /// <summary>
    /// ライブ／バッチ復号失敗を記録し、完了フラグを立ててライブ資源を解放します。
    /// </summary>
    /// <param name="message">失敗メッセージ。</param>
    /// <param name="outputDirectory">未使用（完了経路のシグネチャ合わせ）。</param>
    /// <param name="runGeneration">実行世代。負値なら世代チェックを省略。</param>
    private void FailLive(string message, string outputDirectory, int runGeneration = -1)
    {
        lock (_sync)
        {
            if (_completionPending)
            {
                return;
            }

            if (runGeneration >= 0 && runGeneration != _runGeneration)
            {
                return;
            }

            _lastError = message;
            _decodedBytes = null;
            _lastDecodedPath = null;
            _completionPending = true;
            _state?.StatusBoard.Complete(faulted: true, message);
            StopLiveLocked();
            _liveMode = false;
        }
    }

    /// <summary>
    /// WAV を一括読み込みして復号します（テストと同じ経路。進捗は StatusBoard）。
    /// </summary>
    /// <param name="wavPath">入力 WAV パス。</param>
    /// <param name="profile">復号用コーデックプロファイル。</param>
    /// <param name="state">進捗・orphan 等を保持する復号状態。</param>
    /// <param name="outputDirectory">完了処理へ渡す出力ディレクトリ（現状未使用）。</param>
    /// <param name="runGeneration">このバッチが属する実行世代。</param>
    private void RunBatchWavDecode(
        string wavPath,
        FileWavCodecProfile profile,
        ProgressiveDecodeState state,
        string outputDirectory,
        int runGeneration)
    {
        try
        {
            state.StatusBoard.SetProgress(new CoreProgressInfo(
                CurrentFrame: CoreFrameKind.Fh,
                CurrentBlockIndex: -1,
                PassIndex: 0,
                AcceptedBlockCount: 0,
                TotalBlockCount: 0,
                ProgressPercent: 1.5));

            var codec = new FileWavCodec(profile);
            var (leftSamples, rightSamples) = WavReader.ReadPcm16(wavPath);

            lock (_sync)
            {
                if (runGeneration != _runGeneration || _completionPending)
                {
                    return;
                }
            }

            state.StatusBoard.SetProgress(new CoreProgressInfo(
                CurrentFrame: CoreFrameKind.Fh,
                CurrentBlockIndex: -1,
                PassIndex: 0,
                AcceptedBlockCount: 0,
                TotalBlockCount: 0,
                ProgressPercent: 2.0));

            var status = codec.DecodePcmSamplesProgressive(
                leftSamples,
                rightSamples,
                state,
                correctWow: true,
                wowParams: null,
                tuning: DecodeRuntimeTuning.Default,
                allowIncomplete: false);

            if (status == ProgressiveDecodeStatus.Completed && state.CompletedFile is not null)
            {
                CompleteLiveSuccess(state.CompletedFile, outputDirectory, runGeneration);
                return;
            }

            FailLive(state.LastError ?? "Decode failed.", outputDirectory, runGeneration);
        }
        catch (Exception ex)
        {
            FailLive(ex.Message, outputDirectory, runGeneration);
        }
    }

    /// <summary>
    /// WAV をチャンク逐次読みし、RealtimeDecodeSession へ供給します。
    /// </summary>
    /// <param name="wavPath">入力 WAV パス。</param>
    /// <param name="profile">復号用コーデックプロファイル（サンプルレート／チャネル照合用）。</param>
    /// <param name="session">PCM を受け取るリアルタイム復号セッション。</param>
    /// <param name="outputDirectory">失敗／完了処理へ渡す出力ディレクトリ（現状未使用）。</param>
    private void RunStreamingWavDecode(
        string wavPath,
        FileWavCodecProfile profile,
        RealtimeDecodeSession session,
        string outputDirectory)
    {
        try
        {
            using var reader = WavPcmStreamReader.Open(wavPath);
            if (reader.SampleRate != profile.SampleRate)
            {
                FailLive(
                    $"WAV sample rate {reader.SampleRate} does not match profile {profile.SampleRate}.",
                    outputDirectory);
                return;
            }

            var expectStereo = profile.ChannelMode == ChannelMode.Stereo;
            if (expectStereo && reader.Channels < 2)
            {
                FailLive("Stereo profile requires a 2ch WAV.", outputDirectory);
                return;
            }

            // 背圧: 末尾ブロック(SC36/64QAM 等)が収まるよう余裕を持たせる
            var maxBuffered = profile.SampleRate * 30;
            var chunkFrames = Math.Max(1, profile.SampleRate / 10); // 100ms

            while (reader.TryRead(chunkFrames, out var left, out var right))
            {
                lock (_sync)
                {
                    if (_completionPending || _liveSession is null)
                    {
                        return;
                    }
                }

                while (session.BufferedSampleCount > maxBuffered)
                {
                    lock (_sync)
                    {
                        if (_completionPending || _liveSession is null)
                        {
                            return;
                        }
                    }

                    if (session.ProgressiveState.Completed)
                    {
                        break;
                    }

                    var snap = session.GetSnapshot();
                    if (!snap.IsRunning && snap.DecodedBytes is null)
                    {
                        break;
                    }

                    Thread.Sleep(20);
                }

                if (!expectStereo)
                {
                    right = Array.Empty<System.Numerics.Complex>();
                }

                session.AppendSamples(left, right);

                // FH 前はファイル読み進捗を薄い％で見せる（ワウ探索中の 2.5%+ は上書きしない）
                var state = session.ProgressiveState;
                if (!state.HeaderReady && !state.OpeningWowSearchDone)
                {
                    var readPct = 1.0 + (2.5 * reader.Progress);
                    state.StatusBoard.SetProgress(new CoreProgressInfo(
                        CurrentFrame: CoreFrameKind.Fh,
                        CurrentBlockIndex: -1,
                        PassIndex: 0,
                        AcceptedBlockCount: 0,
                        TotalBlockCount: 0,
                        ProgressPercent: Math.Clamp(readPct, 1.0, 3.5)));
                }
            }

            session.NotifyInputCompleted();
            int runGeneration;
            lock (_sync)
            {
                runGeneration = _runGeneration;
            }

            WatchLiveCompletion(session, outputDirectory, runGeneration);

            lock (_sync)
            {
                if (!_completionPending)
                {
                    var err = session.ProgressiveState.LastError
                              ?? session.GetSnapshot().LastError
                              ?? "Decode did not complete.";
                    FailLive(err, outputDirectory);
                }
            }
        }
        catch (Exception ex)
        {
            FailLive(ex.Message, outputDirectory);
        }
    }
}
