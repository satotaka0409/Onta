using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Onta.Core;
using SkiaSharp;

namespace Onta.View.Core;

/// <summary>
/// 受信IQ点群を表示する散布図モデルです。グループ A〜H ごとに色分けします。
/// 軸スケールは変調方式の理想コンスタレーション範囲に固定し、外れ値で一瞬縮むのを防ぎます。
/// </summary>
public sealed class IqChartModel
{
    private const int MaxPoints = 4096;
    private const int MaxDisplayPoints = 512;
    private const double DefaultAxisLimit = 1.6;
    private const int GroupCount = 8;

    // A=青, B=緑, C=白, D=オレンジ, E=茶, F=紫, G=黄, H=赤（modulation.mdc）
    private static readonly SKColor[] GroupColors =
    [
        new(80, 140, 255),   // A 青
        new(80, 200, 100),   // B 緑
        new(230, 230, 230),  // C 白
        new(255, 160, 40),   // D オレンジ
        new(170, 110, 60),   // E 茶
        new(170, 100, 255),  // F 紫
        new(240, 210, 40),   // G 黄
        new(220, 60, 60)     // H 赤
    ];

    private static readonly string[] GroupLabels = ["A", "B", "C", "D", "E", "F", "G", "H"];

    /// <summary>
    /// メイン画面の I-Q 凡例（GROUP A〜F）。G/H は性能測定のみ。
    /// </summary>
    public static IReadOnlyList<(string Label, byte R, byte G, byte B)> CoreGroupLegendItems { get; } =
        CreateGroupLegendItems(6);

    /// <summary>
    /// 性能測定の I-Q 凡例（GROUP A〜H。G/H は SC-56/64）。
    /// </summary>
    public static IReadOnlyList<(string Label, byte R, byte G, byte B)> PerformanceGroupLegendItems { get; } =
        CreateGroupLegendItems(8);

    /// <summary>
    /// グループ凡例項目を生成します。
    /// </summary>
    /// <param name="count">先頭から何グループまで出すか（6=A〜F、8=A〜H）。</param>
    private static IReadOnlyList<(string Label, byte R, byte G, byte B)> CreateGroupLegendItems(int count)
    {
        var items = new (string Label, byte R, byte G, byte B)[count];
        for (var i = 0; i < count; i++)
        {
            var c = GroupColors[i];
            items[i] = (GroupLabels[i], c.Red, c.Green, c.Blue);
        }

        return items;
    }

    private static readonly SKColor GridColor = new(92, 97, 108);

    private readonly ObservableCollection<ObservablePoint>[] _groupPoints =
    [
        [],
        [],
        [],
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

        var separators = CreateSymmetricSeparators(DefaultAxisLimit);
        XAxes =
        [
            new Axis
            {
                Name = null,
                MinLimit = -DefaultAxisLimit,
                MaxLimit = DefaultAxisLimit,
                MinStep = 0.5,
                ForceStepToMin = true,
                CustomSeparators = separators,
                NamePaint = null,
                LabelsPaint = null,
                TextSize = 0,
                Padding = new LiveChartsCore.Drawing.Padding(0),
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
                CustomSeparators = separators,
                NamePaint = null,
                LabelsPaint = null,
                TextSize = 0,
                Padding = new LiveChartsCore.Drawing.Padding(0),
                SeparatorsPaint = new SolidColorPaint(GridColor) { StrokeThickness = 1 }
            }
        ];
    }

    /// <summary>
    /// 描画系列です（GROUP A〜H）。
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

        if (samples.Count == 0)
        {
            ApplyAxisLimits(IdealAxisLimit(modulation));
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

        // 点追加後に軸を再固定する（LiveCharts がデータ範囲へ追従して原点が寄るのを防ぐ）。
        ApplyAxisLimits(IdealAxisLimit(modulation));
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
    /// <param name="modulation">軸スケールを決める変調方式。</param>
    /// <returns>X/Y 軸の ± 上限。</returns>
    private static double IdealAxisLimit(ModulationScheme modulation) =>
        modulation switch
        {
            ModulationScheme.Bpsk => 1.6,
            ModulationScheme.Qpsk => 1.6,
            ModulationScheme.Qam16 => 1.8,
            ModulationScheme.Qam64 => 2.2,
            ModulationScheme.Qam256 => 2.4,
            _ => DefaultAxisLimit
        };

    /// <summary>
    /// 1 サンプルをグループ別系列へ追加します。
    /// </summary>
    /// <param name="s">追加する IQ サンプル。</param>
    private void AddSample(CoreIqSample s)
    {
        var group = s.Group < GroupCount ? s.Group : (byte)0;
        _groupPoints[group].Add(new ObservablePoint(s.I, s.Q));
    }

    /// <summary>
    /// X/Y 軸の上下限と等間隔目盛を対称に設定します。
    /// </summary>
    /// <param name="limit">原点からの軸上限。</param>
    private void ApplyAxisLimits(double limit)
    {
        var separators = CreateSymmetricSeparators(limit);
        XAxes[0].MinLimit = -limit;
        XAxes[0].MaxLimit = limit;
        XAxes[0].CustomSeparators = separators;
        YAxes[0].MinLimit = -limit;
        YAxes[0].MaxLimit = limit;
        YAxes[0].CustomSeparators = separators;
    }

    /// <summary>
    /// 原点を中央に置く等間隔目盛を作ります。
    /// </summary>
    /// <param name="limit">原点からの軸上限。</param>
    /// <returns>0.5 刻みの対称目盛配列。</returns>
    private static double[] CreateSymmetricSeparators(double limit)
    {
        const double step = 0.5;
        var n = (int)Math.Floor(limit / step);
        var values = new double[(n * 2) + 1];
        for (var i = -n; i <= n; i++)
        {
            values[i + n] = i * step;
        }

        return values;
    }
}
