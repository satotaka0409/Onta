using Onta.Core;

namespace Onta.History;

internal static class HistoryService
{
    public static ReceiveHistoryEntry? TryLoadLatestReceive(string historyFilePath)
    {
        return ReceiveHistoryStore.TryLoadLatestReceive(historyFilePath);
    }

    public static IReadOnlyList<ReceiveHistoryEntry> LoadEntries(string historyFilePath)
    {
        return ReceiveHistoryStore.LoadEntries(historyFilePath);
    }

    public static bool DeleteEntry(string historyFilePath, string entryId)
    {
        return ReceiveHistoryStore.DeleteEntry(historyFilePath, entryId);
    }

    public static bool ExportPayloadToFile(ReceiveHistoryEntry entry, string targetPath)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (entry.Payload.Length == 0)
        {
            return false;
        }

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(targetPath, entry.Payload);
        return true;
    }

    public static void SaveReceive(string historyFilePath, ReceiveHistoryEntry entry)
    {
        ReceiveHistoryStore.Append(historyFilePath, entry);
    }

    public static void SaveSend(
        string historyFilePath,
        string inputPath,
        string outputWavPath,
        string completionMessage = "送信完了")
    {
        var fileName = Path.GetFileName(inputPath);
        long fileSize = 0;
        var contentHashHex = string.Empty;
        if (!string.IsNullOrWhiteSpace(inputPath) && File.Exists(inputPath))
        {
            fileSize = new FileInfo(inputPath).Length;
            contentHashHex = Convert.ToHexString(Hash.ComputeSha256(File.ReadAllBytes(inputPath)));
        }

        var entry = new ReceiveHistoryEntry(
            EntryId: Guid.NewGuid().ToString("N"),
            Kind: HistoryEntryKind.Send,
            ReceivedAtUtc: DateTime.UtcNow,
            ContentHashHex: contentHashHex,
            SourcePath: inputPath ?? string.Empty,
            FileName: string.IsNullOrWhiteSpace(fileName) ? "(不明)" : fileName,
            FileSize: fileSize,
            BlockCount: 0,
            IsSuccess: true,
            OutputPath: outputWavPath ?? string.Empty,
            CompletionMessage: string.IsNullOrWhiteSpace(completionMessage) ? "送信完了" : completionMessage,
            Payload: Array.Empty<byte>(),
            Blocks: Array.Empty<ReceiveBlockHistory>(),
            Orphans: Array.Empty<ReceiveOrphanHistory>());

        ReceiveHistoryStore.Append(historyFilePath, entry);
    }
}

