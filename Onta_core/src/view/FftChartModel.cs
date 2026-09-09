using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Onta.Core;
using SkiaSharp;

namespace Onta.View;

/// <summary>
/// 受信 FFT スペクトル表示用の LiveCharts2 モデルです。
/// </summary>
public sealed class FftChartModel
{
    private readonly ObservableCollection<ObservablePoint> _leftPoints = [];
    private readonly ObservableCollection<ObservablePoint> _rightPoints = [];
    private static readonly SKColor MonoColor = new(143, 202, 255);
    private static readonly SKColor LeftColor = new(166, 221, 176);
    private static readonly SKColor RightColor = new(255, 182, 120);
    private static readonly SKColor AxisColor = new(176, 181, 191);
    private static readonly SKColor GridColor = new(92, 97, 108);
    private readonly ColumnSeries<ObservablePoint> _leftSeries;
    private readonly ColumnSeries<ObservablePoint> _rightSeries;

    public FftChartModel()
    {
        _leftSeries = new ColumnSeries<ObservablePoint>
        {
            Values = _leftPoints,
            Name = "L",
            Fill = new SolidColorPaint(MonoColor),
            Stroke = null,
            MaxBarWidth = 2,
            Padding = 0
        };

        _rightSeries = new ColumnSeries<ObservablePoint>
        {
            Values = _rightPoints,
            Name = "R",
            Fill = new SolidColorPaint(RightColor),
            Stroke = null,
            MaxBarWidth = 2,
            Padding = 0
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
                Name = "Bin",
                MinStep = 8,
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
                MinLimit = -120,
                MaxLimit = 20,
                Labeler = value => $"{value:0}",
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

        for (var i = 0; i < leftSamples.Count; i++)
        {
            var s = leftSamples[i];
            _leftPoints.Add(new ObservablePoint(s.Bin, s.MagnitudeDb));
        }

        if (isStereo)
        {
            for (var i = 0; i < rightSamples.Count; i++)
            {
                var s = rightSamples[i];
                _rightPoints.Add(new ObservablePoint(s.Bin, s.MagnitudeDb));
            }

            _leftSeries.Fill = new SolidColorPaint(LeftColor);
            _rightSeries.IsVisible = true;
        }
        else
        {
            _leftSeries.Fill = new SolidColorPaint(MonoColor);
            _rightSeries.IsVisible = false;
        }
    }

    public void Clear()
    {
        _leftPoints.Clear();
        _rightPoints.Clear();
        _leftSeries.Fill = new SolidColorPaint(MonoColor);
        _rightSeries.IsVisible = false;
    }
}
