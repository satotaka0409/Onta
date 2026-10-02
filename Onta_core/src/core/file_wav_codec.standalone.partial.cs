using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace Onta.Core;

/// <summary>
/// 受信履歴に登録済みのファイル情報です（FH 未受信のまま BH を受けたときの表示用）。
/// </summary>
/// <param name="FileName">表示用ファイル名。</param>
/// <param name="FileSize">ファイルサイズ（バイト）。</param>
/// <param name="BlockCount">全体ブロック数。</param>
public sealed record KnownReceiveFile(string FileName, long FileSize, int BlockCount);

/// <summary>
/// FH 未受信時の BH+BD 単独復号の結果です。
/// </summary>
internal enum StandaloneBlockStatus
{
    /// <summary>BH または BD がまだ届ききっていません。</summary>
    NeedMoreSamples,

    /// <summary>BH を復号し、BD の成否まで確定しました。</summary>
    Decoded,

    /// <summary>指定位置は BH として復号できませんでした。</summary>
    NotBlock
}

public sealed partial class FileWavCodec
{
    /// <summary>
    /// BH 変調部の先頭位置から BH+BD を 1 ブロック分復号し、成功したペイロードを不明ブロックとして登録します（FH 未受信時用）。
    /// </summary>
    /// <param name="leftSamples">L チャネル PCM（バッファ先頭は <see cref="ProgressiveDecodeState.StreamSampleBase"/>）。</param>
    /// <param name="rightSamples">R チャネル PCM（モノラル時は空）。</param>
    /// <param name="state">段階デコード状態。BD 待ちの BH はここに保留します。</param>
    /// <param name="bhStart">BH 変調部の先頭（バッファ先頭基準）。</param>
    /// <param name="tuning">復号チューニング。null なら既定値。</param>
    /// <param name="allowIncomplete">true なら BD が届ききるまで NeedMoreSamples を返します。</param>
    /// <param name="consumedEnd">処理を終えた位置（バッファ先頭基準）。Decoded なら BD 末尾、NotBlock なら BH 先頭の 1 シンボル後。</param>
    /// <returns>復号結果。</returns>
    /// <remarks>BH を一度復号したら BD 待ちの間は再復号しない（リアルタイム受信で 0.1 秒ごとに呼ばれるため）。</remarks>
    internal StandaloneBlockStatus DecodeStandaloneBlockProgressive(
        Complex[] leftSamples,
        Complex[] rightSamples,
        ProgressiveDecodeState state,
        int bhStart,
        DecodeRuntimeTuning? tuning,
        bool allowIncomplete,
        out int consumedEnd)
    {
        ArgumentNullException.ThrowIfNull(state);
        tuning ??= DecodeRuntimeTuning.Default;
        var headerOfdm = CreateHeaderOfdm();
        var symbol = headerOfdm.SamplesPerOfdmSymbol;
        consumedEnd = Math.Clamp(bhStart + symbol, 0, leftSamples.Length);
        var bhAbsolute = state.StreamSampleBase + bhStart;

        byte[] header;
        int dataStart;
        if (state.StandalonePendingHeader is { } pending && state.StandalonePendingBhStart == bhAbsolute)
        {
            header = pending;
            dataStart = (int)(state.StandalonePendingDataStart - state.StreamSampleBase);
        }
        else
        {
            state.StandalonePendingHeader = null;
            var fineRadius = Math.Max(symbol * 2, _profile.SampleRate / 200);
            var bhSamples = HeaderPacketSamples(headerOfdm, BlockHeaderBytes, 0);
            if (allowIncomplete && leftSamples.Length < bhStart + bhSamples + fineRadius + symbol)
            {
                return StandaloneBlockStatus.NeedMoreSamples;
            }

            var cursor = bhStart;
            var logical = (long)bhStart;
            try
            {
                header = DecodeHeaderPacketSyncedTryingGrids(
                    ref headerOfdm,
                    leftSamples,
                    rightSamples,
                    ref cursor,
                    ref logical,
                    BlockHeaderBytes,
                    BlockHeaderPilot,
                    fineRadius,
                    statusBoard: state.StatusBoard,
                    frameKind: CoreFrameKind.Bh);
                EnsureHeaderCrc(header, "standalone block header");
                var declaredSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));
                if (declaredSize < 0 || declaredSize > DataBlockBytes)
                {
                    throw new InvalidDataException($"Invalid block size {declaredSize}.");
                }

                _ = ReadBlockDataModulation(header);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
            {
                return StandaloneBlockStatus.NotBlock;
            }

            state.HeaderRsDecodeCount++;
            dataStart = cursor;
            state.StandalonePendingHeader = header;
            state.StandalonePendingBhStart = bhAbsolute;
            state.StandalonePendingDataStart = state.StreamSampleBase + cursor;
            BeginStandaloneBlock(state, header);
        }

        var blockIndex = ReadStandaloneBlockIndex(header);
        var blockSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));
        var expectedHash = header.AsSpan(24, 32).ToArray();
        var (blockSc, blockModulation) = ReadBlockDataModulation(header);
        var dataOfdm = CreateDataOfdm(blockSc, blockModulation);
        var dataSamples = DataPacketSamples(dataOfdm, blockSize, _profile.ChannelMode, blockModulation);
        var margin = (dataSamples / 50) + (dataOfdm.SamplesPerOfdmSymbol * 8);
        var available = leftSamples.Length - dataStart;
        if (allowIncomplete && available < dataSamples + margin)
        {
            // BD 受信中もメーターを進める（復号は BD が揃ってから）
            var arrived = Math.Clamp(available / (double)Math.Max(1, dataSamples), 0.0, 1.0);
            PublishStandaloneProgress(state, CoreFrameKind.Bd, blockIndex, 20.0 + (40.0 * arrived));
            consumedEnd = Math.Clamp(dataStart, 0, leftSamples.Length);
            return StandaloneBlockStatus.NeedMoreSamples;
        }

        var dataCursor = dataStart;
        var dataLogical = (long)dataStart;
        var padded = Array.Empty<byte>();
        try
        {
            padded = DecodeDataBlockSynced(
                leftSamples,
                rightSamples,
                ref dataCursor,
                ref dataLogical,
                dataOfdm,
                Math.Max(dataOfdm.SamplesPerOfdmSymbol * 2, _profile.SampleRate / 200),
                expectedBlockHash: expectedHash,
                payloadLength: blockSize,
                modulationScheme: blockModulation,
                tuning: tuning,
                punctureRate: ResolveDataPunctureRate(blockModulation),
                wowLocked: false,
                statusBoard: state.StatusBoard,
                out var diag,
                onSoftProgress: frac => PublishStandaloneProgress(
                    state,
                    CoreFrameKind.Bd,
                    blockIndex,
                    60.0 + (39.0 * Math.Clamp(frac, 0.0, 1.0))));
            state.DataBlocksDecoded++;
            state.DataTotalAttempts += diag.TotalAttempts;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            padded = Array.Empty<byte>();
        }

        var acceptable = padded.Length > 0 && IsDataBlockAcceptable(padded, expectedHash, blockSize);
        byte[]? payload = null;
        if (acceptable)
        {
            payload = new byte[blockSize];
            Buffer.BlockCopy(padded, 0, payload, 0, blockSize);
        }

        RegisterStandaloneBlock(state, header, payload);
        state.StandalonePendingHeader = null;
        state.StandalonePendingBhStart = -1;
        consumedEnd = acceptable
            ? Math.Clamp(dataCursor, 0, leftSamples.Length)
            : Math.Clamp(dataStart + dataSamples, 0, leftSamples.Length);
        PublishStandaloneProgress(state, CoreFrameKind.Bd, blockIndex, acceptable ? 99.0 : 0.0);
        return StandaloneBlockStatus.Decoded;
    }

    /// <summary>
    /// BH 手前の無変調区間を手がかりに、バッファ内の BH+BD を順に単独復号します（FH 未受信時用）。
    /// </summary>
    /// <param name="leftSamples">L チャネル PCM。</param>
    /// <param name="rightSamples">R チャネル PCM（モノラル時は空）。</param>
    /// <param name="state">段階デコード状態。</param>
    /// <param name="tuning">復号チューニング。</param>
    /// <returns>BH を 1 件以上復号できたか（DecodedAny）と、BD を 1 件以上受理できたか（AcceptedAny）。</returns>
    /// <remarks>全位置で BH 復号を試す総当たりは 1 回で数十秒かかるため、無変調区間の検出で候補を絞る。</remarks>
    private (bool DecodedAny, bool AcceptedAny) DecodeStandaloneBlocksByAnchors(
        Complex[] leftSamples,
        Complex[] rightSamples,
        ProgressiveDecodeState state,
        DecodeRuntimeTuning tuning)
    {
        var detector = CreateBlockHeaderAnchorDetector();
        var decodedAny = false;
        var acceptedAny = false;
        var nextFree = 0;
        while (detector.TryAdvance(leftSamples, out var anchor))
        {
            if (anchor < nextFree)
            {
                continue;
            }

            var acceptedBefore = state.OrphanPayloadByHash.Count;
            var result = DecodeStandaloneBlockProgressive(
                leftSamples,
                rightSamples,
                state,
                anchor,
                tuning,
                allowIncomplete: false,
                out var end);
            if (result != StandaloneBlockStatus.Decoded)
            {
                continue;
            }

            decodedAny = true;
            acceptedAny |= state.OrphanPayloadByHash.Count > acceptedBefore
                || state.LastError?.StartsWith("BH+BD-ONLY", StringComparison.Ordinal) == true;
            nextFree = end;
        }

        return (decodedAny, acceptedAny);
    }

    /// <summary>
    /// FH 未受信時に BH を復号した直後の処理です。BH メタを登録し、既知ファイルなら画面へファイル情報を出してメーター・グラフ表示を始めます。
    /// </summary>
    /// <param name="state">段階デコード状態。</param>
    /// <param name="header">CRC 検証済みの BH。</param>
    private static void BeginStandaloneBlock(ProgressiveDecodeState state, byte[] header)
    {
        var blockIndex = ReadStandaloneBlockIndex(header);
        var fileHashHex = Convert.ToHexString(header.AsSpan(56, 64));
        state.ReceivedFileHashHex = fileHashHex;
        if (!string.Equals(state.StandaloneKnownFileHash, fileHashHex, StringComparison.OrdinalIgnoreCase))
        {
            state.StandaloneKnownFileHash = fileHashHex;
            try
            {
                state.StandaloneKnownFile = state.ResolveKnownFile?.Invoke(fileHashHex);
            }
            catch
            {
                state.StandaloneKnownFile = null;
            }
        }

        if (blockIndex >= 0)
        {
            state.BlockDataModulationByIndex[blockIndex] = header.AsSpan(8, 4).ToArray();
            state.BlockExpectedHashByIndex[blockIndex] = header.AsSpan(24, 32).ToArray();
            state.BlockSizeByIndex[blockIndex] = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));
            state.BlockHeaderIdentityByIndex[blockIndex] = BuildStandaloneIdentity(header);
            // 再受信中は前回の NG を外す（画面が NG のまま 0% に戻さない）
            if (state.BlockBdOutcomeByIndex.TryGetValue(blockIndex, out var previous) && !previous)
            {
                state.BlockBdOutcomeByIndex.Remove(blockIndex);
            }
        }

        var (blockSc, blockModulation) = ReadBlockDataModulation(header);
        state.DetectedDataSubcarriers = blockSc;
        state.DetectedModulationScheme = blockModulation;
        state.StatusBoard.SetAnalyzing(false);
        if (state.StandaloneKnownFile is { } known)
        {
            var name = string.IsNullOrWhiteSpace(known.FileName) ? "(不明)" : known.FileName;
            state.StatusBoard.SetFileInfo(name, $"{known.FileSize:N0} bytes", known.BlockCount.ToString());
        }

        PublishStandaloneProgress(state, CoreFrameKind.Bh, blockIndex, 20.0);
    }

    /// <summary>
    /// FH 未受信で単独復号したブロックを登録します。BD 成功分は不明ブロック（ペイロード付き）、失敗分は BD 成否だけを残します。
    /// </summary>
    /// <param name="state">段階デコード状態。</param>
    /// <param name="header">CRC 検証済みの BH。</param>
    /// <param name="payload">受理したペイロード。BD 失敗時は null。</param>
    private static void RegisterStandaloneBlock(ProgressiveDecodeState state, byte[] header, byte[]? payload)
    {
        var blockIndex = ReadStandaloneBlockIndex(header);
        var rawIndex = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(12, 8));
        if (payload is null)
        {
            if (blockIndex >= 0
                && !(state.BlockBdOutcomeByIndex.TryGetValue(blockIndex, out var previous) && previous))
            {
                state.BlockBdOutcomeByIndex[blockIndex] = false;
            }

            state.LastError = $"BLK-{rawIndex} BD受信失敗（FH未受信）";
            return;
        }

        var identity = BuildStandaloneIdentity(header);
        var action = state.OrphanPayloadByHash.ContainsKey(identity) ? "UPDATE" : "ADD";
        if (blockIndex >= 0)
        {
            state.BlockBdOutcomeByIndex[blockIndex] = true;
        }

        state.OrphanPayloadByHash[identity] = payload;
        state.OrphanDetailByHash[identity] = $"{action} BH+BD index={rawIndex} (fileHash+blockHash+blockNo)";
        state.OrphanDataModulationByHash[identity] = header.AsSpan(8, 4).ToArray();
        state.DataBlocksAccepted++;
        state.AcceptedBlockCount = state.OrphanPayloadByHash.Count;
        state.LastError = $"BH+BD-ONLY {action} index={rawIndex} key={identity[..Math.Min(12, identity.Length)]}";
    }

    /// <summary>
    /// FH 確定時に、FH 未受信のまま受けた同じファイルの不明ブロックをスロットへ取り込みます。
    /// </summary>
    /// <param name="state">FH 確定直後の段階デコード状態（OutputSlots / SlotAccepted 生成済み）。</param>
    /// <param name="previousFileHash">FH 確定前に BH から得ていたファイルハッシュ（無ければ null）。</param>
    /// <remarks>別ファイルの BH を受けていた場合、ブロック番号で引くメタは FH のファイルと混ざるので捨てる。</remarks>
    private static void AdoptStandaloneBlocks(ProgressiveDecodeState state, string? previousFileHash)
    {
        var fileHash = state.ReceivedFileHashHex;
        var slots = state.OutputSlots;
        var accepted = state.SlotAccepted;
        if (string.IsNullOrWhiteSpace(fileHash) || slots is null || accepted is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(previousFileHash)
            && !string.Equals(previousFileHash, fileHash, StringComparison.OrdinalIgnoreCase))
        {
            state.BlockDataModulationByIndex.Clear();
            state.BlockExpectedHashByIndex.Clear();
            state.BlockSizeByIndex.Clear();
            state.BlockBdOutcomeByIndex.Clear();
            state.BlockHeaderIdentityByIndex.Clear();
        }

        foreach (var key in state.OrphanPayloadByHash.Keys.ToArray())
        {
            var parts = key.Split(':');
            if (parts.Length != 3
                || !int.TryParse(parts[0], out var index)
                || index < 0
                || index >= slots.Length
                || !string.Equals(parts[1], fileHash, StringComparison.OrdinalIgnoreCase)
                || state.OrphanPayloadByHash[key] is not { Length: > 0 } payload)
            {
                continue;
            }

            slots[index] = payload;
            accepted[index] = true;
            state.BlockBdOutcomeByIndex[index] = true;
            state.BlockHashOwners[parts[2]] = index;
            state.OrphanPayloadByHash.Remove(key);
            state.OrphanDetailByHash.Remove(key);
            state.OrphanDataModulationByHash.Remove(key);
        }

        state.AcceptedBlockCount = accepted.Count(x => x);
    }

    /// <summary>
    /// FH 未受信時のブロック進捗を共有ステータスへ書き込みます。既知ファイルなら総ブロック数も出します。
    /// </summary>
    /// <param name="state">段階デコード状態。</param>
    /// <param name="frame">現在のフレーム種別（BH / BD）。</param>
    /// <param name="blockIndex">ブロック番号（不明は負値）。</param>
    /// <param name="blockLocalPercent">ブロック行メーターの局所進捗（%）。</param>
    private static void PublishStandaloneProgress(
        ProgressiveDecodeState state,
        CoreFrameKind frame,
        int blockIndex,
        double blockLocalPercent)
    {
        var total = state.StandaloneKnownFile?.BlockCount ?? 0;
        var acceptedCount = state.OrphanPayloadByHash.Count;
        state.CurrentFrame = frame;
        state.CurrentBlockIndex = blockIndex;
        state.StatusBoard.SetProgress(new CoreProgressInfo(
            CurrentFrame: frame,
            CurrentBlockIndex: blockIndex,
            PassIndex: 0,
            AcceptedBlockCount: acceptedCount,
            TotalBlockCount: total,
            ProgressPercent: total > 0 ? Math.Clamp(100.0 * acceptedCount / total, 0.0, 99.0) : 2.0,
            CurrentBlockProgressPercent: blockLocalPercent));
        state.StatusBoard.SetErrorFrameKind(frame);
    }

    /// <summary>
    /// BH のブロック番号を int で返します。範囲外は -1 です。
    /// </summary>
    /// <param name="header">BH。</param>
    /// <returns>ブロック番号、または -1。</returns>
    private static int ReadStandaloneBlockIndex(byte[] header)
    {
        var value = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(12, 8));
        return value is >= 0 and <= int.MaxValue ? (int)value : -1;
    }

    /// <summary>
    /// BH から不明ブロックの同一性キー（blockIndex:fileHash:blockHash）を組み立てます。
    /// </summary>
    /// <param name="header">BH。</param>
    /// <returns>同一性キー。</returns>
    private static string BuildStandaloneIdentity(byte[] header) =>
        string.Concat(
            BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(12, 8)).ToString(),
            ":",
            Convert.ToHexString(header.AsSpan(56, 64)),
            ":",
            Convert.ToHexString(header.AsSpan(24, 32)));
}
