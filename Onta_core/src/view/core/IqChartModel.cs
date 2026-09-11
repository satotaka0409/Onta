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
    private const int MaxPoints = 256;
    private readonly ObservableCollection<ObservablePoint> _points = [];
    private static readonly SKColor PointColor = new(166, 221, 176);
    private static readonly SKColor GridColor = new(92, 97, 108);

    /// <summary>
    /// IQチャートモデルを初期化します。
    /// </summary>
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
                Name = null,
                MinLimit = -2,
                MaxLimit = 2,
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
                MinLimit = -2,
                MaxLimit = 2,
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
        var count = Math.Min(samples.Count, MaxPoints);
        var start = Math.Max(0, samples.Count - count);
        for (var i = start; i < samples.Count; i++)
        {
            var s = samples[i];
            _points.Add(new ObservablePoint(s.I, s.Q));
        }
    }

    /// <summary>
    /// 表示点群をクリアします。
    /// </summary>
    public void Clear() => _points.Clear();
}




