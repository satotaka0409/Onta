using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Onta.View.Performance;

/// <summary>
/// オシロスコープ（時間波形）用チャートモデルです。
/// トリガー点を t=0、プリトリガーを負時間に置きます。
/// </summary>
public sealed class OscilloscopeChartModel
{
    private const int MaxDisplayPoints = 640;
    private static readonly SKColor WaveColor = new(166, 221, 176);
    private static readonly SKColor AxisColor = new(176, 181, 191);
    private static readonly SKColor GridColor = new(92, 97, 108);
    private static readonly SKColor TriggerColor = new(255, 214, 80);
    private static readonly SKColor LevelColor = new(196, 168, 72);

    private readonly ObservableCollection<ObservablePoint> _points = [];
    private readonly ObservableCollection<ObservablePoint> _triggerTime = [];
    private readonly ObservableCollection<ObservablePoint> _triggerLevel = [];
    private readonly LineSeries<ObservablePoint> _series;
    private double _amplitudeHalf = 1.0;
    private double _timeSpanMs = 20.0;
    private double _dpiScale = 1.0;

    /// <summary>縦軸レンジ（片振幅）。スライダー順は狭い→広い。</summary>
    public static readonly double[] AmplitudeRangeHalf = [0.1, 0.2, 0.5, 1.0];

    /// <summary>横軸表示幅（ms）。スライダー順は短い→長い。</summary>
    public static readonly double[] TimeSpanMsSteps = [1, 2, 5, 10, 20, 50, 100];

    /// <summary>
    /// オシロスコープチャートを初期化します。
    /// </summary>
    /// <param name="stroke">波形色。省略時は L 相当の緑。</param>
    public OscilloscopeChartModel(SKColor? stroke = null)
    {
        var color = stroke ?? WaveColor;
        _series = new LineSeries<ObservablePoint>
        {
            Values = _points,
            Name = "PCM",
            Fill = null,
            Stroke = new SolidColorPaint(color, 1.4f),
            GeometrySize = 0,
            LineSmoothness = 0,
            EnableNullSplitting = false
        };

        Series =
        [
            _series,
            new LineSeries<ObservablePoint>
            {
                Values = _triggerTime,
                Name = "Trig",
                Fill = null,
                Stroke = new SolidColorPaint(TriggerColor, 1.2f),
                GeometrySize = 0,
                LineSmoothness = 0,
                IsVisible = false,
                EnableNullSplitting = false
            },
            new LineSeries<ObservablePoint>
            {
                Values = _triggerLevel,
                Name = "Level",
                Fill = null,
                Stroke = new SolidColorPaint(LevelColor, 1.0f),
                GeometrySize = 0,
                LineSmoothness = 0,
                IsVisible = false,
                EnableNullSplitting = false
            }
        ];
        XAxes =
        [
            new Axis
            {
                Name = null,
                MinLimit = -0.2,
                MaxLimit = 0.8,
                TextSize = 8,
                NameTextSize = 0,
                NamePadding = new LiveChartsCore.Drawing.Padding(0, 0, 0, 0),
                Padding = new LiveChartsCore.Drawing.Padding(0, 0, 0, 2),
                NamePaint = null,
                LabelsPaint = new SolidColorPaint(AxisColor),
                SeparatorsPaint = new SolidColorPaint(GridColor) { StrokeThickness = 1 },
                SeparatorsAtCenter = false,
                TicksAtCenter = false,
                ForceStepToMin = true,
                MinStep = 0.2,
                CustomSeparators = BuildTimeSeparators(-0.2, 0.8),
                Labeler = FormatTimeLabel
            }
        ];
        YAxes =
        [
            new Axis
            {
                Name = null,
                MinLimit = -1.0,
                MaxLimit = 1.0,
                MinStep = 0.5,
                ForceStepToMin = true,
                CustomSeparators = [-1.0, -0.5, 0.0, 0.5, 1.0],
                SeparatorsAtCenter = false,
                TicksAtCenter = false,
                Labeler = v => $"{v:0.0}",
                TextSize = 8,
                NameTextSize = 0,
                NamePadding = new LiveChartsCore.Drawing.Padding(0, 0, 0, 0),
                Padding = new LiveChartsCore.Drawing.Padding(0, 0, 4, 0),
                NamePaint = null,
                LabelsPaint = new SolidColorPaint(AxisColor),
                SeparatorsPaint = new SolidColorPaint(GridColor) { StrokeThickness = 1 }
            }
        ];
    }

    public ISeries[] Series { get; }

    public Axis[] XAxes { get; }

    public Axis[] YAxes { get; }

    /// <summary>
    /// オシロ用の描画余白です（左=振幅、下=時間。下を厚めにして端が見切れないようにする）。
    /// </summary>
    public static Margin CreateDrawMargin() => new(40, 18, 10, 38);

    /// <summary>
    /// スライダーインデックスから片振幅レンジを返します。
    /// </summary>
    public static double AmplitudeHalfFromIndex(int index) =>
        AmplitudeRangeHalf[Math.Clamp(index, 0, AmplitudeRangeHalf.Length - 1)];

    /// <summary>
    /// スライダーインデックスから横軸幅（ms）を返します。
    /// </summary>
    public static double TimeSpanMsFromIndex(int index) =>
        TimeSpanMsSteps[Math.Clamp(index, 0, TimeSpanMsSteps.Length - 1)];

    /// <summary>
    /// 振幅レンジの表示文言です。
    /// </summary>
    public static string FormatAmplitudeRange(double half) =>
        half >= 1.0 ? "±1.0" : $"±{half:0.0}";

    /// <summary>
    /// 時間レンジの表示文言です。
    /// </summary>
    public static string FormatTimeSpan(double ms) => $"{ms:0} ms";

    /// <summary>
    /// 縦軸・横軸の表示レンジを設定します。
    /// </summary>
    /// <param name="amplitudeHalf">片振幅（±この値）。</param>
    /// <param name="timeSpanMs">表示幅（ms）。</param>
    /// <param name="dpiScale">WPF DPI 倍率。Skia 物理ピクセル描画の見切れ補正に使う。</param>
    public void ApplyRanges(double amplitudeHalf, double timeSpanMs, double dpiScale = 1.0)
    {
        _amplitudeHalf = Math.Max(0.05, amplitudeHalf);
        _timeSpanMs = Math.Max(0.2, timeSpanMs);
        _dpiScale = dpiScale < 0.5 ? 1.0 : dpiScale;

        // LiveCharts は Skia を物理ピクセル高さで描き、WPF は DIP で上側だけ見える。
        // 可視範囲が ±amp になるよう Min/Max を DPI で広げ、上端は線幅用に少し余白を残す。
        const double topPad = 1.08;
        YAxes[0].MaxLimit = _amplitudeHalf * topPad;
        YAxes[0].MinLimit = _amplitudeHalf * (topPad - (_dpiScale * (topPad + 1.0)));
        YAxes[0].MinStep = _amplitudeHalf / 2.0;
        YAxes[0].ForceStepToMin = true;
        YAxes[0].SeparatorsAtCenter = false;
        YAxes[0].TicksAtCenter = false;
        YAxes[0].CustomSeparators =
        [
            -_amplitudeHalf,
            -_amplitudeHalf / 2.0,
            0.0,
            _amplitudeHalf / 2.0,
            _amplitudeHalf
        ];
        var decimals = _amplitudeHalf < 0.15 ? "0.00" : "0.0";
        YAxes[0].Labeler = v => v.ToString(decimals);

        var pre = _timeSpanMs * OscilloscopeTrigger.PreTriggerRatio;
        var t0 = -pre;
        var t1 = _timeSpanMs - pre;
        XAxes[0].MinLimit = t0;
        XAxes[0].MaxLimit = t0 + ((t1 - t0) * _dpiScale);
        XAxes[0].LabelsPaint = new SolidColorPaint(AxisColor);
        XAxes[0].TextSize = 8;
        XAxes[0].Labeler = FormatTimeLabel;
        var step = NiceTimeStep(_timeSpanMs);
        XAxes[0].MinStep = step;
        XAxes[0].ForceStepToMin = true;
        XAxes[0].SeparatorsAtCenter = false;
        XAxes[0].TicksAtCenter = false;
        XAxes[0].CustomSeparators = BuildTimeSeparators(t0, t1, step);
    }

    /// <summary>
    /// 表示幅に対して読みやすい時間ステップ（ms）を選びます。
    /// </summary>
    private static double NiceTimeStep(double spanMs)
    {
        // だいたい 8〜12 本の縦線になる刻み。
        double[] candidates = [0.05, 0.1, 0.2, 0.25, 0.5, 1, 2, 2.5, 5, 10, 20, 25, 50];
        var target = spanMs / 10.0;
        foreach (var c in candidates)
        {
            if (c >= target * 0.8)
            {
                return c;
            }
        }

        return candidates[^1];
    }

    /// <summary>
    /// t0〜t1 を step 刻みで並べた横軸目盛りです（端点を含む）。
    /// </summary>
    private static double[] BuildTimeSeparators(double t0, double t1, double step = 0.2)
    {
        if (step <= 0 || t1 <= t0)
        {
            return [t0, t1];
        }

        var first = Math.Ceiling(t0 / step) * step;
        if (Math.Abs(first - t0) < step * 1e-9)
        {
            first = t0;
        }

        var values = new List<double>(16) { t0 };
        for (var t = first; t < t1 - (step * 1e-9); t += step)
        {
            if (Math.Abs(t - t0) > step * 1e-9)
            {
                values.Add(t);
            }
        }

        if (Math.Abs(values[^1] - t1) > step * 1e-9)
        {
            values.Add(t1);
        }

        return values.ToArray();
    }

    /// <summary>
    /// PCM を時間軸（ms）の波形として反映します。トリガー点を t=0 にします。
    /// </summary>
    /// <param name="samples">表示窓の PCM（振幅 -1..1）。</param>
    /// <param name="sampleRate">サンプリング周波数。</param>
    /// <param name="triggerOffset">窓内のトリガー位置。</param>
    /// <param name="triggered">起立検出できたか。</param>
    /// <param name="level">トリガーレベル。</param>
    public void ReplaceWaveform(
        ReadOnlySpan<double> samples,
        int sampleRate,
        int triggerOffset,
        bool triggered,
        double level)
    {
        ApplyRanges(_amplitudeHalf, _timeSpanMs, _dpiScale);
        _points.Clear();
        _triggerTime.Clear();
        _triggerLevel.Clear();
        if (samples.IsEmpty || sampleRate <= 0)
        {
            Series[1].IsVisible = false;
            Series[2].IsVisible = false;
            return;
        }

        var sr = Math.Max(1, sampleRate);
        var trig = Math.Clamp(triggerOffset, 0, samples.Length - 1);
        var pre = _timeSpanMs * OscilloscopeTrigger.PreTriggerRatio;
        var t0 = -pre;
        var t1 = _timeSpanMs - pre;
        var y0 = -_amplitudeHalf;
        var y1 = _amplitudeHalf;

        var stride = Math.Max(1, samples.Length / MaxDisplayPoints);
        var addedTrigger = false;
        for (var i = 0; i < samples.Length; i += stride)
        {
            _points.Add(new ObservablePoint(SampleToMs(i, trig, sr), samples[i]));
            if (i >= trig)
            {
                addedTrigger = addedTrigger || i == trig;
            }
        }

        if (!addedTrigger)
        {
            _points.Add(new ObservablePoint(0.0, samples[trig]));
        }

        var last = samples.Length - 1;
        if (last % stride != 0)
        {
            _points.Add(new ObservablePoint(SampleToMs(last, trig, sr), samples[last]));
        }

        if (triggered)
        {
            _triggerTime.Add(new ObservablePoint(0.0, y0));
            _triggerTime.Add(new ObservablePoint(0.0, y1));
            _triggerLevel.Add(new ObservablePoint(t0, level));
            _triggerLevel.Add(new ObservablePoint(t1, level));
            Series[1].IsVisible = true;
            Series[2].IsVisible = true;
        }
        else
        {
            Series[1].IsVisible = false;
            Series[2].IsVisible = false;
        }
    }

    /// <summary>
    /// 波形をクリアします。レンジ設定は残します。
    /// </summary>
    public void Clear()
    {
        _points.Clear();
        _triggerTime.Clear();
        _triggerLevel.Clear();
        Series[1].IsVisible = false;
        Series[2].IsVisible = false;
        ApplyRanges(_amplitudeHalf, _timeSpanMs, _dpiScale);
    }

    /// <summary>
    /// サンプル番号をトリガー相対のミリ秒にします。
    /// </summary>
    private static double SampleToMs(int index, int triggerOffset, int sampleRate) =>
        1000.0 * (index - triggerOffset) / sampleRate;

    /// <summary>
    /// 時間軸ラベルを整形します。
    /// </summary>
    private static string FormatTimeLabel(double value)
    {
        var abs = Math.Abs(value);
        if (abs >= 10.0)
        {
            return $"{value:0}";
        }

        if (abs >= 1.0)
        {
            return $"{value:0.0}";
        }

        return $"{value:0.00}";
    }
}
