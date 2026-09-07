using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Onta.View;

public enum ErrorRateFrameKind
{
    Fh,
    Bh,
    Bd
}

/// <summary>
/// LiveCharts2 によるエラー率（%）グラフのモデルです（グレー基調）。
/// </summary>
public sealed class ErrorRateChartModel
{
    private const int MaxSamples = 240;
    private readonly ObservableCollection<ObservablePoint> _fhValues = [];
    private readonly ObservableCollection<ObservablePoint> _bhValues = [];
    private readonly ObservableCollection<ObservablePoint> _bdValues = [];
    private readonly List<ErrorRateFrameKind> _kinds = [];
    private int _firstSampleIndex;
    private int _nextSampleIndex;

    // FH/BH/BD を見分けやすくする色分け。
    private static readonly SKColor FhColor = new(186, 215, 255);
    private static readonly SKColor BhColor = new(240, 204, 140);
    private static readonly SKColor BdColor = new(166, 221, 176);
    private static readonly SKColor AxisColor = new(176, 181, 191);
    private static readonly SKColor GridColor = new(92, 97, 108);

    public ErrorRateChartModel()
    {
        Series =
        [
            new LineSeries<ObservablePoint>
            {
                Values = _fhValues,
                Name = "FH",
                Fill = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                Stroke = new SolidColorPaint(FhColor, 2)
            },
            new LineSeries<ObservablePoint>
            {
                Values = _bhValues,
                Name = "BH",
                Fill = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                Stroke = new SolidColorPaint(BhColor, 2)
            },
            new LineSeries<ObservablePoint>
            {
                Values = _bdValues,
                Name = "BD",
                Fill = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                Stroke = new SolidColorPaint(BdColor, 2)
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
                Name = "属性",
                Labeler = LabelForSample,
                MinStep = 1,
                NamePaint = new SolidColorPaint(AxisColor),
                LabelsPaint = new SolidColorPaint(AxisColor),
                SeparatorsPaint = new SolidColorPaint(GridColor) { StrokeThickness = 1 }
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
    public void AddSample(double errorRatePercent, ErrorRateFrameKind kind)
    {
        var value = Math.Clamp(errorRatePercent, 0.0, 100.0);
        LatestPercent = value;

        var x = _nextSampleIndex;
        _nextSampleIndex++;
        _kinds.Add(kind);

        switch (kind)
        {
            case ErrorRateFrameKind.Fh:
                _fhValues.Add(new ObservablePoint(x, value));
                break;
            case ErrorRateFrameKind.Bh:
                _bhValues.Add(new ObservablePoint(x, value));
                break;
            default:
                _bdValues.Add(new ObservablePoint(x, value));
                break;
        }

        while (_kinds.Count > MaxSamples)
        {
            _kinds.RemoveAt(0);
            _firstSampleIndex++;
        }

        TrimOldPoints(_fhValues);
        TrimOldPoints(_bhValues);
        TrimOldPoints(_bdValues);
    }

    private void TrimOldPoints(ObservableCollection<ObservablePoint> series)
    {
        while (series.Count > 0 && series[0].X < _firstSampleIndex)
        {
            series.RemoveAt(0);
        }
    }

    private string LabelForSample(double value)
    {
        var index = (int)Math.Round(value);
        var local = index - _firstSampleIndex;
        if (local < 0 || local >= _kinds.Count)
        {
            return string.Empty;
        }

        return _kinds[local] switch
        {
            ErrorRateFrameKind.Fh => "FH",
            ErrorRateFrameKind.Bh => "BH",
            _ => "BD"
        };
    }

    public void Clear()
    {
        _fhValues.Clear();
        _bhValues.Clear();
        _bdValues.Clear();
        _kinds.Clear();
        _firstSampleIndex = 0;
        _nextSampleIndex = 0;
        LatestPercent = 0;
    }
}
