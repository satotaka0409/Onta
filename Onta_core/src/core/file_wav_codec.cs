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
/// 符号化中に生成された PCM チャンクの通知です（リアルタイム再生向け）。
/// </summary>
/// <param name="left">L チャンネル（またはモノラル）サンプル。</param>
/// <param name="right">R チャンネル（モノラル時は空）。</param>
public delegate void PcmChunkHandler(ReadOnlySpan<Complex> left, ReadOnlySpan<Complex> right);

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
    /// <summary>ブロック時系列インターリーブ倍率（1 / 2）。data_struct.mdc。</summary>
    int BlockInterleaveFactor = 1)
{
    public byte ModulationModeByte => (byte)ModulationScheme;
    public byte ChannelModeByte => (byte)ChannelMode;
    public int LeadingSilenceSamples => SampleRate / 10;
    public int TrailingSilenceSamples => SampleRate / 10;
    /// <summary>全体先頭の全サブキャリア無変調プリアンブル（2 秒）。modulation.mdc。</summary>
    public int UnmodulatedPreambleSamples => SampleRate * 2;
    /// <summary>ファイルヘッダー先頭の無変調区間（1 秒）。</summary>
    public int FileHeaderUnmodulatedSamples => SampleRate;
    /// <summary>ブロックヘッダー先頭の無変調区間（0.3 秒）。</summary>
    public int BlockHeaderUnmodulatedSamples => (SampleRate * 3) / 10;
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
    double WowLocalAmountRange = 0.002,
    /// <summary>BH ごとの適応 wow 再推定を抑止し、FH / 中間 FH 境界でのみ再適用する。</summary>
    bool AdaptiveWowOnlyOnFileHeaderBoundaries = true,
    /// <summary>ステレオ時は L 推定パラメータを R にも適用する。</summary>
    bool ShareStereoWowFromLeft = true,
    /// <summary>前回適用からのパラメータ変化が小さいときはテール再ワープを省略する。</summary>
    double WowSkipRewarpAmountDelta = 0.00025,
    double WowSkipRewarpPhaseDeltaRad = 0.02,
    int DataSyncMaxFullAttempts = 12,
    int DataSyncMaxSoftOnlyAttempts = 8,
    int DataSyncMaxFullAttemptsWhenWowLocked = 4,
    int DataSyncMaxSoftOnlyAttemptsWhenWowLocked = 2,
    double SoftLlrAbortMeanAbs = 0.35,
    double SoftLlrAbortMeanAbsWhenWowLocked = 0.55,
    /// <summary>soft 失敗後の hard turbo を試す最小平均 |LLR|。</summary>
    double SoftHardFallbackMinMeanAbsLlr = 1.0)
{
    public static DecodeRuntimeTuning Default { get; } = new();
}

/// <summary>
/// 増分復号の進行状態です。同じインスタンスを渡して続きから復号します。
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

    /// <summary>画面問い合わせ用の実行状況ボードです。</summary>
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

    /// <summary>増分復号状態を初期化します。</summary>
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
        StatusBoard.Reset();
    }

    /// <summary>画面からの進捗問い合わせです。</summary>
    /// <returns>画面表示用の実行状況スナップショット。</returns>
    public CoreExecutionStatus QueryExecutionStatus() => StatusBoard.Query();
}

/// <summary>
/// 増分復号の結果です。
/// </summary>
public enum ProgressiveDecodeStatus
{
    NeedMoreSamples,
    Completed,
    Failed
}

/// <summary>
/// 直近の復号におけるステージ別統計です。
/// </summary>
/// <param name="HeaderRsDecodeCount">RS を使うヘッダーパケット復号の成功回数。</param>
/// <param name="DataBlocksDecoded">データブロック復号の試行回数。</param>
/// <param name="DataBlocksAccepted">ハッシュ一致で受理されたデータブロック数。</param>
/// <param name="DataAcceptedViaViterbi">ビタービ経路（ハード）で受理されたブロック数。</param>
/// <param name="DataAcceptedViaTurbo">ターボ経路（ソフト）で受理されたブロック数。</param>
/// <param name="DataFallbackUsed">フォールバック経路を使用したブロック数。</param>
/// <param name="DataTotalAttempts">データブロック復号の総試行回数。</param>
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
/// 入力ファイルの WAV 符号化と、WAV からの復元を行うコーデックです。
/// モノラルは 1ch PCM、ステレオは L/R 別データ（2ch）の WAV です。
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
    /// <summary>FH/BH の CRC 対象はパイロット直後から CRC 直前まで。</summary>
    private const int HeaderCrcDataOffset = HeaderPrefixBytes;
    private static readonly byte[] HeaderVersion = [0x00, 0x01];
    private static readonly byte[] FileHeaderPilot = [0xF0, 0xE1, 0xD2, 0xC3, 0xB4, 0xA5];
    private static readonly byte[] BlockHeaderPilot = [0x0F, 0x1E, 0x2D, 0x3C, 0x4B, 0x5A];
    private const int DataBlockBytes = 4096;
    /// <summary>データ部は最大 4096 バイト + CRC-32。</summary>
    private const int DataBlockWithCrcBytes = DataBlockBytes + CrcBytes;
    private const string DataTraceEnvVar = "ONTA_TRACE_DATA_ERRORS";
    /// <summary>ファイルヘッダーをデータ部の何ブロックごとに再送出するか。</summary>
    private const int FileHeaderRepeatIntervalBlocks = 16;
    private const int InterleaveInitSeedFileHeader = unchecked((int)0x13579BDF);
    private const int InterleaveInitSeedBlock = unchecked((int)0x2468ACE1);
    private static readonly ConvolutionalCode.PunctureRate HeaderPunctureRate = ConvolutionalCode.PunctureRate.Rate1_2;

    private readonly FileWavCodecProfile _profile;

    /// <summary>直近復号のステージ別統計です。</summary>
    public DecodeStageMetrics LastDecodeStageMetrics { get; private set; } = DecodeStageMetrics.Empty;

    /// <summary>指定プロファイルでコーデックを初期化します。</summary>
    /// <param name="profile">コーデック初期化用プロファイル。</param>
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
    /// ファイルを符号化して WAV に書き出し、復号結果のバイト列を返します。
    /// </summary>
    /// <param name="inputPath">符号化元ファイルパス。</param>
    /// <param name="wavPath">中間 WAV の書き出しパス。</param>
    /// <param name="restoredPath">復号バイト列の保存先（省略可）。</param>
    /// <returns>復号して得た元ファイルのバイト列。</returns>
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

    /// <summary>ファイルバイト列を OFDM PCM サンプル列へ符号化します。</summary>
    /// <param name="fileBytes">符号化対象のファイルバイト列。</param>
    /// <param name="fileInfo">ファイル名／属性の取得元。</param>
    /// <param name="onFrameTransmitted">フレーム送出時のコールバック（省略可）。</param>
    /// <param name="onPcmChunk">新規 PCM チャンク送出時のコールバック（L/R、リアルタイム再生向け、省略可）。</param>
    /// <param name="retainAllSamples">false の場合、送出済みサンプルを保持せず逐次破棄します。</param>
    /// <param name="cancellationToken">符号化中止用トークン（省略可）。</param>
    /// <returns>L/R の OFDM PCM サンプル列。</returns>
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

        /// <summary>ステレオ時に L/R サンプル長の一致を検査します。</summary>
        /// <param name="stage">不一致検出時に表示する段階名。</param>
        void EnsureStereoParity(string stage)
        {
            if (_profile.ChannelMode == ChannelMode.Stereo && leftPcm.Count != rightPcm.Count)
            {
                throw new InvalidOperationException(
                    $"Stereo stream length mismatch after {stage}: L={leftPcm.Count}, R={rightPcm.Count}");
            }
        }

        /// <summary>未送出の PCM をコールバックへ流します。</summary>
        void FlushPcmChunk()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (onPcmChunk is null)
            {
                return;
            }

            var count = leftPcm.Count - pcmEmitted;
            if (count <= 0)
            {
                return;
            }

            var leftSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(leftPcm).Slice(pcmEmitted, count);
            ReadOnlySpan<Complex> rightSpan = ReadOnlySpan<Complex>.Empty;
            if (_profile.ChannelMode == ChannelMode.Stereo)
            {
                rightSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rightPcm).Slice(pcmEmitted, count);
            }

            onPcmChunk(leftSpan, rightSpan);
            pcmEmitted = leftPcm.Count;

            // 逐次出力モードでは、送出済みサンプルを都度破棄してメモリ常駐を抑える。
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
            // 先頭無変調はベース SC 族のヘッダー配置を使う。
            var preambleOfdm = CreateHeaderOfdm(_profile.ActiveSubcarriers);
            AppendHeaderPair(leftPcm, rightPcm, preambleOfdm.GenerateUnmodulated(_profile.UnmodulatedPreambleSamples));
            EnsureStereoParity("global preamble");
            FlushPcmChunk();
        }

        // data_struct.mdc: 先頭 FH → (BH+BD)×N（16ブロックごとに FH）×パス → 末尾 FH。
        // ×2 の第2パスは奇偶入れ替え＋SC/変調ダウングレード。パス先頭の追加 FH は出さない。
        var openingHeaderOfdm = CreateHeaderOfdm(_profile.ActiveSubcarriers);
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
            var headerOfdm = CreateHeaderOfdm(passSc);
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

    /// <summary>WAV ファイルを読み取り、元ファイルのバイト列へ復号します。</summary>
    /// <param name="wavPath">読み取る WAV パス。</param>
    /// <param name="correctWow">ワウフラッター補正を行うか。</param>
    /// <param name="wowParams">固定 wow/flutter パラメータ（省略時は推定）。</param>
    /// <param name="tuning">復号実行時チューニング（省略時は既定）。</param>
    /// <returns>復元したファイルバイト列。</returns>
    public byte[] DecodeWavToFileBytes(
        string wavPath,
        bool correctWow = true,
        (double Amount, double WowPhase, double FlutterPhase)? wowParams = null,
        DecodeRuntimeTuning? tuning = null)
    {
        var (leftSamples, rightSamples) = WavReader.ReadPcm16(wavPath);
        return DecodePcmSamplesToFileBytes(leftSamples, rightSamples, correctWow, wowParams, tuning);
    }

    /// <summary>PCM サンプル列を一括復号してファイルバイト列を返します。</summary>
    /// <param name="leftSamples">L チャンネル PCM サンプル列。</param>
    /// <param name="rightSamples">R チャンネル PCM サンプル列。</param>
    /// <param name="correctWow">ワウフラッター補正を行うか。</param>
    /// <param name="wowParams">固定 wow/flutter パラメータ（省略時は推定）。</param>
    /// <param name="tuning">復号実行時チューニング（省略時は既定）。</param>
    /// <returns>復元したファイルバイト列。</returns>
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
    /// PCM を増分復号します。不足時は状態を保持して <see cref="ProgressiveDecodeStatus.NeedMoreSamples"/> を返します。
    /// </summary>
    /// <param name="leftSamples">L チャンネル PCM サンプル列。</param>
    /// <param name="rightSamples">R チャンネル PCM サンプル列。</param>
    /// <param name="state">増分復号の進行状態。</param>
    /// <param name="correctWow">ワウフラッター補正を行うか。</param>
    /// <param name="wowParams">固定 wow/flutter パラメータ（省略時は推定）。</param>
    /// <param name="tuning">復号実行時チューニング（省略時は既定）。</param>
    /// <param name="allowIncomplete">サンプル不足時に継続待ちを許すか。</param>
    /// <returns>増分復号の状態（不足／完了／失敗）。</returns>
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
            state.LastError = "Mono profile expects a 1-channel WAV.";
            return ProgressiveDecodeStatus.Failed;
        }

        if (_profile.ChannelMode == ChannelMode.Stereo && rightSamples.Length != leftSamples.Length)
        {
            state.LastError = "Stereo profile expects a 2-channel WAV with equal L/R length.";
            return ProgressiveDecodeStatus.Failed;
        }

        if (leftSamples.Length < state.SourceLength)
        {
            // リングバッファ巻き戻りなどで先頭が欠けた場合はやり直す。
            state.Reset();
        }

        state.SourceLength = leftSamples.Length;

        var headerOfdm = CreateHeaderOfdm(_profile.ActiveSubcarriers);
        var dataOfdmCache = new Dictionary<(int Sc, ModulationScheme Mod), OfdmGenerator>();
        var headerOfdmCache = new Dictionary<int, OfdmGenerator>
        {
            [_profile.ActiveSubcarriers] = headerOfdm
        };

        /// <summary>データ SC 族に対応するヘッダー用 OFDM 生成器を取得（キャッシュ）します。</summary>
        /// <param name="dataSubcarriers">キャッシュキーとなるデータ部 SC 数。</param>
        /// <returns>ヘッダー用 OFDM 生成器。</returns>
        OfdmGenerator ResolveHeaderOfdmFor(int dataSubcarriers)
        {
            if (headerOfdmCache.TryGetValue(dataSubcarriers, out var cached))
            {
                return cached;
            }

            var created = CreateHeaderOfdm(dataSubcarriers);
            headerOfdmCache[dataSubcarriers] = created;
            return created;
        }

        /// <summary>データ部用 OFDM 生成器を取得（キャッシュ）します。</summary>
        /// <param name="activeSubcarriers">データ部の有効サブキャリア数。</param>
        /// <param name="modulationScheme">データ部の変調方式。</param>
        /// <returns>データ部用 OFDM 生成器。</returns>
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

        // 作業用バッファ: wow 補正が新配列を返す／適応補正が in-place 書き込みする。
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
        else if (adaptiveWow)
        {
            // ApplyAdaptiveWowCorrection が配列内容を書き換えるためコピーする。
            leftSamples = (Complex[])leftSamples.Clone();
            if (_profile.ChannelMode == ChannelMode.Stereo)
            {
                rightSamples = (Complex[])rightSamples.Clone();
            }
        }

        var trackedWow = state.TrackedWow ?? (Amount: 0.01, WowPhase: 0.0, FlutterPhase: 0.0);
        var hasTrackedWow = state.HasTrackedWow;

        var warpedCursor = state.HeaderReady ? state.WarpedCursor : 0;
        var logicalOffset = state.HeaderReady ? state.LogicalOffset : 0L;
        var coarseRadius = Math.Max(headerOfdm.SamplesPerOfdmSymbol * 8, _profile.SampleRate / 50);
        var fineRadius = Math.Max(headerOfdm.SamplesPerOfdmSymbol * 2, _profile.SampleRate / 200);

        /// <summary>復号カーソルと wow 追跡状態を ProgressiveDecodeState へ保存します。</summary>
        void PersistCursor()
        {
            state.WarpedCursor = warpedCursor;
            state.LogicalOffset = logicalOffset;
            state.TrackedWow = hasTrackedWow ? trackedWow : state.TrackedWow;
            state.HasTrackedWow = hasTrackedWow;
        }

        /// <summary>画面表示用の進捗・エラー率・ワウフラッターを StatusBoard へ反映します。</summary>
        /// <param name="frame">表示するフレーム種別。</param>
        /// <param name="blockIndex">現在のブロック番号（不明時は -1）。</param>
        /// <param name="errorRatePercent">エラー率（％、省略可）。</param>
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
                // パイロット推定量を表示用 % に写像（中央0のバー向けに小さい値でも見えるようスケール）。
                var wowDisplay = trackedWow.Amount * 200.0;
                state.StatusBoard.SetWowFlutterPercent(wowDisplay, wowDisplay);
            }

            if (state.HeaderReady)
            {
                state.StatusBoard.SetFileInfo(
                    state.StatusBoard.FileName,
                    $"{state.FileSize:N0} bytes",
                    blockCount.ToString());
            }
        }

        /// <summary>サンプル不足時は継続待ち、一括復号時は失敗として返します。</summary>
        /// <param name="detail">一括復号失敗時のメッセージ。</param>
        /// <returns>サンプル不足または失敗のステータス。</returns>
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

        // 適応 wow を適用したか（ステレオ R 共有の再ワープ判定用）。
        var lastWowRewarpApplied = false;

        /// <summary>パイロット相関から wow/flutter を推定し、未処理テールへ補正を適用します。</summary>
        /// <param name="channelSamples">適応補正するチャンネルサンプル列。</param>
        /// <param name="useRightChannel">R チャンネルのパイロット配置を使うか。</param>
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

            var tailLength = channelSamples.Length - warpedCursor;
            if (tailLength <= 0)
            {
                return;
            }

            RewarpTailInPlace(ref channelSamples, warpedCursor, tailLength, newParams.Amount, newParams.WowPhase, newParams.FlutterPhase);
            lastWowRewarpApplied = true;
        }

        /// <summary>既知パラメータで未処理テール区間のみを in-place 再ワープします。</summary>
        /// <param name="channelSamples">再ワープするチャンネルサンプル列。</param>
        /// <param name="start">開始サンプル位置。</param>
        /// <param name="length">対象サンプル長。</param>
        /// <param name="amount">wow 振幅パラメータ。</param>
        /// <param name="wowPhase">wow 位相（ラジアン）。</param>
        /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
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

        /// <summary>L（必要なら R）へ適応 wow 補正を適用します。</summary>
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
                    remaining,
                    trackedWow.Amount,
                    trackedWow.WowPhase,
                    trackedWow.FlutterPhase);
                return;
            }

            ApplyAdaptiveWowCorrection(ref rightSamples, useRightChannel: true);
        }

        /// <summary>位相差を [-π, π] へ折り返します。</summary>
        /// <param name="phase">折り返す位相差（ラジアン）。</param>
        /// <returns>[-π, π] に折り返した位相差。</returns>
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
                    coarseRadius,
                    InterleaveInitSeedFileHeader);
                state.HeaderRsDecodeCount++;
                EnsureHeaderCrc(fileHeader, "file header");
                var fileSize = BinaryPrimitives.ReadInt64BigEndian(fileHeader.AsSpan(860, 8));
                var blockCount = (int)BinaryPrimitives.ReadInt64BigEndian(fileHeader.AsSpan(868, 8));
                if (fileSize < 0 || blockCount < 0)
                {
                    state.LastError = "Invalid file header size/block count.";
                    state.StatusBoard.Complete(faulted: true, state.LastError);
                    return ProgressiveDecodeStatus.Failed;
                }

                state.FileSize = fileSize;
                state.BlockCount = blockCount;
                state.OutputSlots = new byte[blockCount][];
                state.SlotAccepted = new bool[blockCount];
                state.HeaderReady = true;
                state.Pass = 0;
                state.Local = 0;
                PersistCursor();
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

            for (var pass = state.Pass; pass < _profile.BlockInterleaveFactor; pass++)
            {
                var (passSc, _) = ResolveInterleavePassModulation(
                    pass,
                    _profile.ActiveSubcarriers,
                    _profile.ModulationScheme);
                var passHeaderOfdm = ResolveHeaderOfdmFor(passSc);
                var passFhPacketSamples = HeaderPacketSamples(
                    passHeaderOfdm, FileHeaderBytes, _profile.FileHeaderUnmodulatedSamples);
                var passBhPacketSamples = HeaderPacketSamples(
                    passHeaderOfdm, BlockHeaderBytes, _profile.BlockHeaderUnmodulatedSamples);

                var order = GetBlockEmissionOrder(blockCountReady, pass);
                var localStart = pass == state.Pass ? state.Local : 0;
                for (var local = localStart; local < order.Length; local++)
                {
                    // BH 分だけ先に足りるか見る。データ長は BH 読取後に確定する。
                    var minForBlock = passBhPacketSamples + passHeaderOfdm.SamplesPerOfdmSymbol;
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
                            passHeaderOfdm,
                            FileHeaderBytes,
                            FileHeaderPilot,
                            fineRadius,
                            InterleaveInitSeedFileHeader);
                        state.HeaderRsDecodeCount++;
                        EnsureHeaderCrc(midFh, "mid file header");
                    }

                    var expectedBlockIndex = order[local];
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
                        passHeaderOfdm,
                        BlockHeaderBytes,
                        BlockHeaderPilot,
                        fineRadius,
                        InterleaveInitSeedBlock);
                    state.HeaderRsDecodeCount++;
                    EnsureHeaderCrc(blockHeader, "block header");
                    PublishStatus(CoreFrameKind.Bh, expectedBlockIndex, errorRatePercent: 0.0);
                    var blockIndex = BinaryPrimitives.ReadInt64BigEndian(blockHeader.AsSpan(12, 8));
                    var blockSize = BinaryPrimitives.ReadInt32BigEndian(blockHeader.AsSpan(20, 4));
                    if (blockIndex != expectedBlockIndex)
                    {
                        state.LastError =
                            $"Unexpected block index {blockIndex}, expected {expectedBlockIndex} (pass {pass}, local {local}).";
                        state.StatusBoard.Complete(faulted: true, state.LastError);
                        return ProgressiveDecodeStatus.Failed;
                    }

                    if (blockSize < 0 || blockSize > DataBlockBytes)
                    {
                        state.LastError = $"Invalid block size {blockSize}.";
                        state.StatusBoard.Complete(faulted: true, state.LastError);
                        return ProgressiveDecodeStatus.Failed;
                    }

                    // データ部の SC/変調は BH 記載を正とする（受信プロファイルの変調設定は使わない）。
                    var (blockSc, blockModulation) = ReadBlockDataModulation(blockHeader);
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
                        expectedBlockHash: blockHeader.AsSpan(24, 32).ToArray(),
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

                    var expectedHash = blockHeader.AsSpan(24, 32).ToArray();
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

                    // ハッシュ一致なら 0%、不一致は高エラーとして画面へ返す。
                    PublishStatus(
                        CoreFrameKind.Bd,
                        expectedBlockIndex,
                        errorRatePercent: acceptable ? 0.0 : 100.0);
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
                    var trailingPass = Math.Max(0, _profile.BlockInterleaveFactor - 1);
                    var (trailingSc, _) = ResolveInterleavePassModulation(
                        trailingPass,
                        _profile.ActiveSubcarriers,
                        _profile.ModulationScheme);
                    var endFh = DecodeHeaderPacketSynced(
                        leftSamples,
                        rightSamples,
                        ref warpedCursor,
                        ref logicalOffset,
                        ResolveHeaderOfdmFor(trailingSc),
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
                // 末尾 FH が欠ける場合でもデータが揃っていれば成功とする。
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
            PersistCursor();
            PublishStatus(CoreFrameKind.Fh, blockIndex: -1, errorRatePercent: 0.0);
            state.StatusBoard.Complete(faulted: false);
            return ProgressiveDecodeStatus.Completed;
        }
        catch (InvalidDataException ex)
        {
            // 増分モードのみ、残りが明らかに短いときは継続待ちにする。
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

    /// <summary>FH/BH 用（GROUP B・9 SC・BPSK）の OFDM 生成器を作成します。</summary>
    /// <param name="dataSubcarriers">周波数グリッドを決めるデータ部 SC 数。</param>
    /// <returns>FH/BH 用 OFDM 生成器。</returns>
    private OfdmGenerator CreateHeaderOfdm(int dataSubcarriers)
    {
        // modulation.mdc: FH/BH は GROUP B（概念 10–18）9 SC + BPSK。
        // 周波数グリッドはデータ部 SC 族に合わせる（SC-9/18 族 or SC-27/36 族）。
        var groupB = OfdmConfig.ResolveGroupBLeftBins();
        var grid = OfdmConfig.ResolveCarrierGrid(dataSubcarriers);
        var fftSize = OfdmConfig.ResolveFftSize(dataSubcarriers, _profile.ChannelMode);
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

    /// <summary>データ部用の OFDM 生成器を作成します。</summary>
    /// <param name="activeSubcarriers">データ部の有効サブキャリア数。</param>
    /// <param name="modulationScheme">データ部の変調方式。</param>
    /// <returns>データ部用 OFDM 生成器。</returns>
    private OfdmGenerator CreateDataOfdm(int activeSubcarriers, ModulationScheme modulationScheme)
    {
        // SC-9/18: 440Hz 起点・1.3Δf・FFT=128。SC-27/36: FFT=128（ステレオ×2）。
        // CP はデータ部 16 固定。
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

    /// <summary>ファイルヘッダーパケット（無変調＋OFDM）を PCM へ追記します。</summary>
    /// <param name="leftPcm">L チャンネル PCM リスト。</param>
    /// <param name="rightPcm">R チャンネル PCM リスト。</param>
    /// <param name="headerOfdm">ヘッダー用 OFDM 生成器。</param>
    /// <param name="fileHeader">組み立て済みファイルヘッダー。</param>
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
    /// ファイルヘッダー（1 秒）／ブロックヘッダー（0.3 秒）の無変調区間を付けてから OFDM 変調します。
    /// </summary>
    /// <param name="leftPcm">L チャンネル PCM リスト。</param>
    /// <param name="rightPcm">R チャンネル PCM リスト。</param>
    /// <param name="ofdm">OFDM 生成／復調器。</param>
    /// <param name="leftHeaderBytes">L 側ヘッダーバイト列。</param>
    /// <param name="rightHeaderBytes">R 側ヘッダーバイト列（互換用）。</param>
    /// <param name="unmodulatedSamples">先頭無変調区間のサンプル数。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ用初期シード。</param>
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

    /// <summary>ヘッダー用 L/R ペアを追記し、ステレオ整合のため必要なら R を補います。</summary>
    /// <param name="leftPcm">L チャンネル PCM リスト。</param>
    /// <param name="rightPcm">R チャンネル PCM リスト。</param>
    /// <param name="pair">L/R サンプル配列ペア。</param>
    private void AppendHeaderPair(List<Complex> leftPcm, List<Complex> rightPcm, (Complex[] Left, Complex[] Right) pair)
    {
        AppendPair(leftPcm, rightPcm, pair);
        if (_profile.ChannelMode == ChannelMode.Stereo && pair.Right.Length == 0)
        {
            // 生成器がモノラル出力を返した場合のみ、ステレオ整合のため R を補う。
            rightPcm.AddRange(pair.Left);
        }
    }

    /// <summary>パケット長に応じたヘッダー先頭無変調サンプル数を返します。</summary>
    /// <param name="packetLength">ヘッダーパケット長（FH=880 / BH=124）。</param>
    /// <returns>先頭無変調サンプル数。</returns>
    private int HeaderUnmodulatedSamplesFor(int packetLength) =>
        packetLength == FileHeaderBytes
            ? _profile.FileHeaderUnmodulatedSamples
            : _profile.BlockHeaderUnmodulatedSamples;

    /// <summary>
    /// ヘッダー先頭の無変調区間を読み飛ばし、論理サンプル時刻も進めます。
    /// </summary>
    /// <param name="samples">PCM サンプル列。</param>
    /// <param name="warpedCursor">補正後サンプル上の読み取り位置。</param>
    /// <param name="logicalOffset">WAV 先頭からの論理サンプル位置。</param>
    /// <param name="unmodulatedSamples">先頭無変調区間のサンプル数。</param>
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
    /// CP/パイロットスコアで候補を絞り、ヘッダー先頭（パイロット6バイト+バージョン2バイト）一致で確定します。
    /// まず期待位置の厳密復調を試し、失敗時のみ近傍探索します。
    /// ステレオ時は L/R 分割ビットを結合してから復号します。
    /// </summary>
    /// <param name="leftSamples">L チャンネル PCM サンプル列。</param>
    /// <param name="rightSamples">R チャンネル PCM サンプル列。</param>
    /// <param name="warpedCursor">補正後サンプル上の読み取り位置。</param>
    /// <param name="logicalOffset">WAV 先頭からの論理サンプル位置。</param>
    /// <param name="ofdm">OFDM 生成／復調器。</param>
    /// <param name="payloadLength">ヘッダーペイロード長（バイト）。</param>
    /// <param name="expectedPilot">期待するパイロットパターン（省略可）。</param>
    /// <param name="searchRadius">同期探索半径（サンプル）。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ用初期シード。</param>
    /// <returns>同期確定したヘッダーバイト列。</returns>
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
        // ヘッダー復号は常に L 側のみ参照する。
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

    /// <summary>指定開始位置でヘッダーをソフト復号し、パイロット一致なら成功します。</summary>
    /// <param name="leftSamples">L チャンネル PCM サンプル列。</param>
    /// <param name="rightSamples">R チャンネル PCM サンプル列。</param>
    /// <param name="start">開始サンプル位置。</param>
    /// <param name="logicalOffset">WAV 先頭からの論理サンプル位置。</param>
    /// <param name="ofdm">OFDM 生成／復調器。</param>
    /// <param name="totalBitCount">結合後の総ビット数。</param>
    /// <param name="channelBitCount">1 チャンネルあたりのビット数。</param>
    /// <param name="sampleCount">必要サンプル数。</param>
    /// <param name="payloadLength">ペイロード長（バイト）。</param>
    /// <param name="rsByteLength">RS 符号化後バイト長。</param>
    /// <param name="expectedPilot">期待するパイロットパターン（省略可）。</param>
    /// <param name="stereoSplit">ステレオ分割復調するか。</param>
    /// <param name="perSymbolSearchRadius">シンボルごとの微調整探索半径。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ用初期シード。</param>
    /// <param name="payload">成功時のヘッダーペイロード。</param>
    /// <param name="endCursor">成功時の終了カーソル。</param>
    /// <returns>パイロット一致で復号成功なら <see langword="true"/>。</returns>
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

    /// <summary>ソフト LLR から畳み込み BCJR→RS でヘッダーペイロードを復号します。</summary>
    /// <param name="llrs">ソフト LLR 列。</param>
    /// <param name="payloadLength">ヘッダーペイロード長（バイト）。</param>
    /// <param name="rsByteLength">RS 符号化後バイト長。</param>
    /// <returns>復号したヘッダーペイロード。</returns>
    private static byte[] DecodeHeaderFromSoftLlrs(double[] llrs, int payloadLength, int rsByteLength)
    {
        // 仕様: ヘッダー復号はソフト LLR を畳み込み BCJR に通し、中間ハード判定の情報落ちを避ける。
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

    /// <summary>CP/パイロットスコアに基づく同期候補オフセットを収集します。</summary>
    /// <param name="samples">同期探索対象のサンプル列。</param>
    /// <param name="expectedStart">期待する開始サンプル位置。</param>
    /// <param name="sampleCount">パケットに必要なサンプル数。</param>
    /// <param name="searchRadius">同期探索半径（サンプル）。</param>
    /// <param name="ofdm">OFDM 生成／復調器。</param>
    /// <param name="probeSymbols">ロック評価に使う OFDM シンボル数。</param>
    /// <param name="useRightChannel">R チャンネルのパイロット配置を使うか。</param>
    /// <returns>試行すべき開始位置のリスト。</returns>
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

        /// <summary>重複を避けて同期候補開始位置を追加します。</summary>
        /// <param name="start">追加する同期候補開始位置。</param>
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

    /// <summary>ヘッダー先頭のパイロットとバージョンが期待値と一致するか判定します。</summary>
    /// <param name="header">検査するヘッダーバイト列。</param>
    /// <param name="expectedPilot">期待するパイロット 6 バイト。</param>
    /// <returns>パイロットとバージョンが一致すれば <see langword="true"/>。</returns>
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

    /// <summary>データブロックをターボ→畳み込み→QAM 変調して PCM へ追記します。</summary>
    /// <param name="leftPcm">L チャンネル PCM リスト。</param>
    /// <param name="rightPcm">R チャンネル PCM リスト。</param>
    /// <param name="ofdm">OFDM 生成／復調器。</param>
    /// <param name="payload">ブロックデータ本体。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ用初期シード。</param>
    /// <param name="punctureRate">畳み込みパンクチャ率。</param>
    private static void AppendModulatedDataBlock(
        List<Complex> leftPcm,
        List<Complex> rightPcm,
        OfdmGenerator ofdm,
        byte[] payload,
        int interleaveInitSeed,
        ConvolutionalCode.PunctureRate punctureRate)
    {
        var packed = PackDataBlockWithCrc(payload);
        // データ部: ターボ → 畳み込み → QAM。ステレオ時はビット列を L/R に分割して SC 合計 2 倍相当にする。
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
    /// ペイロード + CRC-32 をターボ符号単位（1024 バイト）境界へパディングします。
    /// </summary>
    /// <param name="payload">最大 4096 バイトのブロック本体。</param>
    /// <returns>CRC 付き・ターボ境界パディング済みバイト列。</returns>
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

    /// <summary>ターボ符号単位（1024 バイト）境界へ切り上げた長さを返します。</summary>
    /// <param name="contentLength">切り上げ前の内容長。</param>
    /// <returns>1024 バイト境界へ切り上げた長さ。</returns>
    private static int TurboPaddedLength(int contentLength)
    {
        var unit = TurboEcc1024.DataUnitBytes;
        return ((Math.Max(contentLength, 1) + unit - 1) / unit) * unit;
    }

    /// <summary>
    /// ブロック時系列インターリーブの送出順を返します（data_struct.mdc）。
    /// 偶数パスは 0,1,2,…、奇数パスは奇偶入れ替え 1,0,3,2,…。
    /// </summary>
    /// <param name="blockCount">ブロック総数。</param>
    /// <param name="passIndex">パス番号（偶数=順、奇数=奇偶入れ替え）。</param>
    /// <returns>送出するブロック番号の配列。</returns>
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
    /// インターリーブパスごとのデータ部 SC / 変調を返します（data_struct.mdc）。
    /// パス0は指定どおり。パス1以降は SC・変調を1段階下げます（ステレオ/モノラルは不変）。
    /// </summary>
    /// <param name="passIndex">パス番号（0 は基準、1 以降はダウングレード）。</param>
    /// <param name="baseSubcarriers">パス0のサブキャリア数。</param>
    /// <param name="baseModulation">パス0の変調方式。</param>
    /// <returns>当該パスの SC 数と変調方式。</returns>
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

    /// <summary>符号化ビット列をステレオ用に L/R へ半分ずつ分割します。</summary>
    /// <param name="bits">分割する符号化ビット列。</param>
    /// <param name="leftBits">分割後の L ビット列。</param>
    /// <param name="rightBits">分割後の R ビット列。</param>
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

    /// <summary>ステレオ L/R ビット列を単一ビット列へ結合します。</summary>
    /// <param name="leftBits">L チャンネルビット列。</param>
    /// <param name="rightBits">R チャンネルビット列。</param>
    /// <param name="totalBits">結合後の総ビット数。</param>
    /// <returns>結合したビット列。</returns>
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

    /// <summary>同期探索付きでデータブロックをソフト（必要時ハード）復号します。</summary>
    /// <param name="leftSamples">L チャンネル PCM サンプル列。</param>
    /// <param name="rightSamples">R チャンネル PCM サンプル列。</param>
    /// <param name="warpedCursor">補正後サンプル上の読み取り位置。</param>
    /// <param name="logicalOffset">WAV 先頭からの論理サンプル位置。</param>
    /// <param name="ofdm">OFDM 生成／復調器。</param>
    /// <param name="searchRadius">同期探索半径（サンプル）。</param>
    /// <param name="expectedBlockHash">期待するブロック SHA-256。</param>
    /// <param name="payloadLength">ペイロード長（バイト）。</param>
    /// <param name="modulationScheme">変調方式。</param>
    /// <param name="tuning">復号実行時チューニング（省略時は既定）。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ用初期シード。</param>
    /// <param name="punctureRate">畳み込みパンクチャ率。</param>
    /// <param name="wowLocked">wow 推定がロック済みか。</param>
    /// <param name="statusBoard">画面表示用ステータスボード（省略可）。</param>
    /// <param name="diag">試行診断情報の出力先。</param>
    /// <returns>パディング済みデータブロック（CRC 付き）。</returns>
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

        /// <summary>指定開始位置でデータブロック復号を試し、ハッシュ/CRC 一致なら成功します。</summary>
        /// <param name="start">試行する開始サンプル位置。</param>
        /// <param name="perSymbolRadius">シンボルごとの探索半径。</param>
        /// <param name="padded">成功時のパディング済みペイロード。</param>
        /// <param name="endCursor">成功時の終了カーソル。</param>
        /// <param name="allowHardFallback">ソフト失敗後にハード復号を試すか。</param>
        /// <returns>ハッシュ／CRC 一致で成功なら <see langword="true"/>。</returns>
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
                        // 同期ずれで LLR が潰れている位置は turbo まで進まない。
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

        // 探索位置は ScoreLock で安い順位付けし、有望な候補から ECC を試す。
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
                    // 直前の厳密試行で失敗済み。
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
            // 上位は soft+hard、それ以外は soft のみ（hard 全復調の二重コストを避ける）。
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

        // スコア上位で落ちた場合、残りを soft のみで追加試行（上限付き）。
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

    /// <summary>固定開始位置のスライスからハードビットを復調します。</summary>
    /// <param name="ofdm">OFDM 生成／復調器。</param>
    /// <param name="leftSamples">L チャンネル PCM サンプル列。</param>
    /// <param name="rightSamples">R チャンネル PCM サンプル列。</param>
    /// <param name="start">固定スライスの開始位置。</param>
    /// <param name="channelBitCount">1 チャンネルあたりのビット数。</param>
    /// <param name="totalBitCount">結合後の総ビット数。</param>
    /// <param name="stereoSplit">ステレオ分割復調するか。</param>
    /// <param name="logical">論理サンプル位置（インターリーブ epoch 用）。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ用初期シード。</param>
    /// <returns>復調したハードビット列。</returns>
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

    /// <summary>ストリームからハードビットを復調し、カーソルを進めます。</summary>
    /// <param name="ofdm">OFDM 生成／復調器。</param>
    /// <param name="leftSamples">L チャンネル PCM サンプル列。</param>
    /// <param name="rightSamples">R チャンネル PCM サンプル列。</param>
    /// <param name="cursor">読み取りカーソル（更新される）。</param>
    /// <param name="channelBitCount">1 チャンネルあたりのビット数。</param>
    /// <param name="totalBitCount">結合後の総ビット数。</param>
    /// <param name="stereoSplit">ステレオ分割復調するか。</param>
    /// <param name="logical">論理サンプル位置（インターリーブ epoch 用）。</param>
    /// <param name="searchRadius">同期探索半径（サンプル）。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ用初期シード。</param>
    /// <returns>復調したハードビット列。</returns>
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

    /// <summary>ストリームからソフト LLR を復調し、必要なら I-Q/FFT を StatusBoard へ通知します。</summary>
    /// <param name="ofdm">OFDM 生成／復調器。</param>
    /// <param name="leftSamples">L チャンネル PCM サンプル列。</param>
    /// <param name="rightSamples">R チャンネル PCM サンプル列。</param>
    /// <param name="cursor">読み取りカーソル（更新される）。</param>
    /// <param name="channelBitCount">1 チャンネルあたりのビット数。</param>
    /// <param name="totalBitCount">結合後の総ビット数。</param>
    /// <param name="stereoSplit">ステレオ分割復調するか。</param>
    /// <param name="logical">論理サンプル位置（インターリーブ epoch 用）。</param>
    /// <param name="searchRadius">同期探索半径（サンプル）。</param>
    /// <param name="noiseVariance">ソフト復調の雑音分散。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ用初期シード。</param>
    /// <param name="modulationScheme">変調方式。</param>
    /// <param name="statusBoard">画面表示用ステータスボード（省略可）。</param>
    /// <returns>復調したソフト LLR 列。</returns>
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

    /// <summary>LLR の絶対値を指定上限へクリップします。</summary>
    /// <param name="llrs">クリップ対象の LLR 列。</param>
    /// <param name="maxAbs">LLR 絶対値の上限。</param>
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

    /// <summary>LLR の平均絶対値を返します。</summary>
    /// <param name="llrs">平均を取る LLR 列。</param>
    /// <returns>LLR の平均絶対値。</returns>
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

    /// <summary>ソフト/ハード候補のうちブロックハッシュと CRC が合う方を優先して返します。</summary>
    /// <param name="softCandidate">ソフト復号候補。</param>
    /// <param name="hardCandidate">ハード復号候補。</param>
    /// <param name="expectedBlockHash">期待するブロック SHA-256。</param>
    /// <param name="payloadLength">有効ペイロード長。</param>
    /// <returns>ハッシュ／CRC が合う方（両方失敗時はソフト）。</returns>
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

    /// <summary>候補ペイロードが期待ハッシュと CRC-32 を満たすか判定します。</summary>
    /// <param name="candidate">ハッシュ／CRC 判定対象。</param>
    /// <param name="expectedBlockHash">期待するブロック SHA-256。</param>
    /// <param name="payloadLength">有効ペイロード長。</param>
    /// <returns>ハッシュと CRC が一致すれば <see langword="true"/>。</returns>
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

    /// <summary>1024 バイト単位でターボ符号化します。</summary>
    /// <param name="padded">1024 バイト境界へ揃えたペイロード。</param>
    /// <returns>ターボ符号化バイト列。</returns>
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

    /// <summary>LLR 信頼度からターボ反復回数を決定します。</summary>
    /// <param name="infoLlrs">信頼度判定用の情報 LLR。</param>
    /// <param name="tuning">反復回数の上下限などを含むチューニング。</param>
    /// <returns>採用するターボ反復回数。</returns>
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

    /// <summary>ハード判定ビットからターボ復号します。</summary>
    /// <param name="turboEncoded">ターボ符号化バイト列。</param>
    /// <param name="paddedLength">復号後のパディング長。</param>
    /// <param name="iterations">ターボ反復回数。</param>
    /// <returns>パディング済み復号ペイロード。</returns>
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
    /// 畳み込みソフト出力 LLR をターボへ渡し、失敗時はハード復号へフォールバックします。
    /// </summary>
    /// <param name="infoLlrs">ターボ軟入力 LLR。</param>
    /// <param name="turboEncodedHard">ハード判定のターボ符号語。</param>
    /// <param name="paddedLength">復号後のパディング長。</param>
    /// <param name="iterations">ターボ反復回数。</param>
    /// <returns>パディング済み復号ペイロード。</returns>
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

    /// <summary>データブロック同期復号の試行診断情報です。</summary>
    private readonly record struct DataDecodeDiag(
        int TotalAttempts,
        bool HardMatchSucceeded,
        bool SoftMatchSucceeded,
        bool FallbackUsed,
        int StartDeltaSamples);

    /// <summary>RS 符号化後のバイト長を返します。</summary>
    /// <param name="payloadLength">RS 符号化前のペイロード長。</param>
    /// <returns>RS 符号化後のバイト長。</returns>
    private static int GetReedSolomonEncodedLength(int payloadLength)
    {
        var paddedLength = ((payloadLength + RsEcc256.DataUnitSize - 1) / RsEcc256.DataUnitSize) * RsEcc256.DataUnitSize;
        if (paddedLength == 0)
        {
            paddedLength = RsEcc256.DataUnitSize;
        }

        return (paddedLength / RsEcc256.DataUnitSize) * RsEcc256.EncodedUnitSize;
    }

    /// <summary>畳み込み符号化（終端あり）後のバイト長を返します。</summary>
    /// <param name="inputByteLength">畳み込み入力バイト長。</param>
    /// <param name="punctureRate">畳み込みパンクチャ率。</param>
    /// <returns>畳み込み符号化後のバイト長。</returns>
    private static int GetConvolutionalEncodedLength(int inputByteLength, ConvolutionalCode.PunctureRate punctureRate)
    {
        var encodedBits = ConvolutionalCode.GetEncodedBitLength(inputByteLength * 8, terminated: true, punctureRate: punctureRate);
        return (encodedBits + 7) / 8;
    }

    /// <summary>データ部変調方式に応じた畳み込みパンクチャ率を返します。</summary>
    /// <param name="modulationScheme">データ部変調方式。</param>
    /// <returns>変調に対応するパンクチャ率。</returns>
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

    /// <summary>ブロックヘッダーからデータ部の SC 数と変調方式を読み取ります。</summary>
    /// <param name="blockHeader">ブロックヘッダーバイト列。</param>
    /// <returns>SC 数と変調方式。</returns>
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

    /// <summary>ヘッダーペイロードに RS 符号化を適用します。</summary>
    /// <param name="payload">RS 符号化対象のヘッダーペイロード。</param>
    /// <returns>RS 符号化後のバイト列。</returns>
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

    /// <summary>RS 符号化列を復号します。</summary>
    /// <param name="encoded">RS 符号語バイト列。</param>
    /// <returns>RS 復号後のペイロード。</returns>
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

    /// <summary>仕様どおりのファイルヘッダー（880 バイト）を組み立てます。</summary>
    /// <param name="fileInfo">ファイル名／属性の取得元。</param>
    /// <param name="fileSize">ファイルサイズ（バイト）。</param>
    /// <param name="blockCount">ファイルのブロック数。</param>
    /// <param name="fileHash">ファイル全体 SHA-512（64 バイト）。</param>
    /// <returns>880 バイトのファイルヘッダー。</returns>
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

    /// <summary>作成/更新日時とアトリビュートをヘッダー属性欄へ書き込みます。</summary>
    /// <param name="dest">属性欄（20 バイト）への書き込み先。</param>
    /// <param name="fileInfo">ファイル名／属性の取得元。</param>
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

    /// <summary>日時を 7 バイト（yyyy/mm/dd/HH/MM/SS）で書き込みます。</summary>
    /// <param name="dest">7 バイト日時欄への書き込み先。</param>
    /// <param name="timestamp">書き込む日時。</param>
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

    /// <summary>仕様どおりのブロックヘッダー（124 バイト）を組み立てます。</summary>
    /// <param name="subcarriers">サブキャリア数（9/18/27/36）。</param>
    /// <param name="modulationMode">変調方式コード（1–4）。</param>
    /// <param name="channelMode">チャンネルモード（0=モノラル / 1=ステレオ）。</param>
    /// <param name="blockIndex">ファイル内ブロック位置。</param>
    /// <param name="blockSize">ブロックペイロード長（バイト）。</param>
    /// <param name="blockHash">ブロック SHA-256（32 バイト）。</param>
    /// <param name="fileHash">ファイル全体 SHA-512（64 バイト）。</param>
    /// <returns>124 バイトのブロックヘッダー。</returns>
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

    /// <summary>ヘッダーの CRC-32 を検証し、不一致なら例外を投げます。</summary>
    /// <param name="header">検証対象のヘッダーバイト列。</param>
    /// <param name="headerName">エラーメッセージ用ヘッダー名。</param>
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

    /// <summary>ヘッダー先頭のパイロットとバージョンを検証します。</summary>
    /// <param name="header">検証対象のヘッダーバイト列。</param>
    /// <param name="expectedPilot">期待するパイロット 6 バイト。</param>
    /// <param name="headerName">エラーメッセージ用ヘッダー名。</param>
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

    /// <summary>ファイルを最大 4096 バイトのブロック列へ分割します。</summary>
    /// <param name="fileBytes">分割するファイルバイト列。</param>
    /// <returns>最大 4096 バイト単位のブロック列。</returns>
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

    /// <summary>バイト列を MSB 先行のビット列へ変換します。</summary>
    /// <param name="bytes">変換元バイト列。</param>
    /// <returns>MSB 先行のビット列。</returns>
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

    /// <summary>MSB 先行のビット列をバイト列へ変換します。</summary>
    /// <param name="bits">変換元ビット列。</param>
    /// <returns>パックしたバイト列。</returns>
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

    /// <summary>無音サンプルを L（必要なら R）へ追記します。</summary>
    /// <param name="leftPcm">L チャンネル PCM リスト。</param>
    /// <param name="rightPcm">R チャンネル PCM リスト。</param>
    /// <param name="sampleCount">追記する無音サンプル数。</param>
    /// <param name="stereo">ステレオとして R も追記するか。</param>
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

    /// <summary>L/R サンプル配列ペアを PCM リストへ追記します。</summary>
    /// <param name="leftPcm">L チャンネル PCM リスト。</param>
    /// <param name="rightPcm">R チャンネル PCM リスト。</param>
    /// <param name="pair">L/R サンプル配列ペア。</param>
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

    /// <summary>カーソルを指定サンプル数だけ進め、新しい位置を返します。</summary>
    /// <param name="samples">PCM サンプル列。</param>
    /// <param name="cursor">現在の読み取り位置。</param>
    /// <param name="count">進める／切り出すサンプル数。</param>
    /// <returns>進めた後のカーソル位置。</returns>
    private static int SkipSamples(Complex[] samples, int cursor, int count)
    {
        var next = cursor + count;
        if (next > samples.Length)
        {
            throw new InvalidDataException("WAV ended while skipping preamble/silence.");
        }

        return next;
    }

    /// <summary>カーソル位置から指定長のサンプルを切り出して進めます。</summary>
    /// <param name="samples">PCM サンプル列。</param>
    /// <param name="cursor">現在の読み取り位置（更新される）。</param>
    /// <param name="count">切り出すサンプル数。</param>
    /// <returns>切り出したサンプル配列。</returns>
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
    /// <param name="profile">送信プロファイル。</param>
    /// <param name="fileSizeBytes">見積り対象のファイルサイズ（バイト）。</param>
    /// <returns>送信時間の内訳見積り。</returns>
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

        // 先頭無音(LEAD)は見積行に出さない（実 PCM には残す）。合計秒には含める。
        totalSamples += profile.LeadingSilenceSamples;
        AddSegment(segments, "プリアンブル", profile.UnmodulatedPreambleSamples, profile.SampleRate, ref totalSamples);

        var openingHeaderOfdm = codec.CreateHeaderOfdm(profile.ActiveSubcarriers);
        var openingFhSamples = HeaderPacketSamples(openingHeaderOfdm, FileHeaderBytes, profile.FileHeaderUnmodulatedSamples);
        AddSegment(segments, "FH", openingFhSamples, profile.SampleRate, ref totalSamples);

        OfdmGenerator? trailingHeaderOfdm = openingHeaderOfdm;
        for (var pass = 0; pass < profile.BlockInterleaveFactor; pass++)
        {
            var (passSc, passMod) = ResolveInterleavePassModulation(
                pass,
                profile.ActiveSubcarriers,
                profile.ModulationScheme);
            var headerOfdm = codec.CreateHeaderOfdm(passSc);
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
    /// 内訳を画面表示しやすいテキストへ整形します。
    /// </summary>
    /// <param name="estimate">整形対象の見積り結果。</param>
    /// <param name="digits">秒数の小数桁数。</param>
    /// <returns>画面表示用の内訳テキスト。</returns>
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

    /// <summary>ファイルサイズから各ブロックのペイロード長配列を構築します。</summary>
    /// <param name="fileSizeBytes">ファイルサイズ（バイト）。</param>
    /// <returns>各ブロックのペイロード長配列。</returns>
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

    /// <summary>ヘッダーパケット（無変調＋変調部）の総サンプル数を見積もります。</summary>
    /// <param name="headerOfdm">ヘッダー用 OFDM 生成器。</param>
    /// <param name="payloadLength">ヘッダーペイロード長（バイト）。</param>
    /// <param name="unmodulatedSamples">先頭無変調サンプル数。</param>
    /// <returns>ヘッダーパケットの総サンプル数。</returns>
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

    /// <summary>データパケットの総サンプル数を見積もります。</summary>
    /// <param name="dataOfdm">データ部用 OFDM 生成器。</param>
    /// <param name="payloadLength">データブロックのペイロード長。</param>
    /// <param name="channelMode">チャンネルモード（0=モノラル / 1=ステレオ）。</param>
    /// <param name="modulationScheme">変調方式。</param>
    /// <returns>データパケットの総サンプル数。</returns>
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

    /// <summary>伝送時間見積りのセグメントを追加し、合計サンプルを更新します。</summary>
    /// <param name="segments">セグメント一覧。</param>
    /// <param name="label">セグメント表示ラベル。</param>
    /// <param name="samples">セグメントのサンプル数。</param>
    /// <param name="sampleRate">秒換算用サンプリング周波数。</param>
    /// <param name="totalSamples">累計サンプル数（更新される）。</param>
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

    /// <summary>符号化用のデータブロック（ペイロード本体とその長さ）です。</summary>
    private readonly record struct DataBlock(byte[] Payload, int PayloadLength);
}

/// <summary>
/// 16-bit PCM WAV 書き出しユーティリティです（モノラル 1ch / ステレオ 2ch）。
/// </summary>
public static class WavWriter
{
    /// <summary>
    /// 16-bit PCM WAV を逐次書き込みするストリームライターを生成します。
    /// </summary>
    /// <param name="path">書き出し先 WAV パス。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="peakTarget">実数サンプルに適用するスケール（通常 0..1）。</param>
    /// <param name="channelMode">チャンネルモード。</param>
    /// <returns>逐次書き込みライター。</returns>
    public static StreamingPcm16Writer CreateStreamingPcm16(
        string path,
        int sampleRate,
        double peakTarget,
        ChannelMode channelMode)
    {
        return new StreamingPcm16Writer(path, sampleRate, peakTarget, channelMode);
    }

    /// <summary>
    /// チャンネルモードに応じて 1ch または 2ch の WAV を書き出します。
    /// モノラル時は <paramref name="right"/> を無視し、L のみを出力します。
    /// </summary>
    /// <param name="path">書き出し先 WAV パス。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="left">L チャンネルサンプル列。</param>
    /// <param name="right">R チャンネルサンプル列。</param>
    /// <param name="peakTarget">ピーク正規化の目標振幅。</param>
    /// <param name="channelMode">チャンネルモード（0=モノラル / 1=ステレオ）。</param>
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

    /// <summary>16-bit モノラル PCM WAV を書き出します。</summary>
    /// <param name="path">書き出し先 WAV パス。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="samples">モノラル PCM サンプル列。</param>
    /// <param name="peakTarget">ピーク正規化の目標振幅。</param>
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

    /// <summary>16-bit ステレオ PCM WAV を書き出します。</summary>
    /// <param name="path">書き出し先 WAV パス。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="left">L チャンネルサンプル列。</param>
    /// <param name="right">R チャンネルサンプル列。</param>
    /// <param name="peakTarget">ピーク正規化の目標振幅。</param>
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

    /// <summary>実数値をスケールして 16-bit PCM へ量子化します。</summary>
    /// <param name="value">量子化前の実数値（通常 -1〜1）。</param>
    /// <param name="scale">ピーク正規化スケール。</param>
    /// <returns>量子化した 16-bit PCM 値。</returns>
    private static short ToPcm16(double value, double scale)
    {
        return (short)Math.Round(Math.Clamp(value * scale, -1.0, 1.0) * short.MaxValue);
    }

    /// <summary>
    /// 16-bit PCM WAV を逐次書き込みするライターです。
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
        /// PCM サンプルチャンクを追記します。
        /// </summary>
        /// <param name="left">L チャンネルサンプル。</param>
        /// <param name="right">R チャンネルサンプル（モノラル時は空）。</param>
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
            _writer.Write(0); // 後で確定
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
            _writer.Write(0); // 後で確定
        }
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
    /// <param name="path">読み取り元 WAV パス。</param>
    /// <returns>L/R サンプル列（モノラル時 Right は空）。</returns>
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
    /// <param name="path">読み取り元 WAV パス。</param>
    /// <returns>ステレオ L/R サンプル列。</returns>
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

