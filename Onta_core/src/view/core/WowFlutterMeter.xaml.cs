using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Onta.View.Core;

/// <summary>
/// WOW/Flutter の偏差を左右バーとノブで可視化するメーターです。
/// </summary>
public partial class WowFlutterMeter : UserControl
{
    private double _valuePercent;
    private double _rangePercent = 5.0;
    private string _channelLabel = "L";

    private bool _isActive = true;

    /// <summary>
    /// メーターを初期化します。
    /// </summary>
    public WowFlutterMeter()
    {
        InitializeComponent();
        ChannelLabel = "L";
        UpdateVisual();
    }

    /// <summary>
    /// メーターを有効/無効化します。無効化時は値をリセットします。
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

    /// <summary>表示チャネルラベルです（例: L/R）。</summary>
    public string ChannelLabel
    {
        get => _channelLabel;
        set
        {
            _channelLabel = value ?? string.Empty;
            TitleText.Text = _channelLabel;
        }
    }

    /// <summary>表示レンジ（±%）です。</summary>
    public double RangePercent
    {
        get => _rangePercent;
        set
        {
            _rangePercent = Math.Max(0.1, value);
            UpdateVisual();
        }
    }

    /// <summary>現在の表示値（%）です。</summary>
    public double ValuePercent => _valuePercent;

    /// <summary>
    /// 速度比を % 偏差へ変換して表示します。
    /// </summary>
    /// <param name="speedRatio">速度比（1.0 が基準）。</param>
    public void SetFromSpeedRatio(double speedRatio)
    {
        if (!_isActive)
        {
            return;
        }

        AddSample((speedRatio - 1.0) * 100.0);
    }

    /// <summary>
    /// 直接 % 値を追加して表示を更新します。
    /// </summary>
    /// <param name="valuePercent">表示する偏差値（%）。</param>
    public void AddSample(double valuePercent)
    {
        if (!_isActive)
        {
            return;
        }

        _valuePercent = valuePercent;
        UpdateVisual();
    }

    /// <summary>
    /// 表示値を 0 に戻します。
    /// </summary>
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




