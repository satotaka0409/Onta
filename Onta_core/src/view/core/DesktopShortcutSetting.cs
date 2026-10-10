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
    /// <summary>ClickOnce ショートカットの拡張子です。</summary>
    private const string ShortcutExtension = ".appref-ms";

    /// <summary>
    /// インストーラーの選択をデスクトップへ反映します（ClickOnce から起動したときに <see cref="ClickOnceRegistration"/> が呼ぶ）。失敗しても起動は続けます。
    /// </summary>
    /// <param name="entry">このアプリの ClickOnce 登録（ショートカット名・スタートメニューのフォルダー名を使う）。</param>
    public static void ApplyInstallerChoice(ClickOnceUninstallEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ShortcutFileName) || string.IsNullOrWhiteSpace(entry.ShortcutFolderName))
        {
            return;
        }

        try
        {
            var shortcutFileName = entry.ShortcutFileName + ShortcutExtension;
            var startMenuShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), entry.ShortcutFolderName, shortcutFileName);
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
