using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Onta.Core;
using SkiaSharp;

namespace Onta.View.Core;

/// <summary>
/// 受信IQ点群を表示する散布図モデルです。グループ A/B/C/D ごとに色分けします。
/// </summary>
public sealed class IqChartModel
{
    private const int MaxPoints = 4096;
    private const int MaxDisplayPoints = 512;
    private const double DefaultAxisLimit = 2.0;
    private const int GroupCount = 4;

    // A=青, B=緑, C=白, D=オレンジ（modulation.mdc）
    private static readonly SKColor[] GroupColors =
    [
        new(80, 140, 255),   // A 青
        new(80, 200, 100),   // B 緑
        new(230, 230, 230),  // C 白
        new(255, 160, 40)    // D オレンジ
    ];

    private static readonly SKColor GridColor = new(92, 97, 108);

    private readonly ObservableCollection<ObservablePoint>[] _groupPoints =
    [
        [],
        [],
        [],
        []
    ];

    private readonly ScatterSeries<ObservablePoint>[] _series;

    /// <summary>
    /// IQチャートモデルを初期化します。
    /// </summary>
    public IqChartModel()
    {
        _series = new ScatterSeries<ObservablePoint>[GroupCount];
        for (var g = 0; g < GroupCount; g++)
        {
            _series[g] = new ScatterSeries<ObservablePoint>
            {
                Values = _groupPoints[g],
                Name = $"GROUP {(char)('A' + g)}",
                GeometrySize = 3,
                Fill = new SolidColorPaint(GroupColors[g]),
                Stroke = null
            };
        }

        Series = _series;

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
    /// 描画系列です（GROUP A〜D）。
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
        for (var g = 0; g < GroupCount; g++)
        {
            _groupPoints[g].Clear();
        }

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
            maxAbs = Math.Max(maxAbs, AddSample(samples[i]));
        }

        // 末尾点は間引きで落ちやすいので必ず含める。
        if (stride > 1 && (samples.Count - 1 - start) % stride != 0)
        {
            maxAbs = Math.Max(maxAbs, AddSample(samples[^1]));
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
        for (var g = 0; g < GroupCount; g++)
        {
            _groupPoints[g].Clear();
        }

        ResetAxisLimits();
    }

    private double AddSample(CoreIqSample s)
    {
        var group = s.Group < GroupCount ? s.Group : (byte)0;
        _groupPoints[group].Add(new ObservablePoint(s.I, s.Q));
        return Math.Max(Math.Abs(s.I), Math.Abs(s.Q));
    }

    private void ResetAxisLimits()
    {
        XAxes[0].MinLimit = -DefaultAxisLimit;
        XAxes[0].MaxLimit = DefaultAxisLimit;
        YAxes[0].MinLimit = -DefaultAxisLimit;
        YAxes[0].MaxLimit = DefaultAxisLimit;
    }
}
