using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Onta.Core;
using SkiaSharp;

namespace Onta.View.Core;

/// <summary>
/// 受信IQ点群を表示する散布図モデルです。
/// </summary>
public sealed class IqChartModel
{
    private const int MaxPoints = 4096;
    private const int MaxDisplayPoints = 512;
    private const double DefaultAxisLimit = 2.0;
    private readonly ObservableCollection<ObservablePoint> _points = [];
    private readonly ScatterSeries<ObservablePoint> _series;
    private static readonly SKColor PointColor = new(166, 221, 176);
    private static readonly SKColor GridColor = new(92, 97, 108);

    /// <summary>
    /// IQチャートモデルを初期化します。
    /// </summary>
    public IqChartModel()
    {
        _series = new ScatterSeries<ObservablePoint>
        {
            Values = _points,
            Name = "I-Q",
            GeometrySize = 3,
            Fill = new SolidColorPaint(PointColor),
            Stroke = null
        };

        Series = [_series];

        XAxes =
        [
            new Axis
            {
                Name = null,
                MinLimit = -DefaultAxisLimit,
                MaxLimit = DefaultAxisLimit,
                NamePaint = null,
                LabelsPaint = null,
                SeparatorsPaint = new SolidColorPaint(GridColor) { StrokeThickness = 1 }
            }
        ];

        YAxes =
        [
            new Axis
            {
                Name = null,
                MinLimit = -DefaultAxisLimit,
                MaxLimit = DefaultAxisLimit,
                NamePaint = null,
                LabelsPaint = null,
                SeparatorsPaint = new SolidColorPaint(GridColor) { StrokeThickness = 1 }
            }
        ];
    }

    /// <summary>
    /// 描画系列です。
    /// </summary>
    public ISeries[] Series { get; }

    /// <summary>
    /// X軸設定です。
    /// </summary>
    public Axis[] XAxes { get; }

    /// <summary>
    /// Y軸設定です。
    /// </summary>
    public Axis[] YAxes { get; }

    /// <summary>
    /// 入力サンプルの末尾から最大件数をチャートへ反映します。
    /// </summary>
    /// <param name="samples">描画対象のIQサンプル列。</param>
    public void ReplacePoints(IReadOnlyList<CoreIqSample> samples)
    {
        _points.Clear();
        if (samples.Count == 0)
        {
            ResetAxisLimits();
            return;
        }

        var count = Math.Min(samples.Count, MaxPoints);
        var start = Math.Max(0, samples.Count - count);
        var span = count;
        var stride = Math.Max(1, span / MaxDisplayPoints);
        var maxAbs = DefaultAxisLimit;
        for (var i = start; i < samples.Count; i += stride)
        {
            var s = samples[i];
            _points.Add(new ObservablePoint(s.I, s.Q));
            maxAbs = Math.Max(maxAbs, Math.Abs(s.I));
            maxAbs = Math.Max(maxAbs, Math.Abs(s.Q));
        }

        // 末尾点は間引きで落ちやすいので必ず含める。
        if (stride > 1 && (samples.Count - 1 - start) % stride != 0)
        {
            var last = samples[^1];
            _points.Add(new ObservablePoint(last.I, last.Q));
            maxAbs = Math.Max(maxAbs, Math.Abs(last.I));
            maxAbs = Math.Max(maxAbs, Math.Abs(last.Q));
        }

        var limit = Math.Clamp(Math.Ceiling(maxAbs * 1.15 * 2.0) / 2.0, DefaultAxisLimit, 8.0);
        XAxes[0].MinLimit = -limit;
        XAxes[0].MaxLimit = limit;
        YAxes[0].MinLimit = -limit;
        YAxes[0].MaxLimit = limit;
    }

    /// <summary>
    /// 表示点群をクリアします。
    /// </summary>
    public void Clear()
    {
        _points.Clear();
        ResetAxisLimits();
    }

    private void ResetAxisLimits()
    {
        XAxes[0].MinLimit = -DefaultAxisLimit;
        XAxes[0].MaxLimit = DefaultAxisLimit;
        YAxes[0].MinLimit = -DefaultAxisLimit;
        YAxes[0].MaxLimit = DefaultAxisLimit;
    }
}
