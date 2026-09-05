using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Onta.View;

/// <summary>
/// LiveCharts2 によるエラー率（%）グラフのモデルです（グレー基調）。
/// </summary>
public sealed class ErrorRateChartModel
{
    private const int MaxSamples = 240;
    private readonly ObservableCollection<ObservableValue> _values = [];

    // 画面のグレーパレットに合わせたチャート色。
    private static readonly SKColor LineColor = new(196, 202, 212);
    private static readonly SKColor AxisColor = new(176, 181, 191);
    private static readonly SKColor GridColor = new(92, 97, 108);

    public ErrorRateChartModel()
    {
        Series =
        [
            new LineSeries<ObservableValue>
            {
                Values = _values,
                Name = "エラー率",
                Fill = new SolidColorPaint(new SKColor(138, 144, 156, 40)),
                GeometrySize = 0,
                LineSmoothness = 0,
                Stroke = new SolidColorPaint(LineColor, 2)
            }
        ];

        YAxes =
        [
            new Axis
            {
                Name = "エラー率 (%)",
                MinLimit = 0,
                MaxLimit = 100,
                Labeler = value => $"{value:0}",
                NamePaint = new SolidColorPaint(AxisColor),
                LabelsPaint = new SolidColorPaint(AxisColor),
                SeparatorsPaint = new SolidColorPaint(GridColor) { StrokeThickness = 1 }
            }
        ];

        XAxes =
        [
            new Axis
            {
                IsVisible = false
            }
        ];
    }

    public ISeries[] Series { get; }

    public Axis[] XAxes { get; }

    public Axis[] YAxes { get; }

    public double LatestPercent { get; private set; }

    /// <summary>
    /// エラー率サンプル（0〜100%）を追加します。
    /// </summary>
    public void AddSample(double errorRatePercent)
    {
        var value = Math.Clamp(errorRatePercent, 0.0, 100.0);
        LatestPercent = value;
        _values.Add(new ObservableValue(value));
        while (_values.Count > MaxSamples)
        {
            _values.RemoveAt(0);
        }
    }

    public void Clear()
    {
        _values.Clear();
        LatestPercent = 0;
    }
}
