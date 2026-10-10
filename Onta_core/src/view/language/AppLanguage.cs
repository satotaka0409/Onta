using System.Globalization;

namespace Onta.View.Language;

/// <summary>
/// 起動時の表示言語（日本語／英語）を決めます。優先順は --lang、インストール時に選んだ言語、OS の言語です。
/// </summary>
public static class AppLanguage
{
    private const string LanguageOption = "--lang";
    private const string JapaneseCultureName = "ja-JP";
    private const string EnglishCultureName = "en-US";

    /// <summary>
    /// 表示言語を決めて UI カルチャに設定します。--lang、インストール時に選んだ言語、OS の言語の順に使い、日本語以外は英語にします。
    /// </summary>
    /// <remarks>コアのスレッドを起動する前に呼ぶ（既定カルチャは後から起動するスレッドにも効かせるため）。</remarks>
    /// <param name="args">起動引数（例: --lang en / --lang=ja）。</param>
    public static void ApplyFromArgs(IReadOnlyList<string> args)
    {
        var requested = FindLanguageName(args);
        if (string.IsNullOrWhiteSpace(requested))
        {
            requested = ReadInstalledLanguage();
        }

        var languageName = string.IsNullOrWhiteSpace(requested)
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : requested.Trim();
        var culture = CultureInfo.GetCultureInfo(IsJapanese(languageName) ? JapaneseCultureName : EnglishCultureName);

        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>
    /// 言語名が日本語（ja / ja-JP など）かどうかを判定します。
    /// </summary>
    /// <param name="languageName">言語名またはカルチャ名。</param>
    /// <returns>日本語なら true。</returns>
    private static bool IsJapanese(string languageName)
    {
        try
        {
            return string.Equals(
                CultureInfo.GetCultureInfo(languageName).TwoLetterISOLanguageName,
                "ja",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// インストール時に選んだ表示言語を読み取ります。
    /// </summary>
    /// <returns>言語名（ja / en）。ファイルが無い・読めないときは null。</returns>
    private static string? ReadInstalledLanguage()
    {
        try
        {
            var path = Onta.View.Core.AppPaths.LanguageSettingFilePath;
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 起動引数から --lang の値を取り出します。
    /// </summary>
    /// <param name="args">起動引数。</param>
    /// <returns>言語名。指定が無ければ null。</returns>
    private static string? FindLanguageName(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith(LanguageOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                return arg[(LanguageOption.Length + 1)..];
            }

            if (string.Equals(arg, LanguageOption, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
