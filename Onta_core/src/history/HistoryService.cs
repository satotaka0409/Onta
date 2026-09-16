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
        SaveSend(
            historyFilePath,
            inputPath,
            outputWavPath,
            activeSubcarriers: 8,
            modulationScheme: ModulationScheme.Bpsk,
            channelMode: ChannelMode.Mono,
            completionMessage);
    }

    public static void SaveSend(
        string historyFilePath,
        string inputPath,
        string outputWavPath,
        int activeSubcarriers,
        ModulationScheme modulationScheme,
        ChannelMode channelMode,
        string completionMessage = "送信完了")
    {
        var fileName = Path.GetFileName(inputPath);
        long fileSize = 0;
        var contentHashHex = string.Empty;
        var createdAtUtc = DateTime.UtcNow;
        var updatedAtUtc = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(inputPath) && File.Exists(inputPath))
        {
            var fi = new FileInfo(inputPath);
            fileSize = fi.Length;
            contentHashHex = Convert.ToHexString(Hash.ComputeSha256(File.ReadAllBytes(inputPath)));
            createdAtUtc = fi.CreationTimeUtc;
            updatedAtUtc = fi.LastWriteTimeUtc;
        }

        var entry = new ReceiveHistoryEntry(
            EntryId: Guid.NewGuid().ToString("N"),
            Kind: HistoryEntryKind.Send,
            InputDevice: ReceiveInputDevice.Wav,
            DataModulation: BuildDataModulation(activeSubcarriers, modulationScheme, channelMode),
            ReceivedAtUtc: DateTime.UtcNow,
            CreatedAtUtc: createdAtUtc,
            UpdatedAtUtc: updatedAtUtc,
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

    private static byte[] BuildDataModulation(
        int activeSubcarriers,
        ModulationScheme modulationScheme,
        ChannelMode channelMode)
    {
        var subcarriers = activeSubcarriers is 8 or 16 or 24 or 32 or 40 or 48
            ? (byte)activeSubcarriers
            : (byte)0;
        var modulation = modulationScheme switch
        {
            ModulationScheme.Bpsk => (byte)1,
            ModulationScheme.Qpsk => (byte)2,
            ModulationScheme.Qam16 => (byte)3,
            ModulationScheme.Qam64 => (byte)4,
            _ => (byte)0
        };
        var channel = channelMode switch
        {
            ChannelMode.Mono => (byte)0,
            ChannelMode.Stereo => (byte)1,
            _ => (byte)0
        };

        return [subcarriers, modulation, channel, 0];
    }
}

