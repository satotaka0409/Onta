using System.Numerics;

namespace Onta.Stream;

/// <summary>
/// テープ速度のずれを戻すため、受信 PCM を任意の間隔で補間して取り出します（窓付き sinc 補間）。
/// </summary>
public static class StreamSpeedWarp
{
    /// <summary>片側のタップ数。</summary>
    public const int HalfTaps = 16;

    private const int Phases = 512;
    private static readonly float[] Kernel = BuildKernel();

    /// <summary>
    /// source の位置 start から step 間隔で count 点を補間し、destination へ書き込みます（範囲外は 0）。
    /// </summary>
    /// <param name="source">入力 PCM（実部のみ使用）。</param>
    /// <param name="sourceLength">入力の有効サンプル数。</param>
    /// <param name="start">最初の出力点に対応する入力位置（小数可）。</param>
    /// <param name="step">出力 1 点あたりに進む入力サンプル数（テープが速いと 1 より大きい）。</param>
    /// <param name="destination">出力先（count 以上の長さ）。</param>
    /// <param name="count">出力点数。</param>
    public static void Warp(Complex[] source, int sourceLength, double start, double step, Complex[] destination, int count)
    {
        for (var m = 0; m < count; m++)
        {
            destination[m] = new Complex(Interpolate(source, sourceLength, start + (m * step)), 0.0);
        }
    }

    /// <summary>
    /// source を positions の各位置で補間し、destination へ書き込みます（範囲外は 0）。
    /// </summary>
    /// <param name="source">入力 PCM（実部のみ使用）。</param>
    /// <param name="sourceLength">入力の有効サンプル数。</param>
    /// <param name="positions">出力点ごとの入力位置（小数可）。</param>
    /// <param name="destination">出力先（count 以上の長さ）。</param>
    /// <param name="count">出力点数。</param>
    public static void WarpAt(Complex[] source, int sourceLength, double[] positions, Complex[] destination, int count)
    {
        for (var m = 0; m < count; m++)
        {
            destination[m] = new Complex(Interpolate(source, sourceLength, positions[m]), 0.0);
        }
    }

    /// <summary>
    /// 1 点を窓付き sinc で補間します。
    /// </summary>
    /// <param name="source">入力 PCM（実部のみ使用）。</param>
    /// <param name="sourceLength">入力の有効サンプル数。</param>
    /// <param name="pos">入力位置（小数可）。</param>
    /// <returns>補間値（範囲外の入力は 0 とみなす）。</returns>
    private static double Interpolate(Complex[] source, int sourceLength, double pos)
    {
        var i0 = (int)Math.Floor(pos);
        var phase = (int)Math.Round((pos - i0) * Phases);
        if (phase == Phases)
        {
            phase = 0;
            i0++;
        }

        var kernelOffset = phase * 2 * HalfTaps;
        var first = i0 - HalfTaps + 1;
        var sum = 0.0;
        if (first >= 0 && first + (2 * HalfTaps) <= sourceLength)
        {
            for (var k = 0; k < 2 * HalfTaps; k++)
            {
                sum += source[first + k].Real * Kernel[kernelOffset + k];
            }
        }
        else
        {
            for (var k = 0; k < 2 * HalfTaps; k++)
            {
                var idx = first + k;
                if ((uint)idx < (uint)sourceLength)
                {
                    sum += source[idx].Real * Kernel[kernelOffset + k];
                }
            }
        }

        return sum;
    }

    /// <summary>
    /// 小数位相ごとの Blackman 窓付き sinc 係数表を作ります（位相 0〜Phases、各 2×HalfTaps 係数）。
    /// </summary>
    /// <returns>(Phases+1)×2×HalfTaps の係数表。</returns>
    private static float[] BuildKernel()
    {
        var taps = 2 * HalfTaps;
        var kernel = new float[(Phases + 1) * taps];
        for (var p = 0; p <= Phases; p++)
        {
            var frac = p / (double)Phases;
            for (var k = 0; k < taps; k++)
            {
                // タップ k は入力 i0 - HalfTaps + 1 + k。出力位置からの距離 t
                var t = (k - HalfTaps + 1) - frac;
                var sinc = Math.Abs(t) < 1e-12 ? 1.0 : Math.Sin(Math.PI * t) / (Math.PI * t);
                var x = (t + HalfTaps) / (2.0 * HalfTaps);
                var window = x is < 0 or > 1
                    ? 0.0
                    : 0.42 - (0.5 * Math.Cos(2 * Math.PI * x)) + (0.08 * Math.Cos(4 * Math.PI * x));
                kernel[(p * taps) + k] = (float)(sinc * window);
            }
        }

        return kernel;
    }
}
