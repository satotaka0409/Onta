using Onta.Core;
using Xunit;

namespace Onta.Core.Tests;

public sealed class ConvolutionalCodePunctureTests
{
    [Fact]
    public void Rate3_4_UsesEffectiveThreeQuarterPuncturing()
    {
        // 6 input bits -> mother code 12 bits at rate 1/2.
        // Effective rate 3/4 must transmit 8 bits (12 * 2/3).
        var encodedBits = ConvolutionalCode.GetEncodedBitLength(
            originalBitLength: 6,
            terminated: false,
            punctureRate: ConvolutionalCode.PunctureRate.Rate3_4);

        Assert.Equal(8, encodedBits);
    }

    [Fact]
    public void Rate3_4_HardDecode_RoundTripsWithoutNoise()
    {
        var source = new byte[] { 0x00, 0x11, 0x22, 0x55, 0xAA, 0xFF, 0x7E, 0x81 };

        var encoded = ConvolutionalCode.Encode(
            source,
            terminate: true,
            punctureRate: ConvolutionalCode.PunctureRate.Rate3_4);

        var decoded = ConvolutionalCode.Decode(
            encoded,
            source.Length,
            terminated: true,
            punctureRate: ConvolutionalCode.PunctureRate.Rate3_4);

        Assert.Equal(source, decoded);
    }
}

