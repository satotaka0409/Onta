using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

namespace Onta.Core.Tests.History;

/// <summary>
/// 受信履歴の登録・マージ・ダウンロード・削除テスト（入力は Sample*.txt）。
/// 1. ファイル受信を仮定して履歴登録でき、ブロック履歴も登録される
/// 2. ブロック受信エラーを仮定してエラーブロックが登録される
/// 3. エラーだったブロックが正常受信できた場合、正常ブロックへ更新される
/// 4. 受信成功ファイルをダウンロードできる
/// 5. 受信履歴の単件削除ができる
/// 6. 受信履歴の全削除ができる
/// </summary>
public sealed class OntaTestHistoryReceive
{
    private const string HistoryServiceTypeName = "Onta.History.HistoryService, Onta";
    private const string ReceiveEntryTypeName = "Onta.History.ReceiveHistoryEntry, Onta";
    private const string ReceiveBlockTypeName = "Onta.History.ReceiveBlockHistory, Onta";
    private const string ReceiveOrphanTypeName = "Onta.History.ReceiveOrphanHistory, Onta";
    private const string HistoryKindTypeName = "Onta.History.HistoryEntryKind, Onta";
    private const string InputDeviceTypeName = "Onta.History.ReceiveInputDevice, Onta";
    private const string BlockStateTypeName = "Onta.History.ReceiveBlockState, Onta";

    private const int StateUnknown = 0;
    private const int StateAccepted = 1;
    private const int StateError = 2;

    /// <summary>
    /// 1. Sample1.txt を受信したと仮定し、エントリとブロック履歴が登録されること。
    /// </summary>
    [Fact]
    public void Receive_Register_Sample1Txt_AddsEntryWithBlockHistory()
    {
        using var scope = HistoryTemp.Create();
        var payload = File.ReadAllBytes(TestPaths.ResolveInputTxt("Sample1.txt"));
        var chunks = SplitIntoBlocks(payload, blockCount: 2);
        var fileHash = ToSha512Hex(payload);
        var sourceWav = TestPaths.ResolveOutputPath($"history_rx_sample1_{Guid.NewGuid():N}.wav");

        var blocks = chunks.Select((chunk, i) => CreateCompleteBlock(i, chunk)).ToArray();
        var entry = CreateReceiveEntry(
            entryId: Guid.NewGuid().ToString("N"),
            contentHashHex: fileHash,
            sourcePath: sourceWav,
            fileName: "Sample1.txt",
            fileSize: payload.Length,
            blockCount: blocks.Length,
            isSuccess: true,
            outputPath: string.Empty,
            completionMessage: "受信完了",
            blocks: blocks,
            orphans: Array.Empty<object>(),
            receivedAtUtc: DateTime.UtcNow);

        InvokeSaveReceive(scope.HistoryPath, entry);

        var loaded = LoadEntries(scope.HistoryPath);
        Assert.Single(loaded);
        var receive = loaded[0];
        Assert.Equal("Receive", ReadProperty(receive, "Kind")?.ToString());
        Assert.Equal("Sample1.txt", ReadProperty(receive, "FileName")?.ToString());
        Assert.Equal(fileHash, ReadProperty(receive, "ContentHashHex")?.ToString());
        Assert.Equal(payload.Length, (long)(ReadProperty(receive, "FileSize") ?? -1L));
        Assert.Equal(2, (int)(ReadProperty(receive, "BlockCount") ?? -1));
        Assert.True((bool)(ReadProperty(receive, "IsSuccess") ?? false));

        var storedBlocks = ReadBlocks(receive);
        Assert.Equal(2, storedBlocks.Count);
        Assert.All(storedBlocks, b =>
        {
            Assert.True((bool)(ReadProperty(b, "BlockComplete") ?? false));
            Assert.Equal("Accepted", ReadProperty(b, "State")?.ToString());
            Assert.True(((byte[])(ReadProperty(b, "BlockData") ?? Array.Empty<byte>())).Length > 0);
        });
        Assert.Equal(0, (int)(ReadProperty(storedBlocks[0], "BlockIndex") ?? -1));
        Assert.Equal(1, (int)(ReadProperty(storedBlocks[1], "BlockIndex") ?? -1));
        Assert.Equal(chunks[0].Length, ((byte[])ReadProperty(storedBlocks[0], "BlockData")!).Length);
        Assert.Equal(chunks[1].Length, ((byte[])ReadProperty(storedBlocks[1], "BlockData")!).Length);
    }

    /// <summary>
    /// 2. Sample2.txt のブロック受信エラーを仮定し、エラーブロックが登録されること。
    /// </summary>
    [Fact]
    public void Receive_Register_ErrorBlock_StoresErrorState()
    {
        using var scope = HistoryTemp.Create();
        var payload = File.ReadAllBytes(TestPaths.ResolveInputTxt("Sample2.txt"));
        var chunks = SplitIntoBlocks(payload, blockCount: 2);
        var fileHash = ToSha512Hex(payload);
        var sourceWav = TestPaths.ResolveOutputPath($"history_rx_err_{Guid.NewGuid():N}.wav");

        // BLK-0 はエラー、BLK-1 は未受信扱い（エラーのみ登録）
        var errorBlock = CreateErrorBlock(blockIndex: 0, errorText: "CRC-ERROR");
        var entry = CreateReceiveEntry(
            entryId: Guid.NewGuid().ToString("N"),
            contentHashHex: fileHash,
            sourcePath: sourceWav,
            fileName: "Sample2.txt",
            fileSize: payload.Length,
            blockCount: chunks.Length,
            isSuccess: false,
            outputPath: string.Empty,
            completionMessage: "未完了",
            blocks: [errorBlock],
            orphans: Array.Empty<object>(),
            receivedAtUtc: DateTime.UtcNow);

        InvokeSaveReceive(scope.HistoryPath, entry);

        var receive = LoadEntries(scope.HistoryPath).Single();
        Assert.Equal("Receive", ReadProperty(receive, "Kind")?.ToString());
        Assert.False((bool)(ReadProperty(receive, "IsSuccess") ?? true));

        var storedBlocks = ReadBlocks(receive);
        Assert.Single(storedBlocks);
        var blk0 = storedBlocks[0];
        Assert.Equal(0, (int)(ReadProperty(blk0, "BlockIndex") ?? -1));
        // バイナリ履歴は BlockComplete のみ永続化（State/ErrorText は再読込で Unknown/空）
        Assert.False((bool)(ReadProperty(blk0, "BlockComplete") ?? true), "エラーブロックは未完了として残ること");
        Assert.Empty((byte[])(ReadProperty(blk0, "BlockData") ?? Array.Empty<byte>()));
        Assert.NotEqual("Accepted", ReadProperty(blk0, "State")?.ToString());
    }

    /// <summary>
    /// 3. エラーだったブロックが正常受信できた場合、正常ブロックへ更新されること。
    /// </summary>
    [Fact]
    public void Receive_Retry_ErrorBlockBecomesAccepted()
    {
        using var scope = HistoryTemp.Create();
        var payload = File.ReadAllBytes(TestPaths.ResolveInputTxt("Sample2.txt"));
        var chunks = SplitIntoBlocks(payload, blockCount: 2);
        var fileHash = ToSha512Hex(payload);
        var sourceWav = TestPaths.ResolveOutputPath($"history_rx_retry_{Guid.NewGuid():N}.wav");

        // 先にエラー登録
        InvokeSaveReceive(
            scope.HistoryPath,
            CreateReceiveEntry(
                entryId: Guid.NewGuid().ToString("N"),
                contentHashHex: fileHash,
                sourcePath: sourceWav,
                fileName: "Sample2.txt",
                fileSize: payload.Length,
                blockCount: chunks.Length,
                isSuccess: false,
                outputPath: string.Empty,
                completionMessage: "未完了",
                blocks: [CreateErrorBlock(0, "CRC-ERROR")],
                orphans: Array.Empty<object>(),
                receivedAtUtc: DateTime.UtcNow.AddMinutes(-1)));

        var before = ReadBlocks(LoadEntries(scope.HistoryPath).Single()).Single();
        Assert.False((bool)(ReadProperty(before, "BlockComplete") ?? true), "再試行前はエラー（未完了）ブロックであること");
        Assert.Empty((byte[])(ReadProperty(before, "BlockData") ?? Array.Empty<byte>()));

        // 同一ハッシュで正常ブロックを再登録（マージで上書き）
        InvokeSaveReceive(
            scope.HistoryPath,
            CreateReceiveEntry(
                entryId: Guid.NewGuid().ToString("N"),
                contentHashHex: fileHash,
                sourcePath: sourceWav,
                fileName: "Sample2.txt",
                fileSize: payload.Length,
                blockCount: chunks.Length,
                isSuccess: false,
                outputPath: string.Empty,
                completionMessage: "未完了",
                blocks: [CreateCompleteBlock(0, chunks[0])],
                orphans: Array.Empty<object>(),
                receivedAtUtc: DateTime.UtcNow));

        var entries = LoadEntries(scope.HistoryPath);
        Assert.Single(entries);
        var afterBlocks = ReadBlocks(entries[0]);
        Assert.Single(afterBlocks);
        var blk0 = afterBlocks[0];
        Assert.Equal(0, (int)(ReadProperty(blk0, "BlockIndex") ?? -1));
        Assert.True((bool)(ReadProperty(blk0, "BlockComplete") ?? false));
        Assert.Equal("Accepted", ReadProperty(blk0, "State")?.ToString());
        Assert.Equal(string.Empty, ReadProperty(blk0, "ErrorText")?.ToString());
        Assert.Equal(chunks[0], (byte[])ReadProperty(blk0, "BlockData")!);
    }

    /// <summary>
    /// 4. 受信成功した Sample3.txt を履歴からダウンロードできること。
    /// </summary>
    [Fact]
    public void Receive_ExportPayload_DownloadsOriginalSample3()
    {
        using var scope = HistoryTemp.Create();
        var payload = File.ReadAllBytes(TestPaths.ResolveInputTxt("Sample3.txt"));
        var chunks = SplitIntoBlocks(payload, blockCount: 2);
        var fileHash = ToSha512Hex(payload);
        var sourceWav = TestPaths.ResolveOutputPath($"history_rx_dl_{Guid.NewGuid():N}.wav");
        var downloadPath = TestPaths.ResolveOutputPath($"history_rx_dl_out_{Guid.NewGuid():N}.txt");

        try
        {
            InvokeSaveReceive(
                scope.HistoryPath,
                CreateReceiveEntry(
                    entryId: Guid.NewGuid().ToString("N"),
                    contentHashHex: fileHash,
                    sourcePath: sourceWav,
                    fileName: "Sample3.txt",
                    fileSize: payload.Length,
                    blockCount: chunks.Length,
                    isSuccess: true,
                    outputPath: string.Empty,
                    completionMessage: "受信完了",
                    blocks: chunks.Select((c, i) => CreateCompleteBlock(i, c)).ToArray(),
                    orphans: Array.Empty<object>(),
                    receivedAtUtc: DateTime.UtcNow));

            var receive = LoadEntries(scope.HistoryPath).Single();
            Assert.True(InvokeCanExportPayload(receive));
            Assert.True(InvokeExportPayloadToFile(receive, downloadPath));
            Assert.True(File.Exists(downloadPath));
            Assert.Equal(payload, File.ReadAllBytes(downloadPath));
        }
        finally
        {
            try
            {
                if (File.Exists(downloadPath))
                {
                    File.Delete(downloadPath);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// 5. 受信履歴の単件削除ができること。
    /// </summary>
    [Fact]
    public void Receive_DeleteEntry_RemovesSelectedReceive()
    {
        using var scope = HistoryTemp.Create();
        SaveSuccessfulReceive(scope.HistoryPath, "Sample1.txt");
        SaveSuccessfulReceive(scope.HistoryPath, "Sample2.txt");

        var before = LoadEntries(scope.HistoryPath);
        Assert.Equal(2, before.Count);

        var toDelete = before.Single(e => string.Equals(ReadProperty(e, "FileName")?.ToString(), "Sample1.txt", StringComparison.Ordinal));
        var deleteId = ReadProperty(toDelete, "EntryId")?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(deleteId));
        Assert.True(InvokeDeleteEntry(scope.HistoryPath, deleteId!));

        var after = LoadEntries(scope.HistoryPath);
        Assert.Single(after);
        Assert.Equal("Sample2.txt", ReadProperty(after[0], "FileName")?.ToString());
    }

    /// <summary>
    /// 6. 受信履歴をすべて削除できること。
    /// </summary>
    [Fact]
    public void Receive_DeleteAllEntries_ClearsHistory()
    {
        using var scope = HistoryTemp.Create();
        SaveSuccessfulReceive(scope.HistoryPath, "Sample1.txt");
        SaveSuccessfulReceive(scope.HistoryPath, "Sample2.txt");
        SaveSuccessfulReceive(scope.HistoryPath, "Sample3.txt");

        var before = LoadEntries(scope.HistoryPath);
        Assert.Equal(3, before.Count);
        Assert.All(before, e => Assert.Equal("Receive", ReadProperty(e, "Kind")?.ToString()));

        foreach (var entry in before)
        {
            var entryId = ReadProperty(entry, "EntryId")?.ToString();
            Assert.False(string.IsNullOrWhiteSpace(entryId));
            Assert.True(InvokeDeleteEntry(scope.HistoryPath, entryId!), $"DeleteEntry failed. entryId={entryId}");
        }

        Assert.Empty(LoadEntries(scope.HistoryPath));
    }

    private void SaveSuccessfulReceive(string historyPath, string sampleFileName)
    {
        var payload = File.ReadAllBytes(TestPaths.ResolveInputTxt(sampleFileName));
        var chunks = SplitIntoBlocks(payload, blockCount: 2);
        var fileHash = ToSha512Hex(payload);
        var sourceWav = TestPaths.ResolveOutputPath($"history_rx_{Path.GetFileNameWithoutExtension(sampleFileName)}_{Guid.NewGuid():N}.wav");

        InvokeSaveReceive(
            historyPath,
            CreateReceiveEntry(
                entryId: Guid.NewGuid().ToString("N"),
                contentHashHex: fileHash,
                sourcePath: sourceWav,
                fileName: sampleFileName,
                fileSize: payload.Length,
                blockCount: chunks.Length,
                isSuccess: true,
                outputPath: string.Empty,
                completionMessage: "受信完了",
                blocks: chunks.Select((c, i) => CreateCompleteBlock(i, c)).ToArray(),
                orphans: Array.Empty<object>(),
                receivedAtUtc: DateTime.UtcNow));
    }

    private static byte[][] SplitIntoBlocks(byte[] payload, int blockCount)
    {
        Assert.True(blockCount >= 1);
        Assert.True(payload.Length >= blockCount);

        var chunks = new byte[blockCount][];
        var baseSize = payload.Length / blockCount;
        var offset = 0;
        for (var i = 0; i < blockCount; i++)
        {
            var size = i == blockCount - 1 ? payload.Length - offset : baseSize;
            chunks[i] = payload.AsSpan(offset, size).ToArray();
            offset += size;
        }

        return chunks;
    }

    private static string ToSha512Hex(byte[] payload)
    {
        return Convert.ToHexString(SHA512.HashData(payload));
    }

    private static object CreateCompleteBlock(int blockIndex, byte[] payload)
    {
        var hash = SHA256.HashData(payload);
        return CreateBlock(
            blockIndex: blockIndex,
            blockSize: payload.Length,
            blockComplete: true,
            stateValue: StateAccepted,
            errorText: string.Empty,
            contentHash: hash,
            payload: payload);
    }

    private static object CreateErrorBlock(int blockIndex, string errorText)
    {
        return CreateBlock(
            blockIndex: blockIndex,
            blockSize: 0,
            blockComplete: false,
            stateValue: StateError,
            errorText: errorText,
            contentHash: new byte[32],
            payload: Array.Empty<byte>());
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

    private static bool InvokeCanExportPayload(object receiveEntry)
    {
        var historyServiceType = ResolveType(HistoryServiceTypeName);
        var receiveEntryType = ResolveType(ReceiveEntryTypeName);
        var method = historyServiceType.GetMethod(
            "CanExportPayload",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [receiveEntryType],
            modifiers: null);
        Assert.NotNull(method);
        var result = method!.Invoke(null, [receiveEntry]);
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
        return blocksObj!.Cast<object>().OrderBy(b => (int)(ReadProperty(b, "BlockIndex") ?? -1)).ToList();
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
            Enum.ToObject(historyKindType, 1), // Receive
            Enum.ToObject(inputDeviceType, 0), // Wav
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
        byte[] contentHash,
        byte[] payload)
    {
        var blockType = ResolveType(ReceiveBlockTypeName);
        var blockStateType = ResolveType(BlockStateTypeName);
        return Activator.CreateInstance(blockType, [
            new byte[4],
            blockIndex,
            blockSize,
            contentHash,
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
                $"history_rx_{Guid.NewGuid():N}.bin");
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
                // ignore
            }
        }
    }
}
