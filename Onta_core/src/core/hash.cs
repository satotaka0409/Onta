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
    /// <param name="data">ハッシュ計算対象のバイト配列。</param>
    /// <returns>32 バイトの SHA-256 ダイジェスト。</returns>
    public static byte[] ComputeSha256(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return SHA256.HashData(data);
    }

    /// <summary>
    /// SpanデータからSHA-256を計算します。
    /// </summary>
    /// <param name="data">ハッシュ計算対象のバイト列（Span）。</param>
    /// <returns>32 バイトの SHA-256 ダイジェスト。</returns>
    public static byte[] ComputeSha256(ReadOnlySpan<byte> data)
    {
        return SHA256.HashData(data);
    }

    /// <summary>
    /// バイト配列からSHA-512を計算します。
    /// </summary>
    /// <param name="data">ハッシュ計算対象のバイト配列。</param>
    /// <returns>64 バイトの SHA-512 ダイジェスト。</returns>
    public static byte[] ComputeSha512(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return SHA512.HashData(data);
    }

    /// <summary>
    /// SpanデータからSHA-512を計算します。
    /// </summary>
    /// <param name="data">ハッシュ計算対象のバイト列（Span）。</param>
    /// <returns>64 バイトの SHA-512 ダイジェスト。</returns>
    public static byte[] ComputeSha512(ReadOnlySpan<byte> data)
    {
        return SHA512.HashData(data);
    }
}

