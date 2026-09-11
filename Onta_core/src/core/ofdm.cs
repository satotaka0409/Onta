using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Onta.Core;

/// <summary>
/// OFDM サブキャリアの変調方式です。
/// </summary>
public enum ModulationScheme : byte
{
    /// <summary>
    /// BPSK（1 bit/symbol）。
    /// </summary>
    Bpsk = 1,

    /// <summary>
    /// QPSK（2 bit/symbol）。
    /// </summary>
    Qpsk = 2,

    /// <summary>
    /// 16QAM（4 bit/symbol）。
    /// </summary>
    Qam16 = 3,

    /// <summary>
    /// 64QAM（6 bit/symbol）。
    /// </summary>
    Qam64 = 4
}

/// <summary>
/// 伝送チャネル構成です。
/// </summary>
public enum ChannelMode : byte
{
    /// <summary>
    /// モノラル伝送。
    /// </summary>
    Mono = 0,

    /// <summary>
    /// ステレオ伝送。
    /// </summary>
    Stereo = 1
}

/// <summary>
/// 使用するキャリア配置ファミリです。
/// </summary>
public enum OfdmCarrierGrid : byte
{
    Sc9Family = 0,

    Sc27Family = 1
}

/// <summary>
/// OFDM 変復調の設定パラメータです。
/// </summary>
public sealed record OfdmConfig
{
    /// <summary>
    /// FFT サイズ。
    /// </summary>
    public int FftSize { get; }

    /// <summary>
    /// 有効サブキャリア数（9/18/27/36）。
    /// </summary>
    public int ActiveSubcarriers { get; }

    /// <summary>
    /// 巡回プレフィックス長。
    /// </summary>
    public int CyclicPrefixLength { get; }

    /// <summary>
    /// OFDM シンボル数。
    /// </summary>
    public int OfdmSymbolCount { get; }

    /// <summary>
    /// 変調方式。
    /// </summary>
    public ModulationScheme ModulationScheme { get; }

    /// <summary>
    /// チャネルモード。
    /// </summary>
    public ChannelMode ChannelMode { get; }

    /// <summary>
    /// 周波数インタリーブ有効フラグ。
    /// </summary>
    public bool EnableFrequencyInterleaving { get; }

    /// <summary>
    /// パイロット間隔。
    /// </summary>
    public int PilotSpacing { get; }

    /// <summary>
    /// ステレオ時の左右キャリアずらし量（bin）。
    /// </summary>
    public int StereoFrequencyShiftBins { get; }

    /// <summary>
    /// サンプルレート（Hz）。
    /// </summary>
    public int SampleRate { get; }

    /// <summary>
    /// 周波数インタリーブ更新間隔（シンボル単位）。
    /// </summary>
    public int FrequencyInterleaveIntervalSymbols { get; }

    /// <summary>
    /// 乱数シード。
    /// </summary>
    public int RandomSeed { get; }

    /// <summary>
    /// 概念上の左チャネルキャリア番号一覧。
    /// </summary>
    public IReadOnlyList<int> ConceptualLeftBins { get; }

    /// <summary>
    /// キャリアグリッド種別。
    /// </summary>
    public OfdmCarrierGrid CarrierGrid { get; }

    /// <summary>
    /// OFDM 設定を生成します。
    /// </summary>
    /// <param name="fftSize">FFT サイズ。</param>
    /// <param name="activeSubcarriers">有効サブキャリア数。</param>
    /// <param name="cyclicPrefixLength">巡回プレフィックス長。</param>
    /// <param name="ofdmSymbolCount">OFDM シンボル数。</param>
    /// <param name="modulationScheme">変調方式。</param>
    /// <param name="channelMode">チャネルモード。</param>
    /// <param name="enableFrequencyInterleaving">周波数インタリーブ有効フラグ。</param>
    /// <param name="pilotSpacing">パイロット間隔。</param>
    /// <param name="stereoFrequencyShiftBins">ステレオ時の左右キャリアずらし量。</param>
    /// <param name="sampleRate">サンプルレート（Hz）。</param>
    /// <param name="frequencyInterleaveIntervalSymbols">周波数インタリーブ更新間隔。</param>
    /// <param name="randomSeed">乱数シード。</param>
    /// <param name="conceptualLeftBins">概念上の左チャネルキャリア番号。</param>
    /// <param name="carrierGrid">キャリアグリッド種別。</param>
    public OfdmConfig(
        int fftSize,
        int activeSubcarriers,
        int cyclicPrefixLength,
        int ofdmSymbolCount,
        ModulationScheme modulationScheme = ModulationScheme.Qpsk,
        ChannelMode channelMode = ChannelMode.Mono,
        bool enableFrequencyInterleaving = true,
        int pilotSpacing = 9,
        int stereoFrequencyShiftBins = 1,
        int sampleRate = 44100,
        int frequencyInterleaveIntervalSymbols = 1,
        int randomSeed = 0,
        IReadOnlyList<int>? conceptualLeftBins = null,
        OfdmCarrierGrid? carrierGrid = null)
    {
        FftSize = fftSize;
        ActiveSubcarriers = activeSubcarriers;
        CyclicPrefixLength = cyclicPrefixLength;
        OfdmSymbolCount = ofdmSymbolCount;
        ModulationScheme = modulationScheme;
        ChannelMode = channelMode;
        EnableFrequencyInterleaving = enableFrequencyInterleaving;
        PilotSpacing = pilotSpacing;
        StereoFrequencyShiftBins = stereoFrequencyShiftBins;
        SampleRate = sampleRate;
        FrequencyInterleaveIntervalSymbols = frequencyInterleaveIntervalSymbols;
        RandomSeed = randomSeed;
        ConceptualLeftBins = conceptualLeftBins ?? ResolveConceptualLeftBins(activeSubcarriers);
        CarrierGrid = carrierGrid ?? ResolveCarrierGrid(activeSubcarriers);

        if (FftSize <= 0 || (FftSize & (FftSize - 1)) != 0)
        {
            throw new ArgumentException("FFT size must be a power of two and > 0.");
        }

        if (ActiveSubcarriers is not (9 or 18 or 27 or 36))
        {
            throw new ArgumentException(
                "Active subcarriers must be 9, 18, 27, or 36 (modulation.mdc).",
                nameof(activeSubcarriers));
        }

        if (ConceptualLeftBins.Count != ActiveSubcarriers)
        {
            throw new ArgumentException(
                $"Conceptual left bin count ({ConceptualLeftBins.Count}) must equal active subcarriers ({ActiveSubcarriers}).",
                nameof(conceptualLeftBins));
        }

        if (CyclicPrefixLength < 0 || CyclicPrefixLength >= FftSize)
        {
            throw new ArgumentException("Cyclic prefix length must be >= 0 and < FFT size.");
        }

        if (OfdmSymbolCount <= 0)
        {
            throw new ArgumentException("OFDM symbol count must be > 0.");
        }

        if (modulationScheme is not ModulationScheme.Bpsk
            and not ModulationScheme.Qpsk
            and not ModulationScheme.Qam16
            and not ModulationScheme.Qam64)
        {
            throw new ArgumentException("Modulation scheme must be BPSK, QPSK, 16QAM, or 64QAM.", nameof(modulationScheme));
        }

        if (channelMode is not ChannelMode.Mono and not ChannelMode.Stereo)
        {
            throw new ArgumentException("Channel mode must be Mono or Stereo.", nameof(channelMode));
        }

        if (pilotSpacing <= 0)
        {
            throw new ArgumentException("Pilot spacing must be > 0.", nameof(pilotSpacing));
        }

        if (stereoFrequencyShiftBins < 0)
        {
            throw new ArgumentException("Stereo frequency shift bins must be >= 0.", nameof(stereoFrequencyShiftBins));
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentException("Sample rate must be > 0.", nameof(sampleRate));
        }

        if (frequencyInterleaveIntervalSymbols <= 0)
        {
            throw new ArgumentException("Frequency interleave interval symbols must be > 0.", nameof(frequencyInterleaveIntervalSymbols));
        }

        ValidateCarrierBinsFitFft();
    }

    /// <summary>
    /// ValidateCarrierBinsFitFft を実行します。
    /// </summary>
    private void ValidateCarrierBinsFitFft()
    {
        if (CarrierGrid == OfdmCarrierGrid.Sc9Family)
        {
            foreach (var k in ConceptualLeftBins)
            {
                var leftHz = LeftCarrierHzSc9(k - 1);
                var rightHz = RightCarrierHzSc9(k - 1);
                var leftBin = HzToPositiveBin(leftHz, FftSize, SampleRate);
                var rightBin = HzToPositiveBin(rightHz, FftSize, SampleRate);
                if (leftBin <= 0 || leftBin >= FftSize / 2 || rightBin <= 0 || rightBin >= FftSize / 2)
                {
                    throw new ArgumentException(
                        $"SC-9 family carrier Hz (L={leftHz:F1}, R={rightHz:F1}) does not fit FFT={FftSize}.",
                        nameof(FftSize));
                }
            }

            return;
        }

        var maxConcept = ConceptualLeftBins.Max();
        if (ChannelMode == ChannelMode.Mono)
        {
            if (maxConcept >= FftSize / 2)
            {
                throw new ArgumentException(
                    $"FFT size {FftSize} is too small for conceptual L bin {maxConcept} (need < {FftSize / 2}).",
                    nameof(FftSize));
            }
        }
        else
        {
            var maxFineBin = (2 * maxConcept) + 1;
            if (maxFineBin >= FftSize / 2)
            {
                throw new ArgumentException(
                    $"FFT size {FftSize} is too small for stereo midpoint carriers (need max bin {maxFineBin} < {FftSize / 2}).",
                    nameof(FftSize));
            }
        }
    }

    /// <summary>
    /// Group A の左チャネルキャリア番号を返します。
    /// </summary>
    /// <returns>Group A のキャリア番号配列。</returns>
    public static int[] ResolveGroupALeftBins() => Enumerable.Range(1, 9).ToArray();

    /// <summary>
    /// Group B の左チャネルキャリア番号を返します。
    /// </summary>
    /// <returns>Group B のキャリア番号配列。</returns>
    public static int[] ResolveGroupBLeftBins() => Enumerable.Range(10, 9).ToArray();

    /// <summary>
    /// Group C の左チャネルキャリア番号を返します。
    /// </summary>
    /// <returns>Group C のキャリア番号配列。</returns>
    public static int[] ResolveGroupCLeftBins() => Enumerable.Range(19, 9).ToArray();

    /// <summary>
    /// Group D の左チャネルキャリア番号を返します。
    /// </summary>
    /// <returns>Group D のキャリア番号配列。</returns>
    public static int[] ResolveGroupDLeftBins() => Enumerable.Range(28, 9).ToArray();

    /// <summary>
    /// サブキャリア数に応じた概念左キャリア番号列を返します。
    /// </summary>
    /// <param name="activeSubcarriers">有効サブキャリア数。</param>
    /// <returns>概念左キャリア番号配列。</returns>
    public static int[] ResolveConceptualLeftBins(int activeSubcarriers) =>
        activeSubcarriers switch
        {
            9 => ResolveGroupBLeftBins(),
            18 => ResolveGroupALeftBins().Concat(ResolveGroupBLeftBins()).ToArray(),
            27 => ResolveGroupALeftBins().Concat(ResolveGroupBLeftBins()).Concat(ResolveGroupCLeftBins()).ToArray(),
            36 => ResolveGroupALeftBins()
                .Concat(ResolveGroupBLeftBins())
                .Concat(ResolveGroupCLeftBins())
                .Concat(ResolveGroupDLeftBins())
                .ToArray(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(activeSubcarriers),
                activeSubcarriers,
                "Active subcarriers must be 9, 18, 27, or 36.")
        };

    /// <summary>
    /// サブキャリア構成の最大概念左キャリア番号を返します。
    /// </summary>
    /// <param name="activeSubcarriers">有効サブキャリア数。</param>
    /// <returns>最大概念左キャリア番号。</returns>
    public static int MaxConceptualLeftBin(int activeSubcarriers) =>
        ResolveConceptualLeftBins(activeSubcarriers)[^1];

    /// <summary>SC-27/36 系列のキャリア間隔（Hz）を返します。</summary>
    /// <param name="sampleRate">サンプルレート（Hz）。</param>
    /// <returns>キャリア間隔（Hz）。</returns>
    public static double DeltaF27(int sampleRate = 44100) => sampleRate / 128.0;

    /// <summary>SC-9/18 系列のキャリア間隔（Hz）を返します。</summary>
    /// <param name="sampleRate">サンプルレート（Hz）。</param>
    /// <returns>キャリア間隔（Hz）。</returns>
    public static double DeltaF9(int sampleRate = 44100) => DeltaF27(sampleRate) * 1.3;

    public const double Sc9StartHz = 440.0;

    /// <summary>SC-9 系列の左チャネルキャリア周波数を返します。</summary>
    /// <param name="zeroBasedChannelIndex">0 起点のチャネル番号。</param>
    /// <param name="sampleRate">サンプルレート（Hz）。</param>
    /// <returns>キャリア周波数（Hz）。</returns>
    public static double LeftCarrierHzSc9(int zeroBasedChannelIndex, int sampleRate = 44100) =>
        Sc9StartHz + zeroBasedChannelIndex * DeltaF9(sampleRate);

    /// <summary>SC-9 系列の右チャネルキャリア周波数を返します。</summary>
    /// <param name="zeroBasedChannelIndex">0 起点のチャネル番号。</param>
    /// <param name="sampleRate">サンプルレート（Hz）。</param>
    /// <returns>キャリア周波数（Hz）。</returns>
    public static double RightCarrierHzSc9(int zeroBasedChannelIndex, int sampleRate = 44100) =>
        LeftCarrierHzSc9(zeroBasedChannelIndex, sampleRate) + (DeltaF9(sampleRate) / 2.0);

    /// <summary>周波数（Hz）を正側FFTビンへ変換します。</summary>
    /// <param name="hz">周波数（Hz）。</param>
    /// <param name="fftSize">FFT サイズ。</param>
    /// <param name="sampleRate">サンプルレート（Hz）。</param>
    /// <returns>正側ビン番号。</returns>
    public static int HzToPositiveBin(double hz, int fftSize, int sampleRate)
    {
        var bin = (int)Math.Round(hz * fftSize / sampleRate);
        return Math.Clamp(bin, 1, (fftSize / 2) - 1);
    }

    /// <summary>
    /// サブキャリア数からキャリアグリッド種別を解決します。
    /// </summary>
    /// <param name="activeSubcarriers">有効サブキャリア数。</param>
    /// <returns>キャリアグリッド種別。</returns>
    public static OfdmCarrierGrid ResolveCarrierGrid(int activeSubcarriers) =>
        activeSubcarriers is 9 or 18 ? OfdmCarrierGrid.Sc9Family : OfdmCarrierGrid.Sc27Family;

    /// <summary>
    /// サブキャリア構成とチャネルモードから推奨FFTサイズを返します。
    /// </summary>
    /// <param name="activeSubcarriers">有効サブキャリア数。</param>
    /// <param name="channelMode">チャネルモード。</param>
    /// <returns>推奨FFTサイズ。</returns>
    public static int ResolveFftSize(int activeSubcarriers, ChannelMode channelMode)
    {
        var grid = ResolveCarrierGrid(activeSubcarriers);
        if (grid == OfdmCarrierGrid.Sc9Family)
        {
            return 128;
        }

        var mono = 128;
        return channelMode == ChannelMode.Stereo ? mono * 2 : mono;
    }
}

/// <summary>
/// OFDM 変調/復調の本体実装です。
/// </summary>
public sealed class OfdmGenerator
{
    private enum CarrierChannel
    {
        Left,
        Right
    }

    private readonly OfdmConfig _config;
    private readonly Random _random;
    private readonly List<int> _leftAllCarrierBins;
    private readonly List<int> _leftPilotBins;
    private readonly List<int> _leftDataCarrierBase;
    private readonly Dictionary<int, ModulationScheme> _leftDataCarrierModulationByBin;
    private readonly int[] _leftDataCarrierNoInterleaveOrder;
    private readonly Dictionary<(long Epoch, int Seed), int[]> _leftInterleavedOrderCache;
    private readonly int[][] _leftPilotGroupedCarriers;
    private readonly List<int> _rightAllCarrierBins;
    private readonly List<int> _rightPilotBins;
    private readonly List<int> _rightDataCarrierBase;
    private readonly Dictionary<int, ModulationScheme> _rightDataCarrierModulationByBin;
    private readonly int[] _rightDataCarrierNoInterleaveOrder;
    private readonly Dictionary<(long Epoch, int Seed), int[]> _rightInterleavedOrderCache;
    private readonly int[][] _rightPilotGroupedCarriers;
    private readonly Complex[] _scoreTimeNoCpScratch;
    private readonly Complex[] _scoreFreqBinsScratch;
    private Complex[]? _ifftConjugateScratch;
    private Complex[]? _ifftWorkScratch;
    private Complex[]? _unmodulatedLeftSymbol;
    private Complex[]? _unmodulatedRightSymbol;
    private readonly int _bitsPerOfdmSymbol;
    private ulong _randomBitPool;
    private int _randomBitCount;
    private static readonly int[] Qam16Levels = [-3, -1, 1, 3];
    private static readonly int[] Qam64Levels = [-7, -5, -3, -1, 1, 3, 5, 7];
    private static readonly double[] Qam16PamByBinary = BuildPamByBinary(2, Qam16Levels);
    private static readonly double[] Qam64PamByBinary = BuildPamByBinary(3, Qam64Levels);
    private static readonly Complex PilotSymbol = Complex.One;
    private static readonly Vector256<double> RealLaneMask = Vector256.Create(1.0, 0.0, 1.0, 0.0);
    private static readonly Vector<double> ConjugateSignMask = CreateConjugateSignMask();

    /// <summary>
    /// 現在のチャネルモードを返します。
    /// </summary>
    public ChannelMode ChannelMode => _config.ChannelMode;
    private static readonly Complex UnmodulatedCarrierSymbol = Complex.One;

    /// <summary>
    /// 内部処理です。
    /// </summary>
    public OfdmGenerator(OfdmConfig config)
    {
        _config = config;
        _random = config.RandomSeed == 0 ? Random.Shared : new Random(config.RandomSeed);

        (_leftAllCarrierBins, _leftPilotBins, _leftDataCarrierBase, _leftDataCarrierModulationByBin) =
            BuildChannelLayout(CarrierChannel.Left);
        (_rightAllCarrierBins, _rightPilotBins, _rightDataCarrierBase, _rightDataCarrierModulationByBin) =
            BuildChannelLayout(CarrierChannel.Right);
        _leftDataCarrierNoInterleaveOrder = _leftDataCarrierBase.ToArray();
        _rightDataCarrierNoInterleaveOrder = _rightDataCarrierBase.ToArray();
        _leftInterleavedOrderCache = new Dictionary<(long Epoch, int Seed), int[]>();
        _rightInterleavedOrderCache = new Dictionary<(long Epoch, int Seed), int[]>();
        _leftPilotGroupedCarriers = BuildPilotGroupedCarriers(_leftAllCarrierBins, _leftPilotBins);
        _rightPilotGroupedCarriers = BuildPilotGroupedCarriers(_rightAllCarrierBins, _rightPilotBins);
        _scoreTimeNoCpScratch = new Complex[_config.FftSize];
        _scoreFreqBinsScratch = new Complex[_config.FftSize];

        if (_leftDataCarrierBase.Count != _rightDataCarrierBase.Count)
        {
            throw new InvalidOperationException(
                $"L/R data carrier count mismatch: L={_leftDataCarrierBase.Count}, R={_rightDataCarrierBase.Count}.");
        }

        _bitsPerOfdmSymbol = _leftDataCarrierBase.Sum(bin => BitsPerModulation(_leftDataCarrierModulationByBin[bin]));
        var rightBitsPerOfdmSymbol = _rightDataCarrierBase.Sum(bin => BitsPerModulation(_rightDataCarrierModulationByBin[bin]));
        if (_bitsPerOfdmSymbol != rightBitsPerOfdmSymbol)
        {
            throw new InvalidOperationException(
                $"L/R bits-per-OFDM mismatch: L={_bitsPerOfdmSymbol}, R={rightBitsPerOfdmSymbol}.");
        }
    }

    /// <summary>
    /// 内部処理です。
    /// </summary>
    private (List<int> All, List<int> Pilots, List<int> DataBase, Dictionary<int, ModulationScheme> DataModulationByBin)
        BuildChannelLayout(CarrierChannel channel)
    {
        var all = GetPositiveCarrierBins(channel);
        if (all.Count != _config.ActiveSubcarriers)
        {
            throw new InvalidOperationException(
                $"Expected {_config.ActiveSubcarriers} positive carriers, got {all.Count}.");
        }

        var pilotSet = SelectPilotBins(all, _config.PilotSpacing);
        var pilots = pilotSet.OrderBy(x => x).ToList();
        var data = new List<int>(all.Count);
        var dataModulationByBin = new Dictionary<int, ModulationScheme>(all.Count);
        for (var i = 0; i < all.Count; i++)
        {
            var bin = all[i];
            if (pilotSet.Contains(bin))
            {
                continue;
            }

            data.Add(bin);
            var conceptualLeftBin = _config.ConceptualLeftBins[i];
            dataModulationByBin[bin] = ResolveEffectiveCarrierModulation(_config.ModulationScheme, conceptualLeftBin);
        }

        return (all, pilots, data, dataModulationByBin);
    }

    /// <summary>
    /// BuildPilotGroupedCarriers を構築します。
    /// </summary>
    /// <param name="allCarriers">allCarriers を指定します。</param>
    /// <param name="orderedPilots">orderedPilots を指定します。</param>
    /// <returns>処理結果。</returns>
    private static int[][] BuildPilotGroupedCarriers(List<int> allCarriers, List<int> orderedPilots)
    {
        if (orderedPilots.Count == 0)
        {
            return [];
        }

        var grouped = new List<int>[orderedPilots.Count];
        for (var i = 0; i < grouped.Length; i++)
        {
            grouped[i] = new List<int>();
        }

        foreach (var carrier in allCarriers)
        {
            var groupIndex = ResolvePilotGroupIndex(carrier, orderedPilots);
            grouped[groupIndex].Add(carrier);
        }

        var result = new int[grouped.Length][];
        for (var i = 0; i < grouped.Length; i++)
        {
            result[i] = grouped[i].ToArray();
        }

        return result;
    }

    /// <summary>
    /// IsGroupDConceptualLeftBin を判定します。
    /// </summary>
    /// <param name="conceptualLeftBin">conceptualLeftBin を指定します。</param>
    /// <returns>条件を満たす場合 true、それ以外は false。</returns>
    private static bool IsGroupDConceptualLeftBin(int conceptualLeftBin) => conceptualLeftBin is >= 28 and <= 36;

    /// <summary>
    /// ResolveEffectiveCarrierModulation を解決します。
    /// </summary>
    /// <param name="configuredScheme">configuredScheme を指定します。</param>
    /// <param name="conceptualLeftBin">conceptualLeftBin を指定します。</param>
    /// <returns>処理結果。</returns>
    private static ModulationScheme ResolveEffectiveCarrierModulation(
        ModulationScheme configuredScheme,
        int conceptualLeftBin)
    {
        if (!IsGroupDConceptualLeftBin(conceptualLeftBin))
        {
            return configuredScheme;
        }

        return configuredScheme switch
        {
            ModulationScheme.Qam64 => ModulationScheme.Qam16,
            ModulationScheme.Qam16 => ModulationScheme.Qpsk,
            ModulationScheme.Qpsk => ModulationScheme.Bpsk,
            ModulationScheme.Bpsk => ModulationScheme.Bpsk,
            _ => configuredScheme
        };
    }

    /// <summary>
    /// BitsPerModulation を実行します。
    /// </summary>
    /// <param name="modulationScheme">modulationScheme を指定します。</param>
    /// <returns>処理結果。</returns>
    private static int BitsPerModulation(ModulationScheme modulationScheme) => modulationScheme switch
    {
        ModulationScheme.Bpsk => 1,
        ModulationScheme.Qpsk => 2,
        ModulationScheme.Qam16 => 4,
        ModulationScheme.Qam64 => 6,
        _ => throw new InvalidOperationException("Unsupported modulation scheme.")
    };

    /// <summary>
    /// 内部パラメータです。
    /// </summary>
    private int InterleaveIntervalSymbols =>
        Math.Max(1, _config.FrequencyInterleaveIntervalSymbols);

    /// <summary>
    /// ResolveDataCarrierOrder を解決します。
    /// </summary>
    /// <param name="useRightChannel">useRightChannel を指定します。true で有効です。</param>
    /// <param name="symbolLocalSamplePosition">symbolLocalSamplePosition を指定します。</param>
    /// <param name="interleaveInitSeed">interleaveInitSeed を指定します。</param>
    /// <returns>処理結果。</returns>
    private int[] ResolveDataCarrierOrder(bool useRightChannel, long symbolLocalSamplePosition, int interleaveInitSeed)
    {
        var baseOrder = useRightChannel ? _rightDataCarrierBase : _leftDataCarrierBase;
        var noInterleaveOrder = useRightChannel ? _rightDataCarrierNoInterleaveOrder : _leftDataCarrierNoInterleaveOrder;
        if (!_config.EnableFrequencyInterleaving)
        {
            return noInterleaveOrder;
        }

        var absoluteSymbolPosition = symbolLocalSamplePosition / SamplesPerOfdmSymbol;
        var epoch = absoluteSymbolPosition / InterleaveIntervalSymbols;
        if (baseOrder.Count <= 1)
        {
            return noInterleaveOrder;
        }

        var cache = useRightChannel ? _rightInterleavedOrderCache : _leftInterleavedOrderCache;
        var cacheKey = (epoch, interleaveInitSeed);
        if (cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var order = baseOrder.ToArray();
        var state = CreateMSequenceState(epoch, useRightChannel, interleaveInitSeed);
        var keys = new uint[order.Length];

        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = NextMSequenceWord(ref state);
        }

        Array.Sort(keys, order);

        if (cache.Count >= 4096)
        {
            cache.Clear();
        }

        cache[cacheKey] = order;
        return order;
    }

    /// <summary>
    /// CreateMSequenceState を生成します。
    /// </summary>
    /// <param name="epoch">epoch を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。true で有効です。</param>
    /// <param name="interleaveInitSeed">interleaveInitSeed を指定します。</param>
    /// <returns>処理結果。</returns>
    private uint CreateMSequenceState(long epoch, bool useRightChannel, int interleaveInitSeed)
    {
        ulong x = (uint)(_config.RandomSeed == 0 ? 1 : _config.RandomSeed);
        x ^= (uint)interleaveInitSeed;
        x ^= useRightChannel ? 0xA5A5A5A5u : 0x5A5A5A5Au;
        x ^= unchecked((ulong)epoch * 0x9E3779B97F4A7C15UL);
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        x ^= x >> 31;

        var state = (uint)(x & 0x7FFFFFFF);
        return state == 0 ? 1u : state;
    }

    /// <summary>
    /// NextMSequenceWord を実行します。
    /// </summary>
    /// <param name="state">state を指定します。</param>
    /// <returns>処理結果。</returns>
    private static uint NextMSequenceWord(ref uint state)
    {
        var value = 0u;
        for (var i = 0; i < 31; i++)
        {
            state = AdvanceMSequence31(state);
            value = (value << 1) | (state & 1u);
        }

        return value;
    }

    /// <summary>
    /// AdvanceMSequence31 を実行します。
    /// </summary>
    /// <param name="state">LFSR 迥ｶ諷九・/param>
    /// <returns>処理結果。</returns>
    private static uint AdvanceMSequence31(uint state)
    {
        // Primitive polynomial: x^31 + x^28 + 1
        var feedback = ((state >> 30) ^ (state >> 27)) & 1u;
        state = ((state << 1) & 0x7FFFFFFF) | feedback;
        return state == 0 ? 1u : state;
    }

    /// <summary>
    /// GetPositiveCarrierBins を取得します。
    /// </summary>
    /// <param name="channel">channel を指定します。</param>
    /// <returns>処理結果。</returns>
    private List<int> GetPositiveCarrierBins(CarrierChannel channel)
    {
        var conceptBins = _config.ConceptualLeftBins;
        var bins = new List<int>(conceptBins.Count);
        var used = new HashSet<int>();

        if (_config.CarrierGrid == OfdmCarrierGrid.Sc9Family)
        {
            var useRight = channel == CarrierChannel.Right
                && _config.ChannelMode == ChannelMode.Stereo;
            foreach (var k in conceptBins)
            {
                var hz = useRight
                    ? OfdmConfig.RightCarrierHzSc9(k - 1, _config.SampleRate)
                    : OfdmConfig.LeftCarrierHzSc9(k - 1, _config.SampleRate);
                var bin = OfdmConfig.HzToPositiveBin(hz, _config.FftSize, _config.SampleRate);
                bin = EnsureUniquePositiveBin(bin, used);
                AddPositiveBin(bins, bin);
            }

            return bins;
        }

        if (_config.ChannelMode == ChannelMode.Mono)
        {
            foreach (var k in conceptBins)
            {
                AddPositiveBin(bins, k);
            }

            return bins;
        }

        if (channel == CarrierChannel.Left)
        {
            foreach (var k in conceptBins)
            {
                AddPositiveBin(bins, 2 * k);
            }
        }
        else
        {
            foreach (var k in conceptBins)
            {
                AddPositiveBin(bins, (2 * k) + 1);
            }
        }

        return bins;
    }

    /// <summary>
    /// EnsureUniquePositiveBin を実行します。
    /// </summary>
    /// <param name="preferred">preferred を指定します。</param>
    /// <param name="used">used を指定します。true で有効です。</param>
    /// <returns>処理結果。</returns>
    private int EnsureUniquePositiveBin(int preferred, HashSet<int> used)
    {
        var bin = preferred;
        var max = (_config.FftSize / 2) - 1;
        while (used.Contains(bin))
        {
            bin++;
            if (bin > max)
            {
                bin = preferred - 1;
                while (bin >= 1 && used.Contains(bin))
                {
                    bin--;
                }

                if (bin < 1)
                {
                    throw new InvalidOperationException("Unable to allocate unique positive carrier bin.");
                }

                break;
            }
        }

        used.Add(bin);
        return bin;
    }

    /// <summary>
    /// AddPositiveBin を実行します。
    /// </summary>
    /// <param name="bins">bins を指定します。</param>
    /// <param name="bin">bin を指定します。</param>
    private void AddPositiveBin(List<int> bins, int bin)
    {
        if (bin <= 0 || bin >= _config.FftSize / 2)
        {
            throw new InvalidOperationException(
                $"Carrier bin {bin} is out of positive-frequency range for FFT={_config.FftSize}.");
        }

        bins.Add(bin);
    }

    /// <summary>
    /// ApplyHermitianSymmetry を適用します。
    /// </summary>
    /// <param name="bins">bins を指定します。</param>
    private static void ApplyHermitianSymmetry(Complex[] bins)
    {
        var n = bins.Length;
        bins[0] = new Complex(bins[0].Real, 0.0);
        if ((n & 1) == 0)
        {
            bins[n / 2] = new Complex(bins[n / 2].Real, 0.0);
        }

        for (var k = 1; k < n / 2; k++)
        {
            bins[n - k] = Complex.Conjugate(bins[k]);
        }
    }

    /// <summary>
    /// ToRealTimeSymbol を実行します。
    /// </summary>
    /// <param name="freqBins">freqBins を指定します。</param>
    /// <returns>処理結果。</returns>
    private Complex[] ToRealTimeSymbol(Complex[] freqBins)
    {
        ApplyHermitianSymmetry(freqBins);
        EnsureIfftScratch(freqBins.Length);
        InverseFftInto(freqBins, _ifftWorkScratch!);
        var time = new Complex[freqBins.Length];
        for (var i = 0; i < time.Length; i++)
        {
            time[i] = new Complex(_ifftWorkScratch![i].Real, 0.0);
        }

        return time;
    }

    /// <summary>
    /// EmitRealTimeSymbolWithCp を実行します。
    /// </summary>
    /// <param name="freqBins">freqBins を指定します。</param>
    /// <param name="destinationWithCp">destinationWithCp を指定します。</param>
    private void EmitRealTimeSymbolWithCp(Complex[] freqBins, Span<Complex> destinationWithCp)
    {
        ApplyHermitianSymmetry(freqBins);
        EnsureIfftScratch(freqBins.Length);
        InverseFftInto(freqBins, _ifftWorkScratch!);
        var time = _ifftWorkScratch!;
        for (var i = 0; i < time.Length; i++)
        {
            time[i] = new Complex(time[i].Real, 0.0);
        }

        CopyWithCyclicPrefix(time, _config.CyclicPrefixLength, destinationWithCp);
    }

    /// <summary>
    /// 内部パラメータです。
    /// </summary>
    public int BitsPerModulationSymbol => _config.ModulationScheme switch
    {
        ModulationScheme.Bpsk => 1,
        ModulationScheme.Qpsk => 2,
        ModulationScheme.Qam16 => 4,
        ModulationScheme.Qam64 => 6,
        _ => throw new InvalidOperationException("Unsupported modulation scheme.")
    };

    /// <summary>
    /// 内部パラメータです。
    /// </summary>
    public int BitsPerOfdmSymbol => _bitsPerOfdmSymbol;

    /// <summary>
    /// 内部パラメータです。
    /// </summary>
    public int DataCarrierCount => _leftDataCarrierBase.Count;

    /// <summary>
    /// 内部パラメータです。
    /// </summary>
    public int SamplesPerOfdmSymbol => _config.FftSize + _config.CyclicPrefixLength;

    /// <summary>
    /// GenerateFrame を実行します。
    /// </summary>
    public Complex[] GenerateFrame()
    {
        if (_config.ChannelMode == ChannelMode.Stereo)
        {
            throw new InvalidOperationException("Stereo mode requires GenerateStereoFrame().");
        }

        var symbolLength = SamplesPerOfdmSymbol;
        var frame = new Complex[_config.OfdmSymbolCount * symbolLength];
        var write = 0;

        for (var i = 0; i < _config.OfdmSymbolCount; i++)
        {
            var freqBins = BuildFrequencyDomainSymbol(CarrierChannel.Left);
            EmitRealTimeSymbolWithCp(freqBins, frame.AsSpan(write, symbolLength));
            write += symbolLength;
        }

        return frame;
    }

    /// <summary>
    /// GenerateStereoFrame を実行します。
    /// </summary>
    public (Complex[] Left, Complex[] Right) GenerateStereoFrame()
    {
        if (_config.ChannelMode != ChannelMode.Stereo)
        {
            throw new InvalidOperationException("GenerateStereoFrame() requires stereo mode.");
        }

        var symbolLength = SamplesPerOfdmSymbol;
        var frameLength = _config.OfdmSymbolCount * symbolLength;
        var left = new Complex[frameLength];
        var right = new Complex[frameLength];
        var write = 0;

        for (var i = 0; i < _config.OfdmSymbolCount; i++)
        {
            var leftBins = BuildFrequencyDomainSymbol(CarrierChannel.Left);
            var rightBins = BuildFrequencyDomainSymbol(CarrierChannel.Right);

            EmitRealTimeSymbolWithCp(leftBins, left.AsSpan(write, symbolLength));
            EmitRealTimeSymbolWithCp(rightBins, right.AsSpan(write, symbolLength));
            write += symbolLength;
        }

        return (left, right);
    }

    /// <summary>
    /// GenerateUnmodulated を実行します。
    /// </summary>
    public (Complex[] Left, Complex[] Right) GenerateUnmodulated(int sampleCount)
    {
        if (sampleCount <= 0)
        {
            throw new ArgumentException("Sample count must be > 0.", nameof(sampleCount));
        }

        var left = GenerateUnmodulatedChannel(sampleCount, _leftAllCarrierBins);
        if (_config.ChannelMode == ChannelMode.Mono)
        {
            return (left, Array.Empty<Complex>());
        }

        var right = GenerateUnmodulatedChannel(sampleCount, _rightAllCarrierBins);
        return (left, right);
    }

    /// <summary>
    /// GenerateUnmodulatedChannel を実行します。
    /// </summary>
    /// <param name="sampleCount">sampleCount を指定します。</param>
    /// <param name="carrierBins">carrierBins を指定します。</param>
    /// <returns>処理結果。</returns>
    private Complex[] GenerateUnmodulatedChannel(int sampleCount, List<int> carrierBins)
    {
        var symbolLength = SamplesPerOfdmSymbol;
        var symbol = GetOrBuildUnmodulatedSymbol(carrierBins);
        var symbolCount = (sampleCount + symbolLength - 1) / symbolLength;
        var padded = new Complex[symbolCount * symbolLength];
        for (var i = 0; i < symbolCount; i++)
        {
            Array.Copy(symbol, 0, padded, i * symbolLength, symbolLength);
        }

        if (padded.Length > sampleCount)
        {
            var trimmed = new Complex[sampleCount];
            Array.Copy(padded, trimmed, sampleCount);
            return trimmed;
        }

        return padded;
    }

    /// <summary>
    /// GetOrBuildUnmodulatedSymbol を取得します。
    /// </summary>
    /// <param name="carrierBins">carrierBins を指定します。</param>
    /// <returns>処理結果。</returns>
    private Complex[] GetOrBuildUnmodulatedSymbol(List<int> carrierBins)
    {
        if (ReferenceEquals(carrierBins, _leftAllCarrierBins))
        {
            return _unmodulatedLeftSymbol ??= BuildUnmodulatedSymbol(carrierBins);
        }

        if (ReferenceEquals(carrierBins, _rightAllCarrierBins))
        {
            return _unmodulatedRightSymbol ??= BuildUnmodulatedSymbol(carrierBins);
        }

        return BuildUnmodulatedSymbol(carrierBins);
    }

    /// <summary>
    /// BuildUnmodulatedSymbol を構築します。
    /// </summary>
    /// <param name="carrierBins">carrierBins を指定します。</param>
    /// <returns>処理結果。</returns>
    private Complex[] BuildUnmodulatedSymbol(List<int> carrierBins)
    {
        var symbolLength = SamplesPerOfdmSymbol;
        var symbol = new Complex[symbolLength];
        var freqBins = new Complex[_config.FftSize];
        foreach (var carrierBin in carrierBins)
        {
            freqBins[carrierBin] = UnmodulatedCarrierSymbol;
        }

        EmitRealTimeSymbolWithCp(freqBins, symbol);
        return symbol;
    }

    /// <summary>
    /// ModulateBits を実行します。
    /// </summary>
    public (Complex[] Left, Complex[] Right) ModulateBits(ReadOnlySpan<bool> bits, long absoluteSampleOffset = 0, int interleaveInitSeed = 0)
    {
        return ModulateBitStreams(bits, bits, absoluteSampleOffset, interleaveInitSeed);
    }

    /// <summary>
    /// ModulateBitStreams を実行します。
    /// </summary>
    public (Complex[] Left, Complex[] Right) ModulateBitStreams(
        ReadOnlySpan<bool> leftBits,
        ReadOnlySpan<bool> rightBits,
        long absoluteSampleOffset = 0,
        int interleaveInitSeed = 0)
    {
        if (BitsPerOfdmSymbol <= 0)
        {
            throw new InvalidOperationException("No data carriers available for modulation.");
        }

        var left = ModulateBitsOnChannel(leftBits, useRightChannel: false, absoluteSampleOffset, interleaveInitSeed);
        if (_config.ChannelMode == ChannelMode.Mono)
        {
            return (left, Array.Empty<Complex>());
        }

        var right = ModulateBitsOnChannel(rightBits, useRightChannel: true, absoluteSampleOffset, interleaveInitSeed);
        if (left.Length != right.Length)
        {
            throw new InvalidOperationException(
                $"L/R modulated length mismatch: L={left.Length}, R={right.Length}.");
        }

        return (left, right);
    }

    /// <summary>
    /// ModulateBitsOnChannel を実行します。
    /// </summary>
    /// <param name="bits">bits を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。true で有効です。</param>
    /// <param name="absoluteSampleOffset">absoluteSampleOffset を指定します。</param>
    /// <param name="interleaveInitSeed">interleaveInitSeed を指定します。</param>
    /// <returns>処理結果。</returns>
    private Complex[] ModulateBitsOnChannel(
        ReadOnlySpan<bool> bits,
        bool useRightChannel,
        long absoluteSampleOffset,
        int interleaveInitSeed)
    {
        var pilotBins = useRightChannel ? _rightPilotBins : _leftPilotBins;
        var dataModulationByBin = useRightChannel
            ? _rightDataCarrierModulationByBin
            : _leftDataCarrierModulationByBin;
        var symbolCount = (bits.Length + BitsPerOfdmSymbol - 1) / BitsPerOfdmSymbol;
        if (symbolCount == 0)
        {
            symbolCount = 1;
        }

        var symbolLength = SamplesPerOfdmSymbol;
        var samples = new Complex[symbolCount * symbolLength];
        var write = 0;
        var bitIndex = 0;
        var freqBins = new Complex[_config.FftSize];

        for (var s = 0; s < symbolCount; s++)
        {
            _ = absoluteSampleOffset;
            var symbolOffset = (long)s * SamplesPerOfdmSymbol;
            var dataCarrierOrder = ResolveDataCarrierOrder(useRightChannel, symbolOffset, interleaveInitSeed);

            Array.Clear(freqBins);
            foreach (var pilotBin in pilotBins)
            {
                freqBins[pilotBin] = PilotSymbol;
            }

            foreach (var dataBin in dataCarrierOrder)
            {
                freqBins[dataBin] = ConsumeModulatedSymbol(
                    dataModulationByBin[dataBin],
                    ref bitIndex,
                    bits);
            }

            EmitRealTimeSymbolWithCp(freqBins, samples.AsSpan(write, symbolLength));
            write += symbolLength;
        }

        return samples;
    }

    /// <summary>
    /// CopyWithCyclicPrefix を実行します。
    /// </summary>
    /// <param name="destination">CP 莉倥″蜃ｺ蜉帛・縲・/param>
    /// <param name="symbol">symbol を指定します。</param>
    /// <param name="cpLength">cpLength を指定します。</param>
    private static void CopyWithCyclicPrefix(ReadOnlySpan<Complex> symbol, int cpLength, Span<Complex> destination)
    {
        if (destination.Length < symbol.Length + cpLength)
        {
            throw new ArgumentException("Destination span is shorter than symbol + cyclic prefix.", nameof(destination));
        }

        if (cpLength == 0)
        {
            symbol.CopyTo(destination);
            return;
        }

        symbol.Slice(symbol.Length - cpLength, cpLength).CopyTo(destination);
        symbol.CopyTo(destination.Slice(cpLength));
    }

    /// <summary>
    /// SampleCountForBitCount を実行します。
    /// </summary>
    public int SampleCountForBitCount(int bitCount)
    {
        if (bitCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        }

        var symbolCount = bitCount == 0
            ? 1
            : (bitCount + BitsPerOfdmSymbol - 1) / BitsPerOfdmSymbol;
        return symbolCount * SamplesPerOfdmSymbol;
    }

    /// <summary>
    /// ScoreWowParamsForDiagnostics を実行します。
    /// </summary>
    public double ScoreWowParamsForDiagnostics(
        Complex[] samples,
        bool useRightChannel,
        int analysisStartSample,
        int analysisSampleCount,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        analysisStartSample = Math.Clamp(analysisStartSample, 0, Math.Max(0, samples.Length - SamplesPerOfdmSymbol));
        analysisSampleCount = Math.Clamp(analysisSampleCount, SamplesPerOfdmSymbol, samples.Length - analysisStartSample);
        var carrierBins = useRightChannel ? _rightAllCarrierBins : _leftAllCarrierBins;
        var ideal = GenerateUnmodulatedChannel(analysisSampleCount, carrierBins);
        var corrected = new Complex[analysisSampleCount];
        ResampleSegmentWithInverseSpeed(
            samples,
            analysisStartSample,
            analysisSampleCount,
            Math.Max(1, _config.SampleRate),
            amount,
            wowPhase,
            flutterPhase,
            corrected);
        return CorrelateReal(corrected, ideal);
    }

    /// <summary>
    /// 内部処理です。
    /// </summary>
    public (double Baseline, double BestScore, double Amount, double WowPhase, double FlutterPhase)?
        MatchWowParametersForDiagnostics(
            Complex[] samples,
            bool useRightChannel,
            int analysisStartSample,
            int analysisSampleCount)
    {
        return MatchWowByPreambleCorrelationParams(
            samples, useRightChannel, analysisStartSample, analysisSampleCount);
    }

    /// <summary>
    /// 内部処理です。
    /// </summary>
    public (double Baseline, double BestScore, double Amount, double WowPhase, double FlutterPhase)?
        RefineWowParametersNearHintForDiagnostics(
            Complex[] samples,
            bool useRightChannel,
            int analysisStartSample,
            int analysisSampleCount,
            double hintAmount,
            double hintWowPhase,
            double hintFlutterPhase,
            double phaseRangeRad = 0.3141592653589793,
            double amountRange = 0.002)
    {
        analysisStartSample = Math.Clamp(analysisStartSample, 0, Math.Max(0, samples.Length - SamplesPerOfdmSymbol));
        analysisSampleCount = Math.Clamp(analysisSampleCount, SamplesPerOfdmSymbol, samples.Length - analysisStartSample);
        if (analysisSampleCount < SamplesPerOfdmSymbol * 8)
        {
            return null;
        }

        var carrierBins = useRightChannel ? _rightAllCarrierBins : _leftAllCarrierBins;
        var ideal = GenerateUnmodulatedChannel(analysisSampleCount, carrierBins);
        var sampleRate = Math.Max(1, _config.SampleRate);
        var correctedPreambleBuffer = new Complex[analysisSampleCount];
        var refStride1 = BuildCorrelationReference(ideal, 1);
        var refStride8 = BuildCorrelationReference(ideal, 8);

        double Evaluate(double amount, double wowPhase, double flutterPhase, int corrStride)
        {
            ResampleSegmentWithInverseSpeed(
                samples,
                analysisStartSample,
                analysisSampleCount,
                sampleRate,
                amount,
                wowPhase,
                flutterPhase,
                correctedPreambleBuffer,
                corrStride);
            var reference = corrStride <= 1 ? refStride1 : refStride8;
            return CorrelateRealStridedWithReference(
                correctedPreambleBuffer,
                ideal,
                corrStride,
                reference.Mean,
                reference.Energy,
                reference.Count);
        }

        var baseline = Evaluate(hintAmount, hintWowPhase, hintFlutterPhase, corrStride: 1);
        var best = baseline;
        var bestAmount = hintAmount;
        var bestWow = hintWowPhase;
        var bestFlutter = hintFlutterPhase;

        var phaseStep = Math.Max(0.01, phaseRangeRad / 6.0);
        var amountStep = Math.Max(0.0005, amountRange / 2.0);
        const int coarseStride = 8;

        var coarseHits = new List<(double Score, double Amount, double Wow, double Flutter)>(64);
        for (var wow = hintWowPhase - phaseRangeRad; wow <= hintWowPhase + phaseRangeRad; wow += phaseStep)
        {
            for (var flutter = hintFlutterPhase - phaseRangeRad; flutter <= hintFlutterPhase + phaseRangeRad; flutter += phaseStep)
            {
                for (var amount = Math.Max(0.001, hintAmount - amountRange); amount <= hintAmount + amountRange; amount += amountStep)
                {
                    var score = Evaluate(amount, wow, flutter, coarseStride);
                    if (score > baseline)
                    {
                        coarseHits.Add((score, amount, wow, flutter));
                    }
                }
            }
        }

        if (coarseHits.Count == 0)
        {
            return null;
        }

        coarseHits.Sort((a, b) => b.Score.CompareTo(a.Score));
        var polishCount = Math.Min(8, coarseHits.Count);
        for (var i = 0; i < polishCount; i++)
        {
            var hit = coarseHits[i];
            var score = Evaluate(hit.Amount, hit.Wow, hit.Flutter, corrStride: 1);
            if (score > best)
            {
                best = score;
                bestAmount = hit.Amount;
                bestWow = hit.Wow;
                bestFlutter = hit.Flutter;
            }
        }

        if (best <= baseline)
        {
            return null;
        }

        return (baseline, best, bestAmount, WrapPhase(bestWow), WrapPhase(bestFlutter));
    }

    /// <summary>
    /// CorrectWowFlutter を実行します。
    /// </summary>
    public Complex[] CorrectWowFlutter(
        Complex[] samples,
        bool useRightChannel = false,
        int analysisStartSample = 0,
        int analysisSampleCount = -1,
        int passes = 1)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (passes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(passes));
        }

        if (useRightChannel && _config.ChannelMode != ChannelMode.Stereo)
        {
            throw new InvalidOperationException("Right channel wow correction requires stereo mode.");
        }

        if (samples.Length < SamplesPerOfdmSymbol)
        {
            return samples;
        }

        if (analysisSampleCount < 0)
        {
            analysisSampleCount = samples.Length - analysisStartSample;
        }

        analysisStartSample = Math.Clamp(analysisStartSample, 0, Math.Max(0, samples.Length - SamplesPerOfdmSymbol));
        analysisSampleCount = Math.Clamp(analysisSampleCount, SamplesPerOfdmSymbol, samples.Length - analysisStartSample);

        var current = samples;
        for (var pass = 0; pass < passes; pass++)
        {
            var matched = MatchWowByPreambleCorrelationParams(
                current, useRightChannel, analysisStartSample, analysisSampleCount);
            if (matched is null)
            {
                return current;
            }

            current = CorrectWowFlutterWithParams(
                current,
                matched.Value.Amount,
                matched.Value.WowPhase,
                matched.Value.FlutterPhase);
        }

        return current;
    }

    /// <summary>
    /// 既知のワウ・フラッターパラメータで補正した新規配列を返します。
    /// </summary>
    /// <param name="samples">補正対象サンプル。</param>
    /// <param name="amount">補正量。</param>
    /// <param name="wowPhase">wow 位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
    /// <returns>補正後サンプル配列。</returns>
    public Complex[] CorrectWowFlutterWithParams(
        Complex[] samples,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length < SamplesPerOfdmSymbol || amount == 0.0)
        {
            return samples;
        }

        return WowFlutterWarp.Correct(
            samples,
            Math.Max(1, _config.SampleRate),
            amount,
            wowPhase,
            flutterPhase);
    }

    /// <summary>
    /// 既知のワウ・フラッターパラメータでインプレース補正します。
    /// </summary>
    /// <param name="samples">補正対象サンプル。</param>
    /// <param name="amount">補正量。</param>
    /// <param name="wowPhase">wow 位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
    public void CorrectWowFlutterWithParamsInPlace(
        Complex[] samples,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        CorrectWowFlutterWithParamsInPlace(samples, samples.Length, amount, wowPhase, flutterPhase);
    }

    /// <summary>
    /// 既知のワウ・フラッターパラメータで指定長のみインプレース補正します。
    /// </summary>
    /// <param name="samples">補正対象サンプル。</param>
    /// <param name="length">補正対象長。</param>
    /// <param name="amount">補正量。</param>
    /// <param name="wowPhase">wow 位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
    public void CorrectWowFlutterWithParamsInPlace(
        Complex[] samples,
        int length,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (length < SamplesPerOfdmSymbol || amount == 0.0)
        {
            return;
        }

        WowFlutterWarp.CorrectInPlace(
            samples,
            length,
            Math.Max(1, _config.SampleRate),
            amount,
            wowPhase,
            flutterPhase);
    }

    /// <summary>
    /// MatchWowByPreambleCorrelation を実行します。
    /// </summary>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。true で有効です。</param>
    /// <param name="analysisStartSample">analysisStartSample を指定します。</param>
    /// <param name="analysisSampleCount">analysisSampleCount を指定します。</param>
    /// <returns>処理結果。</returns>
    private double[]? MatchWowByPreambleCorrelation(
        Complex[] samples,
        bool useRightChannel,
        int analysisStartSample,
        int analysisSampleCount)
    {
        var matched = MatchWowByPreambleCorrelationParams(
            samples, useRightChannel, analysisStartSample, analysisSampleCount);
        if (matched is null)
        {
            return null;
        }

        var sampleRate = Math.Max(1, _config.SampleRate);
        return BuildCassetteSpeedProfilePerSample(
            samples.Length,
            sampleRate,
            matched.Value.Amount,
            matched.Value.WowPhase,
            matched.Value.FlutterPhase);
    }

    /// <summary>
    /// 内部処理です。
    /// </summary>
    private (double Baseline, double BestScore, double Amount, double WowPhase, double FlutterPhase)?
        MatchWowByPreambleCorrelationParams(
            Complex[] samples,
            bool useRightChannel,
            int analysisStartSample,
            int analysisSampleCount)
    {
        if (analysisSampleCount < SamplesPerOfdmSymbol * 16)
        {
            return null;
        }

        analysisStartSample = Math.Clamp(analysisStartSample, 0, Math.Max(0, samples.Length - SamplesPerOfdmSymbol));
        analysisSampleCount = Math.Clamp(analysisSampleCount, SamplesPerOfdmSymbol, samples.Length - analysisStartSample);

        var carrierBins = useRightChannel ? _rightAllCarrierBins : _leftAllCarrierBins;
        var ideal = GenerateUnmodulatedChannel(analysisSampleCount, carrierBins);
        var observed = new Complex[analysisSampleCount];
        Array.Copy(samples, analysisStartSample, observed, 0, analysisSampleCount);

        var sampleRate = Math.Max(1, _config.SampleRate);
        var baseline = CorrelateReal(observed, ideal);
        var bestScore = baseline;
        var bestAmount = 0.01;
        var bestWowPhase = 0.0;
        var bestFlutterPhase = 0.0;
        var correctedPreambleBuffer = new Complex[analysisSampleCount];

        var refStride1 = BuildCorrelationReference(ideal, 1);
        var refStride8 = BuildCorrelationReference(ideal, 8);
        var refStride32 = BuildCorrelationReference(ideal, 32);

        double Evaluate(double amount, double wowPhase, double flutterPhase, int corrStride = 1)
        {
            ResampleSegmentWithInverseSpeed(
                samples,
                analysisStartSample,
                analysisSampleCount,
                sampleRate,
                amount,
                wowPhase,
                flutterPhase,
                correctedPreambleBuffer,
                corrStride);
            var reference = corrStride <= 1
                ? refStride1
                : corrStride <= 8
                    ? refStride8
                    : refStride32;
            var score = CorrelateRealStridedWithReference(
                correctedPreambleBuffer,
                ideal,
                corrStride,
                reference.Mean,
                reference.Energy,
                reference.Count);
            if (corrStride <= 1 && score > bestScore)
            {
                bestScore = score;
                bestAmount = amount;
                bestWowPhase = wowPhase;
                bestFlutterPhase = flutterPhase;
            }

            return score;
        }

        const int coarseStride = 32;
        const double earlyExitScore = 0.97;
        var coarseHits = new List<(double Score, double Wow, double Flutter)>(64);
        for (var wi = 0; wi < 18; wi++)
        {
            var wowPhase = wi * Math.PI / 9.0;
            for (var fi = 0; fi < 18; fi++)
            {
                var flutterPhase = fi * Math.PI / 9.0;
                var score = Evaluate(0.01, wowPhase, flutterPhase, coarseStride);
                if (score > baseline + 0.02)
                {
                    coarseHits.Add((score, wowPhase, flutterPhase));
                }
            }
        }

        if (coarseHits.Count == 0)
        {
            return null;
        }

        coarseHits.Sort((a, b) => b.Score.CompareTo(a.Score));
        var expandCount = Math.Min(12, coarseHits.Count);
        for (var i = 0; i < expandCount; i++)
        {
            var center = coarseHits[i];
            for (var dW = -1; dW <= 1; dW++)
            {
                for (var dF = -1; dF <= 1; dF++)
                {
                    if (dW == 0 && dF == 0)
                    {
                        continue;
                    }

                    var wowPhase = WrapPhase(center.Wow + (dW * Math.PI / 18.0));
                    var flutterPhase = WrapPhase(center.Flutter + (dF * Math.PI / 18.0));
                    var score = Evaluate(0.01, wowPhase, flutterPhase, coarseStride);
                    if (score > baseline + 0.02)
                    {
                        coarseHits.Add((score, wowPhase, flutterPhase));
                    }
                }
            }
        }

        coarseHits.Sort((a, b) => b.Score.CompareTo(a.Score));
        var seeds = new List<(double Wow, double Flutter)>(4);
        for (var i = 0; i < coarseHits.Count && seeds.Count < 3; i++)
        {
            var candidate = (coarseHits[i].Wow, coarseHits[i].Flutter);
            var far = true;
            foreach (var existing in seeds)
            {
                if (Math.Abs(WrapPhase(candidate.Wow - existing.Wow)) < 0.25 &&
                    Math.Abs(WrapPhase(candidate.Flutter - existing.Flutter)) < 0.25)
                {
                    far = false;
                    break;
                }
            }

            if (far)
            {
                seeds.Add(candidate);
            }
        }

        foreach (var seed in seeds)
        {
            var wow = seed.Wow;
            var flutter = seed.Flutter;
            for (var pass = 0; pass < 2; pass++)
            {
                var bestLocal = double.NegativeInfinity;
                var bestFlutter = flutter;
                for (var fi = 0; fi < 180; fi++)
                {
                    var trial = fi * Math.PI / 90.0;
                    var score = Evaluate(0.01, wow, trial, corrStride: 8);
                    if (score > bestLocal)
                    {
                        bestLocal = score;
                        bestFlutter = trial;
                    }
                }

                flutter = bestFlutter;

                bestLocal = double.NegativeInfinity;
                bestFlutter = flutter;
                for (var dF = -2; dF <= 2; dF++)
                {
                    var trial = WrapPhase(flutter + (dF * Math.PI / 180.0));
                    var score = Evaluate(0.01, wow, trial, corrStride: 8);
                    if (score > bestLocal)
                    {
                        bestLocal = score;
                        bestFlutter = trial;
                    }
                }

                flutter = bestFlutter;

                bestLocal = double.NegativeInfinity;
                var bestWow = wow;
                for (var wi = 0; wi < 180; wi++)
                {
                    var trial = wi * Math.PI / 90.0;
                    var score = Evaluate(0.01, trial, flutter, corrStride: 8);
                    if (score > bestLocal)
                    {
                        bestLocal = score;
                        bestWow = trial;
                    }
                }

                wow = bestWow;

                bestLocal = double.NegativeInfinity;
                bestWow = wow;
                for (var dW = -2; dW <= 2; dW++)
                {
                    var trial = WrapPhase(wow + (dW * Math.PI / 180.0));
                    var score = Evaluate(0.01, trial, flutter, corrStride: 8);
                    if (score > bestLocal)
                    {
                        bestLocal = score;
                        bestWow = trial;
                    }
                }

                wow = bestWow;
            }

            Evaluate(0.01, wow, flutter, corrStride: 1);

            for (var dW = -5; dW <= 5; dW++)
            {
                for (var dF = -5; dF <= 5; dF++)
                {
                    Evaluate(
                        0.01,
                        wow + (dW * Math.PI / 1200.0),
                        flutter + (dF * Math.PI / 1200.0),
                        corrStride: 1);
                }
            }

            if (bestScore >= earlyExitScore)
            {
                break;
            }
        }

        if (bestScore <= baseline)
        {
            return null;
        }

        foreach (var amount in new[] { 0.008, 0.009, 0.01, 0.011, 0.012 })
        {
            Evaluate(amount, bestWowPhase, bestFlutterPhase, corrStride: 1);
        }

        var fineWow = bestWowPhase;
        var fineFlutter = bestFlutterPhase;
        for (var dF = -20; dF <= 20; dF++)
        {
            Evaluate(bestAmount, fineWow, fineFlutter + (dF * Math.PI / 4000.0), corrStride: 1);
        }

        fineFlutter = bestFlutterPhase;
        for (var dW = -20; dW <= 20; dW++)
        {
            Evaluate(bestAmount, fineWow + (dW * Math.PI / 4000.0), fineFlutter, corrStride: 1);
        }

        if (bestScore < baseline + 0.02 || bestScore < 0.85)
        {
            return null;
        }

        return (baseline, bestScore, bestAmount, bestWowPhase, bestFlutterPhase);
    }

    /// <summary>
    /// WrapPhase を実行します。
    /// </summary>
    /// <param name="phase">謚倥ｊ霑斐☆菴咲嶌 (rad)縲・/param>
    /// <returns>処理結果。</returns>
    private static double WrapPhase(double phase)
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

    /// <summary>
    /// BuildCorrelationReference を構築します。
    /// </summary>
    /// <param name="reference">reference縲・/param>
    private static (double Mean, double Energy, int Count) BuildCorrelationReference(Complex[] reference, int stride)
    {
        stride = Math.Max(1, stride);
        var sum = 0.0;
        var count = 0;
        for (var i = 0; i < reference.Length; i += stride)
        {
            sum += reference[i].Real;
            count++;
        }

        if (count <= 1)
        {
            return (0.0, 0.0, count);
        }

        var mean = sum / count;
        var energy = 0.0;
        for (var i = 0; i < reference.Length; i += stride)
        {
            var centered = reference[i].Real - mean;
            energy += centered * centered;
        }

        return (mean, energy, count);
    }

    /// <summary>
    /// 実数成分の相関係数をストライド付きで計算します。
    /// </summary>
    /// <param name="a">比較元系列。</param>
    /// <param name="b">比較先系列。</param>
    /// <param name="stride">サンプル間引き間隔。</param>
    /// <param name="meanB">系列 b の平均値。</param>
    /// <param name="energyB">系列 b の分散エネルギー。</param>
    /// <param name="count">比較サンプル数。</param>
    /// <returns>正規化相関係数。</returns>
    private static double CorrelateRealStridedWithReference(
        ReadOnlySpan<Complex> a,
        ReadOnlySpan<Complex> b,
        int stride,
        double meanB,
        double energyB,
        int count)
    {
        if (count <= 1 || energyB <= 1e-18)
        {
            return double.NegativeInfinity;
        }

        stride = Math.Max(1, stride);
        var sumA = 0.0;
        for (var i = 0; i < a.Length; i += stride)
        {
            sumA += a[i].Real;
        }

        var meanA = sumA / count;
        var num = 0.0;
        var energyA = 0.0;
        for (var i = 0; i < a.Length; i += stride)
        {
            var xa = a[i].Real - meanA;
            var xb = b[i].Real - meanB;
            num += xa * xb;
            energyA += xa * xa;
        }

        return num / Math.Sqrt((energyA * energyB) + 1e-18);
    }

    /// <summary>
    /// BuildCassetteSpeedProfilePerSample を構築します。
    /// </summary>
    /// <param name="sampleCount">sampleCount を指定します。</param>
    /// <param name="sampleRate">sampleRate を指定します。</param>
    /// <param name="amount">amount を指定します。</param>
    /// <param name="wowPhase">wowPhase を指定します。</param>
    /// <param name="flutterPhase">flutterPhase を指定します。</param>
    /// <returns>処理結果。</returns>
    private static double[] BuildCassetteSpeedProfilePerSample(
        int sampleCount,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        const double wowHz = 0.5;
        const double flutterHz = 6.0;
        var profile = new double[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)sampleRate;
            var modulation =
                (0.65 * Math.Sin((2.0 * Math.PI * wowHz * t) + wowPhase)) +
                (0.35 * Math.Sin((2.0 * Math.PI * flutterHz * t) + flutterPhase));
            var speed = 1.0 + (amount * modulation);
            profile[i] = speed < 0.05 ? 0.05 : speed;
        }

        return profile;
    }

    /// <summary>
    /// BuildCassetteSpeedProfile を構築します。
    /// </summary>
    /// <param name="symbolCount">symbolCount を指定します。</param>
    /// <param name="firstSymbol">firstSymbol を指定します。</param>
    /// <param name="symbolLength">symbolLength を指定します。</param>
    /// <param name="sampleRate">sampleRate を指定します。</param>
    /// <param name="amount">amount を指定します。</param>
    /// <param name="wowPhase">wowPhase を指定します。</param>
    /// <param name="flutterPhase">flutterPhase を指定します。</param>
    /// <returns>処理結果。</returns>
    private static double[] BuildCassetteSpeedProfile(
        int symbolCount,
        int firstSymbol,
        int symbolLength,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        const double wowHz = 0.5;
        const double flutterHz = 6.0;
        var profile = new double[symbolCount];
        for (var s = 0; s < symbolCount; s++)
        {
            var t = ((firstSymbol + s) * symbolLength) / (double)sampleRate;
            var modulation =
                (0.65 * Math.Sin((2.0 * Math.PI * wowHz * t) + wowPhase)) +
                (0.35 * Math.Sin((2.0 * Math.PI * flutterHz * t) + flutterPhase));
            profile[s] = 1.0 + (amount * modulation);
        }

        NormalizeSpeedProfileMean(profile);
        return profile;
    }

    /// <summary>
    /// CorrelateReal を実行します。
    /// </summary>
    /// <param name="a">a を指定します。</param>
    /// <param name="b">b を指定します。</param>
    /// <returns>処理結果。</returns>
    private static double CorrelateReal(Complex[] a, Complex[] b)
    {
        return CorrelateRealStrided(a, b, 1);
    }

    /// <summary>
    /// CorrelateRealStrided を実行します。
    /// </summary>
    /// <param name="a">a を指定します。</param>
    /// <param name="b">b を指定します。</param>
    /// <param name="stride">stride を指定します。</param>
    /// <returns>処理結果。</returns>
    private static double CorrelateRealStrided(Complex[] a, Complex[] b, int stride)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n <= 0)
        {
            return double.NegativeInfinity;
        }

        stride = Math.Max(1, stride);
        var meanA = 0.0;
        var meanB = 0.0;
        var count = 0;
        for (var i = 0; i < n; i += stride)
        {
            meanA += a[i].Real;
            meanB += b[i].Real;
            count++;
        }

        if (count <= 1)
        {
            return double.NegativeInfinity;
        }

        meanA /= count;
        meanB /= count;
        var num = 0.0;
        var da = 0.0;
        var db = 0.0;
        for (var i = 0; i < n; i += stride)
        {
            var xa = a[i].Real - meanA;
            var xb = b[i].Real - meanB;
            num += xa * xb;
            da += xa * xa;
            db += xb * xb;
        }

        return num / Math.Sqrt((da * db) + 1e-18);
    }

    /// <summary>
    /// 診断用に逆速度リサンプルを適用します。
    /// </summary>
    /// <param name="samples">入力サンプル列。</param>
    /// <param name="speedProfile">速度プロファイル。</param>
    /// <returns>補正後サンプル列。</returns>
    public Complex[] ResampleWithInverseSpeedForDiagnostics(Complex[] samples, double[] speedProfile) =>
        ResampleWithInverseSpeed(samples, speedProfile);

    /// <summary>
    /// プリアンブル一致度を診断用途で評価します。
    /// </summary>
    /// <param name="samples">入力サンプル列。</param>
    /// <param name="analysisStartSample">評価開始サンプル位置。</param>
    /// <param name="analysisSampleCount">評価サンプル数。</param>
    /// <param name="useRightChannel">右チャネルで評価する場合 true。</param>
    /// <returns>一致スコア。</returns>
    public double ScorePreambleMatchForDiagnostics(
        Complex[] samples,
        int analysisStartSample,
        int analysisSampleCount,
        bool useRightChannel = false)
    {
        analysisStartSample = Math.Clamp(analysisStartSample, 0, Math.Max(0, samples.Length - SamplesPerOfdmSymbol));
        analysisSampleCount = Math.Clamp(analysisSampleCount, SamplesPerOfdmSymbol, samples.Length - analysisStartSample);
        var carrierBins = useRightChannel ? _rightAllCarrierBins : _leftAllCarrierBins;
        var ideal = GenerateUnmodulatedChannel(analysisSampleCount, carrierBins);
        var observed = new Complex[analysisSampleCount];
        Array.Copy(samples, analysisStartSample, observed, 0, analysisSampleCount);
        return CorrelateReal(observed, ideal);
    }

    /// <summary>
    /// 診断用に wow 速度プロファイルを推定します。
    /// </summary>
    /// <param name="samples">入力サンプル列。</param>
    /// <param name="useRightChannel">右チャネルを使う場合 true。</param>
    /// <param name="analysisStartSample">解析開始位置。</param>
    /// <param name="analysisSampleCount">解析サンプル数。</param>
    /// <returns>推定速度プロファイル。</returns>
    public double[] EstimateWowSpeedProfileForDiagnostics(
        Complex[] samples,
        bool useRightChannel,
        int analysisStartSample,
        int analysisSampleCount)
    {
        return EstimateSpeedProfile(
            samples,
            useRightChannel,
            analysisStartSample,
            analysisSampleCount,
            allowSymbolOffsetSearch: false,
            requireStrongCpImprovement: false);
    }

    /// <summary>
    /// ExtrapolateSpeedProfile を実行します。
    /// </summary>
    /// <param name="preambleSpeeds">preambleSpeeds を指定します。</param>
    /// <param name="sampleCount">sampleCount を指定します。</param>
    /// <param name="analysisStartSample">analysisStartSample を指定します。</param>
    /// <returns>処理結果。</returns>
    private double[] ExtrapolateSpeedProfile(double[] preambleSpeeds, int sampleCount, int analysisStartSample)
    {
        var symbolLength = Math.Max(1, SamplesPerOfdmSymbol);
        var symbolCount = Math.Max(1, (sampleCount + symbolLength - 1) / symbolLength);
        var full = new double[symbolCount];
        if (preambleSpeeds.Length == 0)
        {
            Array.Fill(full, 1.0);
            return full;
        }

        var period = preambleSpeeds.Length;
        var firstSymbol = Math.Max(0, analysisStartSample / symbolLength);
        for (var i = 0; i < full.Length; i++)
        {
            var rel = i - firstSymbol;
            var mod = ((rel % period) + period) % period;
            full[i] = preambleSpeeds[mod];
        }

        return full;
    }

    /// <summary>
    /// NeedsWowCorrection を実行します。
    /// </summary>
    /// <param name="speedProfile">speedProfile を指定します。</param>
    /// <returns>条件を満たす場合 true、それ以外は false。</returns>
    private static bool NeedsWowCorrection(double[] speedProfile)
    {
        if (speedProfile.Length < 32)
        {
            return false;
        }

        var maxDeviation = 0.0;
        var sumAbs = 0.0;
        var above = 0;
        var below = 0;
        for (var i = 0; i < speedProfile.Length; i++)
        {
            var deviation = speedProfile[i] - 1.0;
            maxDeviation = Math.Max(maxDeviation, Math.Abs(deviation));
            sumAbs += Math.Abs(deviation);
            if (deviation > 0.001)
            {
                above++;
            }
            else if (deviation < -0.001)
            {
                below++;
            }
        }

        var meanAbs = sumAbs / speedProfile.Length;
        return maxDeviation >= 0.002
            && meanAbs >= 0.0007
            && above >= 8
            && below >= 8;
    }

    /// <summary>
    /// EstimateSpeedProfile を実行します。
    /// </summary>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。true で有効です。</param>
    /// <param name="analysisStartSample">analysisStartSample を指定します。</param>
    /// <param name="analysisSampleCount">analysisSampleCount を指定します。</param>
    /// <param name="allowSymbolOffsetSearch">allowSymbolOffsetSearch を指定します。true で有効です。</param>
    /// <param name="requireStrongCpImprovement">requireStrongCpImprovement を指定します。</param>
    /// <returns>処理結果。</returns>
    private double[] EstimateSpeedProfile(
        Complex[] samples,
        bool useRightChannel,
        int analysisStartSample,
        int analysisSampleCount,
        bool allowSymbolOffsetSearch,
        bool requireStrongCpImprovement)
    {
        var symbolLength = SamplesPerOfdmSymbol;
        var analysisEnd = Math.Min(samples.Length, analysisStartSample + analysisSampleCount);
        var firstSymbol = analysisStartSample / symbolLength;
        var lastSymbolExclusive = analysisEnd / symbolLength;
        if (lastSymbolExclusive <= firstSymbol)
        {
            return [1.0];
        }

        var pilotBins = useRightChannel ? _rightPilotBins : _leftPilotBins;
        var carrierBins = useRightChannel ? _rightAllCarrierBins : _leftAllCarrierBins;
        var speeds = new double[lastSymbolExclusive - firstSymbol];
        var filtered = 1.0;
        const double smoothAlpha = 0.18;
        const double minSpeed = 0.97;
        const double maxSpeed = 1.03;
        const double deadZone = 0.0005;

        for (var s = firstSymbol; s < lastSymbolExclusive; s++)
        {
            var start = s * symbolLength;
            var timedStart = start;
            if (allowSymbolOffsetSearch)
            {
                var searchRadius = Math.Min(symbolLength / 2, 24);
                var available = Math.Min(symbolLength + searchRadius, samples.Length - start);
                if (available >= symbolLength)
                {
                    var offset = FindBestSymbolOffset(
                        samples.AsSpan(start, available),
                        searchRadius,
                        requireStrongCpImprovement);
                    if (start + offset + symbolLength <= samples.Length)
                    {
                        timedStart = start + offset;
                    }
                }
            }

            if (timedStart + symbolLength > samples.Length)
            {
                speeds[s - firstSymbol] = Math.Clamp(filtered, minSpeed, maxSpeed);
                continue;
            }

            var symbol = samples.AsSpan(timedStart, symbolLength);
            var time = RemoveCyclicPrefix(symbol, _config.CyclicPrefixLength);
            var freqBins = ForwardFftMatchingInverse(time);
            var measured = EstimateSpeedFromPilots(freqBins, pilotBins, carrierBins);
            if (measured > 0.0)
            {
                if (Math.Abs(measured - 1.0) < deadZone)
                {
                    measured = 1.0;
                }

                filtered = (filtered * (1.0 - smoothAlpha)) + (measured * smoothAlpha);
            }

            if (Math.Abs(filtered - 1.0) < deadZone)
            {
                filtered = 1.0;
            }

            speeds[s - firstSymbol] = Math.Clamp(filtered, minSpeed, maxSpeed);
        }

        NormalizeSpeedProfileMean(speeds);
        return FitCassetteWowSpeedProfile(speeds, firstSymbol, symbolLength);
    }

    /// <summary>
    /// FitCassetteWowSpeedProfile を実行します。
    /// </summary>
    /// <param name="measured">measured を指定します。</param>
    /// <param name="firstSymbol">firstSymbol を指定します。</param>
    /// <param name="symbolLength">symbolLength を指定します。</param>
    /// <returns>処理結果。</returns>
    private double[] FitCassetteWowSpeedProfile(double[] measured, int firstSymbol, int symbolLength)
    {
        if (measured.Length < 32)
        {
            return measured;
        }

        // speed = 1 + a sin(w1 t) + b cos(w1 t) + c sin(w2 t) + d cos(w2 t)
        const double wowHz = 0.5;
        const double flutterHz = 6.0;
        var w1 = 2.0 * Math.PI * wowHz;
        var w2 = 2.0 * Math.PI * flutterHz;
        var sampleRate = Math.Max(1, _config.SampleRate);

        // 豁｣隕乗婿遞句ｼ・4x4
        var ata = new double[4, 4];
        var atb = new double[4];
        Span<double> row = stackalloc double[4];
        for (var i = 0; i < measured.Length; i++)
        {
            var t = ((firstSymbol + i) * symbolLength) / (double)sampleRate;
            var y = measured[i] - 1.0;
            row[0] = Math.Sin(w1 * t);
            row[1] = Math.Cos(w1 * t);
            row[2] = Math.Sin(w2 * t);
            row[3] = Math.Cos(w2 * t);
            for (var r = 0; r < 4; r++)
            {
                atb[r] += row[r] * y;
                for (var c = 0; c < 4; c++)
                {
                    ata[r, c] += row[r] * row[c];
                }
            }
        }

        if (!TrySolve4x4(ata, atb, out var coef))
        {
            return measured;
        }

        var ampWow = Math.Sqrt((coef[0] * coef[0]) + (coef[1] * coef[1]));
        var ampFlutter = Math.Sqrt((coef[2] * coef[2]) + (coef[3] * coef[3]));
        if (ampWow + ampFlutter < 0.0015 || ampWow + ampFlutter > 0.08)
        {
            return measured;
        }

        var fitted = new double[measured.Length];
        for (var i = 0; i < measured.Length; i++)
        {
            var t = ((firstSymbol + i) * symbolLength) / (double)sampleRate;
            fitted[i] = 1.0
                + (coef[0] * Math.Sin(w1 * t))
                + (coef[1] * Math.Cos(w1 * t))
                + (coef[2] * Math.Sin(w2 * t))
                + (coef[3] * Math.Cos(w2 * t));
        }

        var measRms = 0.0;
        var fitRms = 0.0;
        for (var i = 0; i < measured.Length; i++)
        {
            var md = measured[i] - 1.0;
            var fd = fitted[i] - 1.0;
            measRms += md * md;
            fitRms += fd * fd;
        }

        measRms = Math.Sqrt(measRms / measured.Length);
        fitRms = Math.Sqrt(fitRms / measured.Length);
        if (fitRms > 1e-6)
        {
            var boost = Math.Clamp(measRms / fitRms, 0.8, 2.5);
            for (var i = 0; i < fitted.Length; i++)
            {
                fitted[i] = 1.0 + ((fitted[i] - 1.0) * boost);
            }
        }

        NormalizeSpeedProfileMean(fitted);
        return fitted;
    }

    /// <summary>
    /// TrySolve4x4 を試行します。
    /// </summary>
    /// <param name="a">4ﾃ・ 菫よ焚陦悟・縲・/param>
    /// <param name="b">b を指定します。</param>
    /// <param name="x">x を指定します。</param>
    /// <returns>条件を満たす場合 true、それ以外は false。</returns>
    private static bool TrySolve4x4(double[,] a, double[] b, out double[] x)
    {
        x = new double[4];
        var m = new double[4, 5];
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                m[r, c] = a[r, c];
            }

            m[r, 4] = b[r];
        }

        for (var col = 0; col < 4; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < 4; r++)
            {
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col]))
                {
                    pivot = r;
                }
            }

            if (Math.Abs(m[pivot, col]) < 1e-12)
            {
                return false;
            }

            if (pivot != col)
            {
                for (var c = col; c < 5; c++)
                {
                    (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
                }
            }

            var den = m[col, col];
            for (var c = col; c < 5; c++)
            {
                m[col, c] /= den;
            }

            for (var r = 0; r < 4; r++)
            {
                if (r == col)
                {
                    continue;
                }

                var factor = m[r, col];
                for (var c = col; c < 5; c++)
                {
                    m[r, c] -= factor * m[col, c];
                }
            }
        }

        for (var i = 0; i < 4; i++)
        {
            x[i] = m[i, 4];
        }

        return true;
    }

    /// <summary>
    /// NormalizeSpeedProfileMean を実行します。
    /// </summary>
    /// <param name="speeds">speeds を指定します。</param>
    private static void NormalizeSpeedProfileMean(double[] speeds)
    {
        if (speeds.Length == 0)
        {
            return;
        }

        var sum = 0.0;
        for (var i = 0; i < speeds.Length; i++)
        {
            sum += speeds[i];
        }

        var mean = sum / speeds.Length;
        if (mean < 1e-6)
        {
            return;
        }

        for (var i = 0; i < speeds.Length; i++)
        {
            speeds[i] /= mean;
        }
    }

    /// <summary>
    /// FindBestSymbolOffset を実行します。
    /// </summary>
    /// <param name="window">謗｢邏｢遯薙・/param>
    /// <param name="maxSearch">maxSearch を指定します。</param>
    /// <param name="requireStrongCpImprovement">requireStrongCpImprovement を指定します。</param>
    /// <returns>処理結果。</returns>
    private int FindBestSymbolOffset(
        ReadOnlySpan<Complex> window,
        int maxSearch,
        bool requireStrongCpImprovement)
    {
        var fftSize = _config.FftSize;
        var cp = _config.CyclicPrefixLength;
        if (cp <= 0 || window.Length < fftSize + cp)
        {
            return 0;
        }

        maxSearch = Math.Min(maxSearch, window.Length - (fftSize + cp));
        if (maxSearch < 0)
        {
            return 0;
        }

        var bestOffset = 0;
        var bestScore = ScoreCpCorrelation(window, 0, fftSize, cp);
        for (var offset = 1; offset <= maxSearch; offset++)
        {
            var score = ScoreCpCorrelation(window, offset, fftSize, cp);
            var accept = requireStrongCpImprovement
                ? score > bestScore * 1.15 && score > bestScore + 1e-6
                : score > bestScore;
            if (accept)
            {
                bestScore = score;
                bestOffset = offset;
            }
        }

        return bestOffset;
    }

    /// <summary>
    /// ScoreCpCorrelation を実行します。
    /// </summary>
    /// <param name="window">window を指定します。</param>
    /// <param name="offset">offset を指定します。</param>
    /// <param name="fftSize">fftSize を指定します。</param>
    /// <param name="cp">cp を指定します。</param>
    /// <returns>処理結果。</returns>
    private static double ScoreCpCorrelation(ReadOnlySpan<Complex> window, int offset, int fftSize, int cp)
    {
        if (Avx.IsSupported && cp >= 2)
        {
            var scoreSimd = 0.0;
            ReadOnlySpan<double> doubles = MemoryMarshal.Cast<Complex, double>(window);
            ref var baseRef = ref MemoryMarshal.GetReference(doubles);
            var i = 0;
            for (; i <= cp - 2; i += 2)
            {
                var aOffset = (offset + i) * 2;
                var bOffset = (offset + fftSize + i) * 2;
                var a = Unsafe.ReadUnaligned<Vector256<double>>(
                    ref Unsafe.As<double, byte>(ref Unsafe.Add(ref baseRef, aOffset)));
                var b = Unsafe.ReadUnaligned<Vector256<double>>(
                    ref Unsafe.As<double, byte>(ref Unsafe.Add(ref baseRef, bOffset)));
                var masked = Avx.Multiply(Avx.Multiply(a, b), RealLaneMask);
                scoreSimd += masked.GetElement(0) + masked.GetElement(2);
            }

            for (; i < cp; i++)
            {
                var a = window[offset + i].Real;
                var b = window[offset + fftSize + i].Real;
                scoreSimd += a * b;
            }

            return scoreSimd;
        }

        if (AdvSimd.Arm64.IsSupported && cp >= 1)
        {
            var scoreSimd = 0.0;
            ReadOnlySpan<double> doubles = MemoryMarshal.Cast<Complex, double>(window);
            ref var baseRef = ref MemoryMarshal.GetReference(doubles);
            for (var i = 0; i < cp; i++)
            {
                var aOffset = (offset + i) * 2;
                var bOffset = (offset + fftSize + i) * 2;
                var a = Unsafe.ReadUnaligned<Vector128<double>>(
                    ref Unsafe.As<double, byte>(ref Unsafe.Add(ref baseRef, aOffset)));
                var b = Unsafe.ReadUnaligned<Vector128<double>>(
                    ref Unsafe.As<double, byte>(ref Unsafe.Add(ref baseRef, bOffset)));
                var prod = AdvSimd.Arm64.Multiply(a, b);
                scoreSimd += prod.GetElement(0);
            }

            return scoreSimd;
        }

        var score = 0.0;
        for (var i = 0; i < cp; i++)
        {
            var a = window[offset + i].Real;
            var b = window[offset + fftSize + i].Real;
            score += a * b;
        }

        return score;
    }

    /// <summary>
    /// EstimateSpeedFromPilots を実行します。
    /// </summary>
    /// <param name="freqBins">freqBins を指定します。</param>
    /// <param name="pilotBins">pilotBins を指定します。</param>
    /// <param name="carrierBins">carrierBins を指定します。</param>
    /// <returns>処理結果。</returns>
    private static double EstimateSpeedFromPilots(
        Complex[] freqBins,
        List<int> pilotBins,
        List<int> carrierBins)
    {
        var weightedSum = 0.0;
        var weightTotal = 0.0;

        foreach (var pilotBin in pilotBins)
        {
            if (pilotBin <= 1 || pilotBin >= (freqBins.Length / 2) - 1)
            {
                continue;
            }

            var y1 = ComplexMagnitude(freqBins[pilotBin - 1]);
            var y2 = ComplexMagnitude(freqBins[pilotBin]);
            var y3 = ComplexMagnitude(freqBins[pilotBin + 1]);
            if (y2 < y1 || y2 < y3 || y2 < 1e-6)
            {
                continue;
            }

            var denom = y1 - (2.0 * y2) + y3;
            var delta = Math.Abs(denom) < 1e-18 ? 0.0 : 0.5 * (y1 - y3) / denom;
            delta = Math.Clamp(delta, -0.5, 0.5);
            var peakBin = pilotBin + delta;

            weightedSum += y2 * (peakBin / pilotBin);
            weightTotal += y2;
        }

        if (weightTotal < 1e-4)
        {
            foreach (var carrierBin in carrierBins)
            {
                if (carrierBin <= 1 || carrierBin >= (freqBins.Length / 2) - 1)
                {
                    continue;
                }

                var y1 = ComplexMagnitude(freqBins[carrierBin - 1]);
                var y2 = ComplexMagnitude(freqBins[carrierBin]);
                var y3 = ComplexMagnitude(freqBins[carrierBin + 1]);
                if (y2 < y1 || y2 < y3 || y2 < 1e-6)
                {
                    continue;
                }

                var denom = y1 - (2.0 * y2) + y3;
                var delta = Math.Abs(denom) < 1e-18 ? 0.0 : 0.5 * (y1 - y3) / denom;
                delta = Math.Clamp(delta, -0.5, 0.5);
                weightedSum += y2 * ((carrierBin + delta) / carrierBin);
                weightTotal += y2;
            }
        }

        if (weightTotal < 1e-6)
        {
            return 0.0;
        }

        return weightedSum / weightTotal;
    }

    /// <summary>
    /// ComplexMagnitude を実行します。
    /// </summary>
    /// <param name="value">隍・ｴ蛟､縲・/param>
    /// <returns>処理結果。</returns>
    private static double ComplexMagnitude(Complex value)
    {
        return Math.Sqrt((value.Real * value.Real) + (value.Imaginary * value.Imaginary));
    }

    /// <summary>
    /// ResampleWithInverseSpeed を実行します。
    /// </summary>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="speedProfile">speedProfile を指定します。</param>
    /// <returns>処理結果。</returns>
    private Complex[] ResampleWithInverseSpeed(Complex[] samples, double[] speedProfile)
    {
        var n = samples.Length;
        if (n == 0)
        {
            return samples;
        }

        BuildInverseCumul(samples.Length, speedProfile, out var cumul, out var scale, out _);
        var output = new Complex[n];
        var warpedIndex = 0;
        for (var j = 0; j < n; j++)
        {
            output[j] = SampleWarpedAtCumul(samples, cumul, scale, j, ref warpedIndex);
        }

        return output;
    }

    /// <summary>
    /// ResampleSegmentWithInverseSpeed を実行します。
    /// </summary>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="speedProfile">speedProfile を指定します。</param>
    /// <param name="segmentStart">segmentStart を指定します。</param>
    /// <param name="segmentLength">segmentLength を指定します。</param>
    /// <returns>処理結果。</returns>
    private Complex[] ResampleSegmentWithInverseSpeed(
        Complex[] samples,
        double[] speedProfile,
        int segmentStart,
        int segmentLength)
    {
        if (segmentLength <= 0)
        {
            return [];
        }

        BuildInverseCumul(samples.Length, speedProfile, out var cumul, out var scale, out _);
        var output = new Complex[segmentLength];
        var warpedIndex = 0;
        var firstTarget = segmentStart / scale;
        while (warpedIndex < samples.Length - 1 && cumul[warpedIndex + 1] < firstTarget)
        {
            warpedIndex++;
        }

        for (var j = 0; j < segmentLength; j++)
        {
            output[j] = SampleWarpedAtCumul(samples, cumul, scale, segmentStart + j, ref warpedIndex);
        }

        return output;
    }

    /// <summary>
    /// ResampleSegmentWithInverseSpeed を実行します。
    /// </summary>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="segmentStart">segmentStart を指定します。</param>
    /// <param name="segmentLength">segmentLength を指定します。</param>
    /// <param name="sampleRate">sampleRate を指定します。</param>
    /// <param name="amount">amount を指定します。</param>
    /// <param name="wowPhase">wowPhase を指定します。</param>
    /// <param name="flutterPhase">flutterPhase を指定します。</param>
    /// <returns>処理結果。</returns>
    private Complex[] ResampleSegmentWithInverseSpeed(
        Complex[] samples,
        int segmentStart,
        int segmentLength,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase,
        int stride = 1)
    {
        var output = new Complex[segmentLength];
        ResampleSegmentWithInverseSpeed(
            samples,
            segmentStart,
            segmentLength,
            sampleRate,
            amount,
            wowPhase,
            flutterPhase,
            output,
            stride);
        return output;
    }

    /// <summary>
    /// ResampleSegmentWithInverseSpeed を実行します。
    /// </summary>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="segmentStart">segmentStart を指定します。</param>
    /// <param name="segmentLength">segmentLength を指定します。</param>
    /// <param name="sampleRate">sampleRate を指定します。</param>
    /// <param name="amount">amount を指定します。</param>
    /// <param name="wowPhase">wowPhase を指定します。</param>
    /// <param name="flutterPhase">flutterPhase を指定します。</param>
    /// <param name="output">output を指定します。</param>
    private void ResampleSegmentWithInverseSpeed(
        Complex[] samples,
        int segmentStart,
        int segmentLength,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase,
        Span<Complex> output,
        int stride = 1)
    {
        if (segmentLength <= 0)
        {
            return;
        }

        if (output.Length < segmentLength)
        {
            throw new ArgumentException("Output span is shorter than segment length.", nameof(output));
        }

        stride = Math.Max(1, stride);
        var n = samples.Length;
        if (n <= 1)
        {
            output.Slice(0, segmentLength).Clear();
            return;
        }

        const double wowHz = 0.5;
        const double flutterHz = 6.0;
        var wowOmega = 2.0 * Math.PI * wowHz / sampleRate;
        var flutterOmega = 2.0 * Math.PI * flutterHz / sampleRate;
        var wowGain = amount * 0.65;
        var flutterGain = amount * 0.35;

        double CumulAt(int i) =>
            CassetteCumulAtCached(i, wowGain, flutterGain, wowPhase, wowOmega, flutterPhase, flutterOmega);

        var cumulEnd = CumulAt(n - 1);
        var scale = cumulEnd > 1e-12 ? (n - 1) / cumulEnd : 1.0;

        var i = 0;
        var c0 = 0.0;
        var c1 = CumulAt(1);
        for (var j = 0; j < segmentLength; j += stride)
        {
            var target = (segmentStart + j) / scale;
            if (i >= n - 1)
            {
                output[j] = samples[^1];
                continue;
            }

            var guess = (int)target;
            if (guess > i + 8 && guess < n - 1)
            {
                i = guess;
                c0 = CumulAt(i);
                c1 = CumulAt(i + 1);
            }

            while (i < n - 2 && c1 <= target)
            {
                i++;
                c0 = c1;
                c1 = CumulAt(i + 1);
            }

            while (i > 0 && c0 > target)
            {
                i--;
                c1 = c0;
                c0 = CumulAt(i);
            }

            if (i >= n - 1)
            {
                output[j] = samples[^1];
                continue;
            }

            var span = c1 - c0;
            var frac = span > 1e-12 ? (target - c0) / span : 0.0;
            frac = Math.Clamp(frac, 0.0, 1.0);
            var a = samples[i].Real;
            var b = samples[i + 1].Real;
            output[j] = new Complex(a + ((b - a) * frac), 0.0);
        }
    }

    /// <summary>
    /// CassetteCumulAt を実行します。
    /// </summary>
    /// <param name="i">i縲・/param>
    /// <param name="sampleRate">sampleRate を指定します。</param>
    /// <param name="amount">amount を指定します。</param>
    /// <param name="wowPhase">wowPhase を指定します。</param>
    /// <param name="flutterPhase">flutterPhase を指定します。</param>
    /// <returns>処理結果。</returns>
    private static double CassetteCumulAt(
        int i,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        if (i <= 0)
        {
            return 0.0;
        }

        const double wowHz = 0.5;
        const double flutterHz = 6.0;
        var wowOmega = 2.0 * Math.PI * wowHz / sampleRate;
        var flutterOmega = 2.0 * Math.PI * flutterHz / sampleRate;
        return CassetteCumulAtCached(
            i,
            amount * 0.65,
            amount * 0.35,
            wowPhase,
            wowOmega,
            flutterPhase,
            flutterOmega);
    }

    /// <summary>
    /// CassetteCumulAtCached を実行します。
    /// </summary>
    /// <param name="i">i縲・/param>
    /// <param name="wowGain">wowGain縲・/param>
    /// <param name="flutterGain">flutterGain縲・/param>
    /// <param name="wowOmega">wowOmega縲・/param>
    /// <param name="flutterOmega">flutterOmega縲・/param>
    /// <param name="wowPhase">wowPhase を指定します。</param>
    /// <param name="flutterPhase">flutterPhase を指定します。</param>
    /// <returns>処理結果。</returns>
    private static double CassetteCumulAtCached(
        int i,
        double wowGain,
        double flutterGain,
        double wowPhase,
        double wowOmega,
        double flutterPhase,
        double flutterOmega)
    {
        if (i <= 0)
        {
            return 0.0;
        }

        return i
            + (wowGain * SumOfSines(wowPhase, wowOmega, i))
            + (flutterGain * SumOfSines(flutterPhase, flutterOmega, i));
    }

    /// <summary>
    /// SumOfSines を実行します。
    /// </summary>
    /// <param name="phase0">phase0縲・/param>
    /// <param name="omega">omega縲・/param>
    /// <param name="count">count を指定します。</param>
    /// <returns>処理結果。</returns>
    private static double SumOfSines(double phase0, double omega, int count)
    {
        if (count <= 0)
        {
            return 0.0;
        }

        var half = omega * 0.5;
        var denom = Math.Sin(half);
        if (Math.Abs(denom) < 1e-12)
        {
            return count * Math.Sin(phase0);
        }

        return Math.Sin(count * half) / denom * Math.Sin(phase0 + ((count - 1) * half));
    }

    /// <summary>
    /// FindCassetteCumulIndex を実行します。
    /// </summary>
    /// <param name="target">target縲・/param>
    /// <param name="sampleCount">sampleCount を指定します。</param>
    /// <param name="sampleRate">sampleRate を指定します。</param>
    /// <param name="amount">amount を指定します。</param>
    /// <param name="wowPhase">wowPhase を指定します。</param>
    /// <param name="flutterPhase">flutterPhase を指定します。</param>
    /// <returns>処理結果。</returns>
    private static int FindCassetteCumulIndex(
        double target,
        int sampleCount,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        var lo = 0;
        var hi = sampleCount - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (CassetteCumulAt(mid, sampleRate, amount, wowPhase, flutterPhase) <= target)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    /// <summary>
    /// BuildInverseCumul を構築します。
    /// </summary>
    /// <param name="cumul">cumul縲・/param>
    /// <param name="speedHop">speedHop縲・/param>
    /// <param name="sampleCount">sampleCount を指定します。</param>
    /// <param name="speedProfile">speedProfile を指定します。</param>
    /// <param name="scale">scale を指定します。</param>
    private void BuildInverseCumul(
        int sampleCount,
        double[] speedProfile,
        out double[] cumul,
        out double scale,
        out int speedHop)
    {
        var symbolLength = Math.Max(1, SamplesPerOfdmSymbol);
        speedHop = speedProfile.Length >= sampleCount ? 1 : symbolLength;
        cumul = new double[sampleCount];
        if (sampleCount == 0)
        {
            scale = 1.0;
            return;
        }

        cumul[0] = 0.0;
        for (var i = 0; i < sampleCount - 1; i++)
        {
            var speedIndex = Math.Min(i / speedHop, speedProfile.Length - 1);
            var speed = speedProfile[speedIndex];
            if (speed < 1e-6)
            {
                speed = 1.0;
            }

            cumul[i + 1] = cumul[i] + speed;
        }

        scale = cumul[^1] > 1e-12 ? (sampleCount - 1) / cumul[^1] : 1.0;
    }

    /// <summary>
    /// SampleWarpedAtCumul を実行します。
    /// </summary>
    /// <param name="cumul">cumul縲・/param>
    /// <param name="outputIndex">outputIndex縲・/param>
    /// <param name="warpedIndex">warpedIndex縲・/param>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="scale">scale を指定します。</param>
    /// <returns>隍・ｴ蛟､縲・/returns>
    private static Complex SampleWarpedAtCumul(
        Complex[] samples,
        double[] cumul,
        double scale,
        int outputIndex,
        ref int warpedIndex)
    {
        var target = outputIndex / scale;
        while (warpedIndex < samples.Length - 1 && cumul[warpedIndex + 1] < target)
        {
            warpedIndex++;
        }

        if (warpedIndex >= samples.Length - 1)
        {
            return samples[^1];
        }

        var span = cumul[warpedIndex + 1] - cumul[warpedIndex];
        var frac = span > 1e-12 ? (target - cumul[warpedIndex]) / span : 0.0;
        var pos = warpedIndex + Math.Clamp(frac, 0.0, 1.0);
        return SampleHermite(samples, pos);
    }

    /// <summary>
    /// SampleHermite を実行します。
    /// </summary>
    /// <param name="position">position縲・/param>
    /// <param name="samples">samples を指定します。</param>
    /// <returns>隍・ｴ蛟､縲・/returns>
    private static Complex SampleHermite(Complex[] samples, double position)
    {
        if (samples.Length == 0)
        {
            return Complex.Zero;
        }

        if (position <= 0.0)
        {
            return samples[0];
        }

        if (position >= samples.Length - 1)
        {
            return samples[^1];
        }

        var i = (int)position;
        var t = position - i;
        var y0 = samples[Math.Max(0, i - 1)].Real;
        var y1 = samples[i].Real;
        var y2 = samples[Math.Min(samples.Length - 1, i + 1)].Real;
        var y3 = samples[Math.Min(samples.Length - 1, i + 2)].Real;
        var m1 = 0.5 * (y2 - y0);
        var m2 = 0.5 * (y3 - y1);
        var t2 = t * t;
        var t3 = t2 * t;
        var value =
            (((2 * t3) - (3 * t2) + 1) * y1) +
            ((t3 - (2 * t2) + t) * m1) +
            (((-2 * t3) + (3 * t2)) * y2) +
            ((t3 - t2) * m2);
        return new Complex(value, 0.0);
    }

    /// <summary>
    /// ScoreLock を実行します。
    /// </summary>
    /// <param name="start">start縲・/param>
    public double ScoreLock(Complex[] samples, int start, int symbolCount, bool useRightChannel = false)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (symbolCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(symbolCount));
        }

        var symbolLength = SamplesPerOfdmSymbol;
        if (start < 0 || start + (symbolCount * symbolLength) > samples.Length)
        {
            return double.NegativeInfinity;
        }

        var pilotBins = useRightChannel ? _rightPilotBins : _leftPilotBins;
        var timeNoCp = _scoreTimeNoCpScratch;
        var freqBins = _scoreFreqBinsScratch;
        var score = 0.0;
        for (var s = 0; s < symbolCount; s++)
        {
            var symbolStart = start + (s * symbolLength);
            score += ScoreSingleSymbolLock(samples.AsSpan(symbolStart, symbolLength), pilotBins, timeNoCp, freqBins);
        }

        return score / symbolCount;
    }

    /// <summary>
    /// FindBestSymbolStart を実行します。
    /// </summary>
    /// <param name="expectedStart">expectedStart縲・/param>
    public int FindBestSymbolStart(
        Complex[] samples,
        int expectedStart,
        int searchRadius,
        bool useRightChannel = false)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var symbolLength = SamplesPerOfdmSymbol;
        expectedStart = Math.Clamp(expectedStart, 0, Math.Max(0, samples.Length - symbolLength));
        if (searchRadius <= 0)
        {
            return expectedStart;
        }

        var pilotBins = useRightChannel ? _rightPilotBins : _leftPilotBins;
        var timeNoCp = _scoreTimeNoCpScratch;
        var freqBins = _scoreFreqBinsScratch;
        var back = Math.Min(searchRadius, expectedStart);
        var forward = Math.Min(searchRadius, samples.Length - symbolLength - expectedStart);
        var cpAtExpected = ScoreSingleSymbolCpLock(samples.AsSpan(expectedStart, symbolLength));
        var scoreAtExpected = ScoreSingleSymbolLock(samples.AsSpan(expectedStart, symbolLength), pilotBins, timeNoCp, freqBins);
        var bestDelta = 0;
        var bestScore = scoreAtExpected;
        var candidateCount = back + forward + 1;

        if (candidateCount <= 7)
        {
            for (var delta = -back; delta <= forward; delta++)
            {
                if (delta == 0)
                {
                    continue;
                }

                var score = ScoreSingleSymbolLock(samples.AsSpan(expectedStart + delta, symbolLength), pilotBins, timeNoCp, freqBins);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestDelta = delta;
                }
            }

            const double smallRangeLockMargin = 0.15;
            if (bestDelta != 0 && bestScore < scoreAtExpected + smallRangeLockMargin)
            {
                return expectedStart;
            }

            return expectedStart + bestDelta;
        }

        var topCandidateCount = Math.Min(candidateCount - 1, 4);
        Span<int> topDeltas = stackalloc int[topCandidateCount];
        Span<double> topCpScores = stackalloc double[topCandidateCount];
        for (var i = 0; i < topCandidateCount; i++)
        {
            topDeltas[i] = 0;
            topCpScores[i] = double.NegativeInfinity;
        }

        for (var delta = -back; delta <= forward; delta++)
        {
            if (delta == 0)
            {
                continue;
            }

            var cpScore = ScoreSingleSymbolCpLock(samples.AsSpan(expectedStart + delta, symbolLength));
            if (cpScore < cpAtExpected - 0.2)
            {
                continue;
            }

            if (cpScore <= topCpScores[^1])
            {
                continue;
            }

            var insertIndex = topCandidateCount - 1;
            while (insertIndex > 0 && cpScore > topCpScores[insertIndex - 1])
            {
                topCpScores[insertIndex] = topCpScores[insertIndex - 1];
                topDeltas[insertIndex] = topDeltas[insertIndex - 1];
                insertIndex--;
            }

            topCpScores[insertIndex] = cpScore;
            topDeltas[insertIndex] = delta;
        }

        for (var i = 0; i < topCandidateCount; i++)
        {
            var delta = topDeltas[i];
            if (delta == 0)
            {
                continue;
            }

            var score = ScoreSingleSymbolLock(samples.AsSpan(expectedStart + delta, symbolLength), pilotBins, timeNoCp, freqBins);
            if (score > bestScore)
            {
                bestScore = score;
                bestDelta = delta;
            }
        }

        const double lockMargin = 0.15;
        if (bestDelta != 0 && bestScore < scoreAtExpected + lockMargin)
        {
            return expectedStart;
        }

        return expectedStart + bestDelta;
    }

    /// <summary>
    /// ScoreSingleSymbolCpLock を実行します。
    /// </summary>
    /// <param name="symbolWithCp">symbolWithCp を指定します。</param>
    /// <returns>処理結果。</returns>
    private double ScoreSingleSymbolCpLock(ReadOnlySpan<Complex> symbolWithCp)
    {
        var fftSize = _config.FftSize;
        var cp = _config.CyclicPrefixLength;
        if (symbolWithCp.Length < fftSize + cp || cp <= 0)
        {
            return double.NegativeInfinity;
        }

        var cpScore = ScoreCpCorrelation(symbolWithCp, 0, fftSize, cp);
        var energy = 0.0;
        for (var i = 0; i < cp; i++)
        {
            var a = symbolWithCp[i].Real;
            var b = symbolWithCp[fftSize + i].Real;
            energy += (a * a) + (b * b);
        }

        if (energy < 1e-12)
        {
            return double.NegativeInfinity;
        }

        return cpScore / (energy * 0.5);
    }

    /// <summary>
    /// ScoreSingleSymbolLock を実行します。
    /// </summary>
    /// <param name="symbolWithCp">symbolWithCp を指定します。</param>
    /// <param name="pilotBins">pilotBins を指定します。</param>
    /// <returns>処理結果。</returns>
    private double ScoreSingleSymbolLock(ReadOnlySpan<Complex> symbolWithCp, List<int> pilotBins)
    {
        var timeNoCp = _scoreTimeNoCpScratch;
        var freqBins = _scoreFreqBinsScratch;
        return ScoreSingleSymbolLock(symbolWithCp, pilotBins, timeNoCp, freqBins);
    }

    /// <summary>
    /// ScoreSingleSymbolLock を実行します。
    /// </summary>
    /// <param name="symbolWithCp">symbolWithCp を指定します。</param>
    /// <param name="pilotBins">pilotBins を指定します。</param>
    /// <param name="timeNoCp">timeNoCp を指定します。</param>
    /// <param name="freqBins">freqBins を指定します。</param>
    /// <returns>処理結果。</returns>
    private double ScoreSingleSymbolLock(
        ReadOnlySpan<Complex> symbolWithCp,
        List<int> pilotBins,
        Complex[] timeNoCp,
        Complex[] freqBins)
    {
        var normalizedCp = ScoreSingleSymbolCpLock(symbolWithCp);
        if (double.IsNegativeInfinity(normalizedCp))
        {
            return double.NegativeInfinity;
        }

        symbolWithCp.Slice(_config.CyclicPrefixLength, _config.FftSize).CopyTo(timeNoCp);
        ForwardFftMatchingInverseInto(timeNoCp, freqBins);
        var pilotPower = 0.0;
        var pilotCount = 0;
        foreach (var pilotBin in pilotBins)
        {
            if (pilotBin <= 0 || pilotBin >= freqBins.Length)
            {
                continue;
            }

            var p = freqBins[pilotBin];
            pilotPower += (p.Real * p.Real) + (p.Imaginary * p.Imaginary);
            pilotCount++;
        }

        if (pilotCount == 0)
        {
            return normalizedCp;
        }

        pilotPower /= pilotCount;
        return (normalizedCp * 2.0) + Math.Log10(pilotPower + 1e-12);
    }

    /// <summary>
    /// ストリームからハード判定ビットを復調します。
    /// </summary>
    /// <param name="samples">入力サンプル列。</param>
    /// <param name="cursor">読み取りカーソル（更新あり）。</param>
    /// <param name="bitCount">取得するビット数。</param>
    /// <param name="useRightChannel">右チャネルを使う場合 true。</param>
    /// <param name="logicalSampleOffset">論理サンプルオフセット。</param>
    /// <param name="searchRadius">シンボル開始探索半径。</param>
    /// <param name="interleaveInitSeed">インタリーブ初期シード。</param>
    /// <returns>復調したビット列。</returns>
    public bool[] DemodulateBitsFromStream(
        Complex[] samples,
        ref int cursor,
        int bitCount,
        bool useRightChannel,
        long logicalSampleOffset,
        int searchRadius = 16,
        int interleaveInitSeed = 0)
    {
        if (bitCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        }

        if (useRightChannel && _config.ChannelMode != ChannelMode.Stereo)
        {
            throw new InvalidOperationException("Right channel demodulation requires stereo mode.");
        }

        ArgumentNullException.ThrowIfNull(samples);
        var pilotBins = useRightChannel ? _rightPilotBins : _leftPilotBins;
        var dataModulationByBin = useRightChannel
            ? _rightDataCarrierModulationByBin
            : _leftDataCarrierModulationByBin;
        var symbolLength = SamplesPerOfdmSymbol;
        var symbolCount = bitCount == 0
            ? 1
            : (bitCount + BitsPerOfdmSymbol - 1) / BitsPerOfdmSymbol;

        var bits = new bool[bitCount];
        var bitIndex = 0;
        _ = logicalSampleOffset;
        var agcState = new PilotGroupAgcState(pilotBins.Count);
        var fftSize = _config.FftSize;
        var timeNoCp = new Complex[fftSize];
        var freqBins = new Complex[fftSize];
        var equalizers = new Complex[fftSize];
        var followRadius = Math.Max(searchRadius, 2);
        var position = cursor;

        for (var s = 0; s < symbolCount && bitIndex < bitCount; s++)
        {
            var start = FindBestSymbolStart(samples, position, followRadius, useRightChannel);
            if (start + symbolLength > samples.Length)
            {
                throw new InvalidDataException("WAV ended while synchronizing OFDM symbol.");
            }

            var symbol = samples.AsSpan(start, symbolLength);
            PrepareSymbolFrequency(
                symbol,
                pilotBins,
                useRightChannel,
                agcState,
                timeNoCp,
                freqBins,
                equalizers);
            var symbolOffset = (long)s * symbolLength;
            var dataOrder = ResolveDataCarrierOrder(useRightChannel, symbolOffset, interleaveInitSeed);

            foreach (var dataBin in dataOrder)
            {
                if (bitIndex >= bitCount)
                {
                    break;
                }

                EmitSymbolBits(
                    freqBins[dataBin] * equalizers[dataBin],
                    dataModulationByBin[dataBin],
                    ref bitIndex,
                    bits);
            }

            position = start + symbolLength;
        }

        cursor = position;
        return bits;
    }

    /// <summary>
    /// ストリームからソフト判定 LLR を復調します。
    /// </summary>
    /// <param name="samples">入力サンプル列。</param>
    /// <param name="cursor">読み取りカーソル（更新あり）。</param>
    /// <param name="bitCount">取得するビット数。</param>
    /// <param name="useRightChannel">右チャネルを使う場合 true。</param>
    /// <param name="logicalSampleOffset">論理サンプルオフセット。</param>
    /// <param name="searchRadius">シンボル開始探索半径。</param>
    /// <param name="noiseVariance">既知雑音分散。</param>
    /// <param name="interleaveInitSeed">インタリーブ初期シード。</param>
    /// <param name="onEqualizedDataSymbol">等化後データシンボルの通知先。</param>
    /// <param name="onEqualizedDataSymbolFrame">等化後シンボル列の通知先。</param>
    /// <param name="onFftSymbolFrame">FFTシンボル列の通知先。</param>
    /// <returns>復調した LLR 列。</returns>
    public double[] DemodulateSoftLlrsFromStream(
        Complex[] samples,
        ref int cursor,
        int bitCount,
        bool useRightChannel,
        long logicalSampleOffset,
        int searchRadius = 16,
        double noiseVariance = 0.05,
        int interleaveInitSeed = 0,
        Action<Complex>? onEqualizedDataSymbol = null,
        Action<Complex[], int>? onEqualizedDataSymbolFrame = null,
        Action<Complex[], int>? onFftSymbolFrame = null)
    {
        return DemodulateSoftLlrsFromStreamCore(
            samples,
            secondarySamples: null,
            ref cursor,
            bitCount,
            useRightChannel,
            secondaryUseRightChannel: false,
            logicalSampleOffset,
            searchRadius,
            noiseVariance,
            estimateNoiseFromPilots: true,
            interleaveInitSeed,
            onEqualizedDataSymbol,
            onEqualizedDataSymbolFrame,
            onFftSymbolFrame);
    }

    /// <summary>
    /// 左右チャネルを統合してソフト判定 LLR を復調します。
    /// </summary>
    /// <param name="leftSamples">左チャネルサンプル列。</param>
    /// <param name="rightSamples">右チャネルサンプル列。</param>
    /// <param name="cursor">読み取りカーソル（更新あり）。</param>
    /// <param name="bitCount">取得するビット数。</param>
    /// <param name="logicalSampleOffset">論理サンプルオフセット。</param>
    /// <param name="searchRadius">シンボル開始探索半径。</param>
    /// <param name="noiseVariance">既知雑音分散。</param>
    /// <param name="estimateNoiseFromPilots">パイロットから雑音推定する場合 true。</param>
    /// <param name="interleaveInitSeed">インタリーブ初期シード。</param>
    /// <returns>復調した LLR 列。</returns>
    public double[] DemodulateSoftLlrsStereoCombined(
        Complex[] leftSamples,
        Complex[] rightSamples,
        ref int cursor,
        int bitCount,
        long logicalSampleOffset,
        int searchRadius = 16,
        double noiseVariance = 0.05,
        bool estimateNoiseFromPilots = true,
        int interleaveInitSeed = 0)
    {
        ArgumentNullException.ThrowIfNull(leftSamples);
        ArgumentNullException.ThrowIfNull(rightSamples);
        if (_config.ChannelMode != ChannelMode.Stereo || rightSamples.Length == 0)
        {
            return DemodulateSoftLlrsFromStreamCore(
                leftSamples,
                secondarySamples: null,
                ref cursor,
                bitCount,
                useRightChannel: false,
                secondaryUseRightChannel: false,
                logicalSampleOffset,
                searchRadius,
                noiseVariance,
                estimateNoiseFromPilots,
                interleaveInitSeed,
                onEqualizedDataSymbol: null,
                onEqualizedDataSymbolFrame: null,
                onFftSymbolFrame: null);
        }

        if (leftSamples.Length != rightSamples.Length)
        {
            throw new ArgumentException("Left/right sample lengths must match for stereo soft combine.");
        }

        return DemodulateSoftLlrsFromStreamCore(
            leftSamples,
            rightSamples,
            ref cursor,
            bitCount,
            useRightChannel: false,
            secondaryUseRightChannel: true,
            logicalSampleOffset,
            searchRadius,
            noiseVariance,
            estimateNoiseFromPilots,
            interleaveInitSeed,
            onEqualizedDataSymbol: null,
            onEqualizedDataSymbolFrame: null,
            onFftSymbolFrame: null);
    }

    /// <summary>
    /// DemodulateSoftLlrsFromStreamCore を実行します。
    /// </summary>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="secondarySamples">secondarySamples を指定します。</param>
    /// <param name="cursor">cursor を指定します。</param>
    /// <param name="bitCount">bitCount を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。true で有効です。</param>
    /// <param name="secondaryUseRightChannel">secondaryUseRightChannel を指定します。</param>
    /// <param name="logicalSampleOffset">logicalSampleOffset を指定します。</param>
    /// <param name="searchRadius">searchRadius を指定します。</param>
    /// <param name="noiseVariance">noiseVariance を指定します。</param>
    /// <param name="estimateNoiseFromPilots">estimateNoiseFromPilots を指定します。</param>
    /// <param name="interleaveInitSeed">interleaveInitSeed を指定します。</param>
    /// <param name="onEqualizedDataSymbol">onEqualizedDataSymbol を指定します。</param>
    /// <param name="onEqualizedDataSymbolFrame">onEqualizedDataSymbolFrame を指定します。</param>
    /// <param name="onFftSymbolFrame">onFftSymbolFrame を指定します。</param>
    /// <returns>処理結果。</returns>
    private double[] DemodulateSoftLlrsFromStreamCore(
        Complex[] samples,
        Complex[]? secondarySamples,
        ref int cursor,
        int bitCount,
        bool useRightChannel,
        bool secondaryUseRightChannel,
        long logicalSampleOffset,
        int searchRadius,
        double noiseVariance,
        bool estimateNoiseFromPilots,
        int interleaveInitSeed,
        Action<Complex>? onEqualizedDataSymbol,
        Action<Complex[], int>? onEqualizedDataSymbolFrame,
        Action<Complex[], int>? onFftSymbolFrame)
    {
        if (bitCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        }

        if (useRightChannel && _config.ChannelMode != ChannelMode.Stereo)
        {
            throw new InvalidOperationException("Right channel demodulation requires stereo mode.");
        }

        ArgumentNullException.ThrowIfNull(samples);
        var pilotBins = useRightChannel ? _rightPilotBins : _leftPilotBins;
        var secondaryPilotBins = secondaryUseRightChannel ? _rightPilotBins : _leftPilotBins;
        var symbolLength = SamplesPerOfdmSymbol;
        var symbolCount = bitCount == 0
            ? 1
            : (bitCount + BitsPerOfdmSymbol - 1) / BitsPerOfdmSymbol;

        var llrs = new double[bitCount];
        var bitIndex = 0;
        _ = logicalSampleOffset;
        var primaryAgcState = new PilotGroupAgcState(pilotBins.Count);
        var secondaryAgcState = new PilotGroupAgcState(secondaryPilotBins.Count);
        var fftSize = _config.FftSize;
        var primaryTimeNoCp = new Complex[fftSize];
        var primaryFreqBins = new Complex[fftSize];
        var primaryEqualizers = new Complex[fftSize];
        var secondaryTimeNoCp = new Complex[fftSize];
        var secondaryFreqBins = new Complex[fftSize];
        var secondaryEqualizers = new Complex[fftSize];
        var followRadius = Math.Max(searchRadius, 2);
        var position = cursor;
        var noiseAccum = 0.0;
        var noiseCount = 0;

        var effectiveVariance = Math.Max(1e-6, noiseVariance);

        for (var s = 0; s < symbolCount && bitIndex < bitCount; s++)
        {
            var start = FindBestSymbolStart(samples, position, followRadius, useRightChannel);
            if (start + symbolLength > samples.Length)
            {
                throw new InvalidDataException("WAV ended while synchronizing OFDM symbol.");
            }

            PrepareSymbolFrequency(
                samples.AsSpan(start, symbolLength),
                pilotBins,
                useRightChannel,
                primaryAgcState,
                primaryTimeNoCp,
                primaryFreqBins,
                primaryEqualizers);
            onFftSymbolFrame?.Invoke(primaryFreqBins, primaryFreqBins.Length);
            if (estimateNoiseFromPilots)
            {
                AccumulatePilotNoiseFromPrepared(
                    primaryFreqBins,
                    primaryEqualizers,
                    pilotBins,
                    ref noiseAccum,
                    ref noiseCount);
            }

            if (secondarySamples is not null)
            {
                if (start + symbolLength > secondarySamples.Length)
                {
                    throw new InvalidDataException("Secondary WAV ended while synchronizing OFDM symbol.");
                }

                PrepareSymbolFrequency(
                    secondarySamples.AsSpan(start, symbolLength),
                    secondaryPilotBins,
                    secondaryUseRightChannel,
                    secondaryAgcState,
                    secondaryTimeNoCp,
                    secondaryFreqBins,
                    secondaryEqualizers);
                if (estimateNoiseFromPilots)
                {
                    AccumulatePilotNoiseFromPrepared(
                        secondaryFreqBins,
                        secondaryEqualizers,
                        secondaryPilotBins,
                        ref noiseAccum,
                        ref noiseCount);
                }
            }

            if (estimateNoiseFromPilots && noiseCount > 0)
            {
                effectiveVariance = Math.Clamp(noiseAccum / noiseCount, 1e-4, 0.5);
            }

            var symbolBitStart = bitIndex;
            EmitSymbolSoftLlrsFromPrepared(
                primaryFreqBins,
                primaryEqualizers,
                useRightChannel,
                (long)s * symbolLength,
                ref bitIndex,
                llrs,
                effectiveVariance,
                addToExisting: false,
                interleaveInitSeed,
                onEqualizedDataSymbol,
                onEqualizedDataSymbolFrame);

            if (secondarySamples is not null)
            {
                var secondaryBitIndex = symbolBitStart;
                EmitSymbolSoftLlrsFromPrepared(
                    secondaryFreqBins,
                    secondaryEqualizers,
                    secondaryUseRightChannel,
                    (long)s * symbolLength,
                    ref secondaryBitIndex,
                    llrs,
                    effectiveVariance,
                    addToExisting: true,
                    interleaveInitSeed,
                        onEqualizedDataSymbol,
                        onEqualizedDataSymbolFrame: null);
            }

            position = start + symbolLength;
        }

        cursor = position;
        return llrs;
    }

    /// <summary>
    /// EmitSymbolSoftLlrsForChannel を実行します。
    /// </summary>
    /// <param name="symbolWithCp">symbolWithCp を指定します。</param>
    /// <param name="pilotBins">pilotBins を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。true で有効です。</param>
    /// <param name="agcState">agcState を指定します。</param>
    /// <param name="logical">logical を指定します。</param>
    /// <param name="timeNoCp">timeNoCp を指定します。</param>
    /// <param name="freqBins">freqBins を指定します。</param>
    /// <param name="equalizers">equalizers を指定します。</param>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="llrs">llrs を指定します。</param>
    /// <param name="noiseVariance">noiseVariance を指定します。</param>
    /// <param name="addToExisting">addToExisting を指定します。</param>
    /// <param name="interleaveInitSeed">interleaveInitSeed を指定します。</param>
    private void EmitSymbolSoftLlrsForChannel(
        ReadOnlySpan<Complex> symbolWithCp,
        List<int> pilotBins,
        bool useRightChannel,
        PilotGroupAgcState agcState,
        long logical,
        Complex[] timeNoCp,
        Complex[] freqBins,
        Complex[] equalizers,
        ref int bitIndex,
        double[] llrs,
        double noiseVariance,
        bool addToExisting,
        int interleaveInitSeed)
    {
        PrepareSymbolFrequency(
            symbolWithCp,
            pilotBins,
            useRightChannel,
            agcState,
            timeNoCp,
            freqBins,
            equalizers);
        EmitSymbolSoftLlrsFromPrepared(
            freqBins,
            equalizers,
            useRightChannel,
            logical,
            ref bitIndex,
            llrs,
            noiseVariance,
            addToExisting,
            interleaveInitSeed,
            onEqualizedDataSymbol: null,
            onEqualizedDataSymbolFrame: null);
    }

    /// <summary>
    /// EmitSymbolSoftLlrsFromPrepared を実行します。
    /// </summary>
    /// <param name="freqBins">freqBins を指定します。</param>
    /// <param name="equalizers">equalizers を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。true で有効です。</param>
    /// <param name="logical">logical を指定します。</param>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="llrs">llrs を指定します。</param>
    /// <param name="noiseVariance">noiseVariance を指定します。</param>
    /// <param name="addToExisting">addToExisting を指定します。</param>
    /// <param name="interleaveInitSeed">interleaveInitSeed を指定します。</param>
    /// <param name="onEqualizedDataSymbol">onEqualizedDataSymbol を指定します。</param>
    /// <param name="onEqualizedDataSymbolFrame">onEqualizedDataSymbolFrame を指定します。</param>
    private void EmitSymbolSoftLlrsFromPrepared(
        Complex[] freqBins,
        Complex[] equalizers,
        bool useRightChannel,
        long logical,
        ref int bitIndex,
        double[] llrs,
        double noiseVariance,
        bool addToExisting,
        int interleaveInitSeed,
        Action<Complex>? onEqualizedDataSymbol,
        Action<Complex[], int>? onEqualizedDataSymbolFrame)
    {
        var dataOrder = ResolveDataCarrierOrder(useRightChannel, logical, interleaveInitSeed);
        var dataModulationByBin = useRightChannel
            ? _rightDataCarrierModulationByBin
            : _leftDataCarrierModulationByBin;
        Span<double> softLlrScratch = stackalloc double[6];
        Complex[]? frameSnapshot = onEqualizedDataSymbolFrame is null ? null : new Complex[dataOrder.Length];
        var frameCount = 0;
        foreach (var dataBin in dataOrder)
        {
            if (bitIndex >= llrs.Length)
            {
                break;
            }

            var equalized = freqBins[dataBin] * equalizers[dataBin];
            onEqualizedDataSymbol?.Invoke(equalized);
            if (frameSnapshot is not null)
            {
                frameSnapshot[frameCount++] = equalized;
            }

            if (addToExisting)
            {
                var tmpIndex = 0;
                EmitSymbolSoftLlrs(
                    equalized,
                    dataModulationByBin[dataBin],
                    ref tmpIndex,
                    softLlrScratch,
                    noiseVariance);
                for (var i = 0; i < tmpIndex && (bitIndex + i) < llrs.Length; i++)
                {
                    llrs[bitIndex + i] += softLlrScratch[i];
                }

                bitIndex += tmpIndex;
            }
            else
            {
                EmitSymbolSoftLlrs(
                    equalized,
                    dataModulationByBin[dataBin],
                    ref bitIndex,
                    llrs,
                    noiseVariance);
            }
        }

        if (frameSnapshot is not null && frameCount > 0)
        {
            onEqualizedDataSymbolFrame?.Invoke(frameSnapshot, frameCount);
        }
    }

    /// <summary>
    /// AccumulatePilotNoise を実行します。
    /// </summary>
    /// <param name="symbolWithCp">symbolWithCp を指定します。</param>
    /// <param name="pilotBins">pilotBins を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。true で有効です。</param>
    /// <param name="timeNoCp">timeNoCp を指定します。</param>
    /// <param name="freqBins">freqBins を指定します。</param>
    /// <param name="equalizers">equalizers を指定します。</param>
    /// <param name="noiseAccum">noiseAccum を指定します。</param>
    /// <param name="noiseCount">noiseCount を指定します。</param>
    private void AccumulatePilotNoise(
        ReadOnlySpan<Complex> symbolWithCp,
        List<int> pilotBins,
        bool useRightChannel,
        Complex[] timeNoCp,
        Complex[] freqBins,
        Complex[] equalizers,
        ref double noiseAccum,
        ref int noiseCount)
    {
        PrepareSymbolFrequency(
            symbolWithCp,
            pilotBins,
            useRightChannel,
            agcState: null,
            timeNoCp,
            freqBins,
            equalizers);
        AccumulatePilotNoiseFromPrepared(freqBins, equalizers, pilotBins, ref noiseAccum, ref noiseCount);
    }

    /// <summary>
    /// AccumulatePilotNoiseFromPrepared を実行します。
    /// </summary>
    /// <param name="freqBins">freqBins を指定します。</param>
    /// <param name="equalizers">equalizers を指定します。</param>
    /// <param name="pilotBins">pilotBins を指定します。</param>
    /// <param name="noiseAccum">noiseAccum を指定します。</param>
    /// <param name="noiseCount">noiseCount を指定します。</param>
    private static void AccumulatePilotNoiseFromPrepared(
        Complex[] freqBins,
        Complex[] equalizers,
        List<int> pilotBins,
        ref double noiseAccum,
        ref int noiseCount)
    {
        foreach (var pilotBin in pilotBins)
        {
            var eq = freqBins[pilotBin] * equalizers[pilotBin];
            var err = eq - PilotSymbol;
            noiseAccum += 0.5 * ((err.Real * err.Real) + (err.Imaginary * err.Imaginary));
            noiseCount++;
        }
    }

    /// <summary>
    /// タイミング追従しながら指定シンボル数を読み飛ばします。
    /// </summary>
    /// <param name="samples">入力サンプル列。</param>
    /// <param name="cursor">読み取りカーソル（更新あり）。</param>
    /// <param name="symbolCount">読み飛ばすシンボル数。</param>
    /// <param name="useRightChannel">右チャネルを使う場合 true。</param>
    /// <param name="searchRadius">シンボル開始探索半径。</param>
    public void SkipSymbolsWithTimingTracking(
        Complex[] samples,
        ref int cursor,
        int symbolCount,
        bool useRightChannel = false,
        int searchRadius = 24)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (symbolCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(symbolCount));
        }

        var symbolLength = SamplesPerOfdmSymbol;
        for (var s = 0; s < symbolCount; s++)
        {
            var start = FindBestSymbolStart(samples, cursor, searchRadius, useRightChannel);
            if (start + symbolLength > samples.Length)
            {
                throw new InvalidDataException("WAV ended while skipping OFDM preamble symbols.");
            }

            cursor = start + symbolLength;
        }
    }

    /// <summary>
    /// OFDM シンボル列からハード判定ビットを復調します。
    /// </summary>
    /// <param name="samples">OFDM シンボル列（CP 付き）。</param>
    /// <param name="bitCount">取得するビット数。</param>
    /// <param name="useRightChannel">右チャネルを使う場合 true。</param>
    /// <param name="absoluteSampleOffset">絶対サンプルオフセット。</param>
    /// <param name="interleaveInitSeed">インタリーブ初期シード。</param>
    /// <returns>復調したビット列。</returns>
    public bool[] DemodulateBits(
        ReadOnlySpan<Complex> samples,
        int bitCount,
        bool useRightChannel = false,
        long absoluteSampleOffset = 0,
        int interleaveInitSeed = 0)
    {
        if (bitCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        }

        if (useRightChannel && _config.ChannelMode != ChannelMode.Stereo)
        {
            throw new InvalidOperationException("Right channel demodulation requires stereo mode.");
        }

        var pilotBins = useRightChannel ? _rightPilotBins : _leftPilotBins;
        var dataModulationByBin = useRightChannel
            ? _rightDataCarrierModulationByBin
            : _leftDataCarrierModulationByBin;

        var symbolLength = SamplesPerOfdmSymbol;
        if (samples.Length % symbolLength != 0)
        {
            throw new ArgumentException("Sample length must be a multiple of OFDM symbol length.", nameof(samples));
        }

        var bits = new bool[bitCount];
        var bitIndex = 0;
        var symbolCount = samples.Length / symbolLength;
        var agcState = new PilotGroupAgcState(pilotBins.Count);
        var fftSize = _config.FftSize;
        var timeNoCp = new Complex[fftSize];
        var freqBins = new Complex[fftSize];
        var equalizers = new Complex[fftSize];

        for (var s = 0; s < symbolCount && bitIndex < bitCount; s++)
        {
            _ = absoluteSampleOffset;
            var symbolOffset = (long)s * symbolLength;
            var dataOrder = ResolveDataCarrierOrder(useRightChannel, symbolOffset, interleaveInitSeed);

            var symbol = samples.Slice(s * symbolLength, symbolLength);
            PrepareSymbolFrequency(
                symbol,
                pilotBins,
                useRightChannel,
                agcState,
                timeNoCp,
                freqBins,
                equalizers);

            foreach (var dataBin in dataOrder)
            {
                if (bitIndex >= bitCount)
                {
                    break;
                }

                EmitSymbolBits(
                    freqBins[dataBin] * equalizers[dataBin],
                    dataModulationByBin[dataBin],
                    ref bitIndex,
                    bits);
            }
        }

        return bits;
    }

    /// <summary>
    /// EstimatePilotEqualizers を実行します。
    /// </summary>
    /// <param name="freqBins">freqBins を指定します。</param>
    /// <param name="pilotBins">pilotBins を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。true で有効です。</param>
    /// <param name="null">null を指定します。</param>
    /// <returns>処理結果。</returns>
    private Complex[] EstimatePilotEqualizers(
        Complex[] freqBins,
        List<int> pilotBins,
        bool useRightChannel,
        PilotGroupAgcState? agcState = null)
    {
        var equalizers = new Complex[freqBins.Length];
        EstimatePilotEqualizersInto(freqBins, pilotBins, useRightChannel, equalizers, agcState);
        return equalizers;
    }

    /// <summary>
    /// EstimatePilotEqualizersInto を実行します。
    /// </summary>
    /// <param name="freqBins">freqBins を指定します。</param>
    /// <param name="pilotBins">pilotBins を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。true で有効です。</param>
    /// <param name="equalizers">equalizers を指定します。</param>
    /// <param name="null">null を指定します。</param>
    private void EstimatePilotEqualizersInto(
        Complex[] freqBins,
        List<int> pilotBins,
        bool useRightChannel,
        Complex[] equalizers,
        PilotGroupAgcState? agcState = null)
    {
        if (equalizers.Length < freqBins.Length)
        {
            throw new ArgumentException("Equalizer buffer is smaller than FFT bins.", nameof(equalizers));
        }

        Array.Fill(equalizers, Complex.One);
        if (pilotBins.Count == 0)
        {
            return;
        }

        var orderedPilots = useRightChannel ? _rightPilotBins : _leftPilotBins;
        var groupedCarriers = useRightChannel ? _rightPilotGroupedCarriers : _leftPilotGroupedCarriers;
        var allCarriers = useRightChannel ? _rightAllCarrierBins : _leftAllCarrierBins;
        var noisePower = EstimateNoisePower(freqBins, orderedPilots, allCarriers);
        var regularization = Math.Max(noisePower * 0.10, 1e-4);

        for (var i = 0; i < orderedPilots.Count; i++)
        {
            var h = freqBins[orderedPilots[i]];
            var mag2 = (h.Real * h.Real) + (h.Imaginary * h.Imaginary);
            if (mag2 < 1e-18)
            {
                continue;
            }

            var groupEq = Complex.Conjugate(h) / (mag2 + regularization);
            var pass = h * groupEq;
            var passMag2 = (pass.Real * pass.Real) + (pass.Imaginary * pass.Imaginary);
            if (passMag2 > 1e-18)
            {
                groupEq *= Complex.One / pass;
            }

            if (agcState is not null)
            {
                groupEq = agcState.Update(i, groupEq);
            }

            foreach (var carrier in groupedCarriers[i])
            {
                equalizers[carrier] = groupEq;
            }
        }
    }

    /// <summary>
    /// ResolvePilotGroupIndex を解決します。
    /// </summary>
    /// <param name="carrierBin">carrierBin を指定します。</param>
    /// <param name="orderedPilots">orderedPilots を指定します。</param>
    /// <returns>処理結果。</returns>
    private static int ResolvePilotGroupIndex(int carrierBin, List<int> orderedPilots)
    {
        if (orderedPilots.Count <= 1)
        {
            return 0;
        }

        if (carrierBin <= orderedPilots[0])
        {
            return 0;
        }

        for (var i = 0; i < orderedPilots.Count - 1; i++)
        {
            var mid = (orderedPilots[i] + orderedPilots[i + 1]) / 2;
            if (carrierBin <= mid)
            {
                return i;
            }
        }

        return orderedPilots.Count - 1;
    }

    private sealed class PilotGroupAgcState
    {
        private readonly Complex[] _smoothed;
        private readonly bool[] _initialized;

        /// <summary>
        /// 内部処理です。
        /// </summary>
        public PilotGroupAgcState(int groupCount)
        {
            var size = Math.Max(1, groupCount);
            _smoothed = new Complex[size];
            _initialized = new bool[size];
        }

        /// <summary>
        /// Update を実行します。
        /// </summary>
        public Complex Update(int groupIndex, Complex instantaneous)
        {
            if ((uint)groupIndex >= (uint)_smoothed.Length)
            {
                return instantaneous;
            }

            if (!_initialized[groupIndex])
            {
                _smoothed[groupIndex] = instantaneous;
                _initialized[groupIndex] = true;
                return instantaneous;
            }

            const double alpha = 0.30;
            _smoothed[groupIndex] = (_smoothed[groupIndex] * (1.0 - alpha)) + (instantaneous * alpha);
            return _smoothed[groupIndex];
        }
    }

    /// <summary>
    /// InterpolatePilotChannel を実行します。
    /// </summary>
    /// <param name="bin">bin を指定します。</param>
    /// <param name="orderedPilots">orderedPilots を指定します。</param>
    /// <param name="channels">channels を指定します。</param>
    /// <returns>処理結果。</returns>
    private static Complex InterpolatePilotChannel(int bin, List<int> orderedPilots, Complex[] channels)
    {
        if (orderedPilots.Count == 1)
        {
            return channels[0];
        }

        if (bin <= orderedPilots[0])
        {
            return channels[0];
        }

        if (bin >= orderedPilots[^1])
        {
            return channels[^1];
        }

        for (var i = 0; i < orderedPilots.Count - 1; i++)
        {
            var left = orderedPilots[i];
            var right = orderedPilots[i + 1];
            if (bin < left || bin > right)
            {
                continue;
            }

            if (bin == left)
            {
                return channels[i];
            }

            if (bin == right)
            {
                return channels[i + 1];
            }

            var t = (bin - left) / (double)(right - left);
            return channels[i] + ((channels[i + 1] - channels[i]) * t);
        }

        return channels[0];
    }

    /// <summary>
    /// EstimateNoisePower を実行します。
    /// </summary>
    /// <param name="freqBins">freqBins を指定します。</param>
    /// <param name="pilotBins">pilotBins を指定します。</param>
    /// <param name="allCarriers">allCarriers を指定します。</param>
    /// <returns>処理結果。</returns>
    private static double EstimateNoisePower(Complex[] freqBins, List<int> pilotBins, List<int> allCarriers)
    {
        var carrierSet = allCarriers.ToHashSet();
        foreach (var pilot in pilotBins)
        {
            carrierSet.Add(pilot);
        }

        var energy = 0.0;
        var count = 0;
        var half = freqBins.Length / 2;
        for (var bin = 1; bin < half; bin++)
        {
            if (carrierSet.Contains(bin))
            {
                continue;
            }

            var v = freqBins[bin];
            energy += (v.Real * v.Real) + (v.Imaginary * v.Imaginary);
            count++;
        }

        if (pilotBins.Count > 0)
        {
            var mean = Complex.Zero;
            foreach (var p in pilotBins)
            {
                mean += freqBins[p];
            }

            mean /= pilotBins.Count;
            foreach (var p in pilotBins)
            {
                var d = freqBins[p] - mean;
                energy += (d.Real * d.Real) + (d.Imaginary * d.Imaginary);
                count++;
            }
        }

        if (count == 0)
        {
            return 1e-4;
        }

        return Math.Max(energy / count, 1e-4);
    }

    /// <summary>
    /// SmoothEqualizers を実行します。
    /// </summary>
    /// <param name="previous">previous を指定します。</param>
    /// <param name="current">current を指定します。</param>
    /// <returns>処理結果。</returns>
    private static Complex[] SmoothEqualizers(ref Complex[]? previous, Complex[] current)
    {
        if (previous is null || previous.Length != current.Length)
        {
            previous = (Complex[])current.Clone();
            return current;
        }

        const double alpha = 0.35; // 現在シンボルを強めに採用する指数移動平均
        var mixed = new Complex[current.Length];
        for (var i = 0; i < current.Length; i++)
        {
            mixed[i] = (current[i] * alpha) + (previous[i] * (1.0 - alpha));
        }

        previous = mixed;
        return mixed;
    }

    /// <summary>
    /// EstimatePilotEqualizer を実行します。
    /// </summary>
    /// <param name="freqBins">freqBins を指定します。</param>
    /// <param name="pilotBins">pilotBins を指定します。</param>
    /// <returns>処理結果。</returns>
    private static Complex EstimatePilotEqualizer(Complex[] freqBins, List<int> pilotBins)
    {
        if (pilotBins.Count == 0)
        {
            return Complex.One;
        }

        var sum = Complex.Zero;
        foreach (var pilotBin in pilotBins)
        {
            sum += freqBins[pilotBin];
        }

        var average = sum / pilotBins.Count;
        var magnitudeSquared = (average.Real * average.Real) + (average.Imaginary * average.Imaginary);
        if (magnitudeSquared < 1e-18)
        {
            return Complex.One;
        }

        // received 竕・PilotSymbol * gain = 1 * gain 竊・equalizer = 1/gain
        return Complex.One / average;
    }

    /// <summary>
    /// EmitSymbolBits を実行します。
    /// </summary>
    /// <param name="symbol">symbol を指定します。</param>
    /// <param name="modulationScheme">modulationScheme を指定します。</param>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="bits">bits を指定します。</param>
    private static void EmitSymbolBits(
        Complex symbol,
        ModulationScheme modulationScheme,
        ref int bitIndex,
        bool[] bits)
    {
        switch (modulationScheme)
        {
            case ModulationScheme.Bpsk:
                WriteBit(ref bitIndex, bits, symbol.Real >= 0.0);
                break;
            case ModulationScheme.Qpsk:
                WriteBit(ref bitIndex, bits, symbol.Real >= 0.0);
                WriteBit(ref bitIndex, bits, symbol.Imaginary >= 0.0);
                break;
            case ModulationScheme.Qam16:
                EmitPamAxisBits(symbol.Real * Math.Sqrt(10.0), bitsPerAxis: 2, Qam16Levels, ref bitIndex, bits);
                EmitPamAxisBits(symbol.Imaginary * Math.Sqrt(10.0), bitsPerAxis: 2, Qam16Levels, ref bitIndex, bits);
                break;
            case ModulationScheme.Qam64:
                EmitPamAxisBits(symbol.Real * Math.Sqrt(42.0), bitsPerAxis: 3, Qam64Levels, ref bitIndex, bits);
                EmitPamAxisBits(symbol.Imaginary * Math.Sqrt(42.0), bitsPerAxis: 3, Qam64Levels, ref bitIndex, bits);
                break;
            default:
                throw new NotSupportedException("Demodulation for this scheme is not implemented yet.");
        }
    }

    /// <summary>
    /// EmitSymbolSoftLlrs を実行します。
    /// </summary>
    /// <param name="symbol">symbol を指定します。</param>
    /// <param name="modulationScheme">modulationScheme を指定します。</param>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="llrs">llrs を指定します。</param>
    /// <param name="noiseVariance">noiseVariance を指定します。</param>
    private static void EmitSymbolSoftLlrs(
        Complex symbol,
        ModulationScheme modulationScheme,
        ref int bitIndex,
        Span<double> llrs,
        double noiseVariance)
    {
        var invVar = 1.0 / Math.Max(1e-6, noiseVariance);
        switch (modulationScheme)
        {
            case ModulationScheme.Bpsk:
                WriteLlr(ref bitIndex, llrs, 2.0 * symbol.Real * invVar);
                break;
            case ModulationScheme.Qpsk:
                WriteLlr(ref bitIndex, llrs, 2.0 * symbol.Real * Math.Sqrt(2.0) * invVar);
                WriteLlr(ref bitIndex, llrs, 2.0 * symbol.Imaginary * Math.Sqrt(2.0) * invVar);
                break;
            case ModulationScheme.Qam16:
                EmitPamAxisSoftLlrsCore(symbol.Real * Math.Sqrt(10.0), bitsPerAxis: 2, Qam16Levels, invVar, ref bitIndex, llrs);
                EmitPamAxisSoftLlrsCore(symbol.Imaginary * Math.Sqrt(10.0), bitsPerAxis: 2, Qam16Levels, invVar, ref bitIndex, llrs);
                break;
            case ModulationScheme.Qam64:
                EmitPamAxisSoftLlrsCore(symbol.Real * Math.Sqrt(42.0), bitsPerAxis: 3, Qam64Levels, invVar, ref bitIndex, llrs);
                EmitPamAxisSoftLlrsCore(symbol.Imaginary * Math.Sqrt(42.0), bitsPerAxis: 3, Qam64Levels, invVar, ref bitIndex, llrs);
                break;
            default:
                throw new NotSupportedException("Soft demodulation for this scheme is not implemented yet.");
        }
    }

    /// <summary>
    /// EmitPamAxisSoftLlrs を実行します。
    /// </summary>
    /// <param name="amplitude">PAM 振幅値。</param>
    /// <param name="bitsPerAxis">bitsPerAxis を指定します。</param>
    /// <param name="levels">levels を指定します。</param>
    /// <param name="invVariance">invVariance を指定します。</param>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="llrs">llrs を指定します。</param>
    private static void EmitPamAxisSoftLlrs(
        double amplitude,
        int bitsPerAxis,
        int[] levels,
        double invVariance,
        ref int bitIndex,
        double[] llrs)
    {
        EmitPamAxisSoftLlrsCore(amplitude, bitsPerAxis, levels, invVariance, ref bitIndex, llrs.AsSpan());
    }

    /// <summary>
    /// EmitPamAxisSoftLlrsCore を実行します。
    /// </summary>
    /// <param name="amplitude">PAM 振幅値。</param>
    /// <param name="bitsPerAxis">bitsPerAxis を指定します。</param>
    /// <param name="levels">levels を指定します。</param>
    /// <param name="invVariance">invVariance を指定します。</param>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="llrs">llrs を指定します。</param>
    private static void EmitPamAxisSoftLlrsCore(
        double amplitude,
        int bitsPerAxis,
        int[] levels,
        double invVariance,
        ref int bitIndex,
        Span<double> llrs)
    {
        if (Avx.IsSupported && bitsPerAxis == 2 && levels.Length == 4)
        {
            EmitPamAxisSoftLlrsQam16Avx(amplitude, invVariance, ref bitIndex, llrs);
            return;
        }

        if (AdvSimd.Arm64.IsSupported && bitsPerAxis == 2 && levels.Length == 4)
        {
            EmitPamAxisSoftLlrsQam16Arm64(amplitude, invVariance, ref bitIndex, llrs);
            return;
        }

        if (Avx.IsSupported && bitsPerAxis == 3 && levels.Length == 8)
        {
            EmitPamAxisSoftLlrsQam64Avx(amplitude, invVariance, ref bitIndex, llrs);
            return;
        }

        if (AdvSimd.Arm64.IsSupported && bitsPerAxis == 3 && levels.Length == 8)
        {
            EmitPamAxisSoftLlrsQam64Arm64(amplitude, invVariance, ref bitIndex, llrs);
            return;
        }

        var mask = (1 << bitsPerAxis) - 1;
        for (var bit = bitsPerAxis - 1; bit >= 0; bit--)
        {
            var minDist0 = double.PositiveInfinity;
            var minDist1 = double.PositiveInfinity;
            for (var binary = 0; binary <= mask; binary++)
            {
                var gray = (binary ^ (binary >> 1)) & mask;
                var level = levels[gray];
                var dist = amplitude - level;
                var dist2 = dist * dist;
                if (((binary >> bit) & 1) == 0)
                {
                    minDist0 = Math.Min(minDist0, dist2);
                }
                else
                {
                    minDist1 = Math.Min(minDist1, dist2);
                }
            }

            WriteLlr(ref bitIndex, llrs, 0.5 * (minDist0 - minDist1) * invVariance);
        }
    }

    /// <summary>
    /// EmitPamAxisSoftLlrsQam16Avx を実行します。
    /// </summary>
    /// <param name="amplitude">PAM 振幅値。</param>
    /// <param name="invVariance">invVariance を指定します。</param>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="llrs">llrs を指定します。</param>
    private static void EmitPamAxisSoftLlrsQam16Avx(
        double amplitude,
        double invVariance,
        ref int bitIndex,
        Span<double> llrs)
    {
        var amp = Vector256.Create(amplitude);
        var levels = Vector256.Create(
            Qam16PamByBinary[0],
            Qam16PamByBinary[1],
            Qam16PamByBinary[2],
            Qam16PamByBinary[3]);
        var diff = Avx.Subtract(amp, levels);
        var dist2 = Avx.Multiply(diff, diff);

        var d0 = dist2.GetElement(0);
        var d1 = dist2.GetElement(1);
        var d2 = dist2.GetElement(2);
        var d3 = dist2.GetElement(3);

        // bit1 (MSB): 0/1 vs 2/3
        var min10 = Math.Min(d0, d1);
        var min11 = Math.Min(d2, d3);
        WriteLlr(ref bitIndex, llrs, 0.5 * (min10 - min11) * invVariance);

        // bit0 (LSB): 0/2 vs 1/3
        var min00 = Math.Min(d0, d2);
        var min01 = Math.Min(d1, d3);
        WriteLlr(ref bitIndex, llrs, 0.5 * (min00 - min01) * invVariance);
    }

    /// <summary>
    /// EmitPamAxisSoftLlrsQam64Avx を実行します。
    /// </summary>
    /// <param name="amplitude">PAM 振幅値。</param>
    /// <param name="invVariance">invVariance を指定します。</param>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="llrs">llrs を指定します。</param>
    private static void EmitPamAxisSoftLlrsQam64Avx(
        double amplitude,
        double invVariance,
        ref int bitIndex,
        Span<double> llrs)
    {
        var amp = Vector256.Create(amplitude);
        var levelsLo = Vector256.Create(
            Qam64PamByBinary[0],
            Qam64PamByBinary[1],
            Qam64PamByBinary[2],
            Qam64PamByBinary[3]);
        var levelsHi = Vector256.Create(
            Qam64PamByBinary[4],
            Qam64PamByBinary[5],
            Qam64PamByBinary[6],
            Qam64PamByBinary[7]);
        var diffLo = Avx.Subtract(amp, levelsLo);
        var diffHi = Avx.Subtract(amp, levelsHi);
        var distLo = Avx.Multiply(diffLo, diffLo);
        var distHi = Avx.Multiply(diffHi, diffHi);

        var d0 = distLo.GetElement(0);
        var d1 = distLo.GetElement(1);
        var d2 = distLo.GetElement(2);
        var d3 = distLo.GetElement(3);
        var d4 = distHi.GetElement(0);
        var d5 = distHi.GetElement(1);
        var d6 = distHi.GetElement(2);
        var d7 = distHi.GetElement(3);

        // bit2 (MSB): 0..3 vs 4..7
        var min20 = Math.Min(Math.Min(d0, d1), Math.Min(d2, d3));
        var min21 = Math.Min(Math.Min(d4, d5), Math.Min(d6, d7));
        WriteLlr(ref bitIndex, llrs, 0.5 * (min20 - min21) * invVariance);

        // bit1: 0,1,4,5 vs 2,3,6,7
        var min10 = Math.Min(Math.Min(d0, d1), Math.Min(d4, d5));
        var min11 = Math.Min(Math.Min(d2, d3), Math.Min(d6, d7));
        WriteLlr(ref bitIndex, llrs, 0.5 * (min10 - min11) * invVariance);

        // bit0 (LSB): 0,2,4,6 vs 1,3,5,7
        var min00 = Math.Min(Math.Min(d0, d2), Math.Min(d4, d6));
        var min01 = Math.Min(Math.Min(d1, d3), Math.Min(d5, d7));
        WriteLlr(ref bitIndex, llrs, 0.5 * (min00 - min01) * invVariance);
    }

    /// <summary>
    /// EmitPamAxisSoftLlrsQam16Arm64 を実行します。
    /// </summary>
    /// <param name="amplitude">PAM 振幅値。</param>
    /// <param name="invVariance">invVariance を指定します。</param>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="llrs">llrs を指定します。</param>
    private static void EmitPamAxisSoftLlrsQam16Arm64(
        double amplitude,
        double invVariance,
        ref int bitIndex,
        Span<double> llrs)
    {
        var amp = Vector128.Create(amplitude);
        var levels01 = Vector128.Create(Qam16PamByBinary[0], Qam16PamByBinary[1]);
        var levels23 = Vector128.Create(Qam16PamByBinary[2], Qam16PamByBinary[3]);
        var diff01 = AdvSimd.Arm64.Subtract(amp, levels01);
        var diff23 = AdvSimd.Arm64.Subtract(amp, levels23);
        var dist01 = AdvSimd.Arm64.Multiply(diff01, diff01);
        var dist23 = AdvSimd.Arm64.Multiply(diff23, diff23);

        var d0 = dist01.GetElement(0);
        var d1 = dist01.GetElement(1);
        var d2 = dist23.GetElement(0);
        var d3 = dist23.GetElement(1);

        var min10 = Math.Min(d0, d1);
        var min11 = Math.Min(d2, d3);
        WriteLlr(ref bitIndex, llrs, 0.5 * (min10 - min11) * invVariance);

        var min00 = Math.Min(d0, d2);
        var min01 = Math.Min(d1, d3);
        WriteLlr(ref bitIndex, llrs, 0.5 * (min00 - min01) * invVariance);
    }

    /// <summary>
    /// EmitPamAxisSoftLlrsQam64Arm64 を実行します。
    /// </summary>
    /// <param name="amplitude">PAM 振幅値。</param>
    /// <param name="invVariance">invVariance を指定します。</param>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="llrs">llrs を指定します。</param>
    private static void EmitPamAxisSoftLlrsQam64Arm64(
        double amplitude,
        double invVariance,
        ref int bitIndex,
        Span<double> llrs)
    {
        var amp = Vector128.Create(amplitude);

        var levels01 = Vector128.Create(Qam64PamByBinary[0], Qam64PamByBinary[1]);
        var levels23 = Vector128.Create(Qam64PamByBinary[2], Qam64PamByBinary[3]);
        var levels45 = Vector128.Create(Qam64PamByBinary[4], Qam64PamByBinary[5]);
        var levels67 = Vector128.Create(Qam64PamByBinary[6], Qam64PamByBinary[7]);

        var dist01 = AdvSimd.Arm64.Multiply(
            AdvSimd.Arm64.Subtract(amp, levels01),
            AdvSimd.Arm64.Subtract(amp, levels01));
        var dist23 = AdvSimd.Arm64.Multiply(
            AdvSimd.Arm64.Subtract(amp, levels23),
            AdvSimd.Arm64.Subtract(amp, levels23));
        var dist45 = AdvSimd.Arm64.Multiply(
            AdvSimd.Arm64.Subtract(amp, levels45),
            AdvSimd.Arm64.Subtract(amp, levels45));
        var dist67 = AdvSimd.Arm64.Multiply(
            AdvSimd.Arm64.Subtract(amp, levels67),
            AdvSimd.Arm64.Subtract(amp, levels67));

        var d0 = dist01.GetElement(0);
        var d1 = dist01.GetElement(1);
        var d2 = dist23.GetElement(0);
        var d3 = dist23.GetElement(1);
        var d4 = dist45.GetElement(0);
        var d5 = dist45.GetElement(1);
        var d6 = dist67.GetElement(0);
        var d7 = dist67.GetElement(1);

        var min20 = Math.Min(Math.Min(d0, d1), Math.Min(d2, d3));
        var min21 = Math.Min(Math.Min(d4, d5), Math.Min(d6, d7));
        WriteLlr(ref bitIndex, llrs, 0.5 * (min20 - min21) * invVariance);

        var min10 = Math.Min(Math.Min(d0, d1), Math.Min(d4, d5));
        var min11 = Math.Min(Math.Min(d2, d3), Math.Min(d6, d7));
        WriteLlr(ref bitIndex, llrs, 0.5 * (min10 - min11) * invVariance);

        var min00 = Math.Min(Math.Min(d0, d2), Math.Min(d4, d6));
        var min01 = Math.Min(Math.Min(d1, d3), Math.Min(d5, d7));
        WriteLlr(ref bitIndex, llrs, 0.5 * (min00 - min01) * invVariance);
    }

    /// <summary>
    /// BuildPamByBinary を構築します。
    /// </summary>
    /// <param name="bitsPerAxis">bitsPerAxis を指定します。</param>
    /// <param name="levels">levels を指定します。</param>
    /// <returns>処理結果。</returns>
    private static double[] BuildPamByBinary(int bitsPerAxis, int[] levels)
    {
        var count = 1 << bitsPerAxis;
        var mapped = new double[count];
        var mask = count - 1;
        for (var binary = 0; binary < count; binary++)
        {
            var gray = (binary ^ (binary >> 1)) & mask;
            mapped[binary] = levels[gray];
        }

        return mapped;
    }

    /// <summary>
    /// WriteLlr を書き込みます。
    /// </summary>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="llrs">llrs を指定します。</param>
    /// <param name="value">value を指定します。</param>
    private static void WriteLlr(ref int bitIndex, Span<double> llrs, double value)
    {
        if (bitIndex >= llrs.Length)
        {
            return;
        }

        llrs[bitIndex++] = value;
    }

    /// <summary>
    /// EmitPamAxisBits を実行します。
    /// </summary>
    /// <param name="amplitude">PAM 振幅値。</param>
    /// <param name="bitsPerAxis">bitsPerAxis を指定します。</param>
    /// <param name="levels">levels を指定します。</param>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="bits">bits を指定します。</param>
    private static void EmitPamAxisBits(
        double amplitude,
        int bitsPerAxis,
        int[] levels,
        ref int bitIndex,
        bool[] bits)
    {
        var grayIndex = FindNearestLevelIndex(amplitude, levels);
        var binaryIndex = GrayToBinary(grayIndex) & ((1 << bitsPerAxis) - 1);
        for (var i = bitsPerAxis - 1; i >= 0; i--)
        {
            WriteBit(ref bitIndex, bits, ((binaryIndex >> i) & 1) != 0);
        }
    }

    /// <summary>
    /// FindNearestLevelIndex を実行します。
    /// </summary>
    /// <param name="amplitude">隕ｳ貂ｬ謖ｯ蟷・・/param>
    /// <param name="levels">levels を指定します。</param>
    /// <returns>処理結果。</returns>
    private static int FindNearestLevelIndex(double amplitude, int[] levels)
    {
        var best = 0;
        var bestDist = Math.Abs(amplitude - levels[0]);
        for (var i = 1; i < levels.Length; i++)
        {
            var dist = Math.Abs(amplitude - levels[i]);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// GrayToBinary を実行します。
    /// </summary>
    /// <param name="gray">gray を指定します。</param>
    /// <returns>処理結果。</returns>
    private static int GrayToBinary(int gray)
    {
        var binary = gray;
        for (var shift = gray >> 1; shift != 0; shift >>= 1)
        {
            binary ^= shift;
        }

        return binary;
    }

    /// <summary>
    /// WriteBit を書き込みます。
    /// </summary>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="bits">bits を指定します。</param>
    /// <param name="value">value を指定します。</param>
    private static void WriteBit(ref int bitIndex, bool[] bits, bool value)
    {
        if (bitIndex >= bits.Length)
        {
            return;
        }

        bits[bitIndex++] = value;
    }

    /// <summary>
    /// PrepareSymbolFrequency を実行します。
    /// </summary>
    /// <param name="symbolWithCp">symbolWithCp を指定します。</param>
    /// <param name="pilotBins">pilotBins を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。true で有効です。</param>
    /// <param name="agcState">agcState を指定します。</param>
    /// <param name="timeNoCp">timeNoCp を指定します。</param>
    /// <param name="freqBins">freqBins を指定します。</param>
    /// <param name="equalizers">equalizers を指定します。</param>
    private void PrepareSymbolFrequency(
        ReadOnlySpan<Complex> symbolWithCp,
        List<int> pilotBins,
        bool useRightChannel,
        PilotGroupAgcState? agcState,
        Complex[] timeNoCp,
        Complex[] freqBins,
        Complex[] equalizers)
    {
        symbolWithCp.Slice(_config.CyclicPrefixLength, _config.FftSize).CopyTo(timeNoCp);
        ForwardFftMatchingInverseInto(timeNoCp, freqBins);
        EstimatePilotEqualizersInto(freqBins, pilotBins, useRightChannel, equalizers, agcState);
    }

    /// <summary>
    /// RemoveCyclicPrefix を実行します。
    /// </summary>
    /// <param name="withCp">withCp を指定します。</param>
    /// <param name="cpLength">cpLength を指定します。</param>
    /// <returns>処理結果。</returns>
    private static Complex[] RemoveCyclicPrefix(ReadOnlySpan<Complex> withCp, int cpLength)
    {
        var result = new Complex[withCp.Length - cpLength];
        withCp.Slice(cpLength).CopyTo(result);
        return result;
    }

    /// <summary>
    /// ForwardFftMatchingInverse を実行します。
    /// </summary>
    /// <param name="time">time を指定します。</param>
    /// <returns>処理結果。</returns>
    private static Complex[] ForwardFftMatchingInverse(Complex[] time)
    {
        var freq = new Complex[time.Length];
        ForwardFftMatchingInverseInto(time, freq);
        return freq;
    }

    /// <summary>
    /// ForwardFftMatchingInverseInto を実行します。
    /// </summary>
    /// <param name="time">time を指定します。</param>
    /// <param name="destination">destination を指定します。</param>
    private static void ForwardFftMatchingInverseInto(ReadOnlySpan<Complex> time, Complex[] destination)
    {
        if (destination.Length < time.Length)
        {
            throw new ArgumentException("FFT destination buffer is smaller than input.", nameof(destination));
        }

        time.CopyTo(destination);
        FftInPlace(destination);
    }

    /// <summary>
    /// BuildFrequencyDomainSymbol を構築します。
    /// </summary>
    /// <param name="channel">channel を指定します。</param>
    /// <returns>処理結果。</returns>
    private Complex[] BuildFrequencyDomainSymbol(CarrierChannel channel)
    {
        var bins = new Complex[_config.FftSize];
        var carrierBins = GetActiveCarrierBins(channel);
        var pilotBins = SelectPilotBins(carrierBins, _config.PilotSpacing);
        var dataBins = carrierBins.Where(bin => !pilotBins.Contains(bin)).ToList();
        var dataModulationByBin = channel == CarrierChannel.Right
            ? _rightDataCarrierModulationByBin
            : _leftDataCarrierModulationByBin;

        if (_config.EnableFrequencyInterleaving)
        {
            ShuffleInPlace(dataBins);
        }

        foreach (var pilotBin in pilotBins)
        {
            bins[pilotBin] = PilotSymbol;
        }

        foreach (var dataBin in dataBins)
        {
            bins[dataBin] = GenerateModulatedSymbol(dataModulationByBin[dataBin]);
        }

        return bins;
    }

    /// <summary>
    /// ConsumeModulatedSymbol を実行します。
    /// </summary>
    /// <param name="modulationScheme">modulationScheme を指定します。</param>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="bits">bits を指定します。</param>
    /// <returns>処理結果。</returns>
    private static Complex ConsumeModulatedSymbol(
        ModulationScheme modulationScheme,
        ref int bitIndex,
        ReadOnlySpan<bool> bits)
    {
        return modulationScheme switch
        {
            ModulationScheme.Bpsk => ConsumeBpskSymbol(ref bitIndex, bits),
            ModulationScheme.Qpsk => ConsumeQpskSymbol(ref bitIndex, bits),
            ModulationScheme.Qam16 => ConsumeQam16Symbol(ref bitIndex, bits),
            ModulationScheme.Qam64 => ConsumeQam64Symbol(ref bitIndex, bits),
            _ => throw new InvalidOperationException("Unsupported modulation scheme.")
        };
    }

    /// <summary>
    /// ReadBitOrZero を読み取ります。
    /// </summary>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="bits">bits を指定します。</param>
    /// <returns>判定結果。</returns>
    private static bool ReadBitOrZero(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        if (bitIndex >= bits.Length)
        {
            bitIndex++;
            return false;
        }

        return bits[bitIndex++];
    }

    /// <summary>
    /// ReadBitField を読み取ります。
    /// </summary>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="bits">bits を指定します。</param>
    /// <param name="bitCount">bitCount を指定します。</param>
    /// <returns>処理結果。</returns>
    private static int ReadBitField(ref int bitIndex, ReadOnlySpan<bool> bits, int bitCount)
    {
        var value = 0;
        for (var i = 0; i < bitCount; i++)
        {
            value = (value << 1) | (ReadBitOrZero(ref bitIndex, bits) ? 1 : 0);
        }

        return value;
    }

    /// <summary>
    /// ConsumeBpskSymbol を実行します。
    /// </summary>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="bits">bits を指定します。</param>
    /// <returns>処理結果。</returns>
    private static Complex ConsumeBpskSymbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var bit = ReadBitOrZero(ref bitIndex, bits);
        return new Complex(bit ? 1.0 : -1.0, 0.0);
    }

    /// <summary>
    /// ConsumeQpskSymbol を実行します。
    /// </summary>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="bits">bits を指定します。</param>
    /// <returns>処理結果。</returns>
    private static Complex ConsumeQpskSymbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var iBit = ReadBitOrZero(ref bitIndex, bits);
        var qBit = ReadBitOrZero(ref bitIndex, bits);
        var real = iBit ? 1.0 : -1.0;
        var imag = qBit ? 1.0 : -1.0;
        return new Complex(real, imag) / Math.Sqrt(2.0);
    }

    /// <summary>
    /// ConsumeQam16Symbol を実行します。
    /// </summary>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="bits">bits を指定します。</param>
    /// <returns>処理結果。</returns>
    private static Complex ConsumeQam16Symbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var real = GrayMappedPamLevel(ReadBitField(ref bitIndex, bits, 2), bitsPerAxis: 2, Qam16Levels);
        var imag = GrayMappedPamLevel(ReadBitField(ref bitIndex, bits, 2), bitsPerAxis: 2, Qam16Levels);
        return new Complex(real, imag) / Math.Sqrt(10.0);
    }

    /// <summary>
    /// ConsumeQam64Symbol を実行します。
    /// </summary>
    /// <param name="bitIndex">bitIndex を指定します。</param>
    /// <param name="bits">bits を指定します。</param>
    /// <returns>処理結果。</returns>
    private static Complex ConsumeQam64Symbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var real = GrayMappedPamLevel(ReadBitField(ref bitIndex, bits, 3), bitsPerAxis: 3, Qam64Levels);
        var imag = GrayMappedPamLevel(ReadBitField(ref bitIndex, bits, 3), bitsPerAxis: 3, Qam64Levels);
        return new Complex(real, imag) / Math.Sqrt(42.0);
    }

    /// <summary>
    /// GrayMappedPamLevel を実行します。
    /// </summary>
    /// <param name="binaryIndex">binaryIndex を指定します。</param>
    /// <param name="bitsPerAxis">bitsPerAxis を指定します。</param>
    /// <param name="levels">levels を指定します。</param>
    /// <returns>処理結果。</returns>
    private static int GrayMappedPamLevel(int binaryIndex, int bitsPerAxis, int[] levels)
    {
        var grayIndex = (binaryIndex ^ (binaryIndex >> 1)) & ((1 << bitsPerAxis) - 1);
        return levels[grayIndex];
    }

    /// <summary>
    /// GetActiveCarrierBins を取得します。
    /// </summary>
    /// <param name="channel">channel を指定します。</param>
    /// <returns>処理結果。</returns>
    private List<int> GetActiveCarrierBins(CarrierChannel channel)
    {
        return GetPositiveCarrierBins(channel);
    }

    /// <summary>
    /// SelectPilotBins を実行します。
    /// </summary>
    /// <param name="orderedBins">orderedBins を指定します。</param>
    /// <param name="spacing">spacing を指定します。</param>
    /// <returns>処理結果。</returns>
    private static HashSet<int> SelectPilotBins(List<int> orderedBins, int spacing)
    {
        var pilots = new HashSet<int>();
        for (var start = 0; start < orderedBins.Count; start += spacing)
        {
            var length = Math.Min(spacing, orderedBins.Count - start);
            if (length <= 0)
            {
                continue;
            }

            var center = start + (length / 2);
            pilots.Add(orderedBins[center]);
        }

        return pilots;
    }

    /// <summary>
    /// ShuffleInPlace を実行します。
    /// </summary>
    /// <param name="_random">_random を指定します。</param>
    private void ShuffleInPlace(List<int> values) => ShuffleInPlace(values, _random);

    /// <summary>
    /// ShuffleInPlace を実行します。
    /// </summary>
    /// <param name="values">values を指定します。</param>
    /// <param name="random">random を指定します。</param>
    private static void ShuffleInPlace(List<int> values, Random random)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    /// <summary>
    /// ShuffleInPlace を実行します。
    /// </summary>
    /// <param name="values">values を指定します。</param>
    /// <param name="random">random を指定します。</param>
    private static void ShuffleInPlace(int[] values, Random random)
    {
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    /// <summary>
    /// GenerateModulatedSymbol を実行します。
    /// </summary>
    /// <param name="modulationScheme">modulationScheme を指定します。</param>
    /// <returns>処理結果。</returns>
    private Complex GenerateModulatedSymbol(ModulationScheme modulationScheme)
    {
        return modulationScheme switch
        {
            ModulationScheme.Bpsk => GenerateBpskSymbol(),
            ModulationScheme.Qpsk => GenerateQpskSymbol(),
            ModulationScheme.Qam16 => GenerateQam16Symbol(),
            ModulationScheme.Qam64 => GenerateQam64Symbol(),
            _ => throw new InvalidOperationException("Unsupported modulation scheme.")
        };
    }

    /// <summary>
    /// GenerateBpskSymbol を実行します。
    /// </summary>
    /// <returns>処理結果。</returns>
    private Complex GenerateBpskSymbol()
    {
        var bit = _random.Next(2);
        return new Complex(bit == 0 ? -1.0 : 1.0, 0.0);
    }

    /// <summary>
    /// GenerateQpskSymbol を実行します。
    /// </summary>
    /// <returns>処理結果。</returns>
    private Complex GenerateQpskSymbol()
    {
        var iBit = _random.Next(2);
        var qBit = _random.Next(2);

        var real = iBit == 0 ? -1.0 : 1.0;
        var imag = qBit == 0 ? -1.0 : 1.0;
        return new Complex(real, imag) / Math.Sqrt(2.0);
    }

    /// <summary>
    /// GenerateQam16Symbol を実行します。
    /// </summary>
    /// <returns>処理結果。</returns>
    private Complex GenerateQam16Symbol()
    {
        var real = GenerateGrayMappedPamLevel(bitsPerAxis: 2, Qam16Levels);
        var imag = GenerateGrayMappedPamLevel(bitsPerAxis: 2, Qam16Levels);
        return new Complex(real, imag) / Math.Sqrt(10.0);
    }

    /// <summary>
    /// GenerateQam64Symbol を実行します。
    /// </summary>
    /// <returns>処理結果。</returns>
    private Complex GenerateQam64Symbol()
    {
        var real = GenerateGrayMappedPamLevel(bitsPerAxis: 3, Qam64Levels);
        var imag = GenerateGrayMappedPamLevel(bitsPerAxis: 3, Qam64Levels);
        return new Complex(real, imag) / Math.Sqrt(42.0);
    }

    /// <summary>
    /// GenerateGrayMappedPamLevel を実行します。
    /// </summary>
    /// <param name="bitsPerAxis">bitsPerAxis を指定します。</param>
    /// <param name="levels">levels を指定します。</param>
    /// <returns>処理結果。</returns>
    private int GenerateGrayMappedPamLevel(int bitsPerAxis, int[] levels)
    {
        var binaryIndex = NextBits(bitsPerAxis);
        var grayIndex = (binaryIndex ^ (binaryIndex >> 1)) & ((1 << bitsPerAxis) - 1);
        return levels[grayIndex];
    }

    /// <summary>
    /// NextBits を実行します。
    /// </summary>
    /// <param name="bitCount">bitCount を指定します。</param>
    /// <returns>処理結果。</returns>
    private int NextBits(int bitCount)
    {
        var value = 0;
        for (var i = 0; i < bitCount; i++)
        {
            if (_randomBitCount == 0)
            {
                _randomBitPool = (ulong)_random.NextInt64();
                _randomBitCount = 63;
            }

            value = (value << 1) | (int)(_randomBitPool & 1UL);
            _randomBitPool >>= 1;
            _randomBitCount--;
        }

        return value;
    }

    /// <summary>
    /// AddCyclicPrefix を実行します。
    /// </summary>
    /// <param name="symbol">symbol を指定します。</param>
    /// <param name="cpLength">cpLength を指定します。</param>
    /// <returns>処理結果。</returns>
    private static Complex[] AddCyclicPrefix(Complex[] symbol, int cpLength)
    {
        if (cpLength == 0)
        {
            return symbol;
        }

        var result = new Complex[symbol.Length + cpLength];
        Array.Copy(symbol, symbol.Length - cpLength, result, 0, cpLength);
        Array.Copy(symbol, 0, result, cpLength, symbol.Length);
        return result;
    }

    /// <summary>
    /// EnsureIfftScratch を実行します。
    /// </summary>
    /// <param name="n">n を指定します。</param>
    private void EnsureIfftScratch(int n)
    {
        if (_ifftConjugateScratch is not null && _ifftConjugateScratch.Length == n)
        {
            return;
        }

        _ifftConjugateScratch = new Complex[n];
        _ifftWorkScratch = new Complex[n];
    }

    /// <summary>
    /// InverseFftInto を実行します。
    /// </summary>
    /// <param name="frequency">frequency を指定します。</param>
    /// <param name="destination">destination を指定します。</param>
    private void InverseFftInto(Complex[] frequency, Complex[] destination)
    {
        if (destination.Length < frequency.Length)
        {
            throw new ArgumentException("Destination is shorter than frequency bins.", nameof(destination));
        }

        EnsureIfftScratch(frequency.Length);
        var scratch = _ifftConjugateScratch!;
        ConjugateInto(frequency, scratch);
        Array.Copy(scratch, destination, frequency.Length);
        FftInPlace(destination);
        ConjugateAndScaleInPlace(destination, 1.0 / frequency.Length);
    }

    /// <summary>
    /// InverseFft を実行します。
    /// </summary>
    /// <param name="frequency">frequency を指定します。</param>
    /// <returns>処理結果。</returns>
    private Complex[] InverseFft(Complex[] frequency)
    {
        EnsureIfftScratch(frequency.Length);
        InverseFftInto(frequency, _ifftWorkScratch!);
        var result = new Complex[frequency.Length];
        Array.Copy(_ifftWorkScratch!, result, frequency.Length);
        return result;
    }

    /// <summary>
    /// CreateConjugateSignMask を生成します。
    /// </summary>
    /// <returns>処理結果。</returns>
    private static Vector<double> CreateConjugateSignMask()
    {
        var values = new double[Vector<double>.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (i & 1) == 0 ? 1.0 : -1.0;
        }

        return new Vector<double>(values);
    }

    /// <summary>
    /// ConjugateInto を実行します。
    /// </summary>
    /// <param name="source">source を指定します。</param>
    /// <param name="destination">destination を指定します。</param>
    private static void ConjugateInto(Complex[] source, Complex[] destination)
    {
        ReadOnlySpan<double> src = MemoryMarshal.Cast<Complex, double>(source.AsSpan());
        Span<double> dst = MemoryMarshal.Cast<Complex, double>(destination.AsSpan());
        var width = Vector<double>.Count;
        var i = 0;
        for (; i <= src.Length - width; i += width)
        {
            var chunk = LoadVector(src, i);
            StoreVector(dst, i, chunk * ConjugateSignMask);
        }

        for (; i < src.Length; i++)
        {
            dst[i] = (i & 1) == 0 ? src[i] : -src[i];
        }
    }

    /// <summary>
    /// ConjugateAndScaleInPlace を実行します。
    /// </summary>
    /// <param name="values">values を指定します。</param>
    /// <param name="scale">scale を指定します。</param>
    private static void ConjugateAndScaleInPlace(Complex[] values, double scale)
    {
        Span<double> data = MemoryMarshal.Cast<Complex, double>(values.AsSpan());
        var mask = ConjugateSignMask * new Vector<double>(scale);
        var width = Vector<double>.Count;
        var i = 0;
        for (; i <= data.Length - width; i += width)
        {
            var chunk = LoadVector(data, i);
            StoreVector(data, i, chunk * mask);
        }

        for (; i < data.Length; i++)
        {
            var sign = (i & 1) == 0 ? 1.0 : -1.0;
            data[i] *= sign * scale;
        }
    }

    /// <summary>
    /// LoadVector を実行します。
    /// </summary>
    /// <param name="index">開始インデックス。</param>
    /// <param name="source">source を指定します。</param>
    /// <returns>処理結果。</returns>
    private static Vector<double> LoadVector(ReadOnlySpan<double> source, int index)
    {
        ref var first = ref MemoryMarshal.GetReference(source);
        ref var at = ref Unsafe.Add(ref first, index);
        return Unsafe.ReadUnaligned<Vector<double>>(ref Unsafe.As<double, byte>(ref at));
    }

    /// <summary>
    /// LoadVector を実行します。
    /// </summary>
    /// <param name="index">開始インデックス。</param>
    /// <param name="source">source を指定します。</param>
    /// <returns>処理結果。</returns>
    private static Vector<double> LoadVector(Span<double> source, int index)
    {
        ref var first = ref MemoryMarshal.GetReference(source);
        ref var at = ref Unsafe.Add(ref first, index);
        return Unsafe.ReadUnaligned<Vector<double>>(ref Unsafe.As<double, byte>(ref at));
    }

    /// <summary>
    /// StoreVector を実行します。
    /// </summary>
    /// <param name="index">開始インデックス。</param>
    /// <param name="destination">destination を指定します。</param>
    /// <param name="value">value を指定します。</param>
    private static void StoreVector(Span<double> destination, int index, Vector<double> value)
    {
        ref var first = ref MemoryMarshal.GetReference(destination);
        ref var at = ref Unsafe.Add(ref first, index);
        Unsafe.WriteUnaligned(ref Unsafe.As<double, byte>(ref at), value);
    }

    /// <summary>
    /// Fft を実行します。
    /// </summary>
    /// <param name="input">input を指定します。</param>
    /// <returns>処理結果。</returns>
    private static Complex[] Fft(Complex[] input)
    {
        var output = new Complex[input.Length];
        Array.Copy(input, output, input.Length);
        FftInPlace(output);
        return output;
    }

    /// <summary>
    /// FftInPlace を実行します。
    /// </summary>
    /// <param name="output">output を指定します。</param>
    private static void FftInPlace(Complex[] output)
    {
        var n = output.Length;

        var bits = (int)Math.Log2(n);

        for (var i = 0; i < n; i++)
        {
            var j = ReverseBits(i, bits);
            if (j > i)
            {
                (output[i], output[j]) = (output[j], output[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2.0 * Math.PI / len;
            var wLen = Complex.FromPolarCoordinates(1.0, angle);
            var useAvx = Avx.IsSupported && len >= 4;
            var useArm64Simd = AdvSimd.Arm64.IsSupported && len >= 4;

            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                var halfLen = len >> 1;
                for (var j = 0; j < halfLen; j++)
                {
                    var upperIndex = i + j;
                    var lowerIndex = upperIndex + halfLen;

                    if (useAvx && (j + 1) < halfLen)
                    {
                        var upperIndex2 = upperIndex + 1;
                        var lowerIndex2 = lowerIndex + 1;

                        var lowerVec = Vector256.Create(
                            output[lowerIndex].Real,
                            output[lowerIndex].Imaginary,
                            output[lowerIndex2].Real,
                            output[lowerIndex2].Imaginary);

                        var w2 = w * wLen;
                        var wrVec = Vector256.Create(w.Real, w.Real, w2.Real, w2.Real);
                        var wiVec = Vector256.Create(w.Imaginary, w.Imaginary, w2.Imaginary, w2.Imaginary);
                        var swapped = Vector256.Create(
                            lowerVec.GetElement(1),
                            lowerVec.GetElement(0),
                            lowerVec.GetElement(3),
                            lowerVec.GetElement(2));
                        var signedImag = Avx.Multiply(swapped, Vector256.Create(-1.0, 1.0, -1.0, 1.0));
                        var twiddled = Avx.Add(Avx.Multiply(lowerVec, wrVec), Avx.Multiply(signedImag, wiVec));

                        var upperVec = Vector256.Create(
                            output[upperIndex].Real,
                            output[upperIndex].Imaginary,
                            output[upperIndex2].Real,
                            output[upperIndex2].Imaginary);
                        var sum = Avx.Add(upperVec, twiddled);
                        var diff = Avx.Subtract(upperVec, twiddled);

                        output[upperIndex] = new Complex(sum.GetElement(0), sum.GetElement(1));
                        output[lowerIndex] = new Complex(diff.GetElement(0), diff.GetElement(1));
                        output[upperIndex2] = new Complex(sum.GetElement(2), sum.GetElement(3));
                        output[lowerIndex2] = new Complex(diff.GetElement(2), diff.GetElement(3));

                        j++;
                        w = w2;
                    }
                    else if (useArm64Simd && (j + 1) < halfLen)
                    {
                        var upperIndex2 = upperIndex + 1;
                        var lowerIndex2 = lowerIndex + 1;

                        var lower1 = Unsafe.ReadUnaligned<Vector128<double>>(
                            ref Unsafe.As<Complex, byte>(ref output[lowerIndex]));
                        var lower2 = Unsafe.ReadUnaligned<Vector128<double>>(
                            ref Unsafe.As<Complex, byte>(ref output[lowerIndex2]));

                        var w2 = w * wLen;
                        var wr1 = Vector128.Create(w.Real);
                        var wi1 = Vector128.Create(w.Imaginary);
                        var wr2 = Vector128.Create(w2.Real);
                        var wi2 = Vector128.Create(w2.Imaginary);

                        var swappedSigned1 = Vector128.Create(-lower1.GetElement(1), lower1.GetElement(0));
                        var swappedSigned2 = Vector128.Create(-lower2.GetElement(1), lower2.GetElement(0));
                        var twiddled1 = AdvSimd.Arm64.Add(
                            AdvSimd.Arm64.Multiply(lower1, wr1),
                            AdvSimd.Arm64.Multiply(swappedSigned1, wi1));
                        var twiddled2 = AdvSimd.Arm64.Add(
                            AdvSimd.Arm64.Multiply(lower2, wr2),
                            AdvSimd.Arm64.Multiply(swappedSigned2, wi2));

                        var upper1 = Unsafe.ReadUnaligned<Vector128<double>>(
                            ref Unsafe.As<Complex, byte>(ref output[upperIndex]));
                        var upper2 = Unsafe.ReadUnaligned<Vector128<double>>(
                            ref Unsafe.As<Complex, byte>(ref output[upperIndex2]));

                        var sum1 = AdvSimd.Arm64.Add(upper1, twiddled1);
                        var diff1 = AdvSimd.Arm64.Subtract(upper1, twiddled1);
                        var sum2 = AdvSimd.Arm64.Add(upper2, twiddled2);
                        var diff2 = AdvSimd.Arm64.Subtract(upper2, twiddled2);

                        output[upperIndex] = new Complex(sum1.GetElement(0), sum1.GetElement(1));
                        output[lowerIndex] = new Complex(diff1.GetElement(0), diff1.GetElement(1));
                        output[upperIndex2] = new Complex(sum2.GetElement(0), sum2.GetElement(1));
                        output[lowerIndex2] = new Complex(diff2.GetElement(0), diff2.GetElement(1));

                        j++;
                        w = w2;
                    }
                    else
                    {
                        var u = output[upperIndex];
                        var v = output[lowerIndex] * w;
                        output[upperIndex] = u + v;
                        output[lowerIndex] = u - v;
                    }

                    w *= wLen;
                }
            }
        }
    }

    /// <summary>
    /// ReverseBits を実行します。
    /// </summary>
    /// <param name="value">value を指定します。</param>
    /// <param name="bitCount">bitCount を指定します。</param>
    /// <returns>処理結果。</returns>
    private static int ReverseBits(int value, int bitCount)
    {
        var reversed = 0;
        for (var i = 0; i < bitCount; i++)
        {
            reversed = (reversed << 1) | (value & 1);
            value >>= 1;
        }

        return reversed;
    }
}

