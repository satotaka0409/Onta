using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudioWaveIn = NAudio.Wave.WaveIn;
using Onta.Core;

namespace Onta.View;

/// <summary>
/// 受信パネルです（ファイル情報・L/R ワウフラッター・エラー率 / I-Q グラフ）。
/// </summary>
public partial class ReceivePanel : UserControl
{
    private const int DefaultAudioDeviceNumber = -1;

    private readonly ErrorRateChartModel _errorChart = new();
    private readonly FftChartModel _fftChart = new();
    private readonly IqChartModel _iqChart = new();
    private readonly DispatcherTimer _demoTimer;
    private readonly Random _rng = new();
    private bool _demoRunning;
    private double _demoBias;
    private double _demoTimeSec;
    private string? _selectedWavPath;
    private string _outputDir = AppPaths.OutputDir;
    private CoreFrameKind _lastErrorFrame = CoreFrameKind.Fh;
    private double _lastErrorPercent = -1;

    public ReceivePanel()
    {
        InitializeComponent();
        ErrorChart.DataContext = _errorChart;
        FftChart.DataContext = _fftChart;
        IqChart.DataContext = _iqChart;
        InitializeAudioDevices();
        UpdateInputModePanels();
        OutputDirBox.Text = _outputDir;

        // ワウフラッター／デモは 0.2 秒間隔のスナップショット。
        _demoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _demoTimer.Tick += OnDemoTick;

        Loaded += (_, _) =>
        {
            StopDemoFeed();
            SetWowFlutterPercent(0, 0);
            ErrorGraph.Clear();
            _fftChart.Clear();
            _iqChart.Clear();
            SetFileInfo("(未受信)", "-", "-");
            ProgressBox.Text = "-";
            UpdateInputModePanels();
        };
        Unloaded += (_, _) => StopDemoFeed();
    }

    public event EventHandler? ReceiveStartRequested;

    /// <summary>WAV入力モードか（音声入力のときは false）。</summary>
    public bool UseWavInput => WavInputRadio.IsChecked == true;

    public string? SelectedWavPath => _selectedWavPath;

    /// <summary>受信ファイルを書き出すフォルダー。</summary>
    public string SelectedOutputDir =>
        string.IsNullOrWhiteSpace(_outputDir) ? AppPaths.OutputDir : _outputDir;

    public int AudioDeviceNumber =>
        AudioDeviceComboBox.SelectedItem is AudioDeviceItem item
            ? item.DeviceNumber
            : DefaultAudioDeviceNumber;

    public string AudioDeviceName =>
        AudioDeviceComboBox.SelectedItem is AudioDeviceItem item
            ? item.Name
            : "既定デバイス";

    public ErrorRateChartModel ErrorGraph => _errorChart;

    public FftChartModel FftGraph => _fftChart;

    public IqChartModel IqGraph => _iqChart;

    public double WowFlutterLeftPercent => WowLeft.ValuePercent;

    public double WowFlutterRightPercent => WowRight.ValuePercent;

    public void SetFileInfo(string fileName, string fileSizeText, string blockCountText)
    {
        FileNameBox.Text = fileName;
        FileSizeBox.Text = fileSizeText;
        BlockCountBox.Text = blockCountText;
    }

    /// <summary>進捗表示テキストを直接更新します（受信開始直後の即時フィードバック用）。</summary>
    public void SetProgressText(string text)
    {
        ProgressBox.Text = text;
    }

    /// <summary>
    /// コア問い合わせ結果（進捗 / エラー率 / I-Q）を画面へ反映します。
    /// ファイル名・サイズ・ブロック数は FH 確定後のみ更新します。
    /// </summary>
    public void ApplyExecutionStatus(CoreExecutionStatus status)
    {
        if (ReceiveDetailPanel.HasFileHeaderInfo(status))
        {
            SetFileInfo(status.FileName, status.FileSizeText, status.BlockCountText);
        }

        var frameLabel = status.Progress.CurrentFrame switch
        {
            CoreFrameKind.Fh => "FH",
            CoreFrameKind.Bh => $"BH#{Math.Max(0, status.Progress.CurrentBlockIndex)}",
            _ => $"BD#{Math.Max(0, status.Progress.CurrentBlockIndex)}"
        };
        ProgressBox.Text =
            $"{frameLabel} {status.Progress.ProgressPercent:0.0}% " +
            $"({status.Progress.AcceptedBlockCount}/{Math.Max(status.Progress.TotalBlockCount, 0)})";

        if (status.IsRunning && status.FftGraph.FftSize > 0)
        {
            SetWowStereoEnabled(status.FftGraph.IsStereo);
        }

        SetWowFlutterPercent(status.WowLeftPercent, status.WowRightPercent);

        var err = status.ErrorRate.LatestPercent;
        var errFrame = status.ErrorRate.FrameKind;
        if (Math.Abs(err - _lastErrorPercent) > 1e-6 || errFrame != _lastErrorFrame)
        {
            _lastErrorPercent = err;
            _lastErrorFrame = errFrame;
            _errorChart.AddSample(err, errFrame switch
            {
                CoreFrameKind.Fh => ErrorRateFrameKind.Fh,
                CoreFrameKind.Bh => ErrorRateFrameKind.Bh,
                _ => ErrorRateFrameKind.Bd
            });
        }

        _iqChart.ReplacePoints(status.IqGraph.Points);
        if (status.IqGraph.ActiveSubcarrierCount > 0)
        {
            IqTitle.Text = $"I-Q ({status.IqGraph.ModulationScheme} / SC={status.IqGraph.ActiveSubcarrierCount})";
        }
        else
        {
            IqTitle.Text = "I-Q";
        }

        _fftChart.ReplacePoints(
            status.FftGraph.LeftPoints,
            status.FftGraph.RightPoints,
            status.FftGraph.IsStereo);
        if (status.FftGraph.FftSize > 0)
        {
            var mode = status.FftGraph.IsStereo ? "Stereo" : "Mono";
            FftTitle.Text = $"FFT ({mode} / N={status.FftGraph.FftSize})";
        }
        else
        {
            FftTitle.Text = "FFT";
        }
    }

    /// <summary>
    /// 送信動作中など、受信変調に基づく表示ができないときは I-Q を空にします。
    /// </summary>
    public void ClearIqDisplay()
    {
        _iqChart.Clear();
        IqTitle.Text = "I-Q";
    }

    public void SetWowFlutterFromPilots(double leftSpeedRatio, double rightSpeedRatio)
    {
        WowLeft.SetFromSpeedRatio(leftSpeedRatio);
        WowRight.SetFromSpeedRatio(rightSpeedRatio);
    }

    /// <summary>
    /// チャンネルモードに応じて R 側ワウフラッターの有効／無効を切り替えます。
    /// モノラル時は R を暗くし停止します。
    /// </summary>
    public void SetWowChannelMode(ChannelMode channelMode)
    {
        SetWowStereoEnabled(channelMode == ChannelMode.Stereo);
    }

    /// <summary>ステレオ時のみ R メーターを動作させます。</summary>
    public void SetWowStereoEnabled(bool stereo)
    {
        WowRight.IsActive = stereo;
        if (!stereo)
        {
            WowRight.Clear();
        }
    }

    public void SetWowFlutterPercent(double leftPercent, double rightPercent)
    {
        WowLeft.AddSample(leftPercent);
        WowRight.AddSample(rightPercent);
    }

    public void AddErrorRateSample(double errorRatePercent, ErrorRateFrameKind frameKind)
    {
        _errorChart.AddSample(errorRatePercent, frameKind);
    }

    public void StartDemoFeed()
    {
        _demoRunning = true;
        _demoBias = 1.5;
        _demoTimeSec = 0.0;
        _demoTimer.Start();
    }

    public void StopDemoFeed()
    {
        _demoRunning = false;
        _demoTimer.Stop();
    }

    private void OnInputModeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: false })
        {
            return;
        }

        UpdateInputModePanels();
        if (UseWavInput)
        {
            // ファイル名・サイズは FH 由来。WAV 選択だけでは更新しない。
            SetFileInfo("(未受信)", "-", "-");
        }
        else
        {
            SetFileInfo($"(音声入力: {AudioDeviceName})", "-", "-");
        }

        ProgressBox.Text = "-";
    }

    private void UpdateInputModePanels()
    {
        if (WavInputRadio is null || WavInputPanel is null || AudioInputPanel is null)
        {
            return;
        }

        var useWav = WavInputRadio.IsChecked == true;
        WavInputPanel.Visibility = useWav ? Visibility.Visible : Visibility.Collapsed;
        AudioInputPanel.Visibility = useWav ? Visibility.Collapsed : Visibility.Visible;
    }

    private void InitializeAudioDevices()
    {
        AudioDeviceComboBox.Items.Clear();
        AudioDeviceComboBox.Items.Add(new AudioDeviceItem(DefaultAudioDeviceNumber, "既定デバイス"));

        try
        {
            for (var i = 0; i < NAudioWaveIn.DeviceCount; i++)
            {
                var caps = NAudioWaveIn.GetCapabilities(i);
                AudioDeviceComboBox.Items.Add(new AudioDeviceItem(i, caps.ProductName));
            }
        }
        catch
        {
            // デバイス列挙失敗時は既定デバイスのみで継続。
        }

        AudioDeviceComboBox.SelectedIndex = 0;
    }

    private void OnAudioDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AudioInputRadio?.IsChecked == true)
        {
            SetFileInfo($"(音声入力: {AudioDeviceName})", "-", "-");
        }
    }

    private void OnBrowseWavInput(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "受信 WAV ファイルを選択",
            Filter = "WAV (*.wav)|*.wav|すべてのファイル (*.*)|*.*",
            InitialDirectory = AppPaths.InputDir
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        _selectedWavPath = dlg.FileName;
        WavPathBox.Text = dlg.FileName;
        var displayName = Path.GetFileName(dlg.FileName);
        var sizeText = "-";
        try
        {
            sizeText = $"{new FileInfo(dlg.FileName).Length:N0} bytes";
        }
        catch
        {
            // サイズ取得に失敗しても選択自体は有効。
        }

        // FH 確定前でも「何を選んだか」を先に見せる。
        SetFileInfo(displayName, sizeText, "-");
        ProgressBox.Text = "スタート待ち";
    }

    private void OnBrowseOutputDir(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "受信ファイルの出力フォルダーを選択",
            InitialDirectory = Directory.Exists(_outputDir) ? _outputDir : AppPaths.OutputDir
        };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            return;
        }

        _outputDir = Path.GetFullPath(dlg.FolderName);
        OutputDirBox.Text = _outputDir;
    }

    private void OnReceiveStartClick(object sender, RoutedEventArgs e)
    {
        ReceiveStartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnDemoTick(object? sender, EventArgs e)
    {
        if (!_demoRunning)
        {
            return;
        }

        _demoTimeSec += _demoTimer.Interval.TotalSeconds;

        _demoBias += (_rng.NextDouble() - 0.5) * 0.12;
        _demoBias = Math.Clamp(_demoBias, 0.2, 4.0);
        var spike = _rng.NextDouble() < 0.03 ? _rng.NextDouble() * 8.0 : 0.0;
        var err = Math.Max(0.0, _demoBias + ((_rng.NextDouble() - 0.5) * 0.6) + spike);
        _errorChart.AddSample(err, ErrorRateFrameKind.Bd);

        var wowL = (1.2 * Math.Sin((2.0 * Math.PI * 0.5 * _demoTimeSec) + 0.3))
                   + (0.35 * Math.Sin((2.0 * Math.PI * 6.0 * _demoTimeSec) + 1.1));
        var wowR = (1.1 * Math.Sin((2.0 * Math.PI * 0.5 * _demoTimeSec) + 2.2))
                   + (0.40 * Math.Sin((2.0 * Math.PI * 6.0 * _demoTimeSec) + 0.4));
        wowL += (_rng.NextDouble() - 0.5) * 0.08;
        wowR += (_rng.NextDouble() - 0.5) * 0.08;
        SetWowFlutterPercent(wowL, wowR);
    }

    private sealed record AudioDeviceItem(int DeviceNumber, string Name);
}
