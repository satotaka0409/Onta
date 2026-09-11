using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudioWaveIn = NAudio.Wave.WaveIn;
using Onta.Core;

namespace Onta.View.Core;

/// <summary>
/// 受信入力設定と受信状態可視化を担うパネルです。
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

    /// <summary>
    /// 受信パネルを初期化します。
    /// </summary>
    public ReceivePanel()
    {
        InitializeComponent();
        ErrorChart.DataContext = _errorChart;
        FftChart.DataContext = _fftChart;
        IqChart.DataContext = _iqChart;
        InitializeAudioDevices();
        UpdateInputModePanels();
        OutputDirBox.Text = _outputDir;

        // デモ表示用タイマー（実受信がないときの可視化確認向け）。
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

    /// <summary>WAV入力モードが選択中なら true。</summary>
    public bool UseWavInput => WavInputRadio.IsChecked == true;

    public string? SelectedWavPath => _selectedWavPath;

    /// <summary>出力フォルダー（未設定時は既定値）を返します。</summary>
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

    /// <summary>
    /// 受信ファイル情報を表示へ反映します。
    /// </summary>
    /// <param name="fileName">ファイル名。</param>
    /// <param name="fileSizeText">表示用サイズ。</param>
    /// <param name="blockCountText">表示用ブロック数。</param>
    public void SetFileInfo(string fileName, string fileSizeText, string blockCountText)
    {
        FileNameBox.Text = fileName;
        FileSizeBox.Text = fileSizeText;
        BlockCountBox.Text = blockCountText;
    }

    /// <summary>進捗テキストを更新します。</summary>
    /// <param name="text">表示文字列。</param>
    public void SetProgressText(string text)
    {
        ProgressBox.Text = text;
    }

    /// <summary>
    /// Core 実行状態を受信UIへ反映します。
    /// </summary>
    /// <param name="status">Core 実行状態。</param>
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
    /// IQ表示を初期状態へ戻します。
    /// </summary>
    public void ClearIqDisplay()
    {
        _iqChart.Clear();
        IqTitle.Text = "I-Q";
    }

    /// <summary>
    /// パイロット推定値から WOW/Flutter 表示を更新します。
    /// </summary>
    /// <param name="leftSpeedRatio">左チャネル速度比。</param>
    /// <param name="rightSpeedRatio">右チャネル速度比。</param>
    public void SetWowFlutterFromPilots(double leftSpeedRatio, double rightSpeedRatio)
    {
        WowLeft.SetFromSpeedRatio(leftSpeedRatio);
        WowRight.SetFromSpeedRatio(rightSpeedRatio);
    }

    /// <summary>
    /// WOW/Flutter 表示のチャネルモードを設定します。
    /// </summary>
    /// <param name="channelMode">チャネルモード。</param>
    public void SetWowChannelMode(ChannelMode channelMode)
    {
        SetWowStereoEnabled(channelMode == ChannelMode.Stereo);
    }

    /// <summary>WOW/Flutter 表示の右チャネル活性状態を切り替えます。</summary>
    /// <param name="stereo">ステレオ表示なら true。</param>
    public void SetWowStereoEnabled(bool stereo)
    {
        WowRight.IsActive = stereo;
        if (!stereo)
        {
            WowRight.Clear();
        }
    }

    /// <summary>
    /// WOW/Flutter パーセント値をグラフへ追加します。
    /// </summary>
    /// <param name="leftPercent">左チャネル値。</param>
    /// <param name="rightPercent">右チャネル値。</param>
    public void SetWowFlutterPercent(double leftPercent, double rightPercent)
    {
        WowLeft.AddSample(leftPercent);
        WowRight.AddSample(rightPercent);
    }

    /// <summary>
    /// エラー率サンプルをグラフへ追加します。
    /// </summary>
    /// <param name="errorRatePercent">エラー率。</param>
    /// <param name="frameKind">対象フレーム種別。</param>
    public void AddErrorRateSample(double errorRatePercent, ErrorRateFrameKind frameKind)
    {
        _errorChart.AddSample(errorRatePercent, frameKind);
    }

    /// <summary>
    /// デモ表示の更新を開始します。
    /// </summary>
    public void StartDemoFeed()
    {
        _demoRunning = true;
        _demoBias = 1.5;
        _demoTimeSec = 0.0;
        _demoTimer.Start();
    }

    /// <summary>
    /// デモ表示の更新を停止します。
    /// </summary>
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
            // WAVモードに切替時は受信対象表示を初期化する。
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
            // デバイス列挙失敗時は既定デバイスのみで動作継続する。
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
            Title = "Select receive WAV file",
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
            // サイズ取得失敗時は既定値 "-" を表示する。
        }

        // ファイル選択後はヘッダー情報待ち状態として表示する。
        SetFileInfo(displayName, sizeText, "-");
        ProgressBox.Text = "待機中";
    }

    private void OnBrowseOutputDir(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select output folder for received file",
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




