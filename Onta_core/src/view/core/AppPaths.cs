namespace Onta.View.Core;

/// <summary>
/// 入出力や履歴保存に使うアプリ共通パスを解決します。
/// </summary>
internal static class AppPaths
{
    /// <summary>表示言語のマニュアルが無いときに使う言語フォルダー名です。</summary>
    private const string FallbackManualLanguage = "ja";

    private static readonly string DataDir = ResolveDataDir();

    /// <summary>送信入力ファイル用フォルダーです。</summary>
    public static string InputDir => ResolveSiblingDir("in_files");

    /// <summary>送受信生成物の出力フォルダーです。</summary>
    public static string OutputDir => ResolveSiblingDir("out_files");

    /// <summary>受信履歴ファイルの保存先です。</summary>
    public static string ReceiveHistoryFilePath => ResolveDataFilePath("Onta_history.bin");

    /// <summary>ファイル画面設定ファイルの保存先です。</summary>
    public static string MainSettingsFilePath => ResolveDataFilePath("Onta_setting.bin");

    /// <summary>インストール時に選んだ表示言語（ja / en）のファイルです。インストーラー（Install-Onta.ps1）が書きます。</summary>
    public static string LanguageSettingFilePath => Path.Combine(DataDir, "Onta_language.txt");

    /// <summary>マニュアル（Markdown）フォルダーです。</summary>
    public static string ManualDir => ResolveManualDir();

    /// <summary>
    /// 表示言語のマニュアルフォルダーを解決します。リポジトリの Onta_manual\{言語} を優先し、無ければ同梱の manual\{言語} を使います。
    /// </summary>
    /// <remarks>開発中はリポジトリ側の編集を「再読込」で即反映できるよう、同梱コピーより優先します。表示言語の版が無ければ日本語版を使います。</remarks>
    /// <returns>Markdown を含む最初の候補フォルダー。見つからなければ同梱 manual\{言語} のパス。</returns>
    private static string ResolveManualDir()
    {
        var language = Onta.View.Language.CoreViewText.ManualLanguageFolder;
        var root = ResolveRootDir();
        var candidates = new[]
        {
            Path.Combine(root, "Onta_manual", language),
            Path.Combine(AppContext.BaseDirectory, "manual", language),
            Path.Combine(root, "Onta_manual", FallbackManualLanguage),
            Path.Combine(AppContext.BaseDirectory, "manual", FallbackManualLanguage),
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.md").Any())
            {
                return candidate;
            }
        }

        return candidates[1];
    }

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

    /// <summary>
    /// ユーザーデータ保存先を解決します（アンインストール後も保持される領域）。
    /// </summary>
    /// <returns>LocalApplicationData 配下の Onta。取得できなければ実行フォルダー配下の data。</returns>
    private static string ResolveDataDir()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            return Path.Combine(local, "Onta");
        }

        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    /// <summary>
    /// 指定データファイルパスを返します。
    /// </summary>
    /// <param name="fileName">データファイル名。</param>
    /// <returns>ユーザーデータフォルダー上の絶対パス。</returns>
    private static string ResolveDataFilePath(string fileName)
    {
        Directory.CreateDirectory(DataDir);
        return Path.Combine(DataDir, fileName);
    }
}




