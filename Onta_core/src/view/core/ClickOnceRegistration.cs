using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Onta.View.Core;

/// <summary>
/// ClickOnce のインストール登録（HKCU のアンインストール情報）を扱います。
/// </summary>
/// <remarks>
/// ClickOnce は「アプリと機能」のアイコン（DisplayIcon）に自分の汎用アイコン（dfshim.dll）を書き、
/// マニフェストのアイコンを使わないため、起動のたびにアプリの exe のアイコンへ書き換えます。
/// </remarks>
internal static class ClickOnceRegistration
{
    /// <summary>ClickOnce の起動ツール（Launcher）が、ClickOnce から起動したときに "true" を設定する環境変数です。</summary>
    private const string ClickOnceDeployedVariable = "ClickOnce_IsNetworkDeployed";

    /// <summary>HKCU のアンインストール情報のキーです。</summary>
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>このアプリの配布マニフェスト名です（アンインストールコマンドに入る）。</summary>
    private const string DeploymentManifestName = "Onta.application";

    /// <summary>
    /// ClickOnce から起動しているときに、アプリと機能のアイコンとインストール時のデスクトップショートカットの選択を反映します。失敗しても起動は続けます。
    /// </summary>
    public static void ApplyOnStartup()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(ClickOnceDeployedVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ClickOnceUninstallEntry? entry;
        try
        {
            entry = FindEntry();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }

        if (entry is null)
        {
            return;
        }

        try
        {
            ApplyDisplayIcon(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // アイコンを書き換えられなくても起動は続ける。
        }

        DesktopShortcutSetting.ApplyInstallerChoice(entry);
    }

    /// <summary>
    /// HKCU のアンインストール情報から、このアプリ（実行中の CPU 版）の ClickOnce 登録を探します。
    /// </summary>
    /// <returns>見つかった登録。無ければ null。</returns>
    private static ClickOnceUninstallEntry? FindEntry()
    {
        var publisher = typeof(ClickOnceRegistration).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return null;
        }

        using var uninstall = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        if (uninstall is null)
        {
            return null;
        }

        var entries = new List<ClickOnceUninstallEntry>();
        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var key = uninstall.OpenSubKey(name);
            if (key is null)
            {
                continue;
            }

            entries.Add(new ClickOnceUninstallEntry(
                name,
                key.GetValue("DisplayName") as string,
                key.GetValue("Publisher") as string,
                key.GetValue("UninstallString") as string,
                key.GetValue("ShortcutFileName") as string,
                key.GetValue("ShortcutFolderName") as string,
                key.GetValue("DisplayIcon") as string));
        }

        var archLabel = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "ARM64" : "x64";
        return SelectEntry(entries, publisher, archLabel);
    }

    /// <summary>
    /// アンインストール情報の一覧から、このアプリの登録を選びます。
    /// 発行元が一致し、アンインストールコマンドが <see cref="DeploymentManifestName"/> を指すものを候補とし、
    /// 複数あれば表示名の末尾が CPU 名（publish.ps1 の「Onta x64」「Onta ARM64」）と一致するものを選びます。
    /// </summary>
    /// <param name="entries">HKCU のアンインストール情報。</param>
    /// <param name="publisher">発行元（アセンブリの Company。publish の PublisherName と同じ）。</param>
    /// <param name="archLabel">実行中の CPU 名（x64 / ARM64）。</param>
    /// <returns>選んだ登録。候補が無いか、複数あって CPU で決まらなければ null。</returns>
    internal static ClickOnceUninstallEntry? SelectEntry(
        IEnumerable<ClickOnceUninstallEntry> entries,
        string publisher,
        string archLabel)
    {
        var candidates = entries
            .Where(e => string.Equals(e.Publisher, publisher, StringComparison.OrdinalIgnoreCase)
                && e.UninstallString is not null
                && e.UninstallString.Contains("dfshim.dll", StringComparison.OrdinalIgnoreCase)
                && e.UninstallString.Contains(DeploymentManifestName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var byArch = candidates
            .Where(e => e.DisplayName is not null
                && e.DisplayName.EndsWith(" " + archLabel, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return byArch.Count == 1 ? byArch[0] : null;
    }

    /// <summary>
    /// アプリと機能のアイコンを、実行中の exe（埋め込みアイコン）へ書き換えます。同じなら書きません。
    /// </summary>
    /// <param name="entry">このアプリの登録。</param>
    private static void ApplyDisplayIcon(ClickOnceUninstallEntry entry)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            return;
        }

        var icon = exe + ",0";
        if (string.Equals(entry.DisplayIcon, icon, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath + "\\" + entry.KeyName, writable: true);
        key?.SetValue("DisplayIcon", icon, RegistryValueKind.String);
    }
}

/// <summary>
/// ClickOnce のアンインストール情報 1 件です。
/// </summary>
/// <param name="KeyName">アンインストール情報のサブキー名。</param>
/// <param name="DisplayName">アプリと機能の表示名（例: Onta x64）。</param>
/// <param name="Publisher">発行元。</param>
/// <param name="UninstallString">アンインストールコマンド。</param>
/// <param name="ShortcutFileName">スタートメニュー・デスクトップのショートカット名（拡張子なし）。</param>
/// <param name="ShortcutFolderName">スタートメニューのフォルダー名。</param>
/// <param name="DisplayIcon">アプリと機能のアイコン。</param>
internal sealed record ClickOnceUninstallEntry(
    string KeyName,
    string? DisplayName,
    string? Publisher,
    string? UninstallString,
    string? ShortcutFileName,
    string? ShortcutFolderName,
    string? DisplayIcon);
