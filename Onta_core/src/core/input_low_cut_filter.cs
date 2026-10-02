using System.Numerics;

namespace Onta.Core;

/// <summary>
/// 受信入力から DC オフセットと低域ハムを除く 2 次バターワース高域通過フィルタです（1 チャネル分の状態を持ちます）。
/// </summary>
/// <remarks>無変調区間の検出は周期相関とキャリアへの電力集中で判定するため、DC やハムが混ざると検出できなくなる。</remarks>
public sealed class InputLowCutFilter
{
    /// <summary>
    /// カットオフ周波数（Hz）です。最低キャリア（約 520 Hz）の位相を回しすぎない高さにしています。
    /// </summary>
    public const double CutoffHz = 100.0;

    private readonly double _b0;
    private readonly double _b1;
    private readonly double _b2;
    private readonly double _a1;
    private readonly double _a2;
    private double _x1;
    private double _x2;
    private double _y1;
    private double _y2;

    /// <summary>
    /// 指定サンプルレート用のフィルタを初期化します。
    /// </summary>
    /// <param name="sampleRate">入力のサンプリング周波数（Hz）。</param>
    public InputLowCutFilter(int sampleRate)
    {
        var w0 = 2.0 * Math.PI * CutoffHz / Math.Max(1, sampleRate);
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / Math.Sqrt(2.0);
        var a0 = 1.0 + alpha;
        _b0 = (1.0 + cos) / 2.0 / a0;
        _b1 = -(1.0 + cos) / a0;
        _b2 = _b0;
        _a1 = -2.0 * cos / a0;
        _a2 = (1.0 - alpha) / a0;
    }

    /// <summary>
    /// サンプル列の実部をその場でフィルタします（前回呼び出しの続きとして状態を引き継ぎます）。
    /// </summary>
    /// <param name="samples">処理するサンプル列。</param>
    public void ProcessInPlace(Span<Complex> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i].Real;
            var y = (_b0 * x) + (_b1 * _x1) + (_b2 * _x2) - (_a1 * _y1) - (_a2 * _y2);
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            samples[i] = new Complex(y, 0.0);
        }
    }

    /// <summary>
    /// 一括入力（WAV 全体など）を新しいフィルタ状態でその場フィルタします。
    /// </summary>
    /// <param name="samples">処理するサンプル列。空なら何もしません。</param>
    /// <param name="sampleRate">入力のサンプリング周波数（Hz）。</param>
    public static void ApplyInPlace(Complex[] samples, int sampleRate)
    {
        if (samples.Length == 0)
        {
            return;
        }

        new InputLowCutFilter(sampleRate).ProcessInPlace(samples);
    }
}
