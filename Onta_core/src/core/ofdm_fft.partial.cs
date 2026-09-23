using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Onta.Core;

public sealed partial class OfdmGenerator
{
    /// <summary>
    /// EnsureIfftScratch は、前提条件を満たすよう確保します。
    /// </summary>
    /// <param name="n">n。</param>
    private void EnsureIfftScratch(int n)
    {
        if (_ifftConjugateScratch is not null && _ifftConjugateScratch.Length == n)
        {
            return;
        }

        _ifftConjugateScratch = new Complex[n];
        _ifftWorkScratch = new Complex[n];
    }

    /// <summary>
    /// InverseFftInto を実行します。
    /// </summary>
    /// <param name="frequency">frequency。</param>
    /// <param name="destination">出力先。</param>
    private void InverseFftInto(Complex[] frequency, Complex[] destination)
    {
        if (destination.Length < frequency.Length)
        {
            throw new ArgumentException("Destination is shorter than frequency bins.", nameof(destination));
        }

        EnsureIfftScratch(frequency.Length);
        var scratch = _ifftConjugateScratch!;
        ConjugateInto(frequency, scratch);
        Array.Copy(scratch, destination, frequency.Length);
        FftInPlace(destination);
        ConjugateAndScaleInPlace(destination, 1.0 / frequency.Length);
    }

    /// <summary>
    /// CreateConjugateSignMask は、インスタンスまたはバッファを生成します。
    /// </summary>
    /// <returns>Vector<double>。</returns>
    private static Vector<double> CreateConjugateSignMask()
    {
        var values = new double[Vector<double>.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (i & 1) == 0 ? 1.0 : -1.0;
        }

        return new Vector<double>(values);
    }

    /// <summary>
    /// ConjugateInto を実行します。
    /// </summary>
    /// <param name="source">入力元。</param>
    /// <param name="destination">出力先。</param>
    private static void ConjugateInto(Complex[] source, Complex[] destination)
    {
        ReadOnlySpan<double> src = MemoryMarshal.Cast<Complex, double>(source.AsSpan());
        Span<double> dst = MemoryMarshal.Cast<Complex, double>(destination.AsSpan());
        var width = Vector<double>.Count;
        var i = 0;
        for (; i <= src.Length - width; i += width)
        {
            var chunk = LoadVector(src, i);
            StoreVector(dst, i, chunk * ConjugateSignMask);
        }

        for (; i < src.Length; i++)
        {
            dst[i] = (i & 1) == 0 ? src[i] : -src[i];
        }
    }

    /// <summary>
    /// ConjugateAndScaleInPlace を実行します。
    /// </summary>
    /// <param name="values">values。</param>
    /// <param name="scale">scale。</param>
    private static void ConjugateAndScaleInPlace(Complex[] values, double scale)
    {
        Span<double> data = MemoryMarshal.Cast<Complex, double>(values.AsSpan());
        var mask = ConjugateSignMask * new Vector<double>(scale);
        var width = Vector<double>.Count;
        var i = 0;
        for (; i <= data.Length - width; i += width)
        {
            var chunk = LoadVector(data, i);
            StoreVector(data, i, chunk * mask);
        }

        for (; i < data.Length; i++)
        {
            var sign = (i & 1) == 0 ? 1.0 : -1.0;
            data[i] *= sign * scale;
        }
    }

    /// <summary>
    /// LoadVector の結果を返します。
    /// </summary>
    /// <param name="source">入力元。</param>
    /// <param name="index">インデックス。</param>
    /// <returns>Vector<double>。</returns>
    private static Vector<double> LoadVector(ReadOnlySpan<double> source, int index)
    {
        ref var first = ref MemoryMarshal.GetReference(source);
        ref var at = ref Unsafe.Add(ref first, index);
        return Unsafe.ReadUnaligned<Vector<double>>(ref Unsafe.As<double, byte>(ref at));
    }

    /// <summary>
    /// LoadVector の結果を返します。
    /// </summary>
    /// <param name="source">入力元。</param>
    /// <param name="index">インデックス。</param>
    /// <returns>Vector<double>。</returns>
    private static Vector<double> LoadVector(Span<double> source, int index)
    {
        ref var first = ref MemoryMarshal.GetReference(source);
        ref var at = ref Unsafe.Add(ref first, index);
        return Unsafe.ReadUnaligned<Vector<double>>(ref Unsafe.As<double, byte>(ref at));
    }

    /// <summary>
    /// StoreVector を実行します。
    /// </summary>
    /// <param name="destination">出力先。</param>
    /// <param name="index">インデックス。</param>
    /// <param name="value">入力値。</param>
    private static void StoreVector(Span<double> destination, int index, Vector<double> value)
    {
        ref var first = ref MemoryMarshal.GetReference(destination);
        ref var at = ref Unsafe.Add(ref first, index);
        Unsafe.WriteUnaligned(ref Unsafe.As<double, byte>(ref at), value);
    }

    private static readonly int[] BitReverse256 = CreateBitReverseTable(256);
    private static readonly int[] BitReverse1024 = CreateBitReverseTable(1024);
    private static readonly int[] BitReverse2048 = CreateBitReverseTable(2048);
    private static readonly int[] BitReverse4096 = CreateBitReverseTable(4096);
    private static readonly double[] Hann256 = CreateHannWindow(256);
    private static readonly double[] Hann1024 = CreateHannWindow(1024);
    private static readonly double[] Hann2048 = CreateHannWindow(2048);
    private static readonly double[] Hann4096 = CreateHannWindow(4096);
    private static readonly Vector256<double> FftSwapSign256 = Vector256.Create(-1.0, 1.0, -1.0, 1.0);
    private static readonly Vector128<double> FftSwapSign128 = Vector128.Create(-1.0, 1.0);
    private static readonly object BitReverseCacheLock = new();
    private static readonly Dictionary<int, int[]> BitReverseExtra = new();
    private static readonly object HannCacheLock = new();
    private static readonly Dictionary<int, double[]> HannExtra = new();

    /// <summary>
    /// 実数 PCM に Hann 窓を掛けて FFT 入力へ置きます（虚部 0）。
    /// </summary>
    /// <param name="timePcm">timePcm。</param>
    /// <param name="destination">出力先。</param>
    internal static void ApplyHannWindowFromRealPcm(ReadOnlySpan<Complex> timePcm, Complex[] destination)
    {
        var n = destination.Length;
        var offset = timePcm.Length - n;
        var hann = ResolveHannWindow(n);

        var src = MemoryMarshal.Cast<Complex, double>(timePcm.Slice(offset, n));
        var dst = MemoryMarshal.Cast<Complex, double>(destination.AsSpan(0, n));
        var i = 0;
        if (Avx.IsSupported && n >= 2)
        {
            for (; i + 2 <= n; i += 2)
            {
                var s = SimdMath.LoadAvx(src, i * 2);
                var reals = Avx.Shuffle(s, s, 0b0000);
                var h = Vector256.Create(hann[i], 0.0, hann[i + 1], 0.0);
                SimdMath.StoreAvx(dst, i * 2, Avx.Multiply(reals, h));
            }
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            for (; i < n; i++)
            {
                var s = SimdMath.LoadNeon(src, i * 2);
                SimdMath.StoreNeon(
                    dst,
                    i * 2,
                    AdvSimd.Arm64.Multiply(s, Vector128.Create(hann[i], 0.0)));
            }

            return;
        }

        for (; i < n; i++)
        {
            destination[i] = new Complex(timePcm[offset + i].Real * hann[i], 0.0);
        }
    }

    /// <summary>
    /// 実数 PCM を虚部 0 の FFT 入力へコピーします。
    /// </summary>
    /// <param name="timePcm">timePcm。</param>
    /// <param name="destination">出力先。</param>
    internal static void CopyRealPcmToFftInput(ReadOnlySpan<Complex> timePcm, Span<Complex> destination)
    {
        var n = destination.Length;
        var offset = timePcm.Length - n;
        var src = MemoryMarshal.Cast<Complex, double>(timePcm.Slice(offset, n));
        var dst = MemoryMarshal.Cast<Complex, double>(destination);
        var i = 0;
        if (Avx.IsSupported && n >= 2)
        {
            var mask = SimdMath.RealLaneMask256;
            for (; i + 2 <= n; i += 2)
            {
                SimdMath.StoreAvx(dst, i * 2, Avx.Multiply(SimdMath.LoadAvx(src, i * 2), mask));
            }
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            var mask = SimdMath.RealLaneMask128;
            for (; i < n; i++)
            {
                SimdMath.StoreNeon(dst, i * 2, AdvSimd.Arm64.Multiply(SimdMath.LoadNeon(src, i * 2), mask));
            }

            return;
        }

        for (; i < n; i++)
        {
            destination[i] = new Complex(timePcm[offset + i].Real, 0.0);
        }
    }

    /// <summary>
    /// FftInPlace は、FFT/IFFT を実行します。
    /// </summary>
    /// <param name="output">output。</param>
    private static void FftInPlace(Complex[] output)
    {
        var n = output.Length;
        if (n <= 1)
        {
            return;
        }

        BitReversePermute(output);
        var data = MemoryMarshal.Cast<Complex, double>(output.AsSpan());

        for (var len = 2; len <= n; len <<= 1)
        {
            var half = len >> 1;
            var angle = -2.0 * Math.PI / len;
            var wLenRe = Math.Cos(angle);
            var wLenIm = Math.Sin(angle);

            if (Avx.IsSupported && half >= 2)
            {
                FftStageAvx(data, n, len, half, wLenRe, wLenIm);
            }
            else if (AdvSimd.Arm64.IsSupported)
            {
                FftStageNeon(data, n, len, half, wLenRe, wLenIm);
            }
            else
            {
                FftStageScalar(output, n, len, half, wLenRe, wLenIm);
            }
        }
    }

    /// <summary>
    /// AVX で 2 バタフライずつ処理します（GetElement なし）。
    /// </summary>
    /// <param name="data">入力データ。</param>
    /// <param name="n">n。</param>
    /// <param name="len">len。</param>
    /// <param name="half">half。</param>
    /// <param name="wLenRe">wLenRe。</param>
    /// <param name="wLenIm">wLenIm。</param>
    private static void FftStageAvx(
        Span<double> data,
        int n,
        int len,
        int half,
        double wLenRe,
        double wLenIm)
    {
        var swapSign = FftSwapSign256;
        var useFma = Fma.IsSupported;
        for (var i = 0; i < n; i += len)
        {
            var wr = 1.0;
            var wi = 0.0;
            for (var j = 0; j < half; j += 2)
            {
                var wr2 = (wr * wLenRe) - (wi * wLenIm);
                var wi2 = (wr * wLenIm) + (wi * wLenRe);
                var uIdx = (i + j) * 2;
                var vIdx = (i + j + half) * 2;
                var u = SimdMath.LoadAvx(data, uIdx);
                var v = SimdMath.LoadAvx(data, vIdx);
                var wRe = Vector256.Create(wr, wr, wr2, wr2);
                var wIm = Vector256.Create(wi, wi, wi2, wi2);
                var swapped = Avx.Shuffle(v, v, 0b0101);
                var signed = Avx.Multiply(swapped, swapSign);
                var twiddled = useFma
                    ? Fma.MultiplyAdd(signed, wIm, Avx.Multiply(v, wRe))
                    : Avx.Add(Avx.Multiply(v, wRe), Avx.Multiply(signed, wIm));
                SimdMath.StoreAvx(data, uIdx, Avx.Add(u, twiddled));
                SimdMath.StoreAvx(data, vIdx, Avx.Subtract(u, twiddled));
                wr = (wr2 * wLenRe) - (wi2 * wLenIm);
                wi = (wr2 * wLenIm) + (wi2 * wLenRe);
            }
        }
    }

    /// <summary>
    /// AdvSimd で 1 複素バタフライを処理します。
    /// </summary>
    /// <param name="data">入力データ。</param>
    /// <param name="n">n。</param>
    /// <param name="len">len。</param>
    /// <param name="half">half。</param>
    /// <param name="wLenRe">wLenRe。</param>
    /// <param name="wLenIm">wLenIm。</param>
    private static void FftStageNeon(
        Span<double> data,
        int n,
        int len,
        int half,
        double wLenRe,
        double wLenIm)
    {
        var swapSign = FftSwapSign128;
        for (var i = 0; i < n; i += len)
        {
            var wr = 1.0;
            var wi = 0.0;
            for (var j = 0; j < half; j++)
            {
                var uIdx = (i + j) * 2;
                var vIdx = (i + j + half) * 2;
                var u = SimdMath.LoadNeon(data, uIdx);
                var v = SimdMath.LoadNeon(data, vIdx);
                var swapped = Vector128.Create(v.GetUpper(), v.GetLower());
                var signed = AdvSimd.Arm64.Multiply(swapped, swapSign);
                var twiddled = AdvSimd.Arm64.Add(
                    AdvSimd.Arm64.Multiply(v, Vector128.Create(wr)),
                    AdvSimd.Arm64.Multiply(signed, Vector128.Create(wi)));
                SimdMath.StoreNeon(data, uIdx, AdvSimd.Arm64.Add(u, twiddled));
                SimdMath.StoreNeon(data, vIdx, AdvSimd.Arm64.Subtract(u, twiddled));
                var nextWr = (wr * wLenRe) - (wi * wLenIm);
                wi = (wr * wLenIm) + (wi * wLenRe);
                wr = nextWr;
            }
        }
    }

    /// <summary>
    /// SIMD が使えない場合の基数 2 バタフライです。
    /// </summary>
    /// <param name="output">output。</param>
    /// <param name="n">n。</param>
    /// <param name="len">len。</param>
    /// <param name="half">half。</param>
    /// <param name="wLenRe">wLenRe。</param>
    /// <param name="wLenIm">wLenIm。</param>
    private static void FftStageScalar(
        Complex[] output,
        int n,
        int len,
        int half,
        double wLenRe,
        double wLenIm)
    {
        for (var i = 0; i < n; i += len)
        {
            var wr = 1.0;
            var wi = 0.0;
            for (var j = 0; j < half; j++)
            {
                var upper = output[i + j];
                var lower = output[i + j + half];
                var vr = (lower.Real * wr) - (lower.Imaginary * wi);
                var vi = (lower.Real * wi) + (lower.Imaginary * wr);
                output[i + j] = new Complex(upper.Real + vr, upper.Imaginary + vi);
                output[i + j + half] = new Complex(upper.Real - vr, upper.Imaginary - vi);
                var nextWr = (wr * wLenRe) - (wi * wLenIm);
                wi = (wr * wLenIm) + (wi * wLenRe);
                wr = nextWr;
            }
        }
    }

    /// <summary>
    /// BitReversePermute を実行します。
    /// </summary>
    /// <param name="output">output。</param>
    private static void BitReversePermute(Complex[] output)
    {
        var n = output.Length;
        var table = ResolveBitReverseTable(n);

        for (var i = 0; i < n; i++)
        {
            var j = table[i];
            if (j > i)
            {
                (output[i], output[j]) = (output[j], output[i]);
            }
        }
    }

    /// <summary>
    /// FFT 長に対応するビット逆順テーブルを返します（常用長は静的、他はキャッシュ）。
    /// </summary>
    /// <param name="n">n。</param>
    /// <returns>結果の配列またはスライス。</returns>
    private static int[] ResolveBitReverseTable(int n) =>
        n switch
        {
            256 => BitReverse256,
            1024 => BitReverse1024,
            2048 => BitReverse2048,
            4096 => BitReverse4096,
            _ => GetOrCreateBitReverseExtra(n)
        };

    /// <summary>
    /// FFT 長に対応する Hann 窓を返します（常用長は静的、他はキャッシュ）。
    /// </summary>
    /// <param name="n">n。</param>
    /// <returns>結果の配列またはスライス。</returns>
    private static double[] ResolveHannWindow(int n) =>
        n switch
        {
            256 => Hann256,
            1024 => Hann1024,
            2048 => Hann2048,
            4096 => Hann4096,
            _ => GetOrCreateHannExtra(n)
        };

    /// <summary>
    /// 非標準 FFT 長のビット逆順テーブルを取得／生成します。
    /// </summary>
    /// <param name="n">n。</param>
    /// <returns>結果の配列またはスライス。</returns>
    private static int[] GetOrCreateBitReverseExtra(int n)
    {
        lock (BitReverseCacheLock)
        {
            if (BitReverseExtra.TryGetValue(n, out var existing))
            {
                return existing;
            }

            var created = CreateBitReverseTable(n);
            BitReverseExtra[n] = created;
            return created;
        }
    }

    /// <summary>
    /// 非標準 FFT 長の Hann 窓を取得／生成します。
    /// </summary>
    /// <param name="n">n。</param>
    /// <returns>結果の配列またはスライス。</returns>
    private static double[] GetOrCreateHannExtra(int n)
    {
        lock (HannCacheLock)
        {
            if (HannExtra.TryGetValue(n, out var existing))
            {
                return existing;
            }

            var created = CreateHannWindow(n);
            HannExtra[n] = created;
            return created;
        }
    }

    /// <summary>
    /// CreateBitReverseTable は、インスタンスまたはバッファを生成します。
    /// </summary>
    /// <param name="n">n。</param>
    /// <returns>結果の配列またはスライス。</returns>
    private static int[] CreateBitReverseTable(int n)
    {
        var bits = (int)Math.Log2(n);
        var table = new int[n];
        for (var i = 0; i < n; i++)
        {
            table[i] = ReverseBits(i, bits);
        }

        return table;
    }

    /// <summary>
    /// CreateHannWindow は、インスタンスまたはバッファを生成します。
    /// </summary>
    /// <param name="n">n。</param>
    /// <returns>結果の配列またはスライス。</returns>
    private static double[] CreateHannWindow(int n)
    {
        var denom = Math.Max(1, n - 1);
        var hann = new double[n];
        var twoPi = 2.0 * Math.PI;
        for (var i = 0; i < n; i++)
        {
            hann[i] = 1.0 - Math.Cos(twoPi * i / denom);
        }

        return hann;
    }

    /// <summary>
    /// ReverseBits の結果を返します。
    /// </summary>
    /// <param name="value">入力値。</param>
    /// <param name="bitCount">ビット数。</param>
    /// <returns>計算した整数値。</returns>
    private static int ReverseBits(int value, int bitCount)
    {
        var reversed = 0;
        for (var i = 0; i < bitCount; i++)
        {
            reversed = (reversed << 1) | (value & 1);
            value >>= 1;
        }

        return reversed;
    }
}


