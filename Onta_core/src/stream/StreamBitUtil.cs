namespace Onta.Stream;

/// <summary>
/// ストリーム用のビット／バイト変換です（MSB ファースト）。
/// </summary>
internal static class StreamBitUtil
{
    /// <summary>
    /// バイト列を MSB ファーストのビット列へ展開します。
    /// </summary>
    public static bool[] BytesToBitsMsb(ReadOnlySpan<byte> bytes)
    {
        var bits = new bool[bytes.Length * 8];
        for (var i = 0; i < bytes.Length; i++)
        {
            for (var b = 0; b < 8; b++)
            {
                bits[(i * 8) + b] = ((bytes[i] >> (7 - b)) & 1) == 1;
            }
        }

        return bits;
    }

    /// <summary>
    /// MSB ファーストのビット列をバイト列へパックします。
    /// </summary>
    public static byte[] BitsToBytesMsb(ReadOnlySpan<bool> bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
        {
            if (!bits[i])
            {
                continue;
            }

            bytes[i / 8] |= (byte)(1 << (7 - (i % 8)));
        }

        return bytes;
    }

    /// <summary>
    /// ソフト判定結果（0/1 バイト）をバイト列へパックします。
    /// </summary>
    public static byte[] SoftBytesToPayload(ReadOnlySpan<byte> softBytes, int payloadLength)
    {
        if (softBytes.Length < payloadLength)
        {
            var padded = new byte[payloadLength];
            softBytes.CopyTo(padded);
            return padded;
        }

        return softBytes[..payloadLength].ToArray();
    }

    /// <summary>
    /// ビット列を L/R へ半分ずつ分割します（余りは L 側）。
    /// </summary>
    public static (bool[] Left, bool[] Right) SplitStereoBits(ReadOnlySpan<bool> bits)
    {
        var mid = (bits.Length + 1) / 2;
        var left = bits[..mid].ToArray();
        var right = bits[mid..].ToArray();
        return (left, right);
    }

    /// <summary>
    /// L/R ビット（または LLR）を結合します。
    /// </summary>
    public static T[] Concat<T>(ReadOnlySpan<T> left, ReadOnlySpan<T> right)
    {
        var result = new T[left.Length + right.Length];
        left.CopyTo(result);
        right.CopyTo(result.AsSpan(left.Length));
        return result;
    }
}
