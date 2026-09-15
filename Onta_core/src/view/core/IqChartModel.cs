using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Onta.Core;
using SkiaSharp;

namespace Onta.View.Core;

/// <summary>
/// 受信IQ点群を表示する散布図モデルです。グループ A/B/C/D/E ごとに色分けします。
/// 軸スケールは変調方式の理想コンスタレーション範囲に固定し、外れ値で一瞬縮むのを防ぎます。
/// </summary>
public sealed class IqChartModel
{
    private const int MaxPoints = 4096;
    private const int MaxDisplayPoints = 512;
    private const double DefaultAxisLimit = 1.6;
    private const int GroupCount = 5;

    // A=青, B=緑, C=白, D=オレンジ, E=紫（modulation.mdc）
    private static readonly SKColor[] GroupColors =
    [
        new(80, 140, 255),   // A 青
        new(80, 200, 100),   // B 緑
        new(230, 230, 230),  // C 白
        new(255, 160, 40),   // D オレンジ
        new(170, 100, 255)   // E 紫
    ];

    private static readonly SKColor GridColor = new(92, 97, 108);

    private readonly ObservableCollection<ObservablePoint>[] _groupPoints =
    [
        [],
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
                MinStep = 0.5,
                ForceStepToMin = true,
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
                MinStep = 0.5,
                ForceStepToMin = true,
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
    /// <param name="modulation">軸スケール決定に使う変調方式。</param>
    public void ReplacePoints(
        IReadOnlyList<CoreIqSample> samples,
        ModulationScheme modulation = ModulationScheme.Bpsk)
    {
        for (var g = 0; g < GroupCount; g++)
        {
            _groupPoints[g].Clear();
        }

        // 空フレームでは軸をいじらない（一瞬のリセットで縮んで見えるのを防ぐ）。
        ApplyAxisLimits(IdealAxisLimit(modulation));
        if (samples.Count == 0)
        {
            return;
        }

        var count = Math.Min(samples.Count, MaxPoints);
        var start = Math.Max(0, samples.Count - count);
        var span = count;
        var stride = Math.Max(1, span / MaxDisplayPoints);
        for (var i = start; i < samples.Count; i += stride)
        {
            AddSample(samples[i]);
        }

        // 末尾点は間引きで落ちやすいので必ず含める。
        if (stride > 1 && (samples.Count - 1 - start) % stride != 0)
        {
            AddSample(samples[^1]);
        }
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

        ApplyAxisLimits(DefaultAxisLimit);
    }

    /// <summary>
    /// 単位エネルギー想定の理想コンスタレーション外接半径＋余白です。
    /// </summary>
    private static double IdealAxisLimit(ModulationScheme modulation) =>
        modulation switch
        {
            ModulationScheme.Bpsk => 1.6,
            ModulationScheme.Qpsk => 1.6,
            ModulationScheme.Qam16 => 1.8,
            ModulationScheme.Qam64 => 2.2,
            _ => DefaultAxisLimit
        };

    private void AddSample(CoreIqSample s)
    {
        var group = s.Group < GroupCount ? s.Group : (byte)0;
        _groupPoints[group].Add(new ObservablePoint(s.I, s.Q));
    }

    private void ApplyAxisLimits(double limit)
    {
        if (Math.Abs((XAxes[0].MaxLimit ?? 0) - limit) <= 1e-9
            && Math.Abs((XAxes[0].MinLimit ?? 0) + limit) <= 1e-9)
        {
            return;
        }

        XAxes[0].MinLimit = -limit;
        XAxes[0].MaxLimit = limit;
        YAxes[0].MinLimit = -limit;
        YAxes[0].MaxLimit = limit;
    }
}
