using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

namespace Onta.Core.Tests.History;

/// <summary>
/// 送信履歴の登録・削除テスト（入力は Sample*.txt）。
/// 1. ファイル送信を仮定して履歴登録できる
/// 2. 別ファイル送信を仮定して追加登録できる
/// 3. 履歴の単件削除ができる
/// 4. 全履歴の削除ができる
/// </summary>
public sealed class OntaTestHistorySend
{
    private const string HistoryServiceTypeName = "Onta.History.HistoryService, Onta";

    /// <summary>
    /// 1. Sample1.txt を送信したと仮定し、送信履歴へ登録できること。
    /// </summary>
    [Fact]
    public void Send_Register_Sample1Txt_AddsSendEntry()
    {
        using var scope = HistoryTemp.Create();
        var inputPath = TestPaths.ResolveInputTxt("Sample1.txt");
        var wavPath = TestPaths.ResolveOutputPath($"history_send_sample1_{Guid.NewGuid():N}.wav");
        var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inputPath)));
        var expectedSize = new FileInfo(inputPath).Length;

        InvokeSaveSend(scope.HistoryPath, inputPath, wavPath, "送信登録テスト1");

        var entries = LoadEntries(scope.HistoryPath);
        Assert.Single(entries);
        var send = entries[0];
        Assert.Equal("Send", ReadProperty(send, "Kind")?.ToString());
        Assert.Equal("Sample1.txt", ReadProperty(send, "FileName")?.ToString());
        Assert.Equal(inputPath, ReadProperty(send, "SourcePath")?.ToString());
        Assert.Equal(wavPath, ReadProperty(send, "OutputPath")?.ToString());
        Assert.Equal(expectedSize, (long)(ReadProperty(send, "FileSize") ?? -1L));
        Assert.Equal(expectedHash, ReadProperty(send, "ContentHashHex")?.ToString());
        Assert.True((bool)(ReadProperty(send, "IsSuccess") ?? false));
    }

    /// <summary>
    /// 2. 別の Sample2.txt を送信したと仮定し、履歴へ追加登録できること。
    /// </summary>
    [Fact]
    public void Send_Register_SecondDifferentFile_AddsAnotherEntry()
    {
        using var scope = HistoryTemp.Create();
        var sample1 = TestPaths.ResolveInputTxt("Sample1.txt");
        var sample2 = TestPaths.ResolveInputTxt("Sample2.txt");
        var wav1 = TestPaths.ResolveOutputPath($"history_send_s1_{Guid.NewGuid():N}.wav");
        var wav2 = TestPaths.ResolveOutputPath($"history_send_s2_{Guid.NewGuid():N}.wav");

        Assert.False(
            string.Equals(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sample1))),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sample2))),
                StringComparison.OrdinalIgnoreCase),
            "Sample1.txt と Sample2.txt は異なる内容であること（送信履歴は ContentHash で同一判定する）");

        InvokeSaveSend(scope.HistoryPath, sample1, wav1, "送信登録テスト1");
        InvokeSaveSend(scope.HistoryPath, sample2, wav2, "送信登録テスト2");

        var entries = LoadEntries(scope.HistoryPath);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("Send", ReadProperty(e, "Kind")?.ToString()));

        var names = entries.Select(e => ReadProperty(e, "FileName")?.ToString()).ToArray();
        Assert.Contains("Sample1.txt", names);
        Assert.Contains("Sample2.txt", names);

        var paths = entries.Select(e => ReadProperty(e, "SourcePath")?.ToString()).ToArray();
        Assert.Contains(sample1, paths);
        Assert.Contains(sample2, paths);
    }

    /// <summary>
    /// 3. 送信履歴の単件削除ができること。
    /// </summary>
    [Fact]
    public void Send_DeleteEntry_RemovesSelectedSend()
    {
        using var scope = HistoryTemp.Create();
        var sample1 = TestPaths.ResolveInputTxt("Sample1.txt");
        var sample2 = TestPaths.ResolveInputTxt("Sample2.txt");
        var wav1 = TestPaths.ResolveOutputPath($"history_send_del1_{Guid.NewGuid():N}.wav");
        var wav2 = TestPaths.ResolveOutputPath($"history_send_del2_{Guid.NewGuid():N}.wav");

        InvokeSaveSend(scope.HistoryPath, sample1, wav1, "削除対象");
        InvokeSaveSend(scope.HistoryPath, sample2, wav2, "残す対象");

        var before = LoadEntries(scope.HistoryPath);
        Assert.Equal(2, before.Count);

        var toDelete = before.Single(e => string.Equals(ReadProperty(e, "FileName")?.ToString(), "Sample1.txt", StringComparison.Ordinal));
        var deleteId = ReadProperty(toDelete, "EntryId")?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(deleteId));

        Assert.True(InvokeDeleteEntry(scope.HistoryPath, deleteId!));

        var after = LoadEntries(scope.HistoryPath);
        Assert.Single(after);
        Assert.Equal("Sample2.txt", ReadProperty(after[0], "FileName")?.ToString());
        Assert.Equal(sample2, ReadProperty(after[0], "SourcePath")?.ToString());
    }

    /// <summary>
    /// 4. 送信履歴をすべて削除できること。
    /// </summary>
    [Fact]
    public void Send_DeleteAllEntries_ClearsHistory()
    {
        using var scope = HistoryTemp.Create();
        var samples = new[]
        {
            TestPaths.ResolveInputTxt("Sample1.txt"),
            TestPaths.ResolveInputTxt("Sample2.txt"),
            TestPaths.ResolveInputTxt("Sample3.txt")
        };

        foreach (var sample in samples)
        {
            var wav = TestPaths.ResolveOutputPath($"history_send_clear_{Path.GetFileNameWithoutExtension(sample)}_{Guid.NewGuid():N}.wav");
            InvokeSaveSend(scope.HistoryPath, sample, wav, "全削除前");
        }

        var before = LoadEntries(scope.HistoryPath);
        Assert.Equal(3, before.Count);
        Assert.All(before, e => Assert.Equal("Send", ReadProperty(e, "Kind")?.ToString()));

        foreach (var entry in before)
        {
            var entryId = ReadProperty(entry, "EntryId")?.ToString();
            Assert.False(string.IsNullOrWhiteSpace(entryId));
            Assert.True(InvokeDeleteEntry(scope.HistoryPath, entryId!), $"DeleteEntry failed. entryId={entryId}");
        }

        Assert.Empty(LoadEntries(scope.HistoryPath));
    }

    private static void InvokeSaveSend(string historyPath, string inputPath, string outputPath, string completionMessage)
    {
        var historyServiceType = ResolveType(HistoryServiceTypeName);
        var saveSend = historyServiceType.GetMethod(
            "SaveSend",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string), typeof(string), typeof(string), typeof(string)],
            modifiers: null);
        Assert.NotNull(saveSend);
        saveSend!.Invoke(null, [historyPath, inputPath, outputPath, completionMessage]);
    }

    private static bool InvokeDeleteEntry(string historyPath, string entryId)
    {
        var historyServiceType = ResolveType(HistoryServiceTypeName);
        var deleteEntry = historyServiceType.GetMethod(
            "DeleteEntry",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string), typeof(string)],
            modifiers: null);
        Assert.NotNull(deleteEntry);
        var result = deleteEntry!.Invoke(null, [historyPath, entryId]);
        Assert.IsType<bool>(result);
        return (bool)result!;
    }

    private static List<object> LoadEntries(string historyPath)
    {
        var historyServiceType = ResolveType(HistoryServiceTypeName);
        var loadEntries = historyServiceType.GetMethod(
            "LoadEntries",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string)],
            modifiers: null);
        Assert.NotNull(loadEntries);
        var loaded = loadEntries!.Invoke(null, [historyPath]);
        Assert.NotNull(loaded);
        return ((IEnumerable)loaded!).Cast<object>().ToList();
    }

    private static Type ResolveType(string typeName)
    {
        return Type.GetType(typeName)
               ?? throw new InvalidOperationException($"Type not found: {typeName}");
    }

    private static object? ReadProperty(object instance, string propertyName)
    {
        var prop = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(prop);
        return prop!.GetValue(instance);
    }

    /// <summary>
    /// 一時履歴ファイルの作成と破棄。
    /// </summary>
    private sealed class HistoryTemp : IDisposable
    {
        public string HistoryPath { get; }

        private HistoryTemp(string historyPath)
        {
            HistoryPath = historyPath;
        }

        public static HistoryTemp Create()
        {
            var historyPath = Path.Combine(
                Path.GetTempPath(),
                "onta_test_history",
                $"history_send_{Guid.NewGuid():N}.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
            return new HistoryTemp(historyPath);
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(HistoryPath))
                {
                    File.Delete(HistoryPath);
                }
            }
            catch
            {
                // 一時ファイル削除失敗はテスト結果に影響させない。
            }
        }
    }
}
