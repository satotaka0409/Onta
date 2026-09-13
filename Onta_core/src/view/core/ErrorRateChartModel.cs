using System.Collections.ObjectModel;
using System.Diagnostics;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Onta.Core;
using SkiaSharp;

namespace Onta.View.Core;

/// <summary>
/// 送信側フレーム種別（進捗イベント用。受信エラー率チャートはビタビ/RS・ターボ）。
/// </summary>
public enum ErrorRateFrameKind
{
    Fh,
    Bh,
    Bd
}

/// <summary>
/// 誤り訂正の中間訂正率を表示するチャートです（ビタビ / RS・ターボを色分け）。
/// 横軸 60 秒固定・右端が最新です。値は訂正過程の訂正ビット数から求めた訂正率。
/// </summary>
public sealed class ErrorRateChartModel
{
    private const double WindowSeconds = 60.0;
    private const double DefaultYMaxPercent = 10.0;
    private const double MinSampleIntervalSeconds = 0.02;
    private const double LineBreakGapSeconds = 1.0;
    private readonly ObservableCollection<ObservablePoint> _viterbiValues = [];
    private readonly ObservableCollection<ObservablePoint> _outerValues = [];
    private readonly Stopwatch _clock = new();
    private double _windowEndSeconds;
    private double _lastSampleSeconds = double.NegativeInfinity;
    private CoreEccDecoderKind _lastDecoderKind = CoreEccDecoderKind.Viterbi;
    private double _peakPercent;

    private static readonly SKColor ViterbiColor = new(100, 170, 255);
    private static readonly SKColor OuterColor = new(255, 150, 70);
    private static readonly SKColor AxisColor = new(210, 214, 220);
    private static readonly SKColor GridColor = new(92, 97, 108);

    public ErrorRateChartModel()
    {
        Series =
        [
            new LineSeries<ObservablePoint>
            {
                Values = _viterbiValues,
                Name = "ビタビ",
                Fill = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                Stroke = new SolidColorPaint(ViterbiColor, 2)
            },
            new LineSeries<ObservablePoint>
            {
                Values = _outerValues,
                Name = "RS/ターボ",
                Fill = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                Stroke = new SolidColorPaint(OuterColor, 2)
            }
        ];

        YAxes =
        [
            new Axis
            {
                Name = "訂正率(%)",
                MinLimit = 0,
                MaxLimit = DefaultYMaxPercent,
                MinStep = 2,
                Labeler = value => $"{value:0}",
                TextSize = 8,
                NameTextSize = 8,
                NamePaint = new SolidColorPaint(AxisColor),
                LabelsPaint = new SolidColorPaint(AxisColor),
                SeparatorsPaint = new SolidColorPaint(GridColor) { StrokeThickness = 1 }
            }
        ];

        XAxes =
        [
            new Axis
            {
                Name = null,
                MinLimit = -WindowSeconds,
                MaxLimit = 0,
                MinStep = 10,
                ForceStepToMin = true,
                Labeler = LabelForTime,
                TextSize = 9,
                NameTextSize = 8,
                NamePaint = new SolidColorPaint(AxisColor),
                LabelsPaint = new SolidColorPaint(AxisColor),
                SeparatorsPaint = new SolidColorPaint(GridColor) { StrokeThickness = 1 }
            }
        ];
    }

    public ISeries[] Series { get; }

    public Axis[] XAxes { get; }

    public Axis[] YAxes { get; }

    public double LatestPercent { get; private set; }

    public CoreEccDecoderKind LatestDecoderKind { get; private set; }

    /// <summary>
    /// 訂正率サンプルを追加します（訂正ビット数 / 対象ビット数）。
    /// </summary>
    public void AddSample(double errorRatePercent, CoreEccDecoderKind decoderKind)
    {
        if (!_clock.IsRunning)
        {
            _clock.Start();
        }

        var value = Math.Clamp(errorRatePercent, 0.0, 100.0);
        var t = _clock.Elapsed.TotalSeconds;
        var decoderChanged = decoderKind != _lastDecoderKind;
        if (!decoderChanged
            && t - _lastSampleSeconds < MinSampleIntervalSeconds
            && _lastSampleSeconds >= 0)
        {
            return;
        }

        LatestPercent = value;
        LatestDecoderKind = decoderKind;
        _lastDecoderKind = decoderKind;
        _lastSampleSeconds = t;
        _windowEndSeconds = t;
        var windowStart = t - WindowSeconds;

        var series = IsOuterDecoder(decoderKind) ? _outerValues : _viterbiValues;
        if (series.Count > 0)
        {
            var last = series[^1];
            if (last.X is { } lastX && t - lastX > LineBreakGapSeconds)
            {
                series.Add(new ObservablePoint(t, null));
            }
        }

        series.Add(new ObservablePoint(t, value));
        _peakPercent = Math.Max(_peakPercent, value);

        TrimOldPoints(_viterbiValues, windowStart);
        TrimOldPoints(_outerValues, windowStart);
        RefreshPeakFromVisible();

        var yMax = Math.Clamp(Math.Ceiling(Math.Max(DefaultYMaxPercent, _peakPercent * 1.25) / 2.0) * 2.0, DefaultYMaxPercent, 100.0);
        YAxes[0].MaxLimit = yMax;

        XAxes[0].MinLimit = windowStart;
        XAxes[0].MaxLimit = t;
    }

    private static bool IsOuterDecoder(CoreEccDecoderKind kind) =>
        kind is CoreEccDecoderKind.Turbo or CoreEccDecoderKind.ReedSolomon;

    private void RefreshPeakFromVisible()
    {
        _peakPercent = 0;
        foreach (var p in _viterbiValues)
        {
            if (p.Y is { } y)
            {
                _peakPercent = Math.Max(_peakPercent, y);
            }
        }

        foreach (var p in _outerValues)
        {
            if (p.Y is { } y)
            {
                _peakPercent = Math.Max(_peakPercent, y);
            }
        }
    }

    private static void TrimOldPoints(ObservableCollection<ObservablePoint> series, double windowStart)
    {
        while (series.Count > 0 && series[0].X < windowStart)
        {
            series.RemoveAt(0);
        }
    }

    private string LabelForTime(double value)
    {
        var age = _windowEndSeconds - value;
        var secondsAgo = Math.Round(age / 10.0) * 10.0;
        secondsAgo = Math.Clamp(secondsAgo, 0.0, WindowSeconds);
        if (secondsAgo < 0.5)
        {
            return "0s";
        }

        return $"{-secondsAgo:0}s";
    }

    public void Clear()
    {
        _viterbiValues.Clear();
        _outerValues.Clear();
        _clock.Reset();
        _windowEndSeconds = 0;
        _lastSampleSeconds = double.NegativeInfinity;
        _lastDecoderKind = CoreEccDecoderKind.Viterbi;
        _peakPercent = 0;
        LatestPercent = 0;
        LatestDecoderKind = CoreEccDecoderKind.Viterbi;
        YAxes[0].MaxLimit = DefaultYMaxPercent;
        XAxes[0].MinLimit = -WindowSeconds;
        XAxes[0].MaxLimit = 0;
    }
}
