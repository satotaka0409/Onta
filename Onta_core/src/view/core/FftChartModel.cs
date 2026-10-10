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
    /// <summary>L チャンネル（緑系）。</summary>
    public static readonly SKColor ChannelLeftColor = new(166, 221, 176);
    /// <summary>R チャンネル（オレンジ系。ワウ／目盛りと同じ）。</summary>
    public static readonly SKColor ChannelRightColor = new(255, 182, 120);
    private static readonly SKColor AxisColor = new(176, 181, 191);
    private static readonly SKColor GridColor = new(92, 97, 108);
    private readonly SKColor _primaryColor;
    private readonly LineSeries<ObservablePoint> _leftSeries;
    private readonly LineSeries<ObservablePoint> _rightSeries;

    /// <summary>現在の表示スタイル（null は未適用。次回必ず適用する）。</summary>
    private bool? _stereoStyle;

    /// <summary>
    /// L 色で FFT チャートを初期化します。
    /// </summary>
    public FftChartModel()
        : this(ChannelLeftColor)
    {
    }

    /// <summary>
    /// 単一系列表示色を指定して FFT チャートを初期化します（性能測定の L/R 分離表示用）。
    /// </summary>
    /// <param name="primaryStroke">モノラル表示時の系列色。</param>
    public FftChartModel(SKColor primaryStroke)
    {
        _primaryColor = primaryStroke;
        _leftSeries = new LineSeries<ObservablePoint>
        {
            Values = Array.Empty<ObservablePoint>(),
            Name = "L",
            Fill = null,
            Stroke = new SolidColorPaint(_primaryColor, 1.5f),
            GeometrySize = 0,
            LineSmoothness = 0,
            AnimationsSpeed = TimeSpan.Zero
        };

        _rightSeries = new LineSeries<ObservablePoint>
        {
            Values = Array.Empty<ObservablePoint>(),
            Name = "R",
            Fill = null,
            Stroke = new SolidColorPaint(ChannelRightColor, 1.5f),
            GeometrySize = 0,
            LineSmoothness = 0,
            AnimationsSpeed = TimeSpan.Zero,
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
    /// <returns>上下左右 0 の描画余白。</returns>
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
    /// ファイル画面向け：軸名・目盛り分の描画余白です。
    /// </summary>
    /// <returns>軸ラベル分を確保した描画余白。</returns>
    public static Margin CreateDrawMarginWithFrequencyLabels() =>
        new(60, 14, 12, 40);

    public ISeries[] Series { get; }

    public Axis[] XAxes { get; }

    public Axis[] YAxes { get; }

    /// <summary>
    /// L/R FFT サンプルで点群を差し替え、ステレオ時は R 系列を表示します。
    /// </summary>
    /// <param name="leftSamples">L チャネルの周波数ビン。</param>
    /// <param name="rightSamples">R チャネルの周波数ビン。</param>
    /// <param name="isStereo">true のとき R も描画し L を緑系色にします。</param>
    public void ReplacePoints(
        IReadOnlyList<CoreFftSample> leftSamples,
        IReadOnlyList<CoreFftSample> rightSamples,
        bool isStereo)
    {
        // 系列インスタンスは固定（画面は起動時に Series を 1 回だけ束縛する）。Values の差し替えで再描画させる。
        _leftSeries.Values = ToPoints(leftSamples);
        _rightSeries.Values = isStereo ? ToPoints(rightSamples) : Array.Empty<ObservablePoint>();
        ApplyStereoStyle(isStereo);

        XAxes[0].MinLimit = XMinLimit;
        XAxes[0].MaxLimit = XMaxLimit;

        // 0 dB = フルスケール正弦波。ピーク追従で上限が縮むと縦軸が不自然になる。
        YAxes[0].MinLimit = -100;
        YAxes[0].MaxLimit = 0;
    }

    /// <summary>
    /// ステレオ／単系列の表示色と R 系列の表示有無を切り替えます（変化時のみ）。
    /// </summary>
    /// <param name="isStereo">ステレオ表示なら true。</param>
    private void ApplyStereoStyle(bool isStereo)
    {
        if (_stereoStyle == isStereo)
        {
            return;
        }

        _stereoStyle = isStereo;
        _leftSeries.Stroke = new SolidColorPaint(isStereo ? ChannelLeftColor : _primaryColor, 1.5f);
        _rightSeries.IsVisible = isStereo;
    }

    /// <summary>
    /// FFT サンプルを表示点配列へ変換します（表示上限を超える周波数は除外）。
    /// </summary>
    /// <param name="samples">FFT サンプル。</param>
    /// <returns>表示点配列。</returns>
    private static ObservablePoint[] ToPoints(IReadOnlyList<CoreFftSample> samples)
    {
        var list = new List<ObservablePoint>(samples.Count);
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (s.FrequencyHz > MaxDisplayHz)
            {
                continue;
            }

            list.Add(new ObservablePoint(s.FrequencyHz, s.MagnitudeDb));
        }

        return list.ToArray();
    }

    /// <summary>
    /// L=緑 / R=オレンジのステレオ表示色を有効にします（ストリーム FFT 凡例用）。
    /// </summary>
    public void ShowStereoChannels()
    {
        _stereoStyle = null;
        ApplyStereoStyle(true);
    }

    /// <summary>
    /// 点群を消し、単系列表示と軸範囲を初期状態へ戻します。
    /// </summary>
    public void Clear()
    {
        _leftSeries.Values = Array.Empty<ObservablePoint>();
        _rightSeries.Values = Array.Empty<ObservablePoint>();
        _stereoStyle = null;
        ApplyStereoStyle(false);
        XAxes[0].MinLimit = XMinLimit;
        XAxes[0].MaxLimit = XMaxLimit;
        YAxes[0].MinLimit = -100;
        YAxes[0].MaxLimit = 0;
    }
}