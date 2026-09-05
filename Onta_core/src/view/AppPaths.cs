namespace Onta.View;

/// <summary>
/// 入出力ディレクトリ（in_files / out_files）の解決です。
/// </summary>
internal static class AppPaths
{
    /// <summary>入力ディレクトリ（C:\proj\Onta\in_files）。</summary>
    public static string InputDir => ResolveSiblingDir("in_files");

    /// <summary>出力ディレクトリ（C:\proj\Onta\out_files）。</summary>
    public static string OutputDir => ResolveSiblingDir("out_files");

    private static string ResolveSiblingDir(string name)
    {
        var candidates = new[]
        {
            Path.Combine(@"C:\proj\Onta", name),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", name)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", name)),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, name)),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", name)),
        };

        foreach (var candidate in candidates)
        {
            var parent = Path.GetDirectoryName(candidate);
            if (parent is not null && Directory.Exists(parent))
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
        }

        var fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, name));
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}
