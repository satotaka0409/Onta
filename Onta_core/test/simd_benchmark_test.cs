using System.Diagnostics;
using System.Reflection;
using System.Runtime.Intrinsics.X86;
using Xunit;
using Xunit.Abstractions;

namespace Onta.Core.Tests;

public sealed class SimdBenchmarkTest
{
    private readonly ITestOutputHelper _output;

    public SimdBenchmarkTest(ITestOutputHelper output)
    {
        _output = output;
    }

    private delegate void EmitPamAxisSoftLlrsDelegate(
        double amplitude,
        int bitsPerAxis,
        int[] levels,
        double invVariance,
        ref int bitIndex,
        double[] llrs);

    [Fact]
    public void EmitPamAxisSoftLlrs_Qam64_SimdVsScalar_Benchmark()
    {
        if (!Avx.IsSupported)
        {
            _output.WriteLine("AVX is not supported on this machine. Benchmark skipped.");
            return;
        }

        var ofdmType = typeof(OfdmGenerator);
        var method = ofdmType.GetMethod(
            "EmitPamAxisSoftLlrs",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var methodDelegate = (EmitPamAxisSoftLlrsDelegate)method!.CreateDelegate(typeof(EmitPamAxisSoftLlrsDelegate));

        var levelsField = ofdmType.GetField(
            "Qam64Levels",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(levelsField);
        var qam64Levels = (int[])levelsField!.GetValue(null)!;

        var amplitudes = CreateDeterministicAmplitudes(200_000);
        const double invVariance = 20.0;

        // Warm-up for JIT and CPU frequency stabilization.
        RunSimd(methodDelegate, amplitudes, qam64Levels, invVariance);
        RunScalar(amplitudes, qam64Levels, invVariance);

        var simd = Measure(() => RunSimd(methodDelegate, amplitudes, qam64Levels, invVariance));
        var scalar = Measure(() => RunScalar(amplitudes, qam64Levels, invVariance));

        // Correctness spot-check: same LLRs for the first samples.
        for (var i = 0; i < 128; i++)
        {
            var simdLlr = ComputeLlrsSimd(methodDelegate, amplitudes[i], qam64Levels, invVariance);
            var scalarLlr = ComputeLlrsScalar(amplitudes[i], qam64Levels, invVariance);
            Assert.Equal(scalarLlr.Length, simdLlr.Length);
            for (var b = 0; b < scalarLlr.Length; b++)
            {
                Assert.True(Math.Abs(simdLlr[b] - scalarLlr[b]) < 1e-12,
                    $"LLR mismatch at sample={i}, bit={b}, simd={simdLlr[b]}, scalar={scalarLlr[b]}");
            }
        }

        var speedup = scalar.Elapsed.TotalMilliseconds / simd.Elapsed.TotalMilliseconds;
        _output.WriteLine($"SIMD elapsed   : {simd.Elapsed.TotalMilliseconds:F3} ms");
        _output.WriteLine($"Scalar elapsed : {scalar.Elapsed.TotalMilliseconds:F3} ms");
        _output.WriteLine($"Speedup        : {speedup:F2}x");
        _output.WriteLine($"Checksums      : simd={simd.Checksum:F6}, scalar={scalar.Checksum:F6}");

        Assert.True(Math.Abs(simd.Checksum - scalar.Checksum) < 1e-6,
            "SIMD and scalar checksums differ.");
    }

    private static (TimeSpan Elapsed, double Checksum) Measure(Func<double> action)
    {
        var sw = Stopwatch.StartNew();
        var checksum = action();
        sw.Stop();
        return (sw.Elapsed, checksum);
    }

    private static double RunSimd(
        EmitPamAxisSoftLlrsDelegate method,
        double[] amplitudes,
        int[] levels,
        double invVariance)
    {
        var checksum = 0.0;
        var llrs = new double[3];
        for (var i = 0; i < amplitudes.Length; i++)
        {
            llrs[0] = 0.0;
            llrs[1] = 0.0;
            llrs[2] = 0.0;
            var bitIndex = 0;
            method(amplitudes[i], 3, levels, invVariance, ref bitIndex, llrs);
            checksum += llrs[0] + llrs[1] + llrs[2];
        }

        return checksum;
    }

    private static double RunScalar(double[] amplitudes, int[] levels, double invVariance)
    {
        var checksum = 0.0;
        var llrs = new double[3];
        for (var i = 0; i < amplitudes.Length; i++)
        {
            llrs[0] = 0.0;
            llrs[1] = 0.0;
            llrs[2] = 0.0;
            var bitIndex = 0;
            EmitPamAxisSoftLlrsScalar(amplitudes[i], 3, levels, invVariance, ref bitIndex, llrs);
            checksum += llrs[0] + llrs[1] + llrs[2];
        }

        return checksum;
    }

    private static double[] ComputeLlrsSimd(
        EmitPamAxisSoftLlrsDelegate method,
        double amplitude,
        int[] levels,
        double invVariance)
    {
        var llrs = new double[3];
        var bitIndex = 0;
        method(amplitude, 3, levels, invVariance, ref bitIndex, llrs);
        return llrs;
    }

    private static double[] ComputeLlrsScalar(double amplitude, int[] levels, double invVariance)
    {
        var llrs = new double[3];
        var bitIndex = 0;
        EmitPamAxisSoftLlrsScalar(amplitude, 3, levels, invVariance, ref bitIndex, llrs);
        return llrs;
    }

    private static void EmitPamAxisSoftLlrsScalar(
        double amplitude,
        int bitsPerAxis,
        int[] levels,
        double invVariance,
        ref int bitIndex,
        double[] llrs)
    {
        var mask = (1 << bitsPerAxis) - 1;
        for (var bit = bitsPerAxis - 1; bit >= 0; bit--)
        {
            var minDist0 = double.PositiveInfinity;
            var minDist1 = double.PositiveInfinity;
            for (var binary = 0; binary <= mask; binary++)
            {
                var gray = (binary ^ (binary >> 1)) & mask;
                var level = levels[gray];
                var dist = amplitude - level;
                var dist2 = dist * dist;
                if (((binary >> bit) & 1) == 0)
                {
                    minDist0 = Math.Min(minDist0, dist2);
                }
                else
                {
                    minDist1 = Math.Min(minDist1, dist2);
                }
            }

            if (bitIndex >= llrs.Length)
            {
                return;
            }

            llrs[bitIndex++] = 0.5 * (minDist0 - minDist1) * invVariance;
        }
    }

    private static double[] CreateDeterministicAmplitudes(int count)
    {
        var values = new double[count];
        var random = new Random(1234567);
        for (var i = 0; i < values.Length; i++)
        {
            // 64QAM 軸スケール近傍を広くカバー。
            values[i] = (random.NextDouble() * 18.0) - 9.0;
        }

        return values;
    }
}
