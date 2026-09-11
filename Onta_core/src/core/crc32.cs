using System.Buffers.Binary;

namespace Onta.Core;

/// <summary>
/// CRC-32 を計算・検証するユーティリティです。
/// </summary>
public static class Crc32
{
    private static readonly uint[][] Tables = BuildTables();

    /// <summary>
    /// 指定データのCRC-32値を計算します。
    /// </summary>
    /// <param name="data">CRC計算対象のデータ。</param>
    /// <returns>CRC-32値。</returns>
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
    /// CRC-32値をビッグエンディアンで書き込みます。
    /// </summary>
    /// <param name="dest4">書き込み先バッファ（4バイト以上）。</param>
    /// <param name="crc">書き込むCRC-32値。</param>
    public static void WriteBigEndian(Span<byte> dest4, uint crc)
    {
        if (dest4.Length < 4)
        {
            throw new ArgumentException("Destination must be at least 4 bytes.", nameof(dest4));
        }

        BinaryPrimitives.WriteUInt32BigEndian(dest4, crc);
    }

    /// <summary>
    /// ビッグエンディアン4バイトからCRC-32値を読み取ります。
    /// </summary>
    /// <param name="src4">読み取り元バッファ（4バイト以上）。</param>
    /// <returns>読み取ったCRC-32値。</returns>
    public static uint ReadBigEndian(ReadOnlySpan<byte> src4)
    {
        if (src4.Length < 4)
        {
            throw new ArgumentException("Source must be at least 4 bytes.", nameof(src4));
        }

        return BinaryPrimitives.ReadUInt32BigEndian(src4);
    }

    /// <summary>
    /// データと格納CRCが一致するか判定します。
    /// </summary>
    /// <param name="data">検証対象データ。</param>
    /// <param name="storedCrcBigEndian">比較対象のCRC値（ビッグエンディアン）。</param>
    /// <returns>一致する場合 true。</returns>
    public static bool Matches(ReadOnlySpan<byte> data, ReadOnlySpan<byte> storedCrcBigEndian)
    {
        return Compute(data) == ReadBigEndian(storedCrcBigEndian);
    }

    /// <summary>
    /// slicing-by-8 用のCRCテーブルを構築します。
    /// </summary>
    /// <returns>処理結果。</returns>
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

