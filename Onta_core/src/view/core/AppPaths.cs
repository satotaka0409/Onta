namespace Onta.View.Core;

/// <summary>
/// 入出力や履歴保存に使うアプリ共通パスを解決します。
/// </summary>
internal static class AppPaths
{
    /// <summary>送信入力ファイル用フォルダーです。</summary>
    public static string InputDir => ResolveSiblingDir("in_files");

    /// <summary>送受信生成物の出力フォルダーです。</summary>
    public static string OutputDir => ResolveSiblingDir("out_files");

    /// <summary>受信履歴ファイルの保存先です。</summary>
    public static string ReceiveHistoryFilePath => Path.Combine(ResolveRootDir(), "Onta_history.bin");

    /// <summary>メイン画面設定ファイルの保存先です。</summary>
    public static string MainSettingsFilePath => Path.Combine(ResolveRootDir(), "Onta_setting.bin");

    /// <summary>
    /// アプリルート（Onta リポジトリ直下など）を候補パスから解決します。
    /// </summary>
    /// <returns>存在する候補の最初のパス。見つからなければ BaseDirectory。</returns>
    private static string ResolveRootDir()
    {
        var candidates = new[]
        {
            @"C:\proj\Onta",
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
            Path.GetFullPath(Environment.CurrentDirectory),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..")),
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// ルート直下の兄弟フォルダーを解決し、無ければ作成します。
    /// </summary>
    /// <param name="name">フォルダー名（例: in_files / out_files）。</param>
    /// <returns>解決した絶対パス。</returns>
    private static string ResolveSiblingDir(string name)
    {
        var root = ResolveRootDir();
        var candidates = new[]
        {
            Path.Combine(root, name),
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




