using System.Buffers.Binary;

namespace Onta.Core;

/// <summary>
/// IEEE CRC-32（Ethernet / ZIP と同系、多項式 0xEDB88320）です。
/// ヘッダー／ブロックデータの改ざん検出に使用します（data_struct.mdc）。
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>
    /// 指定範囲の CRC-32 を計算します。
    /// </summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>
    /// CRC-32 をビッグエンディアン 4 バイトで書き込みます。
    /// </summary>
    public static void WriteBigEndian(Span<byte> dest4, uint crc)
    {
        if (dest4.Length < 4)
        {
            throw new ArgumentException("Destination must be at least 4 bytes.", nameof(dest4));
        }

        BinaryPrimitives.WriteUInt32BigEndian(dest4, crc);
    }

    /// <summary>
    /// ビッグエンディアン 4 バイトから CRC-32 を読み取ります。
    /// </summary>
    public static uint ReadBigEndian(ReadOnlySpan<byte> src4)
    {
        if (src4.Length < 4)
        {
            throw new ArgumentException("Source must be at least 4 bytes.", nameof(src4));
        }

        return BinaryPrimitives.ReadUInt32BigEndian(src4);
    }

    /// <summary>
    /// 計算値と格納値が一致するか検証します。
    /// </summary>
    public static bool Matches(ReadOnlySpan<byte> data, ReadOnlySpan<byte> storedCrcBigEndian)
    {
        return Compute(data) == ReadBigEndian(storedCrcBigEndian);
    }

    private static uint[] BuildTable()
    {
        const uint poly = 0xEDB88320u;
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (poly ^ (crc >> 1)) : (crc >> 1);
            }

            table[i] = crc;
        }

        return table;
    }
}
