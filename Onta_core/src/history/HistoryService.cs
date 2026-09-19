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
        if (!TryBuildPayloadFromHistory(entry, out var payload))
        {
            return false;
        }

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(targetPath, payload);
        return true;
    }

    public static bool CanExportPayload(ReceiveHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return TryBuildPayloadFromHistory(entry, out _);
    }

    private static bool TryBuildPayloadFromHistory(ReceiveHistoryEntry entry, out byte[] payload)
    {
        payload = Array.Empty<byte>();

        if (!entry.IsSuccess)
        {
            return false;
        }

        var completeBlocks = entry.Blocks
            .Where(b => b.BlockComplete && b.BlockIndex >= 0)
            .GroupBy(b => b.BlockIndex)
            .Select(g => g.OrderByDescending(x => x.BlockData.Length).First())
            .OrderBy(b => b.BlockIndex)
            .ToArray();

        if (completeBlocks.Length == 0)
        {
            return false;
        }

        if (entry.BlockCount > 0)
        {
            if (completeBlocks.Length < entry.BlockCount)
            {
                return false;
            }

            for (var i = 0; i < entry.BlockCount; i++)
            {
                if (completeBlocks[i].BlockIndex != i)
                {
                    return false;
                }
            }
        }

        var total = completeBlocks.Sum(b => Math.Max(0, b.BlockData.Length));
        if (total <= 0)
        {
            return false;
        }

        var merged = new byte[total];
        var offset = 0;
        foreach (var block in completeBlocks)
        {
            if (block.BlockData.Length <= 0)
            {
                continue;
            }

            Buffer.BlockCopy(block.BlockData, 0, merged, offset, block.BlockData.Length);
            offset += block.BlockData.Length;
        }

        if (offset == 0)
        {
            return false;
        }

        if (entry.FileSize > 0)
        {
            if (offset < entry.FileSize)
            {
                return false;
            }

            if (offset != entry.FileSize)
            {
                Array.Resize(ref merged, (int)entry.FileSize);
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.ContentHashHex)
            && !entry.ContentHashHex.StartsWith("FH:", StringComparison.Ordinal))
        {
            // 受信は SHA-512（128 hex）、送信履歴は SHA-256（64 hex）を許容する。
            var computed = entry.ContentHashHex.Trim().Length >= 128
                ? Convert.ToHexString(Hash.ComputeSha512(merged))
                : Convert.ToHexString(Hash.ComputeSha256(merged));
            if (!string.Equals(computed, entry.ContentHashHex, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        payload = merged;
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
        var createdAtLocal = DateTime.Now;
        var updatedAtLocal = DateTime.Now;
        if (!string.IsNullOrWhiteSpace(inputPath) && File.Exists(inputPath))
        {
            var fi = new FileInfo(inputPath);
            fileSize = fi.Length;
            contentHashHex = Convert.ToHexString(Hash.ComputeSha256(File.ReadAllBytes(inputPath)));
            createdAtLocal = fi.CreationTime;
            updatedAtLocal = fi.LastWriteTime;
        }

        var entry = new ReceiveHistoryEntry(
            EntryId: Guid.NewGuid().ToString("N"),
            Kind: HistoryEntryKind.Send,
            InputDevice: ReceiveInputDevice.Wav,
            DataModulation: BuildDataModulation(activeSubcarriers, modulationScheme, channelMode),
            ReceivedAtUtc: DateTime.Now,
            CreatedAtUtc: createdAtLocal,
            UpdatedAtUtc: updatedAtLocal,
            ContentHashHex: contentHashHex,
            SourcePath: inputPath ?? string.Empty,
            FileName: string.IsNullOrWhiteSpace(fileName) ? "(不明)" : fileName,
            FileSize: fileSize,
            BlockCount: 0,
            IsSuccess: true,
            OutputPath: outputWavPath ?? string.Empty,
            CompletionMessage: string.IsNullOrWhiteSpace(completionMessage) ? "送信完了" : completionMessage,
            Blocks: Array.Empty<ReceiveBlockHistory>(),
            Orphans: Array.Empty<ReceiveOrphanHistory>());

        ReceiveHistoryStore.Append(historyFilePath, entry);
    }

    private static byte[] BuildDataModulation(
        int activeSubcarriers,
        ModulationScheme modulationScheme,
        ChannelMode channelMode)
    {
        var subcarriers = OfdmConfig.IsSupportedActiveSubcarriers(activeSubcarriers)
            ? (byte)activeSubcarriers
            : (byte)0;
        var modulation = modulationScheme switch
        {
            ModulationScheme.Bpsk => (byte)1,
            ModulationScheme.Qpsk => (byte)2,
            ModulationScheme.Qam16 => (byte)3,
            ModulationScheme.Qam64 => (byte)4,
            ModulationScheme.Qam256 => (byte)5,
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

