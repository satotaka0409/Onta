using Onta.Core;

namespace Onta.History;

internal static class HistoryService
{
    /// <summary>
    /// 履歴ファイルから最新の受信エントリを取得します。
    /// </summary>
    /// <param name="historyFilePath">履歴ファイルパス（Onta_history.bin）。</param>
    /// <returns>最新の受信エントリ。無い場合は null。</returns>
    public static ReceiveHistoryEntry? TryLoadLatestReceive(string historyFilePath)
    {
        return ReceiveHistoryStore.TryLoadLatestReceive(historyFilePath);
    }

    /// <summary>
    /// 履歴ファイルの全エントリ（受信・送信）を読み込みます。
    /// </summary>
    /// <param name="historyFilePath">履歴ファイルパス。</param>
    /// <returns>エントリ一覧。破損・未存在時は空。</returns>
    public static IReadOnlyList<ReceiveHistoryEntry> LoadEntries(string historyFilePath)
    {
        return ReceiveHistoryStore.LoadEntries(historyFilePath);
    }

    /// <summary>
    /// 指定 EntryId の履歴を削除してファイルへ書き戻します。
    /// </summary>
    /// <param name="historyFilePath">履歴ファイルパス。</param>
    /// <param name="entryId">削除対象のエントリ ID。</param>
    /// <returns>削除できた場合は true。</returns>
    public static bool DeleteEntry(string historyFilePath, string entryId)
    {
        return ReceiveHistoryStore.DeleteEntry(historyFilePath, entryId);
    }

    /// <summary>
    /// 受信履歴の完了ブロックからペイロードを再構築し、ファイルへ書き出します。
    /// </summary>
    /// <param name="entry">書き出し元の受信履歴エントリ。</param>
    /// <param name="targetPath">出力先ファイルパス。</param>
    /// <returns>書き出し成功時は true。未完了・ハッシュ不一致などは false。</returns>
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

    /// <summary>
    /// 受信履歴からペイロードを再構築できるか判定します。
    /// </summary>
    /// <param name="entry">判定対象の受信履歴エントリ。</param>
    /// <returns>再構築可能なら true。</returns>
    public static bool CanExportPayload(ReceiveHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return TryBuildPayloadFromHistory(entry, out _);
    }

    /// <summary>
    /// 完了ブロックを BlockIndex 順に結合し、必要ならハッシュ検証したペイロードを構築します。
    /// </summary>
    /// <param name="entry">受信履歴エントリ。</param>
    /// <param name="payload">構築したバイト列。失敗時は空配列。</param>
    /// <returns>構築成功時は true。</returns>
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

    /// <summary>
    /// 受信エントリを履歴へ追記（同一 Identity ならマージ）します。
    /// </summary>
    /// <param name="historyFilePath">履歴ファイルパス。</param>
    /// <param name="entry">保存する受信エントリ。</param>
    public static void SaveReceive(string historyFilePath, ReceiveHistoryEntry entry)
    {
        ReceiveHistoryStore.Append(historyFilePath, entry);
    }

    /// <summary>
    /// 送信完了を履歴へ記録します（既定: SC-8 / BPSK / モノラル）。
    /// </summary>
    /// <param name="historyFilePath">履歴ファイルパス。</param>
    /// <param name="inputPath">送信元ファイルパス。</param>
    /// <param name="outputWavPath">WAV 出力パス（音声出力時は空可）。</param>
    /// <param name="completionMessage">完了メッセージ。</param>
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

    /// <summary>
    /// 送信完了を履歴へ記録します（変調パラメータ付き）。
    /// </summary>
    /// <param name="historyFilePath">履歴ファイルパス。</param>
    /// <param name="inputPath">送信元ファイルパス。</param>
    /// <param name="outputWavPath">WAV 出力パス（音声出力時は空可）。</param>
    /// <param name="activeSubcarriers">データ部サブキャリア数。</param>
    /// <param name="modulationScheme">データ部変調方式。</param>
    /// <param name="channelMode">モノラル／ステレオ。</param>
    /// <param name="completionMessage">完了メッセージ。</param>
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

    /// <summary>
    /// データ部変調方式 4 バイト（SC / 変調 / チャネル / 予備）を組み立てます。
    /// </summary>
    /// <param name="activeSubcarriers">サブキャリア数。非対応時は 0。</param>
    /// <param name="modulationScheme">変調方式（1:BPSK … 5:256QAM）。</param>
    /// <param name="channelMode">0:モノラル / 1:ステレオ。</param>
    /// <returns>長さ 4 の DataModulation バイト列。</returns>
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
