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
/// 波形本体は LiveCharts（HiDPI で Y がずれる）ではなく、外部 Canvas へ渡す点列で描画します。
/// </summary>
public sealed class OscilloscopeChartModel
{
    private const int MaxDisplayPoints = 640;
    private static readonly SKColor WaveColor = new(166, 221, 176);
    private static readonly SKColor GridColor = new(92, 97, 108);
    private static readonly SKColor TriggerColor = new(255, 214, 80);

    private readonly ObservableCollection<ObservablePoint> _points = [];
    private readonly ObservableCollection<ObservablePoint> _triggerTime = [];
    private readonly ObservableCollection<ObservablePoint> _triggerLevel = [];
    private readonly LineSeries<ObservablePoint> _series;
    private double _amplitudeHalf = 1.0;
    private double _timeSpanMs = 20.0;
    private double[] _displayT = [];
    private double[] _displayY = [];
    private bool _triggered;

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
            EnableNullSplitting = false,
            IsVisible = false
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
                Name = "Zero",
                Fill = null,
                Stroke = new SolidColorPaint(TriggerColor, 1.0f),
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
                TextSize = 0,
                NameTextSize = 0,
                NamePadding = new LiveChartsCore.Drawing.Padding(0, 0, 0, 0),
                Padding = new LiveChartsCore.Drawing.Padding(0, 0, 0, 2),
                NamePaint = null,
                LabelsPaint = null,
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
                CustomSeparators = null,
                SeparatorsAtCenter = false,
                TicksAtCenter = false,
                LabelsPaint = null,
                TextSize = 0,
                NameTextSize = 0,
                NamePadding = new LiveChartsCore.Drawing.Padding(0, 0, 0, 0),
                Padding = new LiveChartsCore.Drawing.Padding(0, 0, 0, 0),
                NamePaint = null,
                SeparatorsPaint = null
            }
        ];
    }

    public ISeries[] Series { get; }

    public Axis[] XAxes { get; }

    public Axis[] YAxes { get; }

    /// <summary>現在の片振幅レンジです。</summary>
    public double AmplitudeHalf => _amplitudeHalf;

    /// <summary>表示窓の開始時刻（ms、トリガー相対）です。</summary>
    public double TimeStartMs => -_timeSpanMs * OscilloscopeTrigger.PreTriggerRatio;

    /// <summary>表示窓の終了時刻（ms、トリガー相対）です。</summary>
    public double TimeEndMs => _timeSpanMs + TimeStartMs;

    /// <summary>起立トリガーを検出できたかです。</summary>
    public bool HasTriggerMarker => _triggered;

    /// <summary>外部 Canvas 描画用の時刻列（ms）です。</summary>
    public IReadOnlyList<double> DisplayTimes => _displayT;

    /// <summary>外部 Canvas 描画用の振幅列です。</summary>
    public IReadOnlyList<double> DisplayAmplitudes => _displayY;

    /// <summary>
    /// オシロ用の描画余白です（左=外部振幅ラベル分。上下右は最小）。
    /// </summary>
    /// <returns>LiveCharts 用 Margin（左 40、上下右 8）。</returns>
    public static Margin CreateDrawMargin() => new(40, 8, 8, 8);

    /// <summary>
    /// スライダーインデックスから片振幅レンジを返します。
    /// </summary>
    /// <param name="index">スライダー位置。</param>
    /// <returns>片振幅（±この値）。</returns>
    public static double AmplitudeHalfFromIndex(int index) =>
        AmplitudeRangeHalf[Math.Clamp(index, 0, AmplitudeRangeHalf.Length - 1)];

    /// <summary>
    /// スライダーインデックスから横軸幅（ms）を返します。
    /// </summary>
    /// <param name="index">スライダー位置。</param>
    /// <returns>表示幅（ms）。</returns>
    public static double TimeSpanMsFromIndex(int index) =>
        TimeSpanMsSteps[Math.Clamp(index, 0, TimeSpanMsSteps.Length - 1)];

    /// <summary>
    /// 振幅レンジの表示文言です。
    /// </summary>
    /// <param name="half">片振幅。</param>
    /// <returns>「±0.5」形式の文字列。</returns>
    public static string FormatAmplitudeRange(double half) =>
        half >= 1.0 ? "±1.0" : $"±{half:0.0}";

    /// <summary>
    /// 時間レンジの表示文言です。
    /// </summary>
    /// <param name="ms">表示幅（ms）。</param>
    /// <returns>「N ms」形式の文字列。</returns>
    public static string FormatTimeSpan(double ms) => $"{ms:0} ms";

    /// <summary>
    /// 縦軸・横軸の表示レンジを設定します。センター 0、上下 ±amplitudeHalf。
    /// </summary>
    /// <param name="amplitudeHalf">片振幅（±この値）。</param>
    /// <param name="timeSpanMs">表示幅（ms）。</param>
    public void ApplyRanges(double amplitudeHalf, double timeSpanMs)
    {
        _amplitudeHalf = Math.Max(0.05, amplitudeHalf);
        _timeSpanMs = Math.Max(0.2, timeSpanMs);

        YAxes[0].MinLimit = -_amplitudeHalf;
        YAxes[0].MaxLimit = _amplitudeHalf;
        YAxes[0].MinStep = _amplitudeHalf / 2.0;
        YAxes[0].ForceStepToMin = true;
        YAxes[0].SeparatorsAtCenter = false;
        YAxes[0].TicksAtCenter = false;
        YAxes[0].LabelsPaint = null;
        YAxes[0].TextSize = 0;
        YAxes[0].SeparatorsPaint = null;
        YAxes[0].CustomSeparators = null;

        var t0 = TimeStartMs;
        var t1 = TimeEndMs;
        XAxes[0].MinLimit = t0;
        XAxes[0].MaxLimit = t1;
        XAxes[0].LabelsPaint = null;
        XAxes[0].TextSize = 0;
        XAxes[0].Labeler = FormatTimeLabel;
        var step = NiceTimeStep(_timeSpanMs);
        XAxes[0].MinStep = step;
        XAxes[0].ForceStepToMin = true;
        XAxes[0].SeparatorsAtCenter = false;
        XAxes[0].TicksAtCenter = false;
        XAxes[0].CustomSeparators = BuildTimeSeparators(t0, t1, step);
    }

    /// <summary>
    /// 外部縦軸ラベル用の文言（上→下：+amp … 0 … −amp）です。
    /// </summary>
    /// <param name="amplitudeHalf">片振幅。</param>
    /// <returns>5 本分のラベル文字列。</returns>
    public static string[] FormatAmplitudeTickLabels(double amplitudeHalf)
    {
        var amp = Math.Max(0.05, amplitudeHalf);
        var decimals = amp < 0.15 ? "0.00" : "0.0";
        return
        [
            amp.ToString(decimals),
            (amp / 2.0).ToString(decimals),
            0.0.ToString(decimals),
            (-amp / 2.0).ToString(decimals),
            (-amp).ToString(decimals)
        ];
    }

    /// <summary>
    /// 表示幅に対して読みやすい時間ステップ（ms）を選びます。
    /// </summary>
    /// <param name="spanMs">表示幅（ms）。</param>
    /// <returns>目盛り間隔（ms）。</returns>
    private static double NiceTimeStep(double spanMs)
    {
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
    /// <param name="t0">開始時刻（ms）。</param>
    /// <param name="t1">終了時刻（ms）。</param>
    /// <param name="step">刻み（ms）。</param>
    /// <returns>目盛り位置配列。</returns>
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
        ApplyRanges(_amplitudeHalf, _timeSpanMs);
        _points.Clear();
        _triggerTime.Clear();
        _triggerLevel.Clear();
        Series[0].IsVisible = false;
        Series[1].IsVisible = false;
        Series[2].IsVisible = false;
        _ = level;

        if (samples.IsEmpty || sampleRate <= 0)
        {
            _displayT = [];
            _displayY = [];
            _triggered = false;
            return;
        }

        var sr = Math.Max(1, sampleRate);
        var trig = Math.Clamp(triggerOffset, 0, samples.Length - 1);
        var stride = Math.Max(1, samples.Length / MaxDisplayPoints);
        var times = new List<double>(MaxDisplayPoints + 2);
        var amps = new List<double>(MaxDisplayPoints + 2);
        var addedTrigger = false;
        for (var i = 0; i < samples.Length; i += stride)
        {
            times.Add(SampleToMs(i, trig, sr));
            amps.Add(samples[i]);
            if (i == trig)
            {
                addedTrigger = true;
            }
        }

        var last = samples.Length - 1;
        if (last % stride != 0)
        {
            times.Add(SampleToMs(last, trig, sr));
            amps.Add(samples[last]);
        }

        if (!addedTrigger)
        {
            var insertAt = times.Count;
            for (var i = 0; i < times.Count; i++)
            {
                if (times[i] >= 0.0)
                {
                    insertAt = i;
                    break;
                }
            }

            times.Insert(insertAt, 0.0);
            amps.Insert(insertAt, samples[trig]);
        }

        _displayT = times.ToArray();
        _displayY = amps.ToArray();
        _triggered = triggered;
    }

    /// <summary>
    /// 波形をクリアします。レンジ設定は残します。
    /// </summary>
    public void Clear()
    {
        _points.Clear();
        _triggerTime.Clear();
        _triggerLevel.Clear();
        _displayT = [];
        _displayY = [];
        _triggered = false;
        Series[0].IsVisible = false;
        Series[1].IsVisible = false;
        Series[2].IsVisible = false;
        ApplyRanges(_amplitudeHalf, _timeSpanMs);
    }

    /// <summary>
    /// サンプル番号をトリガー相対のミリ秒にします。
    /// </summary>
    /// <param name="index">サンプル番号。</param>
    /// <param name="triggerOffset">トリガー位置（サンプル）。</param>
    /// <param name="sampleRate">サンプリング周波数。</param>
    /// <returns>トリガー相対時刻（ms）。</returns>
    private static double SampleToMs(int index, int triggerOffset, int sampleRate) =>
        1000.0 * (index - triggerOffset) / sampleRate;

    /// <summary>
    /// 時間軸ラベルを整形します。
    /// </summary>
    /// <param name="value">時刻（ms）。</param>
    /// <returns>表示用文字列。</returns>
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
