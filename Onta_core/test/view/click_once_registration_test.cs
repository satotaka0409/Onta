using Onta.View.Core;
using Xunit;

namespace Onta.Core.Tests.View;

/// <summary>
/// ClickOnce のアンインストール情報から、このアプリの登録を選ぶ処理の検証です。
/// </summary>
public sealed class ClickOnceRegistrationTest
{
    private const string Publisher = "Onta Project";
    private const string OntaUninstall =
        "rundll32.exe dfshim.dll,ShArpMaintain Onta.application, Culture=neutral, PublicKeyToken=0000000000000000, processorArchitecture=msil";

    /// <summary>
    /// 発行元とアンインストールコマンドが一致する登録が 1 件なら、それを選ぶこと（他のアプリは選ばない）。
    /// </summary>
    [Fact]
    public void SingleOntaEntry_IsSelected()
    {
        var entries = new[]
        {
            Entry("other", "Other App", "Other", "rundll32.exe dfshim.dll,ShArpMaintain Other.application"),
            Entry("onta", "Onta x64", Publisher, OntaUninstall),
            Entry("msi", "Onta x64", Publisher, "MsiExec.exe /X{00000000-0000-0000-0000-000000000000}"),
        };

        var selected = ClickOnceRegistration.SelectEntry(entries, Publisher, "x64");

        Assert.Equal("onta", selected?.KeyName);
    }

    /// <summary>
    /// x64 版と ARM64 版の両方があれば、実行中の CPU の版を選ぶこと。
    /// </summary>
    [Theory]
    [InlineData("x64", "onta-x64")]
    [InlineData("ARM64", "onta-arm64")]
    public void BothArchitectures_SelectsRunningOne(string archLabel, string expectedKey)
    {
        var entries = new[]
        {
            Entry("onta-x64", "Onta x64", Publisher, OntaUninstall),
            Entry("onta-arm64", "Onta ARM64", Publisher, OntaUninstall),
        };

        var selected = ClickOnceRegistration.SelectEntry(entries, Publisher, archLabel);

        Assert.Equal(expectedKey, selected?.KeyName);
    }

    /// <summary>
    /// 該当する登録が無ければ null を返すこと。
    /// </summary>
    [Fact]
    public void NoOntaEntry_ReturnsNull()
    {
        var entries = new[]
        {
            Entry("other", "Other App", "Other", "rundll32.exe dfshim.dll,ShArpMaintain Other.application"),
        };

        Assert.Null(ClickOnceRegistration.SelectEntry(entries, Publisher, "x64"));
    }

    /// <summary>
    /// テスト用の登録を作ります。
    /// </summary>
    /// <param name="key">サブキー名。</param>
    /// <param name="displayName">表示名。</param>
    /// <param name="publisher">発行元。</param>
    /// <param name="uninstall">アンインストールコマンド。</param>
    /// <returns>登録。</returns>
    private static ClickOnceUninstallEntry Entry(string key, string displayName, string publisher, string uninstall) =>
        new(key, displayName, publisher, uninstall, displayName, publisher, "dfshim.dll,2");
}
