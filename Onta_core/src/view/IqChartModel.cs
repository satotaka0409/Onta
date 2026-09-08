using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Onta.Core;
using SkiaSharp;

namespace Onta.View;

/// <summary>
/// 受信 I-Q コンスタレーション用の LiveCharts2 モデルです。
/// </summary>
public sealed class IqChartModel
{
    private const int MaxPoints = 256;
    private readonly ObservableCollection<ObservablePoint> _points = [];
    private static readonly SKColor PointColor = new(166, 221, 176);
    private static readonly SKColor AxisColor = new(176, 181, 191);
    private static readonly SKColor GridColor = new(92, 97, 108);

    public IqChartModel()
    {
        Series =
        [
            new ScatterSeries<ObservablePoint>
            {
                Values = _points,
                Name = "I-Q",
                GeometrySize = 3,
                Fill = new SolidColorPaint(PointColor),
                Stroke = null
            }
        ];

        XAxes =
        [
            new Axis
            {
                Name = "I",
                MinLimit = -2,
                MaxLimit = 2,
                NamePaint = new SolidColorPaint(AxisColor),
                LabelsPaint = new SolidColorPaint(AxisColor),
                SeparatorsPaint = new SolidColorPaint(GridColor) { StrokeThickness = 1 }
            }
        ];

        YAxes =
        [
            new Axis
            {
                Name = "Q",
                MinLimit = -2,
                MaxLimit = 2,
                NamePaint = new SolidColorPaint(AxisColor),
                LabelsPaint = new SolidColorPaint(AxisColor),
                SeparatorsPaint = new SolidColorPaint(GridColor) { StrokeThickness = 1 }
            }
        ];
    }

    public ISeries[] Series { get; }

    public Axis[] XAxes { get; }

    public Axis[] YAxes { get; }

    public void ReplacePoints(IReadOnlyList<CoreIqSample> samples)
    {
        _points.Clear();
        var count = Math.Min(samples.Count, MaxPoints);
        var start = Math.Max(0, samples.Count - count);
        for (var i = start; i < samples.Count; i++)
        {
            var s = samples[i];
            _points.Add(new ObservablePoint(s.I, s.Q));
        }
    }

    public void Clear() => _points.Clear();
}
