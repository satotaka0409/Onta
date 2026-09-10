using System.IO;
using System.Windows;
using Microsoft.Win32;
using NAudio.Wave;
using Onta.Core;
using System.Windows.Threading;

namespace Onta.View;

/// <summary>
/// メインウィンドウです（送信上・受信下・タブは送信／受信詳細。画面仕様.mdc）。
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

    public MainWindow()
    {
        InitializeComponent();
        SendPanel.SettingsChanged += (_, _) => RefreshEstimate();
        SendPanel.OutputRequested += OnOutputRequested;
        SendPanel.StopRequested += OnStopRequested;
        ReceivePanel.ReceiveStartRequested += OnReceiveStartRequested;
        _progressPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _progressPollTimer.Tick += OnProgressPollTick;
        Closed += (_, _) => StopAudioPlayback();
        RefreshEstimate();
    }

    private void RefreshEstimate()
    {
        var snap = SendPanel.CreateSnapshot();
        EstimatePanel.UpdateEstimate(snap);

        // 送信ファイル選択後は送信詳細タブを前面に出す。
        if (!string.IsNullOrWhiteSpace(snap.InputFilePath) && File.Exists(snap.InputFilePath))
        {
            BottomTabs.SelectedItem = EstimateTab;
        }
    }

    private void OnOutputRequested(object? sender, SendSettingsSnapshot snap)
    {
        if (string.IsNullOrWhiteSpace(snap.InputFilePath))
        {
            MessageBox.Show(this, "ファイルを選択してください。", "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!File.Exists(snap.InputFilePath))
        {
            MessageBox.Show(this, "入力ファイルが見つかりません。", "Onta", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!snap.WriteWav && !snap.PlayAudio)
        {
            MessageBox.Show(this, "WAV出力 または 音声出力 を選択してください。", "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
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
                MessageBox.Show(this, "コア処理が実行中です。完了後に再実行してください。", "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 前回のファイル再生が残っていれば止める（リアルタイム出力と競合しないように）。
            StopAudioPlayback();

            ReceivePanel.StopDemoFeed();
            ReceivePanel.SetWowFlutterPercent(0, 0);
            // 送信選択ファイルは受信パネルへ表示しない。
            EstimatePanel.ResetProgress();
            BottomTabs.SelectedItem = EstimateTab;
            SendPanel.SetTransmissionRunning(true);
            // I-Q は受信変調に連動するため、送信中は表示しない。
            ReceivePanel.ClearIqDisplay();
            _pollingReceive = false;
            if (!_progressPollTimer.IsEnabled)
            {
                _progressPollTimer.Start();
            }
        }
        catch (Exception ex)
        {
            SendPanel.SetTransmissionRunning(false);
            MessageBox.Show(this, $"出力に失敗しました。\n{ex.Message}", "Onta", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnStopRequested(object? sender, EventArgs e)
    {
        _ = _coreWorker.RequestStop();
    }

    private void OnReceiveStartRequested(object? sender, EventArgs e)
    {
        if (ReceivePanel.UseWavInput)
        {
            if (string.IsNullOrWhiteSpace(ReceivePanel.SelectedWavPath))
            {
                MessageBox.Show(this, "WAV入力ファイルを選択してください。", "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!File.Exists(ReceivePanel.SelectedWavPath))
            {
                MessageBox.Show(this, "WAV ファイルが見つかりません。", "Onta", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var snap = SendPanel.CreateSnapshot();
            // モノラル／ステレオは WAV から決定（送信側 UI とは独立）。
            var profile = CodecProfileFactory.ForWavReceive(
                ReceivePanel.SelectedWavPath,
                snap.ActiveSubcarriers,
                snap.ModulationScheme,
                snap.BlockInterleaveFactor);
            ReceivePanel.SetWowChannelMode(profile.ChannelMode);
            ReceivePanel.StopDemoFeed();
            ReceivePanel.ErrorGraph.Clear();
            ReceivePanel.FftGraph.Clear();
            ReceivePanel.IqGraph.Clear();
            ReceiveDetailPanel.Clear();
            _receiveDetailOpened = false;

            if (!_inputCoreWorker.TryStartWavDecode(ReceivePanel.SelectedWavPath, profile))
            {
                MessageBox.Show(this, "受信コアが実行中です。", "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _pollingReceive = true;
            if (!_progressPollTimer.IsEnabled)
            {
                _progressPollTimer.Start();
            }

            return;
        }

        MessageBox.Show(
            this,
            $"音声入力（デバイス: {ReceivePanel.AudioDeviceName}）は未実装です。",
            "Onta",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnProgressPollTick(object? sender, EventArgs e)
    {
        if (_pollingReceive)
        {
            // 画面 → コア問い合わせ: 進捗 / エラー率 / FFT / I-Q。
            var status = _inputCoreWorker.QueryExecutionStatus();
            ReceivePanel.ApplyExecutionStatus(status);
            ReceiveDetailPanel.ApplyStatus(status);

            // FH 受信後に受信詳細を前面表示し、サイズ／ブロック数を見せる。
            if (!_receiveDetailOpened && ReceiveDetailPanel.HasFileHeaderInfo(status))
            {
                _receiveDetailOpened = true;
                BottomTabs.SelectedItem = ReceiveDetailTab;
            }

            if (_inputCoreWorker.TryConsumeCompletion(out var success, out var message, out var outputPath))
            {
                _pollingReceive = false;
                if (!_coreWorker.GetProgress().IsRunning)
                {
                    _progressPollTimer.Stop();
                }

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

            return;
        }

        // 送信進捗: 送信詳細タブのメーターを更新。
        var progress = _coreWorker.GetProgress();
        EstimatePanel.ApplyProgress(progress.ElapsedAudioSeconds, progress.IsRunning);
        _ = _coreWorker.ConsumeFrameEvents();

        if (!_coreWorker.TryConsumeCompletion(out var completion))
        {
            return;
        }

        _progressPollTimer.Stop();
        SendPanel.SetTransmissionRunning(false);
        EstimatePanel.ApplyProgress(
            completion.IsSuccess ? Math.Max(progress.TotalAudioSeconds, progress.ElapsedAudioSeconds) : progress.ElapsedAudioSeconds,
            isRunning: false);

        if (completion.WasCancelled)
        {
            MessageBox.Show(this, completion.Message, "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (completion.IsSuccess)
        {
            if (completion.Settings.PlayAudio && !completion.PlayedRealtime
                && !string.IsNullOrWhiteSpace(completion.OutputWavPath)
                && File.Exists(completion.OutputWavPath))
            {
                StartAudioPlayback(completion.OutputWavPath, completion.Settings.AudioDeviceNumber);
            }

            var wavLine = completion.Settings.WriteWav && !string.IsNullOrWhiteSpace(completion.OutputWavPath)
                ? $"WAV: {completion.OutputWavPath}\n"
                : "WAV: （未出力）\n";
            MessageBox.Show(
                this,
                "出力が完了しました。\n\n"
                + $"入力: {completion.Settings.InputFilePath}\n"
                + wavLine
                + $"チャンネル: {completion.Settings.ChannelMode}\n"
                + $"サブキャリア: {completion.Settings.ActiveSubcarriers}\n"
                + $"変調: {completion.Settings.ModulationScheme}\n"
                + $"繰り返し: ×{completion.Settings.BlockInterleaveFactor}\n"
                + $"デバイス: {completion.Settings.AudioDeviceName}\n"
                + (completion.PlayedRealtime ? "音声: リアルタイム出力\n" : string.Empty),
                "Onta",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(this, $"出力に失敗しました。\n{completion.Message}", "Onta", MessageBoxButton.OK, MessageBoxImage.Error);
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
