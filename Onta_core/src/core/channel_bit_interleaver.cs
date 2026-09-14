using System.Collections.Concurrent;

namespace Onta.Core;

/// <summary>
/// FH / BH / BD で共通利用するチャネルビットインターリーバです。
/// 復号時は RS / ターボ復号の前段で逆順序化し、31bit M 系列で並び順を生成します。
/// </summary>
public static class ChannelBitInterleaver
{
    /// <summary>FH 用の並び替えシードです。</summary>
    public const int SeedFileHeader = unchecked((int)0xB17F11E1);

    /// <summary>BH / BD 用の並び替えシードです。</summary>
    public const int SeedBlock = unchecked((int)0xB17B10C1);

    /// <summary>
    /// 順方向インターリーブ順列のキャッシュです。
    /// </summary>
    private static readonly ConcurrentDictionary<(int Length, int Seed), int[]> ForwardCache = new();

    /// <summary>
    /// 逆方向インターリーブ順列のキャッシュです。
    /// </summary>
    private static readonly ConcurrentDictionary<(int Length, int Seed), int[]> InverseCache = new();

    /// <summary>
    /// バイト列をビット列へ展開してインターリーブします（MSB 順）。
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
    /// インターリーブ済みのバイト列を元の順序へ復元します。
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
    /// インターリーブ済みビット列を逆変換します。
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
    /// 31bit M 系列（x^31+x^28+1）を使って Fisher-Yates 順列を生成します。
    /// </summary>
    private static int[] BuildInterleaver(int length, int seed)
    {
        var permutation = new int[length];
        for (var i = 0; i < length; i++)
        {
            permutation[i] = i;
        }

        var state = MSequence31.InitializeState(MSequenceUsage.ChannelBitInterleave, seed);
        for (var i = length - 1; i > 0; i--)
        {
            var word = MSequence31.NextWord(ref state);
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


