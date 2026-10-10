using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.WPF;
using Microsoft.Win32;
using Onta.Core;
using Onta.Performance;
using Onta.View.Core;
using Onta.View.Language;
using NAudioWaveIn = NAudio.Wave.WaveIn;
using NAudioWaveOut = NAudio.Wave.WaveOut;
using Ellipse = System.Windows.Shapes.Ellipse;
using ShapePath = System.Windows.Shapes.Path;
using ShapeLine = System.Windows.Shapes.Line;

namespace Onta.View.Performance;

/// <summary>
/// 性能測定画面（トーン／スイープ／OFDM 送信と FFT・オシロ・I-Q・ワウ受信可視化）です。
/// </summary>
public partial class PerformancePanel : UserControl
{
    private const int DefaultAudioDeviceNumber = -1;

    private readonly PerformanceTxWorker _txWorker = new();
    private readonly PerformanceRxWorker _rxWorker = new();
    private readonly DispatcherTimer _pollTimer;
    private bool _runningNotified;

    /// <summary>送信または受信が実行中なら true。</summary>
    public bool IsRunning => _txWorker.IsBusy || _rxWorker.IsBusy;

    /// <summary>実行中状態が変わったときに通知します。</summary>
    public event EventHandler? RunningStateChanged;
    private readonly FftChartModel _fftLeft = new(FftChartModel.ChannelLeftColor);
    private readonly FftChartModel _fftRight = new(FftChartModel.ChannelRightColor);
    private readonly OscilloscopeChartModel _scopeLeft = new(FftChartModel.ChannelLeftColor);
    private readonly OscilloscopeChartModel _scopeRight = new(FftChartModel.ChannelRightColor);
    private readonly IqChartModel _iqLeft = new();
    private readonly IqChartModel _iqRight = new();
    private readonly WowFlutterChartModel _wowChart = new();
    private readonly double[] _scopeLeftBuf = new double[PerformanceConstants.ScopeCaptureSamples];
    private readonly double[] _scopeRightBuf = new double[PerformanceConstants.ScopeCaptureSamples];
    private readonly Complex[] _lissTimeScratch = new Complex[LissajousMeterAnalyzer.FftSize];
    private readonly Complex[] _lissFftScratch = new Complex[LissajousMeterAnalyzer.FftSize];
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

        InitializeOutputDevices();
        InitializeInputDevices();
        EnsureDefaultWavPath();
        UpdateOutputModePanels();
        UpdateRxInputModePanels();
        UpdateSignalModeUi();
        UpdateSignalLevelText();
        UpdateOutputVolumeText();
        UpdateRxInputVolumeText();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 現在の UI 設定を永続化用スナップショットへまとめます。
    /// </summary>
    /// <returns>性能測定 UI 設定。</returns>
    internal PerformanceUiSettingsSnapshot CaptureSettings()
    {
        var (mode, toneHz) = ReadSignalMode();
        var writeWav = WriteWavRadio.IsChecked == true;
        var outputDevice = OutputDeviceCombo.SelectedItem is AudioDeviceItem outItem
            ? outItem.DeviceNumber
            : DefaultAudioDeviceNumber;
        var inputDevice = InputDeviceCombo.SelectedItem is AudioDeviceItem inItem
            ? inItem.DeviceNumber
            : DefaultAudioDeviceNumber;
        var amplitude = SignalLevelSlider is null
            ? 0.80
            : Math.Clamp(SignalLevelSlider.Value, 0.10, 1.0);
        var outputVolume = OutputVolumeSlider is null
            ? 0.80
            : Math.Clamp(OutputVolumeSlider.Value / 100.0, 0.0, 1.0);
        var inputGain = InputGainSlider is null
            ? 0.80
            : Math.Clamp(InputGainSlider.Value / 100.0, 0.0, 1.0);

        return new PerformanceUiSettingsSnapshot(
            SignalMode: mode,
            ToneHz: toneHz,
            ActiveSubcarriers: ReadSubcarriers(),
            ModulationScheme: ReadModulation(),
            DurationSeconds: ReadSelectedDurationSeconds(),
            WriteWav: writeWav,
            WavPath: WavPathTextBox.Text?.Trim() ?? string.Empty,
            OutputDeviceNumber: outputDevice,
            SignalAmplitude: amplitude,
            OutputVolume: outputVolume,
            RxUseWavInput: RxWavInputRadio.IsChecked == true,
            RxWavPath: RxWavPathBox.Text?.Trim() ?? string.Empty,
            InputDeviceNumber: inputDevice,
            InputGain: inputGain,
            FftSize: ReadFftSize(),
            FftWindowKind: ReadFftWindowKind(),
            WavSampleRate: ReadWavSampleRate(),
            RxActiveSubcarriers: ReadSubcarriers("PerfRxSubcarrier"),
            RxModulationScheme: ReadModulation("PerfRxModulation"),
            RxModulated: RxModulatedCheckBox.IsChecked == true);
    }

    /// <summary>
    /// 保存済み設定を UI へ反映します。
    /// </summary>
    /// <param name="snapshot">性能測定 UI 設定。</param>
    internal void ApplySettings(PerformanceUiSettingsSnapshot snapshot)
    {
        switch (snapshot.SignalMode)
        {
            case PerformanceSignalMode.Modulated:
                ModulatedSignalRadio.IsChecked = true;
                break;
            default:
                ReferenceSignalRadio.IsChecked = true;
                break;
        }

        ApplyToneSelection(snapshot.SignalMode, snapshot.ToneHz);
        SetCheckedRadio("PerfSubcarrier", snapshot.ActiveSubcarriers.ToString(), "16");
        SetCheckedRadio("PerfModulation", ToModulationTag(snapshot.ModulationScheme), "Bpsk");
        SetCheckedRadio("PerfRxSubcarrier", snapshot.RxActiveSubcarriers.ToString(), "16");
        SetCheckedRadio("PerfRxModulation", ToModulationTag(snapshot.RxModulationScheme), "Bpsk");
        RxModulatedCheckBox.IsChecked = snapshot.RxModulated;
        SetCheckedRadio("PerfDuration", ((int)Math.Round(snapshot.DurationSeconds)).ToString(), "30");

        WriteWavRadio.IsChecked = snapshot.WriteWav;
        PlayAudioRadio.IsChecked = !snapshot.WriteWav;
        if (!string.IsNullOrWhiteSpace(snapshot.WavPath))
        {
            WavPathTextBox.Text = snapshot.WavPath;
        }

        SelectComboDevice(OutputDeviceCombo, snapshot.OutputDeviceNumber);
        SelectWavSampleRate(snapshot.WavSampleRate);
        if (SignalLevelSlider is not null)
        {
            SignalLevelSlider.Value = Math.Clamp(snapshot.SignalAmplitude, 0.10, 1.0);
        }

        if (OutputVolumeSlider is not null)
        {
            OutputVolumeSlider.Value = Math.Clamp(snapshot.OutputVolume * 100.0, 0.0, 100.0);
        }

        if (snapshot.RxUseWavInput)
        {
            RxWavInputRadio.IsChecked = true;
        }
        else
        {
            RxAudioInputRadio.IsChecked = true;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.RxWavPath))
        {
            RxWavPathBox.Text = snapshot.RxWavPath;
        }

        SelectComboDevice(InputDeviceCombo, snapshot.InputDeviceNumber);
        if (InputGainSlider is not null)
        {
            InputGainSlider.Value = Math.Clamp(snapshot.InputGain * 100.0, 0.0, 100.0);
        }

        SetCheckedRadio("PerfFftSize", PerformanceFftAnalyzer.ClampSize(snapshot.FftSize).ToString(), "2048");
        SelectFftWindow(snapshot.FftWindowKind);
        PushFftAnalysisSettings();

        UpdateSignalModeUi();
        UpdateOutputModePanels();
        UpdateRxInputModePanels();
        UpdateSignalLevelText();
        UpdateOutputVolumeText();
        UpdateRxInputVolumeText();
    }

    /// <summary>
    /// 基準信号のトーン／スイープ／ホワイトノイズ選択を反映します。
    /// </summary>
    /// <param name="mode">信号モード。</param>
    /// <param name="toneHz">トーン周波数（Hz）。</param>
    private void ApplyToneSelection(PerformanceSignalMode mode, double toneHz)
    {
        if (mode == PerformanceSignalMode.Sweep)
        {
            SweepRadio.IsChecked = true;
            return;
        }

        if (mode == PerformanceSignalMode.WhiteNoise)
        {
            WhiteNoiseRadio.IsChecked = true;
            return;
        }

        if (mode == PerformanceSignalMode.Modulated)
        {
            return;
        }

        var tag = toneHz switch
        {
            400 => "400",
            1000 => "1000",
            3000 => "3000",
            8000 => "8000",
            10000 => "10000",
            12500 => "12500",
            15000 => "15000",
            20000 => "20000",
            _ => "315"
        };
        SetCheckedRadio("PerfTone", tag, "315");
    }

    /// <summary>
    /// 指定 GroupName のラジオを Tag で選択します。
    /// </summary>
    /// <param name="groupName">ラジオボタングループ名。</param>
    /// <param name="tag">選択する Tag。</param>
    /// <param name="fallbackTag">見つからないときの代替 Tag。</param>
    private void SetCheckedRadio(string groupName, string tag, string fallbackTag)
    {
        RadioButton? fallback = null;
        foreach (var radio in FindRadios(this))
        {
            if (!string.Equals(radio.GroupName, groupName, StringComparison.Ordinal)
                || radio.Tag is not string radioTag)
            {
                continue;
            }

            if (string.Equals(radioTag, tag, StringComparison.OrdinalIgnoreCase))
            {
                radio.IsChecked = true;
                return;
            }

            if (string.Equals(radioTag, fallbackTag, StringComparison.OrdinalIgnoreCase))
            {
                fallback = radio;
            }
        }

        if (fallback is not null)
        {
            fallback.IsChecked = true;
        }
    }

    /// <summary>
    /// コンボのデバイス番号を選択します（無ければ既定）。
    /// </summary>
    /// <param name="combo">デバイス選択コンボ。</param>
    /// <param name="deviceNumber">オーディオデバイス番号。</param>
    private static void SelectComboDevice(ComboBox combo, int deviceNumber)
    {
        if (combo is null || combo.Items.Count == 0)
        {
            return;
        }

        foreach (var item in combo.Items)
        {
            if (item is AudioDeviceItem device && device.DeviceNumber == deviceNumber)
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    /// <summary>
    /// パネル Loaded 時の初期化を行います。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WowLeftMeter.ChannelLabel = "L";
        WowRightMeter.ChannelLabel = "R";

        BindChart(FftLeftChart, _fftLeft.Series, _fftLeft.XAxes, _fftLeft.YAxes);
        BindChart(FftRightChart, _fftRight.Series, _fftRight.XAxes, _fftRight.YAxes);
        ApplyFftChartLayout(FftLeftChart);
        ApplyFftChartLayout(FftRightChart);
        FftLeftChart.SizeChanged += (_, _) =>
        {
            ApplyFftChartLayout(FftLeftChart);
            LayoutFftFreqLabels(FftLeftFreqLabels);
            LayoutFftFreqGridLines(FftLeftGridLines);
        };
        FftRightChart.SizeChanged += (_, _) =>
        {
            ApplyFftChartLayout(FftRightChart);
            LayoutFftFreqLabels(FftRightFreqLabels);
            LayoutFftFreqGridLines(FftRightGridLines);
        };
        FftLeftFreqLabels.SizeChanged += (_, _) => LayoutFftFreqLabels(FftLeftFreqLabels);
        FftRightFreqLabels.SizeChanged += (_, _) => LayoutFftFreqLabels(FftRightFreqLabels);
        BindChart(ScopeLeftChart, _scopeLeft.Series, _scopeLeft.XAxes, _scopeLeft.YAxes);
        BindChart(ScopeRightChart, _scopeRight.Series, _scopeRight.XAxes, _scopeRight.YAxes);
        ApplyScopeChartLayout(ScopeLeftChart);
        ApplyScopeChartLayout(ScopeRightChart);
        HookScopeOverlaySync(ScopeLeftChart, ScopeLeftYOverlay, ScopeLeftYLabelCol);
        HookScopeOverlaySync(ScopeRightChart, ScopeRightYOverlay, ScopeRightYLabelCol);
        ScopeLeftChart.Loaded += OnScopeChartLoaded;
        ScopeRightChart.Loaded += OnScopeChartLoaded;
        ScopeLeftChart.SizeChanged += OnScopeChartSizeChanged;
        ScopeRightChart.SizeChanged += OnScopeChartSizeChanged;
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
                LayoutFftFreqGridLines(FftLeftGridLines);
                LayoutFftFreqGridLines(FftRightGridLines);
                ApplyScopeChartLayout(ScopeLeftChart);
                ApplyScopeChartLayout(ScopeRightChart);
                ApplyScopeAxisRanges();
                SyncScopeYOverlay(ScopeLeftChart, ScopeLeftYOverlay, ScopeLeftYLabelCol);
                SyncScopeYOverlay(ScopeRightChart, ScopeRightYOverlay, ScopeRightYLabelCol);
                UpdateIqHostSquares();
            },
            DispatcherPriority.Loaded);

        EnsureDefaultWavPath();
        UpdateOutputModePanels();
        UpdateRxInputModePanels();
        UpdateSignalModeUi();
        // コンストラクタで反映済みの表示を、レイアウト確定後にも再同期する。
        UpdateSignalLevelText();
        UpdateOutputVolumeText();
        UpdateRxInputVolumeText();
    }

    /// <summary>
    /// FFT / オシロスコープタブ切替を反映します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnWaveGraphTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || WaveGraphTabs is null)
        {
            return;
        }

        UpdateWaveGraphTabUi();
    }

    /// <summary>
    /// 波形タブの種類です。
    /// </summary>
    private enum WaveGraphMode
    {
        Fft,
        Scope,
        Lissajous
    }

    /// <summary>
    /// 現在選択中の波形タブです。
    /// </summary>
    private WaveGraphMode CurrentWaveGraphMode
    {
        get
        {
            if (LissajousWaveTabItem?.IsSelected == true
                || (WaveGraphTabs is not null && WaveGraphTabs.SelectedIndex == 2))
            {
                return WaveGraphMode.Lissajous;
            }

            if (ScopeWaveTabItem?.IsSelected == true
                || (WaveGraphTabs is not null && WaveGraphTabs.SelectedIndex == 1))
            {
                return WaveGraphMode.Scope;
            }

            return WaveGraphMode.Fft;
        }
    }

    /// <summary>
    /// オシロスコープタブが選択中かどうかです。
    /// </summary>
    private bool IsScopeWaveTabSelected => CurrentWaveGraphMode == WaveGraphMode.Scope;

    /// <summary>
    /// リサージュタブが選択中かどうかです。
    /// </summary>
    private bool IsLissajousTabSelected => CurrentWaveGraphMode == WaveGraphMode.Lissajous;

    /// <summary>
    /// FFT / オシロ / リサージュの表示切替です。
    /// </summary>
    private void UpdateWaveGraphTabUi()
    {
        if (WaveGraphTabs is null
            || FftLeftHost is null || ScopeLeftHost is null
            || FftRightHost is null || ScopeRightHost is null)
        {
            return;
        }

        var mode = CurrentWaveGraphMode;
        var showScope = mode == WaveGraphMode.Scope;
        var showFft = mode == WaveGraphMode.Fft;
        var showLiss = mode == WaveGraphMode.Lissajous;

        if (LeftWaveRow is not null)
        {
            LeftWaveRow.Visibility = showLiss ? Visibility.Collapsed : Visibility.Visible;
        }

        if (RightWaveRow is not null)
        {
            RightWaveRow.Visibility = showLiss ? Visibility.Collapsed : Visibility.Visible;
        }

        if (LissajousHost is not null)
        {
            LissajousHost.Visibility = showLiss ? Visibility.Visible : Visibility.Collapsed;
        }

        // アジマス（リサージュ）時は I-Q を隠す
        if (IqLeftHost is not null)
        {
            IqLeftHost.Visibility = showLiss ? Visibility.Collapsed : Visibility.Visible;
        }

        if (IqRightHost is not null)
        {
            IqRightHost.Visibility = showLiss ? Visibility.Collapsed : Visibility.Visible;
        }

        SetHostVisible(FftLeftHost, showFft);
        SetHostVisible(FftRightHost, showFft);
        SetHostVisible(ScopeLeftHost, showScope);
        SetHostVisible(ScopeRightHost, showScope);

        var fftChrome = showFft ? Visibility.Visible : Visibility.Collapsed;
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

        if (FftOptionsPanel is not null)
        {
            FftOptionsPanel.Visibility = fftChrome;
        }

        if (LeftWaveTitle is not null)
        {
            LeftWaveTitle.Text = showScope ? CoreViewText.ScopeAutoTitle("L", triggered: false) : "L FFT";
        }

        if (RightWaveTitle is not null)
        {
            RightWaveTitle.Text = showScope ? CoreViewText.ScopeAutoTitle("R", triggered: false) : "R FFT";
        }

        if (showScope)
        {
            ApplyScopeFromWorkers();
        }
        else if (showLiss)
        {
            ApplyLissajousFromWorkers();
            UpdateLissajousPlotSquare();
        }
    }

    /// <summary>
    /// ホストの Visibility／ヒットテストを切り替えます。
    /// </summary>
    /// <param name="host">ホスト要素。</param>
    /// <param name="visible">表示するなら true。</param>
    private static void SetHostVisible(UIElement host, bool visible)
    {
        host.Opacity = visible ? 1 : 0;
        host.IsHitTestVisible = visible;
        host.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// パネル Unloaded 時にワーカーとタイマを解放します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pollTimer.Stop();
        _txWorker.RequestStop();
        _rxWorker.RequestStop();
    }

    /// <summary>
    /// L/R 波形行の高さに合わせて I-Q を正方形にします。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
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
    /// <param name="row">行 Grid。</param>
    /// <param name="host">ホスト要素。</param>
    /// <param name="title">タイトル TextBlock。</param>
    /// <param name="legend">凡例パネル。</param>
    /// <param name="chart">LiveCharts チャート。</param>
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
    /// I-Q グラフ横のグループ凡例（A〜I）を構築します。
    /// </summary>
    /// <param name="host">ホスト要素。</param>
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
    /// I-Q プロットの余白を四辺同じにして、点が正方形に載るようにします。
    /// </summary>
    /// <param name="chart">LiveCharts チャート。</param>
    private static void ApplyIqChartLayout(CartesianChart chart)
    {
        chart.DrawMargin = new LiveChartsCore.Measure.Margin(10, 10, 10, 10);
        chart.ClipToBounds = true;
    }

    /// <summary>
    /// LiveCharts へ系列と軸をバインドします。
    /// </summary>
    /// <param name="chart">LiveCharts チャート。</param>
    /// <param name="series">系列コレクション。</param>
    /// <param name="xAxes">X 軸。</param>
    /// <param name="yAxes">Y 軸。</param>
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
    /// FFT チャートの描画余白と横軸範囲を調整します。横軸 MaxLimit は外部目盛り（縦線と同じ 0〜20000 Hz、PI 倍しない）。
    /// </summary>
    /// <param name="chart">LiveCharts チャート。</param>
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
        xAxis.MaxLimit = PerfFftXMaxHz;
        xAxis.MinStep = 2000;
        xAxis.ForceStepToMin = true;
        // 縦線は外部 Canvas の目盛りと同じ 0〜20k・2k 刻み。
        xAxis.CustomSeparators = null;
        xAxis.SeparatorsPaint = null;
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

    /// <summary>性能測定 FFT の横軸上限（Hz）。外部 0〜20k 目盛りと一致させる。</summary>
    private const double PerfFftXMaxHz = 20000.0;

    /// <summary>
    /// ReplacePoints 後も横軸を 0〜20000 Hz に固定します。
    /// </summary>
    private void RestorePerfFftXMax()
    {
        _fftLeft.XAxes[0].MinLimit = 0;
        _fftLeft.XAxes[0].MaxLimit = PerfFftXMaxHz;
        _fftRight.XAxes[0].MinLimit = 0;
        _fftRight.XAxes[0].MaxLimit = PerfFftXMaxHz;
    }

    /// <summary>
    /// オシロチャートの Loaded で Skia DPI 設定と軸を再適用します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnScopeChartLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is CartesianChart chart)
        {
            ApplyScopeChartLayout(chart);
            SyncScopeOverlayFor(chart);
        }

        ApplyScopeAxisRanges();
    }

    /// <summary>
    /// オシロチャートのサイズ変化で軸・外部目盛り位置を更新します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnScopeChartSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height <= 1 || e.NewSize.Width <= 1)
        {
            return;
        }

        if (sender is CartesianChart chart)
        {
            TrySetIgnorePixelScaling(chart);
            SyncScopeOverlayFor(chart);
        }

        if (IsScopeWaveTabSelected)
        {
            ApplyScopeAxisRanges();
        }
    }

    /// <summary>
    /// オシロスコープの軸ラベル用余白を設定します。
    /// </summary>
    /// <param name="chart">LiveCharts チャート。</param>
    private static void ApplyScopeChartLayout(CartesianChart chart)
    {
        chart.DrawMargin = OscilloscopeChartModel.CreateDrawMargin();
        chart.ClipToBounds = false;
        chart.ZoomMode = LiveChartsCore.Measure.ZoomAndPanMode.None;
        // HiDPI で Skia が物理ピクセル描画し下半分が欠けるのを防ぐ。
        TrySetIgnorePixelScaling(chart);
    }

    /// <summary>
    /// LiveCharts の実プロット矩形へ外部 Y 目盛りを同期します。
    /// </summary>
    /// <param name="chart">LiveCharts チャート。</param>
    /// <param name="overlay">オーバーレイ Canvas。</param>
    /// <param name="labelCol">外部 Y ラベル列。</param>
    private void HookScopeOverlaySync(
        CartesianChart chart,
        FrameworkElement? overlay,
        ColumnDefinition? labelCol)
    {
        if (overlay is null || labelCol is null)
        {
            return;
        }

        /// <summary>
        /// オシロスコープの外部 Y 目盛りをチャートの描画余白へ同期します。
        /// </summary>
        void Sync() => SyncScopeYOverlay(chart, overlay, labelCol);

        chart.UpdateFinished += _ => Dispatcher.BeginInvoke(Sync, DispatcherPriority.Render);
        if (chart.CoreChart is CartesianChartEngine engine)
        {
            engine.DrawMarginDefined += _ => Dispatcher.BeginInvoke(Sync, DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// 左右どちらのオーバーレイを同期するか振り分けます。
    /// </summary>
    /// <param name="chart">LiveCharts チャート。</param>
    private void SyncScopeOverlayFor(CartesianChart chart)
    {
        if (ReferenceEquals(chart, ScopeLeftChart))
        {
            SyncScopeYOverlay(chart, ScopeLeftYOverlay, ScopeLeftYLabelCol);
        }
        else if (ReferenceEquals(chart, ScopeRightChart))
        {
            SyncScopeYOverlay(chart, ScopeRightYOverlay, ScopeRightYLabelCol);
        }
    }

    /// <summary>
    /// CoreChart の DrawMargin 実座標に合わせて目盛りオーバーレイを置きます。これにより波形 y=0 と「0.0」／ゼロ線が一致します。
    /// </summary>
    /// <param name="chart">LiveCharts チャート。</param>
    /// <param name="overlay">オーバーレイ Canvas。</param>
    /// <param name="labelCol">外部 Y ラベル列。</param>
    private static void SyncScopeYOverlay(
        CartesianChart chart,
        FrameworkElement? overlay,
        ColumnDefinition? labelCol)
    {
        if (overlay is null || labelCol is null || chart.CoreChart is null)
        {
            return;
        }

        var core = chart.CoreChart;
        var loc = core.DrawMarginLocation;
        var size = core.DrawMarginSize;
        var chartW = chart.ActualWidth;
        var chartH = chart.ActualHeight;
        if (chartW <= 1 || chartH <= 1 || size.Width <= 1 || size.Height <= 1)
        {
            return;
        }

        // DrawMargin が物理ピクセルのときは DIP に戻す。
        var scaleX = 1.0;
        var scaleY = 1.0;
        if (loc.X + size.Width > chartW * 1.2 || loc.Y + size.Height > chartH * 1.2)
        {
            var dpi = VisualTreeHelper.GetDpi(chart);
            scaleX = Math.Max(1.0, dpi.DpiScaleX);
            scaleY = Math.Max(1.0, dpi.DpiScaleY);
        }

        var plotLeft = loc.X / scaleX;
        var plotTop = loc.Y / scaleY;
        var plotW = size.Width / scaleX;
        var plotH = size.Height / scaleY;
        if (plotW <= 1 || plotH <= 1)
        {
            return;
        }

        const double labelWidth = 40.0;
        var marginLeft = Math.Max(0.0, plotLeft - labelWidth);
        var marginRight = Math.Max(0.0, chartW - plotLeft - plotW);
        var marginBottom = Math.Max(0.0, chartH - plotTop - plotH);
        overlay.Margin = new Thickness(marginLeft, plotTop, marginRight, marginBottom);
        labelCol.Width = new GridLength(Math.Max(1.0, plotLeft - marginLeft));
    }

    /// <summary>
    /// SkiaSharp 要素の IgnorePixelScaling を有効化します（DIP を描画座標にする）。
    /// </summary>
    /// <param name="root">探索ルート。</param>
    private static void TrySetIgnorePixelScaling(DependencyObject root)
    {
        if (root is null)
        {
            return;
        }

        var type = root.GetType();
        var prop = type.GetProperty("IgnorePixelScaling");
        if (prop is { CanWrite: true } && prop.PropertyType == typeof(bool))
        {
            prop.SetValue(root, true);
        }

        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            TrySetIgnorePixelScaling(VisualTreeHelper.GetChild(root, i));
        }
    }

    /// <summary>
    /// L/R の振幅・時間レンジスライダー変更です。同期中は相手側へ同じ値を載せます。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
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
        if (IsScopeWaveTabSelected)
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
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
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
    /// <param name="sender">イベント送信元。</param>
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
        if (IsScopeWaveTabSelected)
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
    /// スライダー値をチャート軸へ反映し、外部縦目盛りラベルも更新します。
    /// </summary>
    private void ApplyScopeAxisRanges()
    {
        if (ScopeLeftAmpSlider is null || ScopeRightAmpSlider is null
            || ScopeLeftTimeSlider is null || ScopeRightTimeSlider is null)
        {
            return;
        }

        var ampLeft = ReadScopeAmp(ScopeLeftAmpSlider);
        var ampRight = ReadScopeAmp(ScopeRightAmpSlider);
        _scopeLeft.ApplyRanges(ampLeft, ReadScopeTimeMs(ScopeLeftTimeSlider));
        _scopeRight.ApplyRanges(ampRight, ReadScopeTimeMs(ScopeRightTimeSlider));
        ApplyScopeExternalYLabels(ampLeft, ampRight);
    }

    /// <summary>
    /// オシロ縦軸の外部ラベル（中央=0）を更新します。
    /// </summary>
    /// <param name="ampLeft">L 振幅ハーフ。</param>
    /// <param name="ampRight">R 振幅ハーフ。</param>
    private void ApplyScopeExternalYLabels(double ampLeft, double ampRight)
    {
        var left = OscilloscopeChartModel.FormatAmplitudeTickLabels(ampLeft);
        var right = OscilloscopeChartModel.FormatAmplitudeTickLabels(ampRight);
        SetScopeYLabelTexts(
            ScopeLeftYLabel0, ScopeLeftYLabel1, ScopeLeftYLabel2, ScopeLeftYLabel3, ScopeLeftYLabel4,
            left);
        SetScopeYLabelTexts(
            ScopeRightYLabel0, ScopeRightYLabel1, ScopeRightYLabel2, ScopeRightYLabel3, ScopeRightYLabel4,
            right);
    }

    /// <summary>
    /// 外部縦目盛り TextBlock へ文字列を割り当てます。
    /// </summary>
    /// <param name="t0">上端ラベル。</param>
    /// <param name="t1">上中ラベル。</param>
    /// <param name="t2">中央ラベル。</param>
    /// <param name="t3">下中ラベル。</param>
    /// <param name="t4">下端ラベル。</param>
    /// <param name="labels">ラベル TextBlock 列。</param>
    private static void SetScopeYLabelTexts(
        TextBlock? t0, TextBlock? t1, TextBlock? t2, TextBlock? t3, TextBlock? t4,
        string[] labels)
    {
        if (labels.Length < 5)
        {
            return;
        }

        if (t0 is not null) t0.Text = labels[0];
        if (t1 is not null) t1.Text = labels[1];
        if (t2 is not null) t2.Text = labels[2];
        if (t3 is not null) t3.Text = labels[3];
        if (t4 is not null) t4.Text = labels[4];
    }

    /// <summary>
    /// 縦軸スライダーから振幅レンジを読みます。
    /// </summary>
    /// <param name="slider">スライダー。</param>
    /// <returns>振幅。</returns>
    private static double ReadScopeAmp(Slider slider) =>
        OscilloscopeChartModel.AmplitudeHalfFromIndex((int)Math.Round(slider.Value));

    /// <summary>
    /// 横軸スライダーから表示幅（ms）を読みます。
    /// </summary>
    /// <param name="slider">スライダー。</param>
    /// <returns>表示幅（ms）。</returns>
    private static double ReadScopeTimeMs(Slider slider) =>
        OscilloscopeChartModel.TimeSpanMsFromIndex((int)Math.Round(slider.Value));

    /// <summary>
    /// 時間レンジをサンプル数へ変換します。
    /// </summary>
    /// <param name="timeSpanMs">表示幅（ms）。</param>
    /// <param name="sampleRate">サンプリング周波数。</param>
    /// <returns>表示サンプル数。</returns>
    private static int ScopeDisplaySamples(double timeSpanMs, int sampleRate) =>
        Math.Max(16, (int)Math.Round(timeSpanMs * Math.Max(1, sampleRate) / 1000.0));

    private static readonly (double Hz, string Text)[] FftFreqTicks =
    [
        (0, "0"), (2000, "2k"), (4000, "4k"), (6000, "6k"), (8000, "8k"),
        (10000, "10k"), (12000, "12k"), (14000, "14k"), (16000, "16k"),
        (18000, "18k"), (20000, "20k")
    ];

    /// <summary>
    /// 0〜20 kHz を Canvas 全幅に等間隔で配置します（チャート MaxLimit=20000 と一致）。
    /// </summary>
    /// <param name="canvas">描画 Canvas。</param>
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

    /// <summary>
    /// FFT 縦線 Canvas のサイズ変化で 2 kHz 間隔の縦線を引き直します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnFftGridLinesSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Canvas canvas)
        {
            LayoutFftFreqGridLines(canvas);
        }
    }

    /// <summary>
    /// 外部周波数目盛りと同じ位置（0〜20 kHz・2 kHz 刻み）に縦線を配置します。
    /// </summary>
    /// <param name="canvas">描画 Canvas。</param>
    private static void LayoutFftFreqGridLines(Canvas? canvas)
    {
        if (canvas is null)
        {
            return;
        }

        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        canvas.Children.Clear();
        var brush = new SolidColorBrush(Color.FromRgb(92, 97, 108));
        brush.Freeze();
        const double maxHz = 20000;
        foreach (var (hz, _) in FftFreqTicks)
        {
            var x = width * hz / maxHz;
            // 端は枠線と重なるのでわずかに内側へ
            if (hz <= 0)
            {
                x = 0.5;
            }
            else if (hz >= maxHz)
            {
                x = width - 0.5;
            }

            canvas.Children.Add(new ShapeLine
            {
                X1 = x,
                X2 = x,
                Y1 = 0,
                Y2 = height,
                Stroke = brush,
                StrokeThickness = 1,
                Opacity = 0.85
            });
        }
    }

    /// <summary>
    /// 出力デバイス一覧をコンボへ載せます。
    /// </summary>
    private void InitializeOutputDevices()
    {
        OutputDeviceCombo.Items.Clear();
        OutputDeviceCombo.Items.Add(new AudioDeviceItem(DefaultAudioDeviceNumber, CoreViewText.DefaultDevice));
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

    /// <summary>
    /// 入力デバイス一覧をコンボへ載せます。
    /// </summary>
    private void InitializeInputDevices()
    {
        InputDeviceCombo.Items.Clear();
        InputDeviceCombo.Items.Add(new AudioDeviceItem(DefaultAudioDeviceNumber, CoreViewText.DefaultDevice));
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
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
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
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnRxInputVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateRxInputVolumeText();
        _rxWorker.SetInputGain(InputGainSlider is null
            ? 0.80
            : Math.Clamp(InputGainSlider.Value / 100.0, 0.0, 1.0));
    }

    /// <summary>
    /// 受信側「変調」チェック・SC・変調の変更を、受信中ならその場で受信ワーカーへ反映します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnRxDemodulationChanged(object sender, RoutedEventArgs e)
    {
        // 初期化中（コントロール未生成）や停止中は、スタート時に読むので何もしない。
        if (_rxWorker is null || !_rxWorker.IsBusy)
        {
            return;
        }

        var settings = ReadRxSettings();
        _rxWorker.UpdateDemodulation(
            settings.CaptureConstellation,
            settings.SignalMode,
            settings.ActiveSubcarriers,
            settings.ModulationScheme);
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
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnBrowseRxWavClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = CoreViewText.DialogWavInputFile,
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

    /// <summary>
    /// 既定 WAV 出力パスを確保します。
    /// </summary>
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
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnSignalModeChanged(object sender, RoutedEventArgs e)
    {
        UpdateSignalModeUi();
        if (sender is RadioButton { IsChecked: true })
        {
            PushLiveTxSignalAndClearIq();
        }
    }

    /// <summary>
    /// 送信のサブキャリア数／変調方式の切替を、送信中ならライブ反映します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnTxModulatedSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true })
        {
            PushLiveTxSignalAndClearIq();
        }
    }

    /// <summary>
    /// 送信中なら信号の種類・SC・変調方式の変更をワーカーへ反映し、切替前の信号の I-Q 表示を消します。
    /// </summary>
    private void PushLiveTxSignalAndClearIq()
    {
        if (!_txWorker.IsBusy)
        {
            return;
        }

        PushLiveTxSignal();
        ClearIqCharts();
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

    /// <summary>
    /// 送信 WAV パス選択ダイアログを開きます。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnBrowseWavClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = CoreViewText.DialogWavOutputDestination,
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
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
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
        if (WavSampleRateCombo is not null)
        {
            WavSampleRateCombo.Visibility = writeWav ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// WAV 出力のサンプリング周波数コンボの選択値を返します。
    /// </summary>
    /// <returns><see cref="PerformanceConstants.SampleRate"/>、48000、96000 のいずれか。未選択時は <see cref="PerformanceConstants.SampleRate"/>。</returns>
    private int ReadWavSampleRate()
    {
        if (WavSampleRateCombo?.SelectedItem is ComboBoxItem item
            && SampleRateTag.TryRead(item.Tag, out var rate))
        {
            return PerformanceConstants.NormalizeWavSampleRate(rate);
        }

        return PerformanceConstants.SampleRate;
    }

    /// <summary>
    /// WAV 出力のサンプリング周波数コンボを指定値に合わせます。
    /// </summary>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    private void SelectWavSampleRate(int sampleRate)
    {
        if (WavSampleRateCombo is null)
        {
            return;
        }

        var rate = PerformanceConstants.NormalizeWavSampleRate(sampleRate);
        foreach (var item in WavSampleRateCombo.Items)
        {
            if (item is ComboBoxItem combo
                && SampleRateTag.TryRead(combo.Tag, out var tagRate)
                && tagRate == rate)
            {
                WavSampleRateCombo.SelectedItem = combo;
                return;
            }
        }
    }

    /// <summary>
    /// 送信を開始します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnTxStartClick(object sender, RoutedEventArgs e)
    {
        if (_txWorker.IsBusy)
        {
            return;
        }

        EnsureDefaultWavPath();
        var settings = ReadTxSettings();
        PushFftAnalysisSettings();
        if (!_txWorker.TryStart(settings))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                CoreViewText.MessageTxStartFailed,
                "Onta",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ResetPerformanceGraphs();
        SetTxRunning(true);
        TxStatusText.Text = CoreViewText.TxRunningDuration(settings.DurationSeconds);
        EnsurePollRunning();
    }

    /// <summary>
    /// 送信を停止します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnTxStopClick(object sender, RoutedEventArgs e)
    {
        _txWorker.RequestStop();
        TxStatusText.Text = CoreViewText.StageCancelRequested;
    }

    /// <summary>
    /// 送信・受信の開始時に、FFT / オシロ / リサージュ / I-Q / ワウフラッターを空へ戻します。
    /// </summary>
    private void ResetPerformanceGraphs()
    {
        _fftLeft.Clear();
        _fftRight.Clear();
        RestorePerfFftXMax();

        _scopeLeft.Clear();
        _scopeRight.Clear();
        RedrawScopeWaveCanvases();
        if (IsScopeWaveTabSelected)
        {
            if (LeftWaveTitle is not null)
            {
                LeftWaveTitle.Text = CoreViewText.ScopeAutoTitle("L", triggered: false);
            }

            if (RightWaveTitle is not null)
            {
                RightWaveTitle.Text = CoreViewText.ScopeAutoTitle("R", triggered: false);
            }
        }

        ClearIqCharts();

        _wowChart.Clear();
        _lastWowSampleUtc = DateTime.MinValue;
        WowLeftMeter.Clear();
        WowRightMeter.Clear();
        if (WowReferenceText is not null)
        {
            WowReferenceText.Text = CoreViewText.WowReference("----", "----");
        }

        LissajousCanvas?.Children.Clear();
        if (LissajousTitle is not null)
        {
            LissajousTitle.Text = CoreViewText.LissajousTitleBase;
        }

        if (LissFreqLeftText is not null)
        {
            LissFreqLeftText.Text = "---- Hz";
        }

        if (LissFreqRightText is not null)
        {
            LissFreqRightText.Text = "---- Hz";
        }

        if (LissThdLeftText is not null)
        {
            LissThdLeftText.Text = "--.-- %";
        }

        if (LissThdRightText is not null)
        {
            LissThdRightText.Text = "--.-- %";
        }
    }

    /// <summary>
    /// L/R の I-Q グラフとタイトルを空へ戻します。
    /// </summary>
    private void ClearIqCharts()
    {
        _iqLeft.Clear();
        _iqRight.Clear();
        if (IqLeftTitle is not null)
        {
            IqLeftTitle.Text = "L I-Q";
        }

        if (IqRightTitle is not null)
        {
            IqRightTitle.Text = "R I-Q";
        }
    }

    /// <summary>
    /// 受信を開始します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
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
                CoreViewText.MessageSelectRxWavFile,
                "Onta",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        PushFftAnalysisSettings();
        if (!_rxWorker.TryStart(settings))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                settings.UseWavInput
                    ? CoreViewText.MessageRxStartFailedWav
                    : CoreViewText.MessageRxStartFailedAudio,
                "Onta",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ResetPerformanceGraphs();
        SetRxRunning(true);
        RxStatusText.Text = settings.UseWavInput ? CoreViewText.RxAnalyzingWav : CoreViewText.ReceivingNow;
        EnsurePollRunning();
    }

    /// <summary>
    /// 受信を停止します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnRxStopClick(object sender, RoutedEventArgs e)
    {
        _rxWorker.RequestStop();
        SetRxRunning(false);
        RxStatusText.Text = CoreViewText.Stopped;
        MaybeStopPoll();
    }

    /// <summary>
    /// 共有メモリポーリングタイマを開始します。
    /// </summary>
    private void EnsurePollRunning()
    {
        if (!_pollTimer.IsEnabled)
        {
            _pollTimer.Start();
        }
    }

    /// <summary>
    /// 送受信が止まっていればポーリングを止めます。
    /// </summary>
    private void MaybeStopPoll()
    {
        if (!_txWorker.IsBusy && !_rxWorker.IsBusy)
        {
            _pollTimer.Stop();
        }
    }

    /// <summary>
    /// 共有メモリを読み UI へ反映します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnPollTick(object? sender, EventArgs e)
    {
        if (_rxWorker.IsBusy)
        {
            ApplyRxStatus(_rxWorker.SharedStatus.Read());
        }
        else if (RxStopButton.IsEnabled)
        {
            // WAV 終端・キャプチャ異常終了・ワーカーが落ちた場合
            var status = _rxWorker.SharedStatus.Read();
            ApplyRxStatus(status);
            SetRxRunning(false);
            if (status.IsFaulted && !string.IsNullOrWhiteSpace(status.LastError))
            {
                RxStatusText.Text = status.LastError;
            }
            else if (status.IsCompleted)
            {
                RxStatusText.Text = CoreViewText.Done;
            }
            else
            {
                RxStatusText.Text = CoreViewText.Stopped;
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
            TxStatusText.Text = ok ? CoreViewText.TxCompleted : message;
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
    /// <param name="status">共有ボードの状態。</param>
    private void ApplyRxStatus(CoreExecutionStatus status)
    {
        var mode = CurrentWaveGraphMode;
        if (mode == WaveGraphMode.Scope)
        {
            ApplyScopeFromWorkers();
        }
        else if (mode == WaveGraphMode.Lissajous)
        {
            ApplyLissajousFromWorkers();
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
            ApplyIqFromStatus(status);
        }

        ApplyRxWow(status);

        if (status.IsFaulted && !string.IsNullOrWhiteSpace(status.LastError))
        {
            SetRxRunning(false);
            RxStatusText.Text = status.LastError;
            MaybeStopPoll();
        }
    }

    /// <summary>
    /// ワウのメーターと時系列グラフを更新します。無音のチャネルは値を据え置き、両方無音ならグラフも止めます。
    /// </summary>
    /// <param name="status">共有ボードの状態。</param>
    private void ApplyRxWow(CoreExecutionStatus status)
    {
        _rxWorker.GetWowView(out var leftView, out var rightView);
        UpdateWowReferenceText(leftView, rightView);
        var leftActive = leftView.Active;
        var rightActive = rightView.Active;
        if (!leftActive && !rightActive)
        {
            _wowChart.Pause();
            return;
        }

        if (leftActive)
        {
            WowLeftMeter.AddSample(status.WowLeftPercent);
        }

        if (rightActive)
        {
            WowRightMeter.AddSample(status.WowRightPercent);
        }

        var now = DateTime.UtcNow;
        if ((now - _lastWowSampleUtc).TotalSeconds >= 0.2)
        {
            _wowChart.AddSample(WowLeftMeter.ValuePercent, WowRightMeter.ValuePercent);
            _lastWowSampleUtc = now;
        }

        _wowChart.Tick();
    }

    /// <summary>
    /// ワウの基準周波数（ロック中の送信側周波数と種類）を表示します。無音のチャネルは直前の表示を残します。
    /// </summary>
    /// <param name="left">L のワウ状態。</param>
    /// <param name="right">R のワウ状態。</param>
    private void UpdateWowReferenceText(PerformanceWowChannelView left, PerformanceWowChannelView right)
    {
        if (WowReferenceText is null || (!left.Active && !right.Active))
        {
            return;
        }

        WowReferenceText.Text = CoreViewText.WowReference(FormatWowReference(left), FormatWowReference(right));
    }

    /// <summary>
    /// 1 チャネル分の基準周波数の表示文字列を返します。
    /// </summary>
    /// <param name="view">そのチャネルのワウ状態。</param>
    /// <returns>例: 「3000.0 Hz（トーン）」。未ロックは「----」。</returns>
    private static string FormatWowReference(PerformanceWowChannelView view)
    {
        if (view.ReferenceHz <= 0)
        {
            return "----";
        }

        var kind = view.Kind == PerformanceWowReferenceKind.Carrier
            ? CoreViewText.WowReferenceCarrier
            : CoreViewText.WowReferenceTone;
        return $"{view.ReferenceHz:0.0} Hz{kind}";
    }

    /// <summary>
    /// 送信中の FFT / I-Q 可視化を反映します。
    /// </summary>
    /// <param name="status">共有ボードの状態。</param>
    private void ApplyTxViz(CoreExecutionStatus status)
    {
        ApplyIqFromStatus(status);

        if (status.FftGraph.FftSize <= 0)
        {
            if (IsScopeWaveTabSelected)
            {
                ApplyScopeFromWorkers();
            }
            else if (IsLissajousTabSelected)
            {
                ApplyLissajousFromWorkers();
            }

            return;
        }

        if (IsScopeWaveTabSelected)
        {
            ApplyScopeFromWorkers();
            return;
        }

        if (IsLissajousTabSelected)
        {
            ApplyLissajousFromWorkers();
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
    /// 共有状態の I-Q 点を L/R チャートへ載せます。ステレオは先頭 LeftPointCount 点を L、残りを R とし、モノラルは L/R に同じ点を出します。
    /// </summary>
    /// <param name="status">共有ボードの状態。</param>
    private void ApplyIqFromStatus(CoreExecutionStatus status)
    {
        var iq = status.IqGraph.Points;
        if (iq.Count <= 0)
        {
            return;
        }

        var leftCount = Math.Min(status.IqGraph.LeftPointCount, iq.Count);
        if (status.FftGraph.IsStereo)
        {
            // 点の無いチャネル（まだ信号を受けていない）は描き換えない
            if (leftCount > 0)
            {
                _iqLeft.ReplacePoints(iq.Take(leftCount).ToList(), status.IqGraph.ModulationScheme);
            }

            if (leftCount < iq.Count)
            {
                _iqRight.ReplacePoints(iq.Skip(leftCount).ToList(), status.IqGraph.ModulationScheme);
            }
        }
        else
        {
            _iqLeft.ReplacePoints(iq, status.IqGraph.ModulationScheme);
            _iqRight.ReplacePoints(iq, status.IqGraph.ModulationScheme);
        }

        if (status.IqGraph.ActiveSubcarrierCount > 0)
        {
            if (IqLeftTitle is not null)
            {
                IqLeftTitle.Text = $"L I-Q ({status.IqGraph.ModulationScheme} / SC={status.IqGraph.ActiveSubcarrierCount})";
            }

            if (IqRightTitle is not null)
            {
                IqRightTitle.Text = $"R I-Q ({status.IqGraph.ModulationScheme} / SC={status.IqGraph.ActiveSubcarrierCount})";
            }
        }
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
        RedrawScopeWaveCanvases();

        if (LeftWaveTitle is not null)
        {
            LeftWaveTitle.Text = CoreViewText.ScopeAutoTitle("L", leftCap.Triggered);
        }

        if (RightWaveTitle is not null)
        {
            RightWaveTitle.Text = CoreViewText.ScopeAutoTitle("R", rightCap.Triggered);
        }
    }

    /// <summary>
    /// 受信（優先）または送信の L/R PCM をリサージュ（L→X / R→Y）へ描きます。
    /// ステレオカセットのアジマス調整用。同位相なら対角線、位相差があると楕円になります。
    /// </summary>
    private void ApplyLissajousFromWorkers()
    {
        var copied = _rxWorker.TryCopyLatestPcm(_scopeLeftBuf, _scopeRightBuf, out var count, out var sampleRate);
        if (!copied)
        {
            copied = _txWorker.TryCopyLatestPcm(_scopeLeftBuf, _scopeRightBuf, out count, out sampleRate);
        }

        if (!copied || count <= 1)
        {
            return;
        }

        if (sampleRate <= 0)
        {
            sampleRate = PerformanceConstants.SampleRate;
        }

        var leftSpan = _scopeLeftBuf.AsSpan(0, count);
        var rightSpan = _scopeRightBuf.AsSpan(0, count);
        var leftSilent = PerformanceSignalLevel.IsSilent(leftSpan);
        var rightSilent = PerformanceSignalLevel.IsSilent(rightSpan);
        if (leftSilent && rightSilent)
        {
            return;
        }

        DrawLissajous(leftSpan, rightSpan);

        if (!leftSilent)
        {
            var leftMeters = LissajousMeterAnalyzer.Analyze(
                leftSpan, sampleRate, _lissTimeScratch, _lissFftScratch);
            UpdateLissajousMeterTexts(leftMeters, LissFreqLeftText, LissThdLeftText);
        }

        if (!rightSilent)
        {
            var rightMeters = LissajousMeterAnalyzer.Analyze(
                rightSpan, sampleRate, _lissTimeScratch, _lissFftScratch);
            UpdateLissajousMeterTexts(rightMeters, LissFreqRightText, LissThdRightText);
        }
    }

    /// <summary>
    /// 1 チャネル分の周波数カウンタ・歪み率の表示文言を更新します。
    /// </summary>
    /// <param name="meters">そのチャネルの計測結果。</param>
    /// <param name="frequencyText">周波数カウンタの表示先。</param>
    /// <param name="thdText">歪み率の表示先。</param>
    private static void UpdateLissajousMeterTexts(
        LissajousMeterAnalyzer.ChannelMeters meters,
        TextBlock? frequencyText,
        TextBlock? thdText)
    {
        if (frequencyText is not null)
        {
            frequencyText.Text = meters.FrequencyHz > 0 ? $"{meters.FrequencyHz} Hz" : "---- Hz";
        }

        if (thdText is not null)
        {
            thdText.Text = meters.ThdPercent >= 0 ? $"{meters.ThdPercent:0.00} %" : "--.-- %";
        }
    }

    /// <summary>
    /// リサージュホストのサイズ変化で正方形を合わせます。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnLissajousHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLissajousTabSelected)
        {
            return;
        }

        UpdateLissajousPlotSquare();
    }

    /// <summary>
    /// リサージュ描画領域のサイズ変化です。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnLissajousPlotSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLissajousIdealDiagonal();
        if (IsLissajousTabSelected)
        {
            ApplyLissajousFromWorkers();
        }
    }

    /// <summary>
    /// リサージュ枠をホスト高さに合わせて正方形にします。
    /// </summary>
    private void UpdateLissajousPlotSquare()
    {
        if (LissajousHost is null || LissajousPlotHost is null)
        {
            return;
        }

        var availH = Math.Max(120.0, LissajousHost.ActualHeight - 36);
        var availW = Math.Max(120.0, LissajousHost.ActualWidth - 40);
        var side = Math.Min(availH, availW);
        side = Math.Min(side, 420);
        LissajousPlotHost.Width = side;
        LissajousPlotHost.Height = side;
        UpdateLissajousIdealDiagonal();
    }

    /// <summary>
    /// 同位相の理想対角線（−1,−1〜+1,+1）を更新します。
    /// </summary>
    private void UpdateLissajousIdealDiagonal()
    {
        if (LissajousIdealDiagonal is null || LissajousPlotHost is null)
        {
            return;
        }

        var w = LissajousPlotHost.ActualWidth;
        var h = LissajousPlotHost.ActualHeight;
        if (w <= 1 || h <= 1)
        {
            return;
        }

        LissajousIdealDiagonal.X1 = 0;
        LissajousIdealDiagonal.Y1 = h;
        LissajousIdealDiagonal.X2 = w;
        LissajousIdealDiagonal.Y2 = 0;
    }

    /// <summary>
    /// L=X / R=Y の点列を Canvas に描きます（±1.0 レンジ、中央 0）。
    /// </summary>
    /// <param name="left">左チャネル PCM（計測用）。</param>
    /// <param name="right">右チャネル PCM（計測用）。</param>
    private void DrawLissajous(ReadOnlySpan<double> left, ReadOnlySpan<double> right)
    {
        if (LissajousCanvas is null || LissajousPlotHost is null)
        {
            return;
        }

        var w = LissajousPlotHost.ActualWidth;
        var h = LissajousPlotHost.ActualHeight;
        if (w <= 1 || h <= 1)
        {
            return;
        }

        LissajousCanvas.Children.Clear();
        var n = Math.Min(left.Length, right.Length);
        if (n < 2)
        {
            return;
        }

        const int maxPts = 2400;
        var stride = Math.Max(1, n / maxPts);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var started = false;
            for (var i = 0; i < n; i += stride)
            {
                var lx = Math.Clamp(left[i], -1.0, 1.0);
                var ry = Math.Clamp(right[i], -1.0, 1.0);
                var x = (lx + 1.0) * 0.5 * w;
                var y = (1.0 - ry) * 0.5 * h;
                var pt = new Point(x, y);
                if (!started)
                {
                    ctx.BeginFigure(pt, isFilled: false, isClosed: false);
                    started = true;
                }
                else
                {
                    ctx.LineTo(pt, isStroked: true, isSmoothJoin: false);
                }
            }
        }

        geo.Freeze();
        LissajousCanvas.Children.Add(new ShapePath
        {
            Data = geo,
            Stroke = new SolidColorBrush(Color.FromRgb(166, 221, 176)),
            StrokeThickness = 1.2,
            StrokeLineJoin = PenLineJoin.Round
        });

        if (LissajousTitle is not null)
        {
            var corr = EstimatePhaseCorrelation(left, right);
            var hint = corr >= 0.95
                ? CoreViewText.LissajousHintAlmostInPhase
                : corr >= 0.5
                    ? CoreViewText.LissajousHintPhaseDiffEllipse
                    : corr >= 0
                        ? CoreViewText.LissajousHintLargePhaseDiff
                        : CoreViewText.LissajousHintNearReversePhase;
            LissajousTitle.Text = CoreViewText.LissajousTitleWithHint(hint);
        }
    }

    /// <summary>
    /// L/R の正規化相関で位相同期の目安を返します（1=同位相、0=直交、−1=逆位相）。
    /// </summary>
    /// <param name="left">左チャネル PCM（計測用）。</param>
    /// <param name="right">右チャネル PCM（計測用）。</param>
    /// <returns>相関の目安（−1〜+1）。</returns>
    private static double EstimatePhaseCorrelation(ReadOnlySpan<double> left, ReadOnlySpan<double> right)
    {
        var n = Math.Min(left.Length, right.Length);
        if (n < 8)
        {
            return 0;
        }

        double sumL = 0, sumR = 0, sumLL = 0, sumRR = 0, sumLR = 0;
        var step = Math.Max(1, n / 2048);
        var count = 0;
        for (var i = 0; i < n; i += step)
        {
            var l = left[i];
            var r = right[i];
            sumL += l;
            sumR += r;
            sumLL += l * l;
            sumRR += r * r;
            sumLR += l * r;
            count++;
        }

        if (count < 2)
        {
            return 0;
        }

        var meanL = sumL / count;
        var meanR = sumR / count;
        var cov = sumLR / count - meanL * meanR;
        var varL = sumLL / count - meanL * meanL;
        var varR = sumRR / count - meanR * meanR;
        if (varL <= 1e-12 || varR <= 1e-12)
        {
            return 0;
        }

        return cov / Math.Sqrt(varL * varR);
    }

    /// <summary>
    /// オシロ波形 Canvas のサイズ変化で再描画します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnScopeWaveCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 1 || e.NewSize.Height <= 1)
        {
            return;
        }

        RedrawScopeWaveCanvases();
    }

    /// <summary>
    /// L/R の外部 Canvas へ波形を描き直します（目盛り 0 と対称になる座標系）。
    /// </summary>
    private void RedrawScopeWaveCanvases()
    {
        DrawScopeWaveOnCanvas(
            ScopeLeftWaveCanvas,
            _scopeLeft,
            new SolidColorBrush(Color.FromRgb(
                FftChartModel.ChannelLeftColor.Red,
                FftChartModel.ChannelLeftColor.Green,
                FftChartModel.ChannelLeftColor.Blue)));
        DrawScopeWaveOnCanvas(
            ScopeRightWaveCanvas,
            _scopeRight,
            new SolidColorBrush(Color.FromRgb(
                FftChartModel.ChannelRightColor.Red,
                FftChartModel.ChannelRightColor.Green,
                FftChartModel.ChannelRightColor.Blue)));
    }

    /// <summary>
    /// 1 チャネル分の波形とトリガー縦線を Canvas に描きます（Y: +amp=上端、0=中央、−amp=下端。外部目盛りと同じ）。
    /// </summary>
    /// <param name="canvas">描画 Canvas。</param>
    /// <param name="model">オシロチャートモデル。</param>
    /// <param name="stroke">線色ブラシ。</param>
    private static void DrawScopeWaveOnCanvas(
        Canvas? canvas,
        OscilloscopeChartModel model,
        Brush stroke)
    {
        if (canvas is null)
        {
            return;
        }

        canvas.Children.Clear();
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w <= 1 || h <= 1)
        {
            return;
        }

        var times = model.DisplayTimes;
        var amps = model.DisplayAmplitudes;
        if (times.Count == 0 || times.Count != amps.Count)
        {
            return;
        }

        var amp = Math.Max(0.05, model.AmplitudeHalf);
        var t0 = model.TimeStartMs;
        var t1 = model.TimeEndMs;
        var span = Math.Max(1e-9, t1 - t0);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var started = false;
            for (var i = 0; i < times.Count; i++)
            {
                var x = (times[i] - t0) / span * w;
                var y = (amp - amps[i]) / (2.0 * amp) * h;
                var pt = new Point(x, y);
                if (!started)
                {
                    ctx.BeginFigure(pt, isFilled: false, isClosed: false);
                    started = true;
                }
                else
                {
                    ctx.LineTo(pt, isStroked: true, isSmoothJoin: false);
                }
            }
        }

        geo.Freeze();
        canvas.Children.Add(new ShapePath
        {
            Data = geo,
            Stroke = stroke,
            StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round
        });

        if (model.HasTriggerMarker)
        {
            var tx = (0.0 - t0) / span * w;
            canvas.Children.Add(new ShapeLine
            {
                X1 = tx,
                X2 = tx,
                Y1 = 0,
                Y2 = h,
                Stroke = new SolidColorBrush(Color.FromRgb(255, 214, 80)),
                StrokeThickness = 1.2
            });
        }
    }

    /// <summary>
    /// 送信 UI の実行中状態を切り替えます。
    /// </summary>
    /// <param name="running">実行中なら true。</param>
    private void SetTxRunning(bool running)
    {
        TxStartButton.IsEnabled = !running && !_rxWorker.IsBusy;
        TxStopButton.IsEnabled = running;
        // 送信中は受信スタート不可。
        RxStartButton.IsEnabled = !running && !_rxWorker.IsBusy;
        WriteWavRadio.IsEnabled = !running;
        PlayAudioRadio.IsEnabled = !running;
        if (WavSampleRateCombo is not null)
        {
            WavSampleRateCombo.IsEnabled = !running;
        }
        OutputDeviceCombo.IsEnabled = !running;
        // 周波数ラジオ・信号レベルは再生中も変更可（PCM へライブ反映）。
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
                radio.IsEnabled = true;
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

        NotifyRunningStateChanged();
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
            ? 0.80
            : Math.Clamp(SignalLevelSlider.Value, 0.10, 1.0);
        _txWorker.UpdateLiveSignal(mode, toneHz, amplitude, ReadSubcarriers(), ReadModulation());
    }

    /// <summary>
    /// 信号レベル変更をライブ反映し、% 表示を更新します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnSignalLevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSignalLevelText();
        PushLiveTxSignal();
    }

    /// <summary>
    /// 送信信号レベルの % 表示を更新します（0.10〜1.00 → 10%〜100%）。
    /// </summary>
    private void UpdateSignalLevelText()
    {
        if (SignalLevelValueText is null || SignalLevelSlider is null)
        {
            return;
        }

        var pct = (int)Math.Round(Math.Clamp(SignalLevelSlider.Value, 0.10, 1.00) * 100.0);
        SignalLevelValueText.Text = $"{pct}%";
    }

    /// <summary>
    /// 出力音量スライダーの % 表示を更新します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnOutputVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOutputVolumeText();
        var volume = OutputVolumeSlider is null
            ? 0.80
            : Math.Clamp(OutputVolumeSlider.Value / 100.0, 0.0, 1.0);
        _txWorker.SetOutputVolume(volume);
    }

    /// <summary>
    /// 出力音量ラベルを更新します。
    /// </summary>
    private void UpdateOutputVolumeText()
    {
        if (OutputVolumeValueText is null || OutputVolumeSlider is null)
        {
            return;
        }

        OutputVolumeValueText.Text = $"{(int)Math.Round(OutputVolumeSlider.Value)}%";
    }

    /// <summary>
    /// トーン周波数／スイープ切替をライブ反映します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnToneSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true })
        {
            PushLiveTxSignal();
        }
    }

    /// <summary>
    /// 受信 UI の実行中状態を切り替えます。
    /// </summary>
    /// <param name="running">実行中なら true。</param>
    private void SetRxRunning(bool running)
    {
        RxStartButton.IsEnabled = !running && !_txWorker.IsBusy;
        RxStopButton.IsEnabled = running;
        // 受信中は送信スタート不可。
        TxStartButton.IsEnabled = !running && !_txWorker.IsBusy;
        RxWavInputRadio.IsEnabled = !running;
        RxAudioInputRadio.IsEnabled = !running;
        RxBrowseWavButton.IsEnabled = !running;
        InputDeviceCombo.IsEnabled = !running;
        if (!running)
        {
            UpdateRxInputModePanels();
        }

        NotifyRunningStateChanged();
    }

    /// <summary>
    /// 実行中フラグが変わったときだけ RunningStateChanged を通知します。
    /// </summary>
    private void NotifyRunningStateChanged()
    {
        var running = IsRunning;
        if (running == _runningNotified)
        {
            return;
        }

        _runningNotified = running;
        RunningStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 画面から送信設定を読み取ります。
    /// </summary>
    /// <returns>送信設定スナップショット。</returns>
    private PerformanceTxSettings ReadTxSettings()
    {
        // 性能測定の送信は常にステレオ。
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
            ? 0.80
            : Math.Clamp(SignalLevelSlider.Value, 0.10, 1.0);

        return new PerformanceTxSettings(
            ChannelMode: ChannelMode.Stereo,
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
            OutputVolume: volume,
            WavSampleRate: writeWav ? ReadWavSampleRate() : PerformanceConstants.SampleRate);
    }

    /// <summary>
    /// 画面から受信設定を読み取ります。
    /// </summary>
    /// <returns>受信設定スナップショット。</returns>
    private PerformanceRxSettings ReadRxSettings()
    {
        // 送信が常時ステレオのため、受信解析もステレオ前提。
        var useWav = RxWavInputRadio.IsChecked == true;
        var device = InputDeviceCombo.SelectedItem is AudioDeviceItem item
            ? item.DeviceNumber
            : DefaultAudioDeviceNumber;
        var modulated = RxModulatedCheckBox.IsChecked == true;
        var (sendMode, _) = ReadSignalMode();
        // 「変調」OFF のときは無変調として受ける。送信側が変調タブでもワウ基準はトーン一覧にする。
        var mode = modulated
            ? PerformanceSignalMode.Modulated
            : sendMode == PerformanceSignalMode.Modulated ? PerformanceSignalMode.Tone : sendMode;
        var gain = Math.Clamp(InputGainSlider.Value / 100.0, 0.0, 1.0);

        return new PerformanceRxSettings(
            ChannelMode: ChannelMode.Stereo,
            UseWavInput: useWav,
            WavPath: RxWavPathBox.Text?.Trim() ?? string.Empty,
            InputDeviceNumber: device,
            InputGain: gain,
            ActiveSubcarriers: ReadSubcarriers("PerfRxSubcarrier"),
            ModulationScheme: ReadModulation("PerfRxModulation"),
            CaptureConstellation: mode == PerformanceSignalMode.Modulated,
            SignalMode: mode);
    }

    /// <summary>
    /// 選択中タブとトーン設定から信号モードを解決します。
    /// </summary>
    /// <returns>信号モードとトーン周波数（Hz）。</returns>
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

        if (WhiteNoiseRadio.IsChecked == true)
        {
            return (PerformanceSignalMode.WhiteNoise, 0);
        }

        foreach (var radio in FindRadios(this))
        {
            if (radio.GroupName != "PerfTone" || radio.IsChecked != true || radio.Tag is not string tag)
            {
                continue;
            }

            if (string.Equals(tag, "whitenoise", StringComparison.OrdinalIgnoreCase))
            {
                return (PerformanceSignalMode.WhiteNoise, 0);
            }

            if (double.TryParse(tag, out var hz))
            {
                return (PerformanceSignalMode.Tone, hz);
            }
        }

        return (PerformanceSignalMode.Tone, 315.0);
    }

    /// <summary>
    /// 選択中の送信秒数を返します。
    /// </summary>
    /// <returns>秒数。</returns>
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

    /// <summary>
    /// 選択中のサブキャリア数を返します。
    /// </summary>
    /// <returns>サブキャリア数。</returns>
    /// <param name="groupName">ラジオボタンのグループ名（送信側 PerfSubcarrier／受信側 PerfRxSubcarrier）。</param>
    private int ReadSubcarriers(string groupName = "PerfSubcarrier")
    {
        foreach (var radio in FindRadios(this))
        {
            if (radio.GroupName == groupName
                && radio.IsChecked == true
                && radio.Tag is string tag
                && int.TryParse(tag, out var sc))
            {
                return PerformanceSignalGenerator.ClampSubcarriers(sc);
            }
        }

        return 16;
    }

    /// <summary>
    /// 選択中の変調方式を返します。
    /// </summary>
    /// <returns>変調方式。</returns>
    /// <param name="groupName">ラジオボタンのグループ名（送信側 PerfModulation／受信側 PerfRxModulation）。</param>
    private ModulationScheme ReadModulation(string groupName = "PerfModulation")
    {
        foreach (var radio in FindRadios(this))
        {
            if (radio.GroupName != groupName || radio.IsChecked != true || radio.Tag is not string tag)
            {
                continue;
            }

            return tag switch
            {
                "Bpsk" => ModulationScheme.Bpsk,
                "Qpsk" => ModulationScheme.Qpsk,
                "Psk8" => ModulationScheme.Psk8,
                "Qam16" => ModulationScheme.Qam16,
                "Qam64" => ModulationScheme.Qam64,
                "Qam256" => ModulationScheme.Qam256,
                _ => ModulationScheme.Bpsk
            };
        }

        return ModulationScheme.Bpsk;
    }

    /// <summary>
    /// 変調方式をラジオボタンの Tag 文字列へ変換します。
    /// </summary>
    /// <param name="scheme">変調方式。</param>
    /// <returns>ラジオボタンの Tag。</returns>
    private static string ToModulationTag(ModulationScheme scheme) => scheme switch
    {
        ModulationScheme.Qpsk => "Qpsk",
        ModulationScheme.Psk8 => "Psk8",
        ModulationScheme.Qam16 => "Qam16",
        ModulationScheme.Qam64 => "Qam64",
        ModulationScheme.Qam256 => "Qam256",
        _ => "Bpsk"
    };

    /// <summary>
    /// FFT サイズ／窓の変更を解析へ反映します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnFftAnalysisSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true })
        {
            PushFftAnalysisSettings();
        }
    }

    /// <summary>
    /// FFT 窓コンボの変更を解析へ反映します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnFftWindowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        PushFftAnalysisSettings();
    }

    /// <summary>
    /// 現在の FFT 解析設定を送受信ワーカーへ渡します。
    /// </summary>
    private void PushFftAnalysisSettings()
    {
        var size = ReadFftSize();
        var window = ReadFftWindowKind();
        _txWorker.UpdateFftAnalysis(size, window);
        _rxWorker.UpdateFftAnalysis(size, window);
    }

    /// <summary>
    /// 選択中の FFT 長を返します。
    /// </summary>
    /// <returns>FFT 長。</returns>
    private int ReadFftSize()
    {
        foreach (var radio in FindRadios(this))
        {
            if (radio.GroupName == "PerfFftSize"
                && radio.IsChecked == true
                && radio.Tag is string tag
                && int.TryParse(tag, out var size))
            {
                return PerformanceFftAnalyzer.ClampSize(size);
            }
        }

        return PerformanceFftAnalyzer.DefaultSize;
    }

    /// <summary>
    /// 選択中の FFT 窓関数を返します。
    /// </summary>
    /// <returns>窓種。</returns>
    private PerformanceFftWindowKind ReadFftWindowKind()
    {
        if (FftWindowCombo?.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            return tag switch
            {
                "Hamming" => PerformanceFftWindowKind.Hamming,
                "Blackman" => PerformanceFftWindowKind.Blackman,
                "FlatTop" => PerformanceFftWindowKind.FlatTop,
                "Rectangular" => PerformanceFftWindowKind.Rectangular,
                _ => PerformanceFftWindowKind.Hanning
            };
        }

        return PerformanceFftWindowKind.Hanning;
    }

    /// <summary>
    /// FFT 窓コンボを選択します。
    /// </summary>
    /// <param name="kind">FFT 窓種。</param>
    private void SelectFftWindow(PerformanceFftWindowKind kind)
    {
        if (FftWindowCombo is null)
        {
            return;
        }

        var tag = kind switch
        {
            PerformanceFftWindowKind.Hamming => "Hamming",
            PerformanceFftWindowKind.Blackman => "Blackman",
            PerformanceFftWindowKind.FlatTop => "FlatTop",
            PerformanceFftWindowKind.Rectangular => "Rectangular",
            _ => "Hanning"
        };

        foreach (var item in FftWindowCombo.Items)
        {
            if (item is ComboBoxItem combo && string.Equals(combo.Tag as string, tag, StringComparison.Ordinal))
            {
                FftWindowCombo.SelectedItem = combo;
                return;
            }
        }

        if (FftWindowCombo.Items.Count > 0)
        {
            FftWindowCombo.SelectedIndex = 0;
        }
    }

    /// <summary>
    /// 配下の RadioButton を列挙します。
    /// </summary>
    /// <param name="root">探索ルート。</param>
    /// <returns>配下の RadioButton。</returns>
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

    /// <summary>
    /// 音声入出力デバイスのコンボ項目（番号と表示名）です。
    /// </summary>
    /// <param name="deviceNumber">NAudio デバイス番号（既定は -1）。</param>
    /// <param name="name">コンボに表示するデバイス名。</param>
    private sealed class AudioDeviceItem(int deviceNumber, string name)
    {
        public int DeviceNumber { get; } = deviceNumber;
        public string Name { get; } = name;
        /// <summary>
        /// 表示用デバイス名を返します。
        /// </summary>
        /// <returns>表示文字列。</returns>
        public override string ToString() => Name;
    }
}

