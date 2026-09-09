using System.Buffers.Binary;

namespace Onta.Core;

/// <summary>
/// IEEE CRC-32（Ethernet / ZIP と同系、多項式 0xEDB88320）です。
/// ヘッダー／ブロックデータの改ざん検出に使用します（data_struct.mdc）。
/// </summary>
public static class Crc32
{
    private static readonly uint[][] Tables = BuildTables();

    /// <summary>
    /// 指定範囲の CRC-32 を計算します。
    /// </summary>
    /// <param name="data">CRC 計算対象のバイト列。</param>
    /// <returns>CRC-32 値。</returns>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        while (data.Length >= 8)
        {
            var first = BinaryPrimitives.ReadUInt32LittleEndian(data);
            var second = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4));

            crc ^= first;
            crc = Tables[7][crc & 0xFF]
                ^ Tables[6][(crc >> 8) & 0xFF]
                ^ Tables[5][(crc >> 16) & 0xFF]
                ^ Tables[4][crc >> 24]
                ^ Tables[3][second & 0xFF]
                ^ Tables[2][(second >> 8) & 0xFF]
                ^ Tables[1][(second >> 16) & 0xFF]
                ^ Tables[0][second >> 24];

            data = data.Slice(8);
        }

        foreach (var b in data)
        {
            crc = Tables[0][(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>
    /// CRC-32 をビッグエンディアン 4 バイトで書き込みます。
    /// </summary>
    /// <param name="dest4">書き込み先（4 バイト以上）。</param>
    /// <param name="crc">書き込む CRC-32 値。</param>
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
    /// <param name="src4">読み取り元（4 バイト以上）。</param>
    /// <returns>読み取った CRC-32 値。</returns>
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
    /// <param name="data">CRC 計算対象のバイト列。</param>
    /// <param name="storedCrcBigEndian">格納済み CRC（ビッグエンディアン 4 バイト）。</param>
    /// <returns>一致すれば <see langword="true"/>。</returns>
    public static bool Matches(ReadOnlySpan<byte> data, ReadOnlySpan<byte> storedCrcBigEndian)
    {
        return Compute(data) == ReadBigEndian(storedCrcBigEndian);
    }

    /// <summary>
    /// スライディング方式用の CRC-32 ルックアップテーブル（8×256）を構築します。
    /// </summary>
    /// <returns>8 段×256 エントリの CRC テーブル。</returns>
    private static uint[][] BuildTables()
    {
        const uint poly = 0xEDB88320u;
        var tables = new uint[8][];
        var table0 = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (poly ^ (crc >> 1)) : (crc >> 1);
            }

            table0[i] = crc;
        }

        tables[0] = table0;
        for (var t = 1; t < tables.Length; t++)
        {
            tables[t] = new uint[256];
            for (var i = 0; i < 256; i++)
            {
                var crc = tables[t - 1][i];
                tables[t][i] = table0[crc & 0xFF] ^ (crc >> 8);
            }
        }

        return tables;
    }
}
