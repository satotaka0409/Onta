using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
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
        _leftSeries = new LineSeries<ObservablePoint>
        {
            Values = _leftPoints,
            Name = "L",
            Fill = null,
            Stroke = new SolidColorPaint(MonoColor, 1.5f),
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
                MinLimit = 0,
                MaxLimit = 8000 * (1.0 + AxisPaddingRatio),
                MinStep = 500,
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
                MinLimit = -100,
                MaxLimit = 0,
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

        var maxHz = 1000;
        var peakDb = -80.0;
        for (var i = 0; i < leftSamples.Count; i++)
        {
            var s = leftSamples[i];
            _leftPoints.Add(new ObservablePoint(s.Bin, s.MagnitudeDb));
            maxHz = Math.Max(maxHz, s.Bin);
            peakDb = Math.Max(peakDb, s.MagnitudeDb);
        }

        if (isStereo)
        {
            for (var i = 0; i < rightSamples.Count; i++)
            {
                var s = rightSamples[i];
                _rightPoints.Add(new ObservablePoint(s.Bin, s.MagnitudeDb));
                maxHz = Math.Max(maxHz, s.Bin);
                peakDb = Math.Max(peakDb, s.MagnitudeDb);
            }

            _leftSeries.Stroke = new SolidColorPaint(LeftColor, 1.5f);
            _rightSeries.IsVisible = true;
        }
        else
        {
            _leftSeries.Stroke = new SolidColorPaint(MonoColor, 1.5f);
            _rightSeries.IsVisible = false;
        }

        // Onta のキャリア帯域が見やすいよう、上限はデータに合わせて拡張（最低 8kHz）
        var xMax = Math.Max(8000, maxHz) * (1.0 + AxisPaddingRatio);
        XAxes[0].MinLimit = 0;
        XAxes[0].MaxLimit = xMax;

        YAxes[0].MinLimit = -100;
        YAxes[0].MaxLimit = Math.Clamp(Math.Ceiling((peakDb + 6.0) / 5.0) * 5.0, -20, 20);
    }

    public void Clear()
    {
        _leftPoints.Clear();
        _rightPoints.Clear();
        _leftSeries.Stroke = new SolidColorPaint(MonoColor, 1.5f);
        _rightSeries.IsVisible = false;
        XAxes[0].MinLimit = 0;
        XAxes[0].MaxLimit = 8000 * (1.0 + AxisPaddingRatio);
        YAxes[0].MinLimit = -100;
        YAxes[0].MaxLimit = 0;
    }
}
