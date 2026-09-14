using System.Numerics;

namespace Onta.Core;

public sealed partial class FileWavCodec
{
    private static void AppendModulatedDataBlock(
        List<Complex> leftPcm,
        List<Complex> rightPcm,
        OfdmGenerator ofdm,
        byte[] payload,
        int interleaveInitSeed,
        ConvolutionalCode.PunctureRate punctureRate)
    {
        var packed = PackDataBlockWithCrc(payload);
        var interleaved = ChannelBitInterleaver.InterleaveBytes(
            packed,
            ResolveBitInterleaveSeed(interleaveInitSeed));
        var turboEncoded = EncodeTurboBlock(interleaved);
        var convEncoded = ConvolutionalCode.Encode(turboEncoded, terminate: true, punctureRate: punctureRate);
        var bits = BytesToBitsMsb(convEncoded);
        if (ofdm.ChannelMode == ChannelMode.Stereo)
        {
            SplitBitsForStereo(bits, out var leftBits, out var rightBits);
            AppendPair(leftPcm, rightPcm, ofdm.ModulateBitStreams(leftBits, rightBits, leftPcm.Count, interleaveInitSeed));
        }
        else
        {
            AppendPair(leftPcm, rightPcm, ofdm.ModulateBits(bits, leftPcm.Count, interleaveInitSeed));
        }
    }

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

    private static int TurboPaddedLength(int contentLength)
    {
        var unit = TurboEcc1024.DataUnitBytes;
        return ((Math.Max(contentLength, 1) + unit - 1) / unit) * unit;
    }

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
        int interleaveInitSeed,
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
                                ofdm, leftSamples, rightSamples, start, channelBitCount, bitCount, useStereoSplit, logical, interleaveInitSeed);
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
                                perSymbolRadius,
                                interleaveInitSeed);
                            end = cursor;
                        }

                        var turboEncoded = ConvolutionalCode.Decode(
                            BitsToBytesMsb(bits),
                            turboEncodedLength,
                            out var hardConvMetrics,
                            terminated: true,
                            punctureRate: punctureRate);
                        candidate = DecodeTurboBlock(
                            turboEncoded,
                            paddedLen,
                            tuning.TurboIterationsMax,
                            out var hardTurboRate);
                        candidate = ChannelBitInterleaver.DeinterleaveBytes(
                            candidate,
                            ResolveBitInterleaveSeed(interleaveInitSeed));
                        if (IsDataBlockAcceptable(candidate, expectedBlockHash, payloadLength))
                        {
                            statusBoard?.SetErrorRate(
                                hardConvMetrics.CorrectionRate * 100.0,
                                CoreFrameKind.Bd,
                                CoreEccDecoderKind.Viterbi);
                            statusBoard?.SetErrorRate(
                                hardTurboRate * 100.0,
                                CoreFrameKind.Bd,
                                CoreEccDecoderKind.Turbo);
                        }
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
                            interleaveInitSeed,
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
                            out var softTurboRate);
                        softCandidate = ChannelBitInterleaver.DeinterleaveBytes(
                            softCandidate,
                            ResolveBitInterleaveSeed(interleaveInitSeed));
                        double publishTurboRate = softTurboRate;
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
                                out var fallbackTurboRate);
                            hardCandidate = ChannelBitInterleaver.DeinterleaveBytes(
                                hardCandidate,
                                ResolveBitInterleaveSeed(interleaveInitSeed));
                            publishTurboRate = fallbackTurboRate;
                            candidate = PreferHashMatch(softCandidate, hardCandidate, expectedBlockHash, payloadLength);
                        }
                        else
                        {
                            candidate = softCandidate;
                        }

                        if (IsDataBlockAcceptable(candidate, expectedBlockHash, payloadLength))
                        {
                            statusBoard?.SetErrorRate(
                                softConvMetrics.CorrectionRate * 100.0,
                                CoreFrameKind.Bd,
                                CoreEccDecoderKind.Viterbi);
                            statusBoard?.SetErrorRate(
                                publishTurboRate * 100.0,
                                CoreFrameKind.Bd,
                                CoreEccDecoderKind.Turbo);
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
                    // 既に直近カーソルで復号失敗済みの開始位置は除外する。
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
                interleaveInitSeed,
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
            var softCandidate = DecodeTurboBlockFromLlrs(
                infoLlrs,
                turboEncoded,
                paddedLen,
                turboIterations,
                out var fallbackTurboRate);
            softCandidate = ChannelBitInterleaver.DeinterleaveBytes(
                softCandidate,
                ResolveBitInterleaveSeed(interleaveInitSeed));
            double publishTurboRate = fallbackTurboRate;
            if (IsDataBlockAcceptable(softCandidate, expectedBlockHash, payloadLength))
            {
                softMatchSucceeded = true;
                statusBoard?.SetErrorRate(
                    fallbackConvMetrics.CorrectionRate * 100.0,
                    CoreFrameKind.Bd,
                    CoreEccDecoderKind.Viterbi);
                statusBoard?.SetErrorRate(
                    publishTurboRate * 100.0,
                    CoreFrameKind.Bd,
                    CoreEccDecoderKind.Turbo);
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
                    out var hardFallbackTurboRate);
                hardCandidate = ChannelBitInterleaver.DeinterleaveBytes(
                    hardCandidate,
                    ResolveBitInterleaveSeed(interleaveInitSeed));
                publishTurboRate = hardFallbackTurboRate;
                var preferred = PreferHashMatch(softCandidate, hardCandidate, expectedBlockHash, payloadLength);
                if (IsDataBlockAcceptable(preferred, expectedBlockHash, payloadLength))
                {
                    statusBoard?.SetErrorRate(
                        fallbackConvMetrics.CorrectionRate * 100.0,
                        CoreFrameKind.Bd,
                        CoreEccDecoderKind.Viterbi);
                    statusBoard?.SetErrorRate(
                        publishTurboRate * 100.0,
                        CoreFrameKind.Bd,
                        CoreEccDecoderKind.Turbo);
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

    private static bool[] DemodulateDataBitsFixed(
        OfdmGenerator ofdm,
        Complex[] leftSamples,
        Complex[] rightSamples,
        int start,
        int channelBitCount,
        int totalBitCount,
        bool stereoSplit,
        long logical,
        int interleaveInitSeed)
    {
        var sliceL = new Complex[ofdm.SampleCountForBitCount(channelBitCount)];
        Array.Copy(leftSamples, start, sliceL, 0, sliceL.Length);
        if (!stereoSplit)
        {
            return ofdm.DemodulateBits(sliceL, totalBitCount, useRightChannel: false, logical, interleaveInitSeed);
        }

        var sliceR = new Complex[sliceL.Length];
        Array.Copy(rightSamples, start, sliceR, 0, sliceR.Length);
        var leftBits = ofdm.DemodulateBits(sliceL, channelBitCount, useRightChannel: false, logical, interleaveInitSeed);
        var rightBits = ofdm.DemodulateBits(sliceR, channelBitCount, useRightChannel: true, logical, interleaveInitSeed);
        return JoinStereoBits(leftBits, rightBits, totalBitCount);
    }

    private static bool[] DemodulateDataBitsFromStream(
        OfdmGenerator ofdm,
        Complex[] leftSamples,
        Complex[] rightSamples,
        ref int cursor,
        int channelBitCount,
        int totalBitCount,
        bool stereoSplit,
        long logical,
        int searchRadius,
        int interleaveInitSeed)
    {
        if (!stereoSplit)
        {
            return ofdm.DemodulateBitsFromStream(
                leftSamples, ref cursor, totalBitCount, useRightChannel: false, logical, searchRadius, interleaveInitSeed);
        }

        var leftCursor = cursor;
        var rightCursor = cursor;
        var leftBits = ofdm.DemodulateBitsFromStream(
            leftSamples, ref leftCursor, channelBitCount, useRightChannel: false, logical, searchRadius, interleaveInitSeed);
        var rightBits = ofdm.DemodulateBitsFromStream(
            rightSamples, ref rightCursor, channelBitCount, useRightChannel: true, logical, searchRadius, interleaveInitSeed);
        cursor = leftCursor;
        return JoinStereoBits(leftBits, rightBits, totalBitCount);
    }

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
        int interleaveInitSeed,
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
            : (spectrum, count) => statusBoard.SetFftFrame(spectrum.AsSpan(0, count), isRightChannel: false);
        Action<Complex[], int>? onFftRightFrame = statusBoard is null
            ? null
            : (spectrum, count) => statusBoard.SetFftFrame(spectrum.AsSpan(0, count), isRightChannel: true);

        // 送信側の ~33ms ポーリングに合わせ、ブロック内進捗を間引き通知する。
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
                interleaveInitSeed,
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
            interleaveInitSeed,
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
            interleaveInitSeed,
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
    /// LLR の大きさからビット誤り確率を推定し、パーセントで返します（グラフ用）。
    /// </summary>
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

    private static byte[] DecodeTurboBlock(
        byte[] turboEncoded,
        int paddedLength,
        int iterations,
        out double meanCorrectionRate)
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
            Buffer.BlockCopy(decoded, 0, padded, i * TurboEcc1024.DataUnitBytes, TurboEcc1024.DataUnitBytes);
        }

        meanCorrectionRate = unitCount == 0 ? 0.0 : rateSum / unitCount;
        return padded;
    }

    private static byte[] DecodeTurboBlock(byte[] turboEncoded, int paddedLength, int iterations)
    {
        return DecodeTurboBlock(turboEncoded, paddedLength, iterations, out _);
    }

    /// <summary>
    /// Turbo情報LLRを単位ごとに復号し、失敗時はハード判定にフォールバックします。
    /// </summary>
    private static byte[] DecodeTurboBlockFromLlrs(
        double[] infoLlrs,
        byte[] turboEncodedHard,
        int paddedLength,
        int iterations,
        out double meanCorrectionRate)
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
            Buffer.BlockCopy(decoded, 0, padded, i * TurboEcc1024.DataUnitBytes, TurboEcc1024.DataUnitBytes);
        }

        meanCorrectionRate = unitCount == 0 ? 0.0 : rateSum / unitCount;
        return padded;
    }

    private static byte[] DecodeTurboBlockFromLlrs(
        double[] infoLlrs,
        byte[] turboEncodedHard,
        int paddedLength,
        int iterations)
    {
        return DecodeTurboBlockFromLlrs(infoLlrs, turboEncodedHard, paddedLength, iterations, out _);
    }

    private readonly record struct DataDecodeDiag(
        int TotalAttempts,
        bool HardMatchSucceeded,
        bool SoftMatchSucceeded,
        bool FallbackUsed,
        int StartDeltaSamples);

    private static int GetReedSolomonEncodedLength(int payloadLength)
    {
        var paddedLength = ((payloadLength + RsEcc256.DataUnitSize - 1) / RsEcc256.DataUnitSize) * RsEcc256.DataUnitSize;
        if (paddedLength == 0)
        {
            paddedLength = RsEcc256.DataUnitSize;
        }

        return (paddedLength / RsEcc256.DataUnitSize) * RsEcc256.EncodedUnitSize;
    }

    private static int GetConvolutionalEncodedLength(int inputByteLength, ConvolutionalCode.PunctureRate punctureRate)
    {
        var encodedBits = ConvolutionalCode.GetEncodedBitLength(inputByteLength * 8, terminated: true, punctureRate: punctureRate);
        return (encodedBits + 7) / 8;
    }

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

    private static byte[] ApplyReedSolomonDecode(byte[] encoded) =>
        ApplyReedSolomonDecode(encoded, out _);

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
