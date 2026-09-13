using Onta.Core;
using Onta.History;

namespace Onta.View.Core;

/// <summary>
/// WAV / 音声入力の段階的デコードをバックグラウンドで実行するワーカーです。
/// </summary>
internal sealed class InputCoreWorker : IDisposable
{
    private readonly object _sync = new();
    private ProgressiveDecodeState? _state;
    private Task? _worker;
    private RealtimeDecodeSession? _liveSession;
    private RealtimePcmCapture? _capture;
    private string? _lastDecodedPath;
    private byte[]? _decodedBytes;
    private bool _completionPending;
    private string? _lastError;
    private bool _liveMode;

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
    /// WAVデコード処理を開始します（バックグラウンドで一括復号。UI は StatusBoard をポーリング）。
    /// 逐次ストリーミングは音声入力専用（ファイル WAV では不安定だったため）。
    /// </summary>
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

            _worker = Task.Factory.StartNew(
                () => RunBatchWavDecode(wavPath, profile, state, outDir),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            return true;
        }
    }

    /// <summary>
    /// 音声入力デバイスからリアルタイム受信を開始します。
    /// </summary>
    public bool TryStartAudioDecode(
        int deviceNumber,
        FileWavCodecProfile profile,
        string? outputDirectory = null)
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
                minAttemptSeconds: 2);
            var state = session.ProgressiveState;
            state.StatusBoard.BeginRun("(音声入力)");
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

            session.Start();
            try
            {
                capture.Start(deviceNumber, profile.ChannelMode, profile.SampleRate);
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
                () => WatchLiveCompletion(session, outDir),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            return true;
        }
    }

    /// <summary>
    /// 現在の実行状態を取得します。
    /// </summary>
    public CoreExecutionStatus QueryExecutionStatus()
    {
        lock (_sync)
        {
            if (_liveSession is not null)
            {
                return _liveSession.QueryExecutionStatus();
            }

            return _state?.QueryExecutionStatus() ?? CoreExecutionStatus.Idle;
        }
    }

    /// <summary>
    /// 完了結果を1回だけ取り出します。
    /// </summary>
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
    public byte[]? CaptureDecodedPayload()
    {
        lock (_sync)
        {
            return _decodedBytes?.ToArray();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            StopLiveLocked();
        }
    }

    private ProgressiveDecodeState CreateState(string runLabel)
    {
        var state = new ProgressiveDecodeState();
        state.StatusBoard.BeginRun(runLabel);
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
        return state;
    }

    private bool IsBusyLocked()
    {
        if (_liveMode && _liveSession is not null && !_completionPending)
        {
            return true;
        }

        return _worker is { IsCompleted: false };
    }

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

    private void FailLive(string message, string outputDirectory)
    {
        lock (_sync)
        {
            if (_completionPending)
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

    private void WatchLiveCompletion(RealtimeDecodeSession session, string outputDirectory)
    {
        while (true)
        {
            Thread.Sleep(100);
            if (session.TryConsumeDecoded(out var decoded) && decoded.Length > 0)
            {
                CompleteLiveSuccess(decoded, outputDirectory);
                return;
            }

            var snap = session.GetSnapshot();
            if (!snap.IsRunning)
            {
                lock (_sync)
                {
                    if (_completionPending || _liveSession is null)
                    {
                        return;
                    }

                    if (_state is { Completed: true, CompletedFile: not null } done)
                    {
                        CompleteLiveSuccess(done.CompletedFile, outputDirectory);
                        return;
                    }

                    if (snap.DecodedBytes is null)
                    {
                        FailLive(
                            _state?.LastError ?? snap.LastError ?? "Receive failed.",
                            outputDirectory);
                        return;
                    }
                }
            }

            lock (_sync)
            {
                if (_completionPending || _liveSession is null)
                {
                    return;
                }

                if (_state is { Completed: true, CompletedFile: not null } state)
                {
                    CompleteLiveSuccess(state.CompletedFile, outputDirectory);
                    return;
                }

                if (_state is { Completed: true } failed && failed.CompletedFile is null)
                {
                    FailLive(failed.LastError ?? "Receive failed.", outputDirectory);
                    return;
                }
            }
        }
    }

    private void CompleteLiveSuccess(byte[] decoded, string outputDirectory)
    {
        lock (_sync)
        {
            if (_completionPending)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(outputDirectory);
                var name = !string.IsNullOrWhiteSpace(_state?.ReceivedFileName)
                    ? Path.GetFileName(_state!.ReceivedFileName)
                    : "audio_rx.bin";
                name = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = "audio_rx.bin";
                }

                var outPath = Path.Combine(outputDirectory, name);
                File.WriteAllBytes(outPath, decoded);
                _decodedBytes = decoded;
                _lastDecodedPath = outPath;
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
    /// WAV を一括読み込みして復号します（テストと同じ経路。進捗は StatusBoard）。
    /// </summary>
    private void RunBatchWavDecode(
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
                ProgressPercent: 1.5));

            var codec = new FileWavCodec(profile);
            var (leftSamples, rightSamples) = WavReader.ReadPcm16(wavPath);

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
                CompleteLiveSuccess(state.CompletedFile, outputDirectory);
                return;
            }

            FailLive(state.LastError ?? "Decode failed.", outputDirectory);
        }
        catch (Exception ex)
        {
            FailLive(ex.Message, outputDirectory);
        }
    }

    /// <summary>
    /// WAV をチャンク逐次読みし、RealtimeDecodeSession へ供給します。
    /// </summary>
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
            WatchLiveCompletion(session, outputDirectory);

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
