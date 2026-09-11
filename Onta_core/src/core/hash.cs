using System.Security.Cryptography;

namespace Onta.Core;

/// <summary>
/// SHAハッシュ計算の共通ユーティリティです。
/// </summary>
public static class Hash
{
    /// <summary>
    /// バイト配列からSHA-256を計算します。
    /// </summary>
    /// <param name="data">入力バイト配列。</param>
    /// <returns>SHA-256ハッシュ値。</returns>
    public static byte[] ComputeSha256(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return SHA256.HashData(data);
    }

    /// <summary>
    /// SpanデータからSHA-256を計算します。
    /// </summary>
    /// <param name="data">入力データ。</param>
    /// <returns>SHA-256ハッシュ値。</returns>
    public static byte[] ComputeSha256(ReadOnlySpan<byte> data)
    {
        return SHA256.HashData(data);
    }

    /// <summary>
    /// バイト配列からSHA-512を計算します。
    /// </summary>
    /// <param name="data">入力バイト配列。</param>
    /// <returns>SHA-512ハッシュ値。</returns>
    public static byte[] ComputeSha512(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return SHA512.HashData(data);
    }

    /// <summary>
    /// SpanデータからSHA-512を計算します。
    /// </summary>
    /// <param name="data">入力データ。</param>
    /// <returns>SHA-512ハッシュ値。</returns>
    public static byte[] ComputeSha512(ReadOnlySpan<byte> data)
    {
        return SHA512.HashData(data);
    }
}

