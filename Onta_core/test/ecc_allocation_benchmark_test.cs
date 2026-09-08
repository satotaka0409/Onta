using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Onta.Core.Tests;

public sealed class EccAllocationBenchmarkTest
{
    private readonly ITestOutputHelper _output;

    public EccAllocationBenchmarkTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Convolution_And_Turbo_Decode_Allocation_Benchmark()
    {
        var convPayload = CreateDeterministicBytes(384, seed: 20260908);
        var convRate = ConvolutionalCode.PunctureRate.Rate3_4;
        var convEncoded = ConvolutionalCode.Encode(convPayload, terminate: true, punctureRate: convRate);
        var convBitCount = ConvolutionalCode.GetEncodedBitLength(convPayload.Length * 8, terminated: true, punctureRate: convRate);
        var convSoftLlrs = BuildHardDecisionLlrs(convEncoded, convBitCount, reliability: 4.0);

        var turboPayload = CreateDeterministicBytes(TurboEcc1024.DataUnitBytes, seed: 20260909);
        var turboEncoded = TurboEcc1024.Encode(turboPayload);

        // JIT warm-up
        _ = ConvolutionalCode.Decode(convEncoded, convPayload.Length, terminated: true, punctureRate: convRate);
        _ = ConvolutionalCode.DecodeSoftToInfoLlrs(convSoftLlrs, convPayload.Length, out _, terminated: true, punctureRate: convRate);
        _ = TurboEcc1024.Decode(turboEncoded, iterations: 8, channelReliability: 1.25);

        var convHard = Measure(
            iterations: 600,
            action: () => ConvolutionalCode.Decode(convEncoded, convPayload.Length, terminated: true, punctureRate: convRate),
            validator: decoded => Assert.Equal(convPayload, decoded));

        var convSoft = Measure(
            iterations: 300,
            action: () => ConvolutionalCode.DecodeSoftToInfoLlrs(convSoftLlrs, convPayload.Length, out _, terminated: true, punctureRate: convRate),
            validator: decoded => Assert.Equal(convPayload, decoded));

        var turbo = Measure(
            iterations: 50,
            action: () => TurboEcc1024.Decode(turboEncoded, iterations: 8, channelReliability: 1.25),
            validator: decoded => Assert.Equal(turboPayload, decoded));

        var line1 = $"Convolution hard decode : {convHard.Elapsed.TotalMilliseconds:F2} ms / {convHard.Iterations} iters";
        var line2 = $"  Alloc total={convHard.AllocatedBytes} bytes, per-iter={convHard.AllocatedBytesPerIteration:F1} bytes";
        var line3 = $"Convolution soft decode : {convSoft.Elapsed.TotalMilliseconds:F2} ms / {convSoft.Iterations} iters";
        var line4 = $"  Alloc total={convSoft.AllocatedBytes} bytes, per-iter={convSoft.AllocatedBytesPerIteration:F1} bytes";
        var line5 = $"Turbo decode            : {turbo.Elapsed.TotalMilliseconds:F2} ms / {turbo.Iterations} iters";
        var line6 = $"  Alloc total={turbo.AllocatedBytes} bytes, per-iter={turbo.AllocatedBytesPerIteration:F1} bytes";

        _output.WriteLine(line1);
        _output.WriteLine(line2);
        _output.WriteLine(line3);
        _output.WriteLine(line4);
        _output.WriteLine(line5);
        _output.WriteLine(line6);
        Console.WriteLine(line1);
        Console.WriteLine(line2);
        Console.WriteLine(line3);
        Console.WriteLine(line4);
        Console.WriteLine(line5);
        Console.WriteLine(line6);
    }

    private static (TimeSpan Elapsed, long AllocatedBytes, int Iterations, double AllocatedBytesPerIteration) Measure(
        int iterations,
        Func<byte[]> action,
        Action<byte[]> validator)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            var decoded = action();
            validator(decoded);
        }

        sw.Stop();
        var after = GC.GetTotalAllocatedBytes(true);
        var allocated = after - before;
        return (sw.Elapsed, allocated, iterations, iterations == 0 ? 0.0 : (double)allocated / iterations);
    }

    private static byte[] CreateDeterministicBytes(int length, int seed)
    {
        var random = new Random(seed);
        var data = new byte[length];
        random.NextBytes(data);
        return data;
    }

    private static double[] BuildHardDecisionLlrs(byte[] packedBits, int bitCount, double reliability)
    {
        var llrs = new double[bitCount];
        for (var i = 0; i < bitCount; i++)
        {
            var b = packedBits[i >> 3];
            var bit = ((b >> (7 - (i & 7))) & 1) != 0;
            llrs[i] = bit ? reliability : -reliability;
        }

        return llrs;
    }
}
