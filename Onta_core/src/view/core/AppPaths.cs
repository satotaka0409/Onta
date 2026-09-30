namespace Onta.View.Core;

/// <summary>
/// 入出力や履歴保存に使うアプリ共通パスを解決します。
/// </summary>
internal static class AppPaths
{
    private static readonly string DataDir = ResolveDataDir();

    /// <summary>送信入力ファイル用フォルダーです。</summary>
    public static string InputDir => ResolveSiblingDir("in_files");

    /// <summary>送受信生成物の出力フォルダーです。</summary>
    public static string OutputDir => ResolveSiblingDir("out_files");

    /// <summary>受信履歴ファイルの保存先です。</summary>
    public static string ReceiveHistoryFilePath => ResolveDataFilePath("Onta_history.bin");

    /// <summary>ファイル画面設定ファイルの保存先です。</summary>
    public static string MainSettingsFilePath => ResolveDataFilePath("Onta_setting.bin");

    /// <summary>マニュアル（Markdown）フォルダーです。</summary>
    public static string ManualDir => ResolveManualDir();

    /// <summary>
    /// マニュアルフォルダーを解決します。リポジトリの Onta_manual を優先し、無ければ同梱の manual を使います。
    /// </summary>
    /// <remarks>開発中はリポジトリ側の編集を「再読込」で即反映できるよう、同梱コピーより優先します。</remarks>
    /// <returns>Markdown を含む最初の候補フォルダー。見つからなければ同梱 manual のパス。</returns>
    private static string ResolveManualDir()
    {
        var candidates = new[]
        {
            Path.Combine(ResolveRootDir(), "Onta_manual"),
            Path.Combine(AppContext.BaseDirectory, "manual"),
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.md").Any())
            {
                return candidate;
            }
        }

        return candidates[^1];
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
    /// 指定データファイルパスを返します。新保存先が空で旧保存先にある場合は一度だけ移行します。
    /// </summary>
    /// <param name="fileName">データファイル名。</param>
    /// <returns>ユーザーデータフォルダー上の絶対パス。</returns>
    private static string ResolveDataFilePath(string fileName)
    {
        Directory.CreateDirectory(DataDir);
        var target = Path.Combine(DataDir, fileName);
        TryMigrateLegacyDataFile(fileName, target);
        return target;
    }

    /// <summary>
    /// 新保存先にファイルが無いとき、旧ルートの同名ファイルを一度だけコピーします。
    /// </summary>
    /// <param name="fileName">移行するファイル名。</param>
    /// <param name="target">コピー先の絶対パス。</param>
    private static void TryMigrateLegacyDataFile(string fileName, string target)
    {
        if (File.Exists(target))
        {
            return;
        }

        foreach (var legacyDir in GetLegacyRootCandidates())
        {
            var src = Path.Combine(legacyDir, fileName);
            if (!File.Exists(src))
            {
                continue;
            }

            try
            {
                File.Copy(src, target, overwrite: false);
            }
            catch
            {
                // 移行失敗時は新規作成へフォールバックする。
            }

            return;
        }
    }

    /// <summary>
    /// 旧データファイルを探すルート候補を返します。
    /// </summary>
    /// <returns>存在するディレクトリ（大文字小文字を無視して重複を除いた列）。</returns>
    private static IEnumerable<string> GetLegacyRootCandidates()
    {
        var roots = new[]
        {
            ResolveRootDir(),
            Path.GetFullPath(Environment.CurrentDirectory),
            AppContext.BaseDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")),
        };

        return roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists);
    }
}




