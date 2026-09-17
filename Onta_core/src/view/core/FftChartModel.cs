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
    /// <summary>FFT 横軸の表示上限（Hz）。SC-48 帯域を覆う。</summary>
    private const double MaxDisplayHz = 14000;
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
                MinLimit = 0,
                MaxLimit = MaxDisplayHz,
                MinStep = 1000,
                TextSize = 8,
                NameTextSize = 8,
                // 軸名と目盛ラベルの間を詰める（既定 NamePadding=5 だと空きが大きい）
                NamePadding = new LiveChartsCore.Drawing.Padding(0, 0, 0, 0),
                Padding = new LiveChartsCore.Drawing.Padding(0, 0, 0, 2),
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
                NamePadding = new LiveChartsCore.Drawing.Padding(0, 0, 0, 0),
                Padding = new LiveChartsCore.Drawing.Padding(0, 0, 2, 0),
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

        XAxes[0].MinLimit = 0;
        XAxes[0].MaxLimit = MaxDisplayHz;

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
        XAxes[0].MinLimit = 0;
        XAxes[0].MaxLimit = MaxDisplayHz;
        YAxes[0].MinLimit = -100;
        YAxes[0].MaxLimit = 0;
    }
}
