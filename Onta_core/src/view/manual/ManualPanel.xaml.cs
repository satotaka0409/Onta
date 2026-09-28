using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Onta.View.Core;

namespace Onta.View.Manual;

/// <summary>
/// マニュアルフォルダーの Markdown を章一覧に並べ、選択した章を整形して表示するパネルです。
/// </summary>
public partial class ManualPanel : UserControl
{
    private const string IndexFileName = "00_index.md";
    private const string ReadmeFileName = "README.md";
    private const string LicenseFileName = "99_licence.md";

    /// <summary>仕様章。00_index.md の「仕様」と同じ順・表示名です。</summary>
    private static readonly (string FileName, string Title)[] SpecificationChapters =
    [
        ("data_struct.mdc", "10. データ構造"),
        ("modulation.mdc", "11. 変調方式"),
        ("stream.mdc", "12. ストリーム"),
        ("history_info.mdc", "13. 履歴情報"),
    ];

    private readonly ObservableCollection<ManualChapter> _chapters = [];
    private bool _initialized;

    /// <summary>
    /// パネルを初期化し、初回表示時に章一覧を読み込みます。
    /// </summary>
    public ManualPanel()
    {
        InitializeComponent();
        ChapterList.ItemsSource = _chapters;
        Loaded += (_, _) =>
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            ReloadChapters();
        };
    }

    /// <summary>
    /// マニュアルフォルダーを走査して章一覧を作り直し、選択中の章（無ければ先頭）を再表示します。
    /// </summary>
    public void ReloadChapters()
    {
        var selectedPath = (ChapterList.SelectedItem as ManualChapter)?.FilePath;
        _chapters.Clear();

        var manualDir = AppPaths.ManualDir;
        if (!Directory.Exists(manualDir))
        {
            StatusText.Text = $"マニュアルフォルダーが見つかりません: {manualDir}";
            DocumentViewer.Document = CreateRenderer(manualDir).RenderMessage("マニュアルが見つかりません。");
            return;
        }

        // README.md はリポジトリ閲覧用の目次なので、アプリでは 00_index.md を目次として使う
        var files = Directory.GetFiles(manualDir, "*.md")
            .Where(path => !string.Equals(Path.GetFileName(path), ReadmeFileName, StringComparison.OrdinalIgnoreCase))
            .Where(path => !string.Equals(Path.GetFileName(path), LicenseFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => IsIndexFile(path) ? 0 : 1)
            .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            _chapters.Add(new ManualChapter(path, ReadTitle(path)));
        }

        var specificationDir = Path.Combine(manualDir, "specification");
        foreach (var (fileName, title) in SpecificationChapters)
        {
            var path = Path.Combine(specificationDir, fileName);
            if (File.Exists(path))
            {
                _chapters.Add(new ManualChapter(path, title));
            }
        }

        var licensePath = Path.Combine(manualDir, LicenseFileName);
        if (File.Exists(licensePath))
        {
            _chapters.Add(new ManualChapter(licensePath, "14. ライセンス"));
        }

        StatusText.Text = manualDir;
        var target = _chapters.FirstOrDefault(chapter => PathEquals(chapter.FilePath, selectedPath))
            ?? _chapters.FirstOrDefault();
        if (target is null)
        {
            DocumentViewer.Document = CreateRenderer(manualDir).RenderMessage("マニュアル（*.md）がありません。");
            return;
        }

        ChapterList.SelectedItem = target;
        ChapterList.ScrollIntoView(target);
    }

    /// <summary>
    /// 「再読込」押下で章一覧と本文を読み直します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">ルーティングイベント引数。</param>
    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        ReloadChapters();
    }

    /// <summary>
    /// 章の選択が変わったら、その章の Markdown を表示します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">選択変更のイベント引数。</param>
    private void OnChapterSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChapterList.SelectedItem is ManualChapter chapter)
        {
            ShowChapter(chapter);
        }
    }

    /// <summary>
    /// 章ファイルを読み込み、FlowDocument に整形して表示します。
    /// </summary>
    /// <param name="chapter">表示する章。</param>
    private void ShowChapter(ManualChapter chapter)
    {
        var directory = Path.GetDirectoryName(chapter.FilePath) ?? string.Empty;
        var renderer = CreateRenderer(directory);
        try
        {
            DocumentViewer.Document = renderer.Render(File.ReadAllText(chapter.FilePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DocumentViewer.Document = renderer.RenderMessage($"読み込みに失敗しました: {ex.Message}");
        }
    }

    /// <summary>
    /// 指定フォルダーを基準にリンク・画像を解決するレンダラーを作成します。
    /// </summary>
    /// <param name="baseDirectory">相対リンクと画像の基準フォルダー。</param>
    /// <returns>リンククリックをこのパネルへ渡すレンダラー。</returns>
    private MarkdownFlowDocumentRenderer CreateRenderer(string baseDirectory)
    {
        return new MarkdownFlowDocumentRenderer(baseDirectory, url => OnLinkClicked(baseDirectory, url));
    }

    /// <summary>
    /// 本文中のリンクを処理します。マニュアル内の章（.md / 仕様 .mdc）は章を切り替え、http(s) やその他のファイルは既定のアプリで開きます。
    /// </summary>
    /// <param name="baseDirectory">表示中の章のフォルダー。</param>
    /// <param name="url">Markdown 上のリンク先。</param>
    private void OnLinkClicked(string baseDirectory, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            OpenWithShell(absolute.AbsoluteUri);
            return;
        }

        var pathPart = url.Split('#')[0];
        if (string.IsNullOrWhiteSpace(pathPart))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(baseDirectory, Uri.UnescapeDataString(pathPart)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            StatusText.Text = $"リンク先を解決できません: {url}";
            return;
        }

        var chapter = _chapters.FirstOrDefault(item => PathEquals(item.FilePath, fullPath));
        if (chapter is not null)
        {
            ChapterList.SelectedItem = chapter;
            ChapterList.ScrollIntoView(chapter);
            return;
        }

        if (File.Exists(fullPath))
        {
            OpenWithShell(fullPath);
            return;
        }

        StatusText.Text = $"リンク先が見つかりません: {url}";
    }

    /// <summary>
    /// URL またはファイルを OS の既定アプリで開きます。
    /// </summary>
    /// <param name="target">開く URL またはファイルパス。</param>
    private void OpenWithShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText.Text = $"開けませんでした: {ex.Message}";
        }
    }

    /// <summary>
    /// 章ファイルの最初の H1 見出しを表示名として読み取ります。無ければファイル名を使います。
    /// </summary>
    /// <param name="path">章ファイルのパス。</param>
    /// <returns>最初の H1 見出し。無ければ拡張子を除いたファイル名。</returns>
    private static string ReadTitle(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                {
                    return trimmed[2..].Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// 目次ファイル（00_index.md）かどうかを判定します。
    /// </summary>
    /// <param name="path">判定するファイルパス。</param>
    /// <returns>ファイル名が 00_index.md なら true。</returns>
    private static bool IsIndexFile(string path)
    {
        return string.Equals(Path.GetFileName(path), IndexFileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 2 つのパスが同じファイルを指すかを大文字小文字を区別せずに比較します。
    /// </summary>
    /// <param name="left">比較するパス。</param>
    /// <param name="right">比較するもう一方のパス。</param>
    /// <returns>フルパスが一致すれば true。</returns>
    private static bool PathEquals(string left, string? right)
    {
        return right is not null
            && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// マニュアルの 1 章（Markdown ファイル）です。
/// </summary>
/// <param name="FilePath">Markdown ファイルの絶対パス。</param>
/// <param name="Title">章一覧に表示する名前（最初の H1 見出し）。</param>
public sealed record ManualChapter(string FilePath, string Title);
