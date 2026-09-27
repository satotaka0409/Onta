namespace Onta.Stream;

/// <summary>
/// CRC-16/CCITT-FALSE（初期値 0xFFFF、多項式 0x1021、出力反転なし）です。
/// </summary>
public static class Crc16
{
    private static readonly ushort[] Table = BuildTable();

    /// <summary>
    /// 指定データの CRC-16 を計算します。
    /// </summary>
    /// <param name="data">対象データ。</param>
    /// <returns>CRC-16 値。</returns>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc = (ushort)((crc << 8) ^ Table[(crc >> 8) ^ b]);
        }

        return crc;
    }

    /// <summary>
    /// CRC-16 をビッグエンディアン 2 バイトで書き込みます。
    /// </summary>
    /// <param name="destination">書き込み先（2 バイト以上）。</param>
    /// <param name="crc">CRC 値。</param>
    public static void WriteBigEndian(Span<byte> destination, ushort crc)
    {
        destination[0] = (byte)(crc >> 8);
        destination[1] = (byte)crc;
    }

    /// <summary>
    /// ビッグエンディアン 2 バイトから CRC-16 を読み取ります。
    /// </summary>
    /// <param name="source">読み取り元。</param>
    /// <returns>CRC 値。</returns>
    public static ushort ReadBigEndian(ReadOnlySpan<byte> source)
        => (ushort)((source[0] << 8) | source[1]);

    /// <summary>
    /// データと格納 CRC が一致するか判定します。
    /// </summary>
    /// <param name="data">CRC 計算対象。</param>
    /// <param name="storedCrcBigEndian">格納 CRC（2 バイト BE）。</param>
    /// <returns>一致すれば true。</returns>
    public static bool Matches(ReadOnlySpan<byte> data, ReadOnlySpan<byte> storedCrcBigEndian)
        => Compute(data) == ReadBigEndian(storedCrcBigEndian);

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        const ushort poly = 0x1021;
        for (var i = 0; i < 256; i++)
        {
            ushort crc = (ushort)(i << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ poly)
                    : (ushort)(crc << 1);
            }

            table[i] = crc;
        }

        return table;
    }
}
