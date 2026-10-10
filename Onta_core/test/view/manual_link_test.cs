using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Onta.View.Manual;
using Xunit;

namespace Onta.Core.Tests.View;

/// <summary>
/// マニュアル（日本語・英語）のリンクと画像がリンク切れになっていないことの検証です。
/// </summary>
public sealed class ManualLinkTest
{
    private const string LicenseFileName = "99_licence.md";

    /// <summary>
    /// 同梱マニュアル（インストール版と同じ配置）で、全章のリンク先と画像がその場に存在すること。
    /// </summary>
    /// <param name="language">言語フォルダー名。</param>
    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void BundledManual_AllLinksAndImagesExist(string language)
    {
        var bundledRoot = Path.Combine(AppContext.BaseDirectory, "manual");
        var languageDir = Path.Combine(bundledRoot, language);
        Assert.True(Directory.Exists(languageDir), $"同梱マニュアルがありません: {languageDir}");

        var broken = new List<string>();
        foreach (var document in ManualDocuments(languageDir))
        {
            foreach (var (isImage, target) in LocalTargets(document))
            {
                var exists = ManualLinkResolver.TryGetFullPath(Path.GetDirectoryName(document)!, target, out var fullPath)
                    && File.Exists(fullPath);
                if (!exists)
                {
                    broken.Add(Describe(bundledRoot, document, isImage, target));
                }
            }
        }

        Assert.True(broken.Count == 0, "リンク切れ:\n" + string.Join("\n", broken));
    }

    /// <summary>
    /// リポジトリのマニュアル（開発中の表示）で、リンクは画面と同じ解決（同梱側への予備を含む）で開け、画像は章のフォルダー基準で存在すること。
    /// </summary>
    /// <param name="language">言語フォルダー名。</param>
    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void RepositoryManual_AllLinksResolveAndImagesExist(string language)
    {
        var repositoryRoot = Path.Combine(FindRepositoryRoot(), "Onta_manual");
        var languageDir = Path.Combine(repositoryRoot, language);
        var bundledRoot = Path.Combine(AppContext.BaseDirectory, "manual");
        Assert.True(Directory.Exists(languageDir), $"マニュアルがありません: {languageDir}");

        var broken = new List<string>();
        foreach (var document in ManualDocuments(languageDir))
        {
            foreach (var (isImage, target) in LocalTargets(document))
            {
                string? resolved = null;
                if (ManualLinkResolver.TryGetFullPath(Path.GetDirectoryName(document)!, target, out var fullPath))
                {
                    resolved = isImage
                        ? (File.Exists(fullPath) ? fullPath : null)
                        : ManualLinkResolver.ResolveExistingFile(fullPath, languageDir, bundledRoot);
                }

                if (resolved is null)
                {
                    broken.Add(Describe(repositoryRoot, document, isImage, target));
                }
            }
        }

        Assert.True(broken.Count == 0, "リンク切れ:\n" + string.Join("\n", broken));
    }

    /// <summary>
    /// 日本語・英語とも、第三者ライセンスの章から THIRD-PARTY-NOTICES.txt へリンクしていること（リンクが消えると上の検証では気づけないため）。
    /// </summary>
    /// <param name="language">言語フォルダー名。</param>
    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void BundledManual_LinksToThirdPartyNotices(string language)
    {
        var languageDir = Path.Combine(AppContext.BaseDirectory, "manual", language);
        var notices = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "manual", "THIRD-PARTY-NOTICES.txt"));

        var linked = ManualDocuments(languageDir)
            .Any(document => LocalTargets(document)
                .Any(link => !link.IsImage
                    && ManualLinkResolver.TryGetFullPath(Path.GetDirectoryName(document)!, link.Target, out var fullPath)
                    && string.Equals(fullPath, notices, StringComparison.OrdinalIgnoreCase)));

        Assert.True(linked, $"{language} のマニュアルに THIRD-PARTY-NOTICES.txt へのリンクがありません。");
        Assert.True(File.Exists(notices), $"同梱されていません: {notices}");
    }

    /// <summary>
    /// アプリの csproj で、同じ元ファイルを複数の Content として同梱していないこと（ClickOnce は 1 か所にまとめ、片方が配布されない）。
    /// </summary>
    [Fact]
    public void ProjectContent_NoSourceFileBundledTwice()
    {
        var projectPath = Path.Combine(FindRepositoryRoot(), "Onta_core", "Onta_core.csproj");
        var projectDir = Path.GetDirectoryName(projectPath)!;
        var includes = XDocument.Load(projectPath)
            .Descendants()
            .Where(element => element.Name.LocalName == "Content")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => include!);

        var duplicates = includes
            .SelectMany(include => ExpandInclude(projectDir, include))
            .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, "複数回同梱している元ファイル:\n" + string.Join("\n", duplicates));
    }

    /// <summary>
    /// リンク抽出が描画と同じ記法を拾い、コードブロック・インラインコード・エスケープを除くこと。
    /// </summary>
    [Fact]
    public void ExtractLinks_FollowsRendererSyntax()
    {
        const string markdown = """
            [章](01_a.md) ![図](picture/b.png) `[コード](no1.md)`
            ```text
            [ブロック](no2.md)
            ```
            [![画像リンク](picture/c.png)](https://example.com/)
            \[エスケープ](no3.md)
            | 表 | [セル](02_d.md#節) |
            """;

        var links = MarkdownFlowDocumentRenderer.ExtractLinks(markdown);

        Assert.Equal(
            [
                (false, "01_a.md"),
                (true, "picture/b.png"),
                (true, "picture/c.png"),
                (false, "https://example.com/"),
                (false, "02_d.md#節"),
            ],
            links);
    }

    /// <summary>
    /// 言語フォルダー配下の章（.md・仕様 .mdc）と、言語フォルダーの外のライセンス章を列挙します。
    /// </summary>
    /// <param name="languageDir">言語フォルダー。</param>
    /// <returns>検証する文書のパス。</returns>
    private static IEnumerable<string> ManualDocuments(string languageDir)
    {
        var documents = Directory.EnumerateFiles(languageDir, "*.md", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(languageDir, "*.mdc", SearchOption.AllDirectories))
            .ToList();
        var license = Path.Combine(Path.GetDirectoryName(languageDir)!, LicenseFileName);
        if (File.Exists(license))
        {
            documents.Add(license);
        }

        Assert.NotEmpty(documents);
        return documents;
    }

    /// <summary>
    /// 文書のリンク・画像のうち、ローカルファイルを指すものを返します（外部 URL とページ内アンカーは除く）。
    /// </summary>
    /// <param name="document">文書のパス。</param>
    /// <returns>画像かどうかとリンク先の組。</returns>
    private static IEnumerable<(bool IsImage, string Target)> LocalTargets(string document) =>
        MarkdownFlowDocumentRenderer.ExtractLinks(File.ReadAllText(document))
            .Where(link => !ManualLinkResolver.IsExternal(link.Target) && !ManualLinkResolver.IsAnchorOnly(link.Target));

    /// <summary>
    /// リンク切れの報告行を作ります。
    /// </summary>
    /// <param name="root">表示の基準フォルダー。</param>
    /// <param name="document">リンク元の文書。</param>
    /// <param name="isImage">画像なら true。</param>
    /// <param name="target">リンク先。</param>
    /// <returns>報告行。</returns>
    private static string Describe(string root, string document, bool isImage, string target) =>
        $"{Path.GetRelativePath(root, document)}: {(isImage ? "画像" : "リンク")} {target}";

    /// <summary>
    /// csproj の Include（ワイルドカード可）を実在するファイルの絶対パスへ展開します。
    /// </summary>
    /// <param name="projectDir">csproj のフォルダー。</param>
    /// <param name="include">Include 属性の値。</param>
    /// <returns>該当するファイルの絶対パス。</returns>
    private static IEnumerable<string> ExpandInclude(string projectDir, string include)
    {
        var fullPattern = Path.GetFullPath(Path.Combine(projectDir, include.Replace('/', '\\')));
        var directory = Path.GetDirectoryName(fullPattern)!;
        var pattern = Path.GetFileName(fullPattern);
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            return [fullPattern];
        }

        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Select(Path.GetFullPath)
            : [];
    }

    /// <summary>
    /// このテストのソース位置から、Onta_manual を含むリポジトリのルートを探します。
    /// </summary>
    /// <param name="sourcePath">コンパイラーが埋め込むこのファイルのパス。</param>
    /// <returns>リポジトリのルート。</returns>
    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        var directory = Path.GetDirectoryName(sourcePath);
        while (!string.IsNullOrEmpty(directory))
        {
            if (Directory.Exists(Path.Combine(directory, "Onta_manual")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException($"Onta_manual が見つかりません（起点: {sourcePath}）。");
    }
}
