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
/// 諤ｧ閭ｽ貂ｬ螳夂判髱｢・医ヨ繝ｼ繝ｳ・上せ繧､繝ｼ繝暦ｼ衆FDM 騾∽ｿ｡縺ｨ FFT繝ｻ繧ｪ繧ｷ繝ｭ繝ｻI-Q繝ｻ繝ｯ繧ｦ蜿嶺ｿ｡蜿ｯ隕門喧・峨〒縺吶・
/// </summary>
public partial class PerformancePanel : UserControl
{
    private const int DefaultAudioDeviceNumber = -1;
    private const double WowDisplayGain = 2.0;

    private readonly PerformanceTxWorker _txWorker = new();
    private readonly PerformanceRxWorker _rxWorker = new();
    private readonly DispatcherTimer _pollTimer;
    private bool _runningNotified;

    /// <summary>騾∽ｿ｡縺ｾ縺溘・蜿嶺ｿ｡縺悟ｮ溯｡御ｸｭ縺ｪ繧・true縲・/summary>
    public bool IsRunning => _txWorker.IsBusy || _rxWorker.IsBusy;

    /// <summary>螳溯｡御ｸｭ迥ｶ諷九′螟峨ｏ縺｣縺溘→縺阪↓騾夂衍縺励∪縺吶・/summary>
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
    /// 諤ｧ閭ｽ貂ｬ螳壹ヱ繝阪Ν繧貞・譛溷喧縺励∪縺吶・
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
    /// 迴ｾ蝨ｨ縺ｮ UI 險ｭ螳壹ｒ豌ｸ邯壼喧逕ｨ繧ｹ繝翫ャ繝励す繝ｧ繝・ヨ縺ｸ縺ｾ縺ｨ繧√∪縺吶・
    /// </summary>
    /// <returns>諤ｧ閭ｽ貂ｬ螳・UI 險ｭ螳壹・/returns>
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
            WavSampleRate: ReadWavSampleRate());
    }

    /// <summary>
    /// 菫晏ｭ俶ｸ医∩險ｭ螳壹ｒ UI 縺ｸ蜿肴丐縺励∪縺吶・
    /// </summary>
    /// <param name="snapshot">諤ｧ閭ｽ貂ｬ螳・UI 險ｭ螳壹・/param>
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
        SetCheckedRadio("PerfModulation", snapshot.ModulationScheme switch
        {
            ModulationScheme.Qpsk => "Qpsk",
            ModulationScheme.Psk8 => "Psk8",
            ModulationScheme.Qam16 => "Qam16",
            ModulationScheme.Qam64 => "Qam64",
            ModulationScheme.Qam256 => "Qam256",
            _ => "Bpsk"
        }, "Bpsk");
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
    /// 蝓ｺ貅紋ｿ｡蜿ｷ縺ｮ繝医・繝ｳ・上せ繧､繝ｼ繝暦ｼ上・繝ｯ繧､繝医ヮ繧､繧ｺ驕ｸ謚槭ｒ蜿肴丐縺励∪縺吶・
    /// </summary>
    /// <param name="mode">菫｡蜿ｷ繝｢繝ｼ繝峨・/param>
    /// <param name="toneHz">繝医・繝ｳ蜻ｨ豕｢謨ｰ・・z・峨・/param>
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
    /// 謖・ｮ・GroupName 縺ｮ繝ｩ繧ｸ繧ｪ繧・Tag 縺ｧ驕ｸ謚槭＠縺ｾ縺吶・
    /// </summary>
    /// <param name="groupName">繝ｩ繧ｸ繧ｪ繝懊ち繝ｳ繧ｰ繝ｫ繝ｼ繝怜錐縲・/param>
    /// <param name="tag">驕ｸ謚槭☆繧・Tag縲・/param>
    /// <param name="fallbackTag">隕九▽縺九ｉ縺ｪ縺・→縺阪・莉｣譖ｿ Tag縲・/param>
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
    /// 繧ｳ繝ｳ繝懊・繝・ヰ繧､繧ｹ逡ｪ蜿ｷ繧帝∈謚槭＠縺ｾ縺呻ｼ育┌縺代ｌ縺ｰ譌｢螳夲ｼ峨・
    /// </summary>
    /// <param name="combo">繝・ヰ繧､繧ｹ驕ｸ謚槭さ繝ｳ繝懊・/param>
    /// <param name="deviceNumber">繧ｪ繝ｼ繝・ぅ繧ｪ繝・ヰ繧､繧ｹ逡ｪ蜿ｷ縲・/param>
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
    /// 繝代ロ繝ｫ Loaded 譎ゅ・蛻晄悄蛹悶ｒ陦後＞縺ｾ縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
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
        // 繧ｳ繝ｳ繧ｹ繝医Λ繧ｯ繧ｿ縺ｧ蜿肴丐貂医∩縺ｮ陦ｨ遉ｺ繧偵√Ξ繧､繧｢繧ｦ繝育｢ｺ螳壼ｾ後↓繧ょ・蜷梧悄縺吶ｋ縲・
        UpdateSignalLevelText();
        UpdateOutputVolumeText();
        UpdateRxInputVolumeText();
    }

    /// <summary>
    /// FFT / 繧ｪ繧ｷ繝ｭ繧ｹ繧ｳ繝ｼ繝励ち繝門・譖ｿ繧貞渚譏縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnWaveGraphTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || WaveGraphTabs is null)
        {
            return;
        }

        UpdateWaveGraphTabUi();
    }

    /// <summary>
    /// 豕｢蠖｢繧ｿ繝悶・遞ｮ鬘槭〒縺吶・
    /// </summary>
    private enum WaveGraphMode
    {
        Fft,
        Scope,
        Lissajous
    }

    /// <summary>
    /// 迴ｾ蝨ｨ驕ｸ謚樔ｸｭ縺ｮ豕｢蠖｢繧ｿ繝悶〒縺吶・
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
    /// 繧ｪ繧ｷ繝ｭ繧ｹ繧ｳ繝ｼ繝励ち繝悶′驕ｸ謚樔ｸｭ縺九←縺・°縺ｧ縺吶・
    /// </summary>
    private bool IsScopeWaveTabSelected => CurrentWaveGraphMode == WaveGraphMode.Scope;

    /// <summary>
    /// 繝ｪ繧ｵ繝ｼ繧ｸ繝･繧ｿ繝悶′驕ｸ謚樔ｸｭ縺九←縺・°縺ｧ縺吶・
    /// </summary>
    private bool IsLissajousTabSelected => CurrentWaveGraphMode == WaveGraphMode.Lissajous;

    /// <summary>
    /// FFT / 繧ｪ繧ｷ繝ｭ / 繝ｪ繧ｵ繝ｼ繧ｸ繝･縺ｮ陦ｨ遉ｺ蛻・崛縺ｧ縺吶・
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

        // 繧｢繧ｸ繝槭せ・医Μ繧ｵ繝ｼ繧ｸ繝･・画凾縺ｯ I-Q 繧帝國縺・
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
    /// 繝帙せ繝医・ Visibility・上ヲ繝・ヨ繝・せ繝医ｒ蛻・ｊ譖ｿ縺医∪縺吶・
    /// </summary>
    /// <param name="host">繝帙せ繝郁ｦ∫ｴ縲・/param>
    /// <param name="visible">陦ｨ遉ｺ縺吶ｋ縺ｪ繧・true縲・/param>
    private static void SetHostVisible(UIElement host, bool visible)
    {
        host.Opacity = visible ? 1 : 0;
        host.IsHitTestVisible = visible;
        host.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 繝代ロ繝ｫ Unloaded 譎ゅ↓繝ｯ繝ｼ繧ｫ繝ｼ・上ち繧､繝槭ｒ隗｣謾ｾ縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pollTimer.Stop();
        _txWorker.RequestStop();
        _rxWorker.RequestStop();
    }

    /// <summary>
    /// L/R 豕｢蠖｢陦後・鬮倥＆縺ｫ蜷医ｏ縺帙※ I-Q 繧呈ｭ｣譁ｹ蠖｢縺ｫ縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnWaveRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateIqHostSquares();
    }

    /// <summary>
    /// L/R 縺ｮ I-Q 譫蟷・ｒ縲√ち繧､繝医Ν荳九・繝励Ο繝・ヨ縺碁ｫ倥＆縺ｨ蜷後§豁｣譁ｹ蠖｢縺ｫ縺ｪ繧九ｈ縺・粋繧上○縺ｾ縺吶・
    /// </summary>
    private void UpdateIqHostSquares()
    {
        UpdateIqHostSquare(LeftWaveRow, IqLeftHost, IqLeftTitle, IqLeftLegend, IqLeftChart);
        UpdateIqHostSquare(RightWaveRow, IqRightHost, IqRightTitle, IqRightLegend, IqRightChart);
    }

    /// <summary>
    /// I-Q 繝励Ο繝・ヨ繧呈ｭ｣譁ｹ蠖｢縺ｫ蝗ｺ螳壹＠縲∝・萓句・縺縺代・繧ｹ繝亥ｹ・ｒ蠎・￡縺ｾ縺吶・
    /// </summary>
    /// <param name="row">陦・Grid縲・/param>
    /// <param name="host">繝帙せ繝郁ｦ∫ｴ縲・/param>
    /// <param name="title">繧ｿ繧､繝医Ν TextBlock縲・/param>
    /// <param name="legend">蜃｡萓九ヱ繝阪Ν縲・/param>
    /// <param name="chart">LiveCharts 繝√Ε繝ｼ繝医・/param>
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
    /// I-Q 繧ｰ繝ｩ繝墓ｨｪ縺ｮ繧ｰ繝ｫ繝ｼ繝怜・萓具ｼ・縲廩・峨ｒ讒狗ｯ峨＠縺ｾ縺吶・
    /// </summary>
    /// <param name="host">繝帙せ繝郁ｦ∫ｴ縲・/param>
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
    /// I-Q 繝励Ο繝・ヨ縺ｮ菴咏區繧貞屁霎ｺ蝮・ｭ峨↓縺励※縲∫せ縺梧ｭ｣譁ｹ蠖｢縺ｫ霈峨ｋ繧医≧縺ｫ縺励∪縺吶・
    /// </summary>
    /// <param name="chart">LiveCharts 繝√Ε繝ｼ繝医・/param>
    private static void ApplyIqChartLayout(CartesianChart chart)
    {
        chart.DrawMargin = new LiveChartsCore.Measure.Margin(10, 10, 10, 10);
        chart.ClipToBounds = true;
    }

    /// <summary>
    /// LiveCharts 縺ｸ邉ｻ蛻励→霆ｸ繧偵ヰ繧､繝ｳ繝峨＠縺ｾ縺吶・
    /// </summary>
    /// <param name="chart">LiveCharts 繝√Ε繝ｼ繝医・/param>
    /// <param name="series">邉ｻ蛻励さ繝ｬ繧ｯ繧ｷ繝ｧ繝ｳ縲・/param>
    /// <param name="xAxes">X 霆ｸ縲・/param>
    /// <param name="yAxes">Y 霆ｸ縲・/param>
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
    /// FFT 繝√Ε繝ｼ繝医・謠冗判菴咏區縺ｨ讓ｪ霆ｸ遽・峇繧定ｪｿ謨ｴ縺励∪縺吶・讓ｪ霆ｸ MaxLimit 縺ｯ螟夜Κ逶ｮ逶帙ｊ・冗ｸｦ邱壹→蜷後§ 0窶・0000 Hz・・PI 蛟阪＠縺ｪ縺・ｼ峨・
    /// </summary>
    /// <param name="chart">LiveCharts 繝√Ε繝ｼ繝医・/param>
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
        // 邵ｦ邱壹・螟夜Κ Canvas・育岼逶帙ｊ縺ｨ蜷後§ 0縲・0k繝ｻ2k 蛻ｻ縺ｿ・峨・
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

    /// <summary>諤ｧ閭ｽ貂ｬ螳・FFT 縺ｮ讓ｪ霆ｸ荳企剞・・z・峨ょ､夜Κ 0縲・0k 逶ｮ逶帙ｊ縺ｨ荳閾ｴ縺輔○繧九・/summary>
    private const double PerfFftXMaxHz = 20000.0;

    /// <summary>
    /// ReplacePoints 蠕後ｂ讓ｪ霆ｸ繧・0窶・0000 Hz 縺ｫ蝗ｺ螳壹＠縺ｾ縺吶・
    /// </summary>
    private void RestorePerfFftXMax()
    {
        _fftLeft.XAxes[0].MinLimit = 0;
        _fftLeft.XAxes[0].MaxLimit = PerfFftXMaxHz;
        _fftRight.XAxes[0].MinLimit = 0;
        _fftRight.XAxes[0].MaxLimit = PerfFftXMaxHz;
    }

    /// <summary>
    /// 繧ｪ繧ｷ繝ｭ繝√Ε繝ｼ繝医・ Loaded 縺ｧ Skia DPI 險ｭ螳壹→霆ｸ繧貞・驕ｩ逕ｨ縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
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
    /// 繧ｪ繧ｷ繝ｭ繝√Ε繝ｼ繝医・繧ｵ繧､繧ｺ螟牙喧縺ｧ霆ｸ繝ｻ螟夜Κ逶ｮ逶帙ｊ菴咲ｽｮ繧呈峩譁ｰ縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
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
    /// 繧ｪ繧ｷ繝ｭ繧ｹ繧ｳ繝ｼ繝励・霆ｸ繝ｩ繝吶Ν逕ｨ菴咏區繧定ｨｭ螳壹＠縺ｾ縺吶・
    /// </summary>
    /// <param name="chart">LiveCharts 繝√Ε繝ｼ繝医・/param>
    private static void ApplyScopeChartLayout(CartesianChart chart)
    {
        chart.DrawMargin = OscilloscopeChartModel.CreateDrawMargin();
        chart.ClipToBounds = false;
        chart.ZoomMode = LiveChartsCore.Measure.ZoomAndPanMode.None;
        // HiDPI 縺ｧ Skia 縺檎黄逅・ヴ繧ｯ繧ｻ繝ｫ謠冗判縺嶺ｸ句濠蛻・′谺縺代ｋ縺ｮ繧帝亟縺舌・
        TrySetIgnorePixelScaling(chart);
    }

    /// <summary>
    /// LiveCharts 縺ｮ螳溘・繝ｭ繝・ヨ遏ｩ蠖｢縺ｸ螟夜Κ Y 逶ｮ逶帙ｊ繧貞酔譛溘＠縺ｾ縺吶・
    /// </summary>
    /// <param name="chart">LiveCharts 繝√Ε繝ｼ繝医・/param>
    /// <param name="overlay">繧ｪ繝ｼ繝舌・繝ｬ繧､ Canvas縲・/param>
    /// <param name="labelCol">螟夜Κ Y 繝ｩ繝吶Ν蛻励・/param>
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
    /// 蟾ｦ蜿ｳ縺ｩ縺｡繧牙・縺ｮ繧ｪ繝ｼ繝舌・繝ｬ繧､繧貞酔譛溘☆繧九°謖ｯ繧雁・縺代∪縺吶・
    /// </summary>
    /// <param name="chart">LiveCharts 繝√Ε繝ｼ繝医・/param>
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
    /// CoreChart 縺ｮ DrawMargin 螳溷ｺｧ讓吶↓蜷医ｏ縺帙※逶ｮ逶帙ｊ繧ｪ繝ｼ繝舌・繝ｬ繧､繧堤ｽｮ縺阪∪縺吶・縺薙ｌ縺ｫ繧医ｊ豕｢蠖｢ y=0 縺ｨ縲・.0縲搾ｼ城ｻ・ぞ繝ｭ邱壹′荳閾ｴ縺励∪縺吶・
    /// </summary>
    /// <param name="chart">LiveCharts 繝√Ε繝ｼ繝医・/param>
    /// <param name="overlay">繧ｪ繝ｼ繝舌・繝ｬ繧､ Canvas縲・/param>
    /// <param name="labelCol">螟夜Κ Y 繝ｩ繝吶Ν蛻励・/param>
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

        // DrawMargin 縺檎黄逅・ヴ繧ｯ繧ｻ繝ｫ縺ｮ縺ｨ縺阪・ DIP 縺ｫ謌ｻ縺吶・
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
    /// SkiaSharp 隕∫ｴ縺ｮ IgnorePixelScaling 繧呈怏蜉ｹ蛹悶＠縺ｾ縺呻ｼ・IP・晄緒逕ｻ蠎ｧ讓吶↓縺吶ｋ・峨・
    /// </summary>
    /// <param name="root">謗｢邏｢繝ｫ繝ｼ繝医・/param>
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
    /// L/R 縺ｮ謖ｯ蟷・・譎る俣繝ｬ繝ｳ繧ｸ繧ｹ繝ｩ繧､繝繝ｼ螟画峩縺ｧ縺吶ょ酔譛滉ｸｭ縺ｯ逶ｸ謇句・縺ｸ蜷後§蛟､繧定ｼ峨○縺ｾ縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
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
    /// L/R 蜷梧悄繝√ぉ繝・け縺ｮ螟画峩縺ｧ縺吶０N 譎ゅ・ L 縺ｮ繝ｬ繝ｳ繧ｸ繧・R 縺ｸ繧ｳ繝斐・縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
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
    /// 蜷梧悄荳ｭ縲∝虚縺九＠縺溘せ繝ｩ繧､繝繝ｼ縺ｮ霆ｸ縺縺醍嶌謇九メ繝｣繝阪Ν縺ｸ蜷医ｏ縺帙∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
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
    /// L 縺ｮ謖ｯ蟷・・譎る俣繝ｬ繝ｳ繧ｸ繧・R 縺ｸ繧ｳ繝斐・縺励∪縺吶・
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
    /// 繧ｹ繝ｩ繧､繝繝ｼ讓ｪ縺ｮ繝ｬ繝ｳ繧ｸ謨ｰ蛟､繧呈峩譁ｰ縺励∪縺吶・
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
    /// 繧ｹ繝ｩ繧､繝繝ｼ蛟､繧偵メ繝｣繝ｼ繝郁ｻｸ縺ｸ蜿肴丐縺励∝､夜Κ邵ｦ逶ｮ逶帙ｊ譁・ｨ繧よ峩譁ｰ縺励∪縺吶・
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
    /// 繧ｪ繧ｷ繝ｭ邵ｦ霆ｸ縺ｮ螟夜Κ繝ｩ繝吶Ν・井ｸｭ螟ｮ=0・峨ｒ譖ｴ譁ｰ縺励∪縺吶・
    /// </summary>
    /// <param name="ampLeft">L 謖ｯ蟷・ワ繝ｼ繝輔・/param>
    /// <param name="ampRight">R 謖ｯ蟷・ワ繝ｼ繝輔・/param>
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
    /// 螟夜Κ邵ｦ逶ｮ逶帙ｊ TextBlock 縺ｸ譁・ｨ繧貞牡繧雁ｽ薙※縺ｾ縺吶・
    /// </summary>
    /// <param name="t0">荳顔ｫｯ繝ｩ繝吶Ν縲・/param>
    /// <param name="t1">荳贋ｸｭ繝ｩ繝吶Ν縲・/param>
    /// <param name="t2">荳ｭ螟ｮ繝ｩ繝吶Ν縲・/param>
    /// <param name="t3">荳倶ｸｭ繝ｩ繝吶Ν縲・/param>
    /// <param name="t4">荳狗ｫｯ繝ｩ繝吶Ν縲・/param>
    /// <param name="labels">繝ｩ繝吶Ν TextBlock 蛻励・/param>
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
    /// 邵ｦ霆ｸ繧ｹ繝ｩ繧､繝繝ｼ縺九ｉ迚・険蟷・Ξ繝ｳ繧ｸ繧定ｪｭ縺ｿ縺ｾ縺吶・
    /// </summary>
    /// <param name="slider">繧ｹ繝ｩ繧､繝繝ｼ縲・/param>
    /// <returns>迚・険蟷・・/returns>
    private static double ReadScopeAmp(Slider slider) =>
        OscilloscopeChartModel.AmplitudeHalfFromIndex((int)Math.Round(slider.Value));

    /// <summary>
    /// 讓ｪ霆ｸ繧ｹ繝ｩ繧､繝繝ｼ縺九ｉ陦ｨ遉ｺ蟷・ｼ・s・峨ｒ隱ｭ縺ｿ縺ｾ縺吶・
    /// </summary>
    /// <param name="slider">繧ｹ繝ｩ繧､繝繝ｼ縲・/param>
    /// <returns>陦ｨ遉ｺ蟷・ｼ・s・峨・/returns>
    private static double ReadScopeTimeMs(Slider slider) =>
        OscilloscopeChartModel.TimeSpanMsFromIndex((int)Math.Round(slider.Value));

    /// <summary>
    /// 譎る俣繝ｬ繝ｳ繧ｸ繧偵し繝ｳ繝励Ν謨ｰ縺ｸ螟画鋤縺励∪縺吶・
    /// </summary>
    /// <param name="timeSpanMs">陦ｨ遉ｺ蟷・ｼ・s・峨・/param>
    /// <param name="sampleRate">繧ｵ繝ｳ繝励Μ繝ｳ繧ｰ蜻ｨ豕｢謨ｰ縲・/param>
    /// <returns>陦ｨ遉ｺ繧ｵ繝ｳ繝励Ν謨ｰ縲・/returns>
    private static int ScopeDisplaySamples(double timeSpanMs, int sampleRate) =>
        Math.Max(16, (int)Math.Round(timeSpanMs * Math.Max(1, sampleRate) / 1000.0));

    private static readonly (double Hz, string Text)[] FftFreqTicks =
    [
        (0, "0"), (2000, "2k"), (4000, "4k"), (6000, "6k"), (8000, "8k"),
        (10000, "10k"), (12000, "12k"), (14000, "14k"), (16000, "16k"),
        (18000, "18k"), (20000, "20k")
    ];

    /// <summary>
    /// 0窶・0 kHz 繧・Canvas 蜈ｨ蟷・↓遲蛾俣髫秘・鄂ｮ縺励∪縺呻ｼ医メ繝｣繝ｼ繝・MaxLimit=20000 縺ｨ荳閾ｴ・峨・
    /// </summary>
    /// <param name="canvas">謠冗判 Canvas縲・/param>
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
    /// FFT 邵ｦ邱・Canvas 縺ｮ繧ｵ繧､繧ｺ螟牙喧縺ｧ 2 kHz 髢馴囈縺ｮ邵ｦ邱壹ｒ蠑輔″逶ｴ縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnFftGridLinesSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Canvas canvas)
        {
            LayoutFftFreqGridLines(canvas);
        }
    }

    /// <summary>
    /// 螟夜Κ蜻ｨ豕｢謨ｰ逶ｮ逶帙ｊ縺ｨ蜷後§菴咲ｽｮ・・縲・0 kHz繝ｻ2 kHz 蛻ｻ縺ｿ・峨↓邵ｦ邱壹ｒ驟咲ｽｮ縺励∪縺吶・
    /// </summary>
    /// <param name="canvas">謠冗判 Canvas縲・/param>
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
            // 遶ｯ縺ｯ譫邱壹→驥阪↑繧九・縺ｧ繧上★縺九↓蜀・・縺ｸ
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
    /// 蜃ｺ蜉帙ョ繝舌う繧ｹ荳隕ｧ繧偵さ繝ｳ繝懊∈霈峨○縺ｾ縺吶・
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
            // 蛻玲嫌螟ｱ謨玲凾縺ｯ譌｢螳壹・縺ｿ
        }

        OutputDeviceCombo.SelectedIndex = 0;
    }

    /// <summary>
    /// 蜈･蜉帙ョ繝舌う繧ｹ荳隕ｧ繧偵さ繝ｳ繝懊∈霈峨○縺ｾ縺吶・
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
            // 蛻玲嫌螟ｱ謨玲凾縺ｯ譌｢螳壹・縺ｿ
        }

        InputDeviceCombo.SelectedIndex = 0;
    }

    /// <summary>
    /// WAV蜈･蜉・/ 髻ｳ螢ｰ蜈･蜉帙・繝代ロ繝ｫ陦ｨ遉ｺ繧貞・繧頑崛縺医∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnRxInputModeChanged(object sender, RoutedEventArgs e)
    {
        UpdateRxInputModePanels();
    }

    /// <summary>
    /// 蜿嶺ｿ｡蜈･蜉帙Δ繝ｼ繝峨↓蠢懊§縺ｦ WAV / 髻ｳ螢ｰ繝代ロ繝ｫ繧貞・繧頑崛縺医∪縺吶・
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
    /// 蜿嶺ｿ｡髻ｳ驥上せ繝ｩ繧､繝繝ｼ縺ｮ陦ｨ遉ｺ繧呈峩譁ｰ縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnRxInputVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateRxInputVolumeText();
        _rxWorker.SetInputGain(InputGainSlider is null
            ? 0.80
            : Math.Clamp(InputGainSlider.Value / 100.0, 0.0, 1.0));
    }

    /// <summary>
    /// 蜿嶺ｿ｡髻ｳ驥上Λ繝吶Ν繧呈峩譁ｰ縺励∪縺吶・
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
    /// 蜿嶺ｿ｡蛛ｴ WAV 蜈･蜉帙ヵ繧｡繧､繝ｫ繧帝∈謚槭＠縺ｾ縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
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
    /// 譌｢螳・WAV 蜃ｺ蜉帙ヱ繧ｹ繧堤｢ｺ菫昴＠縺ｾ縺吶・
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
    /// 蝓ｺ貅紋ｿ｡蜿ｷ / 螟芽ｪｿ縺ｮ繝ｩ繧ｸ繧ｪ蛻・崛繧貞渚譏縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnSignalModeChanged(object sender, RoutedEventArgs e)
    {
        UpdateSignalModeUi();
    }

    /// <summary>
    /// 蝓ｺ貅紋ｿ｡蜿ｷ繝代ロ繝ｫ縺ｨ螟芽ｪｿ繝代ロ繝ｫ繧貞・繧頑崛縺医∪縺吶・
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
    /// 騾∽ｿ｡ WAV 繝代せ驕ｸ謚槭ム繧､繧｢繝ｭ繧ｰ繧帝幕縺阪∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
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
    /// WAV蜃ｺ蜉・/ 髻ｳ螢ｰ蜃ｺ蜉帙・繝代ロ繝ｫ陦ｨ遉ｺ繧貞・繧頑崛縺医∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnOutputModeChanged(object sender, RoutedEventArgs e)
    {
        UpdateOutputModePanels();
    }

    /// <summary>
    /// 蜃ｺ蜉帙Δ繝ｼ繝峨↓蠢懊§縺ｦ WAV / 髻ｳ螢ｰ繝代ロ繝ｫ繧貞・繧頑崛縺医∪縺吶・
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
    /// WAV 蜃ｺ蜉帙・繧ｵ繝ｳ繝励Μ繝ｳ繧ｰ蜻ｨ豕｢謨ｰ繧ｳ繝ｳ繝懊・驕ｸ謚槫､繧定ｿ斐＠縺ｾ縺吶・
    /// </summary>
    /// <returns>44100縲・8000縲・6000 縺ｮ縺・★繧後°縲よ悴驕ｸ謚樊凾縺ｯ 44100縲・/returns>
    private int ReadWavSampleRate()
    {
        if (WavSampleRateCombo?.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && int.TryParse(tag, out var rate))
        {
            return PerformanceConstants.NormalizeWavSampleRate(rate);
        }

        return PerformanceConstants.SampleRate;
    }

    /// <summary>
    /// WAV 蜃ｺ蜉帙・繧ｵ繝ｳ繝励Μ繝ｳ繧ｰ蜻ｨ豕｢謨ｰ繧ｳ繝ｳ繝懊ｒ謖・ｮ壼､縺ｫ蜷医ｏ縺帙∪縺吶・
    /// </summary>
    /// <param name="sampleRate">繧ｵ繝ｳ繝励Μ繝ｳ繧ｰ蜻ｨ豕｢謨ｰ・・z・峨・/param>
    private void SelectWavSampleRate(int sampleRate)
    {
        if (WavSampleRateCombo is null)
        {
            return;
        }

        var rate = PerformanceConstants.NormalizeWavSampleRate(sampleRate).ToString();
        foreach (var item in WavSampleRateCombo.Items)
        {
            if (item is ComboBoxItem combo
                && combo.Tag is string tag
                && string.Equals(tag, rate, StringComparison.Ordinal))
            {
                WavSampleRateCombo.SelectedItem = combo;
                return;
            }
        }
    }

    /// <summary>
    /// 騾∽ｿ｡繧帝幕蟋九＠縺ｾ縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
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
    /// 騾∽ｿ｡繧貞●豁｢縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnTxStopClick(object sender, RoutedEventArgs e)
    {
        _txWorker.RequestStop();
        TxStatusText.Text = CoreViewText.StageCancelRequested;
    }

    /// <summary>
    /// 騾∽ｿ｡繝ｻ蜿嶺ｿ｡縺ｮ髢句ｧ区凾縺ｫ縲：FT / 繧ｪ繧ｷ繝ｭ / 繝ｪ繧ｵ繝ｼ繧ｸ繝･ / I-Q / 繝ｯ繧ｦ繝輔Λ繝・ち繝ｼ繧堤ｩｺ縺ｸ謌ｻ縺励∪縺吶・
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

        _wowChart.Clear();
        _lastWowSampleUtc = DateTime.MinValue;
        WowLeftMeter.Clear();
        WowRightMeter.Clear();
        WowLeftMeter.RangePercent = 1.0;
        WowRightMeter.RangePercent = 1.0;

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
    /// 蜿嶺ｿ｡繧帝幕蟋九＠縺ｾ縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
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
    /// 蜿嶺ｿ｡繧貞●豁｢縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnRxStopClick(object sender, RoutedEventArgs e)
    {
        _rxWorker.RequestStop();
        SetRxRunning(false);
        RxStatusText.Text = CoreViewText.Stopped;
        MaybeStopPoll();
    }

    /// <summary>
    /// 蜈ｱ譛峨Γ繝｢繝ｪ繝昴・繝ｪ繝ｳ繧ｰ繧ｿ繧､繝槭ｒ髢句ｧ九＠縺ｾ縺吶・
    /// </summary>
    private void EnsurePollRunning()
    {
        if (!_pollTimer.IsEnabled)
        {
            _pollTimer.Start();
        }
    }

    /// <summary>
    /// 騾∝女菫｡縺梧ｭ｢縺ｾ縺｣縺ｦ縺・ｌ縺ｰ繝昴・繝ｪ繝ｳ繧ｰ繧呈ｭ｢繧√∪縺吶・
    /// </summary>
    private void MaybeStopPoll()
    {
        if (!_txWorker.IsBusy && !_rxWorker.IsBusy)
        {
            _pollTimer.Stop();
        }
    }

    /// <summary>
    /// 蜈ｱ譛峨Γ繝｢繝ｪ繧定ｪｭ縺ｿ UI 縺ｸ蜿肴丐縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnPollTick(object? sender, EventArgs e)
    {
        if (_rxWorker.IsBusy)
        {
            ApplyRxStatus(_rxWorker.SharedStatus.Read());
        }
        else if (RxStopButton.IsEnabled)
        {
            // WAV 邨らｫｯ繧・く繝｣繝励メ繝｣逡ｰ蟶ｸ邨ゆｺ・〒繝ｯ繝ｼ繧ｫ繝ｼ縺瑚誠縺｡縺溷ｴ蜷・
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
    /// 蜿嶺ｿ｡蜈ｱ譛臥憾諷九ｒ繧ｰ繝ｩ繝輔∈蜿肴丐縺励∪縺吶・
    /// </summary>
    /// <param name="status">蜈ｱ譛峨・繝ｼ繝峨・迥ｶ諷九・/param>
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
    /// 騾∽ｿ｡荳ｭ縺ｮ FFT / I-Q 蜿ｯ隕門喧繧貞渚譏縺励∪縺吶・
    /// </summary>
    /// <param name="status">蜈ｱ譛峨・繝ｼ繝峨・迥ｶ諷九・/param>
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
    /// 蜈ｱ譛臥憾諷九・ I-Q 轤ｹ繧・L/R 繝√Ε繝ｼ繝医∈霈峨○縺ｾ縺吶・
    /// </summary>
    /// <param name="status">蜈ｱ譛峨・繝ｼ繝峨・迥ｶ諷九・/param>
    private void ApplyIqFromStatus(CoreExecutionStatus status)
    {
        var iq = status.IqGraph.Points;
        if (iq.Count <= 0)
        {
            return;
        }

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
    /// 蜿嶺ｿ｡・亥━蜈茨ｼ峨∪縺溘・騾∽ｿ｡縺ｮ PCM 繧・AUTO 繝医Μ繧ｬ繝ｼ縺励※繧ｪ繧ｷ繝ｭ縺ｸ謠上″縺ｾ縺吶・
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
    /// 蜿嶺ｿ｡・亥━蜈茨ｼ峨∪縺溘・騾∽ｿ｡縺ｮ L/R PCM 繧偵Μ繧ｵ繝ｼ繧ｸ繝･・・竊湛 / R竊炭・峨∈謠上″縺ｾ縺吶・
    /// 繧ｹ繝・Ξ繧ｪ繧ｫ繧ｻ繝・ヨ縺ｮ繧｢繧ｸ繝槭せ隱ｿ謨ｴ逕ｨ・壼酔菴咲嶌縺ｪ繧牙ｯｾ隗堤ｷ壹∽ｽ咲嶌蟾ｮ縺後≠繧九→讌募・縺ｫ縺ｪ繧翫∪縺吶・
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
        DrawLissajous(leftSpan, rightSpan);

        var leftMeters = LissajousMeterAnalyzer.Analyze(
            leftSpan, sampleRate, _lissTimeScratch, _lissFftScratch);
        var rightMeters = LissajousMeterAnalyzer.Analyze(
            rightSpan, sampleRate, _lissTimeScratch, _lissFftScratch);
        UpdateLissajousMeterTexts(leftMeters, rightMeters);
    }

    /// <summary>
    /// 蜻ｨ豕｢謨ｰ繧ｫ繧ｦ繝ｳ繧ｿ繝ｻ豁ｪ縺ｿ邇・・陦ｨ遉ｺ譁・ｨ繧呈峩譁ｰ縺励∪縺吶・
    /// </summary>
    /// <param name="left">蟾ｦ繝√Ε繝阪Ν PCM・剰ｨ域ｸｬ縲・/param>
    /// <param name="right">蜿ｳ繝√Ε繝阪Ν PCM・剰ｨ域ｸｬ縲・/param>
    private void UpdateLissajousMeterTexts(
        LissajousMeterAnalyzer.ChannelMeters left,
        LissajousMeterAnalyzer.ChannelMeters right)
    {
        if (LissFreqLeftText is not null)
        {
            LissFreqLeftText.Text = left.FrequencyHz > 0 ? $"{left.FrequencyHz} Hz" : "---- Hz";
        }

        if (LissFreqRightText is not null)
        {
            LissFreqRightText.Text = right.FrequencyHz > 0 ? $"{right.FrequencyHz} Hz" : "---- Hz";
        }

        if (LissThdLeftText is not null)
        {
            LissThdLeftText.Text = left.ThdPercent >= 0 ? $"{left.ThdPercent:0.00} %" : "--.-- %";
        }

        if (LissThdRightText is not null)
        {
            LissThdRightText.Text = right.ThdPercent >= 0 ? $"{right.ThdPercent:0.00} %" : "--.-- %";
        }
    }

    /// <summary>
    /// 繝ｪ繧ｵ繝ｼ繧ｸ繝･繝帙せ繝医・繧ｵ繧､繧ｺ螟牙喧縺ｧ豁｣譁ｹ蠖｢繧貞粋繧上○縺ｾ縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnLissajousHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLissajousTabSelected)
        {
            return;
        }

        UpdateLissajousPlotSquare();
    }

    /// <summary>
    /// 繝ｪ繧ｵ繝ｼ繧ｸ繝･謠冗判鬆伜沺縺ｮ繧ｵ繧､繧ｺ螟牙喧縺ｧ縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnLissajousPlotSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLissajousIdealDiagonal();
        if (IsLissajousTabSelected)
        {
            ApplyLissajousFromWorkers();
        }
    }

    /// <summary>
    /// 繝ｪ繧ｵ繝ｼ繧ｸ繝･譫繧偵・繧ｹ繝磯ｫ倥＆縺ｫ蜷医ｏ縺帙※豁｣譁ｹ蠖｢縺ｫ縺励∪縺吶・
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
    /// 蜷御ｽ咲嶌縺ｮ逅・Φ蟇ｾ隗堤ｷ夲ｼ遺・1,竏・・俄・・・1,+1・峨ｒ譖ｴ譁ｰ縺励∪縺吶・
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
    /// L=X / R=Y 縺ｮ轤ｹ蛻励ｒ Canvas 縺ｫ謠上″縺ｾ縺呻ｼ按ｱ1.0 繝ｬ繝ｳ繧ｸ縲∽ｸｭ螟ｮ 0・峨・
    /// </summary>
    /// <param name="left">蟾ｦ繝√Ε繝阪Ν PCM・剰ｨ域ｸｬ縲・/param>
    /// <param name="right">蜿ｳ繝√Ε繝阪Ν PCM・剰ｨ域ｸｬ縲・/param>
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
    /// L/R 縺ｮ豁｣隕丞喧逶ｸ髢｢縺ｧ菴咲嶌蜷梧悄縺ｮ逶ｮ螳峨ｒ霑斐＠縺ｾ縺呻ｼ・=蜷御ｽ咲嶌縲・=逶ｴ莠､縲・1=騾・嶌・峨・
    /// </summary>
    /// <param name="left">蟾ｦ繝√Ε繝阪Ν PCM・剰ｨ域ｸｬ縲・/param>
    /// <param name="right">蜿ｳ繝√Ε繝阪Ν PCM・剰ｨ域ｸｬ縲・/param>
    /// <returns>逶ｸ髢｢縺ｮ逶ｮ螳会ｼ・1縲・・峨・/returns>
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
    /// 繧ｪ繧ｷ繝ｭ豕｢蠖｢ Canvas 縺ｮ繧ｵ繧､繧ｺ螟牙喧縺ｧ蜀肴緒逕ｻ縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnScopeWaveCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 1 || e.NewSize.Height <= 1)
        {
            return;
        }

        RedrawScopeWaveCanvases();
    }

    /// <summary>
    /// L/R 縺ｮ螟夜Κ Canvas 縺ｸ豕｢蠖｢繧呈緒縺咲峩縺励∪縺呻ｼ育岼逶帙ｊ 0 縺ｨ蟇ｾ遘ｰ縺ｫ縺ｪ繧句ｺｧ讓咏ｳｻ・峨・
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
    /// 1 繝√Ε繝阪Ν蛻・・豕｢蠖｢縺ｨ繝医Μ繧ｬ繝ｼ邵ｦ邱壹ｒ Canvas 縺ｫ謠上″縺ｾ縺吶・Y: +amp=荳顔ｫｯ縲・=荳ｭ螟ｮ縲≫・amp=荳狗ｫｯ・亥､夜Κ逶ｮ逶帙ｊ縺ｨ蜷後§・峨・
    /// </summary>
    /// <param name="canvas">謠冗判 Canvas縲・/param>
    /// <param name="model">繧ｪ繧ｷ繝ｭ繝√Ε繝ｼ繝医Δ繝・Ν縲・/param>
    /// <param name="stroke">邱夊牡繝悶Λ繧ｷ縲・/param>
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
    /// 騾∽ｿ｡ UI 縺ｮ螳溯｡御ｸｭ迥ｶ諷九ｒ蛻・ｊ譖ｿ縺医∪縺吶・
    /// </summary>
    /// <param name="running">螳溯｡御ｸｭ縺ｪ繧・true縲・/param>
    private void SetTxRunning(bool running)
    {
        TxStartButton.IsEnabled = !running && !_rxWorker.IsBusy;
        TxStopButton.IsEnabled = running;
        // 騾∽ｿ｡荳ｭ縺ｯ蜿嶺ｿ｡繧ｹ繧ｿ繝ｼ繝井ｸ榊庄縲・
        RxStartButton.IsEnabled = !running && !_rxWorker.IsBusy;
        ReferenceSignalRadio.IsEnabled = !running;
        ModulatedSignalRadio.IsEnabled = !running;
        WriteWavRadio.IsEnabled = !running;
        PlayAudioRadio.IsEnabled = !running;
        if (WavSampleRateCombo is not null)
        {
            WavSampleRateCombo.IsEnabled = !running;
        }
        OutputDeviceCombo.IsEnabled = !running;
        // 蜻ｨ豕｢謨ｰ繝ｩ繧ｸ繧ｪ繝ｻ菫｡蜿ｷ繝ｬ繝吶Ν縺ｯ蜀咲函荳ｭ繧ょ､画峩蜿ｯ・・CM 縺ｸ繝ｩ繧､繝門渚譏・・
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

        NotifyRunningStateChanged();
    }

    /// <summary>
    /// 騾∽ｿ｡荳ｭ縺ｮ蜻ｨ豕｢謨ｰ繝ｻ繝ｬ繝吶Ν繧偵Ρ繝ｼ繧ｫ繝ｼ縺ｸ蜿肴丐縺励∪縺吶・
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
        _txWorker.UpdateLiveSignal(mode, toneHz, amplitude);
    }

    /// <summary>
    /// 菫｡蜿ｷ繝ｬ繝吶Ν螟画峩繧偵Λ繧､繝門渚譏縺励・ 陦ｨ遉ｺ繧呈峩譁ｰ縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnSignalLevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSignalLevelText();
        PushLiveTxSignal();
    }

    /// <summary>
    /// 騾∽ｿ｡菫｡蜿ｷ繝ｬ繝吶Ν縺ｮ % 陦ｨ遉ｺ繧呈峩譁ｰ縺励∪縺呻ｼ・.10縲・.00 竊・10%縲・00%・峨・
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
    /// 蜃ｺ蜉幃浹驥上せ繝ｩ繧､繝繝ｼ縺ｮ % 陦ｨ遉ｺ繧呈峩譁ｰ縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnOutputVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOutputVolumeText();
        var volume = OutputVolumeSlider is null
            ? 0.80
            : Math.Clamp(OutputVolumeSlider.Value / 100.0, 0.0, 1.0);
        _txWorker.SetOutputVolume(volume);
    }

    /// <summary>
    /// 蜃ｺ蜉幃浹驥上Λ繝吶Ν繧呈峩譁ｰ縺励∪縺吶・
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
    /// 繝医・繝ｳ蜻ｨ豕｢謨ｰ・上せ繧､繝ｼ繝怜・譖ｿ繧偵Λ繧､繝門渚譏縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnToneSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true })
        {
            PushLiveTxSignal();
        }
    }

    /// <summary>
    /// 蜿嶺ｿ｡ UI 縺ｮ螳溯｡御ｸｭ迥ｶ諷九ｒ蛻・ｊ譖ｿ縺医∪縺吶・
    /// </summary>
    /// <param name="running">螳溯｡御ｸｭ縺ｪ繧・true縲・/param>
    private void SetRxRunning(bool running)
    {
        RxStartButton.IsEnabled = !running && !_txWorker.IsBusy;
        RxStopButton.IsEnabled = running;
        // 蜿嶺ｿ｡荳ｭ縺ｯ騾∽ｿ｡繧ｹ繧ｿ繝ｼ繝井ｸ榊庄縲・
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
    /// 螳溯｡御ｸｭ繝輔Λ繧ｰ縺悟､峨ｏ縺｣縺溘→縺阪□縺・RunningStateChanged 繧帝夂衍縺励∪縺吶・
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
    /// 逕ｻ髱｢縺九ｉ騾∽ｿ｡險ｭ螳壹ｒ隱ｭ縺ｿ蜿悶ｊ縺ｾ縺吶・
    /// </summary>
    /// <returns>騾∽ｿ｡險ｭ螳壹せ繝翫ャ繝励す繝ｧ繝・ヨ縲・/returns>
    private PerformanceTxSettings ReadTxSettings()
    {
        // 諤ｧ閭ｽ貂ｬ螳壹・騾∽ｿ｡縺ｯ蟶ｸ縺ｫ繧ｹ繝・Ξ繧ｪ縲・
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
    /// 逕ｻ髱｢縺九ｉ蜿嶺ｿ｡險ｭ螳壹ｒ隱ｭ縺ｿ蜿悶ｊ縺ｾ縺吶・
    /// </summary>
    /// <returns>蜿嶺ｿ｡險ｭ螳壹せ繝翫ャ繝励す繝ｧ繝・ヨ縲・/returns>
    private PerformanceRxSettings ReadRxSettings()
    {
        // 騾∽ｿ｡縺悟ｸｸ譎ゅせ繝・Ξ繧ｪ縺ｮ縺溘ａ縲∝女菫｡隗｣譫舌ｂ繧ｹ繝・Ξ繧ｪ蜑肴署縲・
        var useWav = RxWavInputRadio.IsChecked == true;
        var device = InputDeviceCombo.SelectedItem is AudioDeviceItem item
            ? item.DeviceNumber
            : DefaultAudioDeviceNumber;
        var (mode, _) = ReadSignalMode();
        var gain = Math.Clamp(InputGainSlider.Value / 100.0, 0.0, 1.0);

        return new PerformanceRxSettings(
            ChannelMode: ChannelMode.Stereo,
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
    /// 驕ｸ謚樔ｸｭ繧ｿ繝悶→繝医・繝ｳ險ｭ螳壹°繧我ｿ｡蜿ｷ繝｢繝ｼ繝峨ｒ隗｣豎ｺ縺励∪縺吶・
    /// </summary>
    /// <returns>菫｡蜿ｷ繝｢繝ｼ繝峨→繝医・繝ｳ蜻ｨ豕｢謨ｰ・・z・峨・/returns>
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
    /// 驕ｸ謚樔ｸｭ縺ｮ騾∽ｿ｡遘呈焚繧定ｿ斐＠縺ｾ縺吶・
    /// </summary>
    /// <returns>遘呈焚縲・/returns>
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
    /// 驕ｸ謚樔ｸｭ縺ｮ繧ｵ繝悶く繝｣繝ｪ繧｢謨ｰ繧定ｿ斐＠縺ｾ縺吶・
    /// </summary>
    /// <returns>繧ｵ繝悶く繝｣繝ｪ繧｢謨ｰ縲・/returns>
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

        return 16;
    }

    /// <summary>
    /// 驕ｸ謚樔ｸｭ縺ｮ螟芽ｪｿ譁ｹ蠑上ｒ霑斐＠縺ｾ縺吶・
    /// </summary>
    /// <returns>螟芽ｪｿ譁ｹ蠑上・/returns>
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
    /// FFT 繧ｵ繧､繧ｺ・冗ｪ薙・螟画峩繧定ｧ｣譫舌∈蜿肴丐縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnFftAnalysisSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true })
        {
            PushFftAnalysisSettings();
        }
    }

    /// <summary>
    /// FFT 遯薙さ繝ｳ繝懊・螟画峩繧定ｧ｣譫舌∈蜿肴丐縺励∪縺吶・
    /// </summary>
    /// <param name="sender">繧､繝吶Φ繝磯∽ｿ｡蜈・・/param>
    /// <param name="e">繧､繝吶Φ繝亥ｼ墓焚縲・/param>
    private void OnFftWindowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        PushFftAnalysisSettings();
    }

    /// <summary>
    /// 迴ｾ蝨ｨ縺ｮ FFT 隗｣譫占ｨｭ螳壹ｒ騾∝女菫｡繝ｯ繝ｼ繧ｫ繝ｼ縺ｸ貂｡縺励∪縺吶・
    /// </summary>
    private void PushFftAnalysisSettings()
    {
        var size = ReadFftSize();
        var window = ReadFftWindowKind();
        _txWorker.UpdateFftAnalysis(size, window);
        _rxWorker.UpdateFftAnalysis(size, window);
    }

    /// <summary>
    /// 驕ｸ謚樔ｸｭ縺ｮ FFT 髟ｷ繧定ｿ斐＠縺ｾ縺吶・
    /// </summary>
    /// <returns>FFT 髟ｷ縲・/returns>
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
    /// 驕ｸ謚樔ｸｭ縺ｮ FFT 遯馴未謨ｰ繧定ｿ斐＠縺ｾ縺吶・
    /// </summary>
    /// <returns>遯鍋ｨｮ縲・/returns>
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
    /// FFT 遯薙さ繝ｳ繝懊ｒ驕ｸ謚槭＠縺ｾ縺吶・
    /// </summary>
    /// <param name="kind">FFT 遯鍋ｨｮ縲・/param>
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
    /// 驟堺ｸ九・ RadioButton 繧貞・謖吶＠縺ｾ縺吶・
    /// </summary>
    /// <param name="root">謗｢邏｢繝ｫ繝ｼ繝医・/param>
    /// <returns>驟堺ｸ九・ RadioButton縲・/returns>
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
    /// 髻ｳ螢ｰ蜈･蜃ｺ蜉帙ョ繝舌う繧ｹ縺ｮ繧ｳ繝ｳ繝憺・岼・育分蜿ｷ縺ｨ陦ｨ遉ｺ蜷搾ｼ峨〒縺吶・
    /// </summary>
    /// <param name="deviceNumber">NAudio 繝・ヰ繧､繧ｹ逡ｪ蜿ｷ・域里螳壹・ -1・峨・/param>
    /// <param name="name">繧ｳ繝ｳ繝懊↓陦ｨ遉ｺ縺吶ｋ繝・ヰ繧､繧ｹ蜷阪・/param>
    private sealed class AudioDeviceItem(int deviceNumber, string name)
    {
        public int DeviceNumber { get; } = deviceNumber;
        public string Name { get; } = name;
        /// <summary>
        /// 陦ｨ遉ｺ逕ｨ繝・ヰ繧､繧ｹ蜷阪ｒ霑斐＠縺ｾ縺吶・
        /// </summary>
        /// <returns>陦ｨ遉ｺ譁・ｭ怜・縲・/returns>
        public override string ToString() => Name;
    }
}

