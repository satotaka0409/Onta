using System.Text;

namespace Onta.History;

/// <summary>
/// Onta_history.bin（Magic=ONTAHIS1 / Version=1）のバイナリ読込・書込・マージを行います。
/// </summary>
internal static class ReceiveHistoryStore
{
    // Magic は ASCII "ONTAHIS1"（8 バイト）。FormatVersion は別フィールドの uint16。
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ONTAHIS1");
    private const ushort FormatVersion = 1;
    private const int MaxEntryCount = 10000;
    private const int MaxOrphanCount = 100000;
    private static readonly string HistoryLoadLogPath = Path.Combine(AppContext.BaseDirectory, "Onta_history_load.log");

    /// <summary>
    /// 履歴ファイルの中身（送受信エントリと、親のいない不明ブロック）です。
    /// </summary>
    /// <param name="Entries">送受信エントリ。</param>
    /// <param name="Orphans">不明ブロック（未完了エントリ）。</param>
    private sealed record HistoryDocument(List<ReceiveHistoryEntry> Entries, List<ReceiveOrphanHistory> Orphans);

    /// <summary>
    /// 履歴ファイルから最新の受信エントリを返します。
    /// </summary>
    /// <param name="filePath">履歴ファイルパス。</param>
    /// <returns>末尾側で見つかった受信エントリ。無い場合は null。</returns>
    public static ReceiveHistoryEntry? TryLoadLatestReceive(string filePath)
    {
        var all = LoadAll(filePath).Entries;
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
        return LoadAll(filePath).Entries;
    }

    /// <summary>
    /// 履歴ファイルの不明ブロック（親のいない未完了エントリ）を読み込みます。
    /// </summary>
    /// <param name="filePath">履歴ファイルパス。</param>
    /// <returns>不明ブロック一覧。破損・未存在時は空。</returns>
    public static IReadOnlyList<ReceiveOrphanHistory> LoadOrphans(string filePath)
    {
        return LoadAll(filePath).Orphans;
    }

    /// <summary>
    /// 履歴ファイルのエントリを指定一覧で丸ごと置き換えます。不明ブロックはそのまま残します。
    /// </summary>
    /// <param name="filePath">履歴ファイルパス。</param>
    /// <param name="entries">書き込むエントリ一覧。</param>
    public static void ReplaceAll(string filePath, IReadOnlyList<ReceiveHistoryEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(entries);
        WriteAll(filePath, entries, LoadAll(filePath).Orphans);
    }

    /// <summary>
    /// エントリを追記します。同一 Identity があればマージしてから書き戻します。
    /// </summary>
    /// <param name="filePath">履歴ファイルパス。</param>
    /// <param name="entry">追記またはマージするエントリ。</param>
    public static void Append(string filePath, ReceiveHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Save(filePath, entry, []);
    }

    /// <summary>
    /// 受信エントリと不明ブロックを保存します。エントリは同一 Identity があればマージし、
    /// 不明ブロックは既存に蓄積したうえで、ファイルハッシュが一致する受信エントリ（親）があれば取り込んで不明ブロックから消します。
    /// </summary>
    /// <param name="filePath">履歴ファイルパス。</param>
    /// <param name="entry">追記またはマージするエントリ。親のいない受信だけのときは null。</param>
    /// <param name="orphans">今回の受信で得た不明ブロック。</param>
    public static void Save(string filePath, ReceiveHistoryEntry? entry, IReadOnlyList<ReceiveOrphanHistory> orphans)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(orphans);

        var document = LoadAll(filePath);
        var entries = document.Entries;
        if (entry is not null)
        {
            var index = entries.FindIndex(existing => IsSameIdentity(existing, entry));
            if (index >= 0)
            {
                entries[index] = MergeEntry(entries[index], entry);
            }
            else
            {
                entries.Add(entry);
            }
        }

        var merged = MergeOrphans(document.Orphans, orphans);
        var remaining = AdoptOrphansIntoParents(entries, merged);
        WriteAll(filePath, entries, remaining);
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

        var document = LoadAll(filePath);
        var removed = document.Entries.RemoveAll(x => string.Equals(x.EntryId, entryId, StringComparison.Ordinal)) > 0;
        if (!removed)
        {
            return false;
        }

        WriteAll(filePath, document.Entries, document.Orphans);
        return true;
    }

    /// <summary>
    /// 指定識別子の不明ブロックを削除して書き戻します。
    /// </summary>
    /// <param name="filePath">履歴ファイルパス。</param>
    /// <param name="hashHex">不明ブロックの識別子（ブロック位置:ファイルハッシュ:ブロックハッシュ）。</param>
    /// <returns>削除できた場合は true。</returns>
    public static bool DeleteOrphan(string filePath, string hashHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hashHex);

        var document = LoadAll(filePath);
        var removed = document.Orphans.RemoveAll(x => string.Equals(x.HashHex, hashHex, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
        {
            return false;
        }

        WriteAll(filePath, document.Entries, document.Orphans);
        return true;
    }

    /// <summary>
    /// 履歴バイナリを読み込みます。
    /// </summary>
    /// <param name="filePath">履歴ファイルパス。</param>
    /// <returns>エントリと不明ブロック。Magic 不一致・例外時は空（ログ追記）。</returns>
    private static HistoryDocument LoadAll(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new HistoryDocument([], []);
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            var magic = reader.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length || !magic.SequenceEqual(Magic))
            {
                AppendLoadLog($"Invalid magic. file={filePath}");
                return new HistoryDocument([], []);
            }

            return ReadCurrentFormat(reader);
        }
        catch (Exception ex)
        {
            AppendLoadLog($"Load failed. file={filePath} error={ex}");
            return new HistoryDocument([], []);
        }
    }

    /// <summary>
    /// Magic・Version・件数ヘッダ付きで、送受信エントリと不明ブロック（未完了エントリ）をバイナリ書き込みします。
    /// </summary>
    /// <param name="filePath">履歴ファイルパス。</param>
    /// <param name="entries">書き込むエントリ一覧。</param>
    /// <param name="orphans">書き込む不明ブロック一覧。識別子が不正なものは書かない。</param>
    private static void WriteAll(
        string filePath,
        IReadOnlyList<ReceiveHistoryEntry> entries,
        IReadOnlyList<ReceiveOrphanHistory> orphans)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var writable = orphans.Where(orphan => TryGetOrphanKey(orphan, out _, out _, out _)).ToArray();
        using var stream = File.Create(filePath);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        var receiveCount = entries.Count(x => x.Kind == HistoryEntryKind.Receive);
        var sendCount = entries.Count - receiveCount;
        writer.Write((uint)receiveCount);
        writer.Write((uint)sendCount);
        writer.Write((uint)writable.Length);
        foreach (var current in entries)
        {
            WriteEntry(writer, current);
        }

        foreach (var orphan in writable)
        {
            WriteOrphan(writer, orphan);
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
    /// Version=1 形式の件数ヘッダ、送受信エントリ、不明ブロック（未完了エントリ）を読み取ります。
    /// </summary>
    /// <param name="reader">Magic 直後からの BinaryReader。</param>
    /// <returns>読み取ったエントリと不明ブロック。</returns>
    /// <exception cref="InvalidDataException">Version 不一致または件数異常。</exception>
    private static HistoryDocument ReadCurrentFormat(BinaryReader reader)
    {
        var version = reader.ReadUInt16();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported history version: {version}");
        }

        var receiveCount = reader.ReadUInt32();
        var sendCount = reader.ReadUInt32();
        var uncompleteCount = reader.ReadUInt32();

        if (receiveCount > MaxEntryCount || sendCount > MaxEntryCount || uncompleteCount > MaxOrphanCount)
        {
            throw new InvalidDataException("History entry count is out of range.");
        }

        var totalCountLong = (long)receiveCount + sendCount;
        if (totalCountLong > MaxEntryCount)
        {
            throw new InvalidDataException("History total entry count is out of range.");
        }

        var totalCount = (int)totalCountLong;
        var entries = new List<ReceiveHistoryEntry>(totalCount);
        for (var i = 0; i < totalCount; i++)
        {
            entries.Add(ReadEntry(reader));
        }

        var orphans = new List<ReceiveOrphanHistory>((int)uncompleteCount);
        for (var i = 0; i < uncompleteCount; i++)
        {
            orphans.Add(ReadOrphan(reader));
        }

        return new HistoryDocument(entries, orphans);
    }

    /// <summary>
    /// 1 エントリ（メタ・ブロック）をバイナリへ書き出します。
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
        writer.Write((uint)Math.Clamp(entry.FileSize, 0L, uint.MaxValue));
        writer.Write((uint)Math.Max(0, entry.BlockCount));
        writer.Write(entry.IsSuccess);
        writer.Write(entry.OutputPath ?? string.Empty);
        writer.Write(entry.CompletionMessage ?? string.Empty);
        writer.Write(entry.Blocks.Count);
        foreach (var block in entry.Blocks)
        {
            var blockData = block.BlockComplete
                ? (block.BlockData ?? Array.Empty<byte>())
                : Array.Empty<byte>();
            var blockSize = block.BlockComplete ? blockData.Length : Math.Max(0, block.BlockSize);
            WriteBlock(writer, block.DataModulation, block.BlockIndex, blockSize, block.ContentHash, block.BlockComplete, blockData);
        }
    }

    /// <summary>
    /// 受信ブロック 1 件（変調・番号・サイズ・ブロックハッシュ・完了フラグ・本体）を書き出します。
    /// </summary>
    /// <param name="writer">出力先 BinaryWriter。</param>
    /// <param name="dataModulation">データ部変調 4 バイト。</param>
    /// <param name="blockIndex">ブロック番号。</param>
    /// <param name="blockSize">ブロックバイト数。</param>
    /// <param name="contentHash">ブロックハッシュ（32 バイト）。</param>
    /// <param name="blockComplete">ブロック受信が完了していれば true。</param>
    /// <param name="blockData">ブロック本体（未完了なら空）。</param>
    private static void WriteBlock(
        BinaryWriter writer,
        byte[]? dataModulation,
        long blockIndex,
        int blockSize,
        byte[]? contentHash,
        bool blockComplete,
        byte[] blockData)
    {
        writer.Write(NormalizeDataModulation(dataModulation));
        writer.Write((uint)Math.Clamp(blockIndex, 0, uint.MaxValue));
        writer.Write((uint)Math.Max(0, blockSize));
        writer.Write(NormalizeHash32(contentHash));
        writer.Write(blockComplete);
        if (blockComplete && blockData.Length > 0)
        {
            writer.Write(blockData);
        }
    }

    /// <summary>
    /// 不明ブロック 1 件を未完了エントリ形式（受信日時・入力・ファイルハッシュ・受信元・受信ブロック）で書き出します。
    /// </summary>
    /// <param name="writer">出力先 BinaryWriter。</param>
    /// <param name="orphan">識別子が正しい不明ブロック。</param>
    private static void WriteOrphan(BinaryWriter writer, ReceiveOrphanHistory orphan)
    {
        TrySplitOrphanIdentity(orphan.HashHex, out var indexText, out var fileHashHex, out var blockHashHex);
        writer.Write(orphan.ReceivedAtUtc == default ? 0L : orphan.ReceivedAtUtc.ToUniversalTime().Ticks);
        writer.Write((byte)orphan.InputDevice);
        writer.Write(NormalizeHash32(Convert.FromHexString(fileHashHex)));
        writer.Write(orphan.SourcePath ?? string.Empty);
        var payload = orphan.Payload ?? Array.Empty<byte>();
        var blockSize = orphan.IsComplete ? payload.Length : Math.Max(0, orphan.BlockSize);
        WriteBlock(
            writer,
            orphan.DataModulation,
            long.Parse(indexText, System.Globalization.CultureInfo.InvariantCulture),
            blockSize,
            Convert.FromHexString(blockHashHex),
            orphan.IsComplete,
            payload);
    }

    /// <summary>
    /// 未完了エントリ形式の不明ブロック 1 件を読み取ります。
    /// </summary>
    /// <param name="reader">未完了エントリ先頭の BinaryReader。</param>
    /// <returns>復元した不明ブロック。</returns>
    private static ReceiveOrphanHistory ReadOrphan(BinaryReader reader)
    {
        var ticks = reader.ReadInt64();
        var inputDeviceByte = reader.ReadByte();
        var fileHash = reader.ReadBytes(32);
        if (fileHash.Length != 32)
        {
            throw new InvalidDataException("Invalid uncomplete entry: file hash is missing.");
        }

        var sourcePath = reader.ReadString();
        var (dataModulation, blockIndex, blockSize, blockHash, blockComplete, blockData) = ReadBlock(reader);
        var inputDevice = Enum.IsDefined(typeof(ReceiveInputDevice), inputDeviceByte)
            ? (ReceiveInputDevice)inputDeviceByte
            : ReceiveInputDevice.Wav;
        return new ReceiveOrphanHistory(
            HashHex: $"{blockIndex}:{Convert.ToHexString(fileHash)}:{Convert.ToHexString(blockHash)}",
            Detail: string.Empty,
            Payload: blockComplete ? blockData : Array.Empty<byte>(),
            DataModulation: dataModulation,
            ReceivedAtUtc: ticks <= 0 ? default : new DateTime(ticks, DateTimeKind.Utc),
            InputDevice: inputDevice,
            SourcePath: sourcePath,
            BlockSize: blockSize);
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
        var fileSize = (long)reader.ReadUInt32();
        var blockCountU = reader.ReadUInt32();
        if (blockCountU > int.MaxValue)
        {
            throw new InvalidDataException("Invalid entry: BlockCount is out of range.");
        }

        var blockCount = (int)blockCountU;
        var isSuccess = reader.ReadBoolean();
        var outputPath = reader.ReadString();
        var completionMessage = reader.ReadString();

        var blockItemCount = reader.ReadInt32();
        if (blockItemCount < 0 || blockItemCount > 100000)
        {
            throw new InvalidDataException("Invalid receive entry: block count is out of range.");
        }

        var blocks = new List<ReceiveBlockHistory>(blockItemCount);
        for (var i = 0; i < blockItemCount; i++)
        {
            var (blockModulation, blockIndex, blockSize, contentHash, blockComplete, blockData) = ReadBlock(reader);
            blocks.Add(new ReceiveBlockHistory(
                DataModulation: blockModulation,
                BlockIndex: blockIndex,
                BlockSize: blockSize,
                ContentHash: contentHash,
                BlockComplete: blockComplete,
                BlockData: blockData,
                State: blockComplete ? ReceiveBlockState.Accepted : ReceiveBlockState.Unknown,
                ErrorText: string.Empty));
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
            Blocks: blocks);
    }

    /// <summary>
    /// 受信ブロック 1 件を読み取ります。
    /// </summary>
    /// <param name="reader">ブロック先頭の BinaryReader。</param>
    /// <returns>変調・番号・サイズ・ブロックハッシュ・完了フラグ・本体（未完了なら空）。</returns>
    private static (byte[] DataModulation, int BlockIndex, int BlockSize, byte[] ContentHash, bool BlockComplete, byte[] BlockData) ReadBlock(
        BinaryReader reader)
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
        var blockSize = (int)blockSizeU;
        var blockData = blockComplete && blockSize > 0
            ? reader.ReadBytes(blockSize)
            : Array.Empty<byte>();
        if (blockComplete && blockData.Length != blockSize)
        {
            throw new InvalidDataException("Invalid receive block: block data is truncated.");
        }

        return (dataModulation, (int)blockIndexU, blockSize, contentHash, blockComplete, blockData);
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
    /// SHA-256 ハッシュ（ブロック／ファイル）を長さ 32 に正規化します（不足は 0 埋め）。
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
    /// 既存エントリと新規エントリが同一 Identity か判定します（ハッシュ優先、受信はどちらかのハッシュが無いときだけパス＋ファイル名）。
    /// </summary>
    /// <param name="existing">既存エントリ。</param>
    /// <param name="incoming">新規エントリ。</param>
    /// <returns>同一とみなす場合は true。</returns>
    /// <remarks>同じ入力デバイスで受けた別ファイルを 1 件にまとめて上書きしないよう、ハッシュが両方あれば一致だけで判定する。</remarks>
    private static bool IsSameIdentity(ReceiveHistoryEntry existing, ReceiveHistoryEntry incoming)
    {
        if (existing.Kind != incoming.Kind)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(existing.ContentHashHex)
            && !string.IsNullOrWhiteSpace(incoming.ContentHashHex))
        {
            return string.Equals(existing.ContentHashHex, incoming.ContentHashHex, StringComparison.OrdinalIgnoreCase);
        }

        if (incoming.Kind == HistoryEntryKind.Receive)
        {
            return string.Equals(existing.SourcePath, incoming.SourcePath, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(existing.FileName, incoming.FileName, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// 同一 Identity の既存エントリへ新規内容をマージします（日時・ブロック含む）。
    /// </summary>
    /// <param name="existing">既存エントリ。</param>
    /// <param name="incoming">マージ元の新規エントリ。</param>
    /// <returns>マージ後のエントリ。</returns>
    private static ReceiveHistoryEntry MergeEntry(ReceiveHistoryEntry existing, ReceiveHistoryEntry incoming)
    {
        var latest = incoming.ReceivedAtUtc >= existing.ReceivedAtUtc ? incoming.ReceivedAtUtc : existing.ReceivedAtUtc;
        var blocks = MergeBlocks(existing.Blocks, incoming.Blocks);

        var merged = existing with
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
            Blocks = blocks
        };
        // 再読み込みで欠けていたブロックだけ受信した場合も、統合後に全ブロックが揃えば完了にする
        return merged.IsSuccess || merged.Kind != HistoryEntryKind.Receive
            ? merged
            : merged with { IsSuccess = HasAllBlocks(merged.Blocks, merged.BlockCount, merged.FileSize) };
    }

    /// <summary>
    /// 不明ブロックのうち、ファイルハッシュが一致しブロック数が分かっている受信エントリ（親）があるものを、その受信ブロックへ取り込みます。
    /// </summary>
    /// <param name="entries">全エントリ。親を取り込み後のものへ置き換えます。</param>
    /// <param name="orphans">不明ブロック。</param>
    /// <returns>親が見つからず不明ブロックとして残すもの。</returns>
    /// <remarks>取り込んだ不明ブロックは消す。親にすでに完了ブロックがある番号はデータを足さずに消す。</remarks>
    private static List<ReceiveOrphanHistory> AdoptOrphansIntoParents(
        List<ReceiveHistoryEntry> entries,
        IReadOnlyList<ReceiveOrphanHistory> orphans)
    {
        var remaining = new List<ReceiveOrphanHistory>(orphans.Count);
        foreach (var orphan in orphans)
        {
            if (!TryGetOrphanKey(orphan, out _, out var fileHashHex, out var blockIndex))
            {
                remaining.Add(orphan);
                continue;
            }

            var parent = entries.FindIndex(entry =>
                entry.Kind == HistoryEntryKind.Receive
                && entry.BlockCount > 0
                && blockIndex < entry.BlockCount
                && string.Equals(entry.ContentHashHex?.Trim(), fileHashHex, StringComparison.OrdinalIgnoreCase));
            if (parent < 0)
            {
                remaining.Add(orphan);
                continue;
            }

            entries[parent] = AdoptOrphan(entries[parent], orphan, (int)blockIndex);
        }

        return remaining;
    }

    /// <summary>
    /// 不明ブロック 1 件を親の受信ブロックへ取り込みます。データ付きは未完了・未受信の番号を完了にし、データ無し（BH のみ）は未受信の番号だけ NG で足します。
    /// </summary>
    /// <param name="entry">親の受信エントリ。</param>
    /// <param name="orphan">取り込む不明ブロック。</param>
    /// <param name="blockIndex">ブロック位置。</param>
    /// <returns>取り込み後のエントリ。全ブロックが揃えば完了にします。</returns>
    private static ReceiveHistoryEntry AdoptOrphan(ReceiveHistoryEntry entry, ReceiveOrphanHistory orphan, int blockIndex)
    {
        var prior = entry.Blocks.FirstOrDefault(block => block.BlockIndex == blockIndex);
        if (prior is { BlockComplete: true } || (prior is not null && !orphan.IsComplete))
        {
            return entry;
        }

        TrySplitOrphanIdentity(orphan.HashHex, out _, out _, out var blockHashHex);
        var adopted = new ReceiveBlockHistory(
            DataModulation: NormalizeDataModulation(orphan.DataModulation),
            BlockIndex: blockIndex,
            BlockSize: orphan.IsComplete ? orphan.Payload.Length : Math.Max(0, orphan.BlockSize),
            ContentHash: NormalizeHash32(Convert.FromHexString(blockHashHex)),
            BlockComplete: orphan.IsComplete,
            BlockData: orphan.IsComplete ? orphan.Payload : Array.Empty<byte>(),
            State: orphan.IsComplete ? ReceiveBlockState.Accepted : ReceiveBlockState.Error,
            ErrorText: orphan.IsComplete ? string.Empty : "NG");
        var blocks = entry.Blocks
            .Where(block => block.BlockIndex != blockIndex)
            .Append(adopted)
            .OrderBy(block => block.BlockIndex)
            .ToArray();
        return entry with
        {
            Blocks = blocks,
            IsSuccess = entry.IsSuccess || HasAllBlocks(blocks, entry.BlockCount, entry.FileSize)
        };
    }

    /// <summary>
    /// 不明ブロックの一覧を統合します。同じ識別子（ブロック位置＋ファイルハッシュ＋ブロックハッシュ）は 1 件にし、
    /// データ付きを優先、受信日時は新しい方にします。
    /// </summary>
    /// <param name="existing">既存の不明ブロック。</param>
    /// <param name="incoming">追加する不明ブロック。</param>
    /// <returns>統合後の一覧（既存が先）。</returns>
    private static List<ReceiveOrphanHistory> MergeOrphans(
        IReadOnlyList<ReceiveOrphanHistory> existing,
        IReadOnlyList<ReceiveOrphanHistory> incoming)
    {
        var merged = new List<ReceiveOrphanHistory>(existing.Count + incoming.Count);
        var positionByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var orphan in existing.Concat(incoming))
        {
            var key = TryGetOrphanKey(orphan, out var orphanKey, out _, out _) ? orphanKey : orphan.HashHex;
            if (!positionByKey.TryGetValue(key, out var position))
            {
                positionByKey[key] = merged.Count;
                merged.Add(orphan);
                continue;
            }

            var prior = merged[position];
            var latest = orphan.ReceivedAtUtc > prior.ReceivedAtUtc ? orphan.ReceivedAtUtc : prior.ReceivedAtUtc;
            var kept = !prior.IsComplete && orphan.IsComplete ? orphan : prior;
            merged[position] = kept with { ReceivedAtUtc = latest };
        }

        return merged;
    }

    /// <summary>
    /// 不明ブロックの比較用キー（ブロック位置:ファイルハッシュ:ブロックハッシュ、ハッシュは大文字）を作ります。
    /// </summary>
    /// <param name="orphan">不明ブロック。</param>
    /// <param name="key">比較用キー。</param>
    /// <param name="fileHashHex">大文字のファイルハッシュ。</param>
    /// <param name="blockIndex">ブロック位置。</param>
    /// <returns>識別子が正しい形式（SHA-256 のファイルハッシュ・ブロックハッシュ）なら true。</returns>
    private static bool TryGetOrphanKey(
        ReceiveOrphanHistory orphan,
        out string key,
        out string fileHashHex,
        out long blockIndex)
    {
        key = string.Empty;
        fileHashHex = string.Empty;
        blockIndex = -1;
        if (!TrySplitOrphanIdentity(orphan.HashHex, out var indexText, out var fileHash, out var blockHash)
            || fileHash.Trim().Length != 64
            || blockHash.Trim().Length != 64
            || !long.TryParse(indexText, out blockIndex)
            || blockIndex < 0
            || blockIndex > uint.MaxValue)
        {
            return false;
        }

        fileHashHex = fileHash.Trim().ToUpperInvariant();
        key = $"{blockIndex}:{fileHashHex}:{blockHash.Trim().ToUpperInvariant()}";
        return true;
    }

    /// <summary>
    /// 0 から BlockCount-1 まで完了ブロックが揃い、宣言サイズを満たすかを見ます。
    /// </summary>
    /// <param name="blocks">ブロック一覧。</param>
    /// <param name="blockCount">必要なブロック数。</param>
    /// <param name="fileSize">ファイルサイズ。0 以下なら長さは見ません。</param>
    /// <returns>ダウンロードできる揃い方なら true。</returns>
    private static bool HasAllBlocks(IReadOnlyList<ReceiveBlockHistory> blocks, int blockCount, long fileSize)
    {
        if (blockCount <= 0)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < blockCount; i++)
        {
            var block = blocks.FirstOrDefault(item => item.BlockIndex == i);
            if (block is not { BlockComplete: true, BlockData.Length: > 0 })
            {
                return false;
            }

            sum += block.BlockData.Length;
        }

        return fileSize <= 0 || sum >= fileSize;
    }

    /// <summary>
    /// SHA-256（64桁）の16進文字列かを判定します。
    /// </summary>
    /// <param name="value">判定する文字列。</param>
    /// <returns>桁数と文字種が一致すれば true。</returns>
    private static bool IsShaHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.Length != 64)
        {
            return false;
        }

        foreach (var c in text)
        {
            var hex = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!hex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 不明ブロック識別子 index:fileHash:blockHash を分解します。
    /// </summary>
    /// <param name="identity">孤立キー。</param>
    /// <param name="blockIndexText">ブロック番号の文字列。</param>
    /// <param name="fileHashHex">ファイルハッシュ。</param>
    /// <param name="blockHashHex">ブロックハッシュ。</param>
    /// <returns>3要素で両ハッシュが16進なら true。</returns>
    private static bool TrySplitOrphanIdentity(
        string? identity,
        out string blockIndexText,
        out string fileHashHex,
        out string blockHashHex)
    {
        blockIndexText = string.Empty;
        fileHashHex = string.Empty;
        blockHashHex = string.Empty;
        if (string.IsNullOrWhiteSpace(identity))
        {
            return false;
        }

        var parts = identity.Split(':');
        if (parts.Length < 3)
        {
            return false;
        }

        blockIndexText = parts[0];
        fileHashHex = parts[1];
        blockHashHex = parts[2];
        return IsShaHex(fileHashHex) && IsShaHex(blockHashHex);
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
}
