using System.IO;

namespace Onta.View.Core;

/// <summary>
/// 全ブロックがそろった受信ファイルを出力フォルダーへ書き出します。
/// </summary>
internal static class ReceivedFileWriter
{
    private const string FallbackFileName = "received.bin";

    /// <summary>
    /// 受信ファイルを出力フォルダーへ書き出します。同名ファイルが同じ内容ならそれを返し、違う内容なら「名前 (2).拡張子」のように番号を付けます。
    /// </summary>
    /// <param name="outputDirectory">出力フォルダー。</param>
    /// <param name="fileName">FH のファイル名。</param>
    /// <param name="payload">復元したファイルの内容。</param>
    /// <returns>書き出した（または同じ内容で既にあった）ファイルのフルパス。</returns>
    public static string Write(string outputDirectory, string fileName, byte[] payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(payload);

        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);

        var name = SanitizeFileName(fileName);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var path = Path.Combine(directory, name);
        for (var n = 2; File.Exists(path); n++)
        {
            if (HasSameContent(path, payload))
            {
                return path;
            }

            path = Path.Combine(directory, $"{stem} ({n}){extension}");
        }

        File.WriteAllBytes(path, payload);
        return path;
    }

    /// <summary>
    /// FH のファイル名からフォルダー部分と使えない文字を除きます。
    /// </summary>
    /// <param name="fileName">FH のファイル名。</param>
    /// <returns>出力に使うファイル名。空になったら既定名。</returns>
    internal static string SanitizeFileName(string? fileName)
    {
        var name = (fileName ?? string.Empty).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        // Windows は末尾の空白・ピリオドを落とすため、別名で上書きしないよう先に除く
        name = new string(chars).Trim().TrimEnd('.');
        return name.Length == 0 ? FallbackFileName : name;
    }

    /// <summary>
    /// 既存ファイルが書き出す内容と同じか判定します。
    /// </summary>
    /// <param name="path">既存ファイルのパス。</param>
    /// <param name="payload">書き出す内容。</param>
    /// <returns>サイズと内容が一致すれば true。</returns>
    private static bool HasSameContent(string path, byte[] payload)
    {
        try
        {
            return new FileInfo(path).Length == payload.Length
                && File.ReadAllBytes(path).AsSpan().SequenceEqual(payload);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
