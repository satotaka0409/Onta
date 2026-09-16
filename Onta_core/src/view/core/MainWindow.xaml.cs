using System.IO;
using System.Windows;
using Microsoft.Win32;
using NAudio.Wave;
using Onta.Core;
using Onta.History;
using System.Windows.Threading;

namespace Onta.View.Core;

/// <summary>
/// 送受信ワークフロー全体を統括するメインウィンドウです。
/// </summary>
public partial class MainWindow : Window
{
    private readonly OutputCoreWorker _coreWorker = new();
    private readonly InputCoreWorker _inputCoreWorker = new();
    private readonly DispatcherTimer _progressPollTimer;
    private WaveOutEvent? _activeWaveOut;
    private AudioFileReader? _activeAudioReader;
    private bool _pollingReceive;
    private bool _receiveDetailOpened;
    private string _lastReceiveHistorySnapshotKey = string.Empty;

    /// <summary>
    /// メインウィンドウを初期化し、各パネルのイベントを接続します。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        SendPanel.SettingsChanged += (_, _) => RefreshEstimate();
        SendPanel.OutputRequested += OnOutputRequested;
        SendPanel.StopRequested += OnStopRequested;
        ReceivePanel.ReceiveStartRequested += OnReceiveStartRequested;
        ReceivePanel.ReceiveStopRequested += OnReceiveStopRequested;
        _inputCoreWorker.FileHeaderReady += OnReceiveFileHeaderReady;
        _progressPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _progressPollTimer.Tick += OnProgressPollTick;
        Closed += (_, _) =>
        {
            SaveMainSettings();
            _inputCoreWorker.FileHeaderReady -= OnReceiveFileHeaderReady;
            _inputCoreWorker.Dispose();
            StopAudioPlayback();
        };
        LoadMainSettings();
        LoadReceiveHistoryAtStartup();
        RefreshEstimate();
    }

    private void LoadMainSettings()
    {
        try
        {
            if (!MainWindowSettingsStore.TryLoad(AppPaths.MainSettingsFilePath, out var settings))
            {
                return;
            }

            SendPanel.ApplySnapshot(settings.Send);
            ReceivePanel.ApplySettings(settings.Receive);
        }
        catch
        {
            // 設定読込失敗時は既定値で継続する。
        }
    }

    private void SaveMainSettings()
    {
        try
        {
            var settings = new MainWindowSettings(
                Send: SendPanel.CreateSnapshot(),
                Receive: ReceivePanel.CaptureSettings());
            MainWindowSettingsStore.Save(AppPaths.MainSettingsFilePath, settings);
        }
        catch
        {
            // 設定保存失敗時も終了処理は継続する。
        }
    }

    /// <summary>
    /// 現在の送信設定で見積りパネルを更新します。
    /// </summary>
    private void RefreshEstimate()
    {
        var snap = SendPanel.CreateSnapshot();
        EstimatePanel.UpdateEstimate(snap);

        // 入力が選択済みなら見積りタブを前面にする。
        if (!string.IsNullOrWhiteSpace(snap.InputFilePath) && File.Exists(snap.InputFilePath))
        {
            BottomTabs.SelectedItem = EstimateTab;
        }
    }

    /// <summary>
    /// 送信開始要求を処理します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="snap">送信設定スナップショット。</param>
    private void OnOutputRequested(object? sender, SendSettingsSnapshot snap)
    {
        if (string.IsNullOrWhiteSpace(snap.InputFilePath))
        {
            MessageBox.Show(this, "Please select an input file.", "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!File.Exists(snap.InputFilePath))
        {
            MessageBox.Show(this, "Input file was not found.", "Onta", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!snap.WriteWav && !snap.PlayAudio)
        {
            MessageBox.Show(this, "Enable WAV output or realtime audio output.", "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var outputWavPath = ResolveOutputWavPath(snap);
            if (snap.WriteWav && outputWavPath is null)
            {
                return;
            }

            if (!_coreWorker.TryStart(snap, outputWavPath))
            {
                MessageBox.Show(this, "Core is already running. Stop current transmission first.", "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 送信開始前に既存の再生状態をリセットする。
            StopAudioPlayback();

            // 送信中は受信操作を止め、前回の受信可視化をクリアする。
            ReceivePanel.ResetVisualization();
            ReceiveDetailPanel.Clear();
            ReceivePanel.SetInteractionEnabled(false);
            ReceivePanel.ShowFftTab();
            _receiveDetailOpened = false;
            _lastReceiveHistorySnapshotKey = string.Empty;

            // 新規送信開始に合わせて進捗表示を初期化する。
            EstimatePanel.ResetProgress();
            BottomTabs.SelectedItem = EstimateTab;
            SendPanel.SetTransmissionRunning(true);
            // 受信ワーカー未実行なら受信ポーリングは止める。
            if (!_inputCoreWorker.IsRunning)
            {
                _pollingReceive = false;
            }

            if (!_progressPollTimer.IsEnabled)
            {
                _progressPollTimer.Start();
            }
        }
        catch (Exception ex)
        {
            SendPanel.SetTransmissionRunning(false);
            ReceivePanel.SetInteractionEnabled(true);
            MessageBox.Show(this, $"送信中にエラーが発生しました。\n{ex.Message}", "Onta", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 送信停止要求を Core ワーカーへ伝搬します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnStopRequested(object? sender, EventArgs e)
    {
        _ = _coreWorker.RequestStop();
        ReceivePanel.SetInteractionEnabled(true);
    }

    /// <summary>
    /// 受信停止要求を Input ワーカーへ伝搬します。
    /// </summary>
    private void OnReceiveStopRequested(object? sender, EventArgs e)
    {
        _ = _inputCoreWorker.RequestStop();
    }

    /// <summary>
    /// 受信FH確定時に受信パネルと詳細パネルへヘッダー情報を反映します。
    /// </summary>
    /// <param name="fileName">受信ファイル名。</param>
    /// <param name="fileSizeText">表示用ファイルサイズ。</param>
    /// <param name="blockCount">総ブロック数。</param>
    private void OnReceiveFileHeaderReady(
        string fileName,
        string fileSizeText,
        int blockCount,
        DateTime? createdAtUtc,
        DateTime? updatedAtUtc)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            // 送信中は受信表示を更新しない。
            if (_coreWorker.GetProgress().IsRunning)
            {
                return;
            }

            ReceivePanel.SetFileInfo(fileName, fileSizeText, blockCount.ToString());
            ReceiveDetailPanel.ApplyFileHeader(fileName, fileSizeText, blockCount, createdAtUtc, updatedAtUtc);
            SaveReceiveHistoryIfChanged(force: false);
            if (!_receiveDetailOpened)
            {
                _receiveDetailOpened = true;
                BottomTabs.SelectedItem = ReceiveDetailTab;
            }
        });
    }

    /// <summary>
    /// 受信開始要求を処理します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnReceiveStartRequested(object? sender, EventArgs e)
    {
        try
        {
            var outputDir = ReceivePanel.SelectedOutputDir;
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                MessageBox.Show(this, "Please select an output folder.", "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                Directory.CreateDirectory(outputDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"出力フォルダーを作成できません。\n{ex.Message}", "Onta", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!ReceivePanel.UseWavInput)
            {
                StartAudioReceive(outputDir);
                return;
            }

            if (string.IsNullOrWhiteSpace(ReceivePanel.SelectedWavPath))
            {
                MessageBox.Show(this, "Please select a WAV input file.", "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!File.Exists(ReceivePanel.SelectedWavPath))
            {
                MessageBox.Show(this, "WAV input file was not found.", "Onta", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var profile = CodecProfileFactory.ForWavReceive(ReceivePanel.SelectedWavPath);
            ReceivePanel.SetWowChannelMode(profile.ChannelMode);
            ReceivePanel.PrepareForNewReceive();
            ReceivePanel.SetReceiveRunning(true);
            ReceiveDetailPanel.Clear();
            ReceiveDetailPanel.SetSourcePath(ReceivePanel.SelectedWavPath);
            _receiveDetailOpened = false;
            _lastReceiveHistorySnapshotKey = string.Empty;

            if (!_inputCoreWorker.TryStartWavDecode(ReceivePanel.SelectedWavPath, profile, outputDir))
            {
                ReceivePanel.SetReceiveRunning(false);
                MessageBox.Show(this, "Receive core is already running.", "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 受信スタート直後に受信詳細タブへ切り替える。
            BottomTabs.SelectedItem = ReceiveDetailTab;
            _receiveDetailOpened = true;

            // 受信開始時点の入力情報をパネルへ表示する。
            var inputDisplayName = Path.GetFileName(ReceivePanel.SelectedWavPath);
            var inputSizeText = "-";
            try
            {
                inputSizeText = $"{new FileInfo(ReceivePanel.SelectedWavPath).Length:N0} bytes";
            }
            catch
            {
                // サイズ取得失敗時は既定値 "-" を維持する。
            }

            ReceivePanel.SetFileInfo(inputDisplayName, inputSizeText, "-");
            ReceivePanel.SetProgressText("FH 待機中...");

            _pollingReceive = true;
            // 前回完了で止まっていても確実に再開する。
            _progressPollTimer.Stop();
            _progressPollTimer.Start();
            // 開始直後の共有状態を1回分すぐ反映（完了済表示のまま残るのを防ぐ）。
            var startStatus = _inputCoreWorker.SharedStatus.Read();
            ReceivePanel.ApplyExecutionStatus(startStatus);
            ReceiveDetailPanel.ApplyStatus(startStatus);
        }
        catch (Exception ex)
        {
            ReceivePanel.SetReceiveRunning(false);
            MessageBox.Show(this, $"受信開始に失敗しました。\n{ex.Message}", "Onta", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 選択デバイスからのリアルタイム音声受信を開始します。
    /// </summary>
    private void StartAudioReceive(string outputDir)
    {
        // 音声入力はステレオ 44.1kHz 前提（ヘッダーは内部でモノラル扱い）
        var profile = CodecProfileFactory.ForAudioReceive(Onta.Core.ChannelMode.Stereo);
        ReceivePanel.SetWowChannelMode(profile.ChannelMode);
        ReceivePanel.PrepareForNewReceive();
        ReceivePanel.SetReceiveRunning(true);
        ReceiveDetailPanel.Clear();
        ReceiveDetailPanel.SetSourcePath($"(音声入力: {ReceivePanel.AudioDeviceName})");
        _receiveDetailOpened = false;
        _lastReceiveHistorySnapshotKey = string.Empty;

        if (!_inputCoreWorker.TryStartAudioDecode(
                ReceivePanel.AudioDeviceNumber,
                profile,
                outputDir,
                ReceivePanel.AudioVolume))
        {
            ReceivePanel.SetReceiveRunning(false);
            MessageBox.Show(
                this,
                "Receive core is already running, or audio device failed to start.",
                "Onta",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // 受信スタート直後に受信詳細タブへ切り替える。
        BottomTabs.SelectedItem = ReceiveDetailTab;
        _receiveDetailOpened = true;

        ReceivePanel.SetFileInfo($"(音声入力: {ReceivePanel.AudioDeviceName})", "-", "-");
        ReceivePanel.SetProgressText("音声入力中 / FH 待機...");

        _pollingReceive = true;
        _progressPollTimer.Stop();
        _progressPollTimer.Start();
        var audioStartStatus = _inputCoreWorker.SharedStatus.Read();
        ReceivePanel.ApplyExecutionStatus(audioStartStatus);
        ReceiveDetailPanel.ApplyStatus(audioStartStatus);
    }

    /// <summary>
    /// 共有状態メモリを定期読み取りして UI へ反映します（コアへの問い合わせはしません）。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnProgressPollTick(object? sender, EventArgs e)
    {
        var sendProgress = _coreWorker.SharedProgress;

        // 送信見積りメーターは FFT より先に更新し、可視化負荷で止まらないようにする。
        EstimatePanel.ApplyProgress(sendProgress.ElapsedAudioSeconds, sendProgress.IsRunning);

        if (sendProgress.IsRunning)
        {
            var sendStatus = _coreWorker.SharedVizStatus.Read();
            if (sendStatus.FftGraph.FftSize > 0)
            {
                ReceivePanel.ApplySendFft(sendStatus.FftGraph);
            }
        }

        if (_pollingReceive)
        {
            if (!sendProgress.IsRunning)
            {
                // コアが書き込んだ共有状態を読み、各表示へ反映する。
                // 送信中は受信可視化を触らない（送信開始時にクリアした表示を維持する）。
                var status = _inputCoreWorker.SharedStatus.Read();
                ReceivePanel.ApplyExecutionStatus(status);
                ReceiveDetailPanel.ApplyStatus(status);
                ReceiveDetailPanel.SyncBlockHeaders(_inputCoreWorker.CaptureReceivedBlockHeaders());
                ReceiveDetailPanel.SyncCapturedBlocks(_inputCoreWorker.CaptureReceivedBlocks());
                ReceiveDetailPanel.SyncBlockBdOutcomes(_inputCoreWorker.CaptureBlockBdOutcomes());

                // 受信実行中は履歴保存を省略（ディスク I/O が画面更新を遅らせる）。
                // FH 確定コールバックと完了時のみ保存する。
                if (!status.IsRunning || status.IsCompleted)
                {
                    SaveReceiveHistoryIfChanged(force: false);
                }

                // FH確定後に未表示なら受信詳細タブへ遷移する。
                if (!_receiveDetailOpened && ReceiveDetailPanel.HasFileHeaderInfo(status))
                {
                    _receiveDetailOpened = true;
                    BottomTabs.SelectedItem = ReceiveDetailTab;
                }
            }

            if (_inputCoreWorker.TryConsumeCompletion(out var success, out var message, out var outputPath))
            {
                _pollingReceive = false;
                ReceivePanel.SetReceiveRunning(false);
                if (!sendProgress.IsRunning)
                {
                    ReceiveDetailPanel.MarkCompletion(success, message, outputPath);
                    SaveReceiveHistoryIfChanged(force: true);
                    HistoryPanel.ReloadHistory();
                }

                // MessageBox はモーダルなので Tick 内で出すとポーリングが止まる。完了後に遅延表示する。
                var completionSuccess = success;
                var completionMessage = message;
                var completionPath = outputPath;
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (completionSuccess)
                    {
                        MessageBox.Show(
                            this,
                            $"{completionMessage}\n出力: {completionPath}",
                            "Onta",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    else if (string.Equals(completionMessage, "受信を中断しました。", StringComparison.Ordinal))
                    {
                        // ユーザー操作による停止はエラー扱いにしない
                    }
                    else
                    {
                        MessageBox.Show(this, completionMessage, "Onta", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                });
            }
        }

        _ = _coreWorker.ConsumeFrameEvents();

        if (_coreWorker.TryConsumeCompletion(out var completion))
        {
            SendPanel.SetTransmissionRunning(false);
            ReceivePanel.SetInteractionEnabled(true);
            EstimatePanel.ApplyProgress(
                completion.IsSuccess
                    ? Math.Max(sendProgress.TotalAudioSeconds, sendProgress.ElapsedAudioSeconds)
                    : sendProgress.ElapsedAudioSeconds,
                isRunning: false);
            HandleSendCompletion(completion);
            HistoryPanel.ReloadHistory();
        }

        if (!_pollingReceive && !_coreWorker.GetProgress().IsRunning)
        {
            _progressPollTimer.Stop();
        }
    }

    /// <summary>
    /// 起動時に最新受信履歴を読み込み、受信表示へ反映します。
    /// </summary>
    private void LoadReceiveHistoryAtStartup()
    {
        try
        {
            var latest = HistoryService.TryLoadLatestReceive(AppPaths.ReceiveHistoryFilePath);
            if (latest is null)
            {
                return;
            }

            ReceiveDetailPanel.ApplyHistory(latest);
            if (!string.IsNullOrWhiteSpace(latest.FileName) && latest.FileName != "(未受信)")
            {
                var sizeText = latest.FileSize > 0 ? $"{latest.FileSize:N0} bytes" : "-";
                var blockText = latest.BlockCount > 0 ? latest.BlockCount.ToString() : "-";
                ReceivePanel.SetFileInfo(latest.FileName, sizeText, blockText);
                ReceivePanel.SetProgressText("履歴を読み込みました");
            }
        }
        catch
        {
            // 履歴読み込み失敗時は起動を継続する。
        }
    }

    /// <summary>
    /// 現在の受信結果を履歴ファイルへ保存します。
    /// </summary>
    private void SaveReceiveHistoryIfChanged(bool force)
    {
        try
        {
            var orphans = _inputCoreWorker.CaptureOrphans();
            var blocks = _inputCoreWorker.CaptureReceivedBlocks();
            var headers = _inputCoreWorker.CaptureReceivedBlockHeaders();
            var fileHashHex = _inputCoreWorker.CaptureFileHashHex();
            var entry = ReceiveDetailPanel.CaptureHistoryEntry(orphans, blocks, fileHashHex, headers);

            var snapshotKey = BuildReceiveHistorySnapshotKey(entry);
            if (!force && string.Equals(_lastReceiveHistorySnapshotKey, snapshotKey, StringComparison.Ordinal))
            {
                return;
            }

            HistoryService.SaveReceive(AppPaths.ReceiveHistoryFilePath, entry);
            _lastReceiveHistorySnapshotKey = snapshotKey;
        }
        catch
        {
            // 履歴保存失敗時は UI を止めずに継続する。
        }
    }

    /// <summary>
    /// 受信履歴の重複保存を抑制するためのスナップショット識別子を作成します。
    /// </summary>
    /// <param name="entry">履歴エントリ。</param>
    /// <returns>現在状態を表す識別子文字列。</returns>
    private static string BuildReceiveHistorySnapshotKey(ReceiveHistoryEntry entry)
    {
        var blockFingerprint = entry.Blocks.Count == 0
            ? "none"
            : string.Join(",", entry.Blocks
                .OrderBy(b => b.BlockIndex)
                .Select(b => $"{b.BlockIndex}:{(int)b.State}:{b.ErrorText}"));

        return string.Join("|",
            (byte)entry.InputDevice,
            entry.ContentHashHex,
            entry.SourcePath,
            entry.FileName,
            entry.FileSize,
            entry.BlockCount,
            blockFingerprint,
            entry.Orphans.Count,
            entry.IsSuccess,
            entry.OutputPath,
            entry.CompletionMessage);
    }

    /// <summary>
    /// 送信成功時の履歴を保存します。
    /// </summary>
    /// <param name="completion">送信完了情報。</param>
    private static void SaveSendHistory(CoreCompletionResult completion)
    {
        try
        {
            if (!completion.IsSuccess)
            {
                return;
            }

            var input = completion.Settings.InputFilePath ?? string.Empty;
            var outputPath = completion.OutputWavPath ?? string.Empty;
            HistoryService.SaveSend(
                AppPaths.ReceiveHistoryFilePath,
                input,
                outputPath,
                completion.Settings.ActiveSubcarriers,
                completion.Settings.ModulationScheme,
                completion.Settings.ChannelMode,
                "Send completed");
        }
        catch
        {
            // 履歴保存失敗時は送信完了処理を継続する。
        }
    }

    private void HandleSendCompletion(CoreCompletionResult completion)
    {
        if (completion.WasCancelled)
        {
            MessageBox.Show(this, completion.Message, "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (completion.IsSuccess)
        {
            SaveSendHistory(completion);
            if (completion.Settings.PlayAudio && !completion.PlayedRealtime
                && !string.IsNullOrWhiteSpace(completion.OutputWavPath)
                && File.Exists(completion.OutputWavPath))
            {
                StartAudioPlayback(completion.OutputWavPath, completion.Settings.AudioDeviceNumber);
            }

            var wavLine = completion.Settings.WriteWav && !string.IsNullOrWhiteSpace(completion.OutputWavPath)
                ? $"WAV: {completion.OutputWavPath}\n"
                : "WAV: (disabled)\n";
            MessageBox.Show(
                this,
                "Transmission completed.\n\n"
                + $"Input: {completion.Settings.InputFilePath}\n"
                + wavLine
                + $"Channel: {completion.Settings.ChannelMode}\n"
                + $"Subcarrier: {completion.Settings.ActiveSubcarriers}\n"
                + $"Modulation: {completion.Settings.ModulationScheme}\n"
                + $"Interleave: {completion.Settings.BlockInterleaveFactor}\n"
                + $"Device: {completion.Settings.AudioDeviceName}\n"
                + (completion.PlayedRealtime ? "Audio: realtime playback\n" : string.Empty),
                "Onta",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(this, $"送信中にエラーが発生しました。\n{completion.Message}", "Onta", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private string? ResolveOutputWavPath(SendSettingsSnapshot snap)
    {
        if (!snap.WriteWav)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(snap.WavOutputPath))
        {
            return Path.GetFullPath(snap.WavOutputPath);
        }

        var defaultPath = BuildDefaultOutputPath(snap);
        var dlg = new SaveFileDialog
        {
            Title = "WAV 出力ファイル",
            Filter = "WAV (*.wav)|*.wav",
            InitialDirectory = Path.GetDirectoryName(defaultPath),
            FileName = Path.GetFileName(defaultPath),
            AddExtension = true,
            DefaultExt = ".wav"
        };

        if (dlg.ShowDialog(this) != true)
        {
            return null;
        }

        return Path.GetFullPath(dlg.FileName);
    }

    private static string BuildDefaultOutputPath(SendSettingsSnapshot snap)
    {
        if (!string.IsNullOrWhiteSpace(snap.WavOutputPath))
        {
            return Path.GetFullPath(snap.WavOutputPath);
        }

        var inputName = Path.GetFileNameWithoutExtension(snap.InputFilePath);
        return Path.Combine(AppPaths.OutputDir, $"{inputName}_out.wav");
    }

    private void StartAudioPlayback(string wavPath, int deviceNumber)
    {
        StopAudioPlayback();

        _activeAudioReader = new AudioFileReader(wavPath);
        _activeWaveOut = new WaveOutEvent
        {
            DeviceNumber = deviceNumber
        };
        _activeWaveOut.PlaybackStopped += OnPlaybackStopped;
        _activeWaveOut.Init(_activeAudioReader);
        _activeWaveOut.Play();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        StopAudioPlayback();
    }

    private void StopAudioPlayback()
    {
        if (_activeWaveOut is not null)
        {
            _activeWaveOut.PlaybackStopped -= OnPlaybackStopped;
            _activeWaveOut.Dispose();
            _activeWaveOut = null;
        }

        if (_activeAudioReader is not null)
        {
            _activeAudioReader.Dispose();
            _activeAudioReader = null;
        }
    }
}




