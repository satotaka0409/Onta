using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// 1.ファイルを送信して履歴に登録されることを確認
/// 2.ファイルを受信して履歴に登録されることを確認
/// 3.ブロックデータのみを受信して、未完了ブロックに登録されることを確認
/// 4.ブロックデータのみを受信して、既存データに追加されることを確認
/// 5.同じデータを受信した際に、重複して履歴に登録されないことを確認（受信日時の更新のみ行われることを確認）
/// 6.履歴削除が正しく行われることを確認（送信、受信、未完了ブロックを含むすべての履歴が削除されることを確認）
/// 7.ファイルを受信して履歴に登録されて、履歴からダウンロードして、同じファイルかどうか確認
/// </summary>
public sealed class OntaTestHistory2
{
    private const string HistoryServiceTypeName = "Onta.History.HistoryService, Onta";
    private const string ReceiveEntryTypeName = "Onta.History.ReceiveHistoryEntry, Onta";
    private const string ReceiveBlockTypeName = "Onta.History.ReceiveBlockHistory, Onta";
    private const string ReceiveOrphanTypeName = "Onta.History.ReceiveOrphanHistory, Onta";
    private const string HistoryKindTypeName = "Onta.History.HistoryEntryKind, Onta";
    private const string InputDeviceTypeName = "Onta.History.ReceiveInputDevice, Onta";
    private const string BlockStateTypeName = "Onta.History.ReceiveBlockState, Onta";

    [Fact]
    public void HistoryFlow_SendReceiveBlockOnly_AddUpdate_Deduplicate()
    {
        var historyPath = Path.Combine(
            Path.GetTempPath(),
            "onta_test_history",
            $"history_flow_{Guid.NewGuid():N}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);

        var inputPath = TestPaths.ResolveInputPng();
        var wavPath = TestPaths.ResolveOutputPath($"history_flow_{Guid.NewGuid():N}.wav");

        try
        {
            // 1. 送信履歴登録
            InvokeSaveSend(historyPath, inputPath, wavPath, "送信テスト");
            var entries1 = LoadEntries(historyPath);
            Assert.Single(entries1);
            Assert.Equal("Send", ReadProperty(entries1[0], "Kind")?.ToString());

            // 2. 受信履歴登録（成功エントリ）
            var receiveMainId = "RX_MAIN_FILE";
            var receiveMain = CreateReceiveEntry(
                entryId: Guid.NewGuid().ToString("N"),
                contentHashHex: receiveMainId,
                sourcePath: "C:/tmp/receive_main.wav",
                fileName: "receive_main.bin",
                fileSize: 16384,
                blockCount: 2,
                isSuccess: true,
                outputPath: "C:/tmp/receive_main.bin",
                completionMessage: "受信完了",
                blocks: Array.Empty<object>(),
                orphans: Array.Empty<object>(),
                receivedAtUtc: DateTime.UtcNow.AddMinutes(-10));
            InvokeSaveReceive(historyPath, receiveMain);

            var entries2 = LoadEntries(historyPath);
            Assert.Equal(2, entries2.Count);
            Assert.Single(entries2, e => ReadProperty(e, "Kind")?.ToString() == "Receive");

            // 3. BH+BD単独（未完了）を登録
            var blockOnlyHash = "RX_BLOCK_ONLY_FILE";
            var block0Incomplete = CreateBlock(
                blockIndex: 0,
                blockSize: 0,
                blockComplete: false,
                stateValue: 0,
                errorText: "IN-COMPLETE",
                blockHashSeed: 0x11,
                payload: Array.Empty<byte>());
            var blockOnlyEntry1 = CreateReceiveEntry(
                entryId: Guid.NewGuid().ToString("N"),
                contentHashHex: blockOnlyHash,
                sourcePath: "",
                fileName: "(未登録データ)",
                fileSize: 0,
                blockCount: 2,
                isSuccess: false,
                outputPath: "",
                completionMessage: "未完了",
                blocks: new[] { block0Incomplete },
                orphans: Array.Empty<object>(),
                receivedAtUtc: DateTime.UtcNow.AddMinutes(-5));
            InvokeSaveReceive(historyPath, blockOnlyEntry1);

            var entries3 = LoadEntries(historyPath);
            Assert.Equal(3, entries3.Count);
            var blockOnlyMerged1 = entries3.Single(e => string.Equals(ReadProperty(e, "ContentHashHex")?.ToString(), blockOnlyHash, StringComparison.Ordinal));
            var blocksAfter3 = ReadBlocks(blockOnlyMerged1);
            Assert.Single(blocksAfter3);
            Assert.False((bool)(ReadProperty(blocksAfter3[0], "BlockComplete") ?? true));

            // 4. 別ブロックを追加登録（既存データへ追加）
            var block1Incomplete = CreateBlock(
                blockIndex: 1,
                blockSize: 0,
                blockComplete: false,
                stateValue: 0,
                errorText: "IN-COMPLETE",
                blockHashSeed: 0x22,
                payload: Array.Empty<byte>());
            var blockOnlyEntry2 = CreateReceiveEntry(
                entryId: Guid.NewGuid().ToString("N"),
                contentHashHex: blockOnlyHash,
                sourcePath: "",
                fileName: "(未登録データ)",
                fileSize: 0,
                blockCount: 2,
                isSuccess: false,
                outputPath: "",
                completionMessage: "未完了",
                blocks: new[] { block1Incomplete },
                orphans: Array.Empty<object>(),
                receivedAtUtc: DateTime.UtcNow.AddMinutes(-2));
            InvokeSaveReceive(historyPath, blockOnlyEntry2);

            var entries4 = LoadEntries(historyPath);
            Assert.Equal(3, entries4.Count);
            var blockOnlyMerged2 = entries4.Single(e => string.Equals(ReadProperty(e, "ContentHashHex")?.ToString(), blockOnlyHash, StringComparison.Ordinal));
            var blocksAfter4 = ReadBlocks(blockOnlyMerged2);
            Assert.Equal(2, blocksAfter4.Count);
            Assert.Contains(blocksAfter4, b => (int)(ReadProperty(b, "BlockIndex") ?? -1) == 0);
            Assert.Contains(blocksAfter4, b => (int)(ReadProperty(b, "BlockIndex") ?? -1) == 1);

            // 5. 同一データ再受信時に重複せず、ReceivedAtUtcのみ更新
            var beforeRepeat = (DateTime)(ReadProperty(blockOnlyMerged2, "ReceivedAtUtc") ?? DateTime.MinValue);
            var repeatEntry = CreateReceiveEntry(
                entryId: Guid.NewGuid().ToString("N"),
                contentHashHex: blockOnlyHash,
                sourcePath: "",
                fileName: "(未登録データ)",
                fileSize: 0,
                blockCount: 2,
                isSuccess: false,
                outputPath: "",
                completionMessage: "未完了",
                blocks: Array.Empty<object>(),
                orphans: Array.Empty<object>(),
                receivedAtUtc: DateTime.UtcNow.AddMinutes(1));
            InvokeSaveReceive(historyPath, repeatEntry);

            var entries5 = LoadEntries(historyPath);
            Assert.Equal(3, entries5.Count);
            var blockOnlyMerged3 = entries5.Single(e => string.Equals(ReadProperty(e, "ContentHashHex")?.ToString(), blockOnlyHash, StringComparison.Ordinal));
            var afterRepeat = (DateTime)(ReadProperty(blockOnlyMerged3, "ReceivedAtUtc") ?? DateTime.MinValue);
            Assert.True(afterRepeat >= beforeRepeat, "同一データ再受信時に ReceivedAtUtc が更新されること");
            Assert.Equal(2, ReadBlocks(blockOnlyMerged3).Count);
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

    [Fact]
    public void History_DeleteAllEntries_RemovesSendReceiveAndIncomplete()
    {
        var historyPath = Path.Combine(
            Path.GetTempPath(),
            "onta_test_history",
            $"history_delete_{Guid.NewGuid():N}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);

        var inputPath = TestPaths.ResolveInputPng();
        var wavPath = TestPaths.ResolveOutputPath($"history_delete_{Guid.NewGuid():N}.wav");

        try
        {
            // 送信履歴
            InvokeSaveSend(historyPath, inputPath, wavPath, "送信削除テスト");

            // 受信履歴（完了）
            var receiveMain = CreateReceiveEntry(
                entryId: "RX_DELETE_MAIN",
                contentHashHex: "RX_DELETE_MAIN_HASH",
                sourcePath: "C:/tmp/rx_delete_main.wav",
                fileName: "rx_delete_main.bin",
                fileSize: 1024,
                blockCount: 1,
                isSuccess: true,
                outputPath: "C:/tmp/rx_delete_main.bin",
                completionMessage: "受信完了",
                blocks: Array.Empty<object>(),
                orphans: Array.Empty<object>(),
                receivedAtUtc: DateTime.UtcNow.AddMinutes(-3));
            InvokeSaveReceive(historyPath, receiveMain);

            // 受信履歴（未完了ブロック）
            var incompleteBlock = CreateBlock(
                blockIndex: 0,
                blockSize: 0,
                blockComplete: false,
                stateValue: 0,
                errorText: "IN-COMPLETE",
                blockHashSeed: 0x33,
                payload: Array.Empty<byte>());
            var receiveIncomplete = CreateReceiveEntry(
                entryId: "RX_DELETE_INCOMPLETE",
                contentHashHex: "RX_DELETE_INCOMPLETE_HASH",
                sourcePath: "",
                fileName: "(未登録データ)",
                fileSize: 0,
                blockCount: 1,
                isSuccess: false,
                outputPath: "",
                completionMessage: "未完了",
                blocks: new[] { incompleteBlock },
                orphans: Array.Empty<object>(),
                receivedAtUtc: DateTime.UtcNow.AddMinutes(-2));
            InvokeSaveReceive(historyPath, receiveIncomplete);

            var entriesBeforeDelete = LoadEntries(historyPath);
            Assert.Equal(3, entriesBeforeDelete.Count);

            // すべての履歴を削除
            foreach (var entry in entriesBeforeDelete)
            {
                var entryId = ReadProperty(entry, "EntryId")?.ToString();
                Assert.False(string.IsNullOrWhiteSpace(entryId));
                var removed = InvokeDeleteEntry(historyPath, entryId!);
                Assert.True(removed, $"DeleteEntry failed. entryId={entryId}");
            }

            var entriesAfterDelete = LoadEntries(historyPath);
            Assert.Empty(entriesAfterDelete);
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

    [Fact]
    public void History_ExportPayload_RebuildsOriginalFile()
    {
        var historyPath = Path.Combine(
            Path.GetTempPath(),
            "onta_test_history",
            $"history_export_{Guid.NewGuid():N}.bin");
        var downloadPath = Path.Combine(
            Path.GetTempPath(),
            "onta_test_history",
            $"history_export_download_{Guid.NewGuid():N}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);

        var original = new byte[1027];
        for (var i = 0; i < original.Length; i++)
        {
            original[i] = (byte)((i * 17 + 31) & 0xFF);
        }

        var block0 = original.Take(400).ToArray();
        var block1 = original.Skip(400).Take(400).ToArray();
        var block2 = original.Skip(800).ToArray();
        var fileHashHex = Convert.ToHexString(SHA256.HashData(original));

        try
        {
            var receiveMain = CreateReceiveEntry(
                entryId: "RX_EXPORT_MAIN",
                contentHashHex: fileHashHex,
                sourcePath: "C:/tmp/rx_export_main.wav",
                fileName: "rx_export_main.bin",
                fileSize: original.Length,
                blockCount: 3,
                isSuccess: true,
                outputPath: string.Empty,
                completionMessage: "受信完了",
                blocks: new[]
                {
                    CreateBlock(blockIndex: 0, blockSize: block0.Length, blockComplete: true, stateValue: 1, errorText: string.Empty, blockHashSeed: 0x11, payload: block0),
                    CreateBlock(blockIndex: 1, blockSize: block1.Length, blockComplete: true, stateValue: 1, errorText: string.Empty, blockHashSeed: 0x22, payload: block1),
                    CreateBlock(blockIndex: 2, blockSize: block2.Length, blockComplete: true, stateValue: 1, errorText: string.Empty, blockHashSeed: 0x33, payload: block2)
                },
                orphans: Array.Empty<object>(),
                receivedAtUtc: DateTime.UtcNow);
            InvokeSaveReceive(historyPath, receiveMain);

            var entries = LoadEntries(historyPath);
            var receive = entries.Single(e => string.Equals(ReadProperty(e, "EntryId")?.ToString(), "RX_EXPORT_MAIN", StringComparison.Ordinal));
            var exported = InvokeExportPayloadToFile(receive, downloadPath);
            Assert.True(exported, "履歴ブロックからファイルを再構成して保存できること");
            Assert.True(File.Exists(downloadPath), "エクスポート先ファイルが作成されること");

            var downloaded = File.ReadAllBytes(downloadPath);
            Assert.Equal(original.Length, downloaded.Length);
            Assert.True(original.SequenceEqual(downloaded), "履歴からダウンロードした内容が元データと一致すること");
        }
        finally
        {
            try
            {
                if (File.Exists(historyPath))
                {
                    File.Delete(historyPath);
                }

                if (File.Exists(downloadPath))
                {
                    File.Delete(downloadPath);
                }
            }
            catch
            {
                // 一時ファイル削除失敗はテスト結果に影響させない。
            }
        }
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

    private static void InvokeSaveReceive(string historyPath, object receiveEntry)
    {
        var historyServiceType = ResolveType(HistoryServiceTypeName);
        var receiveEntryType = ResolveType(ReceiveEntryTypeName);
        var saveReceive = historyServiceType.GetMethod(
            "SaveReceive",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string), receiveEntryType],
            modifiers: null);
        Assert.NotNull(saveReceive);
        saveReceive!.Invoke(null, [historyPath, receiveEntry]);
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

    private static bool InvokeExportPayloadToFile(object receiveEntry, string targetPath)
    {
        var historyServiceType = ResolveType(HistoryServiceTypeName);
        var receiveEntryType = ResolveType(ReceiveEntryTypeName);
        var export = historyServiceType.GetMethod(
            "ExportPayloadToFile",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [receiveEntryType, typeof(string)],
            modifiers: null);
        Assert.NotNull(export);
        var result = export!.Invoke(null, [receiveEntry, targetPath]);
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

    private static List<object> ReadBlocks(object entry)
    {
        var blocksObj = ReadProperty(entry, "Blocks") as IEnumerable;
        Assert.NotNull(blocksObj);
        return blocksObj!.Cast<object>().ToList();
    }

    private static object CreateReceiveEntry(
        string entryId,
        string contentHashHex,
        string sourcePath,
        string fileName,
        long fileSize,
        int blockCount,
        bool isSuccess,
        string outputPath,
        string completionMessage,
        IReadOnlyList<object> blocks,
        IReadOnlyList<object> orphans,
        DateTime receivedAtUtc)
    {
        var receiveEntryType = ResolveType(ReceiveEntryTypeName);
        var historyKindType = ResolveType(HistoryKindTypeName);
        var inputDeviceType = ResolveType(InputDeviceTypeName);
        var receiveBlockType = ResolveType(ReceiveBlockTypeName);
        var orphanType = ResolveType(ReceiveOrphanTypeName);

        var blockArray = Array.CreateInstance(receiveBlockType, blocks.Count);
        for (var i = 0; i < blocks.Count; i++)
        {
            blockArray.SetValue(blocks[i], i);
        }

        var orphanArray = Array.CreateInstance(orphanType, orphans.Count);
        for (var i = 0; i < orphans.Count; i++)
        {
            orphanArray.SetValue(orphans[i], i);
        }

        var now = DateTime.UtcNow;
        return Activator.CreateInstance(receiveEntryType, [
            entryId,
            Enum.ToObject(historyKindType, 1),
            Enum.ToObject(inputDeviceType, 0),
            new byte[4],
            receivedAtUtc,
            now,
            now,
            contentHashHex,
            sourcePath,
            fileName,
            fileSize,
            blockCount,
            isSuccess,
            outputPath,
            completionMessage,
            blockArray,
            orphanArray
        ])!;
    }

    private static object CreateBlock(
        int blockIndex,
        int blockSize,
        bool blockComplete,
        int stateValue,
        string errorText,
        byte blockHashSeed,
        byte[] payload)
    {
        var blockType = ResolveType(ReceiveBlockTypeName);
        var blockStateType = ResolveType(BlockStateTypeName);
        var hash = Enumerable.Repeat(blockHashSeed, 32).ToArray();
        return Activator.CreateInstance(blockType, [
            new byte[4],
            blockIndex,
            blockSize,
            hash,
            blockComplete,
            payload,
            Enum.ToObject(blockStateType, stateValue),
            errorText
        ])!;
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
}
