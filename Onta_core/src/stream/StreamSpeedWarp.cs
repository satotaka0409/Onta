using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Onta.Core;

namespace Onta.Stream;

/// <summary>
/// テープ速度のずれを戻すため、受信 PCM を任意の間隔で補間して取り出します（窓付き sinc 補間）。
/// </summary>
public static class StreamSpeedWarp
{
    /// <summary>片側のタップ数。</summary>
    public const int HalfTaps = 16;

    private const int Taps = 2 * HalfTaps;
    private const int Phases = 512;
    private static readonly double[] Kernel = BuildKernel();

    /// <summary>AVX 用に 4 タップごとの並びを [k0,k2,k1,k3] へ入れ替えた係数表（複素配列の実部を unpack した順に合わせる）。</summary>
    private static readonly double[] KernelAvx = PermuteForAvx(Kernel);

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
    /// L/R を同じ位置で補間します（<see cref="Warp"/> を 2 チャネル分まとめて行い、係数の読み込みを共有します）。
    /// </summary>
    /// <param name="left">入力 L PCM（実部のみ使用）。</param>
    /// <param name="right">入力 R PCM（実部のみ使用）。</param>
    /// <param name="sourceLength">入力の有効サンプル数（L/R 共通）。</param>
    /// <param name="start">最初の出力点に対応する入力位置（小数可）。</param>
    /// <param name="step">出力 1 点あたりに進む入力サンプル数。</param>
    /// <param name="destinationLeft">L の出力先（count 以上の長さ）。</param>
    /// <param name="destinationRight">R の出力先（count 以上の長さ）。</param>
    /// <param name="count">出力点数。</param>
    public static void WarpStereo(
        Complex[] left,
        Complex[] right,
        int sourceLength,
        double start,
        double step,
        Complex[] destinationLeft,
        Complex[] destinationRight,
        int count)
    {
        for (var m = 0; m < count; m++)
        {
            InterpolatePair(left, right, sourceLength, start + (m * step), out var l, out var r);
            destinationLeft[m] = new Complex(l, 0.0);
            destinationRight[m] = new Complex(r, 0.0);
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
    /// L/R を positions の各位置で補間します（<see cref="WarpAt"/> を 2 チャネル分まとめて行い、係数の読み込みを共有します）。
    /// </summary>
    /// <param name="left">入力 L PCM（実部のみ使用）。</param>
    /// <param name="right">入力 R PCM（実部のみ使用）。</param>
    /// <param name="sourceLength">入力の有効サンプル数（L/R 共通）。</param>
    /// <param name="positions">出力点ごとの入力位置（小数可）。</param>
    /// <param name="destinationLeft">L の出力先（count 以上の長さ）。</param>
    /// <param name="destinationRight">R の出力先（count 以上の長さ）。</param>
    /// <param name="count">出力点数。</param>
    public static void WarpAtStereo(
        Complex[] left,
        Complex[] right,
        int sourceLength,
        double[] positions,
        Complex[] destinationLeft,
        Complex[] destinationRight,
        int count)
    {
        for (var m = 0; m < count; m++)
        {
            InterpolatePair(left, right, sourceLength, positions[m], out var l, out var r);
            destinationLeft[m] = new Complex(l, 0.0);
            destinationRight[m] = new Complex(r, 0.0);
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
        Locate(pos, out var first, out var kernelOffset);
        if (first < 0 || first + Taps > sourceLength)
        {
            return DotClipped(source, sourceLength, first, kernelOffset);
        }

        ref var src = ref Unsafe.As<Complex, double>(ref source[first]);
        if (Avx.IsSupported)
        {
            ref var kernel = ref KernelAvx[kernelOffset];
            return SimdMath.HorizontalSum(DotAvx(ref src, ref kernel));
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            ref var kernel = ref Kernel[kernelOffset];
            var sum = DotNeon(ref src, ref kernel);
            return sum.GetElement(0) + sum.GetElement(1);
        }

        return DotScalar(source, first, kernelOffset);
    }

    /// <summary>
    /// L/R の同じ位置を窓付き sinc で補間します。
    /// </summary>
    /// <param name="left">入力 L PCM（実部のみ使用）。</param>
    /// <param name="right">入力 R PCM（実部のみ使用）。</param>
    /// <param name="sourceLength">入力の有効サンプル数。</param>
    /// <param name="pos">入力位置（小数可）。</param>
    /// <param name="leftValue">L の補間値。</param>
    /// <param name="rightValue">R の補間値。</param>
    private static void InterpolatePair(Complex[] left, Complex[] right, int sourceLength, double pos, out double leftValue, out double rightValue)
    {
        Locate(pos, out var first, out var kernelOffset);
        if (first < 0 || first + Taps > sourceLength)
        {
            leftValue = DotClipped(left, sourceLength, first, kernelOffset);
            rightValue = DotClipped(right, sourceLength, first, kernelOffset);
            return;
        }

        ref var srcL = ref Unsafe.As<Complex, double>(ref left[first]);
        ref var srcR = ref Unsafe.As<Complex, double>(ref right[first]);
        if (Avx.IsSupported)
        {
            ref var kernel = ref KernelAvx[kernelOffset];
            DotAvxPair(ref srcL, ref srcR, ref kernel, out var sumL, out var sumR);
            leftValue = SimdMath.HorizontalSum(sumL);
            rightValue = SimdMath.HorizontalSum(sumR);
            return;
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            ref var kernel = ref Kernel[kernelOffset];
            var sumL = DotNeon(ref srcL, ref kernel);
            var sumR = DotNeon(ref srcR, ref kernel);
            leftValue = sumL.GetElement(0) + sumL.GetElement(1);
            rightValue = sumR.GetElement(0) + sumR.GetElement(1);
            return;
        }

        leftValue = DotScalar(left, first, kernelOffset);
        rightValue = DotScalar(right, first, kernelOffset);
    }

    /// <summary>
    /// 入力位置から、畳み込む最初の入力サンプルと係数表の位置を求めます。
    /// </summary>
    /// <param name="pos">入力位置（小数可）。</param>
    /// <param name="first">最初のタップに対応する入力サンプル位置。</param>
    /// <param name="kernelOffset">係数表の先頭位置（小数位相を <see cref="Phases"/> 段に丸めたもの）。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Locate(double pos, out int first, out int kernelOffset)
    {
        var i0 = (int)Math.Floor(pos);
        var phase = (int)Math.Round((pos - i0) * Phases);
        if (phase == Phases)
        {
            phase = 0;
            i0++;
        }

        kernelOffset = phase * Taps;
        first = i0 - HalfTaps + 1;
    }

    /// <summary>
    /// AVX で 32 タップの積和を求めます（複素配列の実部を unpack で取り出し、入れ替え済み係数と掛ける）。
    /// </summary>
    /// <param name="src">最初のタップの複素数（実部・虚部の順に double が並ぶ）。</param>
    /// <param name="kernel">AVX 用に並べ替えた係数の先頭。</param>
    /// <returns>4 レーンの部分和。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> DotAvx(ref double src, ref double kernel)
    {
        var acc0 = Vector256<double>.Zero;
        var acc1 = Vector256<double>.Zero;
        for (var k = 0; k < Taps; k += 8)
        {
            var re0 = LoadReals(ref src, 2 * k);
            var re1 = LoadReals(ref src, (2 * k) + 8);
            acc0 = MultiplyAdd(re0, Vector256.LoadUnsafe(ref kernel, (nuint)k), acc0);
            acc1 = MultiplyAdd(re1, Vector256.LoadUnsafe(ref kernel, (nuint)(k + 4)), acc1);
        }

        return Avx.Add(acc0, acc1);
    }

    /// <summary>
    /// AVX で L/R 2 チャネル分の 32 タップ積和を、係数の読み込みを共有して求めます。
    /// </summary>
    /// <param name="srcL">L の最初のタップ。</param>
    /// <param name="srcR">R の最初のタップ。</param>
    /// <param name="kernel">AVX 用に並べ替えた係数の先頭。</param>
    /// <param name="sumL">L の 4 レーン部分和。</param>
    /// <param name="sumR">R の 4 レーン部分和。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DotAvxPair(ref double srcL, ref double srcR, ref double kernel, out Vector256<double> sumL, out Vector256<double> sumR)
    {
        var accL0 = Vector256<double>.Zero;
        var accL1 = Vector256<double>.Zero;
        var accR0 = Vector256<double>.Zero;
        var accR1 = Vector256<double>.Zero;
        for (var k = 0; k < Taps; k += 8)
        {
            var k0 = Vector256.LoadUnsafe(ref kernel, (nuint)k);
            var k1 = Vector256.LoadUnsafe(ref kernel, (nuint)(k + 4));
            accL0 = MultiplyAdd(LoadReals(ref srcL, 2 * k), k0, accL0);
            accL1 = MultiplyAdd(LoadReals(ref srcL, (2 * k) + 8), k1, accL1);
            accR0 = MultiplyAdd(LoadReals(ref srcR, 2 * k), k0, accR0);
            accR1 = MultiplyAdd(LoadReals(ref srcR, (2 * k) + 8), k1, accR1);
        }

        sumL = Avx.Add(accL0, accL1);
        sumR = Avx.Add(accR0, accR1);
    }

    /// <summary>
    /// 連続する複素数 4 個の実部を [re0, re2, re1, re3] の並びで読みます。
    /// </summary>
    /// <param name="src">複素配列を double 列として見た先頭。</param>
    /// <param name="offset">読み始める double の位置（複素数 2 個分 = 4 の倍数）。</param>
    /// <returns>実部 4 個（レーン内 unpack の並び）。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> LoadReals(ref double src, int offset)
    {
        var a = Vector256.LoadUnsafe(ref src, (nuint)offset);
        var b = Vector256.LoadUnsafe(ref src, (nuint)(offset + 4));
        return Avx.UnpackLow(a, b);
    }

    /// <summary>
    /// a × b + c を求めます（FMA があれば 1 命令で）。
    /// </summary>
    /// <param name="a">被乗数。</param>
    /// <param name="b">乗数。</param>
    /// <param name="c">加える値。</param>
    /// <returns>a × b + c。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> MultiplyAdd(Vector256<double> a, Vector256<double> b, Vector256<double> c) =>
        Fma.IsSupported ? Fma.MultiplyAdd(a, b, c) : Avx.Add(Avx.Multiply(a, b), c);

    /// <summary>
    /// AdvSimd で 32 タップの積和を求めます（複素数 2 個の実部を UnzipEven で取り出す）。
    /// </summary>
    /// <param name="src">最初のタップの複素数。</param>
    /// <param name="kernel">係数の先頭（タップ順）。</param>
    /// <returns>2 レーンの部分和。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<double> DotNeon(ref double src, ref double kernel)
    {
        var acc0 = Vector128<double>.Zero;
        var acc1 = Vector128<double>.Zero;
        for (var k = 0; k < Taps; k += 4)
        {
            var re0 = AdvSimd.Arm64.UnzipEven(Vector128.LoadUnsafe(ref src, (nuint)(2 * k)), Vector128.LoadUnsafe(ref src, (nuint)((2 * k) + 2)));
            var re1 = AdvSimd.Arm64.UnzipEven(Vector128.LoadUnsafe(ref src, (nuint)((2 * k) + 4)), Vector128.LoadUnsafe(ref src, (nuint)((2 * k) + 6)));
            acc0 = AdvSimd.Arm64.FusedMultiplyAdd(acc0, re0, Vector128.LoadUnsafe(ref kernel, (nuint)k));
            acc1 = AdvSimd.Arm64.FusedMultiplyAdd(acc1, re1, Vector128.LoadUnsafe(ref kernel, (nuint)(k + 2)));
        }

        return AdvSimd.Arm64.Add(acc0, acc1);
    }

    /// <summary>
    /// SIMD が使えないときの 32 タップ積和です。
    /// </summary>
    /// <param name="source">入力 PCM（実部のみ使用）。</param>
    /// <param name="first">最初のタップに対応する入力位置（範囲内であること）。</param>
    /// <param name="kernelOffset">係数表の先頭位置。</param>
    /// <returns>補間値。</returns>
    private static double DotScalar(Complex[] source, int first, int kernelOffset)
    {
        var sum = 0.0;
        for (var k = 0; k < Taps; k++)
        {
            sum += source[first + k].Real * Kernel[kernelOffset + k];
        }

        return sum;
    }

    /// <summary>
    /// 入力の端にかかるときの積和です（範囲外のタップは 0 とみなす）。
    /// </summary>
    /// <param name="source">入力 PCM（実部のみ使用）。</param>
    /// <param name="sourceLength">入力の有効サンプル数。</param>
    /// <param name="first">最初のタップに対応する入力位置。</param>
    /// <param name="kernelOffset">係数表の先頭位置。</param>
    /// <returns>補間値。</returns>
    private static double DotClipped(Complex[] source, int sourceLength, int first, int kernelOffset)
    {
        var sum = 0.0;
        for (var k = 0; k < Taps; k++)
        {
            var idx = first + k;
            if ((uint)idx < (uint)sourceLength)
            {
                sum += source[idx].Real * Kernel[kernelOffset + k];
            }
        }

        return sum;
    }

    /// <summary>
    /// 小数位相ごとの Blackman 窓付き sinc 係数表を作ります（位相 0〜Phases、各 2×HalfTaps 係数）。
    /// </summary>
    /// <returns>(Phases+1)×2×HalfTaps の係数表。</returns>
    private static double[] BuildKernel()
    {
        var kernel = new double[(Phases + 1) * Taps];
        for (var p = 0; p <= Phases; p++)
        {
            var frac = p / (double)Phases;
            for (var k = 0; k < Taps; k++)
            {
                // タップ k は入力 i0 - HalfTaps + 1 + k。出力位置からの距離 t
                var t = (k - HalfTaps + 1) - frac;
                var sinc = Math.Abs(t) < 1e-12 ? 1.0 : Math.Sin(Math.PI * t) / (Math.PI * t);
                var x = (t + HalfTaps) / (2.0 * HalfTaps);
                var window = x is < 0 or > 1
                    ? 0.0
                    : 0.42 - (0.5 * Math.Cos(2 * Math.PI * x)) + (0.08 * Math.Cos(4 * Math.PI * x));
                kernel[(p * Taps) + k] = sinc * window;
            }
        }

        return kernel;
    }

    /// <summary>
    /// 係数表を 4 タップごとに [k0,k2,k1,k3] へ並べ替えます（AVX の unpack が返す実部の並びに合わせる）。
    /// </summary>
    /// <param name="kernel">タップ順の係数表。</param>
    /// <returns>並べ替えた係数表。</returns>
    private static double[] PermuteForAvx(double[] kernel)
    {
        var permuted = new double[kernel.Length];
        for (var i = 0; i < kernel.Length; i += 4)
        {
            permuted[i] = kernel[i];
            permuted[i + 1] = kernel[i + 2];
            permuted[i + 2] = kernel[i + 1];
            permuted[i + 3] = kernel[i + 3];
        }

        return permuted;
    }
}
