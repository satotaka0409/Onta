using System;

namespace Otofa.Core;

public static class GrayCode
{
    public static byte Encode(byte value)
    {
        return (byte)(value ^ (value >> 1));
    }

    public static ushort Encode(ushort value)
    {
        return (ushort)(value ^ (value >> 1));
    }

    public static uint Encode(uint value)
    {
        return value ^ (value >> 1);
    }

    public static ulong Encode(ulong value)
    {
        return value ^ (value >> 1);
    }

    public static byte Decode(byte gray)
    {
        byte value = gray;
        value ^= (byte)(value >> 1);
        value ^= (byte)(value >> 2);
        value ^= (byte)(value >> 4);
        return value;
    }

    public static ushort Decode(ushort gray)
    {
        ushort value = gray;
        value ^= (ushort)(value >> 1);
        value ^= (ushort)(value >> 2);
        value ^= (ushort)(value >> 4);
        value ^= (ushort)(value >> 8);
        return value;
    }

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
