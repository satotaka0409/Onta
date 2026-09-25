using System.Text;

namespace Onta.History;

/// <summary>
/// Onta_history.bin（Magic=ONTAHIS1 / Version=1）のバイナリ読込・書込・マージを行います。
/// </summary>
internal static class ReceiveHistoryStore
{
    // Magic は ASCII "ONTAHIS1"（8 バイト）。FormatVersion は別フィールドの uint16。
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ONTAHIS1");
    // v2: Entry Payloadを削除。
    private const ushort FormatVersion = 1;
    private const int MaxEntryCount = 10000;
    private static readonly string HistoryLoadLogPath = Path.Combine(AppContext.BaseDirectory, "Onta_history_load.log");

    /// <summary>
    /// 履歴ファイルから最新の受信エントリを返します。
    /// </summary>
    /// <param name="filePath">履歴ファイルパス。</param>
    /// <returns>末尾側で見つかった受信エントリ。無い場合は null。</returns>
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

    /// <summary>
    /// 履歴ファイルの全エントリを読み込みます。
    /// </summary>
    /// <param name="filePath">履歴ファイルパス。</param>
    /// <returns>エントリ一覧。破損・未存在時は空。</returns>
    public static IReadOnlyList<ReceiveHistoryEntry> LoadEntries(string filePath)
    {
        return LoadAll(filePath);
    }

    /// <summary>
    /// 履歴ファイルを指定エントリ一覧で丸ごと置き換えます。
    /// </summary>
    /// <param name="filePath">履歴ファイルパス。</param>
    /// <param name="entries">書き込むエントリ一覧。</param>
    public static void ReplaceAll(string filePath, IReadOnlyList<ReceiveHistoryEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(entries);
        WriteAll(filePath, entries);
    }

    /// <summary>
    /// エントリを追記します。同一 Identity があればマージしてから書き戻します。
    /// </summary>
    /// <param name="filePath">履歴ファイルパス。</param>
    /// <param name="entry">追記またはマージするエントリ。</param>
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

        WriteAll(filePath, all);
    }

    /// <summary>
    /// 指定 EntryId のエントリを削除して書き戻します。
    /// </summary>
    /// <param name="filePath">履歴ファイルパス。</param>
    /// <param name="entryId">削除対象のエントリ ID。</param>
    /// <returns>1 件以上削除できた場合は true。</returns>
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

        WriteAll(filePath, all);

        return true;
    }

    /// <summary>
    /// 履歴バイナリを読み込み、エントリ一覧を返します。
    /// </summary>
    /// <param name="filePath">履歴ファイルパス。</param>
    /// <returns>エントリ一覧。Magic 不一致・例外時は空（ログ追記）。</returns>
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
                AppendLoadLog($"Invalid magic. file={filePath}");
                return [];
            }

            return ReadCurrentFormatEntries(reader);
        }
        catch (Exception ex)
        {
            AppendLoadLog($"Load failed. file={filePath} error={ex}");
            return [];
        }
    }

    /// <summary>
    /// Magic・Version・件数ヘッダ付きで全エントリをバイナリ書き込みします。
    /// </summary>
    /// <param name="filePath">履歴ファイルパス。</param>
    /// <param name="entries">書き込むエントリ一覧。</param>
    private static void WriteAll(string filePath, IReadOnlyList<ReceiveHistoryEntry> entries)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(filePath);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        var receiveCount = entries.Count(x => x.Kind == HistoryEntryKind.Receive);
        var sendCount = entries.Count - receiveCount;
        var uncompleteCount = entries.Count(x => !x.IsSuccess);
        writer.Write((uint)receiveCount);
        writer.Write((uint)sendCount);
        writer.Write((uint)uncompleteCount);
        foreach (var current in entries)
        {
            WriteEntry(writer, current);
        }
    }

    /// <summary>
    /// 履歴読込失敗などの診断ログを Onta_history_load.log へ追記します。
    /// </summary>
    /// <param name="message">ログ本文。</param>
    private static void AppendLoadLog(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(HistoryLoadLogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(
                HistoryLoadLogPath,
                $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // ログ書き込み失敗は処理継続。
        }
    }

    /// <summary>
    /// Version=1 形式の件数ヘッダとエントリ本体を読み取ります。
    /// </summary>
    /// <param name="reader">Magic 直後からの BinaryReader。</param>
    /// <returns>読み取ったエントリ一覧。</returns>
    /// <exception cref="InvalidDataException">Version 不一致または件数異常。</exception>
    private static List<ReceiveHistoryEntry> ReadCurrentFormatEntries(BinaryReader reader)
    {
        var version = reader.ReadUInt16();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported history version: {version}");
        }

        var receiveCount = reader.ReadUInt32();
        var sendCount = reader.ReadUInt32();
        _ = reader.ReadUInt32(); // Uncomplete EntryCount（情報用途）

        if (receiveCount > MaxEntryCount || sendCount > MaxEntryCount)
        {
            throw new InvalidDataException("History entry count is out of range.");
        }

        var totalCountLong = (long)receiveCount + sendCount;
        if (totalCountLong < 0 || totalCountLong > MaxEntryCount)
        {
            throw new InvalidDataException("History total entry count is out of range.");
        }

        var totalCount = (int)totalCountLong;
        var entries = new List<ReceiveHistoryEntry>(totalCount);
        for (var i = 0; i < totalCount; i++)
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    /// <summary>
    /// 1 エントリ（メタ・ブロック・オーファン）をバイナリへ書き出します。
    /// </summary>
    /// <param name="writer">出力先 BinaryWriter。</param>
    /// <param name="entry">書き出す履歴エントリ。</param>
    private static void WriteEntry(BinaryWriter writer, ReceiveHistoryEntry entry)
    {
        writer.Write(entry.EntryId ?? Guid.NewGuid().ToString("N"));
        writer.Write((byte)entry.Kind);
        if (entry.Kind == HistoryEntryKind.Receive)
        {
            writer.Write((byte)entry.InputDevice);
        }
        else if (entry.Kind == HistoryEntryKind.Send)
        {
            writer.Write(NormalizeDataModulation(entry.DataModulation));
        }

        writer.Write(entry.ReceivedAtUtc.ToUniversalTime().Ticks);
        writer.Write(entry.CreatedAtUtc.ToUniversalTime().Ticks);
        writer.Write(entry.UpdatedAtUtc.ToUniversalTime().Ticks);
        writer.Write(entry.ContentHashHex ?? string.Empty);
        writer.Write(entry.SourcePath ?? string.Empty);
        writer.Write(entry.FileName ?? string.Empty);
        writer.Write(entry.FileSize);
        writer.Write(entry.BlockCount);
        writer.Write(entry.IsSuccess);
        writer.Write(entry.OutputPath ?? string.Empty);
        writer.Write(entry.CompletionMessage ?? string.Empty);
        writer.Write(entry.Blocks.Count);
        foreach (var block in entry.Blocks)
        {
            var dataModulation = NormalizeDataModulation(block.DataModulation);
            writer.Write(dataModulation);
            writer.Write((uint)Math.Max(0, block.BlockIndex));
            var blockData = block.BlockComplete
                ? (block.BlockData ?? Array.Empty<byte>())
                : Array.Empty<byte>();
            var blockSize = blockData.Length;
            writer.Write((uint)blockSize);
            var contentHash = NormalizeHash32(block.ContentHash);
            writer.Write(contentHash);
            writer.Write(block.BlockComplete);
            if (blockSize > 0)
            {
                writer.Write(blockData);
            }
        }

        writer.Write(entry.Orphans.Count);
        foreach (var orphan in entry.Orphans)
        {
            writer.Write(orphan.HashHex ?? string.Empty);
            writer.Write(EncodeOrphanDetail(orphan.Detail, orphan.DataModulation));
            var orphanPayload = orphan.Payload ?? Array.Empty<byte>();
            writer.Write(orphanPayload.Length);
            writer.Write(orphanPayload);
        }
    }

    /// <summary>
    /// 1 エントリをバイナリから読み取ります。
    /// </summary>
    /// <param name="reader">エントリ先頭位置の BinaryReader。</param>
    /// <returns>復元した履歴エントリ。</returns>
    private static ReceiveHistoryEntry ReadEntry(BinaryReader reader)
    {
        var entryId = reader.ReadString();
        var kind = (HistoryEntryKind)reader.ReadByte();
        var normalizedKind = Enum.IsDefined(typeof(HistoryEntryKind), kind) ? kind : HistoryEntryKind.Receive;
        var inputDevice = ReceiveInputDevice.Wav;
        var dataModulation = new byte[4];
        if (normalizedKind == HistoryEntryKind.Receive)
        {
            var inputDeviceByte = reader.ReadByte();
            inputDevice = Enum.IsDefined(typeof(ReceiveInputDevice), inputDeviceByte)
                ? (ReceiveInputDevice)inputDeviceByte
                : ReceiveInputDevice.Wav;
        }
        else if (normalizedKind == HistoryEntryKind.Send)
        {
            var sendDataModulation = reader.ReadBytes(4);
            if (sendDataModulation.Length != 4)
            {
                throw new InvalidDataException("Invalid send entry: DataModulation is missing.");
            }

            dataModulation = sendDataModulation;
        }

        var ticks = reader.ReadInt64();
        var createdAtTicks = reader.ReadInt64();
        var updatedAtTicks = reader.ReadInt64();
        var contentHashHex = reader.ReadString();
        var sourcePath = reader.ReadString();

        var fileName = reader.ReadString();
        var fileSize = reader.ReadInt64();
        var blockCount = reader.ReadInt32();
        var isSuccess = reader.ReadBoolean();
        var outputPath = reader.ReadString();
        var completionMessage = reader.ReadString();

        var blockItemCount = reader.ReadInt32();
        if (blockItemCount < 0 || blockItemCount > 100000)
        {
            blockItemCount = 0;
        }

        var blocks = ReadCurrentBlocks(reader, blockItemCount);

        var orphanCount = reader.ReadInt32();
        if (orphanCount < 0 || orphanCount > 100000)
        {
            orphanCount = 0;
        }

        var orphans = new List<ReceiveOrphanHistory>(orphanCount);
        for (var i = 0; i < orphanCount; i++)
        {
            var hashHex = reader.ReadString();
            var storedDetail = reader.ReadString();
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

            DecodeOrphanDetail(storedDetail, out var detail, out var orphanDataModulation);
            orphans.Add(new ReceiveOrphanHistory(hashHex, detail, orphanPayload, orphanDataModulation));
        }

        return new ReceiveHistoryEntry(
            EntryId: string.IsNullOrWhiteSpace(entryId) ? Guid.NewGuid().ToString("N") : entryId,
            Kind: normalizedKind,
            InputDevice: inputDevice,
            DataModulation: dataModulation,
            ReceivedAtUtc: new DateTime(ticks, DateTimeKind.Utc),
            CreatedAtUtc: new DateTime(createdAtTicks, DateTimeKind.Utc),
            UpdatedAtUtc: new DateTime(updatedAtTicks, DateTimeKind.Utc),
            ContentHashHex: contentHashHex,
            SourcePath: sourcePath,
            FileName: fileName,
            FileSize: fileSize,
            BlockCount: blockCount,
            IsSuccess: isSuccess,
            OutputPath: outputPath,
            CompletionMessage: completionMessage,
            Blocks: blocks,
            Orphans: orphans);
    }

    /// <summary>
    /// Version=1 形式の受信ブロック列を読み取ります。
    /// </summary>
    /// <param name="reader">ブロック列先頭の BinaryReader。</param>
    /// <param name="blockItemCount">読み取るブロック件数。</param>
    /// <returns>受信ブロック一覧。</returns>
    private static List<ReceiveBlockHistory> ReadCurrentBlocks(BinaryReader reader, int blockItemCount)
    {
        var blocks = new List<ReceiveBlockHistory>(blockItemCount);
        for (var i = 0; i < blockItemCount; i++)
        {
            var dataModulation = reader.ReadBytes(4);
            if (dataModulation.Length != 4)
            {
                throw new InvalidDataException("Invalid receive block: modulation bytes are missing.");
            }

            var blockIndexU = reader.ReadUInt32();
            if (blockIndexU > int.MaxValue)
            {
                throw new InvalidDataException("Invalid receive block: block index is out of range.");
            }

            var blockSizeU = reader.ReadUInt32();
            if (blockSizeU > int.MaxValue)
            {
                throw new InvalidDataException("Invalid receive block: block size is out of range.");
            }

            var contentHash = reader.ReadBytes(32);
            if (contentHash.Length != 32)
            {
                throw new InvalidDataException("Invalid receive block: content hash is missing.");
            }

            var blockComplete = reader.ReadBoolean();
            var blockSize = blockComplete ? (int)blockSizeU : 0;
            var blockData = blockComplete && blockSize > 0
                ? reader.ReadBytes(blockSize)
                : Array.Empty<byte>();
            if (blockComplete && blockData.Length != blockSize)
            {
                throw new InvalidDataException("Invalid receive block: block data is truncated.");
            }

            blocks.Add(new ReceiveBlockHistory(
                DataModulation: dataModulation,
                BlockIndex: (int)blockIndexU,
                BlockSize: blockSize,
                ContentHash: contentHash,
                BlockComplete: blockComplete,
                BlockData: blockData,
                State: blockComplete ? ReceiveBlockState.Accepted : ReceiveBlockState.Unknown,
                ErrorText: string.Empty));
        }

        return blocks;
    }

    /// <summary>
    /// DataModulation を長さ 4 に正規化します（不足は 0 埋め）。
    /// </summary>
    /// <param name="source">元バイト列。null 可。</param>
    /// <returns>長さ 4 のバイト列。</returns>
    private static byte[] NormalizeDataModulation(byte[]? source)
    {
        var normalized = new byte[4];
        if (source is { Length: > 0 })
        {
            Buffer.BlockCopy(source, 0, normalized, 0, Math.Min(4, source.Length));
        }

        return normalized;
    }

    /// <summary>
    /// ブロック ContentHash を長さ 32 に正規化します（不足は 0 埋め）。
    /// </summary>
    /// <param name="source">元ハッシュ。null 可。</param>
    /// <returns>長さ 32 のバイト列。</returns>
    private static byte[] NormalizeHash32(byte[]? source)
    {
        var normalized = new byte[32];
        if (source is { Length: > 0 })
        {
            Buffer.BlockCopy(source, 0, normalized, 0, Math.Min(32, source.Length));
        }

        return normalized;
    }

    /// <summary>
    /// 既存エントリと新規エントリが同一 Identity か判定します（ハッシュ優先、受信はパス＋ファイル名）。
    /// </summary>
    /// <param name="existing">既存エントリ。</param>
    /// <param name="incoming">新規エントリ。</param>
    /// <returns>同一とみなす場合は true。</returns>
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

    /// <summary>
    /// 同一 Identity の既存エントリへ新規内容をマージします（日時・ブロック・オーファン含む）。
    /// </summary>
    /// <param name="existing">既存エントリ。</param>
    /// <param name="incoming">マージ元の新規エントリ。</param>
    /// <returns>マージ後のエントリ。</returns>
    private static ReceiveHistoryEntry MergeEntry(ReceiveHistoryEntry existing, ReceiveHistoryEntry incoming)
    {
        var latest = incoming.ReceivedAtUtc >= existing.ReceivedAtUtc ? incoming.ReceivedAtUtc : existing.ReceivedAtUtc;
        var orphans = incoming.Orphans.Count > 0 ? incoming.Orphans : existing.Orphans;
        var blocks = MergeBlocks(existing.Blocks, incoming.Blocks);

        return existing with
        {
            ReceivedAtUtc = latest,
            DataModulation = incoming.DataModulation is { Length: > 0 }
                ? NormalizeDataModulation(incoming.DataModulation)
                : existing.DataModulation,
            CreatedAtUtc = incoming.CreatedAtUtc > DateTime.MinValue ? incoming.CreatedAtUtc : existing.CreatedAtUtc,
            UpdatedAtUtc = incoming.UpdatedAtUtc > DateTime.MinValue ? incoming.UpdatedAtUtc : existing.UpdatedAtUtc,
            ContentHashHex = string.IsNullOrWhiteSpace(incoming.ContentHashHex) ? existing.ContentHashHex : incoming.ContentHashHex,
            SourcePath = string.IsNullOrWhiteSpace(incoming.SourcePath) ? existing.SourcePath : incoming.SourcePath,
            FileName = string.IsNullOrWhiteSpace(incoming.FileName) ? existing.FileName : incoming.FileName,
            FileSize = incoming.FileSize > 0 ? incoming.FileSize : existing.FileSize,
            BlockCount = incoming.BlockCount > 0 ? incoming.BlockCount : existing.BlockCount,
            IsSuccess = incoming.IsSuccess || existing.IsSuccess,
            OutputPath = string.IsNullOrWhiteSpace(incoming.OutputPath) ? existing.OutputPath : incoming.OutputPath,
            CompletionMessage = string.IsNullOrWhiteSpace(incoming.CompletionMessage) ? existing.CompletionMessage : incoming.CompletionMessage,
            Blocks = blocks,
            Orphans = orphans
        };
    }

    /// <summary>
    /// ブロックを BlockIndex キーで統合します。既存が完了済みのとき、未完了の新規では上書きしません。
    /// </summary>
    /// <param name="existing">既存ブロック一覧。</param>
    /// <param name="incoming">新規ブロック一覧。</param>
    /// <returns>統合後のブロック一覧（Index 昇順）。</returns>
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

            if (prior.BlockComplete && !block.BlockComplete)
            {
                continue;
            }

            if (block.BlockComplete && !prior.BlockComplete)
            {
                map[block.BlockIndex] = block;
                continue;
            }

            if (block.BlockComplete && prior.BlockComplete)
            {
                if (block.BlockData.Length > prior.BlockData.Length)
                {
                    map[block.BlockIndex] = block;
                }

                continue;
            }

            if (!prior.BlockComplete
                && !block.BlockComplete
                && string.IsNullOrWhiteSpace(prior.ErrorText)
                && !string.IsNullOrWhiteSpace(block.ErrorText))
            {
                map[block.BlockIndex] = block;
            }
        }

        return map.Values.OrderBy(x => x.BlockIndex).ToArray();
    }

    /// <summary>
    /// Orphan の DataModulation を Detail 先頭に埋め込みます（履歴 Version=1 互換）。
    /// </summary>
    /// <param name="detail">表示用 Detail 文字列。</param>
    /// <param name="dataModulation">埋め込む変調 4 バイト。不足時は Detail のみ返す。</param>
    /// <returns>#OM#&lt;hex&gt;#&lt;detail&gt; 形式、または元 Detail。</returns>
    private static string EncodeOrphanDetail(string? detail, byte[]? dataModulation)
    {
        var text = detail ?? string.Empty;
        if (dataModulation is not { Length: >= 3 })
        {
            return text;
        }

        var hex = Convert.ToHexString(dataModulation.AsSpan(0, Math.Min(4, dataModulation.Length)));
        return string.Concat("#OM#", hex, "#", text);
    }

    /// <summary>
    /// Detail 先頭の DataModulation メタを分離します。
    /// </summary>
    /// <param name="storedDetail">履歴に保存された Detail（#OM# 付き可）。</param>
    /// <param name="detail">メタ除去後の Detail。</param>
    /// <param name="dataModulation">復元した変調バイト。無い場合は空配列。</param>
    private static void DecodeOrphanDetail(string? storedDetail, out string detail, out byte[] dataModulation)
    {
        detail = storedDetail ?? string.Empty;
        dataModulation = Array.Empty<byte>();
        if (!detail.StartsWith("#OM#", StringComparison.Ordinal))
        {
            return;
        }

        var end = detail.IndexOf('#', 4);
        if (end <= 4)
        {
            return;
        }

        var hex = detail[4..end];
        if (hex.Length < 6 || (hex.Length % 2) != 0)
        {
            return;
        }

        try
        {
            dataModulation = Convert.FromHexString(hex);
            detail = end + 1 < detail.Length ? detail[(end + 1)..] : string.Empty;
        }
        catch (FormatException)
        {
            dataModulation = Array.Empty<byte>();
        }
    }
}
