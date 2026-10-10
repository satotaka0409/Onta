using System.Collections;
using System.Reflection;
using Xunit;

namespace Onta.Core.Tests;

internal static class HistoryAssert
{
    private const string HistoryServiceTypeName = "Onta.History.HistoryService, Onta";

    public static void SaveSendAndAssertRegistered(string testKey, string inputPath, string outputPath)
    {
        // out_files を汚さないよう、検証用履歴は一時ディレクトリへ書き検証後に削除する。
        var historyPath = Path.Combine(
            Path.GetTempPath(),
            "onta_test_history",
            $"history_{SanitizeFileName(testKey)}_{Guid.NewGuid():N}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
        if (File.Exists(historyPath))
        {
            File.Delete(historyPath);
        }

        try
        {
            var historyServiceType = Type.GetType(HistoryServiceTypeName)
                                     ?? throw new InvalidOperationException($"Type not found: {HistoryServiceTypeName}");
            var saveSendMethod = historyServiceType.GetMethod(
                "SaveSend",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(string), typeof(string), typeof(string), typeof(string)],
                modifiers: null);
            Assert.NotNull(saveSendMethod);

            saveSendMethod!.Invoke(null, [historyPath, inputPath, outputPath, "テスト送信履歴"]);

            var loadEntriesMethod = historyServiceType.GetMethod(
                "LoadEntries",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(string)],
                modifiers: null);
            Assert.NotNull(loadEntriesMethod);

            var entriesObject = loadEntriesMethod!.Invoke(null, [historyPath]);
            Assert.NotNull(entriesObject);

            var entries = ((IEnumerable)entriesObject!).Cast<object>().ToList();
            Assert.NotEmpty(entries);

            var latest = entries[^1];
            Assert.Equal("Send", ReadPropertyValue(latest, "Kind")?.ToString());
            Assert.Equal(inputPath, ReadPropertyValue(latest, "SourcePath")?.ToString());
            Assert.Equal(outputPath, ReadPropertyValue(latest, "OutputPath")?.ToString());
        }
        finally
        {
            try
            {
                if (File.Exists(historyPath))
                {
                    File.Delete(historyPath);
                }
            }
            catch
            {
                // 一時ファイル削除失敗はテスト結果に影響させない。
            }
        }
    }

    private static object? ReadPropertyValue(object instance, string propertyName)
    {
        var prop = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(prop);
        return prop!.GetValue(instance);
    }

    private static string SanitizeFileName(string source)
    {
        var safe = source;
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(safe) ? "history_test" : safe;
    }
}
