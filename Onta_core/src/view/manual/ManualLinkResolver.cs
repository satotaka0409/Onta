using System.IO;

namespace Onta.View.Manual;

/// <summary>
/// マニュアル本文のリンク先（URL・ローカルファイル）を解決します。マニュアル画面とリンクテストで共用します。
/// </summary>
internal static class ManualLinkResolver
{
    /// <summary>
    /// 既定のブラウザーで開く外部 URL（http / https / mailto）かどうかを判定します。
    /// </summary>
    /// <param name="url">Markdown 上のリンク先。</param>
    /// <returns>外部 URL なら true。</returns>
    public static bool IsExternal(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var absolute)
        && (absolute.Scheme == Uri.UriSchemeHttp
            || absolute.Scheme == Uri.UriSchemeHttps
            || absolute.Scheme == Uri.UriSchemeMailto);

    /// <summary>
    /// ページ内アンカー（#… だけ）のリンクかどうかを判定します。
    /// </summary>
    /// <param name="url">Markdown 上のリンク先。</param>
    /// <returns>パス部分が空なら true。</returns>
    public static bool IsAnchorOnly(string url) => string.IsNullOrWhiteSpace(url.Split('#')[0]);

    /// <summary>
    /// ローカルのリンク先を、表示中の章のフォルダーを基準に絶対パスへ変換します（#以降は除き、%エスケープは戻します）。
    /// </summary>
    /// <param name="baseDirectory">表示中の章のフォルダー。</param>
    /// <param name="url">Markdown 上のリンク先。</param>
    /// <param name="fullPath">変換した絶対パス。</param>
    /// <returns>パスとして解釈できれば true。</returns>
    public static bool TryGetFullPath(string baseDirectory, string url, out string fullPath)
    {
        fullPath = string.Empty;
        var pathPart = url.Split('#')[0];
        if (string.IsNullOrWhiteSpace(pathPart))
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(Path.Combine(baseDirectory, Uri.UnescapeDataString(pathPart)));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// リンク先のファイルを探します。表示中のマニュアルに無ければ、同梱マニュアル（manual）の同じ相対位置も探します。
    /// </summary>
    /// <param name="fullPath"><see cref="TryGetFullPath"/> で求めたリンク先。</param>
    /// <param name="manualDir">表示中のマニュアルの言語フォルダー（例: Onta_manual\en）。</param>
    /// <param name="bundledManualRoot">ビルド出力に同梱したマニュアルのルート（例: アプリ直下の manual）。</param>
    /// <returns>存在するファイルのパス。どちらにも無ければ null。</returns>
    /// <remarks>THIRD-PARTY-NOTICES.txt はビルド時に Installer から同梱側（manual 直下）へだけコピーされるため、開発中はリポジトリ側に無い。</remarks>
    public static string? ResolveExistingFile(string fullPath, string manualDir, string bundledManualRoot)
    {
        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        var manualRoot = Path.GetDirectoryName(Path.GetFullPath(manualDir));
        if (string.IsNullOrEmpty(manualRoot))
        {
            return null;
        }

        var relative = Path.GetRelativePath(manualRoot, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        var bundledRoot = Path.GetFullPath(bundledManualRoot);
        if (string.Equals(
                bundledRoot.TrimEnd(Path.DirectorySeparatorChar),
                manualRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var candidate = Path.Combine(bundledRoot, relative);
        return File.Exists(candidate) ? candidate : null;
    }
}
