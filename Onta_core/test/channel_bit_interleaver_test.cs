using Onta.Core;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// チャネル・ビットインターリーブの往復とシード分離を検証します。
/// </summary>
public sealed class ChannelBitInterleaverTest
{
    [Fact]
    public void InterleaveDeinterleave_Bits_RoundTrips()
    {
        var bits = new bool[257];
        for (var i = 0; i < bits.Length; i++)
        {
            bits[i] = (i * 17 & 1) != 0;
        }

        var interleaved = ChannelBitInterleaver.Interleave(bits, ChannelBitInterleaver.SeedFileHeader);
        Assert.False(bits.AsSpan().SequenceEqual(interleaved));
        var restored = ChannelBitInterleaver.Deinterleave(interleaved, ChannelBitInterleaver.SeedFileHeader);
        Assert.True(bits.AsSpan().SequenceEqual(restored));
    }

    [Fact]
    public void InterleaveDeinterleave_Llrs_RoundTrips()
    {
        var source = new bool[128];
        for (var i = 0; i < source.Length; i++)
        {
            source[i] = (i & 1) != 0;
        }

        var interleaved = ChannelBitInterleaver.Interleave(source, ChannelBitInterleaver.SeedBlock);
        var llrs = new double[interleaved.Length];
        for (var i = 0; i < interleaved.Length; i++)
        {
            llrs[i] = interleaved[i] ? -2.5 : 2.5;
        }

        var restoredLlrs = ChannelBitInterleaver.Deinterleave(llrs, ChannelBitInterleaver.SeedBlock);
        for (var i = 0; i < source.Length; i++)
        {
            var expected = source[i] ? -2.5 : 2.5;
            Assert.Equal(expected, restoredLlrs[i]);
        }
    }

    [Fact]
    public void FileHeaderAndBlockSeeds_ProduceDifferentOrders()
    {
        var bits = new bool[64];
        for (var i = 0; i < bits.Length; i++)
        {
            bits[i] = i < 32;
        }

        var fh = ChannelBitInterleaver.Interleave(bits, ChannelBitInterleaver.SeedFileHeader);
        var bd = ChannelBitInterleaver.Interleave(bits, ChannelBitInterleaver.SeedBlock);
        Assert.False(fh.AsSpan().SequenceEqual(bd));
    }
}
