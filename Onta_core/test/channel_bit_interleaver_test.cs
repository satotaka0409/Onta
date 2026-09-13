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
    public void InterleaveDeinterleave_Bytes_RoundTrips()
    {
        var source = new byte[160];
        for (var i = 0; i < source.Length; i++)
        {
            source[i] = (byte)(i * 37 + 11);
        }

        var interleaved = ChannelBitInterleaver.InterleaveBytes(source, ChannelBitInterleaver.SeedBlock);
        Assert.False(source.AsSpan().SequenceEqual(interleaved));
        var restored = ChannelBitInterleaver.DeinterleaveBytes(interleaved, ChannelBitInterleaver.SeedBlock);
        Assert.True(source.AsSpan().SequenceEqual(restored));
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

    [Fact]
    public void Interleave_UsesMSequence_NotIdentity()
    {
        var bits = new bool[31];
        for (var i = 0; i < bits.Length; i++)
        {
            bits[i] = i % 3 == 0;
        }

        var interleaved = ChannelBitInterleaver.Interleave(bits, ChannelBitInterleaver.SeedFileHeader);
        Assert.False(bits.AsSpan().SequenceEqual(interleaved));
        // 同一シード・同一長は決定的
        var again = ChannelBitInterleaver.Interleave(bits, ChannelBitInterleaver.SeedFileHeader);
        Assert.True(interleaved.AsSpan().SequenceEqual(again));
    }
}
