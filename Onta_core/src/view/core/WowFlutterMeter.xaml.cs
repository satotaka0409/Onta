using System.Windows;
using System.Windows.Controls;

namespace Onta.View.Core;

/// <summary>
/// WOW/Flutter の偏差を左右バーとノブで可視化するメーターです（中央が 0）。
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
        Loaded += (_, _) => UpdateVisual();
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
    public void SetFromSpeedRatio(double speedRatio)
    {
        if (!_isActive)
        {
            return;
        }

        AddSample((speedRatio - 1.0) * 100.0);
    }

    /// <summary>
    /// 直接 % 値を設定して表示を更新します。
    /// </summary>
    public void AddSample(double valuePercent)
    {
        if (!_isActive)
        {
            return;
        }

        _valuePercent = valuePercent;
        // 大きな偏差でも振り切れないようレンジを自動拡張
        var abs = Math.Abs(_valuePercent);
        if (abs > _rangePercent)
        {
            _rangePercent = Math.Clamp(Math.Ceiling(abs * 1.25 * 2.0) / 2.0, 5.0, 50.0);
        }

        UpdateVisual();
    }

    /// <summary>
    /// 表示値を 0 に戻します。
    /// </summary>
    public void Clear()
    {
        _valuePercent = 0;
        _rangePercent = 5.0;
        UpdateVisual();
    }

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateVisual();
    }

    private void UpdateVisual()
    {
        if (TrackGrid is null || FillNeg is null || FillPos is null || KnobTranslate is null || ValueText is null)
        {
            return;
        }

        var trackWidth = Track.ActualWidth;
        if (trackWidth <= 2)
        {
            return;
        }

        var half = trackWidth / 2.0;
        var clamped = Math.Clamp(_valuePercent, -_rangePercent, _rangePercent);
        var ratio = Math.Abs(clamped) / _rangePercent;
        var fillWidth = ratio * half;

        if (clamped < 0)
        {
            FillNeg.Width = fillWidth;
            FillPos.Width = 0;
        }
        else if (clamped > 0)
        {
            FillNeg.Width = 0;
            FillPos.Width = fillWidth;
        }
        else
        {
            FillNeg.Width = 0;
            FillPos.Width = 0;
        }

        // ノブはトラック左端基準。中央(0)からのオフセット。
        var knobCenterX = half + (clamped / _rangePercent) * half;
        KnobTranslate.X = knobCenterX - (Knob.Width / 2.0);

        ValueText.Text = $"{_valuePercent:+0.00;-0.00;0.00}%";
    }
}
