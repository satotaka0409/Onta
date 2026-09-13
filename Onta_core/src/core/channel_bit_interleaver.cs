using System.Collections.Concurrent;

namespace Onta.Core;

/// <summary>
/// FH / BH / BD 共通のチャネル・ビットインターリーブです。
/// 畳み込み符号化後〜変調前に適用し、復調 LLR（またはハードビット）を逆変換してから復号します。
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
    /// 符号化ビット列をインターリーブします。
    /// </summary>
    /// <param name="bits">入力ビット列。</param>
    /// <param name="seed">並び替えシード（FH と BH/BD で分ける）。</param>
    /// <returns>インターリーブ後ビット列。</returns>
    public static bool[] Interleave(ReadOnlySpan<bool> bits, int seed)
    {
        if (bits.Length == 0)
        {
            return Array.Empty<bool>();
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
    /// インターリーブ済みハードビット列を元順へ戻します。
    /// </summary>
    /// <param name="bits">インターリーブ済みビット列。</param>
    /// <param name="seed">並び替えシード。</param>
    /// <returns>元順のビット列。</returns>
    public static bool[] Deinterleave(ReadOnlySpan<bool> bits, int seed)
    {
        if (bits.Length == 0)
        {
            return Array.Empty<bool>();
        }

        var inverse = GetInverse(bits.Length, seed);
        var output = new bool[bits.Length];
        for (var i = 0; i < bits.Length; i++)
        {
            output[i] = bits[inverse[i]];
        }

        return output;
    }

    /// <summary>
    /// インターリーブ済みソフト LLR を元順へ戻します（硬判定を挟まない）。
    /// </summary>
    /// <param name="llrs">インターリーブ済み LLR。</param>
    /// <param name="seed">並び替えシード。</param>
    /// <returns>元順の LLR。</returns>
    public static double[] Deinterleave(ReadOnlySpan<double> llrs, int seed)
    {
        if (llrs.Length == 0)
        {
            return Array.Empty<double>();
        }

        var inverse = GetInverse(llrs.Length, seed);
        var output = new double[llrs.Length];
        for (var i = 0; i < llrs.Length; i++)
        {
            output[i] = llrs[inverse[i]];
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

    private static int[] BuildInterleaver(int length, int seed)
    {
        var permutation = new int[length];
        for (var i = 0; i < length; i++)
        {
            permutation[i] = i;
        }

        var random = new Random(seed);
        for (var i = length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
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
}
