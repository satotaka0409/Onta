using System.Security.Cryptography;

namespace Onta.Core;

/// <summary>
/// SHA-256 / SHA-512 ハッシュを計算するユーティリティです。
/// </summary>
public static class Hash
{
    /// <summary>
    /// 入力バイト列の SHA-256 ハッシュ値を計算します。
    /// </summary>
    /// <param name="data">ハッシュ化対象のバイト列。</param>
    /// <returns>32 バイトの SHA-256 ハッシュ値。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> が null の場合にスローされます。</exception>
    public static byte[] ComputeSha256(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return SHA256.HashData(data);
    }

    /// <summary>
    /// 入力スパンの SHA-256 ハッシュ値を計算します。
    /// </summary>
    /// <param name="data">ハッシュ化対象のバイト列。</param>
    /// <returns>32 バイトの SHA-256 ハッシュ値。</returns>
    public static byte[] ComputeSha256(ReadOnlySpan<byte> data)
    {
        return SHA256.HashData(data);
    }

    /// <summary>
    /// 入力バイト列の SHA-512 ハッシュ値を計算します。
    /// </summary>
    /// <param name="data">ハッシュ化対象のバイト列。</param>
    /// <returns>64 バイトの SHA-512 ハッシュ値。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> が null の場合にスローされます。</exception>
    public static byte[] ComputeSha512(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return SHA512.HashData(data);
    }

    /// <summary>
    /// 入力スパンの SHA-512 ハッシュ値を計算します。
    /// </summary>
    /// <param name="data">ハッシュ化対象のバイト列。</param>
    /// <returns>64 バイトの SHA-512 ハッシュ値。</returns>
    public static byte[] ComputeSha512(ReadOnlySpan<byte> data)
    {
        return SHA512.HashData(data);
    }
}
