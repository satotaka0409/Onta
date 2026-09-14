namespace Onta.Core;

/// <summary>
/// M 系列の用途を表します。
/// </summary>
public enum MSequenceUsage
{
    /// <summary>チャネルビットインターリーブ用途。</summary>
    ChannelBitInterleave,

    /// <summary>OFDM 周波数インターリーブ（左チャネル）用途。</summary>
    OfdmFrequencyInterleaveLeft,

    /// <summary>OFDM 周波数インターリーブ（右チャネル）用途。</summary>
    OfdmFrequencyInterleaveRight,

    /// <summary>ターボ符号インターリーバ用途。</summary>
    TurboEccInterleaver,

    /// <summary>Wow/Flutter 位相生成用途。</summary>
    WowFlutterPhase
}

/// <summary>
/// 31bit M 系列（x^31+x^28+1）ユーティリティです。
/// </summary>
public static class MSequence31
{
    private const ulong SplitMixConst = 0x9E3779B97F4A7C15UL;
    private const double InvM31 = 1.0 / 2147483648.0;

    /// <summary>
    /// 用途に応じた 31bit M 系列状態を初期化します。
    /// </summary>
    /// <param name="usage">用途。</param>
    /// <param name="seed">主シード（用途に応じて解釈）。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ初期シード。</param>
    /// <param name="epoch">周波数インターリーブエポック。</param>
    /// <returns>初期状態（0 は返しません）。</returns>
    public static uint InitializeState(
        MSequenceUsage usage,
        int seed,
        int interleaveInitSeed = 0,
        long epoch = 0)
    {
        ulong x;
        switch (usage)
        {
            case MSequenceUsage.ChannelBitInterleave:
                x = unchecked((uint)seed);
                x ^= 0xA5A5A5A5u;
                break;

            case MSequenceUsage.OfdmFrequencyInterleaveLeft:
                x = (uint)(seed == 0 ? 1 : seed);
                x ^= (uint)interleaveInitSeed;
                x ^= 0x5A5A5A5Au;
                x ^= unchecked((ulong)epoch * SplitMixConst);
                break;

            case MSequenceUsage.OfdmFrequencyInterleaveRight:
                x = (uint)(seed == 0 ? 1 : seed);
                x ^= (uint)interleaveInitSeed;
                x ^= 0xA5A5A5A5u;
                x ^= unchecked((ulong)epoch * SplitMixConst);
                break;

            case MSequenceUsage.TurboEccInterleaver:
                x = unchecked((uint)seed);
                x ^= 0x3C6EF372u;
                break;

            case MSequenceUsage.WowFlutterPhase:
                x = (uint)(seed == 0 ? 1 : seed);
                x ^= 0x1B56C4E9u;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(usage), usage, "Unsupported M-sequence usage.");
        }

        x += SplitMixConst;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        x ^= x >> 31;

        var state = (uint)(x & 0x7FFFFFFF);
        return state == 0 ? 1u : state;
    }

    /// <summary>
    /// 31bit M 系列状態から 31bit ワードを生成します。
    /// </summary>
    public static uint NextWord(ref uint state)
    {
        var value = 0u;
        for (var i = 0; i < 31; i++)
        {
            var feedback = ((state >> 30) ^ (state >> 27)) & 1u;
            state = ((state << 1) & 0x7FFFFFFF) | feedback;
            if (state == 0)
            {
                state = 1u;
            }

            value = (value << 1) | (state & 1u);
        }

        return value;
    }

    /// <summary>
    /// 31bit M 系列状態から [0,1) の一様値を生成します。
    /// </summary>
    public static double NextUnitDouble(ref uint state)
    {
        return NextWord(ref state) * InvM31;
    }

    /// <summary>
    /// 31bit M 系列状態を 1 ステップ進めます。
    /// </summary>
    public static uint Advance31(uint state)
    {
        // Primitive polynomial: x^31 + x^28 + 1
        var feedback = ((state >> 30) ^ (state >> 27)) & 1u;
        state = ((state << 1) & 0x7FFFFFFF) | feedback;
        return state == 0 ? 1u : state;
    }
}
