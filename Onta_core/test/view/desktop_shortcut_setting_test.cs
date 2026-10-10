using Onta.View.Core;
using Xunit;

namespace Onta.Core.Tests.View;

/// <summary>
/// インストーラーで選んだデスクトップショートカットの有無の反映を検証します。
/// </summary>
public sealed class DesktopShortcutSettingTest : IDisposable
{
    private const string ShortcutFileName = "Onta x64.appref-ms";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "onta_shortcut_test_" + Guid.NewGuid().ToString("N"));
    private readonly string _desktop;
    private readonly string _startMenuShortcut;
    private readonly string _choiceFile;

    /// <summary>
    /// 一時フォルダーにデスクトップ・スタートメニュー・選択ファイルの置き場を作ります。
    /// </summary>
    public DesktopShortcutSettingTest()
    {
        _desktop = Path.Combine(_root, "Desktop");
        var startMenu = Path.Combine(_root, "Programs", "Onta Project");
        Directory.CreateDirectory(_desktop);
        Directory.CreateDirectory(startMenu);
        _startMenuShortcut = Path.Combine(startMenu, ShortcutFileName);
        File.WriteAllText(_startMenuShortcut, "start-menu");
        _choiceFile = Path.Combine(_root, "Onta_desktop_shortcut.txt");
    }

    /// <summary>
    /// 一時フォルダーを消します。
    /// </summary>
    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// 作らないを選んだら、ClickOnce が作ったデスクトップのショートカットを消し、選択ファイルも消すこと。
    /// </summary>
    [Fact]
    public void ChoiceNo_DeletesDesktopShortcut()
    {
        var desktopShortcut = Path.Combine(_desktop, ShortcutFileName);
        File.WriteAllText(desktopShortcut, "clickonce");
        File.WriteAllText(_choiceFile, "0\r\n");

        Assert.True(DesktopShortcutSetting.Apply(_choiceFile, _desktop, _startMenuShortcut, ShortcutFileName));

        Assert.False(File.Exists(desktopShortcut));
        Assert.False(File.Exists(_choiceFile));
        Assert.True(File.Exists(_startMenuShortcut));
    }

    /// <summary>
    /// 作るを選んでデスクトップに無ければ、スタートメニューのショートカットをコピーすること。
    /// </summary>
    [Fact]
    public void ChoiceYes_CopiesStartMenuShortcutWhenMissing()
    {
        File.WriteAllText(_choiceFile, "1\r\n");

        Assert.True(DesktopShortcutSetting.Apply(_choiceFile, _desktop, _startMenuShortcut, ShortcutFileName));

        Assert.Equal("start-menu", File.ReadAllText(Path.Combine(_desktop, ShortcutFileName)));
        Assert.False(File.Exists(_choiceFile));
    }

    /// <summary>
    /// 作るを選んでデスクトップにすでにあれば、そのまま残すこと。
    /// </summary>
    [Fact]
    public void ChoiceYes_KeepsExistingDesktopShortcut()
    {
        var desktopShortcut = Path.Combine(_desktop, ShortcutFileName);
        File.WriteAllText(desktopShortcut, "clickonce");
        File.WriteAllText(_choiceFile, "1");

        Assert.True(DesktopShortcutSetting.Apply(_choiceFile, _desktop, _startMenuShortcut, ShortcutFileName));

        Assert.Equal("clickonce", File.ReadAllText(desktopShortcut));
    }

    /// <summary>
    /// 選択ファイルが無ければ（インストーラーを通さない起動・反映済み）、デスクトップに触れないこと。
    /// </summary>
    [Fact]
    public void NoChoiceFile_DoesNothing()
    {
        var desktopShortcut = Path.Combine(_desktop, ShortcutFileName);
        File.WriteAllText(desktopShortcut, "clickonce");

        Assert.False(DesktopShortcutSetting.Apply(_choiceFile, _desktop, _startMenuShortcut, ShortcutFileName));

        Assert.True(File.Exists(desktopShortcut));
    }
}
