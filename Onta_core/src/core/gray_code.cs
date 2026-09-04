using System;

namespace Otofa.Core;

/// <summary>
/// 整数値およびバイト配列に対するグレイコードの符号化・復号ユーティリティを提供します。
/// </summary>
public static class GrayCode
{
    /// <summary>
    /// 8 ビットの2進値をグレイコードへ変換します。
    /// </summary>
    /// <param name="value">2進値。</param>
    /// <returns>グレイコード化された値。</returns>
    public static byte Encode(byte value)
    {
        return (byte)(value ^ (value >> 1));
    }

    /// <summary>
    /// 16 ビットの2進値をグレイコードへ変換します。
    /// </summary>
    /// <param name="value">2進値。</param>
    /// <returns>グレイコード化された値。</returns>
    public static ushort Encode(ushort value)
    {
        return (ushort)(value ^ (value >> 1));
    }

    /// <summary>
    /// 32 ビットの2進値をグレイコードへ変換します。
    /// </summary>
    /// <param name="value">2進値。</param>
    /// <returns>グレイコード化された値。</returns>
    public static uint Encode(uint value)
    {
        return value ^ (value >> 1);
    }

    /// <summary>
    /// 64 ビットの2進値をグレイコードへ変換します。
    /// </summary>
    /// <param name="value">2進値。</param>
    /// <returns>グレイコード化された値。</returns>
    public static ulong Encode(ulong value)
    {
        return value ^ (value >> 1);
    }

    /// <summary>
    /// 8 ビットのグレイコード値を2進値へ復号します。
    /// </summary>
    /// <param name="gray">グレイコード値。</param>
    /// <returns>復号された2進値。</returns>
    public static byte Decode(byte gray)
    {
        byte value = gray;
        value ^= (byte)(value >> 1);
        value ^= (byte)(value >> 2);
        value ^= (byte)(value >> 4);
        return value;
    }

    /// <summary>
    /// 16 ビットのグレイコード値を2進値へ復号します。
    /// </summary>
    /// <param name="gray">グレイコード値。</param>
    /// <returns>復号された2進値。</returns>
    public static ushort Decode(ushort gray)
    {
        ushort value = gray;
        value ^= (ushort)(value >> 1);
        value ^= (ushort)(value >> 2);
        value ^= (ushort)(value >> 4);
        value ^= (ushort)(value >> 8);
        return value;
    }

    /// <summary>
    /// 32 ビットのグレイコード値を2進値へ復号します。
    /// </summary>
    /// <param name="gray">グレイコード値。</param>
    /// <returns>復号された2進値。</returns>
    public static uint Decode(uint gray)
    {
        uint value = gray;
        value ^= value >> 1;
        value ^= value >> 2;
        value ^= value >> 4;
        value ^= value >> 8;
        value ^= value >> 16;
        return value;
    }

    /// <summary>
    /// 64 ビットのグレイコード値を2進値へ復号します。
    /// </summary>
    /// <param name="gray">グレイコード値。</param>
    /// <returns>復号された2進値。</returns>
    public static ulong Decode(ulong gray)
    {
        ulong value = gray;
        value ^= value >> 1;
        value ^= value >> 2;
        value ^= value >> 4;
        value ^= value >> 8;
        value ^= value >> 16;
        value ^= value >> 32;
        return value;
    }

    /// <summary>
    /// 入力配列の各バイトをグレイコード化します。
    /// </summary>
    /// <param name="values">入力の2進バイト配列。</param>
    /// <returns>同じ順序で並んだグレイコード化バイト配列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> が null の場合にスローされます。</exception>
    public static byte[] EncodeBytes(byte[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var encoded = new byte[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            encoded[i] = Encode(values[i]);
        }

        return encoded;
    }

    /// <summary>
    /// 入力配列の各グレイコードバイトを2進値へ復号します。
    /// </summary>
    /// <param name="grayValues">入力のグレイコード化バイト配列。</param>
    /// <returns>同じ順序で並んだ復号後の2進バイト配列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="grayValues"/> が null の場合にスローされます。</exception>
    public static byte[] DecodeBytes(byte[] grayValues)
    {
        ArgumentNullException.ThrowIfNull(grayValues);

        var decoded = new byte[grayValues.Length];
        for (var i = 0; i < grayValues.Length; i++)
        {
            decoded[i] = Decode(grayValues[i]);
        }

        return decoded;
    }
}
