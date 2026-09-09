using System.IO;
using System.Windows;
using Microsoft.Win32;
using NAudio.Wave;
using Onta.Core;
using System.Windows.Threading;

namespace Onta.View;

/// <summary>
/// メインウィンドウです（送信上・受信下・タブは見積以下。画面仕様.mdc）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly OutputCoreWorker _coreWorker = new();
    private readonly InputCoreWorker _inputCoreWorker = new();
    private readonly DispatcherTimer _progressPollTimer;
    private WaveOutEvent? _activeWaveOut;
    private AudioFileReader? _activeAudioReader;
    private bool _pollingReceive;

    public MainWindow()
    {
        InitializeComponent();
        SendPanel.SettingsChanged += (_, _) => RefreshEstimate();
        SendPanel.OutputRequested += OnOutputRequested;
        ReceivePanel.ReceiveStartRequested += OnReceiveStartRequested;
        _progressPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _progressPollTimer.Tick += OnProgressPollTick;
        Closed += (_, _) => StopAudioPlayback();
        RefreshEstimate();
    }

    private void RefreshEstimate()
    {
        var snap = SendPanel.CreateSnapshot();
        EstimatePanel.UpdateEstimate(snap);

        // 送信ファイル選択後は見積タブを前面に出す。
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
            if (outputWavPath is null)
            {
                return;
            }

            if (!_coreWorker.TryStart(snap, outputWavPath))
            {
                MessageBox.Show(this, "コア処理が実行中です。完了後に再実行してください。", "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ReceivePanel.StopDemoFeed();
            ReceivePanel.SetWowFlutterPercent(0, 0);
            ReceivePanel.SetFileInfo(Path.GetFileName(snap.InputFilePath), "-", "-");
            _pollingReceive = false;
            if (!_progressPollTimer.IsEnabled)
            {
                _progressPollTimer.Start();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"出力に失敗しました。\n{ex.Message}", "Onta", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnReceiveStartRequested(object? sender, EventArgs e)
    {
        if (ReceivePanel.ReceiveSource != "WAV入力" || string.IsNullOrWhiteSpace(ReceivePanel.SelectedWavPath))
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
        var profile = CodecProfileFactory.FromSnapshot(snap);
        ReceivePanel.StopDemoFeed();
        ReceivePanel.ErrorGraph.Clear();
        ReceivePanel.FftGraph.Clear();
        ReceivePanel.IqGraph.Clear();

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
    }

    private void OnProgressPollTick(object? sender, EventArgs e)
    {
        if (_pollingReceive)
        {
            // 画面 → コア問い合わせ（0.5 秒間隔）: 進捗 / エラー率 / FFT / I-Q。
            var status = _inputCoreWorker.QueryExecutionStatus();
            ReceivePanel.ApplyExecutionStatus(status);

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

        var snapshot = _coreWorker.GetProgress();
        // 送信進捗ではファイル情報のみ更新（ワウ・フラッター／エラー率は受信用のため更新しない）。
        _ = _coreWorker.ConsumeFrameEvents();
        ReceivePanel.SetFileInfo(snapshot.InputFileName, snapshot.FileSizeText, snapshot.BlockCountText);

        if (!_coreWorker.TryConsumeCompletion(out var completion))
        {
            return;
        }

        _progressPollTimer.Stop();

        if (completion.IsSuccess)
        {
            if (completion.Settings.PlayAudio)
            {
                StartAudioPlayback(completion.OutputWavPath, completion.Settings.AudioDeviceNumber);
            }

            MessageBox.Show(
                this,
                "出力が完了しました。\n\n"
                + $"入力: {completion.Settings.InputFilePath}\n"
                + $"WAV: {completion.OutputWavPath}\n"
                + $"チャンネル: {completion.Settings.ChannelMode}\n"
                + $"サブキャリア: {completion.Settings.ActiveSubcarriers}\n"
                + $"変調: {completion.Settings.ModulationScheme}\n"
                + $"繰り返し: ×{completion.Settings.BlockInterleaveFactor}\n"
                + $"デバイス: {completion.Settings.AudioDeviceName}",
                "Onta",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(this, $"出力に失敗しました。\n{completion.Message}", "Onta", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private string? ResolveOutputWavPath(SendSettingsSnapshot snap)
    {
        var defaultPath = BuildDefaultOutputPath(snap);

        if (snap.WriteWav)
        {
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

        return defaultPath;
    }

    private static string BuildDefaultOutputPath(SendSettingsSnapshot snap)
    {
        if (!string.IsNullOrWhiteSpace(snap.WavOutputPath))
        {
            return Path.GetFullPath(snap.WavOutputPath);
        }

        var inputName = Path.GetFileNameWithoutExtension(snap.InputFilePath);
        var suffix = snap.WriteWav ? "_out.wav" : "_preview.wav";
        return Path.Combine(AppPaths.OutputDir, $"{inputName}{suffix}");
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
