using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Onta.Core;

/// <summary>
/// 送受信で扱うフレーム種別です。
/// </summary>
public enum TransmissionFrameKind
{
    Fh,
    Bh,
    Bd
}

/// <summary>
/// 受信した PCM チャンクを処理するコールバックです。
/// </summary>
public delegate void PcmChunkHandler(ReadOnlySpan<Complex> left, ReadOnlySpan<Complex> right);

/// <summary>
/// ファイル伝送に使用する OFDM/変調パラメータです。
/// </summary>
public sealed record FileWavCodecProfile(
    int ActiveSubcarriers,
    ModulationScheme ModulationScheme,
    int SampleRate = 44100,
    double SamplePeak = 0.8,
    int HeaderFftSize = 256,
    int DataFftSize = 256,
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
/// 段階デコード時の探索・復号チューニング値です。
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
    /// WOW 再補正時に先読みする秒数です。
    /// </summary>
    double WowRewarpLookaheadSeconds = 8.0)
{
    public static DecodeRuntimeTuning Default { get; } = new();
}

/// <summary>
/// 段階デコードの進捗状態と中間結果を保持します。
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

    public CoreExecutionStatusBoard StatusBoard { get; }

    internal int SourceLength;

    internal int Pass;

    internal int Local;

    internal int WarpedCursor;

    internal long LogicalOffset;
    /// <summary>
    /// 現在の PCM バッファ先頭が、元ストリーム全体の何サンプル目に相当するかを示します。
    /// ストリーミング受信で先頭を破棄した場合でも時刻基準を維持します。
    /// </summary>
    internal long StreamSampleBase;

    internal byte[]?[]? OutputSlots;

    internal bool[]? SlotAccepted;

    internal (double Amount, double WowPhase, double FlutterPhase)? TrackedWow;

    internal bool HasTrackedWow;
    /// <summary>true のとき区間補正モデル、false のとき全体補正モデルを使用します。</summary>
    internal bool UseSegmentWowModel;
    /// <summary>オープニング区間での WOW 探索を実施済みかを示します。</summary>
    internal bool OpeningWowSearchDone;
    /// <summary>最後にオープニング WOW 推定を行った入力長（サンプル）です。</summary>
    internal int LastOpeningWowEstimateAtSamples;

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

    /// <summary>FH/BH から得たファイル全体ハッシュ（SHA-512 の 16 進）。</summary>
    internal string? ReceivedFileHashHex;

    internal Dictionary<string, int> BlockHashOwners { get; } = new(StringComparer.Ordinal);

    internal Dictionary<int, byte[]> BlockDataModulationByIndex { get; } = [];

    internal Dictionary<int, byte[]> BlockExpectedHashByIndex { get; } = [];

    /// <summary>BH で宣言されたブロックペイロードサイズ（バイト）。</summary>
    internal Dictionary<int, int> BlockSizeByIndex { get; } = [];

    /// <summary>BD 受信結果（true=成功 / false=失敗）。キーがある＝BD 処理済み。</summary>
    internal Dictionary<int, bool> BlockBdOutcomeByIndex { get; } = [];

    internal Dictionary<int, string> BlockHeaderIdentityByIndex { get; } = [];

    internal Dictionary<string, byte[]> OrphanPayloadByHash { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, string> OrphanDetailByHash { get; } = new(StringComparer.Ordinal);

    /// <summary>孤立ブロックキー → BH データ変調 4 バイト。</summary>
    internal Dictionary<string, byte[]> OrphanDataModulationByHash { get; } = new(StringComparer.Ordinal);

    internal int? DetectedDataSubcarriers;
    internal ModulationScheme? DetectedModulationScheme;

    /// <summary>
    /// FH 確定時にファイル名・サイズ・ブロック数を通知します。
    /// </summary>
    public Action<string, long, int, DateTime?, DateTime?>? FileHeaderReady;

    internal DateTime? SourceCreatedAtUtc;

    internal DateTime? SourceUpdatedAtUtc;

    /// <summary>
    /// 段階デコード状態を初期化します。
    /// </summary>
    /// <param name="statusBoard">
    /// 画面と共有する状態メモリ。省略時は専用ボードを生成します。
    /// </param>
    public ProgressiveDecodeState(CoreExecutionStatusBoard? statusBoard = null)
    {
        StatusBoard = statusBoard ?? new CoreExecutionStatusBoard();
    }

    /// <summary>
    /// 段階デコード状態を初期化します。
    /// </summary>
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
        StreamSampleBase = 0;
        OutputSlots = null;
        SlotAccepted = null;
        TrackedWow = null;
        HasTrackedWow = false;
        UseSegmentWowModel = false;
        OpeningWowSearchDone = false;
        LastOpeningWowEstimateAtSamples = 0;
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
        ReceivedFileHashHex = null;
        SourceCreatedAtUtc = null;
        SourceUpdatedAtUtc = null;
        BlockHashOwners.Clear();
        BlockDataModulationByIndex.Clear();
        BlockExpectedHashByIndex.Clear();
        BlockSizeByIndex.Clear();
        BlockBdOutcomeByIndex.Clear();
        BlockHeaderIdentityByIndex.Clear();
        OrphanPayloadByHash.Clear();
        OrphanDetailByHash.Clear();
        OrphanDataModulationByHash.Clear();
        DetectedDataSubcarriers = null;
        DetectedModulationScheme = null;
        StatusBoard.Reset();
    }

    /// <summary>
    /// 共有状態メモリのスナップショットを読み取ります。
    /// </summary>
    public CoreExecutionStatus ReadExecutionStatus() => StatusBoard.Read();

    /// <summary>
    /// <see cref="ReadExecutionStatus"/> の互換エイリアスです。
    /// </summary>
    public CoreExecutionStatus QueryExecutionStatus() => ReadExecutionStatus();
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
/// デコード処理の集計メトリクスです。
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
/// ファイルと WAV の相互変換および段階デコードを提供するコーデックです。
/// </summary>
public sealed partial class FileWavCodec
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

    private const int DataBlockBytes = 8192;

    private const int DataBlockWithCrcBytes = DataBlockBytes + CrcBytes;

    private const string DataTraceEnvVar = "ONTA_TRACE_DATA_ERRORS";

    private const int FileHeaderRepeatIntervalBlocks = 16;

    private static readonly ConvolutionalCode.PunctureRate HeaderPunctureRate = ConvolutionalCode.PunctureRate.Rate1_2;

    private readonly FileWavCodecProfile _profile;

    public DecodeStageMetrics LastDecodeStageMetrics { get; private set; } = DecodeStageMetrics.Empty;

    /// <summary>指定したプロファイルでコーデックを初期化します。</summary>
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
    /// 入力ファイルを WAV へ符号化し、再度復号して結果バイト列を返します。
    /// </summary>
    /// <returns>戻り値を返します。</returns>
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

    /// <summary>ファイルバイト列を OFDM の PCM サンプルへ変調します。</summary>
    /// <returns>戻り値を返します。</returns>
    public (Complex[] Left, Complex[] Right) EncodeFileToSamples(
        byte[] fileBytes,
        FileInfo fileInfo,
        Action<TransmissionFrameKind>? onFrameTransmitted = null,
        PcmChunkHandler? onPcmChunk = null,
        bool retainAllSamples = true,
        CancellationToken cancellationToken = default,
        CoreExecutionStatusBoard? txStatusBoard = null)
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

        if (txStatusBoard is not null)
        {
            txStatusBoard.SetFftStereoMode(_profile.ChannelMode == ChannelMode.Stereo);
        }

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
            var headerOfdm = CreateHeaderOfdm(OfdmConfig.ResolveCarrierGrid(passSc));
            var dataOfdm = CreateDataOfdm(passSc, passMod);
            var dataPunctureRate = ResolveDataPunctureRate(passMod);
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
                    _profile.BlockHeaderUnmodulatedSamples);
                EnsureStereoParity("block header");
                FlushPcmChunk();
                onFrameTransmitted?.Invoke(TransmissionFrameKind.Bh);
                AppendModulatedDataBlock(
                    leftPcm,
                    rightPcm,
                    dataOfdm,
                    blocks[blockIndex].Payload,
                    dataPunctureRate);
                EnsureStereoParity("block data");
                FlushPcmChunk();
                onFrameTransmitted?.Invoke(TransmissionFrameKind.Bd);
            }
        }

        AppendFileHeaderPacket(leftPcm, rightPcm, openingHeaderOfdm, fileHeader);
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

    /// <summary>
    /// WAV を読み込み、ファイルバイト列へ復号します。
    /// </summary>
    /// <returns>戻り値を返します。</returns>
    public byte[] DecodeWavToFileBytes(
        string wavPath,
        bool correctWow = true,
        (double Amount, double WowPhase, double FlutterPhase)? wowParams = null,
        DecodeRuntimeTuning? tuning = null)
    {
        var (leftSamples, rightSamples) = WavReader.ReadPcm16(wavPath);
        return DecodePcmSamplesToFileBytes(leftSamples, rightSamples, correctWow, wowParams, tuning);
    }

    /// <summary>
    /// PCM サンプル列をファイルバイト列へ復号します。
    /// </summary>
    /// <returns>戻り値を返します。</returns>
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

    /// <summary>
    /// ProgressiveDecodeState から集計メトリクスを生成します。
    /// </summary>
    /// <returns>戻り値を返します。</returns>
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

    /// <summary>
    /// 指定したサブキャリア数と変調方式でデータ OFDM 生成器を作成します。
    /// </summary>
    /// <returns>戻り値を返します。</returns>
    /// PCM サンプル列を入力として段階デコードを実行します。
    /// </summary>
    /// <returns>戻り値を返します。</returns>
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
            state.LastError = "モノラル送信には 1ch WAV が必要です。";
            return ProgressiveDecodeStatus.Failed;
        }

        if (_profile.ChannelMode == ChannelMode.Stereo && rightSamples.Length != leftSamples.Length)
        {
            state.LastError = "ステレオ送信には L/R の 2ch WAV が必要です。";
            return ProgressiveDecodeStatus.Failed;
        }

        if (leftSamples.Length < state.SourceLength)
        {
            if (state.StreamSampleBase <= 0)
            {
                state.Reset();
            }
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
            ApplyTrackedWowToBuffers(
                headerOfdm,
                ref leftSamples,
                ref rightSamples,
                known,
                state.StreamSampleBase,
                useSegmentModel: state.StreamSampleBase != 0 || state.UseSegmentWowModel);
            state.TrackedWow = known;
            state.HasTrackedWow = true;
        }
        else if (state.HasTrackedWow && state.TrackedWow is { } tracked)
        {
            ApplyTrackedWowToBuffers(
                headerOfdm,
                ref leftSamples,
                ref rightSamples,
                tracked,
                state.StreamSampleBase,
                useSegmentModel: state.UseSegmentWowModel || state.StreamSampleBase != 0);
        }
        else if (adaptiveWow)
        {
            var minForOpeningWow = _profile.LeadingSilenceSamples
                + _profile.UnmodulatedPreambleSamples
                + Math.Max(headerOfdm.SamplesPerOfdmSymbol * 16, _profile.SampleRate / 4);
            var retryGap = Math.Max(_profile.SampleRate * 2, headerOfdm.SamplesPerOfdmSymbol * 64);
            var canRetryEstimate = state.OpeningWowSearchDone
                && !state.HasTrackedWow
                && leftSamples.Length >= state.LastOpeningWowEstimateAtSamples + retryGap;
            var shouldSearch = leftSamples.Length >= minForOpeningWow
                && (!state.OpeningWowSearchDone || canRetryEstimate);

            if (shouldSearch)
            {
                state.OpeningWowSearchDone = true;
                state.LastOpeningWowEstimateAtSamples = leftSamples.Length;
                state.StatusBoard.SetProgress(new CoreProgressInfo(
                    CurrentFrame: CoreFrameKind.Fh,
                    CurrentBlockIndex: -1,
                    PassIndex: 0,
                    AcceptedBlockCount: 0,
                    TotalBlockCount: 0,
                    ProgressPercent: 2.5));

                (double Amount, double WowPhase, double FlutterPhase)? lockedWow = null;
                var useSegmentCorrectModel = false;
                var preambleStart = _profile.LeadingSilenceSamples;
                var preambleCount = Math.Min(
                    _profile.UnmodulatedPreambleSamples,
                    Math.Max(0, leftSamples.Length - preambleStart));

                var rawPreambleScore = preambleCount >= headerOfdm.SamplesPerOfdmSymbol * 16
                    ? headerOfdm.ScorePreambleMatchForDiagnostics(
                        leftSamples,
                        preambleStart,
                        preambleCount,
                        useRightChannel: false)
                    : 0.0;

                if (rawPreambleScore >= 0.70)
                {
                    lockedWow = (0.0, 0.0, 0.0);
                    useSegmentCorrectModel = false;
                    state.StatusBoard.SetProgress(new CoreProgressInfo(
                        CurrentFrame: CoreFrameKind.Fh,
                        CurrentBlockIndex: -1,
                        PassIndex: 0,
                        AcceptedBlockCount: 0,
                        TotalBlockCount: 0,
                        ProgressPercent: 3.5));
                }
                else
                {
                    var tryMatch = !canRetryEstimate
                        && preambleCount >= headerOfdm.SamplesPerOfdmSymbol * 16;
                    var openingDiag = tryMatch
                        ? headerOfdm.MatchWowParametersForDiagnostics(
                            leftSamples,
                            useRightChannel: false,
                            preambleStart,
                            preambleCount,
                            onProgress: (done, total) =>
                            {
                                var frac = total <= 0 ? 0.0 : done / (double)total;
                                state.StatusBoard.SetProgress(new CoreProgressInfo(
                                    CurrentFrame: CoreFrameKind.Fh,
                                    CurrentBlockIndex: -1,
                                    PassIndex: 0,
                                    AcceptedBlockCount: 0,
                                    TotalBlockCount: 0,
                                    ProgressPercent: Math.Clamp(2.5 + (0.7 * frac), 2.5, 3.2)));
                            })
                        : null;
                    if (openingDiag is { } d && d.BestScore >= 0.50)
                    {
                        lockedWow = headerOfdm.RefineWowParametersForCorrectModel(
                            leftSamples,
                            useRightChannel: false,
                            preambleStart,
                            preambleCount,
                            d.Amount,
                            d.WowPhase,
                            d.FlutterPhase,
                            onProgress: (done, total) =>
                            {
                                var frac = total <= 0 ? 0.0 : done / (double)total;
                                state.StatusBoard.SetProgress(new CoreProgressInfo(
                                    CurrentFrame: CoreFrameKind.Fh,
                                    CurrentBlockIndex: -1,
                                    PassIndex: 0,
                                    AcceptedBlockCount: 0,
                                    TotalBlockCount: 0,
                                    ProgressPercent: Math.Clamp(3.2 + (0.7 * frac), 3.2, 3.9)));
                            });
                        useSegmentCorrectModel = false;
                    }
                    else
                    {
                        if (leftSamples.Length <= _profile.SampleRate * 12)
                        {
                            lockedWow = TryEstimateWowByOpeningFileHeader(
                                leftSamples,
                                rightSamples,
                                headerOfdm,
                                onProgress: (done, total) =>
                                {
                                    var frac = total <= 0 ? 0.0 : done / (double)total;
                                    state.StatusBoard.SetProgress(new CoreProgressInfo(
                                        CurrentFrame: CoreFrameKind.Fh,
                                        CurrentBlockIndex: -1,
                                        PassIndex: 0,
                                        AcceptedBlockCount: 0,
                                        TotalBlockCount: 0,
                                        ProgressPercent: Math.Clamp(2.5 + (1.4 * frac), 2.5, 3.9)));
                                });
                            useSegmentCorrectModel = lockedWow is not null;
                        }
                        else
                        {
                            lockedWow = null;
                            useSegmentCorrectModel = false;
                        }
                    }
                }

                if (lockedWow is { } wow)
                {
                    if (Math.Abs(wow.Amount) > 1e-12)
                    {
                        state.StatusBoard.SetProgress(new CoreProgressInfo(
                            CurrentFrame: CoreFrameKind.Fh,
                            CurrentBlockIndex: -1,
                            PassIndex: 0,
                            AcceptedBlockCount: 0,
                            TotalBlockCount: 0,
                            ProgressPercent: 4.0));
                        ApplyTrackedWowToBuffers(
                            headerOfdm,
                            ref leftSamples,
                            ref rightSamples,
                            wow,
                            state.StreamSampleBase,
                            useSegmentModel: useSegmentCorrectModel || state.StreamSampleBase != 0);
                        state.UseSegmentWowModel = useSegmentCorrectModel || state.StreamSampleBase != 0;
                    }

                    state.TrackedWow = wow;
                    state.HasTrackedWow = true;
                    adaptiveWow = false;
                }
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

        void PublishStatus(
            CoreFrameKind frame,
            int blockIndex,
            double? errorRatePercent = null,
            double blockFraction = 0.0,
            CoreEccDecoderKind decoderKind = CoreEccDecoderKind.Viterbi)
        {
            state.CurrentFrame = frame;
            state.CurrentBlockIndex = blockIndex;
            var blockCount = Math.Max(0, state.BlockCount);
            var totalWork = Math.Max(1, Math.Max(1, blockCount) * Math.Max(1, _profile.BlockInterleaveFactor));
            var doneWork = state.HeaderReady
                ? (state.Pass * Math.Max(1, blockCount)) + state.Local + Math.Clamp(blockFraction, 0.0, 0.999)
                : 0;
            var percent = state.Completed
                ? 100.0
                : !state.HeaderReady
                    ? 2.0
                    : Math.Clamp(5.0 + (90.0 * doneWork / totalWork), 0.0, 99.0);

            // 受信詳細のブロックメーター用。全体進捗とは独立した局所進捗。
            var blockLocal = 0.0;
            if (blockIndex >= 0)
            {
                blockLocal = frame switch
                {
                    CoreFrameKind.Bh => 20.0,
                    CoreFrameKind.Bd => Math.Clamp(20.0 + (79.0 * Math.Clamp(blockFraction, 0.0, 1.0)), 20.0, 99.0),
                    _ => 0.0
                };
            }

            state.StatusBoard.SetProgress(new CoreProgressInfo(
                CurrentFrame: frame,
                CurrentBlockIndex: blockIndex,
                PassIndex: state.Pass,
                AcceptedBlockCount: state.AcceptedBlockCount,
                TotalBlockCount: blockCount,
                ProgressPercent: percent,
                CurrentBlockProgressPercent: blockLocal));

            state.StatusBoard.SetErrorFrameKind(frame);

            if (errorRatePercent is { } err)
            {
                state.StatusBoard.SetErrorRate(err, frame, decoderKind);
            }

            if (hasTrackedWow)
            {
                // 固定 Amount ではなく、現在サンプル位置の瞬間速度偏差を公開する
                var sampleIndex = state.StreamSampleBase + Math.Max(0L, logicalOffset);
                state.StatusBoard.SetWowFlutterTracking(
                    trackedWow.Amount,
                    trackedWow.WowPhase,
                    trackedWow.FlutterPhase,
                    _profile.SampleRate,
                    sampleIndex);
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
            headerOfdm.CorrectWowFlutterSegmentInPlace(
                channelSamples,
                start,
                length,
                amount,
                wowPhase,
                flutterPhase);
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
                    if (TryDecodeStandaloneBhBdWithoutFileHeader())
                    {
                        return allowIncomplete
                            ? ProgressiveDecodeStatus.NeedMoreSamples
                            : ProgressiveDecodeStatus.Failed;
                    }

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
                    fileHeader = DecodeHeaderPacketSyncedTryingGrids(
                        ref headerOfdm,
                        leftSamples,
                        rightSamples,
                        ref warpedCursor,
                        ref logicalOffset,
                        FileHeaderBytes,
                        FileHeaderPilot,
                        coarseRadius,
                        onSyncProgress: (done, total) =>
                        {
                            var frac = total <= 0 ? 0.0 : done / (double)total;
                            state.StatusBoard.SetProgress(new CoreProgressInfo(
                                CurrentFrame: CoreFrameKind.Fh,
                                CurrentBlockIndex: -1,
                                PassIndex: 0,
                                AcceptedBlockCount: 0,
                                TotalBlockCount: 0,
                                ProgressPercent: Math.Clamp(3.0 + (2.0 * frac), 3.0, 4.9)));
                        },
                        statusBoard: state.StatusBoard,
                        frameKind: CoreFrameKind.Fh);
                    state.HeaderRsDecodeCount++;
                    EnsureHeaderCrc(fileHeader, "file header");
                }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                {
                    if (TryDecodeStandaloneBhBdWithoutFileHeader())
                    {
                        if (allowIncomplete)
                        {
                            state.LastError = null;
                            PersistCursor();
                            return ProgressiveDecodeStatus.NeedMoreSamples;
                        }

                        state.LastError ??= "FH未受信。BH+BD を未完了ブロックとして保存しました。";
                        PersistCursor();
                        return ProgressiveDecodeStatus.Failed;
                    }

                    if (allowIncomplete)
                    {
                        state.LastError = null;
                        PersistCursor();
                        return ProgressiveDecodeStatus.NeedMoreSamples;
                    }

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
                var sourceCreatedAtUtc = ReadFileHeaderTimestampUtc(fileHeader, 840);
                var sourceUpdatedAtUtc = ReadFileHeaderTimestampUtc(fileHeader, 847);
                state.FileSize = fileSize;
                state.BlockCount = blockCount;
                state.ReceivedFileName = fhFileName;
                state.ReceivedFileHashHex = Convert.ToHexString(fileHeader.AsSpan(776, 64));
                state.SourceCreatedAtUtc = sourceCreatedAtUtc;
                state.SourceUpdatedAtUtc = sourceUpdatedAtUtc;
                state.OutputSlots = new byte[blockCount][];
                state.SlotAccepted = new bool[blockCount];
                state.HeaderReady = true;
                state.Pass = 0;
                state.Local = 0;
                PersistCursor();
                var displayName = string.IsNullOrWhiteSpace(fhFileName) ? "(不明)" : fhFileName;
                state.StatusBoard.SetAnalyzing(false);
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
                    state.FileHeaderReady?.Invoke(displayName, fileSize, blockCount, sourceCreatedAtUtc, sourceUpdatedAtUtc);
                }
                catch
                {
                }

                PublishStatus(CoreFrameKind.Fh, blockIndex: -1);
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

            static string BuildBlockHeaderIdentityKey(long blockIndex, ReadOnlySpan<byte> blockHash, ReadOnlySpan<byte> fileHash)
            {
                return string.Concat(
                    blockIndex.ToString(),
                    ":",
                    Convert.ToHexString(fileHash),
                    ":",
                    Convert.ToHexString(blockHash));
            }

            bool TryDecodeStandaloneBhBdWithoutFileHeader()
            {
                var start = Math.Max(0, warpedCursor);
                if (start >= leftSamples.Length)
                {
                    return false;
                }

                var progressed = false;
                var headerCandidates = new[]
                {
                    CreateHeaderOfdm(OfdmCarrierGrid.Sc8Family),
                    CreateHeaderOfdm(OfdmCarrierGrid.Sc24Family)
                };
                var scanStep = Math.Max(1, headerOfdm.SamplesPerOfdmSymbol / 4);

                for (var probe = start; probe + headerOfdm.SamplesPerOfdmSymbol < leftSamples.Length; probe += scanStep)
                {
                    var consumed = false;
                    foreach (var probeHeaderOfdm in headerCandidates)
                    {
                        var bhRsByteLength = GetReedSolomonEncodedLength(BlockHeaderBytes);
                        var bhConvByteLength = GetConvolutionalEncodedLength(bhRsByteLength, HeaderPunctureRate);
                        var bhBitCount = bhConvByteLength * 8;
                        var bhSampleCount = probeHeaderOfdm.SampleCountForBitCount(bhBitCount);
                        if (probe + bhSampleCount > leftSamples.Length)
                        {
                            continue;
                        }

                        if (!TryDecodeHeaderAt(
                                leftSamples,
                                rightSamples,
                                probe,
                                probe,
                                probeHeaderOfdm,
                                bhBitCount,
                                bhBitCount,
                                bhSampleCount,
                                BlockHeaderBytes,
                                bhRsByteLength,
                                BlockHeaderPilot,
                                stereoSplit: false,
                                perSymbolSearchRadius: Math.Max(2, probeHeaderOfdm.SamplesPerOfdmSymbol / 16),
                                out var blockHeader,
                                out var bhEnd,
                                out _,
                                statusBoard: state.StatusBoard,
                                frameKind: CoreFrameKind.Bh))
                        {
                            continue;
                        }

                        try
                        {
                            EnsureHeaderCrc(blockHeader, "standalone block header");
                            var blockIndex = BinaryPrimitives.ReadInt64BigEndian(blockHeader.AsSpan(12, 8));
                            var blockSize = BinaryPrimitives.ReadInt32BigEndian(blockHeader.AsSpan(20, 4));
                            if (blockSize < 0 || blockSize > DataBlockBytes)
                            {
                                continue;
                            }

                            var expectedHash = blockHeader.AsSpan(24, 32).ToArray();
                            var fileHashInBlockHeader = blockHeader.AsSpan(56, 64).ToArray();
                            if (string.IsNullOrWhiteSpace(state.ReceivedFileHashHex))
                            {
                                state.ReceivedFileHashHex = Convert.ToHexString(fileHashInBlockHeader);
                            }

                            var blockHeaderIdentity = BuildBlockHeaderIdentityKey(blockIndex, expectedHash, fileHashInBlockHeader);
                            var blockDataModulation = new byte[4]
                            {
                                blockHeader[8],
                                blockHeader[9],
                                blockHeader[10],
                                blockHeader[11]
                            };

                            var (blockSc, blockModulation) = ReadBlockDataModulation(blockHeader);
                            var blockDataOfdm = ResolveDataOfdmFor(blockSc, blockModulation);
                            var blockDataPunctureRate = ResolveDataPunctureRate(blockModulation);
                            var dataCursor = bhEnd;
                            var dataLogical = (long)bhEnd;
                            var padded = DecodeDataBlockSynced(
                                leftSamples,
                                rightSamples,
                                ref dataCursor,
                                ref dataLogical,
                                blockDataOfdm,
                                Math.Max(blockDataOfdm.SamplesPerOfdmSymbol * 2, _profile.SampleRate / 200),
                                expectedBlockHash: expectedHash,
                                payloadLength: blockSize,
                                modulationScheme: blockModulation,
                                tuning: tuning,
                                punctureRate: blockDataPunctureRate,
                                wowLocked: hasTrackedWow,
                                statusBoard: state.StatusBoard,
                                out _,
                                onSoftProgress: null);
                            if (!IsDataBlockAcceptable(padded, expectedHash, blockSize))
                            {
                                continue;
                            }

                            var payload = new byte[blockSize];
                            Buffer.BlockCopy(padded, 0, payload, 0, blockSize);
                            var action = "ADD";
                            if (blockIndex >= 0 && blockIndex <= int.MaxValue)
                            {
                                var idx = (int)blockIndex;
                                if (state.BlockHeaderIdentityByIndex.TryGetValue(idx, out var knownIdentity))
                                {
                                    if (!string.Equals(knownIdentity, blockHeaderIdentity, StringComparison.Ordinal))
                                    {
                                        state.LastError =
                                            $"BH-MISMATCH index={blockIndex} (fileHash+blockHash+blockNo mismatch)";
                                        continue;
                                    }

                                    action = state.OrphanPayloadByHash.ContainsKey(blockHeaderIdentity)
                                        ? "UPDATE"
                                        : "ADD";
                                }

                                state.BlockHeaderIdentityByIndex[idx] = blockHeaderIdentity;
                                state.BlockDataModulationByIndex[idx] = blockDataModulation;
                                state.BlockExpectedHashByIndex[idx] = expectedHash;
                                state.BlockSizeByIndex[idx] = blockSize;
                                state.BlockBdOutcomeByIndex[idx] = true;
                            }

                            state.OrphanPayloadByHash[blockHeaderIdentity] = payload;
                            state.OrphanDetailByHash[blockHeaderIdentity] =
                                $"{action} BH+BD index={blockIndex} (fileHash+blockHash+blockNo)";
                            state.OrphanDataModulationByHash[blockHeaderIdentity] = blockDataModulation.ToArray();
                            state.DataBlocksDecoded++;
                            state.DataBlocksAccepted++;
                            state.AcceptedBlockCount = state.OrphanPayloadByHash.Count;
                            state.LastError =
                                $"BH+BD-ONLY {action} index={blockIndex} key={blockHeaderIdentity[..Math.Min(12, blockHeaderIdentity.Length)]}";

                            warpedCursor = dataCursor;
                            logicalOffset = dataLogical;
                            state.Local++;
                            PersistCursor();
                            PublishStatus(
                                CoreFrameKind.Bd,
                                blockIndex >= 0 && blockIndex <= int.MaxValue ? (int)blockIndex : -1);
                            progressed = true;
                            consumed = true;
                            break;
                        }
                        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                        {
                            continue;
                        }
                    }

                    if (consumed)
                    {
                        probe = Math.Max(probe + scanStep, warpedCursor);
                    }
                }

                return progressed;
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
                    state.OrphanDataModulationByHash.Remove(key);
                    state.LastError = $"ORPHAN-RESOLVED hash={key[..Math.Min(12, key.Length)]} BLK-{blockIndex}";
                    return;
                }

                if (slotAccepted[blockIndex])
                {
                    return;
                }

                var suffix = ":" + key;
                var compositeKey = state.OrphanPayloadByHash.Keys.FirstOrDefault(x => x.EndsWith(suffix, StringComparison.Ordinal));
                if (string.IsNullOrWhiteSpace(compositeKey))
                {
                    return;
                }

                if (state.OrphanPayloadByHash.TryGetValue(compositeKey, out var compositePayload))
                {
                    outputSlots[blockIndex] = compositePayload;
                    slotAccepted[blockIndex] = true;
                    state.OrphanPayloadByHash.Remove(compositeKey);
                    state.OrphanDetailByHash.Remove(compositeKey);
                    state.OrphanDataModulationByHash.Remove(compositeKey);
                    state.LastError = $"ORPHAN-RESOLVED key={compositeKey[..Math.Min(12, compositeKey.Length)]} BLK-{blockIndex}";
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

            void SaveOrphanPayload(
                byte[] payload,
                string detail,
                long blockIndex,
                ReadOnlySpan<byte> blockHash,
                ReadOnlySpan<byte> fileHash,
                byte[] dataModulation)
            {
                var key = BuildBlockHeaderIdentityKey(blockIndex, blockHash, fileHash);
                state.OrphanPayloadByHash[key] = payload;
                state.OrphanDetailByHash[key] = detail;
                state.OrphanDataModulationByHash[key] = dataModulation is { Length: 4 }
                    ? dataModulation.ToArray()
                    : new byte[4];
                state.LastError = $"ORPHAN hash={key[..Math.Min(12, key.Length)]} {detail}";
            }

            for (var pass = state.Pass; pass < _profile.BlockInterleaveFactor; pass++)
            {
                var (passSc, passModBase) = ResolveInterleavePassModulation(
                    pass,
                    _profile.ActiveSubcarriers,
                    _profile.ModulationScheme);
                headerOfdm = CreateHeaderOfdm(OfdmConfig.ResolveCarrierGrid(passSc));
                var passDataOfdm = CreateDataOfdm(passSc, passModBase);
                var passMaxBdSamples = DataPacketSamples(
                    passDataOfdm,
                    DataBlockBytes,
                    _profile.ChannelMode,
                    passModBase);
                var passFhPacketSamples = HeaderPacketSamples(
                    headerOfdm, FileHeaderBytes, _profile.FileHeaderUnmodulatedSamples);
                var passBhPacketSamples = HeaderPacketSamples(
                    headerOfdm, BlockHeaderBytes, _profile.BlockHeaderUnmodulatedSamples);

                bool TrySeekNextBlockHeaderStart(int cursor, long logical, out int nextCursor, out long nextLogical)
                {
                    nextCursor = cursor;
                    nextLogical = logical;
                    var step = Math.Max(1, headerOfdm.SamplesPerOfdmSymbol / 4);
                    var minProbe = Math.Max(0, cursor + headerOfdm.SamplesPerOfdmSymbol);
                    var maxProbe = Math.Min(
                        leftSamples.Length - headerOfdm.SamplesPerOfdmSymbol,
                        cursor
                        + passBhPacketSamples
                        + passMaxBdSamples
                        + passBhPacketSamples
                        + (headerOfdm.SamplesPerOfdmSymbol * 2));
                    if (minProbe >= maxProbe)
                    {
                        return false;
                    }

                    for (var probe = minProbe; probe <= maxProbe; probe += step)
                    {
                        var probeHeaderOfdm = headerOfdm;
                        var probeCursor = probe;
                        var probeLogical = logical + (probe - cursor);
                        try
                        {
                            SkipHeaderUnmodulatedPreamble(
                                leftSamples,
                                ref probeCursor,
                                ref probeLogical,
                                _profile.BlockHeaderUnmodulatedSamples);
                            var probeHeader = DecodeHeaderPacketSyncedTryingGrids(
                                ref probeHeaderOfdm,
                                leftSamples,
                                rightSamples,
                                ref probeCursor,
                                ref probeLogical,
                                BlockHeaderBytes,
                                BlockHeaderPilot,
                                fineRadius,
                                statusBoard: null,
                                frameKind: CoreFrameKind.Bh);
                            EnsureHeaderCrc(probeHeader, "block header resync probe");
                            var blockSize = BinaryPrimitives.ReadInt32BigEndian(probeHeader.AsSpan(20, 4));
                            if (blockSize < 0 || blockSize > DataBlockBytes)
                            {
                                continue;
                            }

                            nextCursor = probe;
                            nextLogical = logical + (probe - cursor);
                            return true;
                        }
                        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                        {
                            continue;
                        }
                    }

                    return false;
                }

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

                        if (expectedBlockIndex >= 0
                            && expectedBlockIndex < slotAccepted.Length
                            && !slotAccepted[expectedBlockIndex])
                        {
                            state.BlockBdOutcomeByIndex[expectedBlockIndex] = false;
                        }

                        state.LastError = message;
                        state.Pass = pass;
                        state.Local = local + 1;
                        PersistCursor();
                        PublishStatus(CoreFrameKind.Bd, expectedBlockIndex);
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
                            var midFh = DecodeHeaderPacketSyncedTryingGrids(
                                ref headerOfdm,
                                leftSamples,
                                rightSamples,
                                ref warpedCursor,
                                ref logicalOffset,
                                FileHeaderBytes,
                                FileHeaderPilot,
                                fineRadius,
                                statusBoard: state.StatusBoard,
                                frameKind: CoreFrameKind.Fh);
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

                        var blockHeader = DecodeHeaderPacketSyncedTryingGrids(
                            ref headerOfdm,
                            leftSamples,
                            rightSamples,
                            ref warpedCursor,
                            ref logicalOffset,
                            BlockHeaderBytes,
                            BlockHeaderPilot,
                            fineRadius,
                            statusBoard: state.StatusBoard,
                            frameKind: CoreFrameKind.Bh);
                        state.HeaderRsDecodeCount++;
                        EnsureHeaderCrc(blockHeader, "block header");
                        PublishStatus(CoreFrameKind.Bh, expectedBlockIndex);

                        var blockIndex = BinaryPrimitives.ReadInt64BigEndian(blockHeader.AsSpan(12, 8));
                        var blockSize = BinaryPrimitives.ReadInt32BigEndian(blockHeader.AsSpan(20, 4));
                        if (blockSize < 0 || blockSize > DataBlockBytes)
                        {
                            throw new InvalidDataException($"Invalid block size {blockSize}.");
                        }

                        var ownerKnown = blockIndex >= 0 && blockIndex < blockCountReady;
                        var ownerBlockIndex = ownerKnown ? (int)blockIndex : -1;
                        var expectedHash = blockHeader.AsSpan(24, 32).ToArray();
                        var fileHashInBlockHeader = blockHeader.AsSpan(56, 64).ToArray();
                        if (string.IsNullOrWhiteSpace(state.ReceivedFileHashHex))
                        {
                            state.ReceivedFileHashHex = Convert.ToHexString(fileHashInBlockHeader);
                        }

                        var blockHeaderIdentity = BuildBlockHeaderIdentityKey(blockIndex, expectedHash, fileHashInBlockHeader);
                        var blockDataModulation = new byte[4]
                        {
                            blockHeader[8],
                            blockHeader[9],
                            blockHeader[10],
                            blockHeader[11]
                        };
                        if (ownerKnown)
                        {
                            if (state.BlockHeaderIdentityByIndex.TryGetValue(ownerBlockIndex, out var knownIdentity)
                                && !string.Equals(knownIdentity, blockHeaderIdentity, StringComparison.Ordinal))
                            {
                                ownerKnown = false;
                                ownerBlockIndex = -1;
                                state.LastError =
                                    $"BH-MISMATCH index={blockIndex} (fileHash+blockHash+blockNo mismatch)";
                            }
                        }

                        if (ownerKnown)
                        {
                            RegisterHashOwner(ownerBlockIndex, expectedHash);
                            state.BlockDataModulationByIndex[ownerBlockIndex] = blockDataModulation;
                            state.BlockExpectedHashByIndex[ownerBlockIndex] = expectedHash;
                            state.BlockSizeByIndex[ownerBlockIndex] = blockSize;
                            state.BlockHeaderIdentityByIndex[ownerBlockIndex] = blockHeaderIdentity;
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
                            punctureRate: blockDataPunctureRate,
                            wowLocked: hasTrackedWow,
                            statusBoard: state.StatusBoard,
                            out var diag,
                            onSoftProgress: frac => PublishStatus(
                                CoreFrameKind.Bd,
                                expectedBlockIndex,
                                blockFraction: frac));
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
                            state.BlockBdOutcomeByIndex[ownerBlockIndex] = true;
                        }
                        else if (acceptable)
                        {
                            SaveOrphanPayload(
                                payload,
                                $"孤立ブロック index={blockIndex} (pass {pass}, local {local})",
                                blockIndex,
                                expectedHash,
                                fileHashInBlockHeader,
                                blockDataModulation);
                        }
                        else if (!acceptable)
                        {
                            if (TryResolveOwnerByPayloadHash(payload, out var resolvedOwner))
                            {
                                outputSlots[resolvedOwner] = payload;
                                slotAccepted[resolvedOwner] = true;
                                state.BlockDataModulationByIndex[resolvedOwner] = blockDataModulation;
                                state.BlockExpectedHashByIndex[resolvedOwner] = expectedHash;
                                state.BlockSizeByIndex[resolvedOwner] = blockSize;
                                state.BlockBdOutcomeByIndex[resolvedOwner] = true;
                                effectiveBlockIndex = resolvedOwner;
                                acceptedForStatus = true;
                                state.LastError =
                                    $"ORPHAN-RESOLVED hash一致で BLK-{resolvedOwner} に再割当 (pass {pass}, local {local})";
                            }
                            else if (ownerKnown)
                            {
                                // BH は成功したが BD が不一致 → 当該ブロックは NG。
                                state.BlockBdOutcomeByIndex[ownerBlockIndex] = false;
                                state.LastError =
                                    $"BLK-{ownerBlockIndex} BD受信失敗 (pass {pass}, local {local})";
                                effectiveBlockIndex = ownerBlockIndex;
                            }
                            else
                            {
                                // BD 失敗かつ親不明は不明ブロックに残さない（OK ペイロードのみ孤立登録）。
                                state.LastError =
                                    $"BLK-{blockIndex} BD受信失敗（親不明・破棄） (pass {pass}, local {local})";
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
                            effectiveBlockIndex);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                    {
                        MarkBlockError($"BLK-{expectedBlockIndex} デコードエラー: {ex.Message}");
                        if (TrySeekNextBlockHeaderStart(warpedCursor, logicalOffset, out var seekCursor, out var seekLogical))
                        {
                            warpedCursor = seekCursor;
                            logicalOffset = seekLogical;
                            continue;
                        }

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
                    var endFh = DecodeHeaderPacketSyncedTryingGrids(
                        ref headerOfdm,
                        leftSamples,
                        rightSamples,
                        ref warpedCursor,
                        ref logicalOffset,
                        FileHeaderBytes,
                        FileHeaderPilot,
                        fineRadius,
                        statusBoard: state.StatusBoard,
                        frameKind: CoreFrameKind.Fh);
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
            PublishStatus(CoreFrameKind.Fh, blockIndex: -1);
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

    /// <summary>
    /// 追跡中のワウパラメータでバッファを補正した新規配列に差し替えます（元バッファは破壊しない）。
    /// </summary>
    private void ApplyTrackedWowToBuffers(
        OfdmGenerator headerOfdm,
        ref Complex[] leftSamples,
        ref Complex[] rightSamples,
        (double Amount, double WowPhase, double FlutterPhase) wow,
        long streamSampleBase,
        bool useSegmentModel)
    {
        if (!useSegmentModel && streamSampleBase == 0)
        {
            leftSamples = headerOfdm.CorrectWowFlutterWithParams(
                leftSamples,
                wow.Amount,
                wow.WowPhase,
                wow.FlutterPhase);
            if (_profile.ChannelMode == ChannelMode.Stereo && rightSamples.Length > 0)
            {
                rightSamples = headerOfdm.CorrectWowFlutterWithParams(
                    rightSamples,
                    wow.Amount,
                    wow.WowPhase,
                    wow.FlutterPhase);
            }

            return;
        }

        var left = (Complex[])leftSamples.Clone();
        headerOfdm.CorrectWowFlutterSegmentInPlace(
            left,
            0,
            left.Length,
            wow.Amount,
            wow.WowPhase,
            wow.FlutterPhase,
            streamSampleBase);
        leftSamples = left;

        if (_profile.ChannelMode == ChannelMode.Stereo && rightSamples.Length > 0)
        {
            var right = (Complex[])rightSamples.Clone();
            headerOfdm.CorrectWowFlutterSegmentInPlace(
                right,
                0,
                right.Length,
                wow.Amount,
                wow.WowPhase,
                wow.FlutterPhase,
                streamSampleBase);
            rightSamples = right;
        }
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
            pilotSpacing: 8,
            stereoFrequencyShiftBins: _profile.StereoFrequencyShiftBins,
            sampleRate: _profile.SampleRate,
            randomSeed: _profile.RandomSeed,
            carrierGrid: grid);

        return new OfdmGenerator(config);
    }

    /// <summary>
    /// 先頭 FH の同期成功を指標にワウパラメータを探索します。
    /// プリアンブル相関が LPF 等で崩れる場合のフォールバックです。
    /// prefix Correct で粗い位置を掴み、最終適用と同じ全波形 Correct で精密化します。
    /// </summary>
    private (double Amount, double WowPhase, double FlutterPhase)? TryEstimateWowByOpeningFileHeader(
        Complex[] leftSamples,
        Complex[] rightSamples,
        OfdmGenerator headerOfdm,
        Action<int, int>? onProgress = null)
    {
        var fhDataSamples = HeaderPacketSamples(headerOfdm, FileHeaderBytes, unmodulatedSamples: 0);
        var expectedFhStart =
            _profile.LeadingSilenceSamples
            + _profile.UnmodulatedPreambleSamples
            + _profile.FileHeaderUnmodulatedSamples;
        var prefixLen = Math.Min(
            leftSamples.Length,
            expectedFhStart + fhDataSamples + headerOfdm.SamplesPerOfdmSymbol * 8);
        if (prefixLen < expectedFhStart + fhDataSamples)
        {
            return null;
        }

        var rsByteLength = GetReedSolomonEncodedLength(FileHeaderBytes);
        var convByteLength = GetConvolutionalEncodedLength(rsByteLength, HeaderPunctureRate);
        var bitCount = convByteLength * 8;
        var sampleCount = headerOfdm.SampleCountForBitCount(bitCount);
        var bhRsByteLength = GetReedSolomonEncodedLength(BlockHeaderBytes);
        var bhConvByteLength = GetConvolutionalEncodedLength(bhRsByteLength, HeaderPunctureRate);
        var bhBitCount = bhConvByteLength * 8;
        var bhSampleCount = headerOfdm.SampleCountForBitCount(bhBitCount);
        var sampleRate = Math.Max(1, _profile.SampleRate);
        var searchRadius = Math.Max(2, headerOfdm.SamplesPerOfdmSymbol / 8);
        var stereo = rightSamples.Length >= leftSamples.Length && rightSamples.Length > 0;

        var headerNeedLen = Math.Min(
            leftSamples.Length,
            expectedFhStart
            + sampleCount
            + (searchRadius * 4)
            + _profile.BlockHeaderUnmodulatedSamples
            + bhSampleCount
            + (searchRadius * 4)
            + (headerOfdm.SamplesPerOfdmSymbol * 32));
        var maxDataOfdm = CreateDataOfdm(8, ModulationScheme.Bpsk);
        var maxBdSamples = DataPacketSamples(
            maxDataOfdm,
            DataBlockBytes,
            ChannelMode.Mono,
            ModulationScheme.Bpsk);
        var dataNeedLen = Math.Min(
            leftSamples.Length,
            headerNeedLen
            + ((_profile.BlockHeaderUnmodulatedSamples + bhSampleCount + maxBdSamples) * 2)
            + Math.Max(maxDataOfdm.SamplesPerOfdmSymbol * 16, _profile.SampleRate / 25));

        var workLeft = new Complex[dataNeedLen];
        var workRight = stereo ? new Complex[dataNeedLen] : Array.Empty<Complex>();
        (Complex[] Left, Complex[] Right, OfdmGenerator Ofdm) CreateWorker() =>
            (new Complex[dataNeedLen], stereo ? new Complex[dataNeedLen] : Array.Empty<Complex>(), CreateHeaderOfdm());

        void PrepareWork(
            Complex[] destLeft,
            Complex[] destRight,
            OfdmGenerator ofdm,
            int needLen,
            double amount,
            double wowPhase,
            double flutterPhase)
        {
            needLen = Math.Clamp(needLen, 1, dataNeedLen);
            ofdm.CorrectWowFlutterSegmentTo(
                leftSamples,
                0,
                needLen,
                destLeft.AsSpan(0, needLen),
                amount,
                wowPhase,
                flutterPhase);
            if (needLen < destLeft.Length)
            {
                Array.Clear(destLeft, needLen, destLeft.Length - needLen);
            }

            if (stereo)
            {
                ofdm.CorrectWowFlutterSegmentTo(
                    rightSamples,
                    0,
                    needLen,
                    destRight.AsSpan(0, needLen),
                    amount,
                    wowPhase,
                    flutterPhase);
                if (needLen < destRight.Length)
                {
                    Array.Clear(destRight, needLen, destRight.Length - needLen);
                }
            }
        }

        bool TryHeader(
            Complex[] left,
            Complex[] right,
            OfdmGenerator ofdm,
            int start,
            int payloadLength,
            int rsLen,
            int bits,
            int samples,
            byte[] pilot,
            out int endCursor,
            out double meanAbsLlr,
            out byte[] payload)
        {
            endCursor = start;
            meanAbsLlr = 0.0;
            payload = Array.Empty<byte>();
            if (!TryDecodeHeaderAt(
                    left,
                    right,
                    start,
                    logicalOffset: start,
                    ofdm,
                    bits,
                    bits,
                    samples,
                    payloadLength,
                    rsLen,
                    pilot,
                    stereoSplit: false,
                    perSymbolSearchRadius: searchRadius,
                    out payload,
                    out endCursor,
                    out meanAbsLlr))
            {
                return false;
            }

            try
            {
                EnsureHeaderCrc(payload, payloadLength == FileHeaderBytes ? "file header" : "block header");
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        (double Amount, double WowPhase, double FlutterPhase, double Score)? EvalPrefix(
            Complex[] destLeft,
            Complex[] destRight,
            OfdmGenerator ofdm,
            double amount,
            double wowPhase,
            double flutterPhase)
        {
            PrepareWork(destLeft, destRight, ofdm, prefixLen, amount, wowPhase, flutterPhase);
            if (!TryHeader(
                    destLeft,
                    destRight,
                    ofdm,
                    expectedFhStart,
                    FileHeaderBytes,
                    rsByteLength,
                    bitCount,
                    sampleCount,
                    FileHeaderPilot,
                    out _,
                    out var llr,
                    out _))
            {
                return null;
            }

            return (amount, wowPhase, flutterPhase, llr);
        }

        (double Amount, double WowPhase, double FlutterPhase, double Score)? EvalOpening(
            Complex[] destLeft,
            Complex[] destRight,
            OfdmGenerator ofdm,
            double amount,
            double wowPhase,
            double flutterPhase,
            bool tryDataBlock)
        {
            var need = tryDataBlock ? dataNeedLen : headerNeedLen;
            PrepareWork(destLeft, destRight, ofdm, need, amount, wowPhase, flutterPhase);

            if (!TryHeader(
                    destLeft,
                    destRight,
                    ofdm,
                    expectedFhStart,
                    FileHeaderBytes,
                    rsByteLength,
                    bitCount,
                    sampleCount,
                    FileHeaderPilot,
                    out var fhEnd,
                    out var fhLlr,
                    out _))
            {
                return null;
            }

            var score = fhLlr;
            var bhStart = fhEnd + _profile.BlockHeaderUnmodulatedSamples;
            if (bhStart + bhSampleCount > need
                || !TryHeader(
                    destLeft,
                    destRight,
                    ofdm,
                    bhStart,
                    BlockHeaderBytes,
                    bhRsByteLength,
                    bhBitCount,
                    bhSampleCount,
                    BlockHeaderPilot,
                    out var bhEnd,
                    out var bhLlr,
                    out var bhPayload))
            {
                return (amount, wowPhase, flutterPhase, score);
            }

            score += 1000.0 + bhLlr;
            if (!tryDataBlock)
            {
                return (amount, wowPhase, flutterPhase, score);
            }

            try
            {
                var blockSize = BinaryPrimitives.ReadInt32BigEndian(bhPayload.AsSpan(20, 4));
                if (blockSize < 0 || blockSize > DataBlockBytes)
                {
                    return (amount, wowPhase, flutterPhase, score);
                }

                var expectedHash = bhPayload.AsSpan(24, 32).ToArray();
                var (blockSc, blockModulation) = ReadBlockDataModulation(bhPayload);
                var blockDataOfdm = CreateDataOfdm(blockSc, blockModulation);
                var blockDataPunctureRate = ResolveDataPunctureRate(blockModulation);
                var bdSamples = DataPacketSamples(
                    blockDataOfdm,
                    Math.Max(1, blockSize),
                    _profile.ChannelMode,
                    blockModulation);
                var bdNeed = Math.Min(
                    dataNeedLen,
                    bhEnd
                    + bdSamples
                    + Math.Max(blockDataOfdm.SamplesPerOfdmSymbol * 8, _profile.SampleRate / 50));
                if (bdNeed > need)
                {
                    PrepareWork(destLeft, destRight, ofdm, bdNeed, amount, wowPhase, flutterPhase);
                    if (!TryHeader(
                            destLeft,
                            destRight,
                            ofdm,
                            expectedFhStart,
                            FileHeaderBytes,
                            rsByteLength,
                            bitCount,
                            sampleCount,
                            FileHeaderPilot,
                            out fhEnd,
                            out _,
                            out _)
                        || !TryHeader(
                            destLeft,
                            destRight,
                            ofdm,
                            fhEnd + _profile.BlockHeaderUnmodulatedSamples,
                            BlockHeaderBytes,
                            bhRsByteLength,
                            bhBitCount,
                            bhSampleCount,
                            BlockHeaderPilot,
                            out bhEnd,
                            out _,
                            out bhPayload))
                    {
                        return (amount, wowPhase, flutterPhase, score);
                    }
                }

                var dataCursor = bhEnd;
                var dataLogical = (long)bhEnd;
                var tuningLocal = new DecodeRuntimeTuning(
                    TurboIterationsMin: 4,
                    TurboIterationsMax: 6,
                    DataSyncMaxFullAttemptsWhenWowLocked: 2,
                    DataSyncMaxSoftOnlyAttemptsWhenWowLocked: 1,
                    SoftLlrAbortMeanAbsWhenWowLocked: 0.7);
                var padded = DecodeDataBlockSynced(
                    destLeft,
                    destRight,
                    ref dataCursor,
                    ref dataLogical,
                    blockDataOfdm,
                    Math.Max(blockDataOfdm.SamplesPerOfdmSymbol * 2, _profile.SampleRate / 200),
                    expectedBlockHash: expectedHash,
                    payloadLength: blockSize,
                    modulationScheme: blockModulation,
                    tuning: tuningLocal,
                    punctureRate: blockDataPunctureRate,
                    wowLocked: true,
                    statusBoard: null,
                    out _,
                    onSoftProgress: null);
                if (padded.Length > 0)
                {
                    score += 1_000_000.0;

                    try
                    {
                        var bh1Start = dataCursor + _profile.BlockHeaderUnmodulatedSamples;
                        var need2 = Math.Min(
                            dataNeedLen,
                            bh1Start
                            + bhSampleCount
                            + bdSamples
                            + Math.Max(blockDataOfdm.SamplesPerOfdmSymbol * 8, _profile.SampleRate / 50));
                        if (need2 > need)
                        {
                            PrepareWork(destLeft, destRight, ofdm, need2, amount, wowPhase, flutterPhase);
                            if (!TryHeader(
                                    destLeft,
                                    destRight,
                                    ofdm,
                                    expectedFhStart,
                                    FileHeaderBytes,
                                    rsByteLength,
                                    bitCount,
                                    sampleCount,
                                    FileHeaderPilot,
                                    out fhEnd,
                                    out _,
                                    out _)
                                || !TryHeader(
                                    destLeft,
                                    destRight,
                                    ofdm,
                                    fhEnd + _profile.BlockHeaderUnmodulatedSamples,
                                    BlockHeaderBytes,
                                    bhRsByteLength,
                                    bhBitCount,
                                    bhSampleCount,
                                    BlockHeaderPilot,
                                    out bhEnd,
                                    out _,
                                    out bhPayload))
                            {
                                return (amount, wowPhase, flutterPhase, score);
                            }

                            dataCursor = bhEnd;
                            dataLogical = bhEnd;
                            padded = DecodeDataBlockSynced(
                                destLeft,
                                destRight,
                                ref dataCursor,
                                ref dataLogical,
                                blockDataOfdm,
                                Math.Max(blockDataOfdm.SamplesPerOfdmSymbol * 2, _profile.SampleRate / 200),
                                expectedBlockHash: expectedHash,
                                payloadLength: blockSize,
                                modulationScheme: blockModulation,
                                tuning: tuningLocal,
                                punctureRate: blockDataPunctureRate,
                                wowLocked: true,
                                statusBoard: null,
                                out _,
                                onSoftProgress: null);
                            if (padded.Length == 0)
                            {
                                return (amount, wowPhase, flutterPhase, score);
                            }

                            bh1Start = dataCursor + _profile.BlockHeaderUnmodulatedSamples;
                        }

                        if (bh1Start + bhSampleCount <= destLeft.Length
                            && TryHeader(
                                destLeft,
                                destRight,
                                ofdm,
                                bh1Start,
                                BlockHeaderBytes,
                                bhRsByteLength,
                                bhBitCount,
                                bhSampleCount,
                                BlockHeaderPilot,
                                out var bh1End,
                                out var bh1Llr,
                                out var bh1Payload))
                        {
                            score += 1000.0 + bh1Llr;
                            var blockSize1 = BinaryPrimitives.ReadInt32BigEndian(bh1Payload.AsSpan(20, 4));
                            if (blockSize1 >= 0 && blockSize1 <= DataBlockBytes)
                            {
                                var expectedHash1 = bh1Payload.AsSpan(24, 32).ToArray();
                                var (blockSc1, blockMod1) = ReadBlockDataModulation(bh1Payload);
                                var ofdm1 = CreateDataOfdm(blockSc1, blockMod1);
                                var puncture1 = ResolveDataPunctureRate(blockMod1);
                                var c1 = bh1End;
                                var l1 = (long)bh1End;
                                var padded1 = DecodeDataBlockSynced(
                                    destLeft,
                                    destRight,
                                    ref c1,
                                    ref l1,
                                    ofdm1,
                                    Math.Max(ofdm1.SamplesPerOfdmSymbol * 2, _profile.SampleRate / 200),
                                    expectedBlockHash: expectedHash1,
                                    payloadLength: blockSize1,
                                    modulationScheme: blockMod1,
                                    tuning: tuningLocal,
                                    punctureRate: puncture1,
                                    wowLocked: true,
                                    statusBoard: null,
                                    out _,
                                    onSoftProgress: null);
                                if (padded1.Length > 0)
                                {
                                    score += 1_000_000.0;
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return (amount, wowPhase, flutterPhase, score);
        }

        var amounts = new[] { 0.005, 0.01 };
        const int phaseSteps = 18;
        var coarseTotal = amounts.Length * phaseSteps * phaseSteps;
        var done = 0;
        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
        };

        void ReportProgress()
        {
            var d = Interlocked.Increment(ref done);
            if ((d & 3) == 0 || d >= coarseTotal + 250)
            {
                onProgress?.Invoke(Math.Min(d, coarseTotal + 250), coarseTotal + 250);
            }
        }

        var prefixHits = new ConcurrentBag<(double Amount, double WowPhase, double FlutterPhase, double Score)>();
        foreach (var amount in amounts)
        {
            using var cts = new CancellationTokenSource();
            var opts = new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelOpts.MaxDegreeOfParallelism,
                CancellationToken = cts.Token
            };
            var foundBefore = prefixHits.Count;
            try
            {
                Parallel.For(
                    0,
                    phaseSteps * phaseSteps,
                    opts,
                    CreateWorker,
                    (flat, state, worker) =>
                    {
                        if (state.ShouldExitCurrentIteration)
                        {
                            return worker;
                        }

                        ReportProgress();
                        var wi = flat / phaseSteps;
                        var fi = flat % phaseSteps;
                        var wowPhase = wi * (2.0 * Math.PI / phaseSteps);
                        var flutterPhase = fi * (2.0 * Math.PI / phaseSteps);
                        var hit = EvalPrefix(worker.Left, worker.Right, worker.Ofdm, amount, wowPhase, flutterPhase);
                        if (hit is { } h)
                        {
                            prefixHits.Add(h);
                            if (prefixHits.Count >= 24)
                            {
                                cts.Cancel();
                                state.Stop();
                            }
                        }

                        return worker;
                    },
                    _ => { });
            }
            catch (OperationCanceledException)
            {
            }
            catch (AggregateException ae) when (ae.InnerExceptions.All(static e => e is OperationCanceledException))
            {
            }

            var foundForAmount = prefixHits.Count - foundBefore;
            if (prefixHits.Count >= 24)
            {
                break;
            }

            if (amount == 0.005 && foundForAmount >= 3)
            {
                break;
            }
        }

        if (prefixHits.Count == 0)
        {
            return null;
        }

        var prefixHitList = prefixHits.ToList();
        prefixHitList.Sort((a, b) => b.Score.CompareTo(a.Score));
        var centers = new List<(double Amount, double WowPhase, double FlutterPhase, double Score)>();
        foreach (var hit in prefixHitList)
        {
            var near = false;
            foreach (var c in centers)
            {
                if (Math.Abs(WrapPhase(hit.WowPhase - c.WowPhase)) < 0.35
                    && Math.Abs(WrapPhase(hit.FlutterPhase - c.FlutterPhase)) < 0.35
                    && Math.Abs(hit.Amount - c.Amount) < 1e-9)
                {
                    near = true;
                    break;
                }
            }

            if (!near)
            {
                centers.Add(hit);
            }

            if (centers.Count >= 4)
            {
                break;
            }
        }

        (double Amount, double WowPhase, double FlutterPhase, double Score)? bestBh = null;
        foreach (var center in centers)
        {
            var amountA = 0.005;
            if (TryEvaluateBhAmount(amountA, center, ref bestBh))
            {
                break;
            }

            var amountB = center.Amount;
            if (amountB != amountA && TryEvaluateBhAmount(amountB, center, ref bestBh))
            {
                break;
            }

            const double amountC = 0.01;
            if (amountC != amountA && amountC != amountB && TryEvaluateBhAmount(amountC, center, ref bestBh))
            {
                break;
            }

            if (bestBh is { } locked && locked.Score >= 1000.0)
            {
                break;
            }
        }

        bool TryEvaluateBhAmount(
            double amount,
            (double Amount, double WowPhase, double FlutterPhase, double Score) center,
            ref (double Amount, double WowPhase, double FlutterPhase, double Score)? best)
        {
            var bag = new ConcurrentBag<(double Amount, double WowPhase, double FlutterPhase, double Score)>();
            Parallel.For(
                -20,
                21,
                parallelOpts,
                CreateWorker,
                (k, _, worker) =>
                {
                    ReportProgress();
                    var hit = EvalOpening(
                        worker.Left,
                        worker.Right,
                        worker.Ofdm,
                        amount,
                        center.WowPhase + (k * 0.02),
                        center.FlutterPhase,
                        tryDataBlock: false);
                    if (hit is { } h)
                    {
                        bag.Add(h);
                    }

                    return worker;
                },
                _ => { });

            foreach (var hit in bag)
            {
                if (best is null || hit.Score > best.Value.Score)
                {
                    best = hit;
                }
            }

            return best is { } b0 && b0.Score >= 1000.0 && Math.Abs(b0.Amount - amount) < 1e-9;
        }

        if (bestBh is null)
        {
            return null;
        }

        var best = bestBh.Value;
        {
            var flutterBag = new ConcurrentBag<(double Amount, double WowPhase, double FlutterPhase, double Score)>();
            Parallel.For(
                -12,
                13,
                parallelOpts,
                CreateWorker,
                (k, _, worker) =>
                {
                    ReportProgress();
                    var hit = EvalOpening(
                        worker.Left,
                        worker.Right,
                        worker.Ofdm,
                        best.Amount,
                        best.WowPhase,
                        best.FlutterPhase + (k * 0.02),
                        tryDataBlock: false);
                    if (hit is { } h)
                    {
                        flutterBag.Add(h);
                    }

                    return worker;
                },
                _ => { });
            foreach (var hit in flutterBag)
            {
                if (hit.Score > best.Score)
                {
                    best = hit;
                }
            }
        }

        static IEnumerable<int> SpiralOffsets(int maxAbs)
        {
            yield return 0;
            for (var d = 1; d <= maxAbs; d++)
            {
                yield return d;
                yield return -d;
            }
        }

        (double Amount, double WowPhase, double FlutterPhase, double Score)? bestWithData = null;
        var spiralWow = SpiralOffsets(20).ToArray();
        const int spiralChunk = 8;
        for (var chunkStart = 0; chunkStart < spiralWow.Length; chunkStart += spiralChunk)
        {
            var chunkEnd = Math.Min(spiralWow.Length, chunkStart + spiralChunk);
            var bag = new ConcurrentBag<(double Amount, double WowPhase, double FlutterPhase, double Score)>();
            Parallel.For(
                chunkStart,
                chunkEnd,
                parallelOpts,
                CreateWorker,
                (idx, _, worker) =>
                {
                    ReportProgress();
                    var k = spiralWow[idx];
                    var hit = EvalOpening(
                        worker.Left,
                        worker.Right,
                        worker.Ofdm,
                        best.Amount,
                        best.WowPhase + (k * 0.002),
                        best.FlutterPhase,
                        tryDataBlock: true);
                    if (hit is { } h)
                    {
                        bag.Add(h);
                    }

                    return worker;
                },
                _ => { });

            foreach (var hit in bag)
            {
                if (bestWithData is null || hit.Score > bestWithData.Value.Score)
                {
                    bestWithData = hit;
                }
            }

            if (bestWithData is { } early && early.Score >= 1_000_000.0)
            {
                var refined = early;
                foreach (var dw in SpiralOffsets(12))
                {
                    ReportProgress();
                    var wHit = EvalOpening(
                        workLeft,
                        workRight,
                        headerOfdm,
                        refined.Amount,
                        refined.WowPhase + (dw * 0.001),
                        refined.FlutterPhase,
                        tryDataBlock: true);
                    if (wHit is { } wh && wh.Score > refined.Score)
                    {
                        refined = wh;
                    }
                }

                foreach (var df in SpiralOffsets(40))
                {
                    ReportProgress();
                    var fHit = EvalOpening(
                        workLeft,
                        workRight,
                        headerOfdm,
                        refined.Amount,
                        refined.WowPhase,
                        refined.FlutterPhase + (df * 0.004),
                        tryDataBlock: true);
                    if (fHit is { } fh && fh.Score > refined.Score)
                    {
                        refined = fh;
                    }
                }

                foreach (var dw in SpiralOffsets(8))
                {
                    ReportProgress();
                    var wHit = EvalOpening(
                        workLeft,
                        workRight,
                        headerOfdm,
                        refined.Amount,
                        refined.WowPhase + (dw * 0.001),
                        refined.FlutterPhase,
                        tryDataBlock: true);
                    if (wHit is { } wh && wh.Score > refined.Score)
                    {
                        refined = wh;
                    }
                }

                return (refined.Amount, refined.WowPhase, refined.FlutterPhase);
            }
        }

        if (bestWithData is null || bestWithData.Value.Score < 1_000_000.0)
        {
            var spiralFlutter = SpiralOffsets(15).ToArray();
            for (var chunkStart = 0; chunkStart < spiralFlutter.Length; chunkStart += spiralChunk)
            {
                var chunkEnd = Math.Min(spiralFlutter.Length, chunkStart + spiralChunk);
                var bag = new ConcurrentBag<(double Amount, double WowPhase, double FlutterPhase, double Score)>();
                Parallel.For(
                    chunkStart,
                    chunkEnd,
                    parallelOpts,
                    CreateWorker,
                    (idx, _, worker) =>
                    {
                        ReportProgress();
                        var k = spiralFlutter[idx];
                        var hit = EvalOpening(
                            worker.Left,
                            worker.Right,
                            worker.Ofdm,
                            best.Amount,
                            best.WowPhase,
                            best.FlutterPhase + (k * 0.002),
                            tryDataBlock: true);
                        if (hit is { } h)
                        {
                            bag.Add(h);
                        }

                        return worker;
                    },
                    _ => { });

                foreach (var hit in bag)
                {
                    if (bestWithData is null || hit.Score > bestWithData.Value.Score)
                    {
                        bestWithData = hit;
                    }
                }

                if (bestWithData is { } ok && ok.Score >= 1_000_000.0)
                {
                    return (ok.Amount, ok.WowPhase, ok.FlutterPhase);
                }
            }
        }

        if (bestWithData is { } bdFinal && bdFinal.Score >= 1_000_000.0)
        {
            return (bdFinal.Amount, bdFinal.WowPhase, bdFinal.FlutterPhase);
        }

        return null;

        static double WrapPhase(double x)
        {
            while (x > Math.PI)
            {
                x -= 2.0 * Math.PI;
            }

            while (x < -Math.PI)
            {
                x += 2.0 * Math.PI;
            }

            return x;
        }
    }

    /// <summary>
    /// 指定サンプル数の無音を PCM バッファへ追加します。
    /// </summary>
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

    /// <summary>
    /// 左右チャンネルのサンプル対を PCM バッファへ追加します。
    /// </summary>
    private static void AppendPair(List<Complex> leftPcm, List<Complex> rightPcm, (Complex[] Left, Complex[] Right) pair)
    {
        leftPcm.AddRange(pair.Left);
        if (pair.Right.Length == 0)
        {
            return;
        }

        rightPcm.AddRange(pair.Right);
    }

    /// <summary>
    /// 指定サンプル数だけカーソルを進めます。
    /// </summary>
    /// <returns>戻り値を返します。</returns>
    private static int SkipSamples(Complex[] samples, int cursor, int count)
    {
        var next = cursor + count;
        if (next > samples.Length)
        {
            throw new InvalidDataException("WAV ended while skipping preamble/silence.");
        }

        return next;
    }

    /// <summary>
    /// カーソル位置から指定サンプル数を切り出して返します。
    /// </summary>
    /// <returns>戻り値を返します。</returns>
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

    private readonly record struct DataBlock(byte[] Payload, int PayloadLength);
}

/// <summary>
/// Complex サンプル列を PCM16 WAV として書き出すユーティリティです。
/// </summary>
public static class WavWriter
{
    /// <summary>
    /// ストリーミング書き込み用の WAV ライターを作成します。
    /// </summary>
    /// <returns>戻り値を返します。</returns>
    public static StreamingPcm16Writer CreateStreamingPcm16(
        string path,
        int sampleRate,
        double peakTarget,
        ChannelMode channelMode)
    {
        return new StreamingPcm16Writer(path, sampleRate, peakTarget, channelMode);
    }

    /// <summary>
    /// 指定チャンネルモードで PCM16 WAV を書き出します。
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

    /// <summary>
    /// モノラルの Complex サンプル列を PCM16 WAV として書き出します。
    /// </summary>
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

    /// <summary>
    /// ステレオの Complex サンプル列を PCM16 WAV として書き出します。
    /// </summary>
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

    /// <summary>
    /// 正規化済み振幅値を PCM16 値へ変換します。
    /// </summary>
    /// <returns>戻り値を返します。</returns>
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
        /// PCM チャンクを追記します。
        /// </summary>
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

        /// <summary>
        /// WAV ヘッダーサイズを確定してストリームを破棄します。
        /// </summary>
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

        /// <summary>
        /// サイズ未確定の WAV ヘッダーを書き込みます。
        /// </summary>
        private void WriteHeaderPlaceholder()
        {
            var channels = _channelMode == ChannelMode.Mono ? 1 : 2;
            var blockAlign = (short)(sizeof(short) * channels);
            var byteRate = _sampleRate * blockAlign;

            _writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            _writer.Write(0); // Dispose 時に RIFF チャンクサイズを書き戻す
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
            _writer.Write(0); // Dispose 時に data チャンクサイズを書き戻す
        }
    }
}

/// <summary>
/// PCM16 WAV を読み込み Complex サンプル列へ変換するユーティリティです。
/// </summary>
public static class WavReader
{
    /// <summary>
    /// WAV のチャンネル数（1ch/2ch）を取得します。
    /// </summary>
    /// <returns>戻り値を返します。</returns>
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
    /// PCM16 WAV を Complex サンプル列として読み込みます。
    /// </summary>
    /// <returns>戻り値を返します。</returns>
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

/// <summary>
/// PCM16 WAV をチャンク単位で順次読み込むストリームリーダーです。
/// 全データを一括展開せずに処理できます。
/// </summary>
public sealed class WavPcmStreamReader : IDisposable
{

    private readonly FileStream _stream;

    private readonly BinaryReader _reader;

    private readonly short _channels;

    private readonly int _sampleRate;

    private readonly long _dataStart;

    private readonly long _dataLength;

    private long _dataPosition;

    private bool _disposed;

    private WavPcmStreamReader(
        FileStream stream,
        BinaryReader reader,
        short channels,
        int sampleRate,
        long dataStart,
        long dataLength)
    {
        _stream = stream;
        _reader = reader;
        _channels = channels;
        _sampleRate = sampleRate;
        _dataStart = dataStart;
        _dataLength = dataLength;
        _dataPosition = 0;
        _stream.Position = dataStart;
    }

    /// <summary>チャンネル数（1 または 2）です。</summary>
    public int Channels => _channels;

    /// <summary>サンプルレートです。</summary>
    public int SampleRate => _sampleRate;

    /// <summary>総フレーム数です。</summary>
    public long TotalFrames => _dataLength / (_channels * 2);

    /// <summary>読み込み済みフレーム数です。</summary>
    public long FramesRead => _dataPosition / (_channels * 2);

    /// <summary>読み込み進捗（0.0〜1.0）です。</summary>
    public double Progress =>
        _dataLength <= 0 ? 1.0 : Math.Clamp(_dataPosition / (double)_dataLength, 0.0, 1.0);

    /// <summary>
    /// WAV を開き、data チャンク先頭までシークしたリーダーを返します。
    /// </summary>
    public static WavPcmStreamReader Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = new BinaryReader(stream);
        try
        {
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
            var sampleRate = 0;
            long dataStart = -1;
            long dataLength = -1;

            while (stream.Position + 8 <= stream.Length)
            {
                var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
                var chunkSize = reader.ReadInt32();
                var chunkDataPos = stream.Position;
                if (chunkId == "fmt ")
                {
                    var format = reader.ReadInt16();
                    channels = reader.ReadInt16();
                    sampleRate = reader.ReadInt32();
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
                    dataStart = chunkDataPos;
                    dataLength = Math.Max(0, chunkSize);
                    stream.Position = chunkDataPos + chunkSize;
                    if ((chunkSize & 1) != 0 && stream.Position < stream.Length)
                    {
                        stream.Position += 1;
                    }
                }
                else
                {
                    stream.Position = chunkDataPos + Math.Max(0, chunkSize);
                    if ((chunkSize & 1) != 0 && stream.Position < stream.Length)
                    {
                        stream.Position += 1;
                    }
                }
            }

            if (dataStart < 0 || dataLength < 0 || channels == 0 || sampleRate <= 0)
            {
                throw new InvalidDataException("WAV data chunk not found.");
            }

            return new WavPcmStreamReader(stream, reader, channels, sampleRate, dataStart, dataLength);
        }
        catch
        {
            reader.Dispose();
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 最大 frameCount フレームを読み、Complex の L/R 配列を返します。
    /// </summary>
    /// <returns>戻り値を返します。</returns>
    public bool TryRead(int frameCount, out Complex[] left, out Complex[] right)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        left = Array.Empty<Complex>();
        right = Array.Empty<Complex>();
        if (frameCount <= 0 || _dataPosition >= _dataLength)
        {
            return false;
        }

        var bytesPerFrame = _channels * 2;
        var remainingFrames = (int)Math.Min(frameCount, (_dataLength - _dataPosition) / bytesPerFrame);
        if (remainingFrames <= 0)
        {
            return false;
        }

        var byteCount = remainingFrames * bytesPerFrame;
        var raw = _reader.ReadBytes(byteCount);
        if (raw.Length < bytesPerFrame)
        {
            _dataPosition = _dataLength;
            return false;
        }

        var frames = raw.Length / bytesPerFrame;
        _dataPosition += frames * bytesPerFrame;
        left = new Complex[frames];
        if (_channels == 1)
        {
            for (var i = 0; i < frames; i++)
            {
                var o = i * 2;
                var s = (short)(raw[o] | (raw[o + 1] << 8));
                left[i] = new Complex(s / (double)short.MaxValue, 0.0);
            }

            right = Array.Empty<Complex>();
            return true;
        }

        right = new Complex[frames];
        for (var i = 0; i < frames; i++)
        {
            var o = i * 4;
            var l = (short)(raw[o] | (raw[o + 1] << 8));
            var r = (short)(raw[o + 2] | (raw[o + 3] << 8));
            left[i] = new Complex(l / (double)short.MaxValue, 0.0);
            right[i] = new Complex(r / (double)short.MaxValue, 0.0);
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reader.Dispose();
        _stream.Dispose();
    }
}


