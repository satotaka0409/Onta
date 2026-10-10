using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Onta.Core;

namespace Onta.Stream;

/// <summary>
/// 全シンボル共通値のパイロットがシンボルごとに回る位相から、シンボル位置のずれ（テープ速度の残差・ワウ）を推定します。
/// </summary>
/// <remarks>
/// 信号が 1 シンボルで δ サンプル遅れると、ビン k のパイロット位相は −2πkδ/N 回る。
/// 隣り合うシンボルのパイロット積の位相を、低いビンから順に展開しながら原点を通る直線で当てはめて δ を求める。
/// </remarks>
public static class StreamTimingDrift
{
    /// <summary>
    /// 等間隔に並ぶ OFDM シンボルの各パイロットの DFT 値を求めます。
    /// </summary>
    /// <param name="samples">PCM（実部のみ使用）。</param>
    /// <param name="length">有効サンプル数。</param>
    /// <param name="firstSymbolStart">最初のシンボル（CP 先頭）の位置。</param>
    /// <param name="symbolCount">測るシンボル数。</param>
    /// <param name="fftSize">FFT サイズ。</param>
    /// <param name="cyclicPrefix">CP 長。</param>
    /// <param name="pilotBins">パイロットのビン番号。</param>
    /// <param name="destination">[シンボル, パイロット] の DFT 値の書き込み先。</param>
    /// <returns>測れたシンボル数（信号の末尾で打ち切り）。</returns>
    public static int MeasurePilots(
        Complex[] samples,
        int length,
        int firstSymbolStart,
        int symbolCount,
        int fftSize,
        int cyclicPrefix,
        IReadOnlyList<int> pilotBins,
        Complex[,] destination)
    {
        var twiddles = new Twiddle[pilotBins.Count];
        for (var i = 0; i < pilotBins.Count; i++)
        {
            twiddles[i] = GetTwiddle(fftSize, pilotBins[i]);
        }

        var reals = new double[fftSize];
        var symbolLength = fftSize + cyclicPrefix;
        for (var s = 0; s < symbolCount; s++)
        {
            // CP の中ほどから窓を始め、前後どちらへ数サンプルずれても隣のシンボルにかからないようにする
            var start = firstSymbolStart + (s * symbolLength) + (cyclicPrefix / 2);
            if (start < 0 || start + fftSize > length)
            {
                return s;
            }

            SimdMath.CopyComplexReals(samples.AsSpan(start, fftSize), reals);
            for (var i = 0; i < twiddles.Length; i++)
            {
                destination[s, i] = new Complex(Dot(reals, twiddles[i].Cos), Dot(reals, twiddles[i].Sin));
            }
        }

        return symbolCount;
    }

    /// <summary>
    /// 1 ビン分の DFT 係数 cos / sin（e^{−j2π·bin·n/N}）です。
    /// </summary>
    /// <param name="Cos">実部の係数（長さ N）。</param>
    /// <param name="Sin">虚部の係数（長さ N）。</param>
    private sealed record Twiddle(double[] Cos, double[] Sin);

    private static readonly ConcurrentDictionary<(int FftSize, int Bin), Twiddle> TwiddleCache = new();

    /// <summary>
    /// FFT サイズとビンに対応する DFT 係数を返します（初回だけ作ってキャッシュ）。
    /// </summary>
    /// <param name="fftSize">FFT サイズ。</param>
    /// <param name="bin">ビン番号。</param>
    /// <returns>長さ fftSize の cos / sin 係数。</returns>
    private static Twiddle GetTwiddle(int fftSize, int bin) =>
        TwiddleCache.GetOrAdd((fftSize, bin), static key =>
        {
            var cos = new double[key.FftSize];
            var sin = new double[key.FftSize];
            var index = 0;
            var step = ((key.Bin % key.FftSize) + key.FftSize) % key.FftSize;
            for (var n = 0; n < key.FftSize; n++)
            {
                var a = -2.0 * Math.PI * index / key.FftSize;
                cos[n] = Math.Cos(a);
                sin[n] = Math.Sin(a);
                index += step;
                if (index >= key.FftSize)
                {
                    index -= key.FftSize;
                }
            }

            return new Twiddle(cos, sin);
        });

    /// <summary>
    /// 同じ長さの 2 つの double 列の内積を求めます。
    /// </summary>
    /// <param name="a">1 つ目の列。</param>
    /// <param name="b">2 つ目の列（a 以上の長さ）。</param>
    /// <returns>内積。</returns>
    private static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var n = a.Length;
        var i = 0;
        var sum = 0.0;
        if (Avx.IsSupported && n >= 8)
        {
            var acc0 = Vector256<double>.Zero;
            var acc1 = Vector256<double>.Zero;
            for (; i + 8 <= n; i += 8)
            {
                acc0 = MultiplyAdd(SimdMath.LoadAvx(a, i), SimdMath.LoadAvx(b, i), acc0);
                acc1 = MultiplyAdd(SimdMath.LoadAvx(a, i + 4), SimdMath.LoadAvx(b, i + 4), acc1);
            }

            sum = SimdMath.HorizontalSum(Avx.Add(acc0, acc1));
        }
        else if (AdvSimd.Arm64.IsSupported && n >= 4)
        {
            var acc0 = Vector128<double>.Zero;
            var acc1 = Vector128<double>.Zero;
            for (; i + 4 <= n; i += 4)
            {
                acc0 = AdvSimd.Arm64.FusedMultiplyAdd(acc0, SimdMath.LoadNeon(a, i), SimdMath.LoadNeon(b, i));
                acc1 = AdvSimd.Arm64.FusedMultiplyAdd(acc1, SimdMath.LoadNeon(a, i + 2), SimdMath.LoadNeon(b, i + 2));
            }

            sum = SimdMath.HorizontalSum(AdvSimd.Arm64.Add(acc0, acc1));
        }

        for (; i < n; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
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
    /// パイロットごとの「後のシンボル × 前のシンボルの共役」から、1 シンボルあたりのずれ（サンプル）を求めます。
    /// </summary>
    /// <param name="products">パイロットごとの積（複数シンボル分を足したものでもよい）。</param>
    /// <param name="bins">各積のビン番号。</param>
    /// <param name="count">使う要素数。</param>
    /// <param name="fftSize">FFT サイズ。</param>
    /// <returns>1 シンボルあたりのずれ（正なら信号が後ろへずれていく）。求められなければ NaN。</returns>
    public static double SolveDrift(Complex[] products, int[] bins, int count, int fftSize)
    {
        var order = new int[count];
        for (var i = 0; i < count; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => bins[a].CompareTo(bins[b]));
        double sxy = 0, sxx = 0, slope = 0;
        foreach (var i in order)
        {
            var bin = bins[i];
            var w = products[i].Magnitude;
            if (bin <= 0 || w <= 0)
            {
                continue;
            }

            var theta = products[i].Phase;
            if (sxx > 0)
            {
                theta += 2.0 * Math.PI * Math.Round(((slope * bin) - theta) / (2.0 * Math.PI));
            }

            sxy += w * bin * theta;
            sxx += w * bin * bin;
            slope = sxy / sxx;
        }

        return sxx > 0 ? -slope * fftSize / (2.0 * Math.PI) : double.NaN;
    }
}
