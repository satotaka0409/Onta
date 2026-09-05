namespace Onta.Core.Tests;

/// <summary>
/// テスト用入出力パスの解決です（in_files / out_files）。
/// </summary>
internal static class TestPaths
{
    private const string InputFileName = "QR_326213.png";

    /// <summary>
    /// 既定の入力 PNG（in_files/QR_326213.png）を解決します。
    /// </summary>
    public static string ResolveInputPng()
    {
        foreach (var candidate in EnumerateInputCandidates(InputFileName))
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
            {
                return full;
            }
        }

        throw new FileNotFoundException($"入力ファイル in_files/{InputFileName} が見つかりません。");
    }

    /// <summary>
    /// 出力ディレクトリ（out_files）を解決し、なければ作成します。
    /// </summary>
    public static string ResolveOutputDir()
    {
        foreach (var candidate in EnumerateOutputDirCandidates())
        {
            var full = Path.GetFullPath(candidate);
            var parent = Path.GetDirectoryName(full);
            // out_files の親（Onta ルート）が見える候補を優先する。
            if (parent is not null && Directory.Exists(parent))
            {
                Directory.CreateDirectory(full);
                return full;
            }
        }

        var fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "out_files"));
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    /// <summary>
    /// out_files 配下の出力パスを返します。
    /// </summary>
    public static string ResolveOutputPath(string fileName)
    {
        return Path.Combine(ResolveOutputDir(), fileName);
    }

    private static IEnumerable<string> EnumerateInputCandidates(string fileName)
    {
        yield return Path.Combine("in_files", fileName);
        yield return Path.Combine("..", "in_files", fileName);
        yield return Path.Combine("..", "..", "..", "..", "in_files", fileName);
        yield return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "in_files", fileName);
        yield return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "in_files", fileName);
        yield return Path.Combine(@"C:\proj\Onta\in_files", fileName);
    }

    private static IEnumerable<string> EnumerateOutputDirCandidates()
    {
        yield return "out_files";
        yield return Path.Combine("..", "out_files");
        yield return Path.Combine("..", "..", "..", "..", "out_files");
        yield return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "out_files");
        yield return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "out_files");
        yield return @"C:\proj\Onta\out_files";
    }
}
