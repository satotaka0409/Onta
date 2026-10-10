using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Onta.Core;

/// <summary>
/// コアホットパス向けの AVX / AdvSimd ヘルパです。
/// </summary>
internal static class SimdMath
{
    /// <summary>複素ベクトルの実部だけ残すマスク（[1,0,1,0]）。</summary>
    public static readonly Vector256<double> RealLaneMask256 = Vector256.Create(1.0, 0.0, 1.0, 0.0);

    /// <summary>1 複素数の実部だけ残すマスク（[1,0]）。</summary>
    public static readonly Vector128<double> RealLaneMask128 = Vector128.Create(1.0, 0.0);

    /// <summary>
    /// double 列から AVX ベクトルを読みます。
    /// </summary>
    /// <param name="source">読み取り元の double 列。</param>
    /// <param name="index">先頭要素のインデックス（4 要素単位で読む）。</param>
    /// <returns>index から連続 4 要素を詰めた AVX ベクトル。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<double> LoadAvx(ReadOnlySpan<double> source, int index)
    {
        ref var first = ref MemoryMarshal.GetReference(source);
        return Unsafe.ReadUnaligned<Vector256<double>>(
            ref Unsafe.As<double, byte>(ref Unsafe.Add(ref first, index)));
    }

    /// <summary>
    /// double 列へ AVX ベクトルを書きます。
    /// </summary>
    /// <param name="destination">書き込み先の double 列。</param>
    /// <param name="index">先頭要素のインデックス（4 要素単位で書く）。</param>
    /// <param name="value">書き込む AVX ベクトル。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreAvx(Span<double> destination, int index, Vector256<double> value)
    {
        ref var first = ref MemoryMarshal.GetReference(destination);
        Unsafe.WriteUnaligned(
            ref Unsafe.As<double, byte>(ref Unsafe.Add(ref first, index)),
            value);
    }

    /// <summary>
    /// double 列から NEON ベクトルを読みます。
    /// </summary>
    /// <param name="source">読み取り元の double 列。</param>
    /// <param name="index">先頭要素のインデックス（2 要素単位で読む）。</param>
    /// <returns>index から連続 2 要素を詰めた NEON ベクトル。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<double> LoadNeon(ReadOnlySpan<double> source, int index)
    {
        ref var first = ref MemoryMarshal.GetReference(source);
        return Unsafe.ReadUnaligned<Vector128<double>>(
            ref Unsafe.As<double, byte>(ref Unsafe.Add(ref first, index)));
    }

    /// <summary>
    /// double 列へ NEON ベクトルを書きます。
    /// </summary>
    /// <param name="destination">書き込み先の double 列。</param>
    /// <param name="index">先頭要素のインデックス（2 要素単位で書く）。</param>
    /// <param name="value">書き込む NEON ベクトル。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreNeon(Span<double> destination, int index, Vector128<double> value)
    {
        ref var first = ref MemoryMarshal.GetReference(destination);
        Unsafe.WriteUnaligned(
            ref Unsafe.As<double, byte>(ref Unsafe.Add(ref first, index)),
            value);
    }

    /// <summary>
    /// AVX 4 レーンの合計です。
    /// </summary>
    /// <param name="value">合計対象の AVX ベクトル。</param>
    /// <returns>4 レーンを加算したスカラー値。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double HorizontalSum(Vector256<double> value)
    {
        var low = value.GetLower();
        var high = value.GetUpper();
        var pair = Avx.Add(low, high);
        return pair.GetElement(0) + pair.GetElement(1);
    }

    /// <summary>
    /// NEON 2 レーンの合計です。
    /// </summary>
    /// <param name="value">合計対象の NEON ベクトル。</param>
    /// <returns>2 レーンを加算したスカラー値。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double HorizontalSum(Vector128<double> value) =>
        value.GetElement(0) + value.GetElement(1);

    /// <summary>
    /// 複素配列の実部を連続 double へコピーします。
    /// </summary>
    /// <param name="source">実部を取り出す複素配列。</param>
    /// <param name="destination">実部を連続格納する出力バッファ。</param>
    public static void CopyComplexReals(ReadOnlySpan<Complex> source, Span<double> destination)
    {
        var n = Math.Min(source.Length, destination.Length);
        var src = MemoryMarshal.Cast<Complex, double>(source);
        var i = 0;
        if (Avx2.IsSupported && n >= 4)
        {
            for (; i + 4 <= n; i += 4)
            {
                var a = LoadAvx(src, i * 2);
                var b = LoadAvx(src, (i * 2) + 4);
                var swizzled = Avx.Shuffle(a, b, 0b0000);
                var packed = Avx2.Permute4x64(swizzled, 0b11011000);
                StoreAvx(destination, i, packed);
            }
        }
        else if (AdvSimd.Arm64.IsSupported && n >= 2)
        {
            for (; i + 2 <= n; i += 2)
            {
                var a = LoadNeon(src, i * 2);
                var b = LoadNeon(src, (i * 2) + 2);
                StoreNeon(destination, i, Vector128.Create(a.GetLower(), b.GetLower()));
            }
        }

        for (; i < n; i++)
        {
            destination[i] = source[i].Real;
        }
    }

    /// <summary>
    /// 実部だけを複素配列へ書き、虚部は 0 にします。
    /// </summary>
    /// <param name="source">書き込む実部の連続 double 列。</param>
    /// <param name="destination">実部を格納し虚部を 0 にする複素配列。</param>
    public static void WriteComplexReals(ReadOnlySpan<double> source, Span<Complex> destination)
    {
        var n = Math.Min(source.Length, destination.Length);
        var dst = MemoryMarshal.Cast<Complex, double>(destination);
        var i = 0;
        if (Avx.IsSupported && n >= 2)
        {
            for (; i + 2 <= n; i += 2)
            {
                StoreAvx(dst, i * 2, Vector256.Create(source[i], 0.0, source[i + 1], 0.0));
            }
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            for (; i < n; i++)
            {
                StoreNeon(dst, i * 2, Vector128.Create(source[i], 0.0));
            }

            return;
        }

        for (; i < n; i++)
        {
            destination[i] = new Complex(source[i], 0.0);
        }
    }

    /// <summary>
    /// 複素配列の虚部を 0 にします。
    /// </summary>
    /// <param name="values">虚部を 0 にクリアする複素配列（in-place）。</param>
    public static void ZeroImagInPlace(Span<Complex> values)
    {
        var data = MemoryMarshal.Cast<Complex, double>(values);
        var i = 0;
        if (Avx.IsSupported && data.Length >= 4)
        {
            var mask = RealLaneMask256;
            for (; i + 4 <= data.Length; i += 4)
            {
                StoreAvx(data, i, Avx.Multiply(LoadAvx(data, i), mask));
            }
        }
        else if (AdvSimd.Arm64.IsSupported && data.Length >= 2)
        {
            var mask = RealLaneMask128;
            for (; i + 2 <= data.Length; i += 2)
            {
                StoreNeon(data, i, AdvSimd.Arm64.Multiply(LoadNeon(data, i), mask));
            }
        }

        for (; i + 1 < data.Length; i += 2)
        {
            data[i + 1] = 0.0;
        }
    }

    /// <summary>
    /// 複素配列の実部絶対値の最大を返します。
    /// </summary>
    /// <param name="source">走査対象の複素配列。</param>
    /// <returns>実部絶対値の最大。空配列時は 0。</returns>
    public static double MaxAbsReals(ReadOnlySpan<Complex> source)
    {
        var n = source.Length;
        if (n == 0)
        {
            return 0;
        }

        var src = MemoryMarshal.Cast<Complex, double>(source);
        var max = 0.0;
        var i = 0;
        if (Avx2.IsSupported && n >= 4)
        {
            var vmax = Vector256<double>.Zero;
            var sign = Vector256.Create(-0.0);
            for (; i + 4 <= n; i += 4)
            {
                var a = LoadAvx(src, i * 2);
                var b = LoadAvx(src, (i * 2) + 4);
                var packed = Avx2.Permute4x64(Avx.Shuffle(a, b, 0b0000), 0b11011000);
                vmax = Avx.Max(vmax, Avx.AndNot(sign, packed));
            }

            max = Math.Max(
                Math.Max(vmax.GetElement(0), vmax.GetElement(1)),
                Math.Max(vmax.GetElement(2), vmax.GetElement(3)));
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            var vmax = Vector128<double>.Zero;
            for (; i + 2 <= n; i += 2)
            {
                var a = LoadNeon(src, i * 2);
                var b = LoadNeon(src, (i * 2) + 2);
                var reals = Vector128.Create(a.GetLower(), b.GetLower());
                vmax = AdvSimd.Arm64.Max(vmax, Vector128.Abs(reals));
            }

            max = Math.Max(vmax.GetElement(0), vmax.GetElement(1));
        }

        for (; i < n; i++)
        {
            max = Math.Max(max, Math.Abs(source[i].Real));
        }

        return max;
    }

    /// <summary>
    /// dest[i] = left[i] + right[i] を SIMD で計算します。
    /// </summary>
    /// <param name="left">加算の左辺 double 列。</param>
    /// <param name="right">加算の右辺 double 列。</param>
    /// <param name="destination">要素ごとの和を書き込む出力バッファ。</param>
    public static void Add(ReadOnlySpan<double> left, ReadOnlySpan<double> right, Span<double> destination)
    {
        var n = Math.Min(left.Length, Math.Min(right.Length, destination.Length));
        var i = 0;
        if (Avx.IsSupported && n >= 4)
        {
            for (; i + 4 <= n; i += 4)
            {
                StoreAvx(destination, i, Avx.Add(LoadAvx(left, i), LoadAvx(right, i)));
            }
        }
        else if (AdvSimd.Arm64.IsSupported && n >= 2)
        {
            for (; i + 2 <= n; i += 2)
            {
                StoreNeon(destination, i, AdvSimd.Arm64.Add(LoadNeon(left, i), LoadNeon(right, i)));
            }
        }

        for (; i < n; i++)
        {
            destination[i] = left[i] + right[i];
        }
    }
}
