using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;

namespace Onta.Core;

/// <summary>
/// 送信フレーム種別です（ヘッダー FH / ブロックヘッダー BH / ブロックデータ BD）。
/// </summary>
public enum TransmissionFrameKind
{
    Fh,
    Bh,
    Bd
}

/// <summary>
/// ファイル ↔ WAV ラウンドトリップ用の OFDM プロファイルです。
/// </summary>
public sealed record FileWavCodecProfile(
    int ActiveSubcarriers,
    ModulationScheme ModulationScheme,
    int SampleRate = 44100,
    double SamplePeak = 0.8,
    int HeaderFftSize = 128,
    int DataFftSize = 128,
    int HeaderCyclicPrefixLength = 32,
    int DataCyclicPrefixLength = 16,
    int RandomSeed = 20260904,
    int StereoFrequencyShiftBins = 1,
    ChannelMode ChannelMode = ChannelMode.Stereo,
    /// <summary>ブロック時系列インターリーブ倍率（1 / 2 / 3）。data_struct.mdc。</summary>
    int BlockInterleaveFactor = 1)
{
    public byte ModulationModeByte => (byte)ModulationScheme;
    public byte ChannelModeByte => (byte)ChannelMode;
    public int LeadingSilenceSamples => SampleRate / 10;
    public int TrailingSilenceSamples => SampleRate / 10;
    /// <summary>
    /// 全体先頭の無変調プリアンブル（廃止）。
    /// 互換のためプロパティは残すが、送受信処理では使用しません。
    /// </summary>
    public int UnmodulatedPreambleSamples => 0;
    /// <summary>ファイルヘッダー先頭の無変調区間（1 秒）。</summary>
    public int FileHeaderUnmodulatedSamples => SampleRate;
    /// <summary>ブロックヘッダー先頭の無変調区間（0.5 秒）。</summary>
    public int BlockHeaderUnmodulatedSamples => SampleRate / 2;
}

/// <summary>
/// 復号処理の実行時チューニングです（リアルタイム入力向け）。
/// </summary>
public sealed record DecodeRuntimeTuning(
    int TurboIterationsMin = 8,
    int TurboIterationsMax = 12,
    double TurboLowConfidenceLlr = 1.5,
    double TurboHighConfidenceLlr = 4.0,
    bool PreferLocalWowTracking = true,
    double WowLocalPhaseRangeRad = 0.3141592653589793,
    double WowLocalAmountRange = 0.002)
{
    public static DecodeRuntimeTuning Default { get; } = new();
}

/// <summary>
/// 入力ファイルの WAV 符号化と、WAV からの復元を行うコーデックです。
/// モノラルは 1ch PCM、ステレオは L/R 別データ（2ch）の WAV です。
/// </summary>
public sealed class FileWavCodec
{
    private const int FftSizeSc9Sc18 = 64;
    private const int FftSizeSc27Sc36 = 128;
    private const int FileHeaderBytes = 880;
    private const int FileNameBytes = 768;
    private const int BlockHeaderBytes = 124;
    private const int HeaderPilotBytes = 8;
    private const int CrcBytes = 4;
    /// <summary>FH/BH の CRC 対象はパイロット直後から CRC 直前まで。</summary>
    private const int HeaderCrcDataOffset = 8;
    private static readonly byte[] FileHeaderPilot = [0xF0, 0xE1, 0xD2, 0xC3, 0xB4, 0xA5, 0x96, 0x87];
    private static readonly byte[] BlockHeaderPilot = [0x0F, 0x1E, 0x2D, 0x3C, 0x4B, 0x5A, 0x69, 0x78];
    private const int DataBlockBytes = 4096;
    /// <summary>データ部は最大 4096 バイト + CRC-32。</summary>
    private const int DataBlockWithCrcBytes = DataBlockBytes + CrcBytes;
    private const string DataTraceEnvVar = "ONTA_TRACE_DATA_ERRORS";
    /// <summary>ファイルヘッダーをデータ部の何ブロックごとに再送出するか。</summary>
    private const int FileHeaderRepeatIntervalBlocks = 4;

    private readonly FileWavCodecProfile _profile;

    public FileWavCodec(FileWavCodecProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (_profile.BlockInterleaveFactor is not (1 or 2 or 3))
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                _profile.BlockInterleaveFactor,
                "BlockInterleaveFactor must be 1, 2, or 3.");
        }
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
        WavWriter.WritePcm16(
            wavPath,
            _profile.SampleRate,
            leftSamples,
            rightSamples,
            _profile.SamplePeak,
            _profile.ChannelMode);

        var decodedBytes = DecodeWavToFileBytes(wavPath);
        if (restoredPath is not null)
        {
            File.WriteAllBytes(restoredPath, decodedBytes);
        }

        return decodedBytes;
    }

    public (Complex[] Left, Complex[] Right) EncodeFileToSamples(
        byte[] fileBytes,
        FileInfo fileInfo,
        Action<TransmissionFrameKind>? onFrameTransmitted = null)
    {
        var fileHash = Hash.ComputeSha512(fileBytes);
        var blocks = SplitDataBlocks(fileBytes);
        var blockHeaders = new byte[blocks.Count][];

        for (var i = 0; i < blocks.Count; i++)
        {
            // ブロックヘッダーの SC 数はチャンネルあたりの本数（ステレオ時も 9/18/27/36）。
            blockHeaders[i] = BuildBlockHeader(
                subcarriers: (byte)_profile.ActiveSubcarriers,
                modulationMode: _profile.ModulationModeByte,
                channelMode: _profile.ChannelModeByte,
                blockIndex: i,
                blockSize: blocks[i].PayloadLength,
                blockHash: Hash.ComputeSha256(blocks[i].Payload),
                fileHash: fileHash);
        }

        var fileHeader = BuildFileHeader(fileInfo, fileBytes.Length, blocks.Count, fileHash);
        var headerOfdm = CreateHeaderOfdm();
        var dataOfdm = CreateDataOfdm();
        var leftPcm = new List<Complex>(1 << 20);
        var rightPcm = new List<Complex>(1 << 20);

        AppendSilence(leftPcm, rightPcm, _profile.LeadingSilenceSamples, stereo: _profile.ChannelMode == ChannelMode.Stereo);

        // data_struct.mdc: 各パス先頭に FH、(BH+BD)×N（パス内は4ブロックごとに FH）、最後に FH。
        // ×2 の第2パスは奇偶入れ替え、×3 の第3パスは第1パスと同順。
        for (var pass = 0; pass < _profile.BlockInterleaveFactor; pass++)
        {
            AppendFileHeaderPacket(leftPcm, rightPcm, headerOfdm, fileHeader);
            onFrameTransmitted?.Invoke(TransmissionFrameKind.Fh);
            var order = GetBlockEmissionOrder(blocks.Count, pass);
            for (var local = 0; local < order.Length; local++)
            {
                if (local > 0 && (local % FileHeaderRepeatIntervalBlocks) == 0)
                {
                    AppendFileHeaderPacket(leftPcm, rightPcm, headerOfdm, fileHeader);
                    onFrameTransmitted?.Invoke(TransmissionFrameKind.Fh);
                }

                var blockIndex = order[local];
                // BH は FH と同じ変調方式（BPSK固定）で送出する。
                AppendHeaderPackets(
                    leftPcm,
                    rightPcm,
                    headerOfdm,
                    blockHeaders[blockIndex],
                    blockHeaders[blockIndex],
                    _profile.BlockHeaderUnmodulatedSamples);
                onFrameTransmitted?.Invoke(TransmissionFrameKind.Bh);
                AppendModulatedDataBlock(leftPcm, rightPcm, dataOfdm, blocks[blockIndex].Payload);
                onFrameTransmitted?.Invoke(TransmissionFrameKind.Bd);
            }
        }

        AppendFileHeaderPacket(leftPcm, rightPcm, headerOfdm, fileHeader);
        onFrameTransmitted?.Invoke(TransmissionFrameKind.Fh);

        AppendSilence(leftPcm, rightPcm, _profile.TrailingSilenceSamples, stereo: _profile.ChannelMode == ChannelMode.Stereo);
        return (leftPcm.ToArray(), rightPcm.ToArray());
    }

    public byte[] DecodeWavToFileBytes(
        string wavPath,
        bool correctWow = true,
        (double Amount, double WowPhase, double FlutterPhase)? wowParams = null,
        DecodeRuntimeTuning? tuning = null)
    {
        var (leftSamples, rightSamples) = WavReader.ReadPcm16(wavPath);
        return DecodePcmSamplesToFileBytes(leftSamples, rightSamples, correctWow, wowParams, tuning);
    }

    public byte[] DecodePcmSamplesToFileBytes(
        Complex[] leftSamples,
        Complex[] rightSamples,
        bool correctWow = true,
        (double Amount, double WowPhase, double FlutterPhase)? wowParams = null,
        DecodeRuntimeTuning? tuning = null)
    {
        tuning ??= DecodeRuntimeTuning.Default;
        if (_profile.ChannelMode == ChannelMode.Mono && rightSamples.Length != 0)
        {
            throw new InvalidDataException("Mono profile expects a 1-channel WAV.");
        }

        if (_profile.ChannelMode == ChannelMode.Stereo && rightSamples.Length != leftSamples.Length)
        {
            throw new InvalidDataException("Stereo profile expects a 2-channel WAV with equal L/R length.");
        }

        var headerOfdm = CreateHeaderOfdm();
        var dataOfdm = CreateDataOfdm();
        // 既知パラメータ指定時は全体へ一括適用。
        // 自動補正時（correctWow=true）は、復号進行に合わせて都度解析して適用する。
        if (wowParams is { } known)
        {
            leftSamples = headerOfdm.CorrectWowFlutterWithParams(
                leftSamples,
                known.Amount,
                known.WowPhase,
                known.FlutterPhase);
            if (_profile.ChannelMode == ChannelMode.Stereo)
            {
                rightSamples = headerOfdm.CorrectWowFlutterWithParams(
                    rightSamples,
                    known.Amount,
                    known.WowPhase,
                    known.FlutterPhase);
            }
        }

        var adaptiveWow = wowParams is null && correctWow;
        var trackedWow = (Amount: 0.01, WowPhase: 0.0, FlutterPhase: 0.0);
        var hasTrackedWow = false;

        // warpedCursor: 実波形上の読み位置 / logicalOffset: 符号化時のサンプル時刻（インターリーブ用）
        var warpedCursor = 0;
        warpedCursor = SkipSamples(leftSamples, warpedCursor, _profile.LeadingSilenceSamples);
        var logicalOffset = (long)warpedCursor;

        var coarseRadius = Math.Max(headerOfdm.SamplesPerOfdmSymbol * 8, _profile.SampleRate / 50);
        var fineRadius = Math.Max(headerOfdm.SamplesPerOfdmSymbol * 2, _profile.SampleRate / 200);

        void ApplyAdaptiveWowCorrection(ref Complex[] channelSamples, bool useRightChannel)
        {
            if (!adaptiveWow)
            {
                return;
            }

            var remaining = channelSamples.Length - warpedCursor;
            var minWindow = Math.Max(headerOfdm.SamplesPerOfdmSymbol * 16, _profile.SampleRate / 4);
            if (remaining < minWindow)
            {
                return;
            }

            var analysisStart = Math.Max(0, warpedCursor - (_profile.SampleRate / 8));
            var analysisCount = Math.Min(remaining, Math.Max(minWindow, _profile.SampleRate / 2));
            var diag = hasTrackedWow && tuning.PreferLocalWowTracking
                ? headerOfdm.RefineWowParametersNearHintForDiagnostics(
                    channelSamples,
                    useRightChannel,
                    analysisStart,
                    analysisCount,
                    trackedWow.Amount,
                    trackedWow.WowPhase,
                    trackedWow.FlutterPhase,
                    phaseRangeRad: tuning.WowLocalPhaseRangeRad,
                    amountRange: tuning.WowLocalAmountRange)
                : headerOfdm.MatchWowParametersForDiagnostics(
                    channelSamples,
                    useRightChannel,
                    analysisStart,
                    analysisCount);
            if (diag is null)
            {
                return;
            }

            var gain = diag.Value.BestScore - diag.Value.Baseline;
            if (gain < 1e-5)
            {
                return;
            }

            var tailLength = channelSamples.Length - warpedCursor;
            if (tailLength <= 0)
            {
                return;
            }

            var tail = new Complex[tailLength];
            Array.Copy(channelSamples, warpedCursor, tail, 0, tailLength);
            var correctedTail = headerOfdm.CorrectWowFlutterWithParams(
                tail,
                diag.Value.Amount,
                diag.Value.WowPhase,
                diag.Value.FlutterPhase);
            Array.Copy(correctedTail, 0, channelSamples, warpedCursor, correctedTail.Length);
            trackedWow = (diag.Value.Amount, diag.Value.WowPhase, diag.Value.FlutterPhase);
            hasTrackedWow = true;
        }

        void ApplyAdaptiveWowCorrectionPair()
        {
            ApplyAdaptiveWowCorrection(ref leftSamples, useRightChannel: false);
            if (_profile.ChannelMode == ChannelMode.Stereo)
            {
                ApplyAdaptiveWowCorrection(ref rightSamples, useRightChannel: true);
            }
        }

        // 復号は L チャンネルを基準に同期する。
        ApplyAdaptiveWowCorrectionPair();
        SkipHeaderUnmodulatedPreamble(
            leftSamples,
            ref warpedCursor,
            ref logicalOffset,
            _profile.FileHeaderUnmodulatedSamples);
        ApplyAdaptiveWowCorrectionPair();
        var fileHeader = DecodeHeaderPacketSynced(
            leftSamples,
            rightSamples,
            ref warpedCursor,
            ref logicalOffset,
            headerOfdm,
            FileHeaderBytes,
            FileHeaderPilot,
            coarseRadius);
        EnsureHeaderCrc(fileHeader, "file header");
        var fileSize = BinaryPrimitives.ReadInt64BigEndian(fileHeader.AsSpan(860, 8));
        var blockCount = (int)BinaryPrimitives.ReadInt64BigEndian(fileHeader.AsSpan(868, 8));
        if (fileSize < 0 || blockCount < 0)
        {
            throw new InvalidDataException("Invalid file header size/block count.");
        }

        var outputSlots = new byte[blockCount][];
        var slotAccepted = new bool[blockCount];
        var traceDataErrors = string.Equals(
            Environment.GetEnvironmentVariable(DataTraceEnvVar),
            "1",
            StringComparison.Ordinal);

        for (var pass = 0; pass < _profile.BlockInterleaveFactor; pass++)
        {
            if (pass > 0)
            {
                SkipHeaderUnmodulatedPreamble(
                    leftSamples,
                    ref warpedCursor,
                    ref logicalOffset,
                    _profile.FileHeaderUnmodulatedSamples);
                ApplyAdaptiveWowCorrectionPair();
                var passFh = DecodeHeaderPacketSynced(
            leftSamples,
            rightSamples,
            ref warpedCursor,
                    ref logicalOffset,
                    headerOfdm,
                    FileHeaderBytes,
                    FileHeaderPilot,
                    fineRadius);
                EnsureHeaderCrc(passFh, "pass file header");
            }

            var order = GetBlockEmissionOrder(blockCount, pass);
            for (var local = 0; local < order.Length; local++)
            {
                if (local > 0 && (local % FileHeaderRepeatIntervalBlocks) == 0)
                {
                    SkipHeaderUnmodulatedPreamble(
                        leftSamples,
                        ref warpedCursor,
                        ref logicalOffset,
                        _profile.FileHeaderUnmodulatedSamples);
                    ApplyAdaptiveWowCorrectionPair();
                    var midFh = DecodeHeaderPacketSynced(
            leftSamples,
            rightSamples,
            ref warpedCursor,
                        ref logicalOffset,
                        headerOfdm,
                        FileHeaderBytes,
                        FileHeaderPilot,
                        fineRadius);
                    EnsureHeaderCrc(midFh, "mid file header");
                }

                var expectedBlockIndex = order[local];
                SkipHeaderUnmodulatedPreamble(
                    leftSamples,
                    ref warpedCursor,
                    ref logicalOffset,
                    _profile.BlockHeaderUnmodulatedSamples);
                ApplyAdaptiveWowCorrectionPair();
                var blockHeader = DecodeHeaderPacketSynced(
            leftSamples,
            rightSamples,
            ref warpedCursor,
                    ref logicalOffset,
                    headerOfdm,
                    BlockHeaderBytes,
                    BlockHeaderPilot,
                    fineRadius);
                EnsureHeaderCrc(blockHeader, "block header");
                var blockIndex = BinaryPrimitives.ReadInt64BigEndian(blockHeader.AsSpan(12, 8));
                var blockSize = BinaryPrimitives.ReadInt32BigEndian(blockHeader.AsSpan(20, 4));
                if (blockIndex != expectedBlockIndex)
                {
                    throw new InvalidDataException(
                        $"Unexpected block index {blockIndex}, expected {expectedBlockIndex} (pass {pass}, local {local}).");
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
                    tuning,
                    out var diag);
                if (traceDataErrors)
                {
                    Console.WriteLine(
                        $"[DATA-DIAG] pass={pass} block={expectedBlockIndex} size={blockSize} hardOK={diag.HardMatchSucceeded} softOK={diag.SoftMatchSucceeded} fallback={diag.FallbackUsed} startDelta={diag.StartDeltaSamples} attempts={diag.TotalAttempts}");
                }

                var acceptable = IsDataBlockAcceptable(
                    padded,
                    blockHeader.AsSpan(24, 32).ToArray(),
                    blockSize);
                if (!slotAccepted[expectedBlockIndex] || acceptable)
                {
                    var payload = new byte[blockSize];
                    Buffer.BlockCopy(padded, 0, payload, 0, blockSize);
                    outputSlots[expectedBlockIndex] = payload;
                    if (acceptable)
                    {
                        slotAccepted[expectedBlockIndex] = true;
                    }
                }
            }
        }

        // 末尾 FH（存在すれば読み飛ばし／検証。ストリーム終端でも許容）
        try
        {
            if (warpedCursor + headerOfdm.SamplesPerOfdmSymbol < leftSamples.Length)
            {
                SkipHeaderUnmodulatedPreamble(
                    leftSamples,
                    ref warpedCursor,
                    ref logicalOffset,
                    _profile.FileHeaderUnmodulatedSamples);
                ApplyAdaptiveWowCorrectionPair();
                var endFh = DecodeHeaderPacketSynced(
            leftSamples,
            rightSamples,
            ref warpedCursor,
                    ref logicalOffset,
                    headerOfdm,
                    FileHeaderBytes,
                    FileHeaderPilot,
                    fineRadius);
                EnsureHeaderCrc(endFh, "trailing file header");
            }
        }
        catch (InvalidDataException)
        {
            // 末尾 FH が欠ける場合でもデータが揃っていれば成功とする。
        }

        var output = new byte[fileSize];
        var writeOffset = 0;
        for (var i = 0; i < blockCount; i++)
        {
            if (outputSlots[i] is null)
            {
                throw new InvalidDataException($"Missing decoded block {i}.");
            }

            var payload = outputSlots[i]!;
            if (writeOffset + payload.Length > output.Length)
            {
                throw new InvalidDataException("Decoded payload exceeds file size.");
            }

            Buffer.BlockCopy(payload, 0, output, writeOffset, payload.Length);
            writeOffset += payload.Length;
        }

        if (writeOffset != fileSize)
        {
            throw new InvalidDataException($"Decoded size mismatch: got {writeOffset}, expected {fileSize}.");
        }

        return output;
    }

    private OfdmGenerator CreateHeaderOfdm()
    {
        // FH/BH は 9 SC/ch + BPSK。
        var fftSize = ResolveFftSizeBySubcarrier(_profile.ActiveSubcarriers);
        var cyclicPrefixLength = _profile.HeaderCyclicPrefixLength;
        var scPerChannel = 9;
        var config = new OfdmConfig(
            fftSize: fftSize,
            activeSubcarriers: scPerChannel,
            cyclicPrefixLength: cyclicPrefixLength,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Bpsk,
            channelMode: _profile.ChannelMode,
            enableFrequencyInterleaving: true,
            pilotSpacing: 9,
            stereoFrequencyShiftBins: _profile.StereoFrequencyShiftBins,
            sampleRate: _profile.SampleRate,
            frequencyInterleaveIntervalSymbols: 1,
            randomSeed: _profile.RandomSeed);

        return new OfdmGenerator(config);
    }

    private OfdmGenerator CreateDataOfdm()
    {
        // modulation.mdc: SC-9/18 は FFT=64、SC-27/36 は FFT=128 固定。
        // ステレオ時は L/R 各 ActiveSubcarriers 本 → 合計 2 倍のスループット（ビット列を L/R 分割）。
        var fftSize = ResolveFftSizeBySubcarrier(_profile.ActiveSubcarriers);
        var cyclicPrefixLength = _profile.DataCyclicPrefixLength;
        var scPerChannel = _profile.ActiveSubcarriers;
        var config = new OfdmConfig(
            fftSize: fftSize,
            activeSubcarriers: scPerChannel,
            cyclicPrefixLength: cyclicPrefixLength,
            ofdmSymbolCount: 1,
            modulationScheme: _profile.ModulationScheme,
            channelMode: _profile.ChannelMode,
            enableFrequencyInterleaving: true,
            pilotSpacing: 9,
            stereoFrequencyShiftBins: _profile.StereoFrequencyShiftBins,
            sampleRate: _profile.SampleRate,
            frequencyInterleaveIntervalSymbols: 1,
            randomSeed: _profile.RandomSeed);

        return new OfdmGenerator(config);
    }

    private static int ResolveFftSizeBySubcarrier(int activeSubcarriers)
    {
        return activeSubcarriers switch
        {
            9 or 18 => FftSizeSc9Sc18,
            27 or 36 => FftSizeSc27Sc36,
            _ => throw new ArgumentOutOfRangeException(nameof(activeSubcarriers), activeSubcarriers, "Supported values are 9, 18, 27, or 36.")
        };
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
        if (ofdm.ChannelMode == ChannelMode.Mono)
        {
            // モノラル: L のみ。R に同一データを載せない。
            AppendPair(leftPcm, rightPcm, ofdm.ModulateBits(leftBits, leftPcm.Count));
            return;
        }

        // ステレオ: L/R で別ビット列（ヘッダー本体を分割）し SC 合計 2 倍にする。
        _ = rightHeaderBytes;
        SplitBitsForStereo(leftBits, out var splitLeft, out var splitRight);
        AppendPair(leftPcm, rightPcm, ofdm.ModulateBitStreams(splitLeft, splitRight, leftPcm.Count));
    }

    private int HeaderUnmodulatedSamplesFor(int packetLength) =>
        packetLength == FileHeaderBytes
            ? _profile.FileHeaderUnmodulatedSamples
            : _profile.BlockHeaderUnmodulatedSamples;

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
    /// ステレオ時は L/R 分割ビットを結合してから復号します。
    /// </summary>
    private static byte[] DecodeHeaderPacketSynced(
        Complex[] leftSamples,
        Complex[] rightSamples,
        ref int warpedCursor,
        ref long logicalOffset,
        OfdmGenerator ofdm,
        int payloadLength,
        byte[]? expectedPilot,
        int searchRadius)
    {
        var rsByteLength = GetReedSolomonEncodedLength(payloadLength);
        var convByteLength = GetConvolutionalEncodedLength(rsByteLength);
        var bitCount = convByteLength * 8;
        var stereoSplit = ofdm.ChannelMode == ChannelMode.Stereo
            && rightSamples.Length == leftSamples.Length
            && rightSamples.Length > 0;
        var channelBitCount = stereoSplit ? (bitCount + 1) / 2 : bitCount;
        var sampleCount = ofdm.SampleCountForBitCount(channelBitCount);
        var symbolLength = ofdm.SamplesPerOfdmSymbol;
        var probeSymbols = Math.Clamp(sampleCount / symbolLength, 1, 4);

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
                out var exactEnd))
        {
            warpedCursor = exactEnd;
            logicalOffset += sampleCount;
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
        foreach (var start in candidateStarts)
        {
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
        payload = Array.Empty<byte>();
        endCursor = start;
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
                    noiseVariance: 0.05);
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
                    noiseVariance: 0.05);
                endCursor = cursor;
            }

            payload = DecodeHeaderFromSoftLlrs(llrs, payloadLength, rsByteLength);
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

    private static byte[] DecodeHeaderFromSoftLlrs(double[] llrs, int payloadLength, int rsByteLength)
    {
        // 仕様: ヘッダー復号はソフト LLR を畳み込み BCJR に通し、中間ハード判定の情報落ちを避ける。
        var rsEncoded = ConvolutionalCode.DecodeSoftToInfoLlrs(llrs, rsByteLength, out _, terminated: true);
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
        byte[] payload)
    {
        var packed = PackDataBlockWithCrc(payload);
        // データ部: ターボ → 畳み込み → QAM。ステレオ時はビット列を L/R に分割して SC 合計 2 倍相当にする。
        var turboEncoded = EncodeTurboBlock(packed);
        var convEncoded = ConvolutionalCode.Encode(turboEncoded, terminate: true);
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
    /// ペイロード + CRC-32 をターボ符号単位（1024 バイト）境界へパディングします。
    /// </summary>
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

    /// <summary>
    /// ブロック時系列インターリーブの送出順を返します（data_struct.mdc）。
    /// 偶数パスは 0,1,2,…、奇数パスは奇偶入れ替え 1,0,3,2,…。
    /// </summary>
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

    private static byte[] DecodeDataBlockSynced(
        Complex[] leftSamples,
        Complex[] rightSamples,
        ref int warpedCursor,
        ref long logicalOffset,
        OfdmGenerator ofdm,
        int searchRadius,
        byte[] expectedBlockHash,
        int payloadLength,
        DecodeRuntimeTuning tuning,
        out DataDecodeDiag diag)
    {
        var paddedLen = TurboPaddedLength(payloadLength + CrcBytes);
        var turboEncodedLength = (paddedLen / TurboEcc1024.DataUnitBytes) * TurboEcc1024.EncodedBytes;
        var convByteLength = GetConvolutionalEncodedLength(turboEncodedLength);
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
                            BitsToBytesMsb(bits), turboEncodedLength, terminated: true);
                        candidate = DecodeTurboBlock(turboEncoded, paddedLen, tuning.TurboIterationsMax);
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
                            noiseVariance: 0.05);
                        end = cursor;
                        var turboEncoded = ConvolutionalCode.DecodeSoftToInfoLlrs(
                            qamLlrs, turboEncodedLength, out var infoLlrs, terminated: true);
                        ClampLlrsInPlace(infoLlrs, 16.0);
                        var turboIterations = ResolveTurboIterations(infoLlrs, tuning);
                        var softCandidate = DecodeTurboBlockFromLlrs(infoLlrs, turboEncoded, paddedLen, turboIterations);
                        var hardCandidate = DecodeTurboBlock(turboEncoded, paddedLen, turboIterations);
                        candidate = PreferHashMatch(softCandidate, hardCandidate, expectedBlockHash, payloadLength);
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

        if (warpedCursor >= 0 && warpedCursor + ofdm.SamplesPerOfdmSymbol <= leftSamples.Length)
        {
            fallbackUsed = true;
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
                noiseVariance: 0.08);
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
            var turboIterations = ResolveTurboIterations(infoLlrs, tuning);
            var softCandidate = DecodeTurboBlockFromLlrs(infoLlrs, turboEncoded, paddedLen, turboIterations);
            var hardCandidate = DecodeTurboBlock(turboEncoded, paddedLen, turboIterations);
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
        double noiseVariance)
    {
        if (!stereoSplit)
        {
            return ofdm.DemodulateSoftLlrsFromStream(
                leftSamples,
                ref cursor,
                totalBitCount,
                useRightChannel: false,
                logical,
                searchRadius,
                noiseVariance);
        }

        var leftCursor = cursor;
        var rightCursor = cursor;
        var leftLlrs = ofdm.DemodulateSoftLlrsFromStream(
            leftSamples, ref leftCursor, channelBitCount, useRightChannel: false, logical, searchRadius, noiseVariance);
        var rightLlrs = ofdm.DemodulateSoftLlrsFromStream(
            rightSamples, ref rightCursor, channelBitCount, useRightChannel: true, logical, searchRadius, noiseVariance);
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

        // どちらも完全一致しない場合はソフト優先（従来どおり）。
        return softCandidate;
    }

    private static bool IsDataBlockAcceptable(byte[] candidate, byte[] expectedBlockHash, int payloadLength)
    {
        var actualLen = Math.Clamp(payloadLength, 0, DataBlockBytes);
        if (candidate.Length < actualLen + CrcBytes)
        {
            return false;
        }

        var hash = Hash.ComputeSha256(candidate.AsSpan(0, actualLen).ToArray());
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

        using var turboStream = new MemoryStream((padded.Length / TurboEcc1024.DataUnitBytes) * TurboEcc1024.EncodedBytes);
        for (var offset = 0; offset < padded.Length; offset += TurboEcc1024.DataUnitBytes)
        {
            var unit = new byte[TurboEcc1024.DataUnitBytes];
            Buffer.BlockCopy(padded, offset, unit, 0, TurboEcc1024.DataUnitBytes);
            var encoded = TurboEcc1024.Encode(unit);
            turboStream.Write(encoded, 0, encoded.Length);
        }

        return turboStream.ToArray();
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

    private static byte[] DecodeTurboBlock(byte[] turboEncoded, int paddedLength, int iterations)
    {
        var padded = new byte[paddedLength];
        var unitCount = paddedLength / TurboEcc1024.DataUnitBytes;
        for (var i = 0; i < unitCount; i++)
        {
            var encoded = new byte[TurboEcc1024.EncodedBytes];
            Buffer.BlockCopy(turboEncoded, i * TurboEcc1024.EncodedBytes, encoded, 0, TurboEcc1024.EncodedBytes);
            var decoded = TurboEcc1024.Decode(encoded, iterations: iterations, channelReliability: 1.25);
            Buffer.BlockCopy(decoded, 0, padded, i * TurboEcc1024.DataUnitBytes, TurboEcc1024.DataUnitBytes);
        }

        return padded;
    }

    /// <summary>
    /// 畳み込みソフト出力 LLR をターボへ渡し、失敗時はハード復号へフォールバックします。
    /// </summary>
    private static byte[] DecodeTurboBlockFromLlrs(double[] infoLlrs, byte[] turboEncodedHard, int paddedLength, int iterations)
    {
        var padded = new byte[paddedLength];
        var unitCount = paddedLength / TurboEcc1024.DataUnitBytes;
        var bitsPerUnit = TurboEcc1024.EncodedBits;
        for (var i = 0; i < unitCount; i++)
        {
            var offset = i * bitsPerUnit;
            byte[] decoded;
            if (infoLlrs.Length >= offset + bitsPerUnit)
            {
                var unitLlrs = infoLlrs.AsSpan(offset, bitsPerUnit).ToArray();
                try
                {
                    decoded = TurboEcc1024.DecodeFromChannelLlrs(unitLlrs.AsSpan(), iterations: iterations);
                }
                catch
                {
                    var encoded = new byte[TurboEcc1024.EncodedBytes];
                    Buffer.BlockCopy(turboEncodedHard, i * TurboEcc1024.EncodedBytes, encoded, 0, TurboEcc1024.EncodedBytes);
                    decoded = TurboEcc1024.Decode(encoded, iterations: iterations, channelReliability: 1.25);
                }
            }
            else
            {
                var encoded = new byte[TurboEcc1024.EncodedBytes];
                Buffer.BlockCopy(turboEncodedHard, i * TurboEcc1024.EncodedBytes, encoded, 0, TurboEcc1024.EncodedBytes);
                decoded = TurboEcc1024.Decode(encoded, iterations: iterations, channelReliability: 1.25);
            }

            Buffer.BlockCopy(decoded, 0, padded, i * TurboEcc1024.DataUnitBytes, TurboEcc1024.DataUnitBytes);
        }

        return padded;
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

    private static void AppendSilence(List<Complex> leftPcm, List<Complex> rightPcm, int sampleCount, bool stereo)
    {
        for (var i = 0; i < sampleCount; i++)
        {
            leftPcm.Add(Complex.Zero);
            if (stereo)
            {
                rightPcm.Add(Complex.Zero);
            }
        }
    }

    private static void AppendPair(List<Complex> leftPcm, List<Complex> rightPcm, (Complex[] Left, Complex[] Right) pair)
    {
        leftPcm.AddRange(pair.Left);
        if (pair.Right.Length == 0)
        {
            // モノラル: R は生成しない（1ch WAV 用）。
            return;
        }

        rightPcm.AddRange(pair.Right);
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

    /// <summary>
    /// 送信時の構成要素ごとの所要時間内訳です。
    /// </summary>
    public readonly record struct TransmissionDurationSegment(string Label, long Samples, double Seconds);

    /// <summary>
    /// 送信全体の所要時間見積りです。
    /// </summary>
    public sealed record TransmissionDurationEstimate(
        int SampleRate,
        long TotalSamples,
        double TotalSeconds,
        IReadOnlyList<TransmissionDurationSegment> Segments);

    /// <summary>
    /// ファイルサイズと送信プロファイルから、送信時間の内訳を見積もります。
    /// 送出順は実際の実装（FH再送・インターリーブ順）に合わせます。
    /// </summary>
    public static TransmissionDurationEstimate EstimateTransmissionDuration(FileWavCodecProfile profile, long fileSizeBytes)
    {
        if (fileSizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileSizeBytes));
        }

        var codec = new FileWavCodec(profile);
        var headerOfdm = codec.CreateHeaderOfdm();
        var dataOfdm = codec.CreateDataOfdm();
        var payloadLengths = BuildBlockPayloadLengths(fileSizeBytes);
        var segments = new List<TransmissionDurationSegment>(payloadLengths.Length * Math.Max(1, profile.BlockInterleaveFactor) + 8);
        var totalSamples = 0L;

        var fhPacketSamples = HeaderPacketSamples(headerOfdm, FileHeaderBytes, profile.FileHeaderUnmodulatedSamples);
        var bhPacketSamples = HeaderPacketSamples(headerOfdm, BlockHeaderBytes, profile.BlockHeaderUnmodulatedSamples);

        AddSegment(segments, "LEAD", profile.LeadingSilenceSamples, profile.SampleRate, ref totalSamples);

        for (var pass = 0; pass < profile.BlockInterleaveFactor; pass++)
        {
            AddSegment(segments, "FH", fhPacketSamples, profile.SampleRate, ref totalSamples);
            var order = GetBlockEmissionOrder(payloadLengths.Length, pass);
            for (var local = 0; local < order.Length; local++)
            {
                if (local > 0 && (local % FileHeaderRepeatIntervalBlocks) == 0)
                {
                    AddSegment(segments, "FH", fhPacketSamples, profile.SampleRate, ref totalSamples);
                }

                var blockIndex = order[local];
                var dataSamples = DataPacketSamples(dataOfdm, payloadLengths[blockIndex], profile.ChannelMode);
                var blkSamples = bhPacketSamples + dataSamples;
                var blkLabel = profile.BlockInterleaveFactor == 1
                    ? $"BLK-{blockIndex}"
                    : $"BLK-{blockIndex}(P{pass + 1})";
                AddSegment(segments, blkLabel, blkSamples, profile.SampleRate, ref totalSamples);
            }
        }

        AddSegment(segments, "FH", fhPacketSamples, profile.SampleRate, ref totalSamples);
        AddSegment(segments, "TAIL", profile.TrailingSilenceSamples, profile.SampleRate, ref totalSamples);

        return new TransmissionDurationEstimate(
            profile.SampleRate,
            totalSamples,
            totalSamples / (double)profile.SampleRate,
            segments);
    }

    /// <summary>
    /// 内訳を画面表示しやすいテキストへ整形します。
    /// </summary>
    public static string FormatTransmissionDurationBreakdown(TransmissionDurationEstimate estimate, int digits = 3)
    {
        var sb = new StringBuilder(estimate.Segments.Count * 24);
        var fmt = "F" + Math.Clamp(digits, 0, 6);
        for (var i = 0; i < estimate.Segments.Count; i++)
        {
            var seg = estimate.Segments[i];
            sb.Append(seg.Label)
              .Append(':')
              .Append(seg.Seconds.ToString(fmt))
              .Append("秒")
              .AppendLine();
        }

        sb.Append("合計:")
          .Append(estimate.TotalSeconds.ToString(fmt))
          .Append("秒");
        return sb.ToString();
    }

    private static int[] BuildBlockPayloadLengths(long fileSizeBytes)
    {
        if (fileSizeBytes == 0)
        {
            return [0];
        }

        var blockCountLong = (fileSizeBytes + DataBlockBytes - 1) / DataBlockBytes;
        if (blockCountLong > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(fileSizeBytes), "Block count exceeds supported range.");
        }

        var blockCount = (int)blockCountLong;
        var lengths = new int[blockCount];
        var remaining = fileSizeBytes;
        for (var i = 0; i < blockCount; i++)
        {
            var len = (int)Math.Min(DataBlockBytes, remaining);
            lengths[i] = len;
            remaining -= len;
        }

        return lengths;
    }

    private static int HeaderPacketSamples(OfdmGenerator headerOfdm, int payloadLength, int unmodulatedSamples)
    {
        var rsLength = GetReedSolomonEncodedLength(payloadLength);
        var convLength = GetConvolutionalEncodedLength(rsLength);
        var totalBits = convLength * 8;
        var channelBits = headerOfdm.ChannelMode == ChannelMode.Stereo
            ? (totalBits + 1) / 2
            : totalBits;
        return unmodulatedSamples + headerOfdm.SampleCountForBitCount(channelBits);
    }

    private static int DataPacketSamples(OfdmGenerator dataOfdm, int payloadLength, ChannelMode channelMode)
    {
        var withCrcLength = payloadLength + CrcBytes;
        var paddedLength = TurboPaddedLength(withCrcLength);
        var turboLength = (paddedLength / TurboEcc1024.DataUnitBytes) * TurboEcc1024.EncodedBytes;
        var convLength = GetConvolutionalEncodedLength(turboLength);
        var totalBits = convLength * 8;
        var channelBits = channelMode == ChannelMode.Stereo
            ? (totalBits + 1) / 2
            : totalBits;
        return dataOfdm.SampleCountForBitCount(channelBits);
    }

    private static void AddSegment(
        List<TransmissionDurationSegment> segments,
        string label,
        long samples,
        int sampleRate,
        ref long totalSamples)
    {
        totalSamples += samples;
        segments.Add(new TransmissionDurationSegment(label, samples, samples / (double)sampleRate));
    }

    private readonly record struct DataBlock(byte[] Payload, int PayloadLength);
}

/// <summary>
/// 16-bit PCM WAV 書き出しユーティリティです（モノラル 1ch / ステレオ 2ch）。
/// </summary>
public static class WavWriter
{
    /// <summary>
    /// チャンネルモードに応じて 1ch または 2ch の WAV を書き出します。
    /// モノラル時は <paramref name="right"/> を無視し、L のみを出力します。
    /// </summary>
    public static void WritePcm16(
        string path,
        int sampleRate,
        Complex[] left,
        Complex[] right,
        double peakTarget,
        ChannelMode channelMode)
    {
        if (channelMode == ChannelMode.Mono)
        {
            WriteMono16(path, sampleRate, left, peakTarget);
            return;
        }

        WriteStereo16(path, sampleRate, left, right, peakTarget);
    }

    public static void WriteMono16(
        string path,
        int sampleRate,
        Complex[] samples,
        double peakTarget)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(samples);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var peak = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            peak = Math.Max(peak, Math.Abs(samples[i].Real));
        }

        var scale = peak > 0.0 ? peakTarget / peak : 1.0;
        var dataBytes = samples.Length * sizeof(short);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (var i = 0; i < samples.Length; i++)
        {
            writer.Write(ToPcm16(samples[i].Real, scale));
        }
    }

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
/// 16-bit PCM WAV 読み込みユーティリティです（モノラル 1ch / ステレオ 2ch）。
/// </summary>
public static class WavReader
{
    /// <summary>
    /// 1ch または 2ch の 16-bit PCM WAV を読みます。モノラル時は Right が空配列です。
    /// </summary>
    public static (Complex[] Left, Complex[] Right) ReadPcm16(string path)
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

                if (format != 1 || bitsPerSample != 16 || channels is not (1 or 2))
                {
                    throw new InvalidDataException("Expected 16-bit mono or stereo PCM WAV.");
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

        if (channels == 1)
        {
            var sampleCount = data.Length / 2;
            var left = new Complex[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                var o = i * 2;
                var s = (short)(data[o] | (data[o + 1] << 8));
                left[i] = new Complex(s / (double)short.MaxValue, 0.0);
            }

            return (left, Array.Empty<Complex>());
        }

        var stereoCount = data.Length / 4;
        var leftCh = new Complex[stereoCount];
        var rightCh = new Complex[stereoCount];
        for (var i = 0; i < stereoCount; i++)
        {
            var o = i * 4;
            var l = (short)(data[o] | (data[o + 1] << 8));
            var r = (short)(data[o + 2] | (data[o + 3] << 8));
            leftCh[i] = new Complex(l / (double)short.MaxValue, 0.0);
            rightCh[i] = new Complex(r / (double)short.MaxValue, 0.0);
        }

        return (leftCh, rightCh);
    }

    /// <summary>後方互換: ステレオ WAV のみ読みます。</summary>
    public static (Complex[] Left, Complex[] Right) ReadStereo16(string path)
    {
        var (left, right) = ReadPcm16(path);
        if (right.Length == 0)
        {
            throw new InvalidDataException("Expected 16-bit stereo (2ch) PCM WAV.");
        }

        return (left, right);
    }
}

