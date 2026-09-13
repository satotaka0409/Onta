using System.Collections.Concurrent;

namespace Onta.Core;

/// <summary>
/// FH / BH / BD 共通のビットインターリーブです。
/// 送信: RS/ターボ前段、受信: RS/ターボ後段。並び替えは 31bit M 系列で生成します。
/// </summary>
public static class ChannelBitInterleaver
{
    /// <summary>FH 用の並び替えシードです。</summary>
    public const int SeedFileHeader = unchecked((int)0xB17F11E1);

    /// <summary>BH / BD 用の並び替えシードです。</summary>
    public const int SeedBlock = unchecked((int)0xB17B10C1);

    private static readonly ConcurrentDictionary<(int Length, int Seed), int[]> ForwardCache = new();
    private static readonly ConcurrentDictionary<(int Length, int Seed), int[]> InverseCache = new();

    /// <summary>
    /// バイト列をビット単位でインターリーブします（MSB 先）。
    /// </summary>
    public static byte[] InterleaveBytes(ReadOnlySpan<byte> bytes, int seed)
    {
        if (bytes.Length == 0)
        {
            return [];
        }

        var bits = BytesToBitsMsb(bytes);
        var interleaved = Interleave(bits, seed);
        return BitsToBytesMsb(interleaved);
    }

    /// <summary>
    /// インターリーブ済みバイト列をビット単位で元順へ戻します。
    /// </summary>
    public static byte[] DeinterleaveBytes(ReadOnlySpan<byte> bytes, int seed)
    {
        if (bytes.Length == 0)
        {
            return [];
        }

        var bits = BytesToBitsMsb(bytes);
        var restored = Deinterleave(bits, seed);
        return BitsToBytesMsb(restored);
    }

    /// <summary>
    /// ビット列をインターリーブします。
    /// </summary>
    public static bool[] Interleave(ReadOnlySpan<bool> bits, int seed)
    {
        if (bits.Length == 0)
        {
            return [];
        }

        var permutation = GetForward(bits.Length, seed);
        var output = new bool[bits.Length];
        for (var i = 0; i < bits.Length; i++)
        {
            output[i] = bits[permutation[i]];
        }

        return output;
    }

    /// <summary>
    /// インターリーブ済みビット列を元順へ戻します。
    /// </summary>
    public static bool[] Deinterleave(ReadOnlySpan<bool> bits, int seed)
    {
        if (bits.Length == 0)
        {
            return [];
        }

        var inverse = GetInverse(bits.Length, seed);
        var output = new bool[bits.Length];
        for (var i = 0; i < bits.Length; i++)
        {
            output[i] = bits[inverse[i]];
        }

        return output;
    }

    private static int[] GetForward(int length, int seed)
    {
        return ForwardCache.GetOrAdd((length, seed), static key => BuildInterleaver(key.Length, key.Seed));
    }

    private static int[] GetInverse(int length, int seed)
    {
        return InverseCache.GetOrAdd(
            (length, seed),
            static key =>
            {
                var forward = ForwardCache.GetOrAdd(
                    key,
                    static inner => BuildInterleaver(inner.Length, inner.Seed));
                return BuildDeinterleaver(forward);
            });
    }

    /// <summary>
    /// 31bit M 系列（x^31+x^28+1）で Fisher–Yates 置換を構築します。
    /// </summary>
    private static int[] BuildInterleaver(int length, int seed)
    {
        var permutation = new int[length];
        for (var i = 0; i < length; i++)
        {
            permutation[i] = i;
        }

        var state = InitializeMSequence31(seed);
        for (var i = length - 1; i > 0; i--)
        {
            var word = NextMSequenceWord(ref state);
            var j = (int)(word % (uint)(i + 1));
            (permutation[i], permutation[j]) = (permutation[j], permutation[i]);
        }

        return permutation;
    }

    private static int[] BuildDeinterleaver(int[] interleaver)
    {
        var deinterleaver = new int[interleaver.Length];
        for (var i = 0; i < interleaver.Length; i++)
        {
            deinterleaver[interleaver[i]] = i;
        }

        return deinterleaver;
    }

    private static uint InitializeMSequence31(int seed)
    {
        ulong x = unchecked((uint)seed);
        x ^= 0xA5A5A5A5u;
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        x ^= x >> 31;
        var state = (uint)(x & 0x7FFFFFFF);
        return state == 0 ? 1u : state;
    }

    private static uint NextMSequenceWord(ref uint state)
    {
        var value = 0u;
        for (var i = 0; i < 31; i++)
        {
            state = AdvanceMSequence31(state);
            value = (value << 1) | (state & 1u);
        }

        return value;
    }

    private static uint AdvanceMSequence31(uint state)
    {
        // Primitive polynomial: x^31 + x^28 + 1（OFDM 周波数インターリーブと同じ）
        var feedback = ((state >> 30) ^ (state >> 27)) & 1u;
        state = ((state << 1) & 0x7FFFFFFF) | feedback;
        return state == 0 ? 1u : state;
    }

    private static bool[] BytesToBitsMsb(ReadOnlySpan<byte> bytes)
    {
        var bits = new bool[bytes.Length * 8];
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            var baseIndex = i * 8;
            for (var bit = 0; bit < 8; bit++)
            {
                bits[baseIndex + bit] = ((b >> (7 - bit)) & 1) != 0;
            }
        }

        return bits;
    }

    private static byte[] BitsToBytesMsb(ReadOnlySpan<bool> bits)
    {
        if ((bits.Length & 7) != 0)
        {
            throw new ArgumentException("Bit length must be a multiple of 8.", nameof(bits));
        }

        var bytes = new byte[bits.Length / 8];
        for (var i = 0; i < bytes.Length; i++)
        {
            byte b = 0;
            var baseIndex = i * 8;
            for (var bit = 0; bit < 8; bit++)
            {
                if (bits[baseIndex + bit])
                {
                    b |= (byte)(1 << (7 - bit));
                }
            }

            bytes[i] = b;
        }

        return bytes;
    }
}
