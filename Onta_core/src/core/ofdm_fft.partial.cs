using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Onta.Core;

public sealed partial class OfdmGenerator
{
    private void EnsureIfftScratch(int n)
    {
        if (_ifftConjugateScratch is not null && _ifftConjugateScratch.Length == n)
        {
            return;
        }

        _ifftConjugateScratch = new Complex[n];
        _ifftWorkScratch = new Complex[n];
    }

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

    private Complex[] InverseFft(Complex[] frequency)
    {
        EnsureIfftScratch(frequency.Length);
        InverseFftInto(frequency, _ifftWorkScratch!);
        var result = new Complex[frequency.Length];
        Array.Copy(_ifftWorkScratch!, result, frequency.Length);
        return result;
    }

    private static Vector<double> CreateConjugateSignMask()
    {
        var values = new double[Vector<double>.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (i & 1) == 0 ? 1.0 : -1.0;
        }

        return new Vector<double>(values);
    }

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

    private static Vector<double> LoadVector(ReadOnlySpan<double> source, int index)
    {
        ref var first = ref MemoryMarshal.GetReference(source);
        ref var at = ref Unsafe.Add(ref first, index);
        return Unsafe.ReadUnaligned<Vector<double>>(ref Unsafe.As<double, byte>(ref at));
    }

    private static Vector<double> LoadVector(Span<double> source, int index)
    {
        ref var first = ref MemoryMarshal.GetReference(source);
        ref var at = ref Unsafe.Add(ref first, index);
        return Unsafe.ReadUnaligned<Vector<double>>(ref Unsafe.As<double, byte>(ref at));
    }

    private static void StoreVector(Span<double> destination, int index, Vector<double> value)
    {
        ref var first = ref MemoryMarshal.GetReference(destination);
        ref var at = ref Unsafe.Add(ref first, index);
        Unsafe.WriteUnaligned(ref Unsafe.As<double, byte>(ref at), value);
    }

    private static Complex[] Fft(Complex[] input)
    {
        var output = new Complex[input.Length];
        Array.Copy(input, output, input.Length);
        FftInPlace(output);
        return output;
    }

    private static void FftInPlace(Complex[] output)
    {
        var n = output.Length;

        var bits = (int)Math.Log2(n);

        for (var i = 0; i < n; i++)
        {
            var j = ReverseBits(i, bits);
            if (j > i)
            {
                (output[i], output[j]) = (output[j], output[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2.0 * Math.PI / len;
            var wLen = Complex.FromPolarCoordinates(1.0, angle);
            var useAvx = Avx.IsSupported && len >= 4;
            var useArm64Simd = AdvSimd.Arm64.IsSupported && len >= 4;

            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                var halfLen = len >> 1;
                for (var j = 0; j < halfLen; j++)
                {
                    var upperIndex = i + j;
                    var lowerIndex = upperIndex + halfLen;

                    if (useAvx && (j + 1) < halfLen)
                    {
                        var upperIndex2 = upperIndex + 1;
                        var lowerIndex2 = lowerIndex + 1;

                        var lowerVec = Vector256.Create(
                            output[lowerIndex].Real,
                            output[lowerIndex].Imaginary,
                            output[lowerIndex2].Real,
                            output[lowerIndex2].Imaginary);

                        var w2 = w * wLen;
                        var wrVec = Vector256.Create(w.Real, w.Real, w2.Real, w2.Real);
                        var wiVec = Vector256.Create(w.Imaginary, w.Imaginary, w2.Imaginary, w2.Imaginary);
                        var swapped = Vector256.Create(
                            lowerVec.GetElement(1),
                            lowerVec.GetElement(0),
                            lowerVec.GetElement(3),
                            lowerVec.GetElement(2));
                        var signedImag = Avx.Multiply(swapped, Vector256.Create(-1.0, 1.0, -1.0, 1.0));
                        var twiddled = Avx.Add(Avx.Multiply(lowerVec, wrVec), Avx.Multiply(signedImag, wiVec));

                        var upperVec = Vector256.Create(
                            output[upperIndex].Real,
                            output[upperIndex].Imaginary,
                            output[upperIndex2].Real,
                            output[upperIndex2].Imaginary);
                        var sum = Avx.Add(upperVec, twiddled);
                        var diff = Avx.Subtract(upperVec, twiddled);

                        output[upperIndex] = new Complex(sum.GetElement(0), sum.GetElement(1));
                        output[lowerIndex] = new Complex(diff.GetElement(0), diff.GetElement(1));
                        output[upperIndex2] = new Complex(sum.GetElement(2), sum.GetElement(3));
                        output[lowerIndex2] = new Complex(diff.GetElement(2), diff.GetElement(3));

                        j++;
                        w = w2;
                    }
                    else if (useArm64Simd && (j + 1) < halfLen)
                    {
                        var upperIndex2 = upperIndex + 1;
                        var lowerIndex2 = lowerIndex + 1;

                        var lower1 = Unsafe.ReadUnaligned<Vector128<double>>(
                            ref Unsafe.As<Complex, byte>(ref output[lowerIndex]));
                        var lower2 = Unsafe.ReadUnaligned<Vector128<double>>(
                            ref Unsafe.As<Complex, byte>(ref output[lowerIndex2]));

                        var w2 = w * wLen;
                        var wr1 = Vector128.Create(w.Real);
                        var wi1 = Vector128.Create(w.Imaginary);
                        var wr2 = Vector128.Create(w2.Real);
                        var wi2 = Vector128.Create(w2.Imaginary);

                        var swappedSigned1 = Vector128.Create(-lower1.GetElement(1), lower1.GetElement(0));
                        var swappedSigned2 = Vector128.Create(-lower2.GetElement(1), lower2.GetElement(0));
                        var twiddled1 = AdvSimd.Arm64.Add(
                            AdvSimd.Arm64.Multiply(lower1, wr1),
                            AdvSimd.Arm64.Multiply(swappedSigned1, wi1));
                        var twiddled2 = AdvSimd.Arm64.Add(
                            AdvSimd.Arm64.Multiply(lower2, wr2),
                            AdvSimd.Arm64.Multiply(swappedSigned2, wi2));

                        var upper1 = Unsafe.ReadUnaligned<Vector128<double>>(
                            ref Unsafe.As<Complex, byte>(ref output[upperIndex]));
                        var upper2 = Unsafe.ReadUnaligned<Vector128<double>>(
                            ref Unsafe.As<Complex, byte>(ref output[upperIndex2]));

                        var sum1 = AdvSimd.Arm64.Add(upper1, twiddled1);
                        var diff1 = AdvSimd.Arm64.Subtract(upper1, twiddled1);
                        var sum2 = AdvSimd.Arm64.Add(upper2, twiddled2);
                        var diff2 = AdvSimd.Arm64.Subtract(upper2, twiddled2);

                        output[upperIndex] = new Complex(sum1.GetElement(0), sum1.GetElement(1));
                        output[lowerIndex] = new Complex(diff1.GetElement(0), diff1.GetElement(1));
                        output[upperIndex2] = new Complex(sum2.GetElement(0), sum2.GetElement(1));
                        output[lowerIndex2] = new Complex(diff2.GetElement(0), diff2.GetElement(1));

                        j++;
                        w = w2;
                    }
                    else
                    {
                        var u = output[upperIndex];
                        var v = output[lowerIndex] * w;
                        output[upperIndex] = u + v;
                        output[lowerIndex] = u - v;
                    }

                    w *= wLen;
                }
            }
        }
    }

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


