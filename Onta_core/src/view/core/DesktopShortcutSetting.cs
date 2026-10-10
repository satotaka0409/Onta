using System.Reflection;

namespace Onta.View.Core;

/// <summary>
/// インストーラー（Install-Onta.ps1）で選んだ「デスクトップにショートカットを作成する」をデスクトップへ反映します。
/// </summary>
/// <remarks>
/// ClickOnce はデスクトップショートカットを無条件に作る（作るとアンインストールで消える）ため、
/// 作らないを選んだときはインストール後の初回起動で消します。
/// </remarks>
internal static class DesktopShortcutSetting
{
    /// <summary>ClickOnce の起動ツール（Launcher）が、ClickOnce から起動したときに "true" を設定する環境変数です。</summary>
    private const string ClickOnceDeployedVariable = "ClickOnce_IsNetworkDeployed";

    /// <summary>ClickOnce ショートカットの拡張子です。</summary>
    private const string ShortcutExtension = ".appref-ms";

    /// <summary>
    /// ClickOnce から起動しているときに、インストーラーの選択をデスクトップへ反映します。失敗しても起動は続けます。
    /// </summary>
    public static void ApplyInstallerChoice()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(ClickOnceDeployedVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var product = typeof(DesktopShortcutSetting).Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
            var company = typeof(DesktopShortcutSetting).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
            if (string.IsNullOrWhiteSpace(product) || string.IsNullOrWhiteSpace(company))
            {
                return;
            }

            var shortcutFileName = product + ShortcutExtension;
            var startMenuShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), company, shortcutFileName);
            Apply(
                AppPaths.DesktopShortcutChoiceFilePath,
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                startMenuShortcut,
                shortcutFileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // デスクトップに触れなくても起動は続ける（選択ファイルは残し、次回の起動で再試行する）。
        }
    }

    /// <summary>
    /// 選択ファイルの内容（1 = 作る / 0 = 作らない）に合わせてデスクトップのショートカットを作成・削除し、選択ファイルを消します。
    /// </summary>
    /// <param name="choiceFilePath">インストーラーが書く選択ファイル。無ければ何もしません。</param>
    /// <param name="desktopDirectory">デスクトップフォルダー。</param>
    /// <param name="startMenuShortcutPath">ClickOnce がスタートメニューに作ったショートカット（作るときのコピー元）。</param>
    /// <param name="shortcutFileName">デスクトップ上のショートカットのファイル名。</param>
    /// <returns>選択ファイルを読んで反映したとき true。</returns>
    internal static bool Apply(string choiceFilePath, string desktopDirectory, string startMenuShortcutPath, string shortcutFileName)
    {
        if (!File.Exists(choiceFilePath))
        {
            return false;
        }

        var choice = File.ReadAllText(choiceFilePath).Trim();
        var desktopShortcut = Path.Combine(desktopDirectory, shortcutFileName);
        if (choice == "0")
        {
            if (File.Exists(desktopShortcut))
            {
                File.Delete(desktopShortcut);
            }
        }
        else if (choice == "1" && !File.Exists(desktopShortcut) && File.Exists(startMenuShortcutPath))
        {
            // 同じ版を入れ直したとき（ClickOnce はインストールせず起動だけする）に、以前消したショートカットを戻す。
            File.Copy(startMenuShortcutPath, desktopShortcut);
        }

        File.Delete(choiceFilePath);
        return true;
    }
}
