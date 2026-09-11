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
        _inputCoreWorker.FileHeaderReady += OnReceiveFileHeaderReady;
        _progressPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _progressPollTimer.Tick += OnProgressPollTick;
        Closed += (_, _) =>
        {
            _inputCoreWorker.FileHeaderReady -= OnReceiveFileHeaderReady;
            StopAudioPlayback();
        };
        LoadReceiveHistoryAtStartup();
        RefreshEstimate();
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

            ReceivePanel.StopDemoFeed();
            ReceivePanel.SetWowFlutterPercent(0, 0);
            // 新規送信開始に合わせて進捗表示を初期化する。
            EstimatePanel.ResetProgress();
            BottomTabs.SelectedItem = EstimateTab;
            SendPanel.SetTransmissionRunning(true);
            // 前回受信時のIQ表示を消して誤解を防ぐ。
            ReceivePanel.ClearIqDisplay();
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
    }

    /// <summary>
    /// 受信FH確定時に受信パネルと詳細パネルへヘッダー情報を反映します。
    /// </summary>
    /// <param name="fileName">受信ファイル名。</param>
    /// <param name="fileSizeText">表示用ファイルサイズ。</param>
    /// <param name="blockCount">総ブロック数。</param>
    private void OnReceiveFileHeaderReady(string fileName, string fileSizeText, int blockCount)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            ReceivePanel.SetFileInfo(fileName, fileSizeText, blockCount.ToString());
            ReceiveDetailPanel.ApplyFileHeader(fileName, fileSizeText, blockCount);
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
        if (!ReceivePanel.UseWavInput)
        {
            MessageBox.Show(
                this,
                $"Audio input mode is not implemented yet. Device: {ReceivePanel.AudioDeviceName}",
                "Onta",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
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

            var profile = CodecProfileFactory.ForWavReceive(ReceivePanel.SelectedWavPath);
            ReceivePanel.SetWowChannelMode(profile.ChannelMode);
            ReceivePanel.StopDemoFeed();
            ReceivePanel.ErrorGraph.Clear();
            ReceivePanel.FftGraph.Clear();
            ReceivePanel.IqGraph.Clear();
            ReceiveDetailPanel.Clear();
            ReceiveDetailPanel.SetSourcePath(ReceivePanel.SelectedWavPath);
            _receiveDetailOpened = false;
            _lastReceiveHistorySnapshotKey = string.Empty;

            if (!_inputCoreWorker.TryStartWavDecode(ReceivePanel.SelectedWavPath, profile, outputDir))
            {
                MessageBox.Show(this, "Receive core is already running.", "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

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
            if (!_progressPollTimer.IsEnabled)
            {
                _progressPollTimer.Start();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"受信開始に失敗しました。\n{ex.Message}", "Onta", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 送受信進捗を定期ポーリングして UI へ反映します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnProgressPollTick(object? sender, EventArgs e)
    {
        var sendProgress = _coreWorker.GetProgress();

        if (_pollingReceive)
        {
            // 受信実行状態を各表示へ反映する。
            var status = _inputCoreWorker.QueryExecutionStatus();
            ReceivePanel.ApplyExecutionStatus(status);
            ReceiveDetailPanel.ApplyStatus(status);
            SaveReceiveHistoryIfChanged(force: false);

            // FH確定後に未表示なら受信詳細タブへ遷移する。
            if (!_receiveDetailOpened && ReceiveDetailPanel.HasFileHeaderInfo(status))
            {
                _receiveDetailOpened = true;
                BottomTabs.SelectedItem = ReceiveDetailTab;
            }

            if (_inputCoreWorker.TryConsumeCompletion(out var success, out var message, out var outputPath))
            {
                _pollingReceive = false;
                ReceiveDetailPanel.MarkCompletion(success, message, outputPath);
                SaveReceiveHistoryIfChanged(force: true);
                HistoryPanel.ReloadHistory();

                if (success)
                {
                    MessageBox.Show(
                        this,
                        $"{message}\n出力: {outputPath}",
                        "Onta",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(this, message, "Onta", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // 送信見積りの全体進捗メーターを更新する。
        EstimatePanel.ApplyProgress(sendProgress.ElapsedAudioSeconds, sendProgress.IsRunning);
        _ = _coreWorker.ConsumeFrameEvents();

        if (_coreWorker.TryConsumeCompletion(out var completion))
        {
            SendPanel.SetTransmissionRunning(false);
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
            var payload = _inputCoreWorker.CaptureDecodedPayload() ?? Array.Empty<byte>();
            var entry = ReceiveDetailPanel.CaptureHistoryEntry(orphans, payload);

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
            entry.ContentHashHex,
            entry.SourcePath,
            entry.FileName,
            entry.FileSize,
            entry.BlockCount,
            blockFingerprint,
            entry.Orphans.Count,
            entry.Payload.Length,
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
            HistoryService.SaveSend(AppPaths.ReceiveHistoryFilePath, input, outputPath, "Send completed");
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




