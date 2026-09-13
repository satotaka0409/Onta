using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Onta.Core;
using SkiaSharp;

namespace Onta.View.Core;

/// <summary>
/// 受信FFTスペクトルを描画するためのチャートモデルです。
/// </summary>
public sealed class FftChartModel
{
    private const double AxisPaddingRatio = 0.05;
    private readonly ObservableCollection<ObservablePoint> _leftPoints = [];
    private readonly ObservableCollection<ObservablePoint> _rightPoints = [];
    private static readonly SKColor MonoColor = new(143, 202, 255);
    private static readonly SKColor LeftColor = new(166, 221, 176);
    private static readonly SKColor RightColor = new(255, 182, 120);
    private static readonly SKColor AxisColor = new(176, 181, 191);
    private static readonly SKColor GridColor = new(92, 97, 108);
    private readonly LineSeries<ObservablePoint> _leftSeries;
    private readonly LineSeries<ObservablePoint> _rightSeries;

    public FftChartModel()
    {
        // ColumnSeries は負の X（fftshift）で右端クリップや未描画が起きやすいので Line で描く。
        _leftSeries = new LineSeries<ObservablePoint>
        {
            Values = _leftPoints,
            Name = "L",
            Fill = new SolidColorPaint(MonoColor.WithAlpha(90)),
            Stroke = new SolidColorPaint(MonoColor, 1.5f),
            GeometrySize = 0,
            LineSmoothness = 0
        };

        _rightSeries = new LineSeries<ObservablePoint>
        {
            Values = _rightPoints,
            Name = "R",
            Fill = new SolidColorPaint(RightColor.WithAlpha(70)),
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
                Name = "Bin (0=DC)",
                MinLimit = -64 * (1.0 + AxisPaddingRatio),
                MaxLimit = 64 * (1.0 + AxisPaddingRatio),
                MinStep = 8,
                TextSize = 8,
                NameTextSize = 8,
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
                // 0 dB を縦方向の中央に固定
                MinLimit = -100,
                MaxLimit = 100,
                Labeler = value => $"{value:0}",
                TextSize = 8,
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

    public void ReplacePoints(
        IReadOnlyList<CoreFftSample> leftSamples,
        IReadOnlyList<CoreFftSample> rightSamples,
        bool isStereo)
    {
        _leftPoints.Clear();
        _rightPoints.Clear();

        var maxAbsBin = 1;
        for (var i = 0; i < leftSamples.Count; i++)
        {
            var s = leftSamples[i];
            _leftPoints.Add(new ObservablePoint(s.Bin, s.MagnitudeDb));
            maxAbsBin = Math.Max(maxAbsBin, Math.Abs(s.Bin));
        }

        if (isStereo)
        {
            for (var i = 0; i < rightSamples.Count; i++)
            {
                var s = rightSamples[i];
                _rightPoints.Add(new ObservablePoint(s.Bin, s.MagnitudeDb));
                maxAbsBin = Math.Max(maxAbsBin, Math.Abs(s.Bin));
            }

            _leftSeries.Fill = new SolidColorPaint(LeftColor.WithAlpha(90));
            _leftSeries.Stroke = new SolidColorPaint(LeftColor, 1.5f);
            _rightSeries.IsVisible = true;
        }
        else
        {
            _leftSeries.Fill = new SolidColorPaint(MonoColor.WithAlpha(90));
            _leftSeries.Stroke = new SolidColorPaint(MonoColor, 1.5f);
            _rightSeries.IsVisible = false;
        }

        // 0（DC）を中央にしつつ、右端バーが見切れないよう 5% 余白を取る
        var axisLimit = maxAbsBin * (1.0 + AxisPaddingRatio);
        XAxes[0].MinLimit = -axisLimit;
        XAxes[0].MaxLimit = axisLimit;
    }

    public void Clear()
    {
        _leftPoints.Clear();
        _rightPoints.Clear();
        _leftSeries.Fill = new SolidColorPaint(MonoColor.WithAlpha(90));
        _leftSeries.Stroke = new SolidColorPaint(MonoColor, 1.5f);
        _rightSeries.IsVisible = false;
        var axisLimit = 64 * (1.0 + AxisPaddingRatio);
        XAxes[0].MinLimit = -axisLimit;
        XAxes[0].MaxLimit = axisLimit;
    }
}
