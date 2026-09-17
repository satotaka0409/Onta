using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;

namespace Onta.Core;

public sealed partial class FileWavCodec
{
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
            pilotSpacing: 8,
            stereoFrequencyShiftBins: _profile.StereoFrequencyShiftBins,
            sampleRate: _profile.SampleRate,
            randomSeed: _profile.RandomSeed,
            conceptualLeftBins: groupB,
            carrierGrid: grid);

        return new OfdmGenerator(config);
    }

    private static OfdmCarrierGrid AlternateCarrierGrid(OfdmCarrierGrid grid) =>
        grid == OfdmCarrierGrid.Sc8Family ? OfdmCarrierGrid.Sc24Family : OfdmCarrierGrid.Sc8Family;

    private byte[] DecodeHeaderPacketSyncedTryingGrids(
        ref OfdmGenerator headerOfdm,
        Complex[] leftSamples,
        Complex[] rightSamples,
        ref int warpedCursor,
        ref long logicalOffset,
        int payloadLength,
        byte[]? expectedPilot,
        int searchRadius,
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
                onSyncProgress,
                statusBoard,
                frameKind);
        }
    }

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
            _profile.FileHeaderUnmodulatedSamples);
    }

    private void AppendHeaderPackets(
        List<Complex> leftPcm,
        List<Complex> rightPcm,
        OfdmGenerator ofdm,
        byte[] leftHeaderBytes,
        byte[] rightHeaderBytes,
        int unmodulatedSamples)
    {
        _ = rightHeaderBytes;
        if (unmodulatedSamples > 0)
        {
            var unmodulated = ofdm.GenerateUnmodulated(unmodulatedSamples);
            AppendHeaderPair(leftPcm, rightPcm, unmodulated);
        }

        var leftBits = BytesToBitsMsb(
            ConvolutionalCode.Encode(
                ApplyReedSolomon(leftHeaderBytes),
                terminate: true,
                punctureRate: HeaderPunctureRate));

        var modulated = ofdm.ModulateBits(leftBits, leftPcm.Count);
        AppendHeaderPair(leftPcm, rightPcm, modulated);
    }

    private void AppendHeaderPair(List<Complex> leftPcm, List<Complex> rightPcm, (Complex[] Left, Complex[] Right) pair)
    {
        AppendPair(leftPcm, rightPcm, pair);
        if (_profile.ChannelMode == ChannelMode.Stereo && pair.Right.Length == 0)
        {
            rightPcm.AddRange(pair.Left);
        }
    }

    private int HeaderUnmodulatedSamplesFor(int packetLength) =>
        packetLength == FileHeaderBytes
            ? _profile.FileHeaderUnmodulatedSamples
            : _profile.BlockHeaderUnmodulatedSamples;

    private static void SkipHeaderUnmodulatedPreamble(
        Complex[] samples,
        ref int warpedCursor,
        ref long logicalOffset,
        int unmodulatedSamples)
    {
        warpedCursor = SkipSamples(samples, warpedCursor, unmodulatedSamples);
        logicalOffset += unmodulatedSamples;
    }

    private static byte[] DecodeHeaderPacketSynced(
        Complex[] leftSamples,
        Complex[] rightSamples,
        ref int warpedCursor,
        ref long logicalOffset,
        OfdmGenerator ofdm,
        int payloadLength,
        byte[]? expectedPilot,
        int searchRadius,
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
            out payload,
            out endCursor,
            out _);
    }

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
            var startA = expectedStart - radius;
            if (startA >= 0 && startA + sampleCount <= samples.Length)
            {
                scored.Add((startA, ofdm.ScoreLock(samples, startA, probeSymbols, useRightChannel)));
            }

            var startB = expectedStart + radius;
            if (startB >= 0 && startB + sampleCount <= samples.Length)
            {
                scored.Add((startB, ofdm.ScoreLock(samples, startB, probeSymbols, useRightChannel)));
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

    private static DateTime? ReadFileHeaderTimestampUtc(ReadOnlySpan<byte> fileHeader, int offset)
    {
        if (offset < 0 || fileHeader.Length < offset + 7)
        {
            return null;
        }

        var year = BinaryPrimitives.ReadUInt16BigEndian(fileHeader.Slice(offset, 2));
        var month = fileHeader[offset + 2];
        var day = fileHeader[offset + 3];
        var hour = fileHeader[offset + 4];
        var minute = fileHeader[offset + 5];
        var second = fileHeader[offset + 6];

        if (year is < 1900 or > 9999
            || month is < 1 or > 12
            || day is < 1 or > 31
            || hour > 23
            || minute > 59
            || second > 59)
        {
            return null;
        }

        try
        {
            var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
            return local.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

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


