using System.Collections.ObjectModel;
using System.Diagnostics;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Onta.View.Core;

/// <summary>
/// ワウフラッター偏差(%) の時系列グラフです（タスクマネージャ風スクロール）。
/// 縦軸は中央 0%、上端 +1%、下端 -1%。L/R 色は FFT と同じです。
/// </summary>
public sealed class WowFlutterChartModel
{
    private const double WindowSeconds = 30.0;
    private const double YLimitPercent = 1.0;
    private const double MinSampleSpacingSeconds = 0.05;
    private readonly ObservableCollection<ObservablePoint> _leftValues = [];
    private readonly ObservableCollection<ObservablePoint> _rightValues = [];
    private readonly Stopwatch _clock = new();
    private bool _leftHasHold;
    private bool _rightHasHold;

    // FFT と同じ L/R 色
    private static readonly SKColor LeftColor = new(166, 221, 176);
    private static readonly SKColor RightColor = new(255, 182, 120);
    private static readonly SKColor AxisColor = new(176, 181, 191);
    private static readonly SKColor GridColor = new(92, 97, 108);

    /// <summary>
    /// ワウフラッター時系列チャートを初期化します。
    /// </summary>
    public WowFlutterChartModel()
    {
        Series =
        [
            new LineSeries<ObservablePoint>
            {
                Values = _leftValues,
                Name = "L",
                Fill = null,
                GeometrySize = 0,
                Stroke = new SolidColorPaint(LeftColor, 1.5f),
                LineSmoothness = 0,
                EnableNullSplitting = false
            },
            new LineSeries<ObservablePoint>
            {
                Values = _rightValues,
                Name = "R",
                Fill = null,
                GeometrySize = 0,
                Stroke = new SolidColorPaint(RightColor, 1.5f),
                LineSmoothness = 0,
                EnableNullSplitting = false
            }
        ];

        YAxes =
        [
            new Axis
            {
                Name = null,
                MinLimit = -YLimitPercent,
                MaxLimit = YLimitPercent,
                MinStep = YLimitPercent,
                ForceStepToMin = true,
                Labeler = value => value switch
                {
                    > 0.5 => "+1",
                    < -0.5 => "-1",
                    _ => "0"
                },
                TextSize = 7,
                NameTextSize = 0,
                NamePaint = null,
                LabelsPaint = new SolidColorPaint(AxisColor),
                SeparatorsPaint = new SolidColorPaint(GridColor) { StrokeThickness = 1 },
                TicksPaint = null,
                SubticksPaint = null,
                Padding = new LiveChartsCore.Drawing.Padding(0, 0, 2, 0)
            }
        ];

        XAxes =
        [
            new Axis
            {
                Name = null,
                IsVisible = false,
                MinLimit = -WindowSeconds,
                MaxLimit = 0,
                ShowSeparatorLines = false,
                LabelsPaint = null,
                SeparatorsPaint = null,
                TicksPaint = null,
                SubticksPaint = null,
                NamePaint = null
            }
        ];
    }

    /// <summary>系列（L / R）。</summary>
    public ISeries[] Series { get; }

    /// <summary>横軸（経過時間）。</summary>
    public Axis[] XAxes { get; }

    /// <summary>縦軸（偏差 %）。</summary>
    public Axis[] YAxes { get; }

    /// <summary>
    /// L/R 偏差サンプルを追加します（単位: %、中央 0）。
    /// </summary>
    /// <param name="leftPercent">左チャネル偏差 %。</param>
    /// <param name="rightPercent">右チャネル偏差 %。</param>
    public void AddSample(double leftPercent, double rightPercent)
    {
        if (!_clock.IsRunning)
        {
            _clock.Start();
        }

        var t = _clock.Elapsed.TotalSeconds;
        if (_leftValues.Count > 0 && _leftValues[^1].X is double lastX && t < lastX + MinSampleSpacingSeconds)
        {
            t = lastX + MinSampleSpacingSeconds;
        }

        AppendOrReplace(_leftValues, ref _leftHasHold, t, ClampY(leftPercent));
        AppendOrReplace(_rightValues, ref _rightHasHold, t, ClampY(rightPercent));
        RefreshAxisAndTrim(t);
    }

    /// <summary>
    /// 右端を現在時刻まで伸ばします（ホールド点のみ更新）。
    /// </summary>
    public void Tick()
    {
        if (!_clock.IsRunning)
        {
            return;
        }

        var t = _clock.Elapsed.TotalSeconds;
        UpdateHoldPoint(_leftValues, ref _leftHasHold, t);
        UpdateHoldPoint(_rightValues, ref _rightHasHold, t);
        RefreshAxisAndTrim(t);
    }

    /// <summary>
    /// グラフを初期状態へ戻します。
    /// </summary>
    public void Clear()
    {
        _leftValues.Clear();
        _rightValues.Clear();
        _clock.Reset();
        _leftHasHold = false;
        _rightHasHold = false;
        XAxes[0].MinLimit = -WindowSeconds;
        XAxes[0].MaxLimit = 0;
        YAxes[0].MinLimit = -YLimitPercent;
        YAxes[0].MaxLimit = YLimitPercent;
    }

    private void RefreshAxisAndTrim(double t)
    {
        var windowStart = t - WindowSeconds;
        TrimOldPoints(_leftValues, windowStart, ref _leftHasHold);
        TrimOldPoints(_rightValues, windowStart, ref _rightHasHold);
        XAxes[0].MinLimit = windowStart;
        XAxes[0].MaxLimit = t;
        YAxes[0].MinLimit = -YLimitPercent;
        YAxes[0].MaxLimit = YLimitPercent;
    }

    private static double ClampY(double percent) =>
        Math.Clamp(percent, -YLimitPercent, YLimitPercent);

    private static void AppendOrReplace(
        ObservableCollection<ObservablePoint> series,
        ref bool hasHold,
        double t,
        double value)
    {
        if (hasHold && series.Count > 0)
        {
            series[^1] = new ObservablePoint(t, value);
            hasHold = false;
            return;
        }

        series.Add(new ObservablePoint(t, value));
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

    private static void TrimOldPoints(
        ObservableCollection<ObservablePoint> series,
        double windowStart,
        ref bool hasHold)
    {
        while (series.Count > 0 && series[0].X < windowStart)
        {
            series.RemoveAt(0);
            if (series.Count == 0)
            {
                hasHold = false;
            }
        }

        if (hasHold && series.Count < 2)
        {
            hasHold = false;
        }
    }
}
