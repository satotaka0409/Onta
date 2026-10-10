using System.Globalization;

namespace Onta.View.Core;

/// <summary>
/// サンプリング周波数コンボの項目 Tag（XAML の数値文字列、または <c>x:Static</c> で渡した int）を読みます。
/// </summary>
internal static class SampleRateTag
{
    /// <summary>
    /// Tag をサンプリング周波数（Hz）として読みます。
    /// </summary>
    /// <param name="tag">ComboBoxItem の Tag。</param>
    /// <param name="sampleRate">読めたサンプリング周波数（Hz）。</param>
    /// <returns>int または数値文字列なら true。</returns>
    public static bool TryRead(object? tag, out int sampleRate)
    {
        switch (tag)
        {
            case int value:
                sampleRate = value;
                return true;
            case string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                sampleRate = parsed;
                return true;
            default:
                sampleRate = 0;
                return false;
        }
    }
}
