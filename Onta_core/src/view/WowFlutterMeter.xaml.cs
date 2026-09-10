using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Onta.View;

/// <summary>
/// ワウフラッター偏差メーター（中央 0、数値・時間軸なし、0.2 秒スナップショット）。
/// </summary>
public partial class WowFlutterMeter : UserControl
{
    private double _valuePercent;
    private double _rangePercent = 5.0;
    private string _channelLabel = "L";

    private bool _isActive = true;

    public WowFlutterMeter()
    {
        InitializeComponent();
        ChannelLabel = "L";
        UpdateVisual();
    }

    /// <summary>
    /// false のときメーターを暗くし、更新を停止します（モノラル時の R 用）。
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            Opacity = value ? 1.0 : 0.35;
            IsHitTestVisible = value;
            if (!value)
            {
                _valuePercent = 0;
            }

            UpdateVisual();
        }
    }

    public string ChannelLabel
    {
        get => _channelLabel;
        set
        {
            _channelLabel = value ?? string.Empty;
            TitleText.Text = _channelLabel;
        }
    }

    public double RangePercent
    {
        get => _rangePercent;
        set
        {
            _rangePercent = Math.Max(0.1, value);
            UpdateVisual();
        }
    }

    public double ValuePercent => _valuePercent;

    public void SetFromSpeedRatio(double speedRatio)
    {
        if (!_isActive)
        {
            return;
        }

        AddSample((speedRatio - 1.0) * 100.0);
    }

    public void AddSample(double valuePercent)
    {
        if (!_isActive)
        {
            return;
        }

        _valuePercent = valuePercent;
        UpdateVisual();
    }

    public void Clear()
    {
        _valuePercent = 0;
        UpdateVisual();
    }

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateVisual();
    }

    private void UpdateVisual()
    {
        var width = CanvasRoot.ActualWidth;
        if (width <= 1)
        {
            return;
        }

        var midX = width / 2.0;
        CenterLine.X1 = midX;
        CenterLine.X2 = midX;

        var clamped = Math.Clamp(_valuePercent, -_rangePercent, _rangePercent);
        var half = width / 2.0;
        var fillWidth = Math.Abs(clamped) / _rangePercent * half;

        if (clamped < 0)
        {
            Canvas.SetLeft(FillRect, midX - fillWidth);
            FillRect.Width = fillWidth;
            FillRect.Fill = (Brush)FindResource("BrushFillNeg");
        }
        else if (clamped > 0)
        {
            Canvas.SetLeft(FillRect, midX);
            FillRect.Width = fillWidth;
            FillRect.Fill = (Brush)FindResource("BrushFillPos");
        }
        else
        {
            FillRect.Width = 0;
        }

        var knobX = midX + (clamped / _rangePercent) * half - (Knob.Width / 2.0);
        Canvas.SetLeft(Knob, knobX);
    }
}
