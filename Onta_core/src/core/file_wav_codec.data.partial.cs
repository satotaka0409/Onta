using System.Numerics;

namespace Onta.Core;

public sealed partial class FileWavCodec
{
    /// <summary>
    /// データブロックを誤り訂正符号化して OFDM 変調し、PCM バッファへ追記します。
    /// </summary>
    /// <param name="leftPcm">左チャネル出力 PCM バッファ。</param>
    /// <param name="rightPcm">右チャネル出力 PCM バッファ（モノラル時は未使用）。</param>
    /// <param name="ofdm">変調に使う OFDM 生成器。</param>
    /// <param name="payload">送信対象のペイロード。</param>
    /// <param name="punctureRate">畳み込み符号のパンクチャ率。</param>
    private static void AppendModulatedDataBlock(
        List<Complex> leftPcm,
        List<Complex> rightPcm,
        OfdmGenerator ofdm,
        byte[] payload,
        ConvolutionalCode.PunctureRate punctureRate)
    {
        var packed = PackDataBlockWithCrc(payload);
        var turboEncoded = EncodeTurboBlock(packed);
        var convEncoded = ConvolutionalCode.Encode(turboEncoded, terminate: true, punctureRate: punctureRate);
        var bits = BytesToBitsMsb(convEncoded);
        if (ofdm.ChannelMode == ChannelMode.Stereo)
        {
            SplitBitsForStereo(bits, out var leftBits, out var rightBits);
            AppendPair(leftPcm, rightPcm, ofdm.ModulateBitStreams(leftBits, rightBits, leftPcm.Count));
        }
        else
        {
            AppendPair(leftPcm, rightPcm, ofdm.ModulateBits(bits, leftPcm.Count));
        }
    }

    /// <summary>
    /// ペイロード末尾に CRC を付与し、Turbo 符号単位までパディングしたブロックを返します。
    /// </summary>
    /// <param name="payload">入力ペイロード。</param>
    private static byte[] PackDataBlockWithCrc(byte[] payload)
    {
        if (payload.Length > DataBlockBytes)
        {
            throw new ArgumentException($"Payload exceeds {DataBlockBytes} bytes.", nameof(payload));
        }

        var withCrcLen = payload.Length + CrcBytes;
        var paddedLen = TurboPaddedLength(withCrcLen);
        var packed = new byte[paddedLen];
        Buffer.BlockCopy(payload, 0, packed, 0, payload.Length);
        var crc = Crc32.Compute(payload);
        Crc32.WriteBigEndian(packed.AsSpan(payload.Length, CrcBytes), crc);
        return packed;
    }

    /// <summary>
    /// Turbo 符号化単位に切り上げたバイト数を返します。
    /// </summary>
    /// <param name="contentLength">元データ長（CRC を含む）。</param>
    private static int TurboPaddedLength(int contentLength)
    {
        var unit = TurboEcc1024.DataUnitBytes;
        return ((Math.Max(contentLength, 1) + unit - 1) / unit) * unit;
    }

    /// <summary>
    /// 指定パスでのデータブロック送信順を返します。
    /// </summary>
    /// <param name="blockCount">総ブロック数。</param>
    /// <param name="passIndex">送信パス番号（0始まり）。</param>
    /// <returns>送信順のブロックインデックス配列。</returns>
    public static int[] GetBlockEmissionOrder(int blockCount, int passIndex)
    {
        if (blockCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockCount));
        }

        if (passIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(passIndex));
        }

        var order = new int[blockCount];
        if ((passIndex & 1) == 0)
        {
            for (var i = 0; i < blockCount; i++)
            {
                order[i] = i;
            }

            return order;
        }

        var write = 0;
        for (var i = 0; i < blockCount; i += 2)
        {
            if (i + 1 < blockCount)
            {
                order[write++] = i + 1;
            }

            order[write++] = i;
        }

        return order;
    }

    /// <summary>
    /// パス番号に応じたサブキャリア数と変調方式を解決します。
    /// </summary>
    /// <param name="passIndex">送信パス番号（0 は基本設定）。</param>
    /// <param name="baseSubcarriers">基本サブキャリア数。</param>
    /// <param name="baseModulation">基本変調方式。</param>
    /// <returns>当該パスで使うサブキャリア数と変調方式。</returns>

    public static (int Subcarriers, ModulationScheme Modulation) ResolveInterleavePassModulation(
        int passIndex,
        int baseSubcarriers,
        ModulationScheme baseModulation)
    {
        if (passIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(passIndex));
        }

        if (passIndex == 0)
        {
            return (baseSubcarriers, baseModulation);
        }

        var sc = baseSubcarriers switch
        {
            32 or 24 => 16,
            16 or 8 => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(baseSubcarriers), baseSubcarriers, "Unsupported subcarrier count.")
        };
        var mod = baseModulation switch
        {
            ModulationScheme.Qam64 or ModulationScheme.Qam16 => ModulationScheme.Qpsk,
            ModulationScheme.Qpsk or ModulationScheme.Bpsk => ModulationScheme.Bpsk,
            _ => throw new ArgumentOutOfRangeException(nameof(baseModulation), baseModulation, "Unsupported modulation scheme.")
        };
        return (sc, mod);
    }

    /// <summary>
    /// ビット列をステレオ送信向けに左右チャネルへ分割します。
    /// </summary>
    /// <param name="bits">分割前の連続ビット列。</param>
    /// <param name="leftBits">左チャネルへ割り当てるビット列。</param>
    /// <param name="rightBits">右チャネルへ割り当てるビット列。</param>
    private static void SplitBitsForStereo(bool[] bits, out bool[] leftBits, out bool[] rightBits)
    {
        var half = (bits.Length + 1) / 2;
        leftBits = new bool[half];
        rightBits = new bool[half];
        Array.Copy(bits, 0, leftBits, 0, Math.Min(half, bits.Length));
        if (bits.Length > half)
        {
            Array.Copy(bits, half, rightBits, 0, bits.Length - half);
        }
    }

    /// <summary>
    /// 左右チャネルのビット列を元の順序で再結合します。
    /// </summary>
    /// <param name="leftBits">左チャネル側ビット列。</param>
    /// <param name="rightBits">右チャネル側ビット列。</param>
    /// <param name="totalBits">結合後の総ビット数。</param>
    private static bool[] JoinStereoBits(bool[] leftBits, bool[] rightBits, int totalBits)
    {
        var joined = new bool[totalBits];
        var half = (totalBits + 1) / 2;
        Array.Copy(leftBits, 0, joined, 0, Math.Min(half, leftBits.Length));
        var rightCount = Math.Min(totalBits - half, rightBits.Length);
        if (rightCount > 0)
        {
            Array.Copy(rightBits, 0, joined, half, rightCount);
        }

        return joined;
    }

    /// <summary>
    /// 入力ファイルを固定長データブロックへ分割します。
    /// </summary>
    /// <param name="fileBytes">入力ファイル全体のバイト列。</param>
    private static List<DataBlock> SplitDataBlocks(byte[] fileBytes)
    {
        var blocks = new List<DataBlock>();
        for (var offset = 0; offset < fileBytes.Length; offset += DataBlockBytes)
        {
            var length = Math.Min(DataBlockBytes, fileBytes.Length - offset);
            var payload = new byte[length];
            Buffer.BlockCopy(fileBytes, offset, payload, 0, length);
            blocks.Add(new DataBlock(payload, length));
        }

        if (blocks.Count == 0)
        {
            blocks.Add(new DataBlock(Array.Empty<byte>(), 0));
        }

        return blocks;
    }

    /// <summary>
    /// バイト列を MSB ファーストのビット列へ展開します。
    /// </summary>
    /// <param name="bytes">変換元バイト列。</param>
    private static bool[] BytesToBitsMsb(byte[] bytes)
    {
        var bits = new bool[bytes.Length * 8];
        for (var i = 0; i < bytes.Length; i++)
        {
            for (var b = 0; b < 8; b++)
            {
                bits[(i * 8) + b] = ((bytes[i] >> (7 - b)) & 1) == 1;
            }
        }

        return bits;
    }

    /// <summary>
    /// MSB ファーストのビット列をバイト列へパックします。
    /// </summary>
    /// <param name="bits">変換元ビット列。</param>
    private static byte[] BitsToBytesMsb(bool[] bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
        {
            if (!bits[i])
            {
                continue;
            }

            bytes[i / 8] |= (byte)(1 << (7 - (i % 8)));
        }

        return bytes;
    }

    /// <summary>
    /// 同期済み候補位置からデータブロックを復号し、検証済み候補を返します。
    /// </summary>
    /// <param name="leftSamples">左チャネル入力サンプル列。</param>
    /// <param name="rightSamples">右チャネル入力サンプル列。</param>
    /// <param name="warpedCursor">入力読み取りカーソル（復号成功時に更新）。</param>
    /// <param name="logicalOffset">論理サンプル位置オフセット（復号成功時に更新）。</param>
    /// <param name="ofdm">復調に使う OFDM 生成器。</param>
    /// <param name="searchRadius">同期探索半径（サンプル）。</param>
    /// <param name="expectedBlockHash">期待するブロックハッシュ。</param>
    /// <param name="payloadLength">期待するペイロード長。</param>
    /// <param name="modulationScheme">データ部の変調方式。</param>
    /// <param name="tuning">復号探索・反復回数のチューニング値。</param>
    /// <param name="punctureRate">畳み込み符号のパンクチャ率。</param>
    /// <param name="wowLocked">WOW 補正パラメータが既知かどうか。</param>
    /// <param name="statusBoard">進捗・エラー率通知先。</param>
    /// <param name="diag">復号診断情報の出力先。</param>
    /// <param name="onSoftProgress">ソフト復号の進捗通知コールバック。</param>
    private static byte[] DecodeDataBlockSynced(
        Complex[] leftSamples,
        Complex[] rightSamples,
        ref int warpedCursor,
        ref long logicalOffset,
        OfdmGenerator ofdm,
        int searchRadius,
        byte[] expectedBlockHash,
        int payloadLength,
        ModulationScheme modulationScheme,
        DecodeRuntimeTuning tuning,
        ConvolutionalCode.PunctureRate punctureRate,
        bool wowLocked,
        CoreExecutionStatusBoard? statusBoard,
        out DataDecodeDiag diag,
        Action<double>? onSoftProgress = null)
    {
        var paddedLen = TurboPaddedLength(payloadLength + CrcBytes);
        var turboEncodedLength = (paddedLen / TurboEcc1024.DataUnitBytes) * TurboEcc1024.EncodedBytes;
        var convByteLength = GetConvolutionalEncodedLength(turboEncodedLength, punctureRate);
        var bitCount = convByteLength * 8;
        var useStereoSplit = ofdm.ChannelMode == ChannelMode.Stereo
            && rightSamples.Length == leftSamples.Length
            && rightSamples.Length > 0;
        var channelBitCount = useStereoSplit ? (bitCount + 1) / 2 : bitCount;
        var sampleCount = ofdm.SampleCountForBitCount(channelBitCount);
        var symbolSearchRadius = Math.Max(2, ofdm.SamplesPerOfdmSymbol / 8);
        var expectedStart = warpedCursor;
        var totalAttempts = 0;
        var hardMatchSucceeded = false;
        var softMatchSucceeded = false;
        var fallbackUsed = false;
        var startDeltaSamples = 0;

        var logical = logicalOffset;
        Exception? lastError = null;
        var step = Math.Max(1, ofdm.SamplesPerOfdmSymbol / 8);
        var softLlrAbortMeanAbs = wowLocked
            ? tuning.SoftLlrAbortMeanAbsWhenWowLocked
            : tuning.SoftLlrAbortMeanAbs;
        var maxFullAttempts = wowLocked
            ? tuning.DataSyncMaxFullAttemptsWhenWowLocked
            : tuning.DataSyncMaxFullAttempts;
        var maxSoftOnlyAttempts = wowLocked
            ? tuning.DataSyncMaxSoftOnlyAttemptsWhenWowLocked
            : tuning.DataSyncMaxSoftOnlyAttempts;

        bool TryAt(int start, int perSymbolRadius, out byte[] padded, out int endCursor, bool allowHardFallback = true)
        {
            padded = Array.Empty<byte>();
            endCursor = start;
            if (start < 0 || start + ofdm.SamplesPerOfdmSymbol > leftSamples.Length)
            {
                return false;
            }

            try
            {
                foreach (var useSoft in allowHardFallback ? new[] { true, false } : new[] { true })
                {
                    totalAttempts++;
                    var cursor = start;
                    byte[] candidate;
                    int end;
                    if (!useSoft)
                    {
                        bool[] bits;
                        if (perSymbolRadius <= 0)
                        {
                            if (start + sampleCount > leftSamples.Length)
                            {
                                continue;
                            }

                            bits = DemodulateDataBitsFixed(
                                ofdm, leftSamples, rightSamples, start, channelBitCount, bitCount, useStereoSplit, logical);
                            end = start + sampleCount;
                        }
                        else
                        {
                            bits = DemodulateDataBitsFromStream(
                                ofdm,
                                leftSamples,
                                rightSamples,
                                ref cursor,
                                channelBitCount,
                                bitCount,
                                useStereoSplit,
                                logical,
                                perSymbolRadius);
                            end = cursor;
                        }

                        var turboEncoded = ConvolutionalCode.Decode(
                            BitsToBytesMsb(bits),
                            turboEncodedLength,
                            out var hardConvMetrics,
                            terminated: true,
                            punctureRate: punctureRate);
                        statusBoard?.SetErrorRate(
                            hardConvMetrics.CorrectionRate * 100.0,
                            CoreFrameKind.Bd,
                            CoreEccDecoderKind.Viterbi);
                        candidate = DecodeTurboBlock(
                            turboEncoded,
                            paddedLen,
                            tuning.TurboIterationsMax,
                            out _,
                            onUnitCorrectionRate: rate =>
                                statusBoard?.SetErrorRate(
                                    rate * 100.0,
                                    CoreFrameKind.Bd,
                                    CoreEccDecoderKind.Turbo));
                    }
                    else
                    {
                        var radius = perSymbolRadius <= 0
                            ? Math.Max(8, ofdm.SamplesPerOfdmSymbol / 4)
                            : perSymbolRadius;
                        var qamLlrs = DemodulateDataSoftLlrsFromStream(
                            ofdm,
                            leftSamples,
                            rightSamples,
                            ref cursor,
                            channelBitCount,
                            bitCount,
                            useStereoSplit,
                            logical,
                            radius,
                            noiseVariance: 0.05,
                            modulationScheme,
                            statusBoard,
                            onSoftProgress);
                        end = cursor;
                        var turboEncoded = ConvolutionalCode.DecodeSoftToInfoLlrs(
                            qamLlrs,
                            turboEncodedLength,
                            out var infoLlrs,
                            out var softConvMetrics,
                            terminated: true,
                            punctureRate: punctureRate);
                        ClampLlrsInPlace(infoLlrs, 16.0);
                        statusBoard?.SetErrorRate(
                            softConvMetrics.CorrectionRate * 100.0,
                            CoreFrameKind.Bd,
                            CoreEccDecoderKind.Viterbi);

                        var meanAbs = MeanAbsLlrs(infoLlrs);
                        if (meanAbs < softLlrAbortMeanAbs)
                        {
                            continue;
                        }

                        var turboIterations = ResolveTurboIterations(infoLlrs, tuning);
                        var softCandidate = DecodeTurboBlockFromLlrs(
                            infoLlrs,
                            turboEncoded,
                            paddedLen,
                            turboIterations,
                            out _,
                            onUnitCorrectionRate: rate =>
                                statusBoard?.SetErrorRate(
                                    rate * 100.0,
                                    CoreFrameKind.Bd,
                                    CoreEccDecoderKind.Turbo));
                        if (IsDataBlockAcceptable(softCandidate, expectedBlockHash, payloadLength))
                        {
                            candidate = softCandidate;
                        }
                        else if (meanAbs >= tuning.SoftHardFallbackMinMeanAbsLlr)
                        {
                            var hardCandidate = DecodeTurboBlock(
                                turboEncoded,
                                paddedLen,
                                turboIterations,
                                out _,
                                onUnitCorrectionRate: rate =>
                                    statusBoard?.SetErrorRate(
                                        rate * 100.0,
                                        CoreFrameKind.Bd,
                                        CoreEccDecoderKind.Turbo));
                            candidate = PreferHashMatch(softCandidate, hardCandidate, expectedBlockHash, payloadLength);
                        }
                        else
                        {
                            candidate = softCandidate;
                        }
                    }

                    if (IsDataBlockAcceptable(candidate, expectedBlockHash, payloadLength))
                    {
                        if (useSoft)
                        {
                            softMatchSucceeded = true;
                        }
                        else
                        {
                            hardMatchSucceeded = true;
                        }

                        startDeltaSamples = start - expectedStart;
                        padded = candidate;
                        endCursor = end;
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                lastError = ex;
                return false;
            }
        }

        if (TryAt(warpedCursor, 0, out var hit, out var hitEnd))
        {
            warpedCursor = hitEnd;
            logicalOffset += sampleCount;
            diag = new DataDecodeDiag(
                totalAttempts,
                hardMatchSucceeded,
                softMatchSucceeded,
                fallbackUsed,
                startDeltaSamples);
            return hit;
        }

        var rankedStarts = new List<(int Start, double Score)>(32);
        for (var delta = 0; delta <= searchRadius; delta += step)
        {
            foreach (var start in delta == 0
                         ? new[] { warpedCursor }
                         : new[] { warpedCursor - delta, warpedCursor + delta })
            {
                if (start < 0 || start + (ofdm.SamplesPerOfdmSymbol * 2) > leftSamples.Length)
                {
                    continue;
                }

                if (delta == 0)
                {
                    // 期待位置そのものは先頭で TryAt 済みなので再評価しない。
                    continue;
                }

                var score = ofdm.ScoreLock(leftSamples, start, symbolCount: 2, useRightChannel: false);
                rankedStarts.Add((start, score));
            }
        }

        rankedStarts.Sort((a, b) => b.Score.CompareTo(a.Score));
        var fullAttempts = Math.Min(rankedStarts.Count, Math.Max(1, maxFullAttempts));
        for (var i = 0; i < fullAttempts; i++)
        {
            var allowHard = i < Math.Min(4, fullAttempts);
            if (TryAt(rankedStarts[i].Start, symbolSearchRadius, out hit, out hitEnd, allowHard))
            {
                warpedCursor = hitEnd;
                logicalOffset += sampleCount;
                diag = new DataDecodeDiag(
                    totalAttempts,
                    hardMatchSucceeded,
                    softMatchSucceeded,
                    fallbackUsed,
                    startDeltaSamples);
                return hit;
            }
        }

        var softOnlyLimit = fullAttempts + Math.Max(0, maxSoftOnlyAttempts);
        for (var i = fullAttempts; i < rankedStarts.Count && i < softOnlyLimit; i++)
        {
            if (TryAt(rankedStarts[i].Start, symbolSearchRadius, out hit, out hitEnd, allowHardFallback: false))
            {
                warpedCursor = hitEnd;
                logicalOffset += sampleCount;
                diag = new DataDecodeDiag(
                    totalAttempts,
                    hardMatchSucceeded,
                    softMatchSucceeded,
                    fallbackUsed,
                    startDeltaSamples);
                return hit;
            }
        }

        if (warpedCursor >= 0 && warpedCursor + ofdm.SamplesPerOfdmSymbol <= leftSamples.Length)
        {
            fallbackUsed = true;
            totalAttempts++;
            var cursor = warpedCursor;
            var qamLlrs = DemodulateDataSoftLlrsFromStream(
                ofdm,
                leftSamples,
                rightSamples,
                ref cursor,
                channelBitCount,
                bitCount,
                useStereoSplit,
                logicalOffset,
                Math.Max(2, ofdm.SamplesPerOfdmSymbol / 16),
                noiseVariance: 0.08,
                modulationScheme,
                statusBoard);
            warpedCursor = cursor;
            logicalOffset += sampleCount;
            var turboEncoded = ConvolutionalCode.DecodeSoftToInfoLlrs(
                qamLlrs,
                turboEncodedLength,
                out var infoLlrs,
                out var fallbackConvMetrics,
                terminated: true,
                punctureRate: punctureRate);
            ClampLlrsInPlace(infoLlrs, 16.0);
            _ = lastError;
            var turboIterations = ResolveTurboIterations(infoLlrs, tuning);
            statusBoard?.SetErrorRate(
                fallbackConvMetrics.CorrectionRate * 100.0,
                CoreFrameKind.Bd,
                CoreEccDecoderKind.Viterbi);
            var softCandidate = DecodeTurboBlockFromLlrs(
                infoLlrs,
                turboEncoded,
                paddedLen,
                turboIterations,
                out _,
                onUnitCorrectionRate: rate =>
                    statusBoard?.SetErrorRate(
                        rate * 100.0,
                        CoreFrameKind.Bd,
                        CoreEccDecoderKind.Turbo));
            if (IsDataBlockAcceptable(softCandidate, expectedBlockHash, payloadLength))
            {
                softMatchSucceeded = true;
                diag = new DataDecodeDiag(
                    totalAttempts,
                    hardMatchSucceeded,
                    softMatchSucceeded,
                    fallbackUsed,
                    startDeltaSamples);
                return softCandidate;
            }

            if (MeanAbsLlrs(infoLlrs) >= tuning.SoftHardFallbackMinMeanAbsLlr)
            {
                var hardCandidate = DecodeTurboBlock(
                    turboEncoded,
                    paddedLen,
                    turboIterations,
                    out _,
                    onUnitCorrectionRate: rate =>
                        statusBoard?.SetErrorRate(
                            rate * 100.0,
                            CoreFrameKind.Bd,
                            CoreEccDecoderKind.Turbo));
                var preferred = PreferHashMatch(softCandidate, hardCandidate, expectedBlockHash, payloadLength);
                if (IsDataBlockAcceptable(preferred, expectedBlockHash, payloadLength))
                {
                    if (ReferenceEquals(preferred, hardCandidate))
                    {
                        hardMatchSucceeded = true;
                    }
                    else
                    {
                        softMatchSucceeded = true;
                    }
                }

                diag = new DataDecodeDiag(
                    totalAttempts,
                    hardMatchSucceeded,
                    softMatchSucceeded,
                    fallbackUsed,
                    startDeltaSamples);
                return preferred;
            }

            diag = new DataDecodeDiag(
                totalAttempts,
                hardMatchSucceeded,
                softMatchSucceeded,
                fallbackUsed,
                startDeltaSamples);
            return softCandidate;
        }

        diag = new DataDecodeDiag(
            totalAttempts,
            hardMatchSucceeded,
            softMatchSucceeded,
            fallbackUsed,
            startDeltaSamples);
        throw new InvalidDataException("Data block sync failed.", lastError);
    }

    /// <summary>
    /// 固定開始位置からデータビットを復調します。
    /// </summary>
    /// <param name="ofdm">復調に使う OFDM 生成器。</param>
    /// <param name="leftSamples">左チャネル入力サンプル列。</param>
    /// <param name="rightSamples">右チャネル入力サンプル列。</param>
    /// <param name="start">復調開始サンプル位置。</param>
    /// <param name="channelBitCount">各チャネルで復調するビット数。</param>
    /// <param name="totalBitCount">左右結合後の総ビット数。</param>
    /// <param name="stereoSplit">左右チャネル分割復調を行うかどうか。</param>
    /// <param name="logical">論理サンプル位置。</param>
    private static bool[] DemodulateDataBitsFixed(
        OfdmGenerator ofdm,
        Complex[] leftSamples,
        Complex[] rightSamples,
        int start,
        int channelBitCount,
        int totalBitCount,
        bool stereoSplit,
        long logical)
    {
        var sliceL = new Complex[ofdm.SampleCountForBitCount(channelBitCount)];
        Array.Copy(leftSamples, start, sliceL, 0, sliceL.Length);
        if (!stereoSplit)
        {
            return ofdm.DemodulateBits(sliceL, totalBitCount, useRightChannel: false, logical);
        }

        var sliceR = new Complex[sliceL.Length];
        Array.Copy(rightSamples, start, sliceR, 0, sliceR.Length);
        var leftBits = ofdm.DemodulateBits(sliceL, channelBitCount, useRightChannel: false, logical);
        var rightBits = ofdm.DemodulateBits(sliceR, channelBitCount, useRightChannel: true, logical);
        return JoinStereoBits(leftBits, rightBits, totalBitCount);
    }

    /// <summary>
    /// ストリームから同期探索付きでデータビットを復調します。
    /// </summary>
    /// <param name="ofdm">復調に使う OFDM 生成器。</param>
    /// <param name="leftSamples">左チャネル入力サンプル列。</param>
    /// <param name="rightSamples">右チャネル入力サンプル列。</param>
    /// <param name="cursor">現在の読み取りカーソル（復調後に更新）。</param>
    /// <param name="channelBitCount">各チャネルで復調するビット数。</param>
    /// <param name="totalBitCount">左右結合後の総ビット数。</param>
    /// <param name="stereoSplit">左右チャネル分割復調を行うかどうか。</param>
    /// <param name="logical">論理サンプル位置。</param>
    /// <param name="searchRadius">シンボル開始位置探索半径。</param>
    private static bool[] DemodulateDataBitsFromStream(
        OfdmGenerator ofdm,
        Complex[] leftSamples,
        Complex[] rightSamples,
        ref int cursor,
        int channelBitCount,
        int totalBitCount,
        bool stereoSplit,
        long logical,
        int searchRadius)
    {
        if (!stereoSplit)
        {
            return ofdm.DemodulateBitsFromStream(
                leftSamples, ref cursor, totalBitCount, useRightChannel: false, logical, searchRadius);
        }

        var leftCursor = cursor;
        var rightCursor = cursor;
        var leftBits = ofdm.DemodulateBitsFromStream(
            leftSamples, ref leftCursor, channelBitCount, useRightChannel: false, logical, searchRadius);
        var rightBits = ofdm.DemodulateBitsFromStream(
            rightSamples, ref rightCursor, channelBitCount, useRightChannel: true, logical, searchRadius);
        cursor = leftCursor;
        return JoinStereoBits(leftBits, rightBits, totalBitCount);
    }

    /// <summary>
    /// ストリームから同期探索付きでソフト LLR を復調します。
    /// </summary>
    /// <param name="ofdm">復調に使う OFDM 生成器。</param>
    /// <param name="leftSamples">左チャネル入力サンプル列。</param>
    /// <param name="rightSamples">右チャネル入力サンプル列。</param>
    /// <param name="cursor">現在の読み取りカーソル（復調後に更新）。</param>
    /// <param name="channelBitCount">各チャネルで復調するビット数。</param>
    /// <param name="totalBitCount">左右結合後の総ビット数。</param>
    /// <param name="stereoSplit">左右チャネル分割復調を行うかどうか。</param>
    /// <param name="logical">論理サンプル位置。</param>
    /// <param name="searchRadius">シンボル開始位置探索半径。</param>
    /// <param name="noiseVariance">LLR 推定に使う雑音分散。</param>
    /// <param name="modulationScheme">データ部の変調方式。</param>
    /// <param name="statusBoard">IQ/FFT 可視化と進捗通知の出力先。</param>
    /// <param name="onBlockProgress">ブロック復調進捗通知コールバック。</param>
    private static double[] DemodulateDataSoftLlrsFromStream(
        OfdmGenerator ofdm,
        Complex[] leftSamples,
        Complex[] rightSamples,
        ref int cursor,
        int channelBitCount,
        int totalBitCount,
        bool stereoSplit,
        long logical,
        int searchRadius,
        double noiseVariance,
        ModulationScheme modulationScheme,
        CoreExecutionStatusBoard? statusBoard = null,
        Action<double>? onBlockProgress = null)
    {
        statusBoard?.SetFftStereoMode(stereoSplit);
        statusBoard?.BeginIqCapture(ofdm.ActiveSubcarriers, modulationScheme);

        Action<Complex[], byte[], int>? onIqFrame = statusBoard is null
            ? null
            : (symbols, groups, count) =>
                statusBoard.AppendIqFrame(symbols.AsSpan(0, count), groups.AsSpan(0, count));
        Action<Complex[], int>? onFftLeftFrame = statusBoard is null
            ? null
            : (spectrum, count) => statusBoard.SetFftFrame(
                spectrum.AsSpan(0, count),
                isRightChannel: false,
                sampleRate: ofdm.SampleRate);
        Action<Complex[], int>? onFftRightFrame = statusBoard is null
            ? null
            : (spectrum, count) => statusBoard.SetFftFrame(
                spectrum.AsSpan(0, count),
                isRightChannel: true,
                sampleRate: ofdm.SampleRate);

        // UI 更新頻度を抑え、復調処理のスループット低下を防ぐ。
        var progressClock = System.Diagnostics.Stopwatch.StartNew();
        Action<int, int>? onSymbolProgress = onBlockProgress is null
            ? null
            : (symbolIndex, symbolCount) =>
            {
                if (symbolCount <= 0)
                {
                    return;
                }

                var isLast = symbolIndex + 1 >= symbolCount;
                if (!isLast && progressClock.ElapsedMilliseconds < 33)
                {
                    return;
                }

                progressClock.Restart();
                onBlockProgress((symbolIndex + 1.0) / symbolCount);
            };

        if (!stereoSplit)
        {
            return ofdm.DemodulateSoftLlrsFromStream(
                leftSamples,
                ref cursor,
                totalBitCount,
                useRightChannel: false,
                logical,
                searchRadius,
                noiseVariance,
                onEqualizedDataSymbol: null,
                onEqualizedDataSymbolFrame: onIqFrame,
                onFftSymbolFrame: onFftLeftFrame,
                onOfdmSymbolProgress: onSymbolProgress);
        }

        var leftCursor = cursor;
        var rightCursor = cursor;
        var leftLlrs = ofdm.DemodulateSoftLlrsFromStream(
            leftSamples,
            ref leftCursor,
            channelBitCount,
            useRightChannel: false,
            logical,
            searchRadius,
            noiseVariance,
            onEqualizedDataSymbol: null,
            onEqualizedDataSymbolFrame: onIqFrame,
            onFftSymbolFrame: onFftLeftFrame,
            onOfdmSymbolProgress: onSymbolProgress);
        var rightLlrs = ofdm.DemodulateSoftLlrsFromStream(
            rightSamples,
            ref rightCursor,
            channelBitCount,
            useRightChannel: true,
            logical,
            searchRadius,
            noiseVariance,
            onEqualizedDataSymbol: null,
            onEqualizedDataSymbolFrame: onIqFrame,
            onFftSymbolFrame: onFftRightFrame,
            onOfdmSymbolProgress: null);
        cursor = leftCursor;
        var joined = new double[totalBitCount];
        var half = (totalBitCount + 1) / 2;
        Array.Copy(leftLlrs, 0, joined, 0, Math.Min(half, leftLlrs.Length));
        var rightCount = Math.Min(totalBitCount - half, rightLlrs.Length);
        if (rightCount > 0)
        {
            Array.Copy(rightLlrs, 0, joined, half, rightCount);
        }

        return joined;
    }

    /// <summary>
    /// LLR の絶対値を上限でクリップします。
    /// </summary>
    /// <param name="llrs">対象 LLR 配列。</param>
    /// <param name="maxAbs">絶対値の上限。</param>
    private static void ClampLlrsInPlace(double[] llrs, double maxAbs)
    {
        for (var i = 0; i < llrs.Length; i++)
        {
            if (llrs[i] > maxAbs)
            {
                llrs[i] = maxAbs;
            }
            else if (llrs[i] < -maxAbs)
            {
                llrs[i] = -maxAbs;
            }
        }
    }

    /// <summary>
    /// LLR 配列の平均絶対値を返します。
    /// </summary>
    /// <param name="llrs">対象 LLR スパン。</param>
    private static double MeanAbsLlrs(ReadOnlySpan<double> llrs)
    {
        if (llrs.Length == 0)
        {
            return 0.0;
        }

        var sum = 0.0;
        for (var i = 0; i < llrs.Length; i++)
        {
            sum += Math.Abs(llrs[i]);
        }

        return sum / llrs.Length;
    }

    /// <summary>
    /// LLR 分布から推定ソフトビット誤り率（%）を計算します。
    /// </summary>
    /// <param name="llrs">対象 LLR スパン。</param>
    private static double EstimateSoftBitErrorPercent(ReadOnlySpan<double> llrs)
    {
        if (llrs.Length == 0)
        {
            return 0.0;
        }

        var sum = 0.0;
        for (var i = 0; i < llrs.Length; i++)
        {
            var a = Math.Abs(llrs[i]);
            if (a >= 40.0)
            {
                continue;
            }

            sum += 1.0 / (1.0 + Math.Exp(a));
        }

        return 100.0 * sum / llrs.Length;
    }

    /// <summary>
    /// ハッシュ一致を優先してソフト候補とハード候補の採用値を選びます。
    /// </summary>
    /// <param name="softCandidate">ソフト復号候補。</param>
    /// <param name="hardCandidate">ハード復号候補。</param>
    /// <param name="expectedBlockHash">期待するブロックハッシュ。</param>
    /// <param name="payloadLength">期待するペイロード長。</param>
    private static byte[] PreferHashMatch(
        byte[] softCandidate,
        byte[] hardCandidate,
        byte[] expectedBlockHash,
        int payloadLength)
    {
        if (IsDataBlockAcceptable(softCandidate, expectedBlockHash, payloadLength))
        {
            return softCandidate;
        }

        if (IsDataBlockAcceptable(hardCandidate, expectedBlockHash, payloadLength))
        {
            return hardCandidate;
        }

        return softCandidate;
    }

    /// <summary>
    /// ペイロード長・ハッシュ・CRC でデータブロックの妥当性を検証します。
    /// </summary>
    /// <param name="candidate">検証対象ブロック。</param>
    /// <param name="expectedBlockHash">期待するブロックハッシュ。</param>
    /// <param name="payloadLength">期待するペイロード長。</param>
    private static bool IsDataBlockAcceptable(byte[] candidate, byte[] expectedBlockHash, int payloadLength)
    {
        var actualLen = Math.Clamp(payloadLength, 0, DataBlockBytes);
        if (candidate.Length < actualLen + CrcBytes)
        {
            return false;
        }

        var hash = Hash.ComputeSha256(candidate.AsSpan(0, actualLen));
        if (!hash.AsSpan().SequenceEqual(expectedBlockHash))
        {
            return false;
        }

        return Crc32.Matches(
            candidate.AsSpan(0, actualLen),
            candidate.AsSpan(actualLen, CrcBytes));
    }

    /// <summary>
    /// パディング済みデータを Turbo 符号化します。
    /// </summary>
    /// <param name="padded">Turbo 単位長に揃えた入力バイト列。</param>
    private static byte[] EncodeTurboBlock(byte[] padded)
    {
        if (padded.Length == 0 || padded.Length % TurboEcc1024.DataUnitBytes != 0)
        {
            throw new ArgumentException(
                $"Turbo payload length must be a positive multiple of {TurboEcc1024.DataUnitBytes}.",
                nameof(padded));
        }

        var unitCount = padded.Length / TurboEcc1024.DataUnitBytes;
        var output = new byte[unitCount * TurboEcc1024.EncodedBytes];
        var unit = new byte[TurboEcc1024.DataUnitBytes];
        var writeOffset = 0;
        for (var offset = 0; offset < padded.Length; offset += TurboEcc1024.DataUnitBytes)
        {
            Buffer.BlockCopy(padded, offset, unit, 0, TurboEcc1024.DataUnitBytes);
            var encoded = TurboEcc1024.Encode(unit);
            Buffer.BlockCopy(encoded, 0, output, writeOffset, encoded.Length);
            writeOffset += encoded.Length;
        }

        return output;
    }

    /// <summary>
    /// 入力 LLR の信頼度に応じて Turbo 反復回数を決定します。
    /// </summary>
    /// <param name="infoLlrs">畳み込み復号後の情報 LLR。</param>
    /// <param name="tuning">反復回数決定に使う閾値設定。</param>
    private static int ResolveTurboIterations(double[] infoLlrs, DecodeRuntimeTuning tuning)
    {
        var minIter = Math.Clamp(Math.Min(tuning.TurboIterationsMin, tuning.TurboIterationsMax), 1, 32);
        var maxIter = Math.Clamp(Math.Max(tuning.TurboIterationsMin, tuning.TurboIterationsMax), minIter, 32);
        if (infoLlrs.Length == 0)
        {
            return maxIter;
        }

        var sum = 0.0;
        for (var i = 0; i < infoLlrs.Length; i++)
        {
            sum += Math.Abs(infoLlrs[i]);
        }

        var meanAbs = sum / infoLlrs.Length;
        if (meanAbs <= tuning.TurboLowConfidenceLlr)
        {
            return maxIter;
        }

        if (meanAbs >= tuning.TurboHighConfidenceLlr)
        {
            return minIter;
        }

        var span = Math.Max(1e-9, tuning.TurboHighConfidenceLlr - tuning.TurboLowConfidenceLlr);
        var t = (meanAbs - tuning.TurboLowConfidenceLlr) / span;
        var value = maxIter - ((maxIter - minIter) * t);
        return (int)Math.Round(Math.Clamp(value, minIter, maxIter));
    }

    /// <summary>
    /// Turbo 符号語を復号し、平均訂正率を返します。
    /// </summary>
    /// <param name="turboEncoded">Turbo 符号語バイト列。</param>
    /// <param name="paddedLength">復号後のパディング済み長。</param>
    /// <param name="iterations">Turbo 反復回数。</param>
    /// <param name="meanCorrectionRate">単位平均の訂正率出力。</param>
    private static byte[] DecodeTurboBlock(
        byte[] turboEncoded,
        int paddedLength,
        int iterations,
        out double meanCorrectionRate,
        Action<double>? onUnitCorrectionRate = null)
    {
        var padded = new byte[paddedLength];
        var unitCount = paddedLength / TurboEcc1024.DataUnitBytes;
        var encoded = new byte[TurboEcc1024.EncodedBytes];
        var rateSum = 0.0;
        for (var i = 0; i < unitCount; i++)
        {
            Buffer.BlockCopy(turboEncoded, i * TurboEcc1024.EncodedBytes, encoded, 0, TurboEcc1024.EncodedBytes);
            var decoded = TurboEcc1024.Decode(
                encoded,
                out var metrics,
                iterations: iterations,
                channelReliability: 1.25);
            rateSum += metrics.CorrectionRate;
            onUnitCorrectionRate?.Invoke(metrics.CorrectionRate);
            Buffer.BlockCopy(decoded, 0, padded, i * TurboEcc1024.DataUnitBytes, TurboEcc1024.DataUnitBytes);
        }

        meanCorrectionRate = unitCount == 0 ? 0.0 : rateSum / unitCount;
        return padded;
    }

    /// <summary>
    /// Turbo 符号語を復号します。
    /// </summary>
    /// <param name="turboEncoded">Turbo 符号語バイト列。</param>
    /// <param name="paddedLength">復号後のパディング済み長。</param>
    /// <param name="iterations">Turbo 反復回数。</param>
    private static byte[] DecodeTurboBlock(byte[] turboEncoded, int paddedLength, int iterations)
    {
        return DecodeTurboBlock(turboEncoded, paddedLength, iterations, out _);
    }

    /// <summary>
    /// チャネル LLR を使って Turbo 復号し、平均訂正率を返します。
    /// </summary>
    /// <param name="infoLlrs">復号対象のチャネル LLR。</param>
    /// <param name="turboEncodedHard">LLR 不足時に使うハード符号語。</param>
    /// <param name="paddedLength">復号後のパディング済み長。</param>
    /// <param name="iterations">Turbo 反復回数。</param>
    /// <param name="meanCorrectionRate">単位平均の訂正率出力。</param>
    private static byte[] DecodeTurboBlockFromLlrs(
        double[] infoLlrs,
        byte[] turboEncodedHard,
        int paddedLength,
        int iterations,
        out double meanCorrectionRate,
        Action<double>? onUnitCorrectionRate = null)
    {
        var padded = new byte[paddedLength];
        var unitCount = paddedLength / TurboEcc1024.DataUnitBytes;
        var bitsPerUnit = TurboEcc1024.EncodedBits;
        var encoded = new byte[TurboEcc1024.EncodedBytes];
        var rateSum = 0.0;
        for (var i = 0; i < unitCount; i++)
        {
            var offset = i * bitsPerUnit;
            byte[] decoded;
            TurboEcc1024.DecodeMetrics metrics;
            if (infoLlrs.Length >= offset + bitsPerUnit)
            {
                try
                {
                    decoded = TurboEcc1024.DecodeFromChannelLlrs(
                        infoLlrs.AsSpan(offset, bitsPerUnit),
                        out metrics,
                        iterations: iterations);
                }
                catch
                {
                    Buffer.BlockCopy(turboEncodedHard, i * TurboEcc1024.EncodedBytes, encoded, 0, TurboEcc1024.EncodedBytes);
                    decoded = TurboEcc1024.Decode(
                        encoded,
                        out metrics,
                        iterations: iterations,
                        channelReliability: 1.25);
                }
            }
            else
            {
                Buffer.BlockCopy(turboEncodedHard, i * TurboEcc1024.EncodedBytes, encoded, 0, TurboEcc1024.EncodedBytes);
                decoded = TurboEcc1024.Decode(
                    encoded,
                    out metrics,
                    iterations: iterations,
                    channelReliability: 1.25);
            }

            rateSum += metrics.CorrectionRate;
            onUnitCorrectionRate?.Invoke(metrics.CorrectionRate);
            Buffer.BlockCopy(decoded, 0, padded, i * TurboEcc1024.DataUnitBytes, TurboEcc1024.DataUnitBytes);
        }

        meanCorrectionRate = unitCount == 0 ? 0.0 : rateSum / unitCount;
        return padded;
    }

    /// <summary>
    /// チャネル LLR を使って Turbo 復号します。
    /// </summary>
    /// <param name="infoLlrs">復号対象のチャネル LLR。</param>
    /// <param name="turboEncodedHard">LLR 不足時に使うハード符号語。</param>
    /// <param name="paddedLength">復号後のパディング済み長。</param>
    /// <param name="iterations">Turbo 反復回数。</param>
    private static byte[] DecodeTurboBlockFromLlrs(
        double[] infoLlrs,
        byte[] turboEncodedHard,
        int paddedLength,
        int iterations)
    {
        return DecodeTurboBlockFromLlrs(infoLlrs, turboEncodedHard, paddedLength, iterations, out _);
    }

    /// <summary>
    /// データブロック復号の診断メトリクスです。
    /// </summary>
    private readonly record struct DataDecodeDiag(
        int TotalAttempts,
        bool HardMatchSucceeded,
        bool SoftMatchSucceeded,
        bool FallbackUsed,
        int StartDeltaSamples);

    /// <summary>
    /// 指定ペイロード長を RS 符号化したときの符号語長を返します。
    /// </summary>
    /// <param name="payloadLength">入力ペイロード長。</param>
    private static int GetReedSolomonEncodedLength(int payloadLength)
    {
        var paddedLength = ((payloadLength + RsEcc256.DataUnitSize - 1) / RsEcc256.DataUnitSize) * RsEcc256.DataUnitSize;
        if (paddedLength == 0)
        {
            paddedLength = RsEcc256.DataUnitSize;
        }

        return (paddedLength / RsEcc256.DataUnitSize) * RsEcc256.EncodedUnitSize;
    }

    /// <summary>
    /// 指定入力長を畳み込み符号化したときのバイト長を返します。
    /// </summary>
    /// <param name="inputByteLength">入力バイト長。</param>
    /// <param name="punctureRate">パンクチャ率。</param>
    private static int GetConvolutionalEncodedLength(int inputByteLength, ConvolutionalCode.PunctureRate punctureRate)
    {
        var encodedBits = ConvolutionalCode.GetEncodedBitLength(inputByteLength * 8, terminated: true, punctureRate: punctureRate);
        return (encodedBits + 7) / 8;
    }

    /// <summary>
    /// 変調方式に対応するデータ部パンクチャ率を返します。
    /// </summary>
    /// <param name="modulationScheme">データ部の変調方式。</param>
    private static ConvolutionalCode.PunctureRate ResolveDataPunctureRate(ModulationScheme modulationScheme)
    {
        return modulationScheme switch
        {
            ModulationScheme.Bpsk => ConvolutionalCode.PunctureRate.Rate1_2,
            ModulationScheme.Qpsk => ConvolutionalCode.PunctureRate.Rate1_2,
            ModulationScheme.Qam16 => ConvolutionalCode.PunctureRate.Rate2_3,
            ModulationScheme.Qam64 => ConvolutionalCode.PunctureRate.Rate3_4,
            _ => throw new ArgumentOutOfRangeException(nameof(modulationScheme), modulationScheme, "Unsupported modulation scheme for puncture rate.")
        };
    }

    /// <summary>
    /// ブロックヘッダーからデータ部のサブキャリア数と変調方式を読み取ります。
    /// </summary>
    /// <param name="blockHeader">復号済みブロックヘッダー。</param>
    private static (int Subcarriers, ModulationScheme Modulation) ReadBlockDataModulation(byte[] blockHeader)
    {
        if (blockHeader.Length <= 9)
        {
            throw new InvalidDataException("Invalid block header: missing modulation fields.");
        }

        var subcarriers = blockHeader[8];
        if (subcarriers is not (8 or 16 or 24 or 32))
        {
            throw new InvalidDataException($"Invalid block header subcarrier count: {subcarriers}.");
        }

        var modulation = blockHeader[9] switch
        {
            1 => ModulationScheme.Bpsk,
            2 => ModulationScheme.Qpsk,
            3 => ModulationScheme.Qam16,
            4 => ModulationScheme.Qam64,
            _ => throw new InvalidDataException($"Invalid block header modulation mode: {blockHeader[9]}.")
        };
        return (subcarriers, modulation);
    }

    /// <summary>
    /// ペイロードを RS 符号化します。
    /// </summary>
    /// <param name="payload">入力ペイロード。</param>
    private static byte[] ApplyReedSolomon(byte[] payload)
    {
        var paddedLength = ((payload.Length + RsEcc256.DataUnitSize - 1) / RsEcc256.DataUnitSize) * RsEcc256.DataUnitSize;
        if (paddedLength == 0)
        {
            paddedLength = RsEcc256.DataUnitSize;
        }

        var padded = new byte[paddedLength];
        Buffer.BlockCopy(payload, 0, padded, 0, payload.Length);

        var unitCount = paddedLength / RsEcc256.DataUnitSize;
        var output = new byte[unitCount * RsEcc256.EncodedUnitSize];
        var unit = new byte[RsEcc256.DataUnitSize];
        var writeOffset = 0;
        for (var offset = 0; offset < padded.Length; offset += RsEcc256.DataUnitSize)
        {
            Buffer.BlockCopy(padded, offset, unit, 0, RsEcc256.DataUnitSize);
            var encoded = RsEcc256.Encode(unit);
            Buffer.BlockCopy(encoded, 0, output, writeOffset, encoded.Length);
            writeOffset += encoded.Length;
        }

        return output;
    }

    /// <summary>
    /// RS 符号語を復号します。
    /// </summary>
    /// <param name="encoded">RS 符号語バイト列。</param>
    private static byte[] ApplyReedSolomonDecode(byte[] encoded) =>
        ApplyReedSolomonDecode(encoded, out _);

    /// <summary>
    /// RS 符号語を復号し、復号メトリクスを返します。
    /// </summary>
    /// <param name="encoded">RS 符号語バイト列。</param>
    /// <param name="metrics">復号メトリクス出力。</param>
    private static byte[] ApplyReedSolomonDecode(byte[] encoded, out RsEcc256.DecodeMetrics metrics)
    {
        if (encoded.Length % RsEcc256.EncodedUnitSize != 0)
        {
            throw new ArgumentException("RS encoded length is invalid.", nameof(encoded));
        }

        var unitCount = encoded.Length / RsEcc256.EncodedUnitSize;
        var output = new byte[unitCount * RsEcc256.DataUnitSize];
        var unit = new byte[RsEcc256.EncodedUnitSize];
        var writeOffset = 0;
        long correctedBits = 0;
        long payloadBits = 0;
        for (var offset = 0; offset < encoded.Length; offset += RsEcc256.EncodedUnitSize)
        {
            Buffer.BlockCopy(encoded, offset, unit, 0, RsEcc256.EncodedUnitSize);
            var decoded = RsEcc256.Decode(unit, out var unitMetrics);
            correctedBits += unitMetrics.PayloadCorrectedBitCount;
            payloadBits += unitMetrics.PayloadBitLength;
            Buffer.BlockCopy(decoded, 0, output, writeOffset, decoded.Length);
            writeOffset += decoded.Length;
        }

        metrics = new RsEcc256.DecodeMetrics(
            PayloadCorrectedBitCount: (int)Math.Min(int.MaxValue, correctedBits),
            PayloadCorrectedByteCount: 0,
            CodewordCorrectedSymbolCount: 0,
            CodewordCorrectedBitCount: 0,
            PayloadBitLength: (int)Math.Min(int.MaxValue, payloadBits),
            PayloadCorrectionRate: payloadBits == 0 ? 0.0 : correctedBits / (double)payloadBits);

        return output;
    }

}

