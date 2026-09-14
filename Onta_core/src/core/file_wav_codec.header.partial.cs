using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;

namespace Onta.Core;

public sealed partial class FileWavCodec
{
    /// <summary>
    /// CreateHeaderOfdm を実行します。
    /// </summary>
    /// <param name="carrierGrid">carrierGrid を指定します。</param>
    private OfdmGenerator CreateHeaderOfdm(OfdmCarrierGrid? carrierGrid = null)
    {
        var groupB = OfdmConfig.ResolveGroupBLeftBins();
        // 文字化けしていたコメントを整理しました。
        var grid = carrierGrid ?? OfdmConfig.ResolveCarrierGrid(_profile.ActiveSubcarriers);
        var fftSize = OfdmConfig.ResolveFftSize(_profile.ActiveSubcarriers, _profile.ChannelMode);
        var config = new OfdmConfig(
            fftSize: fftSize,
            activeSubcarriers: groupB.Length,
            cyclicPrefixLength: _profile.HeaderCyclicPrefixLength,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Bpsk,
            channelMode: ChannelMode.Mono,
            enableFrequencyInterleaving: true,
            pilotSpacing: 8,
            stereoFrequencyShiftBins: _profile.StereoFrequencyShiftBins,
            sampleRate: _profile.SampleRate,
            frequencyInterleaveIntervalSymbols: 1,
            randomSeed: _profile.RandomSeed,
            conceptualLeftBins: groupB,
            carrierGrid: grid);

        return new OfdmGenerator(config);
    }

    /// <summary>
    /// AlternateCarrierGrid を実行します。
    /// </summary>
    /// <param name="grid">grid を指定します。</param>
    private static OfdmCarrierGrid AlternateCarrierGrid(OfdmCarrierGrid grid) =>
        grid == OfdmCarrierGrid.Sc8Family ? OfdmCarrierGrid.Sc24Family : OfdmCarrierGrid.Sc8Family;

    /// <summary>
    /// DecodeHeaderPacketSyncedTryingGrids を実行します。
    /// DecodeHeaderPacketSyncedTryingGrids を実行します。
    /// </summary>
    /// <param name="headerOfdm">headerOfdm を指定します。</param>
    /// <param name="leftSamples">leftSamples を指定します。</param>
    /// <param name="rightSamples">rightSamples を指定します。</param>
    /// <param name="warpedCursor">warpedCursor を指定します。</param>
    /// <param name="logicalOffset">logicalOffset を指定します。</param>
    /// <param name="payloadLength">payloadLength を指定します。</param>
    /// <param name="expectedPilot">expectedPilot を指定します。</param>
    /// <param name="searchRadius">searchRadius を指定します。</param>
    /// <param name="interleaveInitSeed">interleaveInitSeed を指定します。</param>
    /// <param name="onSyncProgress">onSyncProgress を指定します。</param>
    /// <param name="statusBoard">statusBoard を指定します。</param>
    /// <param name="frameKind">frameKind を指定します。</param>
    private byte[] DecodeHeaderPacketSyncedTryingGrids(
        ref OfdmGenerator headerOfdm,
        Complex[] leftSamples,
        Complex[] rightSamples,
        ref int warpedCursor,
        ref long logicalOffset,
        int payloadLength,
        byte[]? expectedPilot,
        int searchRadius,
        int interleaveInitSeed,
        Action<int, int>? onSyncProgress = null,
        CoreExecutionStatusBoard? statusBoard = null,
        CoreFrameKind frameKind = CoreFrameKind.Fh)
    {
        var savedCursor = warpedCursor;
        var savedLogical = logicalOffset;
        try
        {
            return DecodeHeaderPacketSynced(
                leftSamples,
                rightSamples,
                ref warpedCursor,
                ref logicalOffset,
                headerOfdm,
                payloadLength,
                expectedPilot,
                searchRadius,
                interleaveInitSeed,
                onSyncProgress,
                statusBoard,
                frameKind);
        }
        catch (InvalidDataException)
        {
            warpedCursor = savedCursor;
            logicalOffset = savedLogical;
            headerOfdm = CreateHeaderOfdm(AlternateCarrierGrid(headerOfdm.CarrierGrid));
            return DecodeHeaderPacketSynced(
                leftSamples,
                rightSamples,
                ref warpedCursor,
                ref logicalOffset,
                headerOfdm,
                payloadLength,
                expectedPilot,
                searchRadius,
                interleaveInitSeed,
                onSyncProgress,
                statusBoard,
                frameKind);
        }
    }

    /// <summary>
    /// AppendFileHeaderPacket を実行します。
    /// </summary>
    /// <param name="leftPcm">leftPcm を指定します。</param>
    /// <param name="rightPcm">rightPcm を指定します。</param>
    /// <param name="headerOfdm">headerOfdm を指定します。</param>
    /// <param name="fileHeader">fileHeader を指定します。</param>
    private void AppendFileHeaderPacket(
        List<Complex> leftPcm,
        List<Complex> rightPcm,
        OfdmGenerator headerOfdm,
        byte[] fileHeader)
    {
        AppendHeaderPackets(
            leftPcm,
            rightPcm,
            headerOfdm,
            fileHeader,
            fileHeader,
            _profile.FileHeaderUnmodulatedSamples,
            InterleaveInitSeedFileHeader);
    }

    /// <summary>
    /// AppendHeaderPackets を実行します。
    /// </summary>
    /// <param name="leftPcm">leftPcm を指定します。</param>
    /// <param name="rightPcm">rightPcm を指定します。</param>
    /// <param name="ofdm">ofdm を指定します。</param>
    /// <param name="leftHeaderBytes">leftHeaderBytes を指定します。</param>
    /// <param name="rightHeaderBytes">rightHeaderBytes を指定します。</param>
    /// <param name="unmodulatedSamples">unmodulatedSamples を指定します。</param>
    /// <param name="interleaveInitSeed">interleaveInitSeed を指定します。</param>
    /// <summary>
    /// AppendHeaderPackets を実行します。
    /// </summary>
    private void AppendHeaderPackets(
        List<Complex> leftPcm,
        List<Complex> rightPcm,
        OfdmGenerator ofdm,
        byte[] leftHeaderBytes,
        byte[] rightHeaderBytes,
        int unmodulatedSamples,
        int interleaveInitSeed)
    {
        _ = rightHeaderBytes;
        if (unmodulatedSamples > 0)
        {
            var unmodulated = ofdm.GenerateUnmodulated(unmodulatedSamples);
            AppendHeaderPair(leftPcm, rightPcm, unmodulated);
        }

        var leftBits = BytesToBitsMsb(
            ConvolutionalCode.Encode(
                ApplyReedSolomon(
                    ChannelBitInterleaver.InterleaveBytes(
                        leftHeaderBytes,
                        ResolveBitInterleaveSeed(interleaveInitSeed))),
                terminate: true,
                punctureRate: HeaderPunctureRate));

        var modulated = ofdm.ModulateBits(leftBits, leftPcm.Count, interleaveInitSeed);
        AppendHeaderPair(leftPcm, rightPcm, modulated);
    }

    /// <summary>
    /// AppendHeaderPair を実行します。
    /// </summary>
    /// <param name="leftPcm">leftPcm を指定します。</param>
    /// <param name="rightPcm">rightPcm を指定します。</param>
    /// <param name="pair">pair を指定します。</param>
    private void AppendHeaderPair(List<Complex> leftPcm, List<Complex> rightPcm, (Complex[] Left, Complex[] Right) pair)
    {
        AppendPair(leftPcm, rightPcm, pair);
        if (_profile.ChannelMode == ChannelMode.Stereo && pair.Right.Length == 0)
        {
            rightPcm.AddRange(pair.Left);
        }
    }

    /// <summary>
    /// HeaderUnmodulatedSamplesFor を実行します。
    /// </summary>
    /// <param name="packetLength">packetLength を指定します。</param>
    private int HeaderUnmodulatedSamplesFor(int packetLength) =>
        packetLength == FileHeaderBytes
            ? _profile.FileHeaderUnmodulatedSamples
            : _profile.BlockHeaderUnmodulatedSamples;

    /// <summary>
    /// SkipHeaderUnmodulatedPreamble を実行します。
    /// </summary>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="warpedCursor">warpedCursor を指定します。</param>
    /// <param name="logicalOffset">logicalOffset を指定します。</param>
    /// <param name="unmodulatedSamples">unmodulatedSamples を指定します。</param>
    /// <summary>
    /// SkipHeaderUnmodulatedPreamble を実行します。
    /// </summary>
    private static void SkipHeaderUnmodulatedPreamble(
        Complex[] samples,
        ref int warpedCursor,
        ref long logicalOffset,
        int unmodulatedSamples)
    {
        warpedCursor = SkipSamples(samples, warpedCursor, unmodulatedSamples);
        logicalOffset += unmodulatedSamples;
    }

    /// <summary>
    /// DecodeHeaderPacketSynced を実行します。
    /// </summary>
    /// <param name="leftSamples">leftSamples を指定します。</param>
    /// <param name="rightSamples">rightSamples を指定します。</param>
    /// <param name="warpedCursor">warpedCursor を指定します。</param>
    /// <param name="logicalOffset">logicalOffset を指定します。</param>
    /// <param name="ofdm">ofdm を指定します。</param>
    /// <param name="payloadLength">payloadLength を指定します。</param>
    /// <param name="expectedPilot">expectedPilot を指定します。</param>
    /// <param name="searchRadius">searchRadius を指定します。</param>
    /// <param name="interleaveInitSeed">interleaveInitSeed を指定します。</param>
    /// <param name="onSyncProgress">onSyncProgress を指定します。</param>
    /// <param name="statusBoard">statusBoard を指定します。</param>
    /// <param name="frameKind">frameKind を指定します。</param>
    private static byte[] DecodeHeaderPacketSynced(
        Complex[] leftSamples,
        Complex[] rightSamples,
        ref int warpedCursor,
        ref long logicalOffset,
        OfdmGenerator ofdm,
        int payloadLength,
        byte[]? expectedPilot,
        int searchRadius,
        int interleaveInitSeed,
        Action<int, int>? onSyncProgress = null,
        CoreExecutionStatusBoard? statusBoard = null,
        CoreFrameKind frameKind = CoreFrameKind.Fh)
    {
        var rsByteLength = GetReedSolomonEncodedLength(payloadLength);
        var convByteLength = GetConvolutionalEncodedLength(rsByteLength, HeaderPunctureRate);
        var bitCount = convByteLength * 8;
        var stereoSplit = false;
        var channelBitCount = stereoSplit ? (bitCount + 1) / 2 : bitCount;
        var sampleCount = ofdm.SampleCountForBitCount(channelBitCount);
        var symbolLength = ofdm.SamplesPerOfdmSymbol;
        var probeSymbols = Math.Clamp(sampleCount / symbolLength, 1, 4);

        onSyncProgress?.Invoke(0, 1);
        if (TryDecodeHeaderAt(
                leftSamples,
                rightSamples,
                warpedCursor,
                logicalOffset,
                ofdm,
                bitCount,
                channelBitCount,
                sampleCount,
                payloadLength,
                rsByteLength,
                expectedPilot,
                stereoSplit,
                perSymbolSearchRadius: 0,
                interleaveInitSeed,
                out var exactPayload,
                out var exactEnd,
                out _,
                statusBoard,
                frameKind))
        {
            warpedCursor = exactEnd;
            logicalOffset += sampleCount;
            onSyncProgress?.Invoke(1, 1);
            return exactPayload;
        }

        var candidateStarts = CollectSyncCandidates(
            leftSamples,
            warpedCursor,
            sampleCount,
            searchRadius,
            ofdm,
            probeSymbols,
            useRightChannel: false);

        Exception? lastError = null;
        for (var i = 0; i < candidateStarts.Count; i++)
        {
            var start = candidateStarts[i];
            onSyncProgress?.Invoke(i + 1, Math.Max(1, candidateStarts.Count));
            if (start == warpedCursor)
            {
                continue;
            }

            try
            {
                if (TryDecodeHeaderAt(
                        leftSamples,
                        rightSamples,
                        start,
                        logicalOffset,
                        ofdm,
                        bitCount,
                        channelBitCount,
                        sampleCount,
                        payloadLength,
                        rsByteLength,
                        expectedPilot,
                        stereoSplit,
                        perSymbolSearchRadius: Math.Min(2, Math.Max(0, symbolLength / 16)),
                        interleaveInitSeed,
                        out var payload,
                        out var endCursor,
                        out _,
                        statusBoard,
                        frameKind))
                {
                    warpedCursor = endCursor;
                    logicalOffset += sampleCount;
                    return payload;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidDataException(
            $"ヘッダーの復号に失敗しました。sample={warpedCursor}, payload={payloadLength}。"
            + "プリアンブル長のずれ、またはワウ・ノイズが大きい可能性があります。",
            lastError);
    }

    /// <summary>
    /// 指定位置からヘッダー復号を試行します。
    /// </summary>
    /// <param name="leftSamples">leftSamples を指定します。</param>
    /// <param name="rightSamples">rightSamples を指定します。</param>
    /// <param name="start">start を指定します。</param>
    /// <param name="logicalOffset">logicalOffset を指定します。</param>
    /// <param name="ofdm">ofdm を指定します。</param>
    /// <param name="totalBitCount">totalBitCount を指定します。</param>
    /// <param name="channelBitCount">channelBitCount を指定します。</param>
    /// <param name="sampleCount">sampleCount を指定します。</param>
    /// <param name="payloadLength">payloadLength を指定します。</param>
    /// <param name="rsByteLength">rsByteLength を指定します。</param>
    /// <param name="expectedPilot">expectedPilot を指定します。</param>
    /// <param name="stereoSplit">stereoSplit を指定します。</param>
    /// <param name="perSymbolSearchRadius">perSymbolSearchRadius を指定します。</param>
    /// <param name="interleaveInitSeed">interleaveInitSeed を指定します。</param>
    /// <param name="payload">payload を指定します。</param>
    /// <param name="endCursor">endCursor を指定します。</param>
    private static bool TryDecodeHeaderAt(
        Complex[] leftSamples,
        Complex[] rightSamples,
        int start,
        long logicalOffset,
        OfdmGenerator ofdm,
        int totalBitCount,
        int channelBitCount,
        int sampleCount,
        int payloadLength,
        int rsByteLength,
        byte[]? expectedPilot,
        bool stereoSplit,
        int perSymbolSearchRadius,
        int interleaveInitSeed,
        out byte[] payload,
        out int endCursor)
    {
        return TryDecodeHeaderAt(
            leftSamples,
            rightSamples,
            start,
            logicalOffset,
            ofdm,
            totalBitCount,
            channelBitCount,
            sampleCount,
            payloadLength,
            rsByteLength,
            expectedPilot,
            stereoSplit,
            perSymbolSearchRadius,
            interleaveInitSeed,
            out payload,
            out endCursor,
            out _);
    }

    /// <summary>
    /// TryDecodeHeaderAt を実行します。
    /// </summary>
    /// <param name="leftSamples">leftSamples を指定します。</param>
    /// <param name="rightSamples">rightSamples を指定します。</param>
    /// <param name="start">start を指定します。</param>
    /// <param name="logicalOffset">logicalOffset を指定します。</param>
    /// <param name="ofdm">ofdm を指定します。</param>
    /// <param name="totalBitCount">totalBitCount を指定します。</param>
    /// <param name="channelBitCount">channelBitCount を指定します。</param>
    /// <param name="sampleCount">sampleCount を指定します。</param>
    /// <param name="payloadLength">payloadLength を指定します。</param>
    /// <param name="rsByteLength">rsByteLength を指定します。</param>
    /// <param name="expectedPilot">expectedPilot を指定します。</param>
    /// <param name="stereoSplit">stereoSplit を指定します。</param>
    /// <param name="perSymbolSearchRadius">perSymbolSearchRadius を指定します。</param>
    /// <param name="interleaveInitSeed">interleaveInitSeed を指定します。</param>
    /// <param name="payload">payload を指定します。</param>
    /// <param name="endCursor">endCursor を指定します。</param>
    /// <param name="meanAbsLlr">meanAbsLlr を指定します。</param>
    /// <param name="statusBoard">statusBoard を指定します。</param>
    /// <param name="frameKind">frameKind を指定します。</param>
    private static bool TryDecodeHeaderAt(
        Complex[] leftSamples,
        Complex[] rightSamples,
        int start,
        long logicalOffset,
        OfdmGenerator ofdm,
        int totalBitCount,
        int channelBitCount,
        int sampleCount,
        int payloadLength,
        int rsByteLength,
        byte[]? expectedPilot,
        bool stereoSplit,
        int perSymbolSearchRadius,
        int interleaveInitSeed,
        out byte[] payload,
        out int endCursor,
        out double meanAbsLlr,
        CoreExecutionStatusBoard? statusBoard = null,
        CoreFrameKind frameKind = CoreFrameKind.Fh)
    {
        payload = Array.Empty<byte>();
        endCursor = start;
        meanAbsLlr = 0.0;
        if (start < 0 || start + sampleCount > leftSamples.Length)
        {
            return false;
        }

        if (stereoSplit && start + sampleCount > rightSamples.Length)
        {
            return false;
        }

        try
        {
            double[] llrs;
            if (perSymbolSearchRadius <= 0)
            {
                var cursor = start;
                llrs = DemodulateDataSoftLlrsFromStream(
                    ofdm,
                    leftSamples,
                    rightSamples,
                    ref cursor,
                    channelBitCount,
                    totalBitCount,
                    stereoSplit,
                    logicalOffset,
                    searchRadius: Math.Max(2, ofdm.SamplesPerOfdmSymbol / 16),
                    noiseVariance: 0.05,
                    interleaveInitSeed,
                    ModulationScheme.Bpsk);
                endCursor = cursor;
            }
            else
            {
                var cursor = start;
                llrs = DemodulateDataSoftLlrsFromStream(
                    ofdm,
                    leftSamples,
                    rightSamples,
                    ref cursor,
                    channelBitCount,
                    totalBitCount,
                    stereoSplit,
                    logicalOffset,
                    perSymbolSearchRadius,
                    noiseVariance: 0.05,
                    interleaveInitSeed,
                    ModulationScheme.Bpsk);
                endCursor = cursor;
            }

            var absSum = 0.0;
            for (var i = 0; i < llrs.Length; i++)
            {
                absSum += Math.Abs(llrs[i]);
            }

            meanAbsLlr = llrs.Length > 0 ? absSum / llrs.Length : 0.0;
            payload = DecodeHeaderFromSoftLlrs(
                llrs,
                payloadLength,
                rsByteLength,
                out var viterbiMetrics,
                out var rsMetrics,
                out _);
            payload = ChannelBitInterleaver.DeinterleaveBytes(
                payload,
                ResolveBitInterleaveSeed(interleaveInitSeed));
            if (expectedPilot is not null && !HeaderPrefixMatches(payload, expectedPilot))
            {
                return false;
            }

            statusBoard?.SetErrorRate(
                viterbiMetrics.CorrectionRate * 100.0,
                frameKind,
                CoreEccDecoderKind.Viterbi);
            statusBoard?.SetErrorRate(
                rsMetrics.PayloadCorrectionRate * 100.0,
                frameKind,
                CoreEccDecoderKind.ReedSolomon);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// DecodeHeaderFromSoftLlrs を実行します。
    /// </summary>
    /// <param name="llrs">llrs を指定します。</param>
    /// <param name="payloadLength">payloadLength を指定します。</param>
    /// <param name="rsByteLength">rsByteLength を指定します。</param>
    /// <param name="viterbiMetrics">viterbiMetrics を指定します。</param>
    /// <param name="rsMetrics">rsMetrics を指定します。</param>
    /// <param name="infoLlrs">infoLlrs を指定します。</param>
    private static byte[] DecodeHeaderFromSoftLlrs(
        double[] llrs,
        int payloadLength,
        int rsByteLength,
        out ConvolutionalCode.DecodeMetrics viterbiMetrics,
        out RsEcc256.DecodeMetrics rsMetrics,
        out double[] infoLlrs)
    {
        var rsEncoded = ConvolutionalCode.DecodeSoftToInfoLlrs(
            llrs,
            rsByteLength,
            out infoLlrs,
            out viterbiMetrics,
            terminated: true,
            punctureRate: HeaderPunctureRate);
        var paddedPayload = ApplyReedSolomonDecode(rsEncoded, out rsMetrics);
        var payload = new byte[payloadLength];
        Buffer.BlockCopy(paddedPayload, 0, payload, 0, payloadLength);
        return payload;
    }

    /// <summary>
    /// CollectSyncCandidates を実行します。
    /// </summary>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="expectedStart">expectedStart を指定します。</param>
    /// <param name="sampleCount">sampleCount を指定します。</param>
    /// <param name="searchRadius">searchRadius を指定します。</param>
    /// <param name="ofdm">ofdm を指定します。</param>
    /// <param name="probeSymbols">probeSymbols を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。</param>
    private static List<int> CollectSyncCandidates(
        Complex[] samples,
        int expectedStart,
        int sampleCount,
        int searchRadius,
        OfdmGenerator ofdm,
        int probeSymbols,
        bool useRightChannel)
    {
        var symbolLength = ofdm.SamplesPerOfdmSymbol;
        var step = Math.Max(1, symbolLength / 16);
        var unique = new List<int>();
        var seen = new HashSet<int>();

        void Add(int start)
        {
            if (start < 0 || start + sampleCount > samples.Length)
            {
                return;
            }

            if (seen.Add(start))
            {
                unique.Add(start);
            }
        }

        Add(expectedStart);
        Add(ofdm.FindBestSymbolStart(samples, expectedStart, Math.Min(searchRadius, symbolLength), useRightChannel));

        var scored = new List<(int Start, double Score)>();
        for (var radius = 0; radius <= searchRadius; radius += Math.Max(step, symbolLength / 4))
        {
            foreach (var start in new[] { expectedStart - radius, expectedStart + radius })
            {
                if (start < 0 || start + sampleCount > samples.Length)
                {
                    continue;
                }

                scored.Add((start, ofdm.ScoreLock(samples, start, probeSymbols, useRightChannel)));
            }
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var top = Math.Min(8, scored.Count);
        for (var i = 0; i < top; i++)
        {
            Add(scored[i].Start);
            Add(ofdm.FindBestSymbolStart(samples, scored[i].Start, step, useRightChannel));
        }

        return unique;
    }

    /// <summary>
    /// HeaderPrefixMatches を実行します。
    /// </summary>
    /// <param name="header">header を指定します。</param>
    /// <param name="expectedPilot">expectedPilot を指定します。</param>
    private static bool HeaderPrefixMatches(byte[] header, byte[] expectedPilot)
    {
        if (expectedPilot.Length != HeaderPilotBytes || HeaderVersion.Length != HeaderVersionBytes)
        {
            return false;
        }

        if (header.Length < HeaderPrefixBytes)
        {
            return false;
        }

        for (var i = 0; i < expectedPilot.Length; i++)
        {
            if (header[i] != expectedPilot[i])
            {
                return false;
            }
        }

        for (var i = 0; i < HeaderVersionBytes; i++)
        {
            if (header[HeaderPilotBytes + i] != HeaderVersion[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// ReadFileHeaderFileName を実行します。
    /// </summary>
    /// <param name="fileHeader">fileHeader を指定します。</param>
    private static string ReadFileHeaderFileName(ReadOnlySpan<byte> fileHeader)
    {
        if (fileHeader.Length < HeaderPrefixBytes + FileNameBytes)
        {
            return string.Empty;
        }

        var nameBytes = fileHeader.Slice(HeaderPrefixBytes, FileNameBytes);
        var end = nameBytes.IndexOf((byte)0);
        if (end < 0)
        {
            end = nameBytes.Length;
        }
        else if (end == 0)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(nameBytes[..end]).Trim();
    }

    /// <summary>
    /// BuildFileHeader を実行します。
    /// </summary>
    /// <param name="fileInfo">fileInfo を指定します。</param>
    /// <param name="fileSize">fileSize を指定します。</param>
    /// <param name="blockCount">blockCount を指定します。</param>
    /// <param name="fileHash">fileHash を指定します。</param>
    private static byte[] BuildFileHeader(FileInfo fileInfo, long fileSize, int blockCount, byte[] fileHash)
    {
        if (fileHash.Length != 64)
        {
            throw new ArgumentException("File hash must be SHA-512 (64 bytes).", nameof(fileHash));
        }

        var header = new byte[FileHeaderBytes];
        Buffer.BlockCopy(FileHeaderPilot, 0, header, 0, HeaderPilotBytes);
        Buffer.BlockCopy(HeaderVersion, 0, header, HeaderPilotBytes, HeaderVersionBytes);

        var nameBytes = Encoding.UTF8.GetBytes(fileInfo.Name);
        var copyLen = Math.Min(nameBytes.Length, FileNameBytes);
        Buffer.BlockCopy(nameBytes, 0, header, HeaderPrefixBytes, copyLen);
        Buffer.BlockCopy(fileHash, 0, header, HeaderPrefixBytes + FileNameBytes, 64);

        WriteFileAttributes(header.AsSpan(840, 20), fileInfo);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(860, 8), fileSize);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(868, 8), blockCount);
        var crc = Crc32.Compute(header.AsSpan(HeaderCrcDataOffset, FileHeaderBytes - HeaderCrcDataOffset - CrcBytes));
        Crc32.WriteBigEndian(header.AsSpan(FileHeaderBytes - CrcBytes, CrcBytes), crc);
        return header;
    }

    /// <summary>
    /// WriteFileAttributes を実行します。
    /// </summary>
    /// <param name="dest">dest を指定します。</param>
    /// <param name="fileInfo">fileInfo を指定します。</param>
    private static void WriteFileAttributes(Span<byte> dest, FileInfo fileInfo)
    {
        WriteTimestamp(dest.Slice(0, 7), fileInfo.CreationTime);
        WriteTimestamp(dest.Slice(7, 7), fileInfo.LastWriteTime);

        var attrs = new byte[6];
        byte flags = 0;
        if (fileInfo.IsReadOnly)
        {
            flags |= 0x01;
        }

        var ext = fileInfo.Extension;
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            flags |= 0x02;
        }

        attrs[0] = flags;
        attrs.CopyTo(dest.Slice(14, 6));
    }

    /// <summary>
    /// WriteTimestamp を実行します。
    /// </summary>
    /// <param name="dest">dest を指定します。</param>
    /// <param name="timestamp">timestamp を指定します。</param>
    private static void WriteTimestamp(Span<byte> dest, DateTime timestamp)
    {
        var local = timestamp.ToLocalTime();
        BinaryPrimitives.WriteUInt16BigEndian(dest, (ushort)local.Year);
        dest[2] = (byte)local.Month;
        dest[3] = (byte)local.Day;
        dest[4] = (byte)local.Hour;
        dest[5] = (byte)local.Minute;
        dest[6] = (byte)local.Second;
    }

    /// <summary>
    /// BuildBlockHeader を実行します。
    /// </summary>
    /// <param name="subcarriers">subcarriers を指定します。</param>
    /// <param name="modulationMode">modulationMode を指定します。</param>
    /// <param name="channelMode">channelMode を指定します。</param>
    /// <param name="blockIndex">blockIndex を指定します。</param>
    /// <param name="blockSize">blockSize を指定します。</param>
    /// <param name="blockHash">blockHash を指定します。</param>
    /// <param name="fileHash">fileHash を指定します。</param>
    private static byte[] BuildBlockHeader(
        byte subcarriers,
        byte modulationMode,
        byte channelMode,
        long blockIndex,
        int blockSize,
        byte[] blockHash,
        byte[] fileHash)
    {
        if (blockHash.Length != 32)
        {
            throw new ArgumentException("Block hash must be SHA-256 (32 bytes).", nameof(blockHash));
        }

        if (fileHash.Length != 64)
        {
            throw new ArgumentException("File hash must be SHA-512 (64 bytes).", nameof(fileHash));
        }

        var header = new byte[BlockHeaderBytes];
        Buffer.BlockCopy(BlockHeaderPilot, 0, header, 0, HeaderPilotBytes);
        Buffer.BlockCopy(HeaderVersion, 0, header, HeaderPilotBytes, HeaderVersionBytes);
        header[8] = subcarriers;
        header[9] = modulationMode;
        header[10] = channelMode;
        header[11] = 0;
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(12, 8), blockIndex);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20, 4), blockSize);
        Buffer.BlockCopy(blockHash, 0, header, 24, 32);
        Buffer.BlockCopy(fileHash, 0, header, 56, 64);
        var crc = Crc32.Compute(header.AsSpan(HeaderCrcDataOffset, BlockHeaderBytes - HeaderCrcDataOffset - CrcBytes));
        Crc32.WriteBigEndian(header.AsSpan(BlockHeaderBytes - CrcBytes, CrcBytes), crc);
        return header;
    }

    /// <summary>
    /// EnsureHeaderCrc を実行します。
    /// </summary>
    /// <param name="header">header を指定します。</param>
    /// <param name="headerName">headerName を指定します。</param>
    private static void EnsureHeaderCrc(byte[] header, string headerName)
    {
        if (header.Length < HeaderCrcDataOffset + CrcBytes)
        {
            throw new InvalidDataException($"Invalid {headerName}: too short for CRC.");
        }

        var dataLen = header.Length - HeaderCrcDataOffset - CrcBytes;
        if (!Crc32.Matches(header.AsSpan(HeaderCrcDataOffset, dataLen), header.AsSpan(header.Length - CrcBytes, CrcBytes)))
        {
            throw new InvalidDataException($"Invalid {headerName}: CRC-32 mismatch.");
        }
    }

    /// <summary>
    /// EnsureHeaderPilot を実行します。
    /// </summary>
    /// <param name="header">header を指定します。</param>
    /// <param name="expectedPilot">expectedPilot を指定します。</param>
    /// <param name="headerName">headerName を指定します。</param>
    private static void EnsureHeaderPilot(byte[] header, byte[] expectedPilot, string headerName)
    {
        if (expectedPilot.Length != HeaderPilotBytes || HeaderVersion.Length != HeaderVersionBytes)
        {
            throw new InvalidDataException($"Invalid {headerName}: pilot/version definition mismatch.");
        }

        if (header.Length < HeaderPrefixBytes)
        {
            throw new InvalidDataException($"Invalid {headerName}: too short for pilot.");
        }

        for (var i = 0; i < expectedPilot.Length; i++)
        {
            if (header[i] != expectedPilot[i])
            {
                throw new InvalidDataException($"Invalid {headerName} pilot at byte {i}.");
            }
        }

        for (var i = 0; i < HeaderVersionBytes; i++)
        {
            if (header[HeaderPilotBytes + i] != HeaderVersion[i])
            {
                throw new InvalidDataException($"Invalid {headerName} version at byte {HeaderPilotBytes + i}.");
            }
        }
    }
}

