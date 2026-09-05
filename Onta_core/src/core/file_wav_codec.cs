using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Onta.Core;

/// <summary>
/// ファイル ↔ WAV ラウンドトリップ用の OFDM プロファイルです。
/// </summary>
public sealed record FileWavCodecProfile(
    int FftSize,
    int CyclicPrefixLength,
    int ActiveSubcarriers,
    ModulationScheme ModulationScheme,
    int SampleRate = 44100,
    double SamplePeak = 0.8,
    int RandomSeed = 20260904,
    int StereoFrequencyShiftBins = 1,
    ChannelMode ChannelMode = ChannelMode.Stereo)
{
    public byte ModulationModeByte => (byte)ModulationScheme;
    public byte ChannelModeByte => (byte)ChannelMode;
    public int LeadingSilenceSamples => SampleRate / 10;
    public int TrailingSilenceSamples => SampleRate / 10;
    /// <summary>全体先頭の無変調プリアンブル（2 秒）。</summary>
    public int UnmodulatedPreambleSamples => SampleRate * 2;
    /// <summary>ファイルヘッダー先頭の無変調区間（1 秒）。</summary>
    public int FileHeaderUnmodulatedSamples => SampleRate;
    /// <summary>ブロックヘッダー先頭の無変調区間（0.5 秒）。</summary>
    public int BlockHeaderUnmodulatedSamples => SampleRate / 2;
}

/// <summary>
/// 入力ファイルの WAV 符号化と、WAV からの復元を行うコーデックです。
/// WAV は 2ch ステレオ PCM（L=左OFDM実信号, R=右OFDM実信号）です。
/// </summary>
public sealed class FileWavCodec
{
    private const int FileHeaderBytes = 876;
    private const int FileNameBytes = 768;
    private const int BlockHeaderBytes = 120;
    private const int HeaderPilotBytes = 8;
    private static readonly byte[] FileHeaderPilot = [0xF0, 0xE1, 0xD2, 0xC3, 0xB4, 0xA5, 0x96, 0x87];
    private static readonly byte[] BlockHeaderPilot = [0x0F, 0x1E, 0x2D, 0x3C, 0x4B, 0x5A, 0x69, 0x78];
    private const int DataBlockBytes = 4096;
    private const string DataTraceEnvVar = "ONTA_TRACE_DATA_ERRORS";

    private readonly FileWavCodecProfile _profile;

    public FileWavCodec(FileWavCodecProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <summary>
    /// ファイルを符号化して WAV に書き出し、復号結果のバイト列を返します。
    /// </summary>
    public byte[] EncodeDecodeRoundTrip(string inputPath, string wavPath, string? restoredPath = null)
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("入力ファイルが見つかりません。", inputPath);
        }

        var originalBytes = File.ReadAllBytes(inputPath);
        var fileInfo = new FileInfo(inputPath);
        var (leftSamples, rightSamples) = EncodeFileToSamples(originalBytes, fileInfo);
        WavWriter.WriteStereo16(wavPath, _profile.SampleRate, leftSamples, rightSamples, _profile.SamplePeak);

        var decodedBytes = DecodeWavToFileBytes(wavPath);
        if (restoredPath is not null)
        {
            File.WriteAllBytes(restoredPath, decodedBytes);
        }

        return decodedBytes;
    }

    public (Complex[] Left, Complex[] Right) EncodeFileToSamples(byte[] fileBytes, FileInfo fileInfo)
    {
        var fileHash = Hash.ComputeSha512(fileBytes);
        var blocks = SplitDataBlocks(fileBytes);
        var blockHeadersForCatalog = new byte[blocks.Count][];
        var blockHeadersForData = new byte[blocks.Count][];

        for (var i = 0; i < blocks.Count; i++)
        {
            blockHeadersForCatalog[i] = BuildBlockHeader(
                subcarriers: 0,
                modulationMode: 0,
                channelMode: 0,
                blockIndex: i,
                blockSize: blocks[i].PayloadLength,
                blockHash: Hash.ComputeSha256(blocks[i].Payload),
                fileHash: fileHash);

            blockHeadersForData[i] = BuildBlockHeader(
                subcarriers: (byte)_profile.ActiveSubcarriers,
                modulationMode: _profile.ModulationModeByte,
                channelMode: _profile.ChannelModeByte,
                blockIndex: i,
                blockSize: blocks[i].PayloadLength,
                blockHash: Hash.ComputeSha256(blocks[i].Payload),
                fileHash: fileHash);
        }

        var fileHeader = BuildFileHeader(fileInfo, fileBytes.Length, blocks.Count, fileHash);
        var (leftHeaderPackets, rightHeaderPackets) = BuildStereoHeaderPackets(fileHeader, blockHeadersForCatalog);
        var headerOfdm = CreateHeaderOfdm();
        var dataOfdm = CreateDataOfdm();
        var leftPcm = new List<Complex>(1 << 20);
        var rightPcm = new List<Complex>(1 << 20);

        AppendSilence(leftPcm, rightPcm, _profile.LeadingSilenceSamples);
        AppendPair(leftPcm, rightPcm, headerOfdm.GenerateUnmodulated(_profile.UnmodulatedPreambleSamples));

        for (var i = 0; i < leftHeaderPackets.Count; i++)
        {
            AppendHeaderPackets(
                leftPcm,
                rightPcm,
                headerOfdm,
                leftHeaderPackets[i],
                rightHeaderPackets[i],
                CatalogHeaderUnmodulatedSamplesFor(leftHeaderPackets[i].Length));
        }

        for (var i = 0; i < blocks.Count; i++)
        {
            AppendHeaderPackets(
                leftPcm,
                rightPcm,
                headerOfdm,
                blockHeadersForData[i],
                blockHeadersForData[i],
                _profile.BlockHeaderUnmodulatedSamples);
            AppendModulatedDataBlock(leftPcm, rightPcm, dataOfdm, blocks[i].PaddedPayload);
        }

        for (var i = 0; i < leftHeaderPackets.Count; i++)
        {
            AppendHeaderPackets(
                leftPcm,
                rightPcm,
                headerOfdm,
                leftHeaderPackets[i],
                rightHeaderPackets[i],
                CatalogHeaderUnmodulatedSamplesFor(leftHeaderPackets[i].Length));
        }

        AppendSilence(leftPcm, rightPcm, _profile.TrailingSilenceSamples);
        return (leftPcm.ToArray(), rightPcm.ToArray());
    }

    public byte[] DecodeWavToFileBytes(
        string wavPath,
        bool correctWow = true,
        (double Amount, double WowPhase, double FlutterPhase)? wowParams = null)
    {
        var (leftSamples, rightSamples) = WavReader.ReadStereo16(wavPath);
        var headerOfdm = CreateHeaderOfdm();
        var dataOfdm = CreateDataOfdm();
        // ワウ検出時のみ、パイロット推定に基づく時間補正を適用する。
        if (wowParams is { } known)
        {
            leftSamples = headerOfdm.CorrectWowFlutterWithParams(
                leftSamples,
                known.Amount,
                known.WowPhase,
                known.FlutterPhase);
            if (rightSamples.Length == leftSamples.Length)
            {
                rightSamples = headerOfdm.CorrectWowFlutterWithParams(
                    rightSamples,
                    known.Amount,
                    known.WowPhase,
                    known.FlutterPhase);
            }
        }
        else if (correctWow)
        {
            leftSamples = headerOfdm.CorrectWowFlutter(
                leftSamples,
                useRightChannel: false,
                analysisStartSample: _profile.LeadingSilenceSamples,
                analysisSampleCount: _profile.UnmodulatedPreambleSamples);
            if (rightSamples.Length == leftSamples.Length)
            {
                rightSamples = headerOfdm.CorrectWowFlutter(
                    rightSamples,
                    useRightChannel: true,
                    analysisStartSample: _profile.LeadingSilenceSamples,
                    analysisSampleCount: _profile.UnmodulatedPreambleSamples);
            }
        }

        // warpedCursor: 実波形上の読み位置 / logicalOffset: 符号化時のサンプル時刻（インターリーブ用）
        var warpedCursor = 0;
        warpedCursor = SkipSamples(leftSamples, warpedCursor, _profile.LeadingSilenceSamples);
        warpedCursor = SkipSamples(leftSamples, warpedCursor, _profile.UnmodulatedPreambleSamples);
        var logicalOffset = (long)warpedCursor;

        var coarseRadius = Math.Max(headerOfdm.SamplesPerOfdmSymbol * 8, _profile.SampleRate / 50);
        var fineRadius = Math.Max(headerOfdm.SamplesPerOfdmSymbol * 2, _profile.SampleRate / 200);

        // 復号は L チャンネルを使用（R は冗長・逆順ヘッダー）。
        // FH 先頭 1 秒 / BH 先頭 0.5 秒の無変調を、パケット復調前に読み飛ばす。
        SkipHeaderUnmodulatedPreamble(
            leftSamples,
            ref warpedCursor,
            ref logicalOffset,
            _profile.FileHeaderUnmodulatedSamples);
        var fileHeader = DecodeHeaderPacketSynced(
            leftSamples,
            ref warpedCursor,
            ref logicalOffset,
            headerOfdm,
            FileHeaderBytes,
            FileHeaderPilot,
            coarseRadius,
            useRightChannel: false);
        var fileSize = BinaryPrimitives.ReadInt64BigEndian(fileHeader.AsSpan(860, 8));
        var blockCount = (int)BinaryPrimitives.ReadInt64BigEndian(fileHeader.AsSpan(868, 8));
        if (fileSize < 0 || blockCount < 0)
        {
            throw new InvalidDataException("Invalid file header size/block count.");
        }

        var (leftCatalog, _) = BuildStereoHeaderPackets(fileHeader, CreatePlaceholderBlockHeaders(blockCount));
        for (var i = 1; i < leftCatalog.Count; i++)
        {
            var packetLength = leftCatalog[i].Length;
            var expectedPilot = packetLength == FileHeaderBytes
                ? FileHeaderPilot
                : packetLength == BlockHeaderBytes
                    ? BlockHeaderPilot
                    : null;
            SkipHeaderUnmodulatedPreamble(
                leftSamples,
                ref warpedCursor,
                ref logicalOffset,
                CatalogHeaderUnmodulatedSamplesFor(packetLength));
            _ = DecodeHeaderPacketSynced(
                leftSamples,
                ref warpedCursor,
                ref logicalOffset,
                headerOfdm,
                packetLength,
                expectedPilot,
                fineRadius,
                useRightChannel: false);
        }

        var output = new byte[fileSize];
        var writeOffset = 0;
        var traceDataErrors = string.Equals(
            Environment.GetEnvironmentVariable(DataTraceEnvVar),
            "1",
            StringComparison.Ordinal);
        for (var i = 0; i < blockCount; i++)
        {
            SkipHeaderUnmodulatedPreamble(
                leftSamples,
                ref warpedCursor,
                ref logicalOffset,
                _profile.BlockHeaderUnmodulatedSamples);
            var blockHeader = DecodeHeaderPacketSynced(
                leftSamples,
                ref warpedCursor,
                ref logicalOffset,
                headerOfdm,
                BlockHeaderBytes,
                BlockHeaderPilot,
                fineRadius,
                useRightChannel: false);
            var blockIndex = BinaryPrimitives.ReadInt64BigEndian(blockHeader.AsSpan(12, 8));
            var blockSize = BinaryPrimitives.ReadInt32BigEndian(blockHeader.AsSpan(20, 4));
            if (blockIndex != i)
            {
                throw new InvalidDataException($"Unexpected block index {blockIndex}, expected {i}.");
            }

            if (blockSize < 0 || blockSize > DataBlockBytes)
            {
                throw new InvalidDataException($"Invalid block size {blockSize}.");
            }

            var padded = DecodeDataBlockSynced(
                leftSamples,
                rightSamples,
                ref warpedCursor,
                ref logicalOffset,
                dataOfdm,
                Math.Max(dataOfdm.SamplesPerOfdmSymbol * 2, _profile.SampleRate / 200),
                expectedBlockHash: blockHeader.AsSpan(24, 32).ToArray(),
                payloadLength: blockSize,
                out var diag);
            if (traceDataErrors)
            {
                Console.WriteLine(
                    $"[DATA-DIAG] block={i} size={blockSize} hardOK={diag.HardMatchSucceeded} softOK={diag.SoftMatchSucceeded} fallback={diag.FallbackUsed} startDelta={diag.StartDeltaSamples} attempts={diag.TotalAttempts}");
            }
            if (writeOffset + blockSize > output.Length)
            {
                throw new InvalidDataException("Decoded payload exceeds file size.");
            }

            Buffer.BlockCopy(padded, 0, output, writeOffset, blockSize);
            writeOffset += blockSize;
        }

        if (writeOffset != fileSize)
        {
            throw new InvalidDataException($"Decoded size mismatch: got {writeOffset}, expected {fileSize}.");
        }

        return output;
    }

    private static byte[][] CreatePlaceholderBlockHeaders(int blockCount)
    {
        var headers = new byte[blockCount][];
        for (var i = 0; i < blockCount; i++)
        {
            headers[i] = new byte[BlockHeaderBytes];
        }

        return headers;
    }

    private OfdmGenerator CreateHeaderOfdm()
    {
        // modulation.mdc: FH/BH は 9 サブキャリア（1 パイロット）+ BPSK 固定。
        var config = new OfdmConfig(
            fftSize: 32,
            activeSubcarriers: 9,
            cyclicPrefixLength: 8,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Bpsk,
            channelMode: _profile.ChannelMode,
            enableFrequencyInterleaving: true,
            pilotSpacing: 9,
            stereoFrequencyShiftBins: _profile.StereoFrequencyShiftBins,
            sampleRate: _profile.SampleRate,
            frequencyInterleaveIntervalSeconds: 1.0,
            randomSeed: _profile.RandomSeed);

        return new OfdmGenerator(config);
    }

    private OfdmGenerator CreateDataOfdm()
    {
        var config = new OfdmConfig(
            fftSize: _profile.FftSize,
            activeSubcarriers: _profile.ActiveSubcarriers,
            cyclicPrefixLength: _profile.CyclicPrefixLength,
            ofdmSymbolCount: 1,
            modulationScheme: _profile.ModulationScheme,
            channelMode: _profile.ChannelMode,
            enableFrequencyInterleaving: true,
            pilotSpacing: 9,
            stereoFrequencyShiftBins: _profile.StereoFrequencyShiftBins,
            sampleRate: _profile.SampleRate,
            frequencyInterleaveIntervalSeconds: 1.0,
            randomSeed: _profile.RandomSeed);

        return new OfdmGenerator(config);
    }

    /// <summary>
    /// ファイルヘッダー（1 秒）／ブロックヘッダー（0.5 秒）の無変調区間を付けてから OFDM 変調します。
    /// </summary>
    private static void AppendHeaderPackets(
        List<Complex> leftPcm,
        List<Complex> rightPcm,
        OfdmGenerator ofdm,
        byte[] leftHeaderBytes,
        byte[] rightHeaderBytes,
        int unmodulatedSamples)
    {
        if (unmodulatedSamples > 0)
        {
            AppendPair(leftPcm, rightPcm, ofdm.GenerateUnmodulated(unmodulatedSamples));
        }

        var leftBits = BytesToBitsMsb(ConvolutionalCode.Encode(ApplyReedSolomon(leftHeaderBytes), terminate: true));
        var rightBits = BytesToBitsMsb(ConvolutionalCode.Encode(ApplyReedSolomon(rightHeaderBytes), terminate: true));
        AppendPair(leftPcm, rightPcm, ofdm.ModulateBitStreams(leftBits, rightBits, leftPcm.Count));
    }

    private int HeaderUnmodulatedSamplesFor(int packetLength) =>
        packetLength == FileHeaderBytes
            ? _profile.FileHeaderUnmodulatedSamples
            : _profile.BlockHeaderUnmodulatedSamples;

    private int CatalogHeaderUnmodulatedSamplesFor(int packetLength) =>
        packetLength == FileHeaderBytes
            ? _profile.FileHeaderUnmodulatedSamples
            : 0;

    /// <summary>
    /// ヘッダー先頭の無変調区間を読み飛ばし、論理サンプル時刻も進めます。
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
    /// CP/パイロットスコアで候補を絞り、ヘッダー先頭 8 バイトのパイロット一致で確定します。
    /// まず期待位置の厳密復調を試し、失敗時のみ近傍探索します。
    /// </summary>
    private static byte[] DecodeHeaderPacketSynced(
        Complex[] samples,
        ref int warpedCursor,
        ref long logicalOffset,
        OfdmGenerator ofdm,
        int payloadLength,
        byte[]? expectedPilot,
        int searchRadius,
        bool useRightChannel)
    {
        var rsByteLength = GetReedSolomonEncodedLength(payloadLength);
        var convByteLength = GetConvolutionalEncodedLength(rsByteLength);
        var bitCount = convByteLength * 8;
        var sampleCount = ofdm.SampleCountForBitCount(bitCount);
        var symbolLength = ofdm.SamplesPerOfdmSymbol;
        var probeSymbols = Math.Clamp(sampleCount / symbolLength, 1, 4);

        if (TryDecodeHeaderAt(
                samples,
                warpedCursor,
                logicalOffset,
                ofdm,
                bitCount,
                sampleCount,
                payloadLength,
                rsByteLength,
                expectedPilot,
                useRightChannel,
                perSymbolSearchRadius: 0,
                out var exactPayload,
                out var exactEnd))
        {
            warpedCursor = exactEnd;
            logicalOffset += sampleCount;
            return exactPayload;
        }

        var candidateStarts = CollectSyncCandidates(
            samples,
            warpedCursor,
            sampleCount,
            searchRadius,
            ofdm,
            probeSymbols,
            useRightChannel);

        Exception? lastError = null;
        foreach (var start in candidateStarts)
        {
            if (start == warpedCursor)
            {
                continue;
            }

            try
            {
                if (TryDecodeHeaderAt(
                        samples,
                        start,
                        logicalOffset,
                        ofdm,
                        bitCount,
                        sampleCount,
                        payloadLength,
                        rsByteLength,
                        expectedPilot,
                        useRightChannel,
                        perSymbolSearchRadius: Math.Min(2, Math.Max(0, symbolLength / 16)),
                        out var payload,
                        out var endCursor))
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
            $"Header sync failed near sample {warpedCursor} (payload={payloadLength}).",
            lastError);
    }

    private static bool TryDecodeHeaderAt(
        Complex[] samples,
        int start,
        long logicalOffset,
        OfdmGenerator ofdm,
        int bitCount,
        int sampleCount,
        int payloadLength,
        int rsByteLength,
        byte[]? expectedPilot,
        bool useRightChannel,
        int perSymbolSearchRadius,
        out byte[] payload,
        out int endCursor)
    {
        payload = Array.Empty<byte>();
        endCursor = start;
        if (start < 0 || start + sampleCount > samples.Length)
        {
            return false;
        }

        try
        {
            bool[] bits;
            if (perSymbolSearchRadius <= 0)
            {
                var slice = new Complex[sampleCount];
                Array.Copy(samples, start, slice, 0, sampleCount);
                bits = ofdm.DemodulateBits(slice, bitCount, useRightChannel, logicalOffset);
                endCursor = start + sampleCount;
            }
            else
            {
                var cursor = start;
                bits = ofdm.DemodulateBitsFromStream(
                    samples,
                    ref cursor,
                    bitCount,
                    useRightChannel,
                    logicalOffset,
                    searchRadius: perSymbolSearchRadius);
                endCursor = cursor;
            }

            payload = DecodeHeaderBits(bits, payloadLength, rsByteLength);
            if (expectedPilot is not null && !HeaderPilotMatches(payload, expectedPilot))
            {
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte[] DecodeHeaderBits(bool[] bits, int payloadLength, int rsByteLength)
    {
        var convEncoded = BitsToBytesMsb(bits);
        var rsEncoded = ConvolutionalCode.Decode(convEncoded, rsByteLength, terminated: true);
        var paddedPayload = ApplyReedSolomonDecode(rsEncoded);
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

        // ScoreLock はプリアンブル類似波形で偽ピークを出しやすいので、
        // 期待位置からの距離順に走査し、パイロット一致で確定する。
        Add(expectedStart);
        Add(ofdm.FindBestSymbolStart(samples, expectedStart, Math.Min(searchRadius, symbolLength), useRightChannel));
        for (var radius = step; radius <= searchRadius; radius += step)
        {
            Add(expectedStart - radius);
            Add(expectedStart + radius);
        }

        // スコア上位も少しだけ候補に足す（距離順の後ろで試す）。
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
        var top = Math.Min(6, scored.Count);
        for (var i = 0; i < top; i++)
        {
            Add(scored[i].Start);
            Add(ofdm.FindBestSymbolStart(samples, scored[i].Start, step, useRightChannel));
        }

        return unique;
    }

    private static bool HeaderPilotMatches(byte[] header, byte[] expectedPilot)
    {
        if (header.Length < expectedPilot.Length)
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

        return true;
    }

    private static void AppendModulatedDataBlock(
        List<Complex> leftPcm,
        List<Complex> rightPcm,
        OfdmGenerator ofdm,
        byte[] padded4096)
    {
        if (padded4096.Length != DataBlockBytes)
        {
            throw new ArgumentException($"Data block must be {DataBlockBytes} bytes.", nameof(padded4096));
        }

        // データ部: ターボ → 畳み込み → QAM。復号では畳み込み BCJR の情報 LLR をターボへ直接渡す。
        var turboEncoded = EncodeTurboBlock(padded4096);
        var convEncoded = ConvolutionalCode.Encode(turboEncoded, terminate: true);
        var bits = BytesToBitsMsb(convEncoded);
        AppendPair(leftPcm, rightPcm, ofdm.ModulateBits(bits, leftPcm.Count));
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
        out DataDecodeDiag diag)
    {
        var turboEncodedLength = (DataBlockBytes / TurboEcc1024.DataUnitBytes) * TurboEcc1024.EncodedBytes;
        var convByteLength = GetConvolutionalEncodedLength(turboEncodedLength);
        var bitCount = convByteLength * 8;
        var sampleCount = ofdm.SampleCountForBitCount(bitCount);
        var symbolSearchRadius = Math.Max(2, ofdm.SamplesPerOfdmSymbol / 8);
        var expectedStart = warpedCursor;
        var totalAttempts = 0;
        var hardMatchSucceeded = false;
        var softMatchSucceeded = false;
        var fallbackUsed = false;
        var startDeltaSamples = 0;
        var useStereoCombine = ofdm.ChannelMode == ChannelMode.Stereo
            && rightSamples.Length == leftSamples.Length
            && rightSamples.Length > 0;

        var logical = logicalOffset;
        Exception? lastError = null;
        var step = Math.Max(1, ofdm.SamplesPerOfdmSymbol / 8);

        bool TryAt(int start, int perSymbolRadius, out byte[] padded, out int endCursor)
        {
            padded = Array.Empty<byte>();
            endCursor = start;
            if (start < 0 || start + ofdm.SamplesPerOfdmSymbol > leftSamples.Length)
            {
                return false;
            }

            try
            {
                // ソフト（QAM LLR→BCJR→ターボ）を先に、次にハードを試す。
                foreach (var useSoft in new[] { true, false })
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

                            var slice = new Complex[sampleCount];
                            Array.Copy(leftSamples, start, slice, 0, sampleCount);
                            bits = ofdm.DemodulateBits(slice, bitCount, useRightChannel: false, logical);
                            end = start + sampleCount;
                        }
                        else
                        {
                            bits = ofdm.DemodulateBitsFromStream(
                                leftSamples,
                                ref cursor,
                                bitCount,
                                useRightChannel: false,
                                logical,
                                searchRadius: perSymbolRadius);
                            end = cursor;
                        }

                        var turboEncoded = ConvolutionalCode.Decode(
                            BitsToBytesMsb(bits), turboEncodedLength, terminated: true);
                        candidate = DecodeTurboBlock(turboEncoded);
                    }
                    else
                    {
                        var radius = perSymbolRadius <= 0
                            ? Math.Max(8, ofdm.SamplesPerOfdmSymbol / 4)
                            : perSymbolRadius;
                        double[] qamLlrs;
                        if (useStereoCombine)
                        {
                            qamLlrs = ofdm.DemodulateSoftLlrsStereoCombined(
                                leftSamples,
                                rightSamples,
                                ref cursor,
                                bitCount,
                                logical,
                                searchRadius: radius,
                                noiseVariance: 0.05,
                                estimateNoiseFromPilots: true);
                        }
                        else
                        {
                            qamLlrs = ofdm.DemodulateSoftLlrsFromStream(
                                leftSamples,
                                ref cursor,
                                bitCount,
                                useRightChannel: false,
                                logical,
                                searchRadius: radius,
                                noiseVariance: 0.05);
                        }

                        end = cursor;
                        // BCJR 情報 LLR（ターボ極性）をターボへ直接投入。ハードも併記してハッシュ優先。
                        var turboEncoded = ConvolutionalCode.DecodeSoftToInfoLlrs(
                            qamLlrs, turboEncodedLength, out var infoLlrs, terminated: true);
                        ClampLlrsInPlace(infoLlrs, 16.0);
                        var softCandidate = DecodeTurboBlockFromLlrs(infoLlrs, turboEncoded);
                        var hardCandidate = DecodeTurboBlock(turboEncoded);
                        candidate = PreferHashMatch(softCandidate, hardCandidate, expectedBlockHash, payloadLength);
                    }

                    var actualLen = Math.Clamp(payloadLength, 0, DataBlockBytes);
                    var hash = Hash.ComputeSha256(candidate.AsSpan(0, actualLen).ToArray());
                    if (hash.AsSpan().SequenceEqual(expectedBlockHash))
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

        // クリーン経路を最優先。
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

        for (var delta = 0; delta <= searchRadius; delta += step)
        {
            foreach (var start in delta == 0
                         ? new[] { warpedCursor }
                         : new[] { warpedCursor - delta, warpedCursor + delta })
            {
                if (TryAt(start, symbolSearchRadius, out hit, out hitEnd))
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
        }

        // ハッシュ一致しない場合でも、期待位置のソフト復調を返す。
        if (warpedCursor >= 0 && warpedCursor + ofdm.SamplesPerOfdmSymbol <= leftSamples.Length)
        {
            fallbackUsed = true;
            var cursor = warpedCursor;
            double[] qamLlrs;
            if (useStereoCombine)
            {
                qamLlrs = ofdm.DemodulateSoftLlrsStereoCombined(
                    leftSamples,
                    rightSamples,
                    ref cursor,
                    bitCount,
                    logicalOffset,
                    searchRadius: Math.Max(2, ofdm.SamplesPerOfdmSymbol / 16),
                    noiseVariance: 0.08,
                    estimateNoiseFromPilots: true);
            }
            else
            {
                qamLlrs = ofdm.DemodulateSoftLlrsFromStream(
                    leftSamples,
                    ref cursor,
                    bitCount,
                    useRightChannel: false,
                    logicalOffset,
                    searchRadius: Math.Max(2, ofdm.SamplesPerOfdmSymbol / 16),
                    noiseVariance: 0.08);
            }

            warpedCursor = cursor;
            logicalOffset += sampleCount;
            var turboEncoded = ConvolutionalCode.DecodeSoftToInfoLlrs(
                qamLlrs, turboEncodedLength, out var infoLlrs, terminated: true);
            ClampLlrsInPlace(infoLlrs, 16.0);
            _ = lastError;
            diag = new DataDecodeDiag(
                totalAttempts,
                hardMatchSucceeded,
                softMatchSucceeded,
                fallbackUsed,
                startDeltaSamples);
            var softCandidate = DecodeTurboBlockFromLlrs(infoLlrs, turboEncoded);
            var hardCandidate = DecodeTurboBlock(turboEncoded);
            return PreferHashMatch(softCandidate, hardCandidate, expectedBlockHash, payloadLength);
        }

        diag = new DataDecodeDiag(
            totalAttempts,
            hardMatchSucceeded,
            softMatchSucceeded,
            fallbackUsed,
            startDeltaSamples);
        throw new InvalidDataException("Data block sync failed.", lastError);
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

    private static byte[] PreferHashMatch(
        byte[] softCandidate,
        byte[] hardCandidate,
        byte[] expectedBlockHash,
        int payloadLength)
    {
        var actualLen = Math.Clamp(payloadLength, 0, DataBlockBytes);
        var softHash = Hash.ComputeSha256(softCandidate.AsSpan(0, actualLen).ToArray());
        if (softHash.AsSpan().SequenceEqual(expectedBlockHash))
        {
            return softCandidate;
        }

        var hardHash = Hash.ComputeSha256(hardCandidate.AsSpan(0, actualLen).ToArray());
        if (hardHash.AsSpan().SequenceEqual(expectedBlockHash))
        {
            return hardCandidate;
        }

        return softCandidate;
    }

    private readonly record struct DataDecodeDiag(
        int TotalAttempts,
        bool HardMatchSucceeded,
        bool SoftMatchSucceeded,
        bool FallbackUsed,
        int StartDeltaSamples);

    private static byte[] EncodeTurboBlock(byte[] padded4096)
    {
        using var turboStream = new MemoryStream((DataBlockBytes / TurboEcc1024.DataUnitBytes) * TurboEcc1024.EncodedBytes);
        for (var offset = 0; offset < DataBlockBytes; offset += TurboEcc1024.DataUnitBytes)
        {
            var unit = new byte[TurboEcc1024.DataUnitBytes];
            Buffer.BlockCopy(padded4096, offset, unit, 0, TurboEcc1024.DataUnitBytes);
            var encoded = TurboEcc1024.Encode(unit);
            turboStream.Write(encoded, 0, encoded.Length);
        }

        return turboStream.ToArray();
    }

    private static byte[] DecodeTurboBlock(byte[] turboEncoded)
    {
        var padded = new byte[DataBlockBytes];
        var unitCount = DataBlockBytes / TurboEcc1024.DataUnitBytes;
        for (var i = 0; i < unitCount; i++)
        {
            var encoded = new byte[TurboEcc1024.EncodedBytes];
            Buffer.BlockCopy(turboEncoded, i * TurboEcc1024.EncodedBytes, encoded, 0, TurboEcc1024.EncodedBytes);
            var decoded = TurboEcc1024.Decode(encoded, iterations: 12, channelReliability: 1.25);
            Buffer.BlockCopy(decoded, 0, padded, i * TurboEcc1024.DataUnitBytes, TurboEcc1024.DataUnitBytes);
        }

        return padded;
    }

    /// <summary>
    /// 畳み込みソフト出力 LLR をターボへ渡し、失敗時はハード復号へフォールバックします。
    /// </summary>
    private static byte[] DecodeTurboBlockFromLlrs(double[] infoLlrs, byte[] turboEncodedHard)
    {
        var padded = new byte[DataBlockBytes];
        var unitCount = DataBlockBytes / TurboEcc1024.DataUnitBytes;
        var bitsPerUnit = TurboEcc1024.EncodedBits;
        for (var i = 0; i < unitCount; i++)
        {
            var offset = i * bitsPerUnit;
            byte[] decoded;
            if (infoLlrs.Length >= offset + bitsPerUnit)
            {
                decoded = TurboEcc1024.DecodeFromChannelLlrs(
                    infoLlrs.AsSpan(offset, bitsPerUnit),
                    iterations: 16);
            }
            else
            {
                var encoded = new byte[TurboEcc1024.EncodedBytes];
                Buffer.BlockCopy(
                    turboEncodedHard,
                    i * TurboEcc1024.EncodedBytes,
                    encoded,
                    0,
                    TurboEcc1024.EncodedBytes);
                decoded = TurboEcc1024.Decode(encoded, iterations: 12, channelReliability: 1.25);
            }

            Buffer.BlockCopy(decoded, 0, padded, i * TurboEcc1024.DataUnitBytes, TurboEcc1024.DataUnitBytes);
        }

        return padded;
    }

    private static int GetReedSolomonEncodedLength(int payloadLength)
    {
        var paddedLength = ((payloadLength + RsEcc256.DataUnitSize - 1) / RsEcc256.DataUnitSize) * RsEcc256.DataUnitSize;
        if (paddedLength == 0)
        {
            paddedLength = RsEcc256.DataUnitSize;
        }

        return (paddedLength / RsEcc256.DataUnitSize) * RsEcc256.EncodedUnitSize;
    }

    private static int GetConvolutionalEncodedLength(int inputByteLength)
    {
        var inputBits = inputByteLength * 8;
        var totalInputBits = inputBits + (ConvolutionalCode.ConstraintLength - 1);
        var encodedBits = totalInputBits * ConvolutionalCode.OutputBitsPerInputBit;
        return (encodedBits + 7) / 8;
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

        using var output = new MemoryStream((paddedLength / RsEcc256.DataUnitSize) * RsEcc256.EncodedUnitSize);
        for (var offset = 0; offset < padded.Length; offset += RsEcc256.DataUnitSize)
        {
            var unit = new byte[RsEcc256.DataUnitSize];
            Buffer.BlockCopy(padded, offset, unit, 0, RsEcc256.DataUnitSize);
            var encoded = RsEcc256.Encode(unit);
            output.Write(encoded, 0, encoded.Length);
        }

        return output.ToArray();
    }

    private static byte[] ApplyReedSolomonDecode(byte[] encoded)
    {
        if (encoded.Length % RsEcc256.EncodedUnitSize != 0)
        {
            throw new ArgumentException("RS encoded length is invalid.", nameof(encoded));
        }

        using var output = new MemoryStream((encoded.Length / RsEcc256.EncodedUnitSize) * RsEcc256.DataUnitSize);
        for (var offset = 0; offset < encoded.Length; offset += RsEcc256.EncodedUnitSize)
        {
            var unit = new byte[RsEcc256.EncodedUnitSize];
            Buffer.BlockCopy(encoded, offset, unit, 0, RsEcc256.EncodedUnitSize);
            var decoded = RsEcc256.Decode(unit);
            output.Write(decoded, 0, decoded.Length);
        }

        return output.ToArray();
    }

    private static byte[] BuildFileHeader(FileInfo fileInfo, long fileSize, int blockCount, byte[] fileHash)
    {
        if (fileHash.Length != 64)
        {
            throw new ArgumentException("File hash must be SHA-512 (64 bytes).", nameof(fileHash));
        }

        var header = new byte[FileHeaderBytes];
        Buffer.BlockCopy(FileHeaderPilot, 0, header, 0, HeaderPilotBytes);

        var nameBytes = Encoding.UTF8.GetBytes(fileInfo.Name);
        var copyLen = Math.Min(nameBytes.Length, FileNameBytes);
        Buffer.BlockCopy(nameBytes, 0, header, HeaderPilotBytes, copyLen);
        Buffer.BlockCopy(fileHash, 0, header, HeaderPilotBytes + FileNameBytes, 64);

        WriteFileAttributes(header.AsSpan(840, 20), fileInfo);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(860, 8), fileSize);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(868, 8), blockCount);
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
        header[8] = subcarriers;
        header[9] = modulationMode;
        header[10] = channelMode;
        header[11] = 0;
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(12, 8), blockIndex);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20, 4), blockSize);
        Buffer.BlockCopy(blockHash, 0, header, 24, 32);
        Buffer.BlockCopy(fileHash, 0, header, 56, 64);
        return header;
    }

    private static void EnsureHeaderPilot(byte[] header, byte[] expectedPilot, string headerName)
    {
        if (header.Length < expectedPilot.Length)
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
    }

    /// <summary>
    /// ステレオ向けファイル全体ヘッダー並びを構築します。
    /// 右: 順方向、左: BH 逆順（modulation.mdc）。
    /// </summary>
    private static (List<byte[]> Left, List<byte[]> Right) BuildStereoHeaderPackets(
        byte[] fileHeader,
        byte[][] blockHeaders)
    {
        var n = blockHeaders.Length;
        var left = new List<byte[]>();
        var right = new List<byte[]>();

        void AppendRightRound()
        {
            right.Add(fileHeader);
            foreach (var bh in blockHeaders)
            {
                right.Add(bh);
            }
        }

        void AppendLeftRound()
        {
            left.Add(fileHeader);
            for (var i = n - 1; i >= 0; i--)
            {
                left.Add(blockHeaders[i]);
            }
        }

        if (n <= 2)
        {
            // 右 [FH,BH...,FH,BH...,FH] / 左 [FH,逆BH...,FH,逆BH...,FH]
            AppendRightRound();
            AppendRightRound();
            right.Add(fileHeader);

            AppendLeftRound();
            AppendLeftRound();
            left.Add(fileHeader);
            return (left, right);
        }

        // 例: 右 [FH,BH-0..N-1,FH] / 左 [FH,BH-(N-1)..0,FH]
        AppendRightRound();
        right.Add(fileHeader);
        AppendLeftRound();
        left.Add(fileHeader);
        return (left, right);
    }

    private static List<DataBlock> SplitDataBlocks(byte[] fileBytes)
    {
        var blocks = new List<DataBlock>();
        for (var offset = 0; offset < fileBytes.Length; offset += DataBlockBytes)
        {
            var length = Math.Min(DataBlockBytes, fileBytes.Length - offset);
            var payload = new byte[length];
            Buffer.BlockCopy(fileBytes, offset, payload, 0, length);

            var padded = new byte[DataBlockBytes];
            Buffer.BlockCopy(payload, 0, padded, 0, length);
            blocks.Add(new DataBlock(payload, padded, length));
        }

        if (blocks.Count == 0)
        {
            blocks.Add(new DataBlock(Array.Empty<byte>(), new byte[DataBlockBytes], 0));
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

    private static void AppendSilence(List<Complex> leftPcm, List<Complex> rightPcm, int sampleCount)
    {
        for (var i = 0; i < sampleCount; i++)
        {
            leftPcm.Add(Complex.Zero);
            rightPcm.Add(Complex.Zero);
        }
    }

    private static void AppendPair(List<Complex> leftPcm, List<Complex> rightPcm, (Complex[] Left, Complex[] Right) pair)
    {
        leftPcm.AddRange(pair.Left);
        if (pair.Right.Length == 0)
        {
            // モノラル: WAV は 2ch 固定のため、R は無音で長さを合わせる。
            for (var i = 0; i < pair.Left.Length; i++)
            {
                rightPcm.Add(Complex.Zero);
            }
        }
        else
        {
            rightPcm.AddRange(pair.Right);
        }
    }

    private static int SkipSamples(Complex[] samples, int cursor, int count)
    {
        var next = cursor + count;
        if (next > samples.Length)
        {
            throw new InvalidDataException("WAV ended while skipping preamble/silence.");
        }

        return next;
    }

    private static Complex[] TakeSamples(Complex[] samples, ref int cursor, int count)
    {
        if (cursor + count > samples.Length)
        {
            throw new InvalidDataException("WAV ended while reading OFDM payload.");
        }

        var slice = new Complex[count];
        Array.Copy(samples, cursor, slice, 0, count);
        cursor += count;
        return slice;
    }

    private readonly record struct DataBlock(byte[] Payload, byte[] PaddedPayload, int PayloadLength);
}

/// <summary>
/// 16-bit PCM WAV（2ch ステレオ: L/R）書き出しユーティリティです。
/// </summary>
public static class WavWriter
{
    public static void WriteStereo16(
        string path,
        int sampleRate,
        Complex[] left,
        Complex[] right,
        double peakTarget)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Left/Right sample lengths must match.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var peak = 0.0;
        for (var i = 0; i < left.Length; i++)
        {
            peak = Math.Max(peak, Math.Abs(left[i].Real));
            peak = Math.Max(peak, Math.Abs(right[i].Real));
        }

        var scale = peak > 0.0 ? peakTarget / peak : 1.0;
        var dataBytes = left.Length * sizeof(short) * 2;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)2); // stereo L/R
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short) * 2);
        writer.Write((short)(sizeof(short) * 2));
        writer.Write((short)16);

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (var i = 0; i < left.Length; i++)
        {
            writer.Write(ToPcm16(left[i].Real, scale));
            writer.Write(ToPcm16(right[i].Real, scale));
        }
    }

    private static short ToPcm16(double value, double scale)
    {
        return (short)Math.Round(Math.Clamp(value * scale, -1.0, 1.0) * short.MaxValue);
    }
}

/// <summary>
/// 16-bit PCM WAV（2ch ステレオ: L/R）読み込みユーティリティです。
/// </summary>
public static class WavReader
{
    public static (Complex[] Left, Complex[] Right) ReadStereo16(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        var riff = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (riff != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        reader.ReadInt32();
        var wave = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (wave != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        short channels = 0;
        short bitsPerSample = 0;
        byte[]? data = null;

        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var chunkSize = reader.ReadInt32();
            if (chunkId == "fmt ")
            {
                var format = reader.ReadInt16();
                channels = reader.ReadInt16();
                reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bitsPerSample = reader.ReadInt16();
                var remaining = chunkSize - 16;
                if (remaining > 0)
                {
                    reader.ReadBytes(remaining);
                }

                if (format != 1 || channels != 2 || bitsPerSample != 16)
                {
                    throw new InvalidDataException("Expected 16-bit stereo (2ch) PCM WAV.");
                }
            }
            else if (chunkId == "data")
            {
                data = reader.ReadBytes(chunkSize);
                if ((chunkSize & 1) != 0 && stream.Position < stream.Length)
                {
                    reader.ReadByte();
                }
            }
            else
            {
                reader.ReadBytes(chunkSize);
                if ((chunkSize & 1) != 0 && stream.Position < stream.Length)
                {
                    reader.ReadByte();
                }
            }
        }

        if (data is null)
        {
            throw new InvalidDataException("WAV data chunk not found.");
        }

        var sampleCount = data.Length / 4;
        var left = new Complex[sampleCount];
        var right = new Complex[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var o = i * 4;
            var l = (short)(data[o] | (data[o + 1] << 8));
            var r = (short)(data[o + 2] | (data[o + 3] << 8));
            left[i] = new Complex(l / (double)short.MaxValue, 0.0);
            right[i] = new Complex(r / (double)short.MaxValue, 0.0);
        }

        return (left, right);
    }
}
