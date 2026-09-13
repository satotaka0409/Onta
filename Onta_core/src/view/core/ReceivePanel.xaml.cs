using System.Windows;
using System.Windows.Controls;
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
    private string? _selectedWavPath;
    private string _outputDir = AppPaths.OutputDir;
    private CoreFrameKind _lastErrorFrame = CoreFrameKind.Fh;
    private CoreEccDecoderKind _lastErrorDecoder = CoreEccDecoderKind.Viterbi;
    private double _lastErrorPercent = -1;
    private int _lastErrorSequence = -1;

    /// <summary>
    /// 受信パネルを初期化します。
    /// </summary>
    public ReceivePanel()
    {
        InitializeComponent();
        BindChart(ErrorChart, _errorChart.Series, _errorChart.XAxes, _errorChart.YAxes);
        BindChart(FftChart, _fftChart.Series, _fftChart.XAxes, _fftChart.YAxes);
        BindChart(IqChart, _iqChart.Series, _iqChart.XAxes, _iqChart.YAxes);
        ErrorRateTabRadio.Checked += OnReceiveGraphTabChanged;
        FftTabRadio.Checked += OnReceiveGraphTabChanged;
        InitializeAudioDevices();
        UpdateInputModePanels();
        OutputDirBox.Text = _outputDir;
        UpdateReceiveGraphTabVisibility();

        Loaded += (_, _) =>
        {
            SetWowFlutterPercent(0, 0);
            ErrorGraph.Clear();
            _fftChart.Clear();
            _iqChart.Clear();
            SetFileInfo("(未受信)", "-", "-");
            ProgressBox.Text = "-";
            UpdateInputModePanels();
            UpdateReceiveGraphTabVisibility();
        };
    }

    private static void BindChart(
        LiveChartsCore.SkiaSharpView.WPF.CartesianChart chart,
        LiveChartsCore.ISeries[] series,
        LiveChartsCore.SkiaSharpView.Axis[] xAxes,
        LiveChartsCore.SkiaSharpView.Axis[] yAxes)
    {
        chart.Series = series;
        chart.XAxes = xAxes;
        chart.YAxes = yAxes;
    }

    private void OnReceiveGraphTabChanged(object sender, RoutedEventArgs e)
    {
        UpdateReceiveGraphTabVisibility();
    }

    private void UpdateReceiveGraphTabVisibility()
    {
        // InitializeComponent 中に Checked が発火するため、未生成要素を参照しない
        if (ErrorChartHost is null || FftChartHost is null || FftTabRadio is null)
        {
            return;
        }

        var showFft = FftTabRadio.IsChecked == true;
        // Collapsed だとサイズ0になり LiveCharts が壊れるので、常にレイアウトしつつ Opacity で切替
        ErrorChartHost.Opacity = showFft ? 0 : 1;
        ErrorChartHost.IsHitTestVisible = !showFft;
        FftChartHost.Opacity = showFft ? 1 : 0;
        FftChartHost.IsHitTestVisible = showFft;
        FftChartHost.Visibility = Visibility.Visible;
        ErrorChartHost.Visibility = Visibility.Visible;
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

        // I-Q / FFT を先に更新する（エラーレート側の LiveCharts 更新で例外・遅延しても可視化を落とさない）
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

        var err = status.ErrorRate.LatestPercent;
        var errFrame = status.ErrorRate.FrameKind;
        var errDecoder = status.ErrorRate.DecoderKind;
        var errSeq = status.ErrorRate.Sequence;
        // 解析中は止め、訂正率サンプルが届いたときだけ追加
        if (!status.IsAnalyzing && errSeq != _lastErrorSequence && errSeq > 0)
        {
            _lastErrorPercent = err;
            _lastErrorFrame = errFrame;
            _lastErrorDecoder = errDecoder;
            _lastErrorSequence = errSeq;
            try
            {
                _errorChart.AddSample(err, errDecoder);
            }
            catch
            {
                // エラーレート描画失敗で受信可視化全体を止めない
            }
        }
    }

    /// <summary>
    /// 新しい受信開始時にグラフ／誤差ゲートを初期化します。
    /// </summary>
    public void PrepareForNewReceive()
    {
        _errorChart.Clear();
        _fftChart.Clear();
        _iqChart.Clear();
        _lastErrorPercent = -1;
        _lastErrorFrame = CoreFrameKind.Fh;
        _lastErrorDecoder = CoreEccDecoderKind.Viterbi;
        _lastErrorSequence = -1;
        SetWowFlutterPercent(0, 0);
        IqTitle.Text = "I-Q";
        FftTitle.Text = "FFT";
        ProgressBox.Text = "開始中…";
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

    private sealed record AudioDeviceItem(int DeviceNumber, string Name);
}




