namespace Onta.Core;

/// <summary>
/// M 系列の用途種別です。
/// </summary>
public enum MSequenceUsage
{
    /// <summary>ターボ符号インターリーバ用途。</summary>
    TurboEccInterleaver,

    /// <summary>WOW/Flutter 位相生成用途。</summary>
    WowFlutterPhase
}

/// <summary>
/// 31bit M 系列（x^31 + x^28 + 1）を生成するユーティリティです。
/// </summary>
public static class MSequence31
{
    /// <summary>
    /// SplitMix の拡散定数です。
    /// </summary>
    private const ulong SplitMixConst = 0x9E3779B97F4A7C15UL;

    /// <summary>
    /// 31bit 整数を [0, 1) の実数へ正規化する係数です。
    /// </summary>
    private const double InvM31 = 1.0 / 2147483648.0;

    /// <summary>
    /// 用途に応じた 31bit M 系列状態を初期化します。
    /// </summary>
    /// <param name="usage">系列の用途種別。</param>
    /// <param name="seed">基本シード値。</param>
    /// <returns>ゼロを除く 31bit の初期状態。</returns>
    public static uint InitializeState(
        MSequenceUsage usage,
        int seed)
    {
        ulong x;
        switch (usage)
        {
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
    /// M 系列を 31 ステップ進めて 31bit ワードを生成します。
    /// </summary>
    /// <param name="state">更新対象の系列状態。</param>
    /// <returns>生成された 31bit ワード。</returns>
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
    /// M 系列ワードを [0, 1) の実数として返します。
    /// </summary>
    /// <param name="state">更新対象の系列状態。</param>
    /// <returns>[0, 1) の擬似乱数。</returns>
    public static double NextUnitDouble(ref uint state)
    {
        return NextWord(ref state) * InvM31;
    }

    /// <summary>
    /// M 系列状態を 1 ステップ進めます。
    /// </summary>
    /// <param name="state">現在の系列状態。</param>
    /// <returns>次の系列状態（ゼロ状態は 1 へ補正）。</returns>
    public static uint Advance31(uint state)
    {
        // Primitive polynomial: x^31 + x^28 + 1
        var feedback = ((state >> 30) ^ (state >> 27)) & 1u;
        state = ((state << 1) & 0x7FFFFFFF) | feedback;
        return state == 0 ? 1u : state;
    }
}
