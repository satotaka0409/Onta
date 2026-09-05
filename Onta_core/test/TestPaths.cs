namespace Onta.Core.Tests;

/// <summary>
/// テスト用入力ファイルのパス解決です。
/// </summary>
internal static class TestPaths
{
    /// <summary>
    /// 既定の入力 PNG（files/QR_326213.png）を解決します。
    /// </summary>
    public static string ResolveInputPng()
    {
        var candidates = new[]
        {
            Path.Combine("files", "QR_326213.png"),
            Path.Combine("..", "files", "QR_326213.png"),
            Path.Combine("..", "..", "..", "..", "files", "QR_326213.png"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "files", "QR_326213.png"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "files", "QR_326213.png"),
            @"C:\proj\Onta\files\QR_326213.png",
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
            {
                return full;
            }
        }

        throw new FileNotFoundException("入力ファイル files/QR_326213.png が見つかりません。");
    }
}
