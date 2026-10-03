using Onta.Core;
using Xunit;
using Kernel = Onta.Core.ConvolutionalCode.TrellisKernel;

namespace Onta.Core.Tests.Core;

public sealed class ConvolutionalCodeSimdTests
{
    /// <summary>
    /// SIMD 経路の情報ビット LLR がスカラ経路と完全に一致することを確かめます（雑音・パンクチャ欠落・終端有無）。
    /// </summary>
    [Theory]
    [InlineData(128, true, 0.0)]
    [InlineData(128, false, 0.0)]
    [InlineData(128, true, 1.5)]
    [InlineData(256, true, 0.0)]
    [InlineData(256, false, 0.0)]
    [InlineData(256, true, 1.5)]
    public void SimdKernel_MatchesScalar(int vectorBits, bool terminated, double noise)
    {
        var kernel = vectorBits == 256 ? Kernel.Vector256 : Kernel.Vector128;
        if (!ConvolutionalCode.IsTrellisKernelSupported(kernel))
        {
            return;
        }

        const int InfoBits = 2000;
        var tCount = InfoBits + (terminated ? 6 : 0);
        var rng = new Random(11);
        var llrs = new double[tCount * 2];
        for (var i = 0; i < llrs.Length; i++)
        {
            // 3 ビットに 1 つはパンクチャ欠落（0）とし、到達不能状態・同値の比較も通す
            llrs[i] = i % 3 == 2 ? 0.0 : ((rng.Next(2) * 2) - 1) * 4.0 + (noise * ((rng.NextDouble() * 2) - 1) * 4.0);
        }

        var expected = new double[InfoBits];
        var actual = new double[InfoBits];
        ConvolutionalCode.ComputeInfoLlrs(llrs, terminated, expected, Kernel.Scalar);
        ConvolutionalCode.ComputeInfoLlrs(llrs, terminated, actual, kernel);

        for (var i = 0; i < InfoBits; i++)
        {
            Assert.True(expected[i] == actual[i], $"bit {i}: scalar={expected[i]:R} simd={actual[i]:R}");
        }
    }

    /// <summary>
    /// 自動選択の経路で、雑音付きソフト入力を正しく復号できることを確かめます。
    /// </summary>
    [Theory]
    [InlineData(ConvolutionalCode.PunctureRate.Rate1_2)]
    [InlineData(ConvolutionalCode.PunctureRate.Rate2_3)]
    [InlineData(ConvolutionalCode.PunctureRate.Rate3_4)]
    public void DecodeSoft_NoisyLlrs_RoundTrips(ConvolutionalCode.PunctureRate rate)
    {
        var rng = new Random(5);
        var source = new byte[1045];
        rng.NextBytes(source);
        var encoded = ConvolutionalCode.Encode(source, terminate: true, punctureRate: rate);
        var bitCount = ConvolutionalCode.GetEncodedBitLength(source.Length * 8, terminated: true, rate);
        var llrs = new double[bitCount];
        for (var i = 0; i < bitCount; i++)
        {
            var bit = (encoded[i >> 3] >> (7 - (i & 7))) & 1;
            llrs[i] = (bit == 1 ? 1.0 : -1.0) + (0.3 * ((rng.NextDouble() * 2) - 1));
        }

        var decoded = ConvolutionalCode.DecodeSoft(llrs, source.Length, terminated: true, punctureRate: rate);

        Assert.Equal(source, decoded);
    }

    /// <summary>
    /// ハード判定ビタビの AVX2 経路が、スカラ経路と同じ復号結果・メトリクスになることを確かめます（誤り多めで同点の経路も通す）。
    /// </summary>
    [Theory]
    [InlineData(ConvolutionalCode.PunctureRate.Rate1_2, true, 0.0)]
    [InlineData(ConvolutionalCode.PunctureRate.Rate1_2, true, 0.04)]
    [InlineData(ConvolutionalCode.PunctureRate.Rate2_3, true, 0.03)]
    [InlineData(ConvolutionalCode.PunctureRate.Rate3_4, true, 0.02)]
    [InlineData(ConvolutionalCode.PunctureRate.Rate1_2, false, 0.04)]
    [InlineData(ConvolutionalCode.PunctureRate.Rate3_4, false, 0.15)]
    public void HardDecode_SimdMatchesScalar(ConvolutionalCode.PunctureRate rate, bool terminated, double errorRate)
    {
        var rng = new Random(23);
        var source = new byte[1045];
        rng.NextBytes(source);
        var encoded = ConvolutionalCode.Encode(source, terminate: terminated, punctureRate: rate);
        var bitCount = ConvolutionalCode.GetEncodedBitLength(source.Length * 8, terminated, rate);
        for (var i = 0; i < bitCount; i++)
        {
            if (rng.NextDouble() < errorRate)
            {
                encoded[i >> 3] ^= (byte)(0x80 >> (i & 7));
            }
        }

        var expected = ConvolutionalCode.DecodeHard(encoded, source.Length, out var expectedMetrics, terminated, rate, Kernel.Scalar);
        var actual = ConvolutionalCode.DecodeHard(encoded, source.Length, out var actualMetrics, terminated, rate, Kernel.Auto);

        Assert.Equal(expected, actual);
        Assert.Equal(expectedMetrics, actualMetrics);
        if (errorRate == 0.0)
        {
            Assert.Equal(source, actual);
        }
    }
}
