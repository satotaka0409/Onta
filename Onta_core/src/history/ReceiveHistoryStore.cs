using System.Text;

namespace Onta.History;

internal static class ReceiveHistoryStore
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ONTAHIS1");
    // 履歴フォーマット版数は当面 v1 固定。互換性に影響するため勝手に上げない。
    private const int FormatVersion = 1;

    public static ReceiveHistoryEntry? TryLoadLatestReceive(string filePath)
    {
        var all = LoadAll(filePath);
        for (var i = all.Count - 1; i >= 0; i--)
        {
            if (all[i].Kind == HistoryEntryKind.Receive)
            {
                return all[i];
            }
        }

        return null;
    }

    public static IReadOnlyList<ReceiveHistoryEntry> LoadEntries(string filePath)
    {
        return LoadAll(filePath);
    }

    public static void Append(string filePath, ReceiveHistoryEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(entry);

        var all = LoadAll(filePath);
        var updated = false;
        for (var i = 0; i < all.Count; i++)
        {
            if (!IsSameIdentity(all[i], entry))
            {
                continue;
            }

            all[i] = MergeEntry(all[i], entry);
            updated = true;
            break;
        }

        if (!updated)
        {
            all.Add(entry);
        }

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(filePath);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(all.Count);
        foreach (var current in all)
        {
            WriteEntry(writer, current);
        }
    }

    public static bool DeleteEntry(string filePath, string entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);

        var all = LoadAll(filePath);
        var removed = all.RemoveAll(x => string.Equals(x.EntryId, entryId, StringComparison.Ordinal)) > 0;
        if (!removed)
        {
            return false;
        }

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(filePath);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(all.Count);
        foreach (var current in all)
        {
            WriteEntry(writer, current);
        }

        return true;
    }

    private static List<ReceiveHistoryEntry> LoadAll(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            var magic = reader.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length || !magic.SequenceEqual(Magic))
            {
                return [];
            }

            var version = reader.ReadInt32();
            if (version != FormatVersion)
            {
                return [];
            }

            var count = reader.ReadInt32();
            if (count < 0 || count > 10000)
            {
                return [];
            }

            var entries = new List<ReceiveHistoryEntry>(count);
            for (var i = 0; i < count; i++)
            {
                entries.Add(ReadEntry(reader));
            }

            return entries;
        }
        catch
        {
            return [];
        }
    }

    private static void WriteEntry(BinaryWriter writer, ReceiveHistoryEntry entry)
    {
        writer.Write(entry.EntryId ?? Guid.NewGuid().ToString("N"));
        writer.Write((byte)entry.Kind);
        writer.Write(entry.ReceivedAtUtc.ToUniversalTime().Ticks);
        writer.Write(entry.ContentHashHex ?? string.Empty);
        writer.Write(entry.SourcePath ?? string.Empty);
        writer.Write(entry.FileName ?? string.Empty);
        writer.Write(entry.FileSize);
        writer.Write(entry.BlockCount);
        writer.Write(entry.IsSuccess);
        writer.Write(entry.OutputPath ?? string.Empty);
        writer.Write(entry.CompletionMessage ?? string.Empty);
        var payload = entry.Payload ?? Array.Empty<byte>();
        writer.Write(payload.Length);
        writer.Write(payload);
        writer.Write(entry.Blocks.Count);
        foreach (var block in entry.Blocks)
        {
            writer.Write(block.BlockIndex);
            writer.Write((byte)block.State);
            writer.Write(block.ErrorText ?? string.Empty);
        }

        writer.Write(entry.Orphans.Count);
        foreach (var orphan in entry.Orphans)
        {
            writer.Write(orphan.HashHex ?? string.Empty);
            writer.Write(orphan.Detail ?? string.Empty);
            var orphanPayload = orphan.Payload ?? Array.Empty<byte>();
            writer.Write(orphanPayload.Length);
            writer.Write(orphanPayload);
        }
    }

    private static ReceiveHistoryEntry ReadEntry(BinaryReader reader)
    {
        var entryId = reader.ReadString();
        var kind = (HistoryEntryKind)reader.ReadByte();
        var ticks = reader.ReadInt64();
        var contentHashHex = reader.ReadString();
        var sourcePath = reader.ReadString();
        var fileName = reader.ReadString();
        var fileSize = reader.ReadInt64();
        var blockCount = reader.ReadInt32();
        var isSuccess = reader.ReadBoolean();
        var outputPath = reader.ReadString();
        var completionMessage = reader.ReadString();
        var payloadLength = reader.ReadInt32();
        if (payloadLength < 0 || payloadLength > (512 * 1024 * 1024))
        {
            payloadLength = 0;
        }

        var payload = reader.ReadBytes(payloadLength);
        if (payload.Length != payloadLength)
        {
            payload = Array.Empty<byte>();
        }

        var blockItemCount = reader.ReadInt32();
        if (blockItemCount < 0 || blockItemCount > 100000)
        {
            blockItemCount = 0;
        }

        var blocks = new List<ReceiveBlockHistory>(blockItemCount);
        for (var i = 0; i < blockItemCount; i++)
        {
            var blockIndex = reader.ReadInt32();
            var stateByte = reader.ReadByte();
            var errorText = reader.ReadString();
            var state = Enum.IsDefined(typeof(ReceiveBlockState), stateByte)
                ? (ReceiveBlockState)stateByte
                : ReceiveBlockState.Unknown;
            blocks.Add(new ReceiveBlockHistory(blockIndex, state, errorText));
        }

        var orphanCount = reader.ReadInt32();
        if (orphanCount < 0 || orphanCount > 100000)
        {
            orphanCount = 0;
        }

        var orphans = new List<ReceiveOrphanHistory>(orphanCount);
        for (var i = 0; i < orphanCount; i++)
        {
            var hashHex = reader.ReadString();
            var detail = reader.ReadString();
            var orphanPayloadLength = reader.ReadInt32();
            if (orphanPayloadLength < 0 || orphanPayloadLength > (32 * 1024 * 1024))
            {
                orphanPayloadLength = 0;
            }

            var orphanPayload = reader.ReadBytes(orphanPayloadLength);
            if (orphanPayload.Length != orphanPayloadLength)
            {
                orphanPayload = Array.Empty<byte>();
            }

            orphans.Add(new ReceiveOrphanHistory(hashHex, detail, orphanPayload));
        }

        return new ReceiveHistoryEntry(
            EntryId: string.IsNullOrWhiteSpace(entryId) ? Guid.NewGuid().ToString("N") : entryId,
            Kind: Enum.IsDefined(typeof(HistoryEntryKind), kind) ? kind : HistoryEntryKind.Receive,
            ReceivedAtUtc: new DateTime(ticks, DateTimeKind.Utc),
            ContentHashHex: contentHashHex,
            SourcePath: sourcePath,
            FileName: fileName,
            FileSize: fileSize,
            BlockCount: blockCount,
            IsSuccess: isSuccess,
            OutputPath: outputPath,
            CompletionMessage: completionMessage,
            Payload: payload,
            Blocks: blocks,
            Orphans: orphans);
    }

    private static bool IsSameIdentity(ReceiveHistoryEntry existing, ReceiveHistoryEntry incoming)
    {
        if (existing.Kind != incoming.Kind)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(existing.ContentHashHex)
            && !string.IsNullOrWhiteSpace(incoming.ContentHashHex)
            && string.Equals(existing.ContentHashHex, incoming.ContentHashHex, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (incoming.Kind == HistoryEntryKind.Receive)
        {
            return string.Equals(existing.SourcePath, incoming.SourcePath, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(existing.FileName, incoming.FileName, StringComparison.Ordinal);
        }

        return false;
    }

    private static ReceiveHistoryEntry MergeEntry(ReceiveHistoryEntry existing, ReceiveHistoryEntry incoming)
    {
        var latest = incoming.ReceivedAtUtc >= existing.ReceivedAtUtc ? incoming.ReceivedAtUtc : existing.ReceivedAtUtc;
        var payload = incoming.Payload.Length > 0 ? incoming.Payload : existing.Payload;
        var orphans = incoming.Orphans.Count > 0 ? incoming.Orphans : existing.Orphans;
        var blocks = MergeBlocks(existing.Blocks, incoming.Blocks);

        return existing with
        {
            ReceivedAtUtc = latest,
            ContentHashHex = string.IsNullOrWhiteSpace(incoming.ContentHashHex) ? existing.ContentHashHex : incoming.ContentHashHex,
            SourcePath = string.IsNullOrWhiteSpace(incoming.SourcePath) ? existing.SourcePath : incoming.SourcePath,
            FileName = string.IsNullOrWhiteSpace(incoming.FileName) ? existing.FileName : incoming.FileName,
            FileSize = incoming.FileSize > 0 ? incoming.FileSize : existing.FileSize,
            BlockCount = incoming.BlockCount > 0 ? incoming.BlockCount : existing.BlockCount,
            IsSuccess = incoming.IsSuccess || existing.IsSuccess,
            OutputPath = string.IsNullOrWhiteSpace(incoming.OutputPath) ? existing.OutputPath : incoming.OutputPath,
            CompletionMessage = string.IsNullOrWhiteSpace(incoming.CompletionMessage) ? existing.CompletionMessage : incoming.CompletionMessage,
            Payload = payload,
            Blocks = blocks,
            Orphans = orphans
        };
    }

    private static IReadOnlyList<ReceiveBlockHistory> MergeBlocks(
        IReadOnlyList<ReceiveBlockHistory> existing,
        IReadOnlyList<ReceiveBlockHistory> incoming)
    {
        if (existing.Count == 0)
        {
            return incoming;
        }

        if (incoming.Count == 0)
        {
            return existing;
        }

        var map = new Dictionary<int, ReceiveBlockHistory>();
        foreach (var block in existing)
        {
            map[block.BlockIndex] = block;
        }

        foreach (var block in incoming)
        {
            if (!map.TryGetValue(block.BlockIndex, out var prior))
            {
                map[block.BlockIndex] = block;
                continue;
            }

            if (prior.State == ReceiveBlockState.Accepted && block.State != ReceiveBlockState.Accepted)
            {
                continue;
            }

            if (block.State == ReceiveBlockState.Accepted || prior.State == ReceiveBlockState.Unknown)
            {
                map[block.BlockIndex] = block;
                continue;
            }

            if (prior.State == ReceiveBlockState.Error
                && string.IsNullOrWhiteSpace(prior.ErrorText)
                && !string.IsNullOrWhiteSpace(block.ErrorText))
            {
                map[block.BlockIndex] = block;
            }
        }

        return map.Values.OrderBy(x => x.BlockIndex).ToArray();
    }
}

