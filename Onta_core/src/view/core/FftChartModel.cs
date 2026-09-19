using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Onta.Core;
using SkiaSharp;

namespace Onta.View.Core;

/// <summary>
/// 送受信 FFT スペクトルを描画するためのチャートモデルです（横軸=Hz）。
/// </summary>
public sealed class FftChartModel
{
    /// <summary>FFT 横軸の表示上限（Hz）。</summary>
    private const double MaxDisplayHz = 20000;
    private readonly ObservableCollection<ObservablePoint> _leftPoints = [];
    private readonly ObservableCollection<ObservablePoint> _rightPoints = [];
    private static readonly SKColor LeftColor = new(166, 221, 176);
    private static readonly SKColor RightColor = new(255, 182, 120);
    private static readonly SKColor AxisColor = new(176, 181, 191);
    private static readonly SKColor GridColor = new(92, 97, 108);
    private readonly LineSeries<ObservablePoint> _leftSeries;
    private readonly LineSeries<ObservablePoint> _rightSeries;

    public FftChartModel()
    {
        _leftSeries = new LineSeries<ObservablePoint>
        {
            Values = _leftPoints,
            Name = "L",
            Fill = null,
            Stroke = new SolidColorPaint(LeftColor, 1.5f),
            GeometrySize = 0,
            LineSmoothness = 0
        };

        _rightSeries = new LineSeries<ObservablePoint>
        {
            Values = _rightPoints,
            Name = "R",
            Fill = null,
            Stroke = new SolidColorPaint(RightColor, 1.5f),
            GeometrySize = 0,
            LineSmoothness = 0,
            IsVisible = false
        };

        Series =
        [
            _leftSeries,
            _rightSeries
        ];

        XAxes =
        [
            new Axis
            {
                Name = "Frequency (Hz)",
                MinLimit = XMinLimit,
                MaxLimit = XMaxLimit,
                MinStep = 2000,
                ForceStepToMin = true,
                CustomSeparators = [0, 2000, 4000, 6000, 8000, 10000, 12000, 14000, 16000, 18000, 20000],
                Labeler = value => value >= 1000 ? $"{value / 1000:0}k" : $"{value:0}",
                TextSize = 9,
                NameTextSize = 9,
                NamePadding = new LiveChartsCore.Drawing.Padding(0, 2, 0, 0),
                Padding = new LiveChartsCore.Drawing.Padding(0, 0, 0, 0),
                NamePaint = new SolidColorPaint(AxisColor),
                LabelsPaint = new SolidColorPaint(AxisColor),
                SeparatorsPaint = new SolidColorPaint(GridColor) { StrokeThickness = 1 }
            }
        ];

        YAxes =
        [
            new Axis
            {
                Name = "Magnitude (dB)",
                MinLimit = -100,
                MaxLimit = 0,
                MinStep = 20,
                ForceStepToMin = true,
                // 下限 -100 dB を必ず含め、短い高さでも見切れないよう 20 dB 刻み
                CustomSeparators = [-100, -80, -60, -40, -20, 0],
                Labeler = value => $"{value:0}",
                LabelsAlignment = Align.Middle,
                TextSize = 9,
                NameTextSize = 9,
                NamePadding = new LiveChartsCore.Drawing.Padding(0, 0, 0, 0),
                Padding = new LiveChartsCore.Drawing.Padding(0, 0, 0, 0),
                NamePaint = new SolidColorPaint(AxisColor),
                LabelsPaint = new SolidColorPaint(AxisColor),
                SeparatorsPaint = new SolidColorPaint(GridColor) { StrokeThickness = 1 },
                TicksPaint = null,
                SubticksPaint = null
            }
        ];
    }

    /// <summary>横軸の表示下限（Hz）。</summary>
    private const double XMinLimit = 0;

    /// <summary>横軸の表示上限（Hz）。</summary>
    private const double XMaxLimit = MaxDisplayHz;

    /// <summary>
    /// 性能測定 FFT 用の描画余白です。
    /// 横軸数値はパネル側 Canvas（プロット全幅の等間隔）。LC 内ラベルは短い高さで消えるため使わない。
    /// </summary>
    public static Margin CreateDrawMargin() =>
        new(DrawMarginLeft, DrawMarginTop, DrawMarginRight, DrawMarginBottom);

    /// <summary>性能測定 FFT の DrawMargin 左。</summary>
    public const float DrawMarginLeft = 0f;

    /// <summary>性能測定 FFT の DrawMargin 上。</summary>
    public const float DrawMarginTop = 0f;

    /// <summary>性能測定 FFT の DrawMargin 右。</summary>
    public const float DrawMarginRight = 0f;

    /// <summary>性能測定 FFT の DrawMargin 下（0＝プロット全高。周波数ラベルは外部）。</summary>
    public const float DrawMarginBottom = 0f;

    /// <summary>
    /// メイン画面向け：軸名・目盛り分の描画余白です。
    /// </summary>
    public static Margin CreateDrawMarginWithFrequencyLabels() =>
        new(60, 14, 12, 40);

    public ISeries[] Series { get; }

    public Axis[] XAxes { get; }

    public Axis[] YAxes { get; }

    public void ReplacePoints(
        IReadOnlyList<CoreFftSample> leftSamples,
        IReadOnlyList<CoreFftSample> rightSamples,
        bool isStereo)
    {
        _leftPoints.Clear();
        _rightPoints.Clear();

        for (var i = 0; i < leftSamples.Count; i++)
        {
            var s = leftSamples[i];
            if (s.FrequencyHz > MaxDisplayHz)
            {
                continue;
            }

            _leftPoints.Add(new ObservablePoint(s.FrequencyHz, s.MagnitudeDb));
        }

        if (isStereo)
        {
            for (var i = 0; i < rightSamples.Count; i++)
            {
                var s = rightSamples[i];
                if (s.FrequencyHz > MaxDisplayHz)
                {
                    continue;
                }

                _rightPoints.Add(new ObservablePoint(s.FrequencyHz, s.MagnitudeDb));
            }

            _leftSeries.Stroke = new SolidColorPaint(LeftColor, 1.5f);
            _rightSeries.IsVisible = true;
        }
        else
        {
            // ヘッダー／モノラルは L のみ（L 色）。R 系列は出さない。
            _leftSeries.Stroke = new SolidColorPaint(LeftColor, 1.5f);
            _rightSeries.IsVisible = false;
        }

        XAxes[0].MinLimit = XMinLimit;
        XAxes[0].MaxLimit = XMaxLimit;

        // 0 dB = フルスケール正弦波。ピーク追従で上限が縮むと縦軸が不自然になる。
        YAxes[0].MinLimit = -100;
        YAxes[0].MaxLimit = 0;
    }

    public void Clear()
    {
        _leftPoints.Clear();
        _rightPoints.Clear();
        _leftSeries.Stroke = new SolidColorPaint(LeftColor, 1.5f);
        _rightSeries.IsVisible = false;
        XAxes[0].MinLimit = XMinLimit;
        XAxes[0].MaxLimit = XMaxLimit;
        YAxes[0].MinLimit = -100;
        YAxes[0].MaxLimit = 0;
    }
}
