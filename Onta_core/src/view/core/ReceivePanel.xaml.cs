using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NAudioWaveIn = NAudio.Wave.WaveIn;
using Onta.Core;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace Onta.View.Core;

/// <summary>
/// 受信入力設定と受信状態可視化を担うパネルです。
/// </summary>
public partial class ReceivePanel : UserControl
{
    private const int DefaultAudioDeviceNumber = -1;
    /// <summary>ワウフラッターメーターの表示感度（実偏差%に対する倍率）。</summary>
    private const double WowFlutterDisplayGain = 2.0;
    private static readonly SolidColorBrush ErrorLegendViterbiBrush = new(Color.FromRgb(100, 170, 255));
    private static readonly SolidColorBrush ErrorLegendOuterBrush = new(Color.FromRgb(255, 150, 70));
    private static readonly SolidColorBrush ErrorLegendLeftBrush = new(Color.FromRgb(166, 221, 176));
    private static readonly SolidColorBrush ErrorLegendRightBrush = new(Color.FromRgb(255, 182, 120));

    private readonly ErrorRateChartModel _errorChart = new();
    private readonly FftChartModel _fftChart = new();
    private readonly IqChartModel _iqChart = new();
    private readonly WowFlutterChartModel _wowChart = new();
    private string? _selectedWavPath;
    private string _outputDir = AppPaths.OutputDir;
    private CoreFrameKind _lastErrorFrame = CoreFrameKind.Fh;
    private CoreEccDecoderKind _lastErrorDecoder = CoreEccDecoderKind.Viterbi;
    private double _lastErrorPercent = -1;
    private int _lastErrorSequence = -1;
    private bool _errorChartStereoMode;

    // ワウメーター用: コアのモデルを壁時計で補間して左右に揺らす
    private bool _wowTrackingActive;
    private double _wowAmount;
    private double _wowPhase;
    private double _flutterPhase;
    private int _wowSampleRate = 44100;
    private long _wowSampleIndexAtSync;
    private long _wowSyncTimestamp;

    public readonly record struct ReceiveSettingsSnapshot(
        bool UseWavInput,
        string WavInputPath,
        string OutputDirectory,
        int AudioDeviceNumber,
        double AudioVolume);

    /// <summary>
    /// 受信パネルを初期化します。
    /// </summary>
    public ReceivePanel()
    {
        InitializeComponent();
        BindChart(ErrorChart, _errorChart.Series, _errorChart.XAxes, _errorChart.YAxes);
        BindChart(FftChart, _fftChart.Series, _fftChart.XAxes, _fftChart.YAxes);
        BindChart(IqChart, _iqChart.Series, _iqChart.XAxes, _iqChart.YAxes);
        BindChart(WowFlutterChart, _wowChart.Series, _wowChart.XAxes, _wowChart.YAxes);
        BuildIqGroupLegend();
        ErrorRateTabRadio.Checked += OnReceiveGraphTabChanged;
        FftTabRadio.Checked += OnReceiveGraphTabChanged;
        InitializeAudioDevices();
        UpdateInputModePanels();
        OutputDirBox.Text = _outputDir;
        SetErrorChartStereoMode(false);
        UpdateReceiveGraphTabVisibility();

        Loaded += (_, _) =>
        {
            UpdateWowMeters(0, 0);
            ErrorGraph.Clear();
            _fftChart.Clear();
            _iqChart.Clear();
            _wowChart.Clear();
            SetFileInfo("(未受信)", "-", "-");
            ProgressBox.Text = "-";
            SetErrorChartStereoMode(false);
            UpdateInputModePanels();
            UpdateReceiveGraphTabVisibility();
            UpdateIqSquareSize();
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
        if (ErrorRateLegend is not null)
        {
            ErrorRateLegend.Visibility = showFft ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>
    /// I-Q グラフ横のグループ凡例（色ドット + A〜F）を構築します。
    /// </summary>
    private void BuildIqGroupLegend()
    {
        IqGroupLegend.Children.Clear();
        foreach (var item in IqChartModel.GroupLegendItems)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 2)
            };
            row.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(Color.FromRgb(item.R, item.G, item.B)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            row.Children.Add(new TextBlock
            {
                Text = item.Label,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11
            });
            IqGroupLegend.Children.Add(row);
        }
    }

    /// <summary>
    /// グラフ行のリサイズに合わせて I-Q を正方形に保ちます。
    /// </summary>
    private void OnReceiveGraphsRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateIqSquareSize();
    }

    /// <summary>
    /// I-Q チャート領域を正方形にし、凡例分だけホスト幅を広げます。
    /// </summary>
    private void UpdateIqSquareSize()
    {
        if (ReceiveGraphsRow is null || IqPanelHost is null || IqTitle is null)
        {
            return;
        }

        if (ReceiveGraphsRow.ActualHeight <= 0)
        {
            return;
        }

        // 行の高さ一杯の正方形。左グラフが極端に狭くならないよう上限。
        var side = Math.Floor(ReceiveGraphsRow.ActualHeight);
        if (ReceiveGraphsRow.ActualWidth > 0)
        {
            side = Math.Min(side, Math.Floor(ReceiveGraphsRow.ActualWidth * 0.38));
        }

        side = Math.Clamp(side, 180, 420);
        IqGroupLegend.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var legendWidth = Math.Ceiling(IqGroupLegend.DesiredSize.Width) + 8;
        var hostWidth = side + legendWidth;
        if (Math.Abs(IqPanelHost.Width - hostWidth) <= 0.5
            && Math.Abs(IqPanelHost.Height - side) <= 0.5)
        {
            return;
        }

        IqPanelHost.Width = hostWidth;
        IqPanelHost.Height = side;
        ReceiveGraphsRow.ColumnDefinitions[1].Width = new GridLength(hostWidth);
    }

    public event EventHandler? ReceiveStartRequested;
    public event EventHandler? ReceiveStopRequested;

    private bool _receiveRunning;

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

    /// <summary>
    /// 音声入力の音量（0〜1）を返します。
    /// </summary>
    public double AudioVolume
    {
        get
        {
            if (AudioVolumeSlider is null)
            {
                return 0.8;
            }

            return Math.Clamp(AudioVolumeSlider.Value / 100.0, 0.0, 1.0);
        }
    }

    /// <summary>
    /// 現在の受信設定を取得します。
    /// </summary>
    public ReceiveSettingsSnapshot CaptureSettings()
    {
        return new ReceiveSettingsSnapshot(
            UseWavInput: UseWavInput,
            WavInputPath: _selectedWavPath ?? string.Empty,
            OutputDirectory: SelectedOutputDir,
            AudioDeviceNumber: AudioDeviceNumber,
            AudioVolume: AudioVolume);
    }

    /// <summary>
    /// 保存済み受信設定を UI へ反映します。
    /// </summary>
    public void ApplySettings(ReceiveSettingsSnapshot snapshot)
    {
        if (snapshot.UseWavInput)
        {
            WavInputRadio.IsChecked = true;
        }
        else
        {
            AudioInputRadio.IsChecked = true;
        }

        _selectedWavPath = string.IsNullOrWhiteSpace(snapshot.WavInputPath)
            ? null
            : snapshot.WavInputPath;
        WavPathBox.Text = _selectedWavPath ?? string.Empty;

        _outputDir = string.IsNullOrWhiteSpace(snapshot.OutputDirectory)
            ? AppPaths.OutputDir
            : Path.GetFullPath(snapshot.OutputDirectory);
        OutputDirBox.Text = _outputDir;

        SelectAudioDevice(snapshot.AudioDeviceNumber);
        var volume = Math.Clamp(snapshot.AudioVolume * 100.0, 0.0, 100.0);
        AudioVolumeSlider.Value = volume;
        AudioVolumeValueText.Text = $"{(int)Math.Round(volume)}%";

        UpdateInputModePanels();
        if (UseWavInput)
        {
            if (!string.IsNullOrWhiteSpace(_selectedWavPath))
            {
                SetFileInfo(Path.GetFileName(_selectedWavPath), "-", "-");
            }
            else
            {
                SetFileInfo("(未受信)", "-", "-");
            }
        }
        else
        {
            SetFileInfo($"(音声入力: {AudioDeviceName})", "-", "-");
        }
    }

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
            SetErrorChartStereoMode(status.FftGraph.IsStereo);
        }

        ApplyWowFlutterFromStatus(status);
        // FH 解析中／ワウ未ロック時は時系列を進めない（エラー率と同じ方針）
        if (status.WowTrackingActive && !status.IsAnalyzing)
        {
            try
            {
                _wowChart.Tick();
            }
            catch
            {
            }
        }

        // I-Q / FFT を先に更新する（エラーレート側の LiveCharts 更新で例外・遅延しても可視化を落とさない）
        _iqChart.ReplacePoints(status.IqGraph.Points, status.IqGraph.ModulationScheme);
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

        // 解析中は止め、積まれた訂正率サンプルを系列ごとに追加（ビタビ / RS・ターボ）
        if (!status.IsAnalyzing && status.ErrorRateSamples.Count > 0)
        {
            foreach (var sample in status.ErrorRateSamples)
            {
                _lastErrorPercent = sample.LatestPercent;
                _lastErrorFrame = sample.FrameKind;
                _lastErrorDecoder = sample.DecoderKind;
                _lastErrorSequence = sample.Sequence;
                try
                {
                    _errorChart.AddSample(sample.LatestPercent, sample.DecoderKind, sample.RightPercent);
                }
                catch
                {
                    // エラーレート描画失敗で受信可視化全体を止めない
                }
            }
        }
        else if (!status.IsAnalyzing
                 && status.ErrorRate.Sequence != _lastErrorSequence
                 && status.ErrorRate.Sequence > 0)
        {
            // 互換: サンプル列が空でも Latest があれば反映
            var err = status.ErrorRate;
            _lastErrorPercent = err.LatestPercent;
            _lastErrorFrame = err.FrameKind;
            _lastErrorDecoder = err.DecoderKind;
            _lastErrorSequence = err.Sequence;
            try
            {
                _errorChart.AddSample(err.LatestPercent, err.DecoderKind, err.RightPercent);
            }
            catch
            {
            }
        }
        else
        {
            try
            {
                _errorChart.Tick();
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 受信グラフ／メーター／ファイル表示を初期状態へ戻します。
    /// </summary>
    public void ResetVisualization()
    {
        _errorChart.Clear();
        _fftChart.Clear();
        _iqChart.Clear();
        _wowChart.Clear();
        _lastErrorPercent = -1;
        _lastErrorFrame = CoreFrameKind.Fh;
        _lastErrorDecoder = CoreEccDecoderKind.Viterbi;
        _lastErrorSequence = -1;
        _errorChartStereoMode = false;
        ClearWowTracking();
        UpdateWowMeters(0, 0);
        SetFileInfo("(未受信)", "-", "-");
        ProgressBox.Text = "-";
        IqTitle.Text = "I-Q";
    }

    /// <summary>
    /// 新しい受信開始時にグラフ／誤差ゲートを初期化します。
    /// </summary>
    public void PrepareForNewReceive()
    {
        ResetVisualization();
        ProgressBox.Text = "開始中…";
    }

    /// <summary>
    /// 送信中の FFT を受信パネルのグラフへ反映します。
    /// </summary>
    public void ApplySendFft(CoreFftGraphInfo fft)
    {
        if (fft.FftSize <= 0)
        {
            return;
        }

        SetErrorChartStereoMode(fft.IsStereo);
        _fftChart.ReplacePoints(fft.LeftPoints, fft.RightPoints, fft.IsStereo);
    }

    /// <summary>
    /// FFT タブを前面にします。
    /// </summary>
    public void ShowFftTab()
    {
        FftTabRadio.IsChecked = true;
        UpdateReceiveGraphTabVisibility();
    }

    /// <summary>
    /// 受信操作（入出力設定・スタート）の有効/無効を切り替えます。
    /// </summary>
    /// <param name="enabled">有効なら true。</param>
    public void SetInteractionEnabled(bool enabled)
    {
        if (_receiveRunning)
        {
            return;
        }

        IoGroupBox.IsEnabled = enabled;
        StartButton.IsEnabled = enabled;
        StopButton.IsEnabled = false;
    }

    /// <summary>
    /// 受信実行中状態に合わせて入力 UI の有効/無効を切り替えます。
    /// </summary>
    /// <param name="isRunning">受信中なら true。</param>
    public void SetReceiveRunning(bool isRunning)
    {
        _receiveRunning = isRunning;
        // 受信中は入出力グループを操作不可にする
        IoGroupBox.IsEnabled = !isRunning;
        StartButton.IsEnabled = !isRunning;
        StopButton.IsEnabled = isRunning;
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
        ClearWowTracking();
        SetWowFlutterPercent((leftSpeedRatio - 1.0) * 100.0, (rightSpeedRatio - 1.0) * 100.0);
    }

    /// <summary>
    /// WOW/Flutter 表示のチャネルモードを設定します。
    /// </summary>
    /// <param name="channelMode">チャネルモード。</param>
    public void SetWowChannelMode(ChannelMode channelMode)
    {
        var stereo = channelMode == ChannelMode.Stereo;
        SetWowStereoEnabled(stereo);
        SetErrorChartStereoMode(stereo);
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

    private void SetErrorChartStereoMode(bool stereo)
    {
        if (_errorChartStereoMode == stereo)
        {
            return;
        }

        _errorChartStereoMode = stereo;
        _errorChart.SetStereoChannelMode(stereo);
        UpdateErrorRateLegend(stereo);
    }

    private void UpdateErrorRateLegend(bool stereo)
    {
        if (ErrorLegendAColor is null
            || ErrorLegendAText is null
            || ErrorLegendBColor is null
            || ErrorLegendBText is null)
        {
            return;
        }

        if (stereo)
        {
            ErrorLegendAColor.Background = ErrorLegendLeftBrush;
            ErrorLegendAText.Text = "L";
            ErrorLegendBColor.Background = ErrorLegendRightBrush;
            ErrorLegendBText.Text = "R";
        }
        else
        {
            ErrorLegendAColor.Background = ErrorLegendViterbiBrush;
            ErrorLegendAText.Text = "ビタビ";
            ErrorLegendBColor.Background = ErrorLegendOuterBrush;
            ErrorLegendBText.Text = "RS／ターボ";
        }
    }

    /// <summary>
    /// コア状態のワウモデルから瞬間速度偏差を評価してメーターへ反映します。
    /// </summary>
    private void ApplyWowFlutterFromStatus(CoreExecutionStatus status)
    {
        if (!status.WowTrackingActive || status.IsAnalyzing)
        {
            ClearWowTracking();
            // メーターのみ更新。FH 解析中はグラフへは載せない。
            UpdateWowMeters(status.WowLeftPercent, status.WowRightPercent);
            return;
        }

        _wowAmount = status.WowAmount;
        _wowPhase = status.WowPhase;
        _flutterPhase = status.FlutterPhase;
        _wowSampleRate = Math.Max(1, status.WowSampleRate);

        if (!_wowTrackingActive)
        {
            // 初回ロック時だけサンプル位置を合わせ、以後は壁時計で連続再生する
            _wowTrackingActive = true;
            _wowSampleIndexAtSync = Math.Max(0L, status.WowSampleIndex);
            _wowSyncTimestamp = Stopwatch.GetTimestamp();
        }

        var dt = (Stopwatch.GetTimestamp() - _wowSyncTimestamp) / (double)Stopwatch.Frequency;
        var sampleIndex = _wowSampleIndexAtSync + (long)(dt * _wowSampleRate);
        var percent = WowFlutterWarp.EvaluateSpeedDeviationPercent(
            _wowSampleRate, _wowAmount, _wowPhase, _flutterPhase, sampleIndex);
        SetWowFlutterPercent(percent, percent);
    }

    private void ClearWowTracking()
    {
        _wowTrackingActive = false;
        _wowAmount = 0;
        _wowPhase = 0;
        _flutterPhase = 0;
        _wowSampleIndexAtSync = 0;
        _wowSyncTimestamp = 0;
    }

    /// <summary>
    /// ワウメーターのみを更新します（時系列グラフは更新しない）。
    /// </summary>
    private void UpdateWowMeters(double leftPercent, double rightPercent)
    {
        WowLeft.AddSample(leftPercent * WowFlutterDisplayGain);
        WowRight.AddSample(rightPercent * WowFlutterDisplayGain);
    }

    /// <summary>
    /// WOW/Flutter パーセント値をメーターと時系列グラフへ反映します。
    /// </summary>
    /// <param name="leftPercent">左チャネル値。</param>
    /// <param name="rightPercent">右チャネル値。</param>
    public void SetWowFlutterPercent(double leftPercent, double rightPercent)
    {
        UpdateWowMeters(leftPercent, rightPercent);
        try
        {
            // グラフは実偏差%（±1%軸）。メーター用の表示ゲインは掛けない。
            _wowChart.AddSample(leftPercent, rightPercent);
        }
        catch
        {
        }
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

    private void SelectAudioDevice(int deviceNumber)
    {
        for (var i = 0; i < AudioDeviceComboBox.Items.Count; i++)
        {
            if (AudioDeviceComboBox.Items[i] is AudioDeviceItem item && item.DeviceNumber == deviceNumber)
            {
                AudioDeviceComboBox.SelectedIndex = i;
                return;
            }
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

    private void OnAudioVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AudioVolumeValueText is not null)
        {
            AudioVolumeValueText.Text = $"{(int)Math.Round(e.NewValue)}%";
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

    private void OnReceiveStopClick(object sender, RoutedEventArgs e)
    {
        ReceiveStopRequested?.Invoke(this, EventArgs.Empty);
    }

    private sealed record AudioDeviceItem(int DeviceNumber, string Name);
}




