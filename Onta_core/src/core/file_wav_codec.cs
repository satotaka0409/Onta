using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;

namespace Onta.Core;

/// <summary>
/// 送信処理で扱うフレーム種別です。
/// </summary>
public enum TransmissionFrameKind
{
    Fh,
    Bh,
    Bd
}

/// <summary>
/// 生成されたPCMチャンクを受け取るコールバックです。
/// </summary>
public delegate void PcmChunkHandler(ReadOnlySpan<Complex> left, ReadOnlySpan<Complex> right);

/// <summary>
/// ファイル送受信に使う OFDM / 変調 / 音声条件のプロファイルです。
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
    int BlockInterleaveFactor = 1)
{
    public byte ModulationModeByte => (byte)ModulationScheme;
    public byte ChannelModeByte => (byte)ChannelMode;
    public int LeadingSilenceSamples => SampleRate / 10;
    public int TrailingSilenceSamples => SampleRate / 10;
    public int UnmodulatedPreambleSamples => SampleRate * 2;
    public int FileHeaderUnmodulatedSamples => SampleRate;
    public int BlockHeaderUnmodulatedSamples => (SampleRate * 3) / 10;
}

/// <summary>
/// 段階デコード時の探索・復号アルゴリズム調整値です。
/// </summary>
public sealed record DecodeRuntimeTuning(
    int TurboIterationsMin = 8,
    int TurboIterationsMax = 12,
    double TurboLowConfidenceLlr = 1.5,
    double TurboHighConfidenceLlr = 4.0,
    bool PreferLocalWowTracking = true,
    double WowLocalPhaseRangeRad = 0.3141592653589793,
    double WowLocalAmountRange = 0.002,
    bool AdaptiveWowOnlyOnFileHeaderBoundaries = true,
    bool ShareStereoWowFromLeft = true,
    double WowSkipRewarpAmountDelta = 0.00025,
    double WowSkipRewarpPhaseDeltaRad = 0.02,
    int DataSyncMaxFullAttempts = 12,
    int DataSyncMaxSoftOnlyAttempts = 8,
    int DataSyncMaxFullAttemptsWhenWowLocked = 4,
    int DataSyncMaxSoftOnlyAttemptsWhenWowLocked = 2,
    double SoftLlrAbortMeanAbs = 0.35,
    double SoftLlrAbortMeanAbsWhenWowLocked = 0.55,
    double SoftHardFallbackMinMeanAbsLlr = 1.0,
    /// <summary>
    /// 適応再ワープ時に先読みする秒数です。
    /// </summary>
    double WowRewarpLookaheadSeconds = 8.0)
{
    public static DecodeRuntimeTuning Default { get; } = new();
}

/// <summary>
/// 段階デコードの進行状態と中間結果を保持します。
/// </summary>
public sealed class ProgressiveDecodeState
{
    public bool HeaderReady { get; internal set; }
    public bool Completed { get; internal set; }
    public byte[]? CompletedFile { get; internal set; }
    public string? LastError { get; internal set; }
    public int AcceptedBlockCount { get; internal set; }
    public int BlockCount { get; internal set; }
    public long FileSize { get; internal set; }

    public CoreExecutionStatusBoard StatusBoard { get; } = new();

    internal int SourceLength;
    internal int Pass;
    internal int Local;
    internal int WarpedCursor;
    internal long LogicalOffset;
    internal byte[]?[]? OutputSlots;
    internal bool[]? SlotAccepted;
    internal (double Amount, double WowPhase, double FlutterPhase)? TrackedWow;
    internal bool HasTrackedWow;
    internal CoreFrameKind CurrentFrame = CoreFrameKind.Fh;
    internal int CurrentBlockIndex = -1;
    internal int HeaderRsDecodeCount;
    internal int DataBlocksDecoded;
    internal int DataBlocksAccepted;
    internal int DataAcceptedViaViterbi;
    internal int DataAcceptedViaTurbo;
    internal int DataFallbackUsed;
    internal int DataTotalAttempts;
    internal string? ReceivedFileName;
    internal Dictionary<string, int> BlockHashOwners { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, byte[]> OrphanPayloadByHash { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, string> OrphanDetailByHash { get; } = new(StringComparer.Ordinal);

    internal int? DetectedDataSubcarriers;
    internal ModulationScheme? DetectedModulationScheme;

    /// <summary>
    /// FH確定時にファイル名/サイズ/ブロック数を通知します。
    /// </summary>
    public Action<string, long, int>? FileHeaderReady;

    public void Reset()
    {
        HeaderReady = false;
        Completed = false;
        CompletedFile = null;
        LastError = null;
        AcceptedBlockCount = 0;
        BlockCount = 0;
        FileSize = 0;
        SourceLength = 0;
        Pass = 0;
        Local = 0;
        WarpedCursor = 0;
        LogicalOffset = 0;
        OutputSlots = null;
        SlotAccepted = null;
        TrackedWow = null;
        HasTrackedWow = false;
        CurrentFrame = CoreFrameKind.Fh;
        CurrentBlockIndex = -1;
        HeaderRsDecodeCount = 0;
        DataBlocksDecoded = 0;
        DataBlocksAccepted = 0;
        DataAcceptedViaViterbi = 0;
        DataAcceptedViaTurbo = 0;
        DataFallbackUsed = 0;
        DataTotalAttempts = 0;
        ReceivedFileName = null;
        BlockHashOwners.Clear();
        OrphanPayloadByHash.Clear();
        OrphanDetailByHash.Clear();
        DetectedDataSubcarriers = null;
        DetectedModulationScheme = null;
        StatusBoard.Reset();
    }

    public CoreExecutionStatus QueryExecutionStatus() => StatusBoard.Query();
}

/// <summary>
/// 段階デコード呼び出しの結果状態です。
/// </summary>
public enum ProgressiveDecodeStatus
{
    NeedMoreSamples,
    Completed,
    Failed
}

/// <summary>
/// 復号処理の統計情報です。
/// </summary>
public readonly record struct DecodeStageMetrics(
    int HeaderRsDecodeCount,
    int DataBlocksDecoded,
    int DataBlocksAccepted,
    int DataAcceptedViaViterbi,
    int DataAcceptedViaTurbo,
    int DataFallbackUsed,
    int DataTotalAttempts)
{
    public static DecodeStageMetrics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// ファイルとWAVの相互変換・段階デコードを提供する中核コーデックです。
/// </summary>
public sealed class FileWavCodec
{
    private const int FileHeaderBytes = 880;
    private const int FileNameBytes = 768;
    private const int BlockHeaderBytes = 124;
    private const int HeaderPilotBytes = 6;
    private const int HeaderVersionBytes = 2;
    private const int HeaderPrefixBytes = HeaderPilotBytes + HeaderVersionBytes;
    private const int CrcBytes = 4;
    private const int HeaderCrcDataOffset = HeaderPrefixBytes;
    private static readonly byte[] HeaderVersion = [0x00, 0x01];
    private static readonly byte[] FileHeaderPilot = [0xF0, 0xE1, 0xD2, 0xC3, 0xB4, 0xA5];
    private static readonly byte[] BlockHeaderPilot = [0x0F, 0x1E, 0x2D, 0x3C, 0x4B, 0x5A];
    private const int DataBlockBytes = 4096;
    private const int DataBlockWithCrcBytes = DataBlockBytes + CrcBytes;
    private const string DataTraceEnvVar = "ONTA_TRACE_DATA_ERRORS";
    private const int FileHeaderRepeatIntervalBlocks = 16;
    private const int InterleaveInitSeedFileHeader = unchecked((int)0x13579BDF);
    private const int InterleaveInitSeedBlock = unchecked((int)0x2468ACE1);
    private static readonly ConvolutionalCode.PunctureRate HeaderPunctureRate = ConvolutionalCode.PunctureRate.Rate1_2;

    private readonly FileWavCodecProfile _profile;

    public DecodeStageMetrics LastDecodeStageMetrics { get; private set; } = DecodeStageMetrics.Empty;

    /// <summary>指定したプロファイルでコーデックを初期化します。</summary>
    /// <param name="profile">送受信の変調・OFDM条件を含むプロファイル。</param>
    public FileWavCodec(FileWavCodecProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (_profile.BlockInterleaveFactor is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                _profile.BlockInterleaveFactor,
                "BlockInterleaveFactor must be 1 or 2.");
        }
    }

    /// <summary>
    /// 入力ファイルをWAVへエンコードし、再デコードして復元バイト列を返します。
    /// </summary>
    /// <param name="inputPath">送信元ファイルのパス。</param>
    /// <param name="wavPath">中間WAVの出力先パス。</param>
    /// <param name="restoredPath">復元したバイト列を保存する先。null の場合は保存しません。</param>
    /// <returns>復元されたファイルのバイト列。</returns>
    public byte[] EncodeDecodeRoundTrip(string inputPath, string wavPath, string? restoredPath = null)
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("入力ファイルが見つかりません。", inputPath);
        }

        var originalBytes = File.ReadAllBytes(inputPath);
        var fileInfo = new FileInfo(inputPath);
        using (var wavWriter = WavWriter.CreateStreamingPcm16(
                   wavPath,
                   _profile.SampleRate,
                   _profile.SamplePeak,
                   _profile.ChannelMode))
        {
            _ = EncodeFileToSamples(
                originalBytes,
                fileInfo,
                onPcmChunk: (leftChunk, rightChunk) =>
                {
                    wavWriter.WriteChunk(leftChunk, rightChunk);
                },
                retainAllSamples: false);
        }

        var decodedBytes = DecodeWavToFileBytes(wavPath, correctWow: false);
        if (restoredPath is not null)
        {
            File.WriteAllBytes(restoredPath, decodedBytes);
        }

        return decodedBytes;
    }

    /// <summary>ファイルバイト列をOFDMのPCMサンプルへエンコードします。</summary>
    /// <param name="fileBytes">送信するファイルのバイト列。</param>
    /// <param name="fileInfo">ファイル名などヘッダー生成に使う情報。</param>
    /// <param name="onFrameTransmitted">フレーム送信時に呼ばれるコールバック。</param>
    /// <param name="onPcmChunk">生成PCMチャンクごとのコールバック（L/Rサンプル）。</param>
    /// <param name="retainAllSamples">true の場合、全PCMを戻り値にも保持します。</param>
    /// <param name="cancellationToken">処理中断用トークン。</param>
    /// <returns>L/R のOFDM PCMサンプル。</returns>
    public (Complex[] Left, Complex[] Right) EncodeFileToSamples(
        byte[] fileBytes,
        FileInfo fileInfo,
        Action<TransmissionFrameKind>? onFrameTransmitted = null,
        PcmChunkHandler? onPcmChunk = null,
        bool retainAllSamples = true,
        CancellationToken cancellationToken = default)
    {
        if (!retainAllSamples && onPcmChunk is null)
        {
            throw new ArgumentException("onPcmChunk is required when retainAllSamples is false.", nameof(onPcmChunk));
        }

        var fileHash = Hash.ComputeSha512(fileBytes);
        var blocks = SplitDataBlocks(fileBytes);
        var blockHashes = new byte[blocks.Count][];
        for (var i = 0; i < blocks.Count; i++)
        {
            blockHashes[i] = Hash.ComputeSha256(blocks[i].Payload);
        }

        var fileHeader = BuildFileHeader(fileInfo, fileBytes.Length, blocks.Count, fileHash);
        var leftPcm = new List<Complex>(1 << 20);
        var rightPcm = new List<Complex>(1 << 20);
        var pcmEmitted = 0;

        void EnsureStereoParity(string stage)
        {
            if (_profile.ChannelMode == ChannelMode.Stereo && leftPcm.Count != rightPcm.Count)
            {
                throw new InvalidOperationException(
                    $"Stereo stream length mismatch after {stage}: L={leftPcm.Count}, R={rightPcm.Count}");
            }
        }

        void FlushPcmChunk()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (onPcmChunk is null)
            {
                return;
            }

            var remaining = leftPcm.Count - pcmEmitted;
            if (remaining <= 0)
            {
                return;
            }

            var maxFlush = Math.Max(512, _profile.SampleRate / 20);
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(remaining, maxFlush);
                var leftSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(leftPcm).Slice(pcmEmitted, count);
                ReadOnlySpan<Complex> rightSpan = ReadOnlySpan<Complex>.Empty;
                if (_profile.ChannelMode == ChannelMode.Stereo)
                {
                    rightSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rightPcm).Slice(pcmEmitted, count);
                }

                onPcmChunk(leftSpan, rightSpan);
                pcmEmitted += count;
                remaining -= count;
            }

            if (!retainAllSamples)
            {
                leftPcm.Clear();
                rightPcm.Clear();
                pcmEmitted = 0;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        AppendSilence(leftPcm, rightPcm, _profile.LeadingSilenceSamples, stereo: _profile.ChannelMode == ChannelMode.Stereo);
        EnsureStereoParity("leading silence");
        FlushPcmChunk();
        if (_profile.UnmodulatedPreambleSamples > 0)
        {
            var preambleOfdm = CreateHeaderOfdm();
            AppendHeaderPair(leftPcm, rightPcm, preambleOfdm.GenerateUnmodulated(_profile.UnmodulatedPreambleSamples));
            EnsureStereoParity("global preamble");
            FlushPcmChunk();
        }

        var openingHeaderOfdm = CreateHeaderOfdm();
        AppendFileHeaderPacket(leftPcm, rightPcm, openingHeaderOfdm, fileHeader);
        EnsureStereoParity("file header");
        FlushPcmChunk();
        onFrameTransmitted?.Invoke(TransmissionFrameKind.Fh);

        OfdmGenerator? trailingHeaderOfdm = openingHeaderOfdm;
        for (var pass = 0; pass < _profile.BlockInterleaveFactor; pass++)
        {
            var (passSc, passMod) = ResolveInterleavePassModulation(
                pass,
                _profile.ActiveSubcarriers,
                _profile.ModulationScheme);
            var headerOfdm = CreateHeaderOfdm();
            var dataOfdm = CreateDataOfdm(passSc, passMod);
            var dataPunctureRate = ResolveDataPunctureRate(passMod);
            trailingHeaderOfdm = headerOfdm;
            var order = GetBlockEmissionOrder(blocks.Count, pass);
            for (var local = 0; local < order.Length; local++)
            {
                if (local > 0 && (local % FileHeaderRepeatIntervalBlocks) == 0)
                {
                    AppendFileHeaderPacket(leftPcm, rightPcm, headerOfdm, fileHeader);
                    EnsureStereoParity("file header repeat");
                    FlushPcmChunk();
                    onFrameTransmitted?.Invoke(TransmissionFrameKind.Fh);
                }

                var blockIndex = order[local];
                var blockHeader = BuildBlockHeader(
                    subcarriers: (byte)passSc,
                    modulationMode: (byte)passMod,
                    channelMode: _profile.ChannelModeByte,
                    blockIndex: blockIndex,
                    blockSize: blocks[blockIndex].PayloadLength,
                    blockHash: blockHashes[blockIndex],
                    fileHash: fileHash);
                AppendHeaderPackets(
                    leftPcm,
                    rightPcm,
                    headerOfdm,
                    blockHeader,
                    blockHeader,
                    _profile.BlockHeaderUnmodulatedSamples,
                    InterleaveInitSeedBlock);
                EnsureStereoParity("block header");
                FlushPcmChunk();
                onFrameTransmitted?.Invoke(TransmissionFrameKind.Bh);
                AppendModulatedDataBlock(
                    leftPcm,
                    rightPcm,
                    dataOfdm,
                    blocks[blockIndex].Payload,
                    InterleaveInitSeedBlock,
                    dataPunctureRate);
                EnsureStereoParity("block data");
                FlushPcmChunk();
                onFrameTransmitted?.Invoke(TransmissionFrameKind.Bd);
            }
        }

        AppendFileHeaderPacket(leftPcm, rightPcm, trailingHeaderOfdm!, fileHeader);
        EnsureStereoParity("trailing file header");
        FlushPcmChunk();
        onFrameTransmitted?.Invoke(TransmissionFrameKind.Fh);

        AppendSilence(leftPcm, rightPcm, _profile.TrailingSilenceSamples, stereo: _profile.ChannelMode == ChannelMode.Stereo);
        EnsureStereoParity("trailing silence");
        FlushPcmChunk();

        if (!retainAllSamples)
        {
            return (Array.Empty<Complex>(), Array.Empty<Complex>());
        }

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
        LastDecodeStageMetrics = DecodeStageMetrics.Empty;
        var state = new ProgressiveDecodeState();
        var status = DecodePcmSamplesProgressive(
            leftSamples,
            rightSamples,
            state,
            correctWow,
            wowParams,
            tuning,
            allowIncomplete: false);
        LastDecodeStageMetrics = BuildDecodeStageMetrics(state);
        if (status == ProgressiveDecodeStatus.Completed && state.CompletedFile is not null)
        {
            return state.CompletedFile;
        }

        throw new InvalidDataException(state.LastError ?? "Decode failed.");
    }

    private static DecodeStageMetrics BuildDecodeStageMetrics(ProgressiveDecodeState state)
    {
        return new DecodeStageMetrics(
            HeaderRsDecodeCount: state.HeaderRsDecodeCount,
            DataBlocksDecoded: state.DataBlocksDecoded,
            DataBlocksAccepted: state.DataBlocksAccepted,
            DataAcceptedViaViterbi: state.DataAcceptedViaViterbi,
            DataAcceptedViaTurbo: state.DataAcceptedViaTurbo,
            DataFallbackUsed: state.DataFallbackUsed,
            DataTotalAttempts: state.DataTotalAttempts);
    }

    /// <summary>
    /// PCM配列を入力として段階デコードを実行します。
    /// </summary>
    /// <param name="leftSamples">左チャネルPCMサンプル列。</param>
    /// <param name="rightSamples">右チャネルPCMサンプル列（モノラル時は空配列）。</param>
    /// <param name="state">呼び出し間で保持する段階デコード状態。</param>
    /// <param name="correctWow">true の場合、WOW/Flutter補正を有効化します。</param>
    /// <param name="wowParams">既知のWOW/Flutter補正パラメータ。</param>
    /// <param name="tuning">復号探索の調整パラメータ。</param>
    /// <param name="allowIncomplete">true の場合、不足サンプル時に NeedMoreSamples を返します。</param>
    /// <returns>段階デコードの結果状態。</returns>
    public ProgressiveDecodeStatus DecodePcmSamplesProgressive(
        Complex[] leftSamples,
        Complex[] rightSamples,
        ProgressiveDecodeState state,
        bool correctWow = true,
        (double Amount, double WowPhase, double FlutterPhase)? wowParams = null,
        DecodeRuntimeTuning? tuning = null,
        bool allowIncomplete = true)
    {
        ArgumentNullException.ThrowIfNull(state);
        tuning ??= DecodeRuntimeTuning.Default;
        state.LastError = null;

        if (state.Completed && state.CompletedFile is not null)
        {
            return ProgressiveDecodeStatus.Completed;
        }

        if (_profile.ChannelMode == ChannelMode.Mono && rightSamples.Length != 0)
        {
            state.LastError = "モノラル受信には1ch WAVが必要です。";
            return ProgressiveDecodeStatus.Failed;
        }

        if (_profile.ChannelMode == ChannelMode.Stereo && rightSamples.Length != leftSamples.Length)
        {
            state.LastError = "ステレオ受信にはL/R同長の2ch WAVが必要です。";
            return ProgressiveDecodeStatus.Failed;
        }

        if (leftSamples.Length < state.SourceLength)
        {
            state.Reset();
        }

        state.SourceLength = leftSamples.Length;

        var headerOfdm = CreateHeaderOfdm();
        var dataOfdmCache = new Dictionary<(int Sc, ModulationScheme Mod), OfdmGenerator>();

        OfdmGenerator ResolveDataOfdmFor(int activeSubcarriers, ModulationScheme modulationScheme)
        {
            var key = (activeSubcarriers, modulationScheme);
            if (dataOfdmCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var created = CreateDataOfdm(activeSubcarriers, modulationScheme);
            dataOfdmCache[key] = created;
            return created;
        }

        var fhPacketSamples = HeaderPacketSamples(headerOfdm, FileHeaderBytes, _profile.FileHeaderUnmodulatedSamples);
        var bhPacketSamplesBase = HeaderPacketSamples(headerOfdm, BlockHeaderBytes, _profile.BlockHeaderUnmodulatedSamples);

        var adaptiveWow = wowParams is null && correctWow && !state.HasTrackedWow;
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

            state.TrackedWow = known;
            state.HasTrackedWow = true;
        }
        else if (state.HasTrackedWow && state.TrackedWow is { } tracked)
        {
            leftSamples = headerOfdm.CorrectWowFlutterWithParams(
                leftSamples,
                tracked.Amount,
                tracked.WowPhase,
                tracked.FlutterPhase);
            if (_profile.ChannelMode == ChannelMode.Stereo)
            {
                rightSamples = headerOfdm.CorrectWowFlutterWithParams(
                    rightSamples,
                    tracked.Amount,
                    tracked.WowPhase,
                    tracked.FlutterPhase);
            }
        }

        var trackedWow = state.TrackedWow ?? (Amount: 0.01, WowPhase: 0.0, FlutterPhase: 0.0);
        var hasTrackedWow = state.HasTrackedWow;

        var warpedCursor = state.HeaderReady ? state.WarpedCursor : 0;
        var logicalOffset = state.HeaderReady ? state.LogicalOffset : 0L;
        var coarseRadius = Math.Max(headerOfdm.SamplesPerOfdmSymbol * 8, _profile.SampleRate / 50);
        var fineRadius = Math.Max(headerOfdm.SamplesPerOfdmSymbol * 2, _profile.SampleRate / 200);
        var rewarpLookaheadSamples = Math.Max(
            headerOfdm.SamplesPerOfdmSymbol * 64,
            (int)(Math.Max(1.0, tuning.WowRewarpLookaheadSeconds) * _profile.SampleRate));

        int ResolveRewarpLength(int remaining) =>
            Math.Min(remaining, rewarpLookaheadSamples);

        void PersistCursor()
        {
            state.WarpedCursor = warpedCursor;
            state.LogicalOffset = logicalOffset;
            state.TrackedWow = hasTrackedWow ? trackedWow : state.TrackedWow;
            state.HasTrackedWow = hasTrackedWow;
        }

        void PublishStatus(CoreFrameKind frame, int blockIndex, double? errorRatePercent = null)
        {
            state.CurrentFrame = frame;
            state.CurrentBlockIndex = blockIndex;
            var blockCount = Math.Max(0, state.BlockCount);
            var totalWork = Math.Max(1, Math.Max(1, blockCount) * Math.Max(1, _profile.BlockInterleaveFactor));
            var doneWork = state.HeaderReady
                ? (state.Pass * Math.Max(1, blockCount)) + state.Local
                : 0;
            var percent = state.Completed
                ? 100.0
                : !state.HeaderReady
                    ? 2.0
                    : Math.Clamp(5.0 + (90.0 * doneWork / totalWork), 0.0, 99.0);

            state.StatusBoard.SetProgress(new CoreProgressInfo(
                CurrentFrame: frame,
                CurrentBlockIndex: blockIndex,
                PassIndex: state.Pass,
                AcceptedBlockCount: state.AcceptedBlockCount,
                TotalBlockCount: blockCount,
                ProgressPercent: percent));

            state.StatusBoard.SetErrorFrameKind(frame);

            if (errorRatePercent is { } err)
            {
                state.StatusBoard.SetErrorRate(err, frame);
            }

            if (hasTrackedWow)
            {
                var wowDisplay = trackedWow.Amount * 200.0;
                state.StatusBoard.SetWowFlutterPercent(wowDisplay, wowDisplay);
            }

            if (state.HeaderReady)
            {
                state.StatusBoard.SetFileInfo(
                    string.IsNullOrWhiteSpace(state.ReceivedFileName) ? state.StatusBoard.FileName : state.ReceivedFileName,
                    $"{state.FileSize:N0} bytes",
                    blockCount.ToString());
            }
        }

        ProgressiveDecodeStatus NeedMoreOrFail(string? detail = null)
        {
            PersistCursor();
            if (allowIncomplete)
            {
                state.LastError = null;
                return ProgressiveDecodeStatus.NeedMoreSamples;
            }

            state.LastError = detail ?? "Incomplete PCM stream for decode.";
            return ProgressiveDecodeStatus.Failed;
        }

        var lastWowRewarpApplied = false;

        void ApplyAdaptiveWowCorrection(ref Complex[] channelSamples, bool useRightChannel)
        {
            lastWowRewarpApplied = false;
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

            var newParams = (diag.Value.Amount, diag.Value.WowPhase, diag.Value.FlutterPhase);
            var skipRewarp = hasTrackedWow
                && Math.Abs(newParams.Amount - trackedWow.Amount) < tuning.WowSkipRewarpAmountDelta
                && Math.Abs(WrapPhaseDelta(newParams.WowPhase - trackedWow.WowPhase)) < tuning.WowSkipRewarpPhaseDeltaRad
                && Math.Abs(WrapPhaseDelta(newParams.FlutterPhase - trackedWow.FlutterPhase)) < tuning.WowSkipRewarpPhaseDeltaRad;
            trackedWow = newParams;
            hasTrackedWow = true;
            if (skipRewarp)
            {
                return;
            }

            var tailLength = ResolveRewarpLength(channelSamples.Length - warpedCursor);
            if (tailLength <= 0)
            {
                return;
            }

            RewarpTailInPlace(ref channelSamples, warpedCursor, tailLength, newParams.Amount, newParams.WowPhase, newParams.FlutterPhase);
            lastWowRewarpApplied = true;
        }

        void RewarpTailInPlace(
            ref Complex[] channelSamples,
            int start,
            int length,
            double amount,
            double wowPhase,
            double flutterPhase)
        {
            var rented = System.Buffers.ArrayPool<Complex>.Shared.Rent(length);
            try
            {
                Array.Copy(channelSamples, start, rented, 0, length);
                headerOfdm.CorrectWowFlutterWithParamsInPlace(rented, length, amount, wowPhase, flutterPhase);
                Array.Copy(rented, 0, channelSamples, start, length);
            }
            finally
            {
                System.Buffers.ArrayPool<Complex>.Shared.Return(rented, clearArray: false);
            }
        }

        void ApplyAdaptiveWowCorrectionPair()
        {
            ApplyAdaptiveWowCorrection(ref leftSamples, useRightChannel: false);
            if (_profile.ChannelMode != ChannelMode.Stereo)
            {
                return;
            }

            if (tuning.ShareStereoWowFromLeft)
            {
                if (!hasTrackedWow || !lastWowRewarpApplied)
                {
                    return;
                }

                var remaining = rightSamples.Length - warpedCursor;
                if (remaining <= 0)
                {
                    return;
                }

                RewarpTailInPlace(
                    ref rightSamples,
                    warpedCursor,
                    ResolveRewarpLength(remaining),
                    trackedWow.Amount,
                    trackedWow.WowPhase,
                    trackedWow.FlutterPhase);
                return;
            }

            ApplyAdaptiveWowCorrection(ref rightSamples, useRightChannel: true);
        }

        static double WrapPhaseDelta(double phase)
        {
            var twoPi = 2.0 * Math.PI;
            phase %= twoPi;
            if (phase > Math.PI)
            {
                phase -= twoPi;
            }
            else if (phase < -Math.PI)
            {
                phase += twoPi;
            }

            return phase;
        }

        try
        {
            if (!state.HeaderReady)
            {
                PublishStatus(CoreFrameKind.Fh, blockIndex: -1);

                var minForFh = _profile.LeadingSilenceSamples
                    + _profile.UnmodulatedPreambleSamples
                    + fhPacketSamples
                    + headerOfdm.SamplesPerOfdmSymbol;
                if (leftSamples.Length < minForFh)
                {
                    return NeedMoreOrFail();
                }

                warpedCursor = SkipSamples(leftSamples, 0, _profile.LeadingSilenceSamples);
                warpedCursor = SkipSamples(leftSamples, warpedCursor, _profile.UnmodulatedPreambleSamples);
                logicalOffset = warpedCursor;

                state.StatusBoard.SetProgress(new CoreProgressInfo(
                    CurrentFrame: CoreFrameKind.Fh,
                    CurrentBlockIndex: -1,
                    PassIndex: 0,
                    AcceptedBlockCount: 0,
                    TotalBlockCount: 0,
                    ProgressPercent: 3.0));
                ApplyAdaptiveWowCorrectionPair();
                SkipHeaderUnmodulatedPreamble(
                    leftSamples,
                    ref warpedCursor,
                    ref logicalOffset,
                    _profile.FileHeaderUnmodulatedSamples);
                ApplyAdaptiveWowCorrectionPair();
                state.StatusBoard.SetProgress(new CoreProgressInfo(
                    CurrentFrame: CoreFrameKind.Fh,
                    CurrentBlockIndex: -1,
                    PassIndex: 0,
                    AcceptedBlockCount: 0,
                    TotalBlockCount: 0,
                    ProgressPercent: 4.0));

                byte[] fileHeader;
                try
                {
                    fileHeader = DecodeHeaderPacketSynced(
                        leftSamples,
                        rightSamples,
                        ref warpedCursor,
                        ref logicalOffset,
                        headerOfdm,
                        FileHeaderBytes,
                        FileHeaderPilot,
                        coarseRadius,
                        InterleaveInitSeedFileHeader);
                    state.HeaderRsDecodeCount++;
                    EnsureHeaderCrc(fileHeader, "file header");
                }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                {
                    state.LastError = ex.Message;
                    PersistCursor();
                    return ProgressiveDecodeStatus.Failed;
                }

                var fileSize = BinaryPrimitives.ReadInt64BigEndian(fileHeader.AsSpan(860, 8));
                var blockCount = (int)BinaryPrimitives.ReadInt64BigEndian(fileHeader.AsSpan(868, 8));
                if (fileSize < 0 || blockCount < 0)
                {
                    state.LastError = "Invalid file header size/block count.";
                    state.StatusBoard.Complete(faulted: true, state.LastError);
                    return ProgressiveDecodeStatus.Failed;
                }

                var fhFileName = ReadFileHeaderFileName(fileHeader);
                state.FileSize = fileSize;
                state.BlockCount = blockCount;
                state.ReceivedFileName = fhFileName;
                state.OutputSlots = new byte[blockCount][];
                state.SlotAccepted = new bool[blockCount];
                state.HeaderReady = true;
                state.Pass = 0;
                state.Local = 0;
                PersistCursor();
                var displayName = string.IsNullOrWhiteSpace(fhFileName) ? "(不明)" : fhFileName;
                state.StatusBoard.SetFileInfo(displayName, $"{fileSize:N0} bytes", blockCount.ToString());
                state.StatusBoard.SetProgress(new CoreProgressInfo(
                    CurrentFrame: CoreFrameKind.Fh,
                    CurrentBlockIndex: -1,
                    PassIndex: 0,
                    AcceptedBlockCount: 0,
                    TotalBlockCount: blockCount,
                    ProgressPercent: 5.0));
                try
                {
                    state.FileHeaderReady?.Invoke(displayName, fileSize, blockCount);
                }
                catch
                {
                }

                PublishStatus(CoreFrameKind.Fh, blockIndex: -1, errorRatePercent: 0.0);
            }

            var outputSlots = state.OutputSlots ?? throw new InvalidOperationException("Output slots missing.");
            var slotAccepted = state.SlotAccepted ?? throw new InvalidOperationException("Slot flags missing.");
            var fileSizeReady = state.FileSize;
            var blockCountReady = state.BlockCount;
            var traceDataErrors = string.Equals(
                Environment.GetEnvironmentVariable(DataTraceEnvVar),
                "1",
                StringComparison.Ordinal);

            static string HashToKey(ReadOnlySpan<byte> hash)
            {
                return Convert.ToHexString(hash);
            }

            void RegisterHashOwner(int blockIndex, ReadOnlySpan<byte> expectedHash)
            {
                if (blockIndex < 0 || blockIndex >= blockCountReady)
                {
                    return;
                }

                var key = HashToKey(expectedHash);
                state.BlockHashOwners[key] = blockIndex;

                if (state.OrphanPayloadByHash.TryGetValue(key, out var orphanPayload)
                    && !slotAccepted[blockIndex])
                {
                    outputSlots[blockIndex] = orphanPayload;
                    slotAccepted[blockIndex] = true;
                    state.OrphanPayloadByHash.Remove(key);
                    state.OrphanDetailByHash.Remove(key);
                    state.LastError = $"ORPHAN-RESOLVED hash={key[..Math.Min(12, key.Length)]} BLK-{blockIndex}";
                }
            }

            bool TryResolveOwnerByPayloadHash(byte[] payload, out int ownerBlock)
            {
                ownerBlock = -1;
                var key = HashToKey(Hash.ComputeSha256(payload));
                if (state.BlockHashOwners.TryGetValue(key, out var block)
                    && block >= 0
                    && block < blockCountReady)
                {
                    ownerBlock = block;
                    return true;
                }

                return false;
            }

            void SaveOrphanPayload(byte[] payload, string detail)
            {
                var key = HashToKey(Hash.ComputeSha256(payload));
                state.OrphanPayloadByHash[key] = payload;
                state.OrphanDetailByHash[key] = detail;
                state.LastError = $"ORPHAN hash={key[..Math.Min(12, key.Length)]} {detail}";
            }

            for (var pass = state.Pass; pass < _profile.BlockInterleaveFactor; pass++)
            {
                var passFhPacketSamples = fhPacketSamples;
                var passBhPacketSamples = bhPacketSamplesBase;

                var order = GetBlockEmissionOrder(blockCountReady, pass);
                var localStart = pass == state.Pass ? state.Local : 0;
                for (var local = localStart; local < order.Length; local++)
                {
                    var expectedBlockIndex = order[local];

                    void MarkBlockError(string message)
                    {
                        state.AcceptedBlockCount = 0;
                        for (var i = 0; i < slotAccepted.Length; i++)
                        {
                            if (slotAccepted[i])
                            {
                                state.AcceptedBlockCount++;
                            }
                        }

                        state.LastError = message;
                        state.Pass = pass;
                        state.Local = local + 1;
                        PersistCursor();
                        PublishStatus(CoreFrameKind.Bd, expectedBlockIndex, errorRatePercent: 100.0);
                    }

                    var minForBlock = passBhPacketSamples + headerOfdm.SamplesPerOfdmSymbol;
                    if (local > 0 && (local % FileHeaderRepeatIntervalBlocks) == 0)
                    {
                        minForBlock += passFhPacketSamples;
                    }

                    if (leftSamples.Length - warpedCursor < minForBlock)
                    {
                        state.Pass = pass;
                        state.Local = local;
                        return NeedMoreOrFail();
                    }
                    try
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
                                fineRadius,
                                InterleaveInitSeedFileHeader);
                            state.HeaderRsDecodeCount++;
                            EnsureHeaderCrc(midFh, "mid file header");
                        }

                        SkipHeaderUnmodulatedPreamble(
                            leftSamples,
                            ref warpedCursor,
                            ref logicalOffset,
                            _profile.BlockHeaderUnmodulatedSamples);
                        if (!tuning.AdaptiveWowOnlyOnFileHeaderBoundaries)
                        {
                            ApplyAdaptiveWowCorrectionPair();
                        }

                        var blockHeader = DecodeHeaderPacketSynced(
                            leftSamples,
                            rightSamples,
                            ref warpedCursor,
                            ref logicalOffset,
                            headerOfdm,
                            BlockHeaderBytes,
                            BlockHeaderPilot,
                            fineRadius,
                            InterleaveInitSeedBlock);
                        state.HeaderRsDecodeCount++;
                        EnsureHeaderCrc(blockHeader, "block header");
                        PublishStatus(CoreFrameKind.Bh, expectedBlockIndex, errorRatePercent: 0.0);

                        var blockIndex = BinaryPrimitives.ReadInt64BigEndian(blockHeader.AsSpan(12, 8));
                        var blockSize = BinaryPrimitives.ReadInt32BigEndian(blockHeader.AsSpan(20, 4));
                        if (blockSize < 0 || blockSize > DataBlockBytes)
                        {
                            throw new InvalidDataException($"Invalid block size {blockSize}.");
                        }

                        var ownerKnown = blockIndex >= 0 && blockIndex < blockCountReady;
                        var ownerBlockIndex = ownerKnown ? (int)blockIndex : -1;
                        var expectedHash = blockHeader.AsSpan(24, 32).ToArray();
                        if (ownerKnown)
                        {
                            RegisterHashOwner(ownerBlockIndex, expectedHash);
                        }

                        var (blockSc, blockModulation) = ReadBlockDataModulation(blockHeader);
                        state.DetectedDataSubcarriers = blockSc;
                        state.DetectedModulationScheme = blockModulation;
                        var blockDataOfdm = ResolveDataOfdmFor(blockSc, blockModulation);
                        var blockDataPunctureRate = ResolveDataPunctureRate(blockModulation);
                        PublishStatus(CoreFrameKind.Bd, expectedBlockIndex);

                        var dataSamplesNeeded = DataPacketSamples(
                            blockDataOfdm,
                            blockSize,
                            _profile.ChannelMode,
                            blockModulation);
                        if (leftSamples.Length - warpedCursor < dataSamplesNeeded)
                        {
                            warpedCursor = Math.Max(0, warpedCursor - passBhPacketSamples);
                            logicalOffset = Math.Max(0, logicalOffset - passBhPacketSamples);
                            state.Pass = pass;
                            state.Local = local;
                            return NeedMoreOrFail();
                        }

                        var padded = DecodeDataBlockSynced(
                            leftSamples,
                            rightSamples,
                            ref warpedCursor,
                            ref logicalOffset,
                            blockDataOfdm,
                            Math.Max(blockDataOfdm.SamplesPerOfdmSymbol * 2, _profile.SampleRate / 200),
                            expectedBlockHash: expectedHash,
                            payloadLength: blockSize,
                            modulationScheme: blockModulation,
                            tuning: tuning,
                            interleaveInitSeed: InterleaveInitSeedBlock,
                            punctureRate: blockDataPunctureRate,
                            wowLocked: hasTrackedWow,
                            statusBoard: state.StatusBoard,
                            out var diag);
                        state.DataBlocksDecoded++;
                        state.DataTotalAttempts += diag.TotalAttempts;
                        if (diag.FallbackUsed)
                        {
                            state.DataFallbackUsed++;
                        }

                        if (traceDataErrors)
                        {
                            Console.WriteLine(
                                $"[DATA-DIAG] pass={pass} block={expectedBlockIndex} size={blockSize} hardOK={diag.HardMatchSucceeded} softOK={diag.SoftMatchSucceeded} fallback={diag.FallbackUsed} startDelta={diag.StartDeltaSamples} attempts={diag.TotalAttempts}");
                        }

                        var acceptable = IsDataBlockAcceptable(padded, expectedHash, blockSize);
                        if (acceptable)
                        {
                            state.DataBlocksAccepted++;
                            if (diag.HardMatchSucceeded)
                            {
                                state.DataAcceptedViaViterbi++;
                            }

                            if (diag.SoftMatchSucceeded)
                            {
                                state.DataAcceptedViaTurbo++;
                            }
                        }

                        var payload = new byte[blockSize];
                        Buffer.BlockCopy(padded, 0, payload, 0, blockSize);
                        var effectiveBlockIndex = ownerKnown ? ownerBlockIndex : expectedBlockIndex;
                        var acceptedForStatus = acceptable && ownerKnown;
                        if (acceptable && ownerKnown)
                        {
                            outputSlots[ownerBlockIndex] = payload;
                            slotAccepted[ownerBlockIndex] = true;
                        }
                        else if (acceptable)
                        {
                            SaveOrphanPayload(payload, $"孤立ブロック index={blockIndex} (pass {pass}, local {local})");
                        }
                        else if (!acceptable)
                        {
                            if (TryResolveOwnerByPayloadHash(payload, out var resolvedOwner))
                            {
                                outputSlots[resolvedOwner] = payload;
                                slotAccepted[resolvedOwner] = true;
                                effectiveBlockIndex = resolvedOwner;
                                acceptedForStatus = true;
                                state.LastError =
                                    $"ORPHAN-RESOLVED hash一致で BLK-{resolvedOwner} に組み込み (pass {pass}, local {local})";
                            }
                            else
                            {
                                var detail = ownerKnown
                                    ? $"孤立BLK-{ownerBlockIndex} 未一致 (pass {pass}, local {local})"
                                    : $"孤立ブロック index={blockIndex} (pass {pass}, local {local})";
                                SaveOrphanPayload(payload, detail);
                            }
                        }

                        state.Pass = pass;
                        state.Local = local + 1;
                        PersistCursor();

                        state.AcceptedBlockCount = 0;
                        for (var i = 0; i < slotAccepted.Length; i++)
                        {
                            if (slotAccepted[i])
                            {
                                state.AcceptedBlockCount++;
                            }
                        }

                        if (acceptedForStatus)
                        {
                            state.LastError = null;
                        }
                        PublishStatus(
                            CoreFrameKind.Bd,
                            effectiveBlockIndex,
                            errorRatePercent: acceptedForStatus ? 0.0 : 100.0);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                    {
                        MarkBlockError($"BLK-{expectedBlockIndex} 復号エラー: {ex.Message}");
                        if (warpedCursor + headerOfdm.SamplesPerOfdmSymbol <= leftSamples.Length)
                        {
                            warpedCursor += headerOfdm.SamplesPerOfdmSymbol;
                            logicalOffset += headerOfdm.SamplesPerOfdmSymbol;
                        }

                        continue;
                    }
                }

                state.Pass = pass + 1;
                state.Local = 0;
                PersistCursor();
            }

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
                        fineRadius,
                        InterleaveInitSeedFileHeader);
                    state.HeaderRsDecodeCount++;
                    EnsureHeaderCrc(endFh, "trailing file header");
                }
            }
            catch (InvalidDataException)
            {
            }

            var output = new byte[fileSizeReady];
            var writeOffset = 0;
            for (var i = 0; i < blockCountReady; i++)
            {
                if (outputSlots[i] is null)
                {
                    state.LastError = $"Missing decoded block {i}.";
                    PersistCursor();
                    return ProgressiveDecodeStatus.Failed;
                }

                var payload = outputSlots[i]!;
                if (writeOffset + payload.Length > output.Length)
                {
                    state.LastError = "Decoded payload exceeds file size.";
                    return ProgressiveDecodeStatus.Failed;
                }

                Buffer.BlockCopy(payload, 0, output, writeOffset, payload.Length);
                writeOffset += payload.Length;
            }

            if (writeOffset != fileSizeReady)
            {
                state.LastError = $"Decoded size mismatch: got {writeOffset}, expected {fileSizeReady}.";
                return ProgressiveDecodeStatus.Failed;
            }

            state.CompletedFile = output;
            state.Completed = true;
            state.LastError = null;
            PersistCursor();
            PublishStatus(CoreFrameKind.Fh, blockIndex: -1, errorRatePercent: 0.0);
            state.StatusBoard.Complete(faulted: false);
            return ProgressiveDecodeStatus.Completed;
        }
        catch (InvalidDataException ex)
        {
            if (allowIncomplete)
            {
                var remaining = leftSamples.Length - warpedCursor;
                var minContinue = Math.Min(fhPacketSamples, bhPacketSamplesBase) + headerOfdm.SamplesPerOfdmSymbol;
                if (remaining < minContinue)
                {
                    return NeedMoreOrFail();
                }
            }

            state.LastError = ex.Message;
            PersistCursor();
            return ProgressiveDecodeStatus.Failed;
        }
        catch (Exception ex)
        {
            state.LastError = ex.Message;
            PersistCursor();
            return ProgressiveDecodeStatus.Failed;
        }
    }

    private OfdmGenerator CreateHeaderOfdm()
    {
        var groupB = OfdmConfig.ResolveGroupBLeftBins();
        const int headerSubcarriers = 9;
        var grid = OfdmConfig.ResolveCarrierGrid(headerSubcarriers);
        var fftSize = OfdmConfig.ResolveFftSize(headerSubcarriers, _profile.ChannelMode);
        var config = new OfdmConfig(
            fftSize: fftSize,
            activeSubcarriers: groupB.Length,
            cyclicPrefixLength: _profile.HeaderCyclicPrefixLength,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Bpsk,
            channelMode: ChannelMode.Mono,
            enableFrequencyInterleaving: true,
            pilotSpacing: 9,
            stereoFrequencyShiftBins: _profile.StereoFrequencyShiftBins,
            sampleRate: _profile.SampleRate,
            frequencyInterleaveIntervalSymbols: 1,
            randomSeed: _profile.RandomSeed,
            conceptualLeftBins: groupB,
            carrierGrid: grid);

        return new OfdmGenerator(config);
    }

    private OfdmGenerator CreateDataOfdm(int activeSubcarriers, ModulationScheme modulationScheme)
    {
        var grid = OfdmConfig.ResolveCarrierGrid(activeSubcarriers);
        var fftSize = OfdmConfig.ResolveFftSize(activeSubcarriers, _profile.ChannelMode);
        var config = new OfdmConfig(
            fftSize: fftSize,
            activeSubcarriers: activeSubcarriers,
            cyclicPrefixLength: _profile.DataCyclicPrefixLength,
            ofdmSymbolCount: 1,
            modulationScheme: modulationScheme,
            channelMode: _profile.ChannelMode,
            enableFrequencyInterleaving: true,
            pilotSpacing: 9,
            stereoFrequencyShiftBins: _profile.StereoFrequencyShiftBins,
            sampleRate: _profile.SampleRate,
            frequencyInterleaveIntervalSymbols: 1,
            randomSeed: _profile.RandomSeed,
            carrierGrid: grid);

        return new OfdmGenerator(config);
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
            _profile.FileHeaderUnmodulatedSamples,
            InterleaveInitSeedFileHeader);
    }

    /// <summary>
    /// ヘッダー前置無変調区間と符号化ヘッダーをPCMへ追加します。
    /// </summary>
    /// <param name="leftPcm">左チャネル出力バッファ。</param>
    /// <param name="rightPcm">右チャネル出力バッファ。</param>
    /// <param name="ofdm">ヘッダー変調に使うOFDM生成器。</param>
    /// <param name="leftHeaderBytes">送信するヘッダーバイト列。</param>
    /// <param name="rightHeaderBytes">将来拡張用の右ヘッダーバイト列。</param>
    /// <param name="unmodulatedSamples">前置の無変調サンプル数。</param>
    /// <param name="interleaveInitSeed">周波数インタリーブ初期シード。</param>
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
                ApplyReedSolomon(leftHeaderBytes),
                terminate: true,
                punctureRate: HeaderPunctureRate));

        var modulated = ofdm.ModulateBits(leftBits, leftPcm.Count, interleaveInitSeed);
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

    /// <summary>
    /// ヘッダー前置無変調サンプルを読み飛ばします。
    /// </summary>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="warpedCursor">warpedCursor を指定します。</param>
    /// <param name="logicalOffset">logicalOffset を指定します。</param>
    /// <param name="unmodulatedSamples">unmodulatedSamples を指定します。</param>
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
    /// 現在カーソル近傍でヘッダーパケット同期復号を行います。
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
    /// <returns>処理結果。</returns>
    private static byte[] DecodeHeaderPacketSynced(
        Complex[] leftSamples,
        Complex[] rightSamples,
        ref int warpedCursor,
        ref long logicalOffset,
        OfdmGenerator ofdm,
        int payloadLength,
        byte[]? expectedPilot,
        int searchRadius,
        int interleaveInitSeed)
    {
        var rsByteLength = GetReedSolomonEncodedLength(payloadLength);
        var convByteLength = GetConvolutionalEncodedLength(rsByteLength, HeaderPunctureRate);
        var bitCount = convByteLength * 8;
        var stereoSplit = false;
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
                interleaveInitSeed,
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
                        interleaveInitSeed,
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
        int interleaveInitSeed,
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

            payload = DecodeHeaderFromSoftLlrs(llrs, payloadLength, rsByteLength);
            if (expectedPilot is not null && !HeaderPrefixMatches(payload, expectedPilot))
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
        var rsEncoded = ConvolutionalCode.DecodeSoftToInfoLlrs(
            llrs,
            rsByteLength,
            out _,
            terminated: true,
            punctureRate: HeaderPunctureRate);
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

        Add(expectedStart);
        Add(ofdm.FindBestSymbolStart(samples, expectedStart, Math.Min(searchRadius, symbolLength), useRightChannel));
        for (var radius = step; radius <= searchRadius; radius += step)
        {
            Add(expectedStart - radius);
            Add(expectedStart + radius);
        }

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

    private static void AppendModulatedDataBlock(
        List<Complex> leftPcm,
        List<Complex> rightPcm,
        OfdmGenerator ofdm,
        byte[] payload,
        int interleaveInitSeed,
        ConvolutionalCode.PunctureRate punctureRate)
    {
        var packed = PackDataBlockWithCrc(payload);
        var turboEncoded = EncodeTurboBlock(packed);
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

    /// <summary>
    /// データペイロードにCRCを付与し Turbo 入力長へパディングします。
    /// </summary>
    /// <param name="payload">payload を指定します。</param>
    /// <returns>処理結果。</returns>
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
    /// 指定パスのブロック送信順序を返します。
    /// </summary>
    /// <param name="blockCount">ブロック総数。</param>
    /// <param name="passIndex">送信パス番号。</param>
    /// <returns>送信順序のブロック番号配列。</returns>
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
    /// 再送パスに応じたサブキャリア数と変調方式を決定します。
    /// </summary>
    /// <param name="passIndex">送信パス番号。</param>
    /// <param name="baseSubcarriers">基準サブキャリア数。</param>
    /// <param name="baseModulation">基準変調方式。</param>
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
            36 or 27 => 18,
            18 or 9 => 9,
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
        out DataDecodeDiag diag)
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
                            BitsToBytesMsb(bits), turboEncodedLength, terminated: true, punctureRate: punctureRate);
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
                            noiseVariance: 0.05,
                            interleaveInitSeed,
                            modulationScheme,
                            statusBoard);
                        end = cursor;
                        var turboEncoded = ConvolutionalCode.DecodeSoftToInfoLlrs(
                            qamLlrs, turboEncodedLength, out var infoLlrs, terminated: true, punctureRate: punctureRate);
                        ClampLlrsInPlace(infoLlrs, 16.0);

                        var meanAbs = MeanAbsLlrs(infoLlrs);
                        if (meanAbs < softLlrAbortMeanAbs)
                        {
                            continue;
                        }

                        var turboIterations = ResolveTurboIterations(infoLlrs, tuning);
                        var softCandidate = DecodeTurboBlockFromLlrs(infoLlrs, turboEncoded, paddedLen, turboIterations);
                        if (IsDataBlockAcceptable(softCandidate, expectedBlockHash, payloadLength))
                        {
                            candidate = softCandidate;
                        }
                        else if (meanAbs >= tuning.SoftHardFallbackMinMeanAbsLlr)
                        {
                            var hardCandidate = DecodeTurboBlock(turboEncoded, paddedLen, turboIterations);
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
                qamLlrs, turboEncodedLength, out var infoLlrs, terminated: true, punctureRate: punctureRate);
            ClampLlrsInPlace(infoLlrs, 16.0);
            _ = lastError;
            var turboIterations = ResolveTurboIterations(infoLlrs, tuning);
            var softCandidate = DecodeTurboBlockFromLlrs(infoLlrs, turboEncoded, paddedLen, turboIterations);
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
                var hardCandidate = DecodeTurboBlock(turboEncoded, paddedLen, turboIterations);
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
        CoreExecutionStatusBoard? statusBoard = null)
    {
        statusBoard?.SetFftStereoMode(stereoSplit);

        Action<Complex[], int>? onIqFrame = statusBoard is null
            ? null
            : (symbols, count) => statusBoard.SetIqFrame(symbols.AsSpan(0, count), modulationScheme);
        Action<Complex[], int>? onFftLeftFrame = statusBoard is null
            ? null
            : (spectrum, count) => statusBoard.SetFftFrame(spectrum.AsSpan(0, count), isRightChannel: false);
        Action<Complex[], int>? onFftRightFrame = statusBoard is null
            ? null
            : (spectrum, count) => statusBoard.SetFftFrame(spectrum.AsSpan(0, count), isRightChannel: true);

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
                onFftSymbolFrame: onFftLeftFrame);
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
            onFftSymbolFrame: onFftLeftFrame);
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
            onEqualizedDataSymbolFrame: null,
            onFftSymbolFrame: onFftRightFrame);
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

    private static byte[] DecodeTurboBlock(byte[] turboEncoded, int paddedLength, int iterations)
    {
        var padded = new byte[paddedLength];
        var unitCount = paddedLength / TurboEcc1024.DataUnitBytes;
        var encoded = new byte[TurboEcc1024.EncodedBytes];
        for (var i = 0; i < unitCount; i++)
        {
            Buffer.BlockCopy(turboEncoded, i * TurboEcc1024.EncodedBytes, encoded, 0, TurboEcc1024.EncodedBytes);
            var decoded = TurboEcc1024.Decode(encoded, iterations: iterations, channelReliability: 1.25);
            Buffer.BlockCopy(decoded, 0, padded, i * TurboEcc1024.DataUnitBytes, TurboEcc1024.DataUnitBytes);
        }

        return padded;
    }

    /// <summary>
    /// Turbo情報LLRを単位ごとに復号し、失敗時はハード判定にフォールバックします。
    /// </summary>
    /// <param name="infoLlrs">infoLlrs を指定します。</param>
    /// <param name="turboEncodedHard">turboEncodedHard を指定します。</param>
    /// <param name="paddedLength">paddedLength を指定します。</param>
    /// <param name="iterations">iterations を指定します。</param>
    /// <returns>処理結果。</returns>
    private static byte[] DecodeTurboBlockFromLlrs(double[] infoLlrs, byte[] turboEncodedHard, int paddedLength, int iterations)
    {
        var padded = new byte[paddedLength];
        var unitCount = paddedLength / TurboEcc1024.DataUnitBytes;
        var bitsPerUnit = TurboEcc1024.EncodedBits;
        var encoded = new byte[TurboEcc1024.EncodedBytes];
        for (var i = 0; i < unitCount; i++)
        {
            var offset = i * bitsPerUnit;
            byte[] decoded;
            if (infoLlrs.Length >= offset + bitsPerUnit)
            {
                try
                {
                    decoded = TurboEcc1024.DecodeFromChannelLlrs(infoLlrs.AsSpan(offset, bitsPerUnit), iterations: iterations);
                }
                catch
                {
                    Buffer.BlockCopy(turboEncodedHard, i * TurboEcc1024.EncodedBytes, encoded, 0, TurboEcc1024.EncodedBytes);
                    decoded = TurboEcc1024.Decode(encoded, iterations: iterations, channelReliability: 1.25);
                }
            }
            else
            {
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
        if (subcarriers is not (9 or 18 or 27 or 36))
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

    private static byte[] ApplyReedSolomonDecode(byte[] encoded)
    {
        if (encoded.Length % RsEcc256.EncodedUnitSize != 0)
        {
            throw new ArgumentException("RS encoded length is invalid.", nameof(encoded));
        }

        var unitCount = encoded.Length / RsEcc256.EncodedUnitSize;
        var output = new byte[unitCount * RsEcc256.DataUnitSize];
        var unit = new byte[RsEcc256.EncodedUnitSize];
        var writeOffset = 0;
        for (var offset = 0; offset < encoded.Length; offset += RsEcc256.EncodedUnitSize)
        {
            Buffer.BlockCopy(encoded, offset, unit, 0, RsEcc256.EncodedUnitSize);
            var decoded = RsEcc256.Decode(unit);
            Buffer.BlockCopy(decoded, 0, output, writeOffset, decoded.Length);
            writeOffset += decoded.Length;
        }

        return output;
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
    /// 送信時間内訳の1セグメントです。
    /// </summary>
    public readonly record struct TransmissionDurationSegment(string Label, long Samples, double Seconds);

    /// <summary>
    /// 送信時間見積り結果です。
    /// </summary>
    public sealed record TransmissionDurationEstimate(
        int SampleRate,
        long TotalSamples,
        double TotalSeconds,
        IReadOnlyList<TransmissionDurationSegment> Segments);

    /// <summary>
    /// プロファイルと入力サイズから送信時間を見積もります。
    /// </summary>
    /// <param name="profile">見積り対象プロファイル。</param>
    /// <param name="fileSizeBytes">入力ファイルサイズ（バイト）。</param>
    /// <returns>送信時間見積り。</returns>
    public static TransmissionDurationEstimate EstimateTransmissionDuration(FileWavCodecProfile profile, long fileSizeBytes)
    {
        if (fileSizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileSizeBytes));
        }

        var codec = new FileWavCodec(profile);
        var payloadLengths = BuildBlockPayloadLengths(fileSizeBytes);
        var segments = new List<TransmissionDurationSegment>(payloadLengths.Length * Math.Max(1, profile.BlockInterleaveFactor) + 8);
        var totalSamples = 0L;

        totalSamples += profile.LeadingSilenceSamples;
        AddSegment(segments, "プリアンブル", profile.UnmodulatedPreambleSamples, profile.SampleRate, ref totalSamples);

        var openingHeaderOfdm = codec.CreateHeaderOfdm();
        var openingFhSamples = HeaderPacketSamples(openingHeaderOfdm, FileHeaderBytes, profile.FileHeaderUnmodulatedSamples);
        AddSegment(segments, "FH", openingFhSamples, profile.SampleRate, ref totalSamples);

        OfdmGenerator? trailingHeaderOfdm = openingHeaderOfdm;
        for (var pass = 0; pass < profile.BlockInterleaveFactor; pass++)
        {
            var (passSc, passMod) = ResolveInterleavePassModulation(
                pass,
                profile.ActiveSubcarriers,
                profile.ModulationScheme);
            var headerOfdm = codec.CreateHeaderOfdm();
            var dataOfdm = codec.CreateDataOfdm(passSc, passMod);
            trailingHeaderOfdm = headerOfdm;
            var fhPacketSamples = HeaderPacketSamples(headerOfdm, FileHeaderBytes, profile.FileHeaderUnmodulatedSamples);
            var bhPacketSamples = HeaderPacketSamples(headerOfdm, BlockHeaderBytes, profile.BlockHeaderUnmodulatedSamples);
            var order = GetBlockEmissionOrder(payloadLengths.Length, pass);
            for (var local = 0; local < order.Length; local++)
            {
                if (local > 0 && (local % FileHeaderRepeatIntervalBlocks) == 0)
                {
                    AddSegment(segments, "FH", fhPacketSamples, profile.SampleRate, ref totalSamples);
                }

                var blockIndex = order[local];
                var dataSamples = DataPacketSamples(
                    dataOfdm,
                    payloadLengths[blockIndex],
                    profile.ChannelMode,
                    passMod);
                var blkSamples = bhPacketSamples + dataSamples;
                var blkLabel = profile.BlockInterleaveFactor == 1
                    ? $"BLK-{blockIndex}"
                    : $"BLK-{blockIndex}(P{pass + 1})";
                AddSegment(segments, blkLabel, blkSamples, profile.SampleRate, ref totalSamples);
            }
        }

        AddSegment(
            segments,
            "FH",
            HeaderPacketSamples(trailingHeaderOfdm!, FileHeaderBytes, profile.FileHeaderUnmodulatedSamples),
            profile.SampleRate,
            ref totalSamples);
        AddSegment(segments, "TAIL", profile.TrailingSilenceSamples, profile.SampleRate, ref totalSamples);

        return new TransmissionDurationEstimate(
            profile.SampleRate,
            totalSamples,
            totalSamples / (double)profile.SampleRate,
            segments);
    }

    /// <summary>
    /// 送信時間見積りを表示向けテキストへ整形します。
    /// </summary>
    /// <param name="estimate">送信時間見積り。</param>
    /// <param name="digits">秒表示の小数桁数。</param>
    /// <returns>内訳表示テキスト。</returns>
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
                            .Append("s")
              .AppendLine();
        }

        sb.Append("合計:")
  .Append(estimate.TotalSeconds.ToString(fmt))
            .Append("s");
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
        var convLength = GetConvolutionalEncodedLength(rsLength, HeaderPunctureRate);
        var totalBits = convLength * 8;
        var channelBits = headerOfdm.ChannelMode == ChannelMode.Stereo
            ? (totalBits + 1) / 2
            : totalBits;
        return unmodulatedSamples + headerOfdm.SampleCountForBitCount(channelBits);
    }

    private static int DataPacketSamples(
        OfdmGenerator dataOfdm,
        int payloadLength,
        ChannelMode channelMode,
        ModulationScheme modulationScheme)
    {
        var withCrcLength = payloadLength + CrcBytes;
        var paddedLength = TurboPaddedLength(withCrcLength);
        var turboLength = (paddedLength / TurboEcc1024.DataUnitBytes) * TurboEcc1024.EncodedBytes;
        var convLength = GetConvolutionalEncodedLength(turboLength, ResolveDataPunctureRate(modulationScheme));
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
/// Complex サンプル列を PCM16 WAV として書き出すユーティリティです。
/// </summary>
public static class WavWriter
{
    /// <summary>
    /// ストリーミングWAVライターを生成します。
    /// </summary>
    /// <param name="path">出力WAVパス。</param>
    /// <param name="sampleRate">サンプルレート。</param>
    /// <param name="peakTarget">ピーク振幅目標値。</param>
    /// <param name="channelMode">チャネルモード。</param>
    /// <returns>ストリーミングライター。</returns>
    public static StreamingPcm16Writer CreateStreamingPcm16(
        string path,
        int sampleRate,
        double peakTarget,
        ChannelMode channelMode)
    {
        return new StreamingPcm16Writer(path, sampleRate, peakTarget, channelMode);
    }

    /// <summary>
    /// 指定チャネルモードで PCM16 WAV を書き出します。
    /// </summary>
    /// <param name="path">出力WAVパス。</param>
    /// <param name="sampleRate">サンプルレート。</param>
    /// <param name="left">左チャネルサンプル。</param>
    /// <param name="right">右チャネルサンプル。</param>
    /// <param name="peakTarget">ピーク振幅目標値。</param>
    /// <param name="channelMode">チャネルモード。</param>
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

    /// <summary>
    /// PCM16 WAV を逐次書き込むストリーミングライターです。
    /// </summary>
    public sealed class StreamingPcm16Writer : IDisposable
    {
        private readonly FileStream _stream;
        private readonly BinaryWriter _writer;
        private readonly ChannelMode _channelMode;
        private readonly int _sampleRate;
        private readonly double _scale;
        private long _sampleFrames;
        private bool _disposed;

        internal StreamingPcm16Writer(string path, int sampleRate, double peakTarget, ChannelMode channelMode)
        {
            ArgumentNullException.ThrowIfNull(path);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

            _channelMode = channelMode;
            _sampleRate = sampleRate;
            _scale = Math.Clamp(peakTarget, 0.0, 1.0);
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            _writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);
            WriteHeaderPlaceholder();
        }

        /// <summary>
        /// PCMチャンクを追記します。
        /// </summary>
        /// <param name="left">左チャネルチャンク。</param>
        /// <param name="right">右チャネルチャンク。</param>
        public void WriteChunk(ReadOnlySpan<Complex> left, ReadOnlySpan<Complex> right)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(StreamingPcm16Writer));
            }

            if (_channelMode == ChannelMode.Mono)
            {
                for (var i = 0; i < left.Length; i++)
                {
                    _writer.Write(ToPcm16(left[i].Real, _scale));
                }

                _sampleFrames += left.Length;
                return;
            }

            if (left.Length != right.Length)
            {
                throw new ArgumentException("Left/Right sample lengths must match for stereo.");
            }

            for (var i = 0; i < left.Length; i++)
            {
                _writer.Write(ToPcm16(left[i].Real, _scale));
                _writer.Write(ToPcm16(right[i].Real, _scale));
            }

            _sampleFrames += left.Length;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            var channels = _channelMode == ChannelMode.Mono ? 1 : 2;
            var dataBytesLong = _sampleFrames * sizeof(short) * channels;
            if (dataBytesLong > int.MaxValue)
            {
                throw new InvalidOperationException("WAV data exceeds RIFF 32-bit size limit.");
            }

            var dataBytes = (int)dataBytesLong;
            var riffSize = 36 + dataBytes;

            _writer.Flush();
            var end = _stream.Position;
            _stream.Seek(4, SeekOrigin.Begin);
            _writer.Write(riffSize);
            _stream.Seek(40, SeekOrigin.Begin);
            _writer.Write(dataBytes);
            _stream.Seek(end, SeekOrigin.Begin);

            _writer.Dispose();
            _stream.Dispose();
            _disposed = true;
        }

        private void WriteHeaderPlaceholder()
        {
            var channels = _channelMode == ChannelMode.Mono ? 1 : 2;
            var blockAlign = (short)(sizeof(short) * channels);
            var byteRate = _sampleRate * blockAlign;

            _writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            _writer.Write(0); // 後で更新
            _writer.Write(Encoding.ASCII.GetBytes("WAVE"));

            _writer.Write(Encoding.ASCII.GetBytes("fmt "));
            _writer.Write(16);
            _writer.Write((short)1); // PCM
            _writer.Write((short)channels);
            _writer.Write(_sampleRate);
            _writer.Write(byteRate);
            _writer.Write(blockAlign);
            _writer.Write((short)16);

            _writer.Write(Encoding.ASCII.GetBytes("data"));
            _writer.Write(0); // 後で更新
        }
    }
}

/// <summary>
/// PCM16 WAV を読み込んで Complex サンプルへ復元するユーティリティです。
/// </summary>
public static class WavReader
{
    /// <summary>
    /// WAVファイルのチャネル数（1ch/2ch）を取得します。
    /// </summary>
    /// <param name="path">入力WAVパス。</param>
    /// <returns>チャネル数。</returns>
    public static int PeekChannelCount(string path)
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

        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var chunkSize = reader.ReadInt32();
            if (chunkId == "fmt ")
            {
                var format = reader.ReadInt16();
                var channels = reader.ReadInt16();
                if (format != 1 || channels is not (1 or 2))
                {
                    throw new NotSupportedException("Only PCM 1ch/2ch WAV is supported.");
                }

                return channels;
            }

            stream.Position += Math.Max(0, chunkSize);
            if ((chunkSize & 1) != 0)
            {
                stream.Position += 1;
            }
        }

        throw new InvalidDataException("fmt chunk not found.");
    }

    /// <summary>
    /// PCM16 WAV を Complex サンプルへ読み込みます。
    /// </summary>
    /// <param name="path">入力WAVパス。</param>
    /// <returns>左/右チャネルのサンプル列。</returns>
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


