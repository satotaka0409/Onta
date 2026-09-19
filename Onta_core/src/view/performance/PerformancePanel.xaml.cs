using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.WPF;
using Microsoft.Win32;
using Onta.Core;
using Onta.View.Core;
using NAudioWaveIn = NAudio.Wave.WaveIn;
using NAudioWaveOut = NAudio.Wave.WaveOut;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace Onta.View.Performance;

/// <summary>
/// 性能測定画面（トーン／スイープ／OFDM 送信と FFT・オシロ・I-Q・ワウ受信可視化）です。
/// </summary>
public partial class PerformancePanel : UserControl
{
    private const int DefaultAudioDeviceNumber = -1;
    private const double WowDisplayGain = 2.0;

    private readonly PerformanceTxWorker _txWorker = new();
    private readonly PerformanceRxWorker _rxWorker = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly FftChartModel _fftLeft = new();
    private readonly FftChartModel _fftRight = new();
    private readonly OscilloscopeChartModel _scopeLeft = new();
    private readonly OscilloscopeChartModel _scopeRight = new(new SkiaSharp.SKColor(255, 182, 120));
    private readonly IqChartModel _iqLeft = new();
    private readonly IqChartModel _iqRight = new();
    private readonly WowFlutterChartModel _wowChart = new();
    private readonly double[] _scopeLeftBuf = new double[PerformanceConstants.ScopeCaptureSamples];
    private readonly double[] _scopeRightBuf = new double[PerformanceConstants.ScopeCaptureSamples];
    private DateTime _lastWowSampleUtc = DateTime.MinValue;
    private bool _scopeRangeSyncing;

    /// <summary>
    /// 性能測定パネルを初期化します。
    /// </summary>
    public PerformancePanel()
    {
        InitializeComponent();

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _pollTimer.Tick += OnPollTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WowLeftMeter.ChannelLabel = "L";
        WowRightMeter.ChannelLabel = "R";
        WowLeftMeter.RangePercent = 1.0;
        WowRightMeter.RangePercent = 1.0;

        BindChart(FftLeftChart, _fftLeft.Series, _fftLeft.XAxes, _fftLeft.YAxes);
        BindChart(FftRightChart, _fftRight.Series, _fftRight.XAxes, _fftRight.YAxes);
        ApplyFftChartLayout(FftLeftChart);
        ApplyFftChartLayout(FftRightChart);
        FftLeftChart.SizeChanged += (_, _) =>
        {
            ApplyFftChartLayout(FftLeftChart);
            LayoutFftFreqLabels(FftLeftFreqLabels);
        };
        FftRightChart.SizeChanged += (_, _) =>
        {
            ApplyFftChartLayout(FftRightChart);
            LayoutFftFreqLabels(FftRightFreqLabels);
        };
        FftLeftFreqLabels.SizeChanged += (_, _) => LayoutFftFreqLabels(FftLeftFreqLabels);
        FftRightFreqLabels.SizeChanged += (_, _) => LayoutFftFreqLabels(FftRightFreqLabels);
        BindChart(ScopeLeftChart, _scopeLeft.Series, _scopeLeft.XAxes, _scopeLeft.YAxes);
        BindChart(ScopeRightChart, _scopeRight.Series, _scopeRight.XAxes, _scopeRight.YAxes);
        ApplyScopeChartLayout(ScopeLeftChart);
        ApplyScopeChartLayout(ScopeRightChart);
        BindChart(IqLeftChart, _iqLeft.Series, _iqLeft.XAxes, _iqLeft.YAxes);
        BindChart(IqRightChart, _iqRight.Series, _iqRight.XAxes, _iqRight.YAxes);
        ApplyIqChartLayout(IqLeftChart);
        ApplyIqChartLayout(IqRightChart);
        BuildIqGroupLegend(IqLeftLegend);
        BuildIqGroupLegend(IqRightLegend);
        BindChart(WowFlutterChart, _wowChart.Series, _wowChart.XAxes, _wowChart.YAxes);
        UpdateScopeRangeLabels();
        ApplyScopeAxisRanges();
        UpdateWaveGraphTabUi();
        UpdateIqHostSquares();

        Dispatcher.BeginInvoke(
            () =>
            {
                ApplyFftChartLayout(FftLeftChart);
                ApplyFftChartLayout(FftRightChart);
                LayoutFftFreqLabels(FftLeftFreqLabels);
                LayoutFftFreqLabels(FftRightFreqLabels);
                UpdateIqHostSquares();
            },
            DispatcherPriority.Loaded);

        InitializeOutputDevices();
        InitializeInputDevices();
        EnsureDefaultWavPath();
        UpdateOutputModePanels();
        UpdateRxInputModePanels();
        UpdateSignalModeUi();
        UpdateRxInputVolumeText();
    }

    /// <summary>
    /// FFT / オシロスコープ切替を反映します。
    /// </summary>
    private void OnWaveGraphTabChanged(object sender, RoutedEventArgs e)
    {
        UpdateWaveGraphTabUi();
    }

    /// <summary>
    /// FFT とオシロスコープの表示切替（TabItem は使わず Opacity で重ねる）。
    /// </summary>
    private void UpdateWaveGraphTabUi()
    {
        if (FftTabRadio is null || ScopeTabRadio is null
            || FftLeftHost is null || ScopeLeftHost is null
            || FftRightHost is null || ScopeRightHost is null)
        {
            return;
        }

        var showScope = ScopeTabRadio.IsChecked == true;
        SetHostVisible(FftLeftHost, !showScope);
        SetHostVisible(FftRightHost, !showScope);
        SetHostVisible(ScopeLeftHost, showScope);
        SetHostVisible(ScopeRightHost, showScope);
        var fftChrome = showScope ? Visibility.Collapsed : Visibility.Visible;
        if (FftLeftFreqLabels is not null)
        {
            FftLeftFreqLabels.Visibility = fftChrome;
        }

        if (FftRightFreqLabels is not null)
        {
            FftRightFreqLabels.Visibility = fftChrome;
        }

        if (FftLeftDbLabels is not null)
        {
            FftLeftDbLabels.Visibility = fftChrome;
        }

        if (FftRightDbLabels is not null)
        {
            FftRightDbLabels.Visibility = fftChrome;
        }

        var scopeChrome = showScope ? Visibility.Visible : Visibility.Collapsed;
        if (ScopeLeftAmpHost is not null)
        {
            ScopeLeftAmpHost.Visibility = scopeChrome;
        }

        if (ScopeRightAmpHost is not null)
        {
            ScopeRightAmpHost.Visibility = scopeChrome;
        }

        if (ScopeLeftTimeHost is not null)
        {
            ScopeLeftTimeHost.Visibility = scopeChrome;
        }

        if (ScopeRightTimeHost is not null)
        {
            ScopeRightTimeHost.Visibility = scopeChrome;
        }

        if (ScopeLrSyncCheck is not null)
        {
            ScopeLrSyncCheck.Visibility = scopeChrome;
        }

        if (LeftWaveTitle is not null)
        {
            LeftWaveTitle.Text = showScope ? "L オシロスコープ  AUTO" : "L FFT";
        }

        if (RightWaveTitle is not null)
        {
            RightWaveTitle.Text = showScope ? "R オシロスコープ  AUTO" : "R FFT";
        }

        if (showScope)
        {
            ApplyScopeFromWorkers();
        }
    }

    private static void SetHostVisible(UIElement host, bool visible)
    {
        host.Opacity = visible ? 1 : 0;
        host.IsHitTestVisible = visible;
        host.Visibility = Visibility.Visible;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pollTimer.Stop();
        _txWorker.RequestStop();
        _rxWorker.RequestStop();
    }

    /// <summary>
    /// L/R 波形行の高さに合わせて I-Q を正方形にします。
    /// </summary>
    private void OnWaveRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateIqHostSquares();
    }

    /// <summary>
    /// L/R の I-Q 枠幅を、タイトル下のプロットが高さと同じ正方形になるよう合わせます。
    /// </summary>
    private void UpdateIqHostSquares()
    {
        UpdateIqHostSquare(LeftWaveRow, IqLeftHost, IqLeftTitle, IqLeftLegend, IqLeftChart);
        UpdateIqHostSquare(RightWaveRow, IqRightHost, IqRightTitle, IqRightLegend, IqRightChart);
    }

    /// <summary>
    /// I-Q プロットを正方形に固定し、凡例分だけホスト幅を広げます。
    /// </summary>
    private static void UpdateIqHostSquare(
        Grid row,
        FrameworkElement host,
        FrameworkElement title,
        FrameworkElement legend,
        FrameworkElement chart)
    {
        if (row is null || host is null || title is null || legend is null || chart is null || row.ActualHeight <= 1)
        {
            return;
        }

        var padH = 12.0;
        var padV = 12.0;
        if (host is Border border)
        {
            padH = border.Padding.Left + border.Padding.Right;
            padV = border.Padding.Top + border.Padding.Bottom;
        }

        var titleH = title.ActualHeight;
        if (titleH <= 0)
        {
            titleH = 16;
        }

        titleH += title.Margin.Top + title.Margin.Bottom;
        legend.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var legendWidth = Math.Ceiling(legend.DesiredSize.Width) + legend.Margin.Left + legend.Margin.Right;
        var chartSide = Math.Floor(row.ActualHeight - padV - titleH);
        if (row.ActualWidth > 0)
        {
            chartSide = Math.Min(chartSide, Math.Floor(row.ActualWidth * 0.42) - padH - legendWidth);
        }

        chartSide = Math.Clamp(chartSide, 200, 360);
        if (Math.Abs(chart.Width - chartSide) > 0.5 || Math.Abs(chart.Height - chartSide) > 0.5)
        {
            chart.Width = chartSide;
            chart.Height = chartSide;
        }

        var hostWidth = chartSide + padH + legendWidth;
        if (Math.Abs(host.Width - hostWidth) > 0.5)
        {
            host.Width = hostWidth;
        }
    }

    /// <summary>
    /// I-Q グラフ横のグループ凡例（A〜H。G/H は性能測定のみ）を構築します。
    /// </summary>
    private void BuildIqGroupLegend(Panel host)
    {
        host.Children.Clear();
        foreach (var item in IqChartModel.PerformanceGroupLegendItems)
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
            host.Children.Add(row);
        }
    }

    /// <summary>
    /// I-Q プロットの余白を四辺均等にして、点が正方形に載るようにします。
    /// </summary>
    private static void ApplyIqChartLayout(CartesianChart chart)
    {
        chart.DrawMargin = new LiveChartsCore.Measure.Margin(10, 10, 10, 10);
        chart.ClipToBounds = true;
    }

    /// <summary>
    /// LiveCharts へ系列と軸をバインドします。
    /// </summary>
    private static void BindChart(
        CartesianChart chart,
        ISeries[] series,
        Axis[] xAxes,
        Axis[] yAxes)
    {
        chart.Series = series;
        chart.XAxes = xAxes;
        chart.YAxes = yAxes;
    }

    /// <summary>
    /// FFT チャートの描画余白と横軸範囲を調整します。
    /// LiveCharts は Skia を物理ピクセル幅で描くため、125% DPI では
    /// MaxLimit=20000 だと 8 kHz が 10k に見える。MaxLimit=20000×DPI にして
    /// 0–20k の目盛りがコントロール全幅に乗る。
    /// </summary>
    private void ApplyFftChartLayout(CartesianChart chart)
    {
        chart.DrawMargin = FftChartModel.CreateDrawMargin();
        chart.ClipToBounds = false;

        var model = ReferenceEquals(chart, FftLeftChart) ? _fftLeft : _fftRight;
        var xAxis = model.XAxes[0];
        xAxis.Name = null;
        xAxis.NamePaint = null;
        xAxis.LabelsPaint = null;
        xAxis.TextSize = 0;
        xAxis.NameTextSize = 0;
        xAxis.MinLimit = 0;
        xAxis.MaxLimit = GetPerfFftXMaxHz();
        xAxis.MinStep = 2000;
        xAxis.ForceStepToMin = true;
        xAxis.CustomSeparators = [0, 2000, 4000, 6000, 8000, 10000, 12000, 14000, 16000, 18000, 20000];
        xAxis.SeparatorsAtCenter = false;
        xAxis.TicksAtCenter = false;
        xAxis.Padding = new LiveChartsCore.Drawing.Padding(0, 0, 0, 0);

        var yAxis = model.YAxes[0];
        yAxis.Name = null;
        yAxis.NamePaint = null;
        yAxis.LabelsPaint = null;
        yAxis.TextSize = 0;
        yAxis.NameTextSize = 0;
        yAxis.MinLimit = -100;
        yAxis.MaxLimit = 0;
        yAxis.CustomSeparators = [-100, -80, -60, -40, -20, 0];
        yAxis.SeparatorsAtCenter = false;
        yAxis.TicksAtCenter = false;
        yAxis.Padding = new LiveChartsCore.Drawing.Padding(0, 0, 0, 0);
    }

    /// <summary>
    /// 性能測定 FFT の横軸 MaxLimit（Hz）。物理ピクセル描画の DPI 分だけ広げる。
    /// </summary>
    private double GetPerfFftXMaxHz()
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpi < 0.5)
        {
            dpi = 1.0;
        }

        return 20000.0 * dpi;
    }

    /// <summary>
    /// ReplacePoints が MaxLimit を 20000 に戻すので、DPI 補正を掛け直します。
    /// </summary>
    private void RestorePerfFftXMax()
    {
        var max = GetPerfFftXMaxHz();
        _fftLeft.XAxes[0].MinLimit = 0;
        _fftLeft.XAxes[0].MaxLimit = max;
        _fftRight.XAxes[0].MinLimit = 0;
        _fftRight.XAxes[0].MaxLimit = max;
    }

    /// <summary>
    /// オシロスコープの軸ラベル用余白を設定します。
    /// </summary>
    private static void ApplyScopeChartLayout(CartesianChart chart)
    {
        chart.DrawMargin = OscilloscopeChartModel.CreateDrawMargin();
        chart.ClipToBounds = false;
        // LiveCharts が自動余白をいじって L/R で目盛り位置がずれるのを防ぐ。
        chart.ZoomMode = LiveChartsCore.Measure.ZoomAndPanMode.None;
    }

    /// <summary>
    /// L/R の振幅・時間レンジスライダー変更です。同期中は相手側へ同じ値を載せます。
    /// </summary>
    private void OnScopeRangeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScopeLeftAmpSlider is null || ScopeRightAmpSlider is null
            || ScopeLeftTimeSlider is null || ScopeRightTimeSlider is null)
        {
            return;
        }

        if (!_scopeRangeSyncing && ScopeLrSyncCheck?.IsChecked == true)
        {
            _scopeRangeSyncing = true;
            try
            {
                SyncScopeRangePeer(sender);
            }
            finally
            {
                _scopeRangeSyncing = false;
            }
        }

        UpdateScopeRangeLabels();
        if (ScopeTabRadio?.IsChecked == true)
        {
            ApplyScopeFromWorkers();
        }
        else
        {
            ApplyScopeAxisRanges();
        }
    }

    /// <summary>
    /// L/R 同期チェックの変更です。ON 時は L のレンジを R へコピーします。
    /// </summary>
    private void OnScopeLrSyncChanged(object sender, RoutedEventArgs e)
    {
        if (ScopeLrSyncCheck is null)
        {
            return;
        }

        if (ScopeLrSyncCheck.IsChecked == true)
        {
            CopyScopeRangeLeftToRight();
        }
    }

    /// <summary>
    /// 同期中、動かしたスライダーの軸だけ相手チャネルへ合わせます。
    /// </summary>
    private void SyncScopeRangePeer(object sender)
    {
        if (ReferenceEquals(sender, ScopeLeftAmpSlider))
        {
            ScopeRightAmpSlider.Value = ScopeLeftAmpSlider.Value;
        }
        else if (ReferenceEquals(sender, ScopeLeftTimeSlider))
        {
            ScopeRightTimeSlider.Value = ScopeLeftTimeSlider.Value;
        }
        else if (ReferenceEquals(sender, ScopeRightAmpSlider))
        {
            ScopeLeftAmpSlider.Value = ScopeRightAmpSlider.Value;
        }
        else if (ReferenceEquals(sender, ScopeRightTimeSlider))
        {
            ScopeLeftTimeSlider.Value = ScopeRightTimeSlider.Value;
        }
    }

    /// <summary>
    /// L の振幅・時間レンジを R へコピーします。
    /// </summary>
    private void CopyScopeRangeLeftToRight()
    {
        if (ScopeLeftAmpSlider is null || ScopeRightAmpSlider is null
            || ScopeLeftTimeSlider is null || ScopeRightTimeSlider is null)
        {
            return;
        }

        _scopeRangeSyncing = true;
        try
        {
            ScopeRightAmpSlider.Value = ScopeLeftAmpSlider.Value;
            ScopeRightTimeSlider.Value = ScopeLeftTimeSlider.Value;
        }
        finally
        {
            _scopeRangeSyncing = false;
        }

        UpdateScopeRangeLabels();
        if (ScopeTabRadio?.IsChecked == true)
        {
            ApplyScopeFromWorkers();
        }
        else
        {
            ApplyScopeAxisRanges();
        }
    }

    /// <summary>
    /// スライダー横のレンジ数値を更新します。
    /// </summary>
    private void UpdateScopeRangeLabels()
    {
        if (ScopeLeftAmpValue is not null && ScopeLeftAmpSlider is not null)
        {
            ScopeLeftAmpValue.Text = OscilloscopeChartModel.FormatAmplitudeRange(ReadScopeAmp(ScopeLeftAmpSlider));
        }

        if (ScopeRightAmpValue is not null && ScopeRightAmpSlider is not null)
        {
            ScopeRightAmpValue.Text = OscilloscopeChartModel.FormatAmplitudeRange(ReadScopeAmp(ScopeRightAmpSlider));
        }

        if (ScopeLeftTimeValue is not null && ScopeLeftTimeSlider is not null)
        {
            ScopeLeftTimeValue.Text = OscilloscopeChartModel.FormatTimeSpan(ReadScopeTimeMs(ScopeLeftTimeSlider));
        }

        if (ScopeRightTimeValue is not null && ScopeRightTimeSlider is not null)
        {
            ScopeRightTimeValue.Text = OscilloscopeChartModel.FormatTimeSpan(ReadScopeTimeMs(ScopeRightTimeSlider));
        }
    }

    /// <summary>
    /// スライダー値をチャート軸へ反映します。
    /// </summary>
    private void ApplyScopeAxisRanges()
    {
        if (ScopeLeftAmpSlider is null || ScopeRightAmpSlider is null
            || ScopeLeftTimeSlider is null || ScopeRightTimeSlider is null)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        if (dpi < 0.5)
        {
            dpi = 1.0;
        }

        _scopeLeft.ApplyRanges(ReadScopeAmp(ScopeLeftAmpSlider), ReadScopeTimeMs(ScopeLeftTimeSlider), dpi);
        _scopeRight.ApplyRanges(ReadScopeAmp(ScopeRightAmpSlider), ReadScopeTimeMs(ScopeRightTimeSlider), dpi);
    }

    /// <summary>
    /// 縦軸スライダーから片振幅レンジを読みます。
    /// </summary>
    private static double ReadScopeAmp(Slider slider) =>
        OscilloscopeChartModel.AmplitudeHalfFromIndex((int)Math.Round(slider.Value));

    /// <summary>
    /// 横軸スライダーから表示幅（ms）を読みます。
    /// </summary>
    private static double ReadScopeTimeMs(Slider slider) =>
        OscilloscopeChartModel.TimeSpanMsFromIndex((int)Math.Round(slider.Value));

    /// <summary>
    /// 時間レンジをサンプル数へ変換します。
    /// </summary>
    private static int ScopeDisplaySamples(double timeSpanMs, int sampleRate) =>
        Math.Max(16, (int)Math.Round(timeSpanMs * Math.Max(1, sampleRate) / 1000.0));

    private static readonly (double Hz, string Text)[] FftFreqTicks =
    [
        (0, "0"), (2000, "2k"), (4000, "4k"), (6000, "6k"), (8000, "8k"),
        (10000, "10k"), (12000, "12k"), (14000, "14k"), (16000, "16k"),
        (18000, "18k"), (20000, "20k")
    ];

    /// <summary>
    /// 0–20 kHz を Canvas 全幅に等間隔配置します（MaxLimit の DPI 補正と対）。
    /// </summary>
    private static void LayoutFftFreqLabels(Canvas canvas)
    {
        if (canvas is null)
        {
            return;
        }

        var width = canvas.ActualWidth;
        if (width <= 1)
        {
            return;
        }

        canvas.Children.Clear();
        var brush = (System.Windows.Media.Brush)Application.Current.FindResource("BrushTextMuted");
        const double maxHz = 20000;
        foreach (var (hz, text) in FftFreqTicks)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 9,
                Foreground = brush,
                TextAlignment = TextAlignment.Center
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var x = (width * hz / maxHz) - (label.DesiredSize.Width * 0.5);
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, 0);
            canvas.Children.Add(label);
        }
    }

    private void InitializeOutputDevices()
    {
        OutputDeviceCombo.Items.Clear();
        OutputDeviceCombo.Items.Add(new AudioDeviceItem(DefaultAudioDeviceNumber, "既定デバイス"));
        try
        {
            for (var i = 0; i < NAudioWaveOut.DeviceCount; i++)
            {
                var caps = NAudioWaveOut.GetCapabilities(i);
                OutputDeviceCombo.Items.Add(new AudioDeviceItem(i, caps.ProductName));
            }
        }
        catch
        {
            // 列挙失敗時は既定のみ
        }

        OutputDeviceCombo.SelectedIndex = 0;
    }

    private void InitializeInputDevices()
    {
        InputDeviceCombo.Items.Clear();
        InputDeviceCombo.Items.Add(new AudioDeviceItem(DefaultAudioDeviceNumber, "既定デバイス"));
        try
        {
            for (var i = 0; i < NAudioWaveIn.DeviceCount; i++)
            {
                var caps = NAudioWaveIn.GetCapabilities(i);
                InputDeviceCombo.Items.Add(new AudioDeviceItem(i, caps.ProductName));
            }
        }
        catch
        {
            // 列挙失敗時は既定のみ
        }

        InputDeviceCombo.SelectedIndex = 0;
    }

    /// <summary>
    /// WAV入力 / 音声入力のパネル表示を切り替えます。
    /// </summary>
    private void OnRxInputModeChanged(object sender, RoutedEventArgs e)
    {
        UpdateRxInputModePanels();
    }

    /// <summary>
    /// 受信入力モードに応じて WAV / 音声パネルを切り替えます。
    /// </summary>
    private void UpdateRxInputModePanels()
    {
        if (RxWavInputRadio is null || RxWavInputPanel is null || RxAudioInputPanel is null)
        {
            return;
        }

        var useWav = RxWavInputRadio.IsChecked == true;
        RxWavInputPanel.Visibility = useWav ? Visibility.Visible : Visibility.Collapsed;
        RxAudioInputPanel.Visibility = useWav ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 受信音量スライダーの表示を更新します。
    /// </summary>
    private void OnRxInputVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateRxInputVolumeText();
    }

    /// <summary>
    /// 受信音量ラベルを更新します。
    /// </summary>
    private void UpdateRxInputVolumeText()
    {
        if (RxInputVolumeValueText is null || InputGainSlider is null)
        {
            return;
        }

        RxInputVolumeValueText.Text = $"{(int)Math.Round(InputGainSlider.Value)}%";
    }

    /// <summary>
    /// 受信側 WAV 入力ファイルを選択します。
    /// </summary>
    private void OnBrowseRxWavClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "WAV 入力ファイル",
            Filter = "WAV (*.wav)|*.wav",
            CheckFileExists = true
        };
        if (!string.IsNullOrWhiteSpace(RxWavPathBox.Text))
        {
            try
            {
                dlg.InitialDirectory = Path.GetDirectoryName(RxWavPathBox.Text);
                dlg.FileName = Path.GetFileName(RxWavPathBox.Text);
            }
            catch
            {
                // ignore
            }
        }

        if (dlg.ShowDialog() == true)
        {
            RxWavPathBox.Text = dlg.FileName;
        }
    }

    private void EnsureDefaultWavPath()
    {
        if (!string.IsNullOrWhiteSpace(WavPathTextBox.Text))
        {
            return;
        }

        WavPathTextBox.Text = Path.Combine(
            AppPaths.OutputDir,
            $"perf_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
    }

    /// <summary>
    /// 基準信号 / 変調のラジオ切替を反映します。
    /// </summary>
    private void OnSignalModeChanged(object sender, RoutedEventArgs e)
    {
        UpdateSignalModeUi();
    }

    /// <summary>
    /// 基準信号パネルと変調パネルを切り替えます。
    /// </summary>
    private void UpdateSignalModeUi()
    {
        if (ReferenceSignalRadio is null || ModulatedSignalRadio is null
            || ReferenceSignalHost is null || ModulatedSignalHost is null)
        {
            return;
        }

        var modulated = ModulatedSignalRadio.IsChecked == true;
        SetHostVisible(ReferenceSignalHost, !modulated);
        SetHostVisible(ModulatedSignalHost, modulated);
    }

    private void OnBrowseWavClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "WAV 出力先",
            Filter = "WAV (*.wav)|*.wav",
            InitialDirectory = AppPaths.OutputDir,
            FileName = string.IsNullOrWhiteSpace(WavPathTextBox.Text)
                ? "perf_out.wav"
                : Path.GetFileName(WavPathTextBox.Text)
        };
        if (dlg.ShowDialog() == true)
        {
            WavPathTextBox.Text = dlg.FileName;
        }
    }

    /// <summary>
    /// WAV出力 / 音声出力のパネル表示を切り替えます。
    /// </summary>
    private void OnOutputModeChanged(object sender, RoutedEventArgs e)
    {
        UpdateOutputModePanels();
    }

    /// <summary>
    /// 出力モードに応じて WAV / 音声パネルを切り替えます。
    /// </summary>
    private void UpdateOutputModePanels()
    {
        if (WriteWavRadio is null || WavOutputPanel is null || AudioOutputPanel is null)
        {
            return;
        }

        var writeWav = WriteWavRadio.IsChecked == true;
        WavOutputPanel.Visibility = writeWav ? Visibility.Visible : Visibility.Collapsed;
        AudioOutputPanel.Visibility = writeWav ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnTxStartClick(object sender, RoutedEventArgs e)
    {
        if (_txWorker.IsBusy)
        {
            return;
        }

        EnsureDefaultWavPath();
        var settings = ReadTxSettings();
        if (!_txWorker.TryStart(settings))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "送信を開始できませんでした。",
                "Onta",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SetTxRunning(true);
        TxStatusText.Text = $"送信中… {settings.DurationSeconds:0}s";
        EnsurePollRunning();
    }

    private void OnTxStopClick(object sender, RoutedEventArgs e)
    {
        _txWorker.RequestStop();
        TxStatusText.Text = "停止要求…";
    }

    private void OnRxStartClick(object sender, RoutedEventArgs e)
    {
        if (_rxWorker.IsBusy)
        {
            return;
        }

        var settings = ReadRxSettings();
        if (settings.UseWavInput && string.IsNullOrWhiteSpace(settings.WavPath))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "WAV 入力ファイルを選択してください。",
                "Onta",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!_rxWorker.TryStart(settings))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                settings.UseWavInput
                    ? "受信を開始できませんでした（WAV ファイルを確認してください）。"
                    : "受信を開始できませんでした（入力デバイスを確認してください）。",
                "Onta",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _fftLeft.Clear();
        _fftRight.Clear();
        _scopeLeft.Clear();
        _scopeRight.Clear();
        _iqLeft.Clear();
        _iqRight.Clear();
        _wowChart.Clear();
        WowLeftMeter.Clear();
        WowRightMeter.Clear();
        SetRxRunning(true);
        RxStatusText.Text = settings.UseWavInput ? "WAV 解析中" : "受信中";
        EnsurePollRunning();
    }

    private void OnRxStopClick(object sender, RoutedEventArgs e)
    {
        _rxWorker.RequestStop();
        SetRxRunning(false);
        RxStatusText.Text = "停止";
        MaybeStopPoll();
    }

    private void EnsurePollRunning()
    {
        if (!_pollTimer.IsEnabled)
        {
            _pollTimer.Start();
        }
    }

    private void MaybeStopPoll()
    {
        if (!_txWorker.IsBusy && !_rxWorker.IsBusy)
        {
            _pollTimer.Stop();
        }
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (_rxWorker.IsBusy)
        {
            ApplyRxStatus(_rxWorker.SharedStatus.Read());
        }
        else if (RxStopButton.IsEnabled)
        {
            // WAV 終端やキャプチャ異常終了でワーカーが落ちた場合
            var status = _rxWorker.SharedStatus.Read();
            ApplyRxStatus(status);
            SetRxRunning(false);
            if (status.IsFaulted && !string.IsNullOrWhiteSpace(status.LastError))
            {
                RxStatusText.Text = status.LastError;
            }
            else if (status.IsCompleted)
            {
                RxStatusText.Text = "完了";
            }
            else
            {
                RxStatusText.Text = "停止";
            }

            MaybeStopPoll();
        }
        else if (_txWorker.IsBusy)
        {
            PushLiveTxSignal();
            ApplyTxViz(_txWorker.SharedVizStatus.Read());
        }

        if (_txWorker.TryConsumeCompletion(out var ok, out var message))
        {
            SetTxRunning(false);
            TxStatusText.Text = ok ? "送信完了" : message;
            MaybeStopPoll();
        }

        if (!_txWorker.IsBusy && !_rxWorker.IsBusy)
        {
            _pollTimer.Stop();
        }
    }

    /// <summary>
    /// 受信共有状態をグラフへ反映します。
    /// </summary>
    private void ApplyRxStatus(CoreExecutionStatus status)
    {
        var showScope = ScopeTabRadio?.IsChecked == true;
        if (showScope)
        {
            ApplyScopeFromWorkers();
        }
        else
        {
            _fftLeft.ReplacePoints(status.FftGraph.LeftPoints, Array.Empty<CoreFftSample>(), isStereo: false);
            _fftRight.ReplacePoints(
                status.FftGraph.IsStereo ? status.FftGraph.RightPoints : status.FftGraph.LeftPoints,
                Array.Empty<CoreFftSample>(),
                isStereo: false);
            RestorePerfFftXMax();
        }

        var iq = status.IqGraph.Points;
        if (iq.Count > 0)
        {
            var leftCount = status.IqGraph.LeftPointCount;
            if (leftCount <= 0 || leftCount >= iq.Count)
            {
                _iqLeft.ReplacePoints(iq, status.IqGraph.ModulationScheme);
                _iqRight.ReplacePoints(iq, status.IqGraph.ModulationScheme);
            }
            else
            {
                _iqLeft.ReplacePoints(iq.Take(leftCount).ToList(), status.IqGraph.ModulationScheme);
                _iqRight.ReplacePoints(iq.Skip(leftCount).ToList(), status.IqGraph.ModulationScheme);
            }
        }

        var left = status.WowLeftPercent;
        var right = status.WowRightPercent;
        WowLeftMeter.AddSample(left * WowDisplayGain);
        WowRightMeter.AddSample(right * WowDisplayGain);

        var now = DateTime.UtcNow;
        if ((now - _lastWowSampleUtc).TotalSeconds >= 0.2)
        {
            _wowChart.AddSample(left, right);
            _lastWowSampleUtc = now;
        }

        _wowChart.Tick();

        if (status.IsFaulted && !string.IsNullOrWhiteSpace(status.LastError))
        {
            SetRxRunning(false);
            RxStatusText.Text = status.LastError;
            MaybeStopPoll();
        }
    }

    /// <summary>
    /// 送信中の FFT 可視化を反映します。
    /// </summary>
    private void ApplyTxViz(CoreExecutionStatus status)
    {
        if (status.FftGraph.FftSize <= 0)
        {
            if (ScopeTabRadio?.IsChecked == true)
            {
                ApplyScopeFromWorkers();
            }

            return;
        }

        if (ScopeTabRadio?.IsChecked == true)
        {
            ApplyScopeFromWorkers();
            return;
        }

        _fftLeft.ReplacePoints(status.FftGraph.LeftPoints, Array.Empty<CoreFftSample>(), isStereo: false);
        if (status.FftGraph.IsStereo)
        {
            _fftRight.ReplacePoints(status.FftGraph.RightPoints, Array.Empty<CoreFftSample>(), isStereo: false);
        }
        else
        {
            _fftRight.ReplacePoints(status.FftGraph.LeftPoints, Array.Empty<CoreFftSample>(), isStereo: false);
        }

        RestorePerfFftXMax();
    }

    /// <summary>
    /// 受信（優先）または送信の PCM を AUTO トリガーしてオシロへ描きます。
    /// </summary>
    private void ApplyScopeFromWorkers()
    {
        ApplyScopeAxisRanges();
        var copied = _rxWorker.TryCopyLatestPcm(_scopeLeftBuf, _scopeRightBuf, out var count, out var sampleRate);
        if (!copied)
        {
            copied = _txWorker.TryCopyLatestPcm(_scopeLeftBuf, _scopeRightBuf, out count, out sampleRate);
        }

        if (!copied || count <= 0 || ScopeLeftTimeSlider is null || ScopeRightTimeSlider is null)
        {
            return;
        }

        var leftCap = OscilloscopeTrigger.Find(
            _scopeLeftBuf.AsSpan(0, count),
            sampleRate,
            ScopeDisplaySamples(ReadScopeTimeMs(ScopeLeftTimeSlider), sampleRate));
        var rightCap = OscilloscopeTrigger.Find(
            _scopeRightBuf.AsSpan(0, count),
            sampleRate,
            ScopeDisplaySamples(ReadScopeTimeMs(ScopeRightTimeSlider), sampleRate));
        _scopeLeft.ReplaceWaveform(
            _scopeLeftBuf.AsSpan(leftCap.Start, leftCap.Length),
            sampleRate,
            leftCap.TriggerOffset,
            leftCap.Triggered,
            leftCap.Level);
        _scopeRight.ReplaceWaveform(
            _scopeRightBuf.AsSpan(rightCap.Start, rightCap.Length),
            sampleRate,
            rightCap.TriggerOffset,
            rightCap.Triggered,
            rightCap.Level);
        ApplyScopeChartLayout(ScopeLeftChart);
        ApplyScopeChartLayout(ScopeRightChart);

        if (LeftWaveTitle is not null)
        {
            LeftWaveTitle.Text = leftCap.Triggered ? "L オシロスコープ  AUTO ↑" : "L オシロスコープ  AUTO";
        }

        if (RightWaveTitle is not null)
        {
            RightWaveTitle.Text = rightCap.Triggered ? "R オシロスコープ  AUTO ↑" : "R オシロスコープ  AUTO";
        }
    }

    private void SetTxRunning(bool running)
    {
        TxStartButton.IsEnabled = !running;
        TxStopButton.IsEnabled = running;
        StereoRadio.IsEnabled = !running;
        MonoRadio.IsEnabled = !running;
        ReferenceSignalRadio.IsEnabled = !running;
        ModulatedSignalRadio.IsEnabled = !running;
        WriteWavRadio.IsEnabled = !running;
        PlayAudioRadio.IsEnabled = !running;
        OutputDeviceCombo.IsEnabled = !running;
        OutputVolumeSlider.IsEnabled = !running;
        // 周波数ラジオ・信号レベルは再生中も変更可（PCM へライブ反映）
        if (SignalLevelSlider is not null)
        {
            SignalLevelSlider.IsEnabled = true;
        }

        foreach (var radio in FindRadios(ReferenceSignalHost as DependencyObject ?? this))
        {
            if (string.Equals(radio.GroupName, "PerfTone", StringComparison.Ordinal))
            {
                radio.IsEnabled = true;
            }
        }

        foreach (var radio in FindRadios(ModulatedSignalHost as DependencyObject ?? this))
        {
            if (string.Equals(radio.GroupName, "PerfSubcarrier", StringComparison.Ordinal)
                || string.Equals(radio.GroupName, "PerfModulation", StringComparison.Ordinal))
            {
                radio.IsEnabled = !running;
            }
        }

        BrowseWavButton.IsEnabled = !running;
        WavPathTextBox.IsEnabled = !running;
        foreach (var radio in FindRadios(Sec30Radio.Parent as DependencyObject ?? this))
        {
            if (string.Equals(radio.GroupName, "PerfDuration", StringComparison.Ordinal))
            {
                radio.IsEnabled = !running;
            }
        }

        if (!running)
        {
            UpdateOutputModePanels();
        }
    }

    /// <summary>
    /// 送信中の周波数・レベルをワーカーへ反映します。
    /// </summary>
    private void PushLiveTxSignal()
    {
        if (!_txWorker.IsBusy)
        {
            return;
        }

        var (mode, toneHz) = ReadSignalMode();
        var amplitude = SignalLevelSlider is null
            ? 0.7
            : Math.Clamp(SignalLevelSlider.Value, 0.10, 1.0);
        _txWorker.UpdateLiveSignal(mode, toneHz, amplitude);
    }

    /// <summary>
    /// 信号レベル変更をライブ反映します。
    /// </summary>
    private void OnSignalLevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        PushLiveTxSignal();
    }

    /// <summary>
    /// トーン周波数／スイープ切替をライブ反映します。
    /// </summary>
    private void OnToneSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true })
        {
            PushLiveTxSignal();
        }
    }

    private void SetRxRunning(bool running)
    {
        RxStartButton.IsEnabled = !running;
        RxStopButton.IsEnabled = running;
        RxWavInputRadio.IsEnabled = !running;
        RxAudioInputRadio.IsEnabled = !running;
        RxBrowseWavButton.IsEnabled = !running;
        InputDeviceCombo.IsEnabled = !running;
        InputGainSlider.IsEnabled = !running;
        if (!running)
        {
            UpdateRxInputModePanels();
        }
    }

    private PerformanceTxSettings ReadTxSettings()
    {
        var channel = StereoRadio.IsChecked == true ? ChannelMode.Stereo : ChannelMode.Mono;
        var duration = ReadSelectedDurationSeconds();
        var (mode, toneHz) = ReadSignalMode();
        var sc = ReadSubcarriers();
        var mod = ReadModulation();
        var device = OutputDeviceCombo.SelectedItem is AudioDeviceItem item
            ? item.DeviceNumber
            : DefaultAudioDeviceNumber;

        var writeWav = WriteWavRadio.IsChecked == true;
        var volume = Math.Clamp(OutputVolumeSlider.Value / 100.0, 0.0, 1.0);
        var amplitude = SignalLevelSlider is null
            ? 0.7
            : Math.Clamp(SignalLevelSlider.Value, 0.10, 1.0);

        return new PerformanceTxSettings(
            ChannelMode: channel,
            SignalMode: mode,
            ToneHz: toneHz,
            ActiveSubcarriers: sc,
            ModulationScheme: mod,
            DurationSeconds: duration,
            WriteWav: writeWav,
            WavPath: WavPathTextBox.Text?.Trim() ?? string.Empty,
            PlayAudio: !writeWav,
            AudioDeviceNumber: device,
            SignalAmplitude: amplitude,
            OutputVolume: volume);
    }

    private PerformanceRxSettings ReadRxSettings()
    {
        var channel = StereoRadio.IsChecked == true ? ChannelMode.Stereo : ChannelMode.Mono;
        var useWav = RxWavInputRadio.IsChecked == true;
        var device = InputDeviceCombo.SelectedItem is AudioDeviceItem item
            ? item.DeviceNumber
            : DefaultAudioDeviceNumber;
        var (mode, _) = ReadSignalMode();
        var gain = Math.Clamp(InputGainSlider.Value / 100.0, 0.0, 1.0);

        return new PerformanceRxSettings(
            ChannelMode: channel,
            UseWavInput: useWav,
            WavPath: RxWavPathBox.Text?.Trim() ?? string.Empty,
            InputDeviceNumber: device,
            InputGain: gain,
            ActiveSubcarriers: ReadSubcarriers(),
            ModulationScheme: ReadModulation(),
            CaptureConstellation: mode == PerformanceSignalMode.Modulated,
            SignalMode: mode);
    }

    /// <summary>
    /// 選択中タブとトーン設定から信号モードを解決します。
    /// </summary>
    private (PerformanceSignalMode Mode, double ToneHz) ReadSignalMode()
    {
        if (ModulatedSignalRadio.IsChecked == true)
        {
            return (PerformanceSignalMode.Modulated, 0);
        }

        if (SweepRadio.IsChecked == true)
        {
            return (PerformanceSignalMode.Sweep, 0);
        }

        foreach (var radio in FindRadios(this))
        {
            if (radio.GroupName != "PerfTone" || radio.IsChecked != true || radio.Tag is not string tag)
            {
                continue;
            }

            if (double.TryParse(tag, out var hz))
            {
                return (PerformanceSignalMode.Tone, hz);
            }
        }

        return (PerformanceSignalMode.Tone, 315.0);
    }

    private double ReadSelectedDurationSeconds()
    {
        foreach (var radio in FindRadios(this))
        {
            if (radio.GroupName == "PerfDuration"
                && radio.IsChecked == true
                && radio.Tag is string tag
                && double.TryParse(tag, out var sec))
            {
                return sec;
            }
        }

        return 30.0;
    }

    private int ReadSubcarriers()
    {
        foreach (var radio in FindRadios(this))
        {
            if (radio.GroupName == "PerfSubcarrier"
                && radio.IsChecked == true
                && radio.Tag is string tag
                && int.TryParse(tag, out var sc))
            {
                return PerformanceSignalGenerator.ClampSubcarriers(sc);
            }
        }

        return 8;
    }

    private ModulationScheme ReadModulation()
    {
        foreach (var radio in FindRadios(this))
        {
            if (radio.GroupName != "PerfModulation" || radio.IsChecked != true || radio.Tag is not string tag)
            {
                continue;
            }

            return tag switch
            {
                "Bpsk" => ModulationScheme.Bpsk,
                "Qpsk" => ModulationScheme.Qpsk,
                "Qam16" => ModulationScheme.Qam16,
                "Qam64" => ModulationScheme.Qam64,
                "Qam256" => ModulationScheme.Qam256,
                _ => ModulationScheme.Bpsk
            };
        }

        return ModulationScheme.Bpsk;
    }

    /// <summary>
    /// 配下の RadioButton を列挙します。
    /// </summary>
    private static IEnumerable<RadioButton> FindRadios(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is RadioButton radio)
            {
                yield return radio;
            }

            if (child is DependencyObject dep)
            {
                foreach (var nested in FindRadios(dep))
                {
                    yield return nested;
                }
            }
        }
    }

    private sealed class AudioDeviceItem(int deviceNumber, string name)
    {
        public int DeviceNumber { get; } = deviceNumber;
        public string Name { get; } = name;
        public override string ToString() => Name;
    }
}
