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
/// ターボは 1024B 単位など疎でないサンプルを折れ線で表示します。
/// </summary>
public sealed class ErrorRateChartModel
{
    private const double WindowSeconds = 60.0;
    private const double DefaultYMaxPercent = 10.0;
    /// <summary>同一 UI ティックに複数点が来たときの横方向最小間隔（秒）。</summary>
    private const double MinSampleSpacingSeconds = 0.08;
    private readonly ObservableCollection<ObservablePoint> _viterbiValues = [];
    private readonly ObservableCollection<ObservablePoint> _outerValues = [];
    private readonly Stopwatch _clock = new();
    private double _windowEndSeconds;
    private bool _viterbiHasHold;
    private bool _outerHasHold;
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
                GeometrySize = 4,
                GeometryFill = new SolidColorPaint(ViterbiColor),
                GeometryStroke = null,
                Stroke = new SolidColorPaint(ViterbiColor, 2),
                LineSmoothness = 0,
                EnableNullSplitting = false
            },
            new LineSeries<ObservablePoint>
            {
                Values = _outerValues,
                Name = "RS/ターボ",
                Fill = null,
                GeometrySize = 4,
                GeometryFill = new SolidColorPaint(OuterColor),
                GeometryStroke = null,
                Stroke = new SolidColorPaint(OuterColor, 2),
                LineSmoothness = 0,
                EnableNullSplitting = false
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
        var outer = IsOuterDecoder(decoderKind);
        var series = outer ? _outerValues : _viterbiValues;
        ref var hasHold = ref outer ? ref _outerHasHold : ref _viterbiHasHold;

        // コアが一気に複数単位を公開したとき、同一時刻に潰さない
        if (series.Count > 0 && series[^1].X is double lastX && t < lastX + MinSampleSpacingSeconds)
        {
            t = lastX + MinSampleSpacingSeconds;
        }

        // ホールド点を実サンプルに置き換える（一度消すと線が 0 へ落ちて隙間になる）
        if (hasHold && series.Count > 0)
        {
            series[^1] = new ObservablePoint(t, value);
            hasHold = false;
        }
        else
        {
            series.Add(new ObservablePoint(t, value));
        }

        LatestPercent = value;
        LatestDecoderKind = decoderKind;
        _windowEndSeconds = t;
        RefreshAxisAndTrim(t);
    }

    /// <summary>
    /// 右端を「今」まで伸ばすため、末尾ホールド点だけを更新します。
    /// 実サンプルの X は変更しません。
    /// </summary>
    public void Tick()
    {
        if (!_clock.IsRunning)
        {
            return;
        }

        var t = _clock.Elapsed.TotalSeconds;
        _windowEndSeconds = t;
        UpdateHoldPoint(_viterbiValues, ref _viterbiHasHold, t);
        UpdateHoldPoint(_outerValues, ref _outerHasHold, t);
        RefreshAxisAndTrim(t);
    }

    private void RefreshAxisAndTrim(double t)
    {
        var windowStart = t - WindowSeconds;
        TrimOldPoints(_viterbiValues, windowStart, ref _viterbiHasHold);
        TrimOldPoints(_outerValues, windowStart, ref _outerHasHold);
        RefreshPeakFromVisible();

        var yMax = Math.Clamp(
            Math.Ceiling(Math.Max(DefaultYMaxPercent, _peakPercent * 1.25) / 2.0) * 2.0,
            DefaultYMaxPercent,
            100.0);
        YAxes[0].MaxLimit = yMax;
        XAxes[0].MinLimit = windowStart;
        XAxes[0].MaxLimit = t;
    }

    private static void UpdateHoldPoint(
        ObservableCollection<ObservablePoint> series,
        ref bool hasHold,
        double t)
    {
        if (series.Count == 0)
        {
            return;
        }

        // 実サンプル（ホールド中ならその直前）の Y を今まで水平延長
        var real = hasHold && series.Count >= 2 ? series[^2] : series[^1];
        if (real.Y is not { } y || real.X is not { } realX)
        {
            return;
        }

        if (t - realX < 1e-3)
        {
            return;
        }

        if (hasHold)
        {
            series[^1] = new ObservablePoint(t, y);
        }
        else
        {
            series.Add(new ObservablePoint(t, y));
            hasHold = true;
        }
    }

    private static bool IsOuterDecoder(CoreEccDecoderKind kind) =>
        kind is CoreEccDecoderKind.Turbo or CoreEccDecoderKind.ReedSolomon;

    private void RefreshPeakFromVisible()
    {
        _peakPercent = 0;
        AccumulatePeak(_viterbiValues);
        AccumulatePeak(_outerValues);
    }

    private void AccumulatePeak(ObservableCollection<ObservablePoint> series)
    {
        foreach (var p in series)
        {
            if (p.Y is { } y)
            {
                _peakPercent = Math.Max(_peakPercent, y);
            }
        }
    }

    private static void TrimOldPoints(
        ObservableCollection<ObservablePoint> series,
        double windowStart,
        ref bool hasHold)
    {
        while (series.Count > 0 && series[0].X < windowStart)
        {
            series.RemoveAt(0);
            // 先頭を消したらホールドフラグは「末尾がホールドか」で再判定する
            if (series.Count == 0)
            {
                hasHold = false;
            }
        }

        // 実点1つ + ホールド1つ、または実点のみ、の形を維持。壊れたらホールド解除。
        if (hasHold && series.Count < 2)
        {
            hasHold = series.Count == 0 ? false : hasHold && series.Count >= 1;
            if (series.Count == 1)
            {
                // 残った1点がホールドだけになった場合は実点扱いへ
                hasHold = false;
            }
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
        _viterbiHasHold = false;
        _outerHasHold = false;
        _peakPercent = 0;
        LatestPercent = 0;
        LatestDecoderKind = CoreEccDecoderKind.Viterbi;
        YAxes[0].MaxLimit = DefaultYMaxPercent;
        XAxes[0].MinLimit = -WindowSeconds;
        XAxes[0].MaxLimit = 0;
    }
}
