using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Onta.Core;

/// <summary>
/// OFDM サブキャリアに割り当てる変調方式です。
/// データ部ブロックヘッダーの符号値と一致させます（1:BPSK 2:QPSK 3:16QAM 4:64QAM）。
/// 値 0 はファイルヘッダー内プレースホルダ用（未確定）です。
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
/// 音声チャンネル構成です。
/// </summary>
public enum ChannelMode : byte
{
    /// <summary>
    /// モノラル（L チャンネル配置を使用）。
    /// </summary>
    Mono = 0,

    /// <summary>
    /// ステレオ（L/R でサブキャリアをずらし、各 ch に ActiveSubcarriers 本＝合計 2 倍）。
    /// </summary>
    Stereo = 1
}

/// <summary>
/// 搬送波周波数グリッドの族です（modulation.mdc）。
/// </summary>
public enum OfdmCarrierGrid : byte
{
    /// <summary>SC-9/18: 440 Hz 起点、Δf = 1.3 × (fs/128)、R は L の中間。</summary>
    Sc9Family = 0,

    /// <summary>SC-27/36: 概念ビン k × (fs/128)、ステレオは 2k / 2k+1。</summary>
    Sc27Family = 1
}

/// <summary>
/// コンストラクタで妥当性検証を行う、不変の OFDM パラメータ集合です。
/// </summary>
public sealed record OfdmConfig
{
    /// <summary>
    /// FFT サイズを取得します。2 のべき乗である必要があります。
    /// </summary>
    public int FftSize { get; }

    /// <summary>
    /// 有効サブキャリア数を取得します。
    /// </summary>
    public int ActiveSubcarriers { get; }

    /// <summary>
    /// サンプル数単位の巡回プレフィックス長を取得します。
    /// </summary>
    public int CyclicPrefixLength { get; }

    /// <summary>
    /// 1 フレームあたりに生成する OFDM シンボル数を取得します。
    /// </summary>
    public int OfdmSymbolCount { get; }

    /// <summary>
    /// サブキャリアへ割り当てる変調方式を取得します。
    /// </summary>
    public ModulationScheme ModulationScheme { get; }

    /// <summary>
    /// モノラル / ステレオのチャンネル構成を取得します。
    /// </summary>
    public ChannelMode ChannelMode { get; }

    /// <summary>
    /// サブキャリア周波数インターリーブを有効にするかを取得します。
    /// </summary>
    public bool EnableFrequencyInterleaving { get; }

    /// <summary>
    /// パイロット挿入間隔（N サブキャリアごとに 1 パイロット）を取得します。
    /// </summary>
    public int PilotSpacing { get; }

    /// <summary>
    /// （互換用）旧ステレオ整数ビンずれ。現行実装では R は L 隣接 CH の中間周波数に固定するため未使用です。
    /// </summary>
    public int StereoFrequencyShiftBins { get; }

    /// <summary>
    /// サンプリング周波数 (Hz) を取得します。
    /// </summary>
    public int SampleRate { get; }

    /// <summary>
    /// 周波数インターリーブ並べ替えを更新する間隔（OFDM シンボル数）を取得します。
    /// </summary>
    public int FrequencyInterleaveIntervalSymbols { get; }

    /// <summary>
    /// 乱数シードを取得します。0 の場合は <see cref="Random.Shared"/> を使用します。
    /// </summary>
    public int RandomSeed { get; }

    /// <summary>
    /// L 概念チャンネル番号（GROUP 表の 1 始まりインデックス。A0=1 … D8=36）。
    /// null のときは <see cref="ResolveConceptualLeftBins"/> を使用します。
    /// FH/BH は GROUP B（10–18）を明示指定します。
    /// </summary>
    public IReadOnlyList<int> ConceptualLeftBins { get; }

    /// <summary>
    /// SC-9/18 族または SC-27/36 族の周波数グリッドです。
    /// </summary>
    public OfdmCarrierGrid CarrierGrid { get; }

    /// <summary>
    /// 妥当性検証済みの OFDM 設定を初期化します。
    /// </summary>
    /// <param name="fftSize">FFT サイズ（2 のべき乗）。</param>
    /// <param name="activeSubcarriers">有効サブキャリア数（9/18/27/36）。</param>
    /// <param name="cyclicPrefixLength">巡回プレフィックス長（サンプル数）。</param>
    /// <param name="ofdmSymbolCount">1 フレームあたりの OFDM シンボル数。</param>
    /// <param name="modulationScheme">サブキャリアの変調方式。</param>
    /// <param name="channelMode">モノラル / ステレオ構成。</param>
    /// <param name="enableFrequencyInterleaving">周波数インターリーブを有効にするか。</param>
    /// <param name="pilotSpacing">パイロット挿入間隔（N 本ごとに 1）。</param>
    /// <param name="stereoFrequencyShiftBins">互換用の旧ステレオビンずれ（現行未使用）。</param>
    /// <param name="sampleRate">サンプリング周波数 (Hz)。</param>
    /// <param name="frequencyInterleaveIntervalSymbols">インターリーブ並べ替え更新間隔（シンボル数）。</param>
    /// <param name="randomSeed">乱数シード（0 なら共有乱数）。</param>
    /// <param name="conceptualLeftBins">L 概念チャンネル番号列（null 可）。</param>
    /// <param name="carrierGrid">搬送波周波数グリッド族（null 可）。</param>
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
    /// 搬送波ビンが FFT 正周波数範囲に収まるかを検証します。
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

        // SC-27/36: 概念ビンを FFT ビンとして使用（ステレオは 2k / 2k+1）。
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

    /// <summary>GROUP A の L 概念番号（1–9）。</summary>
    /// <returns>GROUP A の L 概念番号配列。</returns>
    public static int[] ResolveGroupALeftBins() => Enumerable.Range(1, 9).ToArray();

    /// <summary>GROUP B の L 概念番号（10–18）。FH/BH で使用。</summary>
    /// <returns>GROUP B の L 概念番号配列。</returns>
    public static int[] ResolveGroupBLeftBins() => Enumerable.Range(10, 9).ToArray();

    /// <summary>GROUP C の L 概念番号（19–27）。</summary>
    /// <returns>GROUP C の L 概念番号配列。</returns>
    public static int[] ResolveGroupCLeftBins() => Enumerable.Range(19, 9).ToArray();

    /// <summary>GROUP D の L 概念番号（28–36）。</summary>
    /// <returns>GROUP D の L 概念番号配列。</returns>
    public static int[] ResolveGroupDLeftBins() => Enumerable.Range(28, 9).ToArray();

    /// <summary>
    /// modulation.mdc の GROUP 表に従う L 概念番号を返します。
    /// SC-9=B(10-18), SC-18=A+B(1-18), SC-27=A+B+C(1-27), SC-36=A+B+C+D(1-36)。
    /// </summary>
    /// <param name="activeSubcarriers">有効サブキャリア数。</param>
    /// <returns>SC 構成に応じた L 概念番号配列。</returns>
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

    /// <summary>使用する L 概念番号の最大値です。</summary>
    /// <param name="activeSubcarriers">有効サブキャリア数。</param>
    /// <returns>最大の L 概念チャンネル番号。</returns>
    public static int MaxConceptualLeftBin(int activeSubcarriers) =>
        ResolveConceptualLeftBins(activeSubcarriers)[^1];

    /// <summary>SC-27/36 のビン間隔 Δf = fs/128。</summary>
    /// <param name="sampleRate">サンプリング周波数 (Hz)。</param>
    /// <returns>SC-27/36 族の搬送波間隔 (Hz)。</returns>
    public static double DeltaF27(int sampleRate = 44100) => sampleRate / 128.0;

    /// <summary>SC-9/18 のビン間隔 Δf = 1.3 × Δf27。</summary>
    /// <param name="sampleRate">サンプリング周波数 (Hz)。</param>
    /// <returns>SC-9/18 族の搬送波間隔 (Hz)。</returns>
    public static double DeltaF9(int sampleRate = 44100) => DeltaF27(sampleRate) * 1.3;

    /// <summary>SC-9/18 の L 先頭周波数（GROUP A CH0）。</summary>
    public const double Sc9StartHz = 440.0;

    /// <summary>SC-9/18 の L 搬送波周波数（i=0 が A0）。</summary>
    /// <param name="zeroBasedChannelIndex">0 始まりチャンネル索引。</param>
    /// <param name="sampleRate">サンプリング周波数 (Hz)。</param>
    /// <returns>SC-9 族 L 側搬送波周波数 (Hz)。</returns>
    public static double LeftCarrierHzSc9(int zeroBasedChannelIndex, int sampleRate = 44100) =>
        Sc9StartHz + zeroBasedChannelIndex * DeltaF9(sampleRate);

    /// <summary>SC-9/18 の R 搬送波周波数（L の中間 = L + Δf9/2）。</summary>
    /// <param name="zeroBasedChannelIndex">0 始まりチャンネル索引。</param>
    /// <param name="sampleRate">サンプリング周波数 (Hz)。</param>
    /// <returns>SC-9 族 R 側搬送波周波数 (Hz)。</returns>
    public static double RightCarrierHzSc9(int zeroBasedChannelIndex, int sampleRate = 44100) =>
        LeftCarrierHzSc9(zeroBasedChannelIndex, sampleRate) + (DeltaF9(sampleRate) / 2.0);

    /// <summary>目標周波数を正周波数 FFT ビンへ最近傍割当します。</summary>
    /// <param name="hz">目標周波数 (Hz)。</param>
    /// <param name="fftSize">FFT サイズ。</param>
    /// <param name="sampleRate">サンプリング周波数 (Hz)。</param>
    /// <returns>最近傍の正周波数 FFT ビン。</returns>
    public static int HzToPositiveBin(double hz, int fftSize, int sampleRate)
    {
        var bin = (int)Math.Round(hz * fftSize / sampleRate);
        return Math.Clamp(bin, 1, (fftSize / 2) - 1);
    }

    /// <summary>
    /// アクティブサブキャリア数から搬送波グリッド族を決定します。
    /// </summary>
    /// <param name="activeSubcarriers">有効サブキャリア数。</param>
    /// <returns>対応する搬送波グリッド族。</returns>
    public static OfdmCarrierGrid ResolveCarrierGrid(int activeSubcarriers) =>
        activeSubcarriers is 9 or 18 ? OfdmCarrierGrid.Sc9Family : OfdmCarrierGrid.Sc27Family;

    /// <summary>
    /// modulation.mdc: SC-9/18 → FFT=128（目標 Hz を最近傍ビンへ）、SC-27/36 → 128（ステレオは中間配置のため×2）。
    /// </summary>
    /// <param name="activeSubcarriers">有効サブキャリア数。</param>
    /// <param name="channelMode">チャンネル構成。</param>
    /// <returns>推奨 FFT サイズ。</returns>
    public static int ResolveFftSize(int activeSubcarriers, ChannelMode channelMode)
    {
        var grid = ResolveCarrierGrid(activeSubcarriers);
        if (grid == OfdmCarrierGrid.Sc9Family)
        {
            // L/R は別 PCM。仕様どおり FFT=128 で目標周波数を最近傍ビン割当。
            return 128;
        }

        var mono = 128;
        return channelMode == ChannelMode.Stereo ? mono * 2 : mono;
    }
}

/// <summary>
/// <see cref="OfdmConfig"/> に基づいて時間領域 OFDM サンプルを生成します。
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
    /// チャンネル構成（モノラル／ステレオ）を取得します。
    /// </summary>
    public ChannelMode ChannelMode => _config.ChannelMode;
    private static readonly Complex UnmodulatedCarrierSymbol = Complex.One;

    /// <summary>
    /// 新しいジェネレータを初期化します。
    /// </summary>
    /// <param name="config">妥当性検証済みの OFDM 設定。</param>
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
    /// チャンネルの全キャリア・パイロット・データ配置と変調割当を構築します。
    /// </summary>
    /// <param name="channel">対象キャリアチャンネル（L/R）。</param>
    /// <returns>全キャリア・パイロット・データ配置とビンごとの変調割当。</returns>
    private (List<int> All, List<int> Pilots, List<int> DataBase, Dictionary<int, ModulationScheme> DataModulationByBin)
        BuildChannelLayout(CarrierChannel channel)
    {
        // 2ch 実信号 WAV 向け: 正周波数側に ActiveSubcarriers 本を配置し、
        // 負周波数は共役対称で埋めて IFFT 結果を実数化する。
        var all = GetPositiveCarrierBins(channel);
        if (all.Count != _config.ActiveSubcarriers)
        {
            throw new InvalidOperationException(
                $"Expected {_config.ActiveSubcarriers} positive carriers, got {all.Count}.");
        }

        var pilotSet = SelectPilotBins(all, _config.PilotSpacing);
        var pilots = pilotSet.OrderBy(x => x).ToList();
        // データ順のベース（未シャッフル）。FrequencyInterleaveIntervalSymbols ごとに並べ替える。
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
    /// 各パイロット群に属するキャリアビン配列を構築します。
    /// </summary>
    /// <param name="allCarriers">全キャリアビン。</param>
    /// <param name="orderedPilots">周波数順パイロット。</param>
    /// <returns>パイロットごとにグループ化したキャリア列。</returns>
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
    /// 概念 L 番号が GROUP D（28–36）かを判定します。
    /// </summary>
    /// <param name="conceptualLeftBin">L 概念チャンネル番号。</param>
    /// <returns>GROUP D なら true。</returns>
    private static bool IsGroupDConceptualLeftBin(int conceptualLeftBin) => conceptualLeftBin is >= 28 and <= 36;

    /// <summary>
    /// GROUP D 向けに変調を 1 段階下げた実効変調方式を返します。
    /// </summary>
    /// <param name="configuredScheme">設定変調方式。</param>
    /// <param name="conceptualLeftBin">L 概念チャンネル番号。</param>
    /// <returns>GROUP D 段階下げを反映した実効変調。</returns>
    private static ModulationScheme ResolveEffectiveCarrierModulation(
        ModulationScheme configuredScheme,
        int conceptualLeftBin)
    {
        if (!IsGroupDConceptualLeftBin(conceptualLeftBin))
        {
            return configuredScheme;
        }

        // modulation.mdc: GROUP D は 1 段階ダウンで送受信する。
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
    /// 変調方式あたりのビット数を返します。
    /// </summary>
    /// <param name="modulationScheme">変調方式。</param>
    /// <returns>1 シンボルあたりのビット数。</returns>
    private static int BitsPerModulation(ModulationScheme modulationScheme) => modulationScheme switch
    {
        ModulationScheme.Bpsk => 1,
        ModulationScheme.Qpsk => 2,
        ModulationScheme.Qam16 => 4,
        ModulationScheme.Qam64 => 6,
        _ => throw new InvalidOperationException("Unsupported modulation scheme.")
    };

    /// <summary>
    /// 周波数インターリーブ並べ替えを更新する OFDM シンボル間隔です。
    /// </summary>
    private int InterleaveIntervalSymbols =>
        Math.Max(1, _config.FrequencyInterleaveIntervalSymbols);

    /// <summary>
    /// epoch と M 系列に基づきデータキャリアの並べ替え順を返します。
    /// </summary>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <param name="symbolLocalSamplePosition">シンボル先頭の論理サンプル位置。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ用 M 系列初期シード。</param>
    /// <returns>データキャリアの処理順ビン列。</returns>
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
    /// インターリーブ用 31bit M 系列 LFSR の初期状態を生成します。
    /// </summary>
    /// <param name="epoch">インターリーブ epoch。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ用 M 系列初期シード。</param>
    /// <returns>初期化した LFSR 状態。</returns>
    private uint CreateMSequenceState(long epoch, bool useRightChannel, int interleaveInitSeed)
    {
        // SplitMix で初期状態を拡散し、31bit LFSR のゼロ状態を避ける。
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
    /// M 系列から並べ替えキー用の 31bit 語を取り出します。
    /// </summary>
    /// <param name="state">LFSR 状態（参照更新）。</param>
    /// <returns>次の疑似乱数ワード。</returns>
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
    /// 31bit 原始多項式で M 系列 LFSR を 1 ステップ進めます。
    /// </summary>
    /// <param name="state">LFSR 状態。</param>
    /// <returns>1 ステップ進めた LFSR 状態。</returns>
    private static uint AdvanceMSequence31(uint state)
    {
        // Primitive polynomial: x^31 + x^28 + 1
        var feedback = ((state >> 30) ^ (state >> 27)) & 1u;
        state = ((state << 1) & 0x7FFFFFFF) | feedback;
        return state == 0 ? 1u : state;
    }

    /// <summary>
    /// 正周波数側のアクティブサブキャリアビンを返します（Hermitian 実 OFDM 用）。
    /// SC-9/18: 目標 Hz（440 起点・1.3Δf・R 中間）を最近傍ビンへ。
    /// SC-27/36: 概念ビン k、ステレオは 2k / 2k+1。
    /// </summary>
    /// <param name="channel">対象キャリアチャンネル。</param>
    /// <returns>正周波数キャリアビンリスト。</returns>
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
    /// 正周波数ビンが重複しないよう近傍へずらして確保します。
    /// </summary>
    /// <param name="preferred">希望する正周波数ビン。</param>
    /// <param name="used">使用済みビン集合。</param>
    /// <returns>重複を避けた正周波数ビン。</returns>
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
    /// 正周波数範囲内のキャリアビンをリストへ追加します。
    /// </summary>
    /// <param name="bins">追加先のビンリスト。</param>
    /// <param name="bin">追加する正周波数ビン。</param>
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
    /// 実 OFDM 用に周波数ビンへ Hermite 共役対称を適用します。
    /// </summary>
    /// <param name="bins">エルミート対称を適用する周波数ビン。</param>
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
    /// 周波数ビンを Hermitian 対称化して IFFT し、実時間シンボルを返します。
    /// </summary>
    /// <param name="freqBins">周波数領域シンボル。</param>
    /// <returns>実数時間領域シンボル。</returns>
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
    /// Hermitian 対称化 → IFFT → 実数化までを scratch 上で行い、CP 付きで destination へ書き込みます。
    /// </summary>
    /// <param name="freqBins">周波数領域シンボル。</param>
    /// <param name="destinationWithCp">CP 付き時間領域の書き込み先。</param>
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
    /// 1 変調シンボルあたりのビット数です。
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
    /// 1 OFDM シンボルあたりに載せるデータビット数です。
    /// </summary>
    public int BitsPerOfdmSymbol => _bitsPerOfdmSymbol;

    /// <summary>
    /// データ用サブキャリア本数です。
    /// </summary>
    public int DataCarrierCount => _leftDataCarrierBase.Count;

    /// <summary>
    /// CP 込みの 1 OFDM シンボル長（サンプル数）です。
    /// </summary>
    public int SamplesPerOfdmSymbol => _config.FftSize + _config.CyclicPrefixLength;

    /// <summary>
    /// 設定された OFDM シンボル列に巡回プレフィックスを付与した 1 フレームを生成します。
    /// </summary>
    /// <returns>フレーム全体を連結した時間領域サンプル列。</returns>
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
    /// ステレオ用に L/R 2 チャンネルの OFDM フレームを生成します。
    /// </summary>
    /// <returns>Left と Right の時間領域サンプル列。</returns>
    /// <exception cref="InvalidOperationException">モードがステレオでない場合にスローされます。</exception>
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
    /// 全アクティブサブキャリアを無変調（固定参照点）で並べた OFDM を、指定サンプル数ぶん生成します。
    /// 全体先頭の同期用プリアンブル、および FH（1 秒）／BH（0.3 秒）先頭の無変調区間に使用します。
    /// </summary>
    /// <param name="sampleCount">生成する時間領域サンプル数。</param>
    /// <returns>モノラル時は Left のみ、ステレオ時は L/R 両方。</returns>
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
    /// 指定キャリア配置の無変調 OFDM を指定サンプル数ぶん生成します。
    /// </summary>
    /// <param name="sampleCount">生成するサンプル数。</param>
    /// <param name="carrierBins">無変調で並べるキャリアビン。</param>
    /// <returns>無変調時間領域サンプル列。</returns>
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
    /// 無変調 OFDM シンボルはキャリア固定なので 1 本キャッシュし、プリアンブル長ぶんタイルします。
    /// </summary>
    /// <param name="carrierBins">無変調キャリアビン。</param>
    /// <returns>キャッシュ済みまたは新規の無変調シンボル。</returns>
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
    /// 無変調キャリア固定の CP 付き OFDM シンボル 1 本を生成します。
    /// </summary>
    /// <param name="carrierBins">無変調キャリアビン。</param>
    /// <returns>無変調の CP 付きシンボル。</returns>
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
    /// ペイロードビット列を OFDM 変調し、巡回プレフィックス付き時間領域サンプルを返します。
    /// ステレオ時は同一ビット列を L/R それぞれ異なるキャリア配置・インターリーブで送信します。
    /// </summary>
    /// <param name="bits">変調するビット列。</param>
    /// <param name="absoluteSampleOffset">WAV 全体先頭からのサンプル位置（インターリーブ epoch 算出用）。</param>
    /// <param name="interleaveInitSeed">インターリーブ初期シード。</param>
    /// <returns>Left と Right の変調済み時間領域サンプル。</returns>
    public (Complex[] Left, Complex[] Right) ModulateBits(ReadOnlySpan<bool> bits, long absoluteSampleOffset = 0, int interleaveInitSeed = 0)
    {
        return ModulateBitStreams(bits, bits, absoluteSampleOffset, interleaveInitSeed);
    }

    /// <summary>
    /// L/R に異なるビット列を載せて OFDM 変調します（ステレオヘッダー並び用）。
    /// 両ストリームの生成サンプル長は一致する必要があります。
    /// </summary>
    /// <param name="leftBits">L チャンネルビット列。</param>
    /// <param name="rightBits">R チャンネルビット列。</param>
    /// <param name="absoluteSampleOffset">WAV 全体先頭からのサンプル位置（インターリーブ epoch 算出用）。</param>
    /// <param name="interleaveInitSeed">インターリーブ初期シード。</param>
    /// <returns>Left と Right の変調済み時間領域サンプル。</returns>
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
    /// 1 チャンネル分のビット列を OFDM 変調して時間領域サンプルを返します。
    /// </summary>
    /// <param name="bits">ビット列。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <param name="absoluteSampleOffset">WAV 先頭からのサンプル位置（epoch 算出用）。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ用 M 系列初期シード。</param>
    /// <returns>1 チャンネル分の時間領域サンプル。</returns>
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
    /// シンボル末尾を巡回プレフィックスとして先頭へ複写します。
    /// </summary>
    /// <param name="symbol">CP なし時間領域シンボル。</param>
    /// <param name="cpLength">巡回プレフィックス長。</param>
    /// <param name="destination">CP 付き出力先。</param>
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
    /// 指定ビット数を載せるのに必要な時間領域サンプル数です。
    /// </summary>
    /// <param name="bitCount">ペイロードビット数。</param>
    /// <returns>必要な時間領域サンプル数。</returns>
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
    /// 診断用: 指定ワウパラメータでのプリアンブル区間スコアを返します。
    /// </summary>
    /// <param name="samples">時間領域サンプル列。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <param name="analysisStartSample">解析区間の開始サンプル。</param>
    /// <param name="analysisSampleCount">解析に使うサンプル数。</param>
    /// <param name="amount">ワウ・フラッター振幅パラメータ。</param>
    /// <param name="wowPhase">ワウ成分の位相 (rad)。</param>
    /// <param name="flutterPhase">フラッター成分の位相 (rad)。</param>
    /// <returns>プリアンブル相関スコア。</returns>
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
    /// 診断用: ワウパラメータ探索の結果を返します。
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
    /// 診断用: 直前推定値の近傍のみを探索してワウパラメータを更新します。
    /// ideal / リサンプルバッファを探索全体で再利用します（MatchWow と同型）。
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

        // 粗い間引き相関で近傍格子を走査し、上位のみフル解像度で確定する。
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
    /// パイロットの周波数ずれから再生速度（ワウフラッター）を推定し、逆リサンプリングで補正します。
    /// 復号前に WAV 全体へ適用することを想定しています。
    /// </summary>
    /// <param name="samples">補正対象の時間領域サンプル（実数 OFDM）。</param>
    /// <param name="useRightChannel">R チャンネルのパイロット配置を使うか。</param>
    /// <param name="analysisStartSample">速度推定に使う区間の開始（無変調プリアンブル先頭を推奨）。</param>
    /// <param name="analysisSampleCount">速度推定に使うサンプル数（プリアンブル長を推奨）。</param>
    /// <param name="passes">推定→補正の反復回数。</param>
    /// <returns>ワウ補正後の時間領域サンプル。</returns>
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

        // 既知プリアンブルとの相関最大化でワウパラメータを同定し、全長速度プロファイルは作らない。
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
    /// 既知のワウ・フラッターパラメータで可逆写像の逆補正を行います。
    /// </summary>
    /// <param name="samples">補正対象の時間領域サンプル。</param>
    /// <param name="amount">ワウ・フラッター振幅パラメータ。</param>
    /// <param name="wowPhase">ワウ成分の位相 (rad)。</param>
    /// <param name="flutterPhase">フラッター成分の位相 (rad)。</param>
    /// <returns>指定パラメータで補正したサンプル。</returns>
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
    /// 既知パラメータによる逆補正を in-place で適用します（セグメント補正向け）。
    /// </summary>
    /// <param name="samples">補正対象の時間領域サンプル。</param>
    /// <param name="amount">ワウ・フラッター振幅パラメータ。</param>
    /// <param name="wowPhase">ワウ成分の位相 (rad)。</param>
    /// <param name="flutterPhase">フラッター成分の位相 (rad)。</param>
    public void CorrectWowFlutterWithParamsInPlace(
        Complex[] samples,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        CorrectWowFlutterWithParamsInPlace(samples, samples.Length, amount, wowPhase, flutterPhase);
    }

    /// <summary>
    /// 先頭 <paramref name="length"/> サンプルだけを in-place 補正します。
    /// </summary>
    /// <param name="samples">補正対象の時間領域サンプル。</param>
    /// <param name="length">補正する先頭からの長さ。</param>
    /// <param name="amount">ワウ・フラッター振幅パラメータ。</param>
    /// <param name="wowPhase">ワウ成分の位相 (rad)。</param>
    /// <param name="flutterPhase">フラッター成分の位相 (rad)。</param>
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
    /// 既知の無変調プリアンブルとの相関が最大になるワウ・フラッターを探索します。
    /// NoisePlus と同じ合成式: speed = 1 + A*(0.65*sin(wow)+0.35*sin(flutter))。
    /// </summary>
    /// <param name="samples">時間領域サンプル列。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <param name="analysisStartSample">解析区間の開始サンプル。</param>
    /// <param name="analysisSampleCount">解析に使うサンプル数。</param>
    /// <returns>最適ワウパラメータ列。失敗時は null。</returns>
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
    /// プリアンブル相関が最大になるワウ・フラッターパラメータを探索します。
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
            // 間引き相関は候補出し専用。best 更新はフル解像度のみ。
            if (corrStride <= 1 && score > bestScore)
            {
                bestScore = score;
                bestAmount = amount;
                bestWowPhase = wowPhase;
                bestFlutterPhase = flutterPhase;
            }

            return score;
        }

        // 尖った相関面向け: 粗い格子 → 近傍補完 → 少数シードの座標降下 → 局所研磨。
        // 旧実装の 36×36 + 5 シード×360° 全走査より評価回数を大幅に削減する。
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
            // 座標降下: 2° 格子で flutter/wow を交互に精密化（旧 1°×360 の半分解像）。
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

                // 2° 近傍を 1° で研磨
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

            // シード結果をフル解像度で確定候補に
            Evaluate(0.01, wow, flutter, corrStride: 1);

            // 局所の超精密研磨（幅 ±0.013 rad 程度）
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
            // 低スコアのまま補正すると、誤パラメータで波形を壊す（ノイズ下で顕著）。
            return null;
        }

        return (baseline, bestScore, bestAmount, bestWowPhase, bestFlutterPhase);
    }

    /// <summary>
    /// 位相を (-π, π] へ折り返します。
    /// </summary>
    /// <param name="phase">折り返す位相 (rad)。</param>
    /// <returns>(-π, π] に畳み込んだ位相。</returns>
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
    /// 間引き相関用に参照波形の平均・エネルギーを事前計算します。
    /// </summary>
    /// <param name="reference">reference。</param>
    /// <param name="stride">ストライド（間引き間隔）。</param>
    /// <returns>間引き相関用に参照波形の平均・エネルギーを事前計算。</returns>
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
    /// 事前計算済み参照統計を使い、実部の間引き正規化相関を返します。
    /// </summary>
    /// <param name="a">実数相関の一方。</param>
    /// <param name="b">参照側配列。</param>
    /// <param name="stride">ストライド（間引き間隔）。</param>
    /// <param name="meanB">参照 B の平均。</param>
    /// <param name="energyB">参照 B のエネルギー。</param>
    /// <param name="count">相関に使う要素数。</param>
    /// <returns>正規化相関値。</returns>
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
    /// カセット速度モデルからサンプル単位の速度プロファイルを生成します。
    /// </summary>
    /// <param name="sampleCount">生成するサンプル数。</param>
    /// <param name="sampleRate">サンプリング周波数 (Hz)。</param>
    /// <param name="amount">ワウ・フラッター振幅パラメータ。</param>
    /// <param name="wowPhase">ワウ成分の位相 (rad)。</param>
    /// <param name="flutterPhase">フラッター成分の位相 (rad)。</param>
    /// <returns>サンプルごとの速度倍率列。</returns>
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
            // NoisePlus と同様、平均正規化はせず下限のみかける。
            profile[i] = speed < 0.05 ? 0.05 : speed;
        }

        return profile;
    }

    /// <summary>
    /// カセット速度モデルからシンボル単位の速度プロファイルを生成します。
    /// </summary>
    /// <param name="symbolCount">シンボル数。</param>
    /// <param name="firstSymbol">先頭シンボル索引。</param>
    /// <param name="symbolLength">1 シンボルのサンプル長。</param>
    /// <param name="sampleRate">サンプリング周波数 (Hz)。</param>
    /// <param name="amount">ワウ・フラッター振幅パラメータ。</param>
    /// <param name="wowPhase">ワウ成分の位相 (rad)。</param>
    /// <param name="flutterPhase">フラッター成分の位相 (rad)。</param>
    /// <returns>シンボル代表速度列。</returns>
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
    /// 2 波形の実部について正規化相関を計算します。
    /// </summary>
    /// <param name="a">実数相関の一方。</param>
    /// <param name="b">実数相関の他方。</param>
    /// <returns>実数相関値。</returns>
    private static double CorrelateReal(Complex[] a, Complex[] b)
    {
        return CorrelateRealStrided(a, b, 1);
    }

    /// <summary>
    /// 間引きしながら 2 波形の実部正規化相関を計算します。
    /// </summary>
    /// <param name="a">実数相関の一方。</param>
    /// <param name="b">実数相関の他方。</param>
    /// <param name="stride">ストライド（間引き間隔）。</param>
    /// <returns>ストライド付き実数相関値。</returns>
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
    /// 診断用: 速度プロファイルによる逆リサンプリングを公開します。
    /// </summary>
    /// <param name="samples">リサンプル対象サンプル。</param>
    /// <param name="speedProfile">再生速度プロファイル（サンプルごと）。</param>
    /// <returns>逆速度リサンプル結果。</returns>
    public Complex[] ResampleWithInverseSpeedForDiagnostics(Complex[] samples, double[] speedProfile) =>
        ResampleWithInverseSpeed(samples, speedProfile);

    /// <summary>
    /// 診断用: 無変調プリアンブル理想波形との相関を返します。
    /// </summary>
    /// <param name="samples">時間領域サンプル列。</param>
    /// <param name="analysisStartSample">解析区間の開始サンプル。</param>
    /// <param name="analysisSampleCount">解析に使うサンプル数。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <returns>プリアンブル一致スコア。</returns>
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
    /// 診断用: 指定区間の速度プロファイルを推定します。
    /// </summary>
    /// <param name="samples">時間領域サンプル列。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <param name="analysisStartSample">解析区間の開始サンプル。</param>
    /// <param name="analysisSampleCount">解析に使うサンプル数。</param>
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
    /// プリアンブル区間の速度プロファイルを、同じ周期でファイル全体へ外挿します。
    /// </summary>
    /// <param name="preambleSpeeds">プリアンブル速度列。</param>
    /// <param name="sampleCount">出力サンプル数。</param>
    /// <param name="analysisStartSample">プリアンブル開始位置。</param>
    /// <returns>全長へ外挿した速度プロファイル。</returns>
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
    /// 速度プロファイルがワウ補正を要する有意な変動かを判定します。
    /// </summary>
    /// <param name="speedProfile">速度プロファイル。</param>
    /// <returns>補正が必要なら true。</returns>
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
        // 0.5% 級のワウでも補正する。ノイズだけの微小揺れは上下対称かつ小さいので弾く。
        return maxDeviation >= 0.002
            && meanAbs >= 0.0007
            && above >= 8
            && below >= 8;
    }

    /// <summary>
    /// パイロット周波数ずれから区間の速度プロファイルを推定します。
    /// </summary>
    /// <param name="samples">時間領域サンプル列。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <param name="analysisStartSample">解析区間の開始サンプル。</param>
    /// <param name="analysisSampleCount">解析に使うサンプル数。</param>
    /// <param name="allowSymbolOffsetSearch">シンボルオフセット探索を許可するか。</param>
    /// <param name="requireStrongCpImprovement">強い CP 相関改善を要求するか。</param>
    /// <returns>推定した速度プロファイル。</returns>
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
    /// カセットで典型的なワウ(0.5Hz)・フラッター(6Hz)成分を最小二乗で抽出し、速度プロファイルを滑らかにします。
    /// </summary>
    /// <param name="measured">測定速度列。</param>
    /// <param name="firstSymbol">先頭シンボル索引。</param>
    /// <param name="symbolLength">1 シンボルのサンプル長。</param>
    /// <returns>カセットモデルに当てはめた速度列。</returns>
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

        // 正規方程式 4x4
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

        // 振幅が小さすぎる／大きすぎる場合は測定値を維持
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

        // 生推定の RMS に合わせて振幅を補正（正弦フィットはノイズで過小評価されやすい）。
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
    /// 4×4 連立方程式を Gauss 消去で解き、成功時に解を返します。
    /// </summary>
    /// <param name="a">4×4 係数行列。</param>
    /// <param name="b">右辺ベクトル。</param>
    /// <param name="x">解の出力先。</param>
    /// <returns>解けたら true。</returns>
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
    /// 速度プロファイルの平均が 1 になるよう正規化します。
    /// </summary>
    /// <param name="speeds">正規化する速度列。</param>
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

        // 推定バイアスで全体が伸縮しないよう、平均速度を 1 に正規化する。
        for (var i = 0; i < speeds.Length; i++)
        {
            speeds[i] /= mean;
        }
    }

    /// <summary>
    /// CP とシンボル末尾の相関が最大になる開始オフセットを返します。
    /// </summary>
    /// <param name="window">探索窓。</param>
    /// <param name="maxSearch">最大探索オフセット。</param>
    /// <param name="requireStrongCpImprovement">強い CP 相関改善を要求するか。</param>
    /// <returns>最良シンボルオフセット。</returns>
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
    /// 指定オフセットでの CP とシンボル末尾の相関スコアを返します。
    /// </summary>
    /// <param name="window">探索対象のサンプル窓。</param>
    /// <param name="offset">候補シンボルオフセット。</param>
    /// <param name="fftSize">FFT サイズ。</param>
    /// <param name="cp">巡回プレフィックス長。</param>
    /// <returns>CP 相関スコア。</returns>
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
    /// パイロット近傍ピークから相対再生速度を推定します。
    /// </summary>
    /// <param name="freqBins">周波数領域シンボル。</param>
    /// <param name="pilotBins">パイロットビン。</param>
    /// <param name="carrierBins">キャリアビン。</param>
    /// <returns>パイロットから推定した速度倍率。</returns>
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
            // パイロット位置が局所最大でない場合はデータ漏れと判断して捨てる。
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

        // 無変調プリアンブルなど、パイロットが弱いが全キャリアがトーンのとき。
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
    /// 複素数の絶対値を返します。
    /// </summary>
    /// <param name="value">複素値。</param>
    /// <returns>複素絶対値。</returns>
    private static double ComplexMagnitude(Complex value)
    {
        return Math.Sqrt((value.Real * value.Real) + (value.Imaginary * value.Imaginary));
    }

    /// <summary>
    /// 速度プロファイルに基づき全長を逆リサンプリングします。
    /// </summary>
    /// <param name="samples">リサンプル対象サンプル。</param>
    /// <param name="speedProfile">再生速度プロファイル（サンプルごと）。</param>
    /// <returns>逆速度でリサンプルした列。</returns>
    private Complex[] ResampleWithInverseSpeed(Complex[] samples, double[] speedProfile)
    {
        var n = samples.Length;
        if (n == 0)
        {
            return samples;
        }

        // NoisePlus.ApplyWowFlutter と同じ cumul（長さ N、蓄積は N-1 回）で逆写像する。
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
    /// 全長の速度累積に基づき、指定区間だけ逆リサンプリングした結果を返します。
    /// </summary>
    /// <param name="samples">リサンプル対象サンプル。</param>
    /// <param name="speedProfile">再生速度プロファイル（サンプルごと）。</param>
    /// <param name="segmentStart">セグメント開始位置。</param>
    /// <param name="segmentLength">セグメント長。</param>
    /// <returns>セグメントのリサンプル結果。</returns>
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
    /// カセット速度モデルをオンザフライで使い、指定区間だけ逆リサンプリングします（探索用）。
    /// 累積は閉形式で計算し、インデックスは二分探索します。
    /// </summary>
    /// <param name="samples">リサンプル対象サンプル。</param>
    /// <param name="segmentStart">セグメント開始位置。</param>
    /// <param name="segmentLength">セグメント長。</param>
    /// <param name="sampleRate">サンプリング周波数 (Hz)。</param>
    /// <param name="amount">ワウ・フラッター振幅パラメータ。</param>
    /// <param name="wowPhase">ワウ成分の位相 (rad)。</param>
    /// <param name="flutterPhase">フラッター成分の位相 (rad)。</param>
    /// <param name="stride">ストライド（間引き間隔）。</param>
    /// <returns>セグメントのリサンプル結果。</returns>
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
    /// カセット速度モデルで区間を逆リサンプリングし出力スパンへ書き込みます。
    /// </summary>
    /// <param name="samples">リサンプル対象サンプル。</param>
    /// <param name="segmentStart">セグメント開始位置。</param>
    /// <param name="segmentLength">セグメント長。</param>
    /// <param name="sampleRate">サンプリング周波数 (Hz)。</param>
    /// <param name="amount">ワウ・フラッター振幅パラメータ。</param>
    /// <param name="wowPhase">ワウ成分の位相 (rad)。</param>
    /// <param name="flutterPhase">フラッター成分の位相 (rad)。</param>
    /// <param name="output">インプレース FFT バッファ。</param>
    /// <param name="stride">ストライド（間引き間隔）。</param>
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

        // 出力インデックスが増えるにつれ target も単調増加するので、累積位置は前進のみで追う。
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

            // amount が小さいときは i≈target なので、大きく遅れている場合だけジャンプする。
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
    /// NoisePlus と同じ速度モデルの累積 sum_{k=0}^{i-1} speed[k] を閉形式で返します。
    /// </summary>
    /// <param name="i">i。</param>
    /// <param name="sampleRate">サンプリング周波数 (Hz)。</param>
    /// <param name="amount">ワウ・フラッター振幅パラメータ。</param>
    /// <param name="wowPhase">ワウ成分の位相 (rad)。</param>
    /// <param name="flutterPhase">フラッター成分の位相 (rad)。</param>
    /// <returns>計算結果の実数。</returns>
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
    /// 事前計算したゲイン・角周波数でカセット速度累積を閉形式計算します。
    /// </summary>
    /// <param name="i">i。</param>
    /// <param name="wowGain">wowGain。</param>
    /// <param name="flutterGain">flutterGain。</param>
    /// <param name="wowPhase">ワウ成分の位相 (rad)。</param>
    /// <param name="wowOmega">wowOmega。</param>
    /// <param name="flutterPhase">フラッター成分の位相 (rad)。</param>
    /// <param name="flutterOmega">flutterOmega。</param>
    /// <returns>計算結果の実数。</returns>
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
    /// 等差角の正弦和を閉形式で計算します。
    /// </summary>
    /// <param name="phase0">phase0。</param>
    /// <param name="omega">omega。</param>
    /// <param name="count">相関に使う要素数。</param>
    /// <returns>計算結果の実数。</returns>
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
    /// 目標累積値以下となる最大サンプル索引を二分探索します。
    /// </summary>
    /// <param name="target">target。</param>
    /// <param name="sampleCount">生成または処理するサンプル数。</param>
    /// <param name="sampleRate">サンプリング周波数 (Hz)。</param>
    /// <param name="amount">ワウ・フラッター振幅パラメータ。</param>
    /// <param name="wowPhase">ワウ成分の位相 (rad)。</param>
    /// <param name="flutterPhase">フラッター成分の位相 (rad)。</param>
    /// <returns>計算結果の整数。</returns>
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
    /// NoisePlus と同じ規則で速度累積と終端スケールを構築します。
    /// </summary>
    /// <param name="sampleCount">生成または処理するサンプル数。</param>
    /// <param name="speedProfile">再生速度プロファイル（サンプルごと）。</param>
    /// <param name="cumul">cumul。</param>
    /// <param name="scale">スケール係数。</param>
    /// <param name="speedHop">speedHop。</param>
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
    /// 累積時間軸上の目標位置から Hermite 補間サンプルを取得します。
    /// </summary>
    /// <param name="samples">時間領域サンプル列。</param>
    /// <param name="cumul">cumul。</param>
    /// <param name="scale">スケール係数。</param>
    /// <param name="outputIndex">outputIndex。</param>
    /// <param name="warpedIndex">warpedIndex。</param>
    /// <returns>複素値。</returns>
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
        // 連続位置（warpedIndex + frac）で 4 点 Hermite 補間し、線形の二重補間誤差を減らす。
        var pos = warpedIndex + Math.Clamp(frac, 0.0, 1.0);
        return SampleHermite(samples, pos);
    }

    /// <summary>
    /// 実部に対する 4 点 Hermite 補間でサンプル値を返します。
    /// </summary>
    /// <param name="samples">時間領域サンプル列。</param>
    /// <param name="position">position。</param>
    /// <returns>複素値。</returns>
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
    /// 指定位置から数 OFDM シンボル分の同期スコア（CP 相関 + パイロット強度）を返します。
    /// </summary>
    /// <param name="samples">時間領域サンプル列。</param>
    /// <param name="start">start。</param>
    /// <param name="symbolCount">シンボル数。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <returns>計算結果の実数。</returns>
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
    /// CP 相関とパイロット品質が最大になるシンボル開始位置を、期待位置近傍から探します。
    /// 期待位置とのスコア差が小さい場合は期待位置を維持し、クリーン信号の誤ロックを防ぎます。
    /// </summary>
    /// <param name="samples">時間領域サンプル列。</param>
    /// <param name="expectedStart">expectedStart。</param>
    /// <param name="searchRadius">タイミング探索半径（サンプル）。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <returns>計算結果の整数。</returns>
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

        // 近傍が狭いときは従来どおり総当たりし、分岐コストを避ける。
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

        // わずかなスコア差では動かさない（サイドローブへの吸い込み防止）。
        const double lockMargin = 0.15;
        if (bestDelta != 0 && bestScore < scoreAtExpected + lockMargin)
        {
            return expectedStart;
        }

        return expectedStart + bestDelta;
    }

    /// <summary>
    /// 1 シンボルの CP 相関をエネルギー正規化したロックスコアを返します。
    /// </summary>
    /// <param name="symbolWithCp">CP 付き OFDM シンボル。</param>
    /// <returns>計算結果の実数。</returns>
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
    /// 1 シンボルの CP 相関とパイロット品質からロックスコアを返します。
    /// </summary>
    /// <param name="symbolWithCp">CP 付き OFDM シンボル。</param>
    /// <param name="pilotBins">パイロットの FFT ビン集合。</param>
    /// <returns>計算結果の実数。</returns>
    private double ScoreSingleSymbolLock(ReadOnlySpan<Complex> symbolWithCp, List<int> pilotBins)
    {
        var timeNoCp = _scoreTimeNoCpScratch;
        var freqBins = _scoreFreqBinsScratch;
        return ScoreSingleSymbolLock(symbolWithCp, pilotBins, timeNoCp, freqBins);
    }

    /// <summary>
    /// スクラッチ FFT バッファを使い CP とパイロット品質でロックスコアを返します。
    /// </summary>
    /// <param name="symbolWithCp">CP 付き OFDM シンボル。</param>
    /// <param name="pilotBins">パイロットの FFT ビン集合。</param>
    /// <param name="timeNoCp">CP 除去後の時間領域バッファ。</param>
    /// <param name="freqBins">周波数領域ビン配列。</param>
    /// <returns>計算結果の実数。</returns>
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
        // パイロットが強く、CP も揃っている位置を高スコアにする。
        return (normalizedCp * 2.0) + Math.Log10(pilotPower + 1e-12);
    }

    /// <summary>
    /// ストリーム上で CP/パイロット同期しながらビットを取り出します。
    /// <paramref name="logicalSampleOffset"/> は周波数インターリーブ用の符号化時刻です。
    /// </summary>
    /// <param name="samples">時間領域サンプル列。</param>
    /// <param name="cursor">ストリーム読み取りカーソル（更新される）。</param>
    /// <param name="bitCount">ビット数。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <param name="logicalSampleOffset">周波数インターリーブ用の論理サンプル位置。</param>
    /// <param name="searchRadius">タイミング探索半径（サンプル）。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ用 M 系列初期シード。</param>
    /// <returns>ハード判定ビット列。</returns>
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
        // シンボルごとに CP/パイロットで追従し、ワウによる累積ずれを吸収する。
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
    /// ストリーム上で CP/パイロット同期しながらソフト LLR を取り出します。
    /// </summary>
    /// <param name="samples">時間領域サンプル列。</param>
    /// <param name="cursor">ストリーム読み取りカーソル（更新される）。</param>
    /// <param name="bitCount">ビット数。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <param name="logicalSampleOffset">周波数インターリーブ用の論理サンプル位置。</param>
    /// <param name="searchRadius">タイミング探索半径（サンプル）。</param>
    /// <param name="noiseVariance">ノイズ分散（ソフト LLR 用）。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ用 M 系列初期シード。</param>
    /// <param name="onEqualizedDataSymbol">等化後データキャリアのコールバック。</param>
    /// <param name="onEqualizedDataSymbolFrame">等化後シンボルフレームのコールバック。</param>
    /// <param name="onFftSymbolFrame">FFT 後シンボルフレームのコールバック。</param>
    /// <returns>ソフト LLR 列。</returns>
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
    /// ステレオ時に L/R 同一ペイロードのソフト LLR を加算合成します。
    /// タイミングは L のパイロット同期に合わせ、R も同じシンボル境界で復調します。
    /// </summary>
    /// <param name="leftSamples">L チャンネル時間領域サンプル。</param>
    /// <param name="rightSamples">R チャンネル時間領域サンプル。</param>
    /// <param name="cursor">ストリーム読み取りカーソル（更新される）。</param>
    /// <param name="bitCount">ビット数。</param>
    /// <param name="logicalSampleOffset">周波数インターリーブ用の論理サンプル位置。</param>
    /// <param name="searchRadius">タイミング探索半径（サンプル）。</param>
    /// <param name="noiseVariance">ノイズ分散（ソフト LLR 用）。</param>
    /// <param name="estimateNoiseFromPilots">パイロットからノイズ分散を推定するか。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ用 M 系列初期シード。</param>
    /// <returns>L/R 合成ソフト LLR 列。</returns>
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
    /// ストリーム同期しながらソフト LLR を復調する共通実装です。
    /// </summary>
    /// <param name="samples">時間領域サンプル列。</param>
    /// <param name="secondarySamples">副チャンネルの時間領域サンプル。</param>
    /// <param name="cursor">ストリーム読み取りカーソル（更新される）。</param>
    /// <param name="bitCount">ビット数。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <param name="secondaryUseRightChannel">副側を R チャンネル配置で扱うか。</param>
    /// <param name="logicalSampleOffset">周波数インターリーブ用の論理サンプル位置。</param>
    /// <param name="searchRadius">タイミング探索半径（サンプル）。</param>
    /// <param name="noiseVariance">ノイズ分散（ソフト LLR 用）。</param>
    /// <param name="estimateNoiseFromPilots">パイロットからノイズ分散を推定するか。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ用 M 系列初期シード。</param>
    /// <param name="onEqualizedDataSymbol">等化後データキャリアのコールバック。</param>
    /// <param name="onEqualizedDataSymbolFrame">等化後シンボルフレームのコールバック。</param>
    /// <param name="onFftSymbolFrame">FFT 後シンボルフレームのコールバック。</param>
    /// <returns>ソフト LLR 列。</returns>
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

        // パイロット残差は本復調パス内で蓄積する（別周回の sync/FFT はしない）。
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
    /// 1 シンボルを FFT・等化してデータキャリアのソフト LLR を出力します。
    /// </summary>
    /// <param name="symbolWithCp">CP 付き OFDM シンボル。</param>
    /// <param name="pilotBins">パイロットの FFT ビン集合。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <param name="agcState">パイロットグループ AGC 状態。</param>
    /// <param name="logical">論理サンプル位置。</param>
    /// <param name="timeNoCp">CP 除去後の時間領域バッファ。</param>
    /// <param name="freqBins">周波数領域ビン配列。</param>
    /// <param name="equalizers">パイロット等化係数。</param>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="llrs">ソフト LLR 出力バッファ。</param>
    /// <param name="noiseVariance">ノイズ分散（ソフト LLR 用）。</param>
    /// <param name="addToExisting">既存 LLR に加算するか。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ用 M 系列初期シード。</param>
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
    /// 準備済み周波数ビンと等化係数からデータキャリアのソフト LLR を出力します。
    /// </summary>
    /// <param name="freqBins">周波数領域ビン配列。</param>
    /// <param name="equalizers">パイロット等化係数。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <param name="logical">論理サンプル位置。</param>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="llrs">ソフト LLR 出力バッファ。</param>
    /// <param name="noiseVariance">ノイズ分散（ソフト LLR 用）。</param>
    /// <param name="addToExisting">既存 LLR に加算するか。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ用 M 系列初期シード。</param>
    /// <param name="onEqualizedDataSymbol">等化後データキャリアのコールバック。</param>
    /// <param name="onEqualizedDataSymbolFrame">等化後シンボルフレームのコールバック。</param>
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
    /// シンボルからパイロット残差を蓄積し雑音分散推定に使います。
    /// </summary>
    /// <param name="symbolWithCp">CP 付き OFDM シンボル。</param>
    /// <param name="pilotBins">パイロットの FFT ビン集合。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <param name="timeNoCp">CP 除去後の時間領域バッファ。</param>
    /// <param name="freqBins">周波数領域ビン配列。</param>
    /// <param name="equalizers">パイロット等化係数。</param>
    /// <param name="noiseAccum">ノイズ電力の累積値。</param>
    /// <param name="noiseCount">ノイズ累積のサンプル数。</param>
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
    /// 準備済み等化後パイロット残差を雑音蓄積へ加えます。
    /// </summary>
    /// <param name="freqBins">周波数領域ビン配列。</param>
    /// <param name="equalizers">パイロット等化係数。</param>
    /// <param name="pilotBins">パイロットの FFT ビン集合。</param>
    /// <param name="noiseAccum">ノイズ電力の累積値。</param>
    /// <param name="noiseCount">ノイズ累積のサンプル数。</param>
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
    /// 無変調プリアンブルなどを CP/パイロット同期で読み進め、ワウによる位置ずれを吸収します。
    /// </summary>
    /// <param name="samples">時間領域サンプル列。</param>
    /// <param name="cursor">ストリーム読み取りカーソル（更新される）。</param>
    /// <param name="symbolCount">スキップするシンボル数。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <param name="searchRadius">タイミング探索半径（サンプル）。</param>
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
    /// 時間領域サンプルからペイロードビットをハード判定で取り出します。
    /// </summary>
    /// <param name="samples">CP 付き OFDM シンボル列。</param>
    /// <param name="bitCount">取り出すビット数。</param>
    /// <param name="useRightChannel">R チャンネル復調するか。</param>
    /// <param name="absoluteSampleOffset">WAV 全体先頭からのサンプル位置（インターリーブ epoch 算出用）。</param>
    /// <param name="interleaveInitSeed">インターリーブ初期シード。</param>
    /// <returns>ハード判定ビット列。</returns>
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
    /// パイロット配置グループごとに等化係数を推定します。
    /// 同一グループのキャリアへ同一係数を適用し、シンボルごとにAGC状態を更新します。
    /// </summary>
    /// <param name="freqBins">周波数領域ビン配列。</param>
    /// <param name="pilotBins">パイロットの FFT ビン集合。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <param name="agcState">パイロットグループ AGC 状態。</param>
    /// <returns>パイロット等化係数列。</returns>
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
    /// パイロット群ごとの等化係数をバッファへ書き込みます。
    /// </summary>
    /// <param name="freqBins">周波数領域ビン配列。</param>
    /// <param name="pilotBins">パイロットの FFT ビン集合。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <param name="equalizers">パイロット等化係数。</param>
    /// <param name="agcState">パイロットグループ AGC 状態。</param>
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

            // received ≈ 1 * h を逆補正し、群ごとのレベルを 1 に合わせる。
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
    /// キャリアビンが属するパイロット群の索引を返します。
    /// </summary>
    /// <param name="carrierBin">キャリアビン。</param>
    /// <param name="orderedPilots">周波数順パイロット。</param>
    /// <returns>パイロットグループ索引。</returns>
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
        /// パイロット群ごとの AGC 平滑状態を初期化します。
        /// </summary>
        /// <param name="groupCount">グループ数。</param>
        public PilotGroupAgcState(int groupCount)
        {
            var size = Math.Max(1, groupCount);
            _smoothed = new Complex[size];
            _initialized = new bool[size];
        }

        /// <summary>
        /// パイロット群の瞬間等化係数を指数平滑して返します。
        /// </summary>
        /// <param name="groupIndex">更新するグループ索引。</param>
        /// <param name="instantaneous">瞬間チャネル推定。</param>
        /// <returns>平滑化したチャネル推定。</returns>
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
    /// 隣接パイロット間でチャネル応答を線形補間します。
    /// </summary>
    /// <param name="bin">補間対象ビン。</param>
    /// <param name="orderedPilots">周波数順パイロットビン。</param>
    /// <param name="channels">パイロットチャネル推定列。</param>
    /// <returns>補間した複素チャネル。</returns>
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
    /// 未使用ビンとパイロット残差から雑音電力を推定します。
    /// </summary>
    /// <param name="freqBins">周波数領域ビン配列。</param>
    /// <param name="pilotBins">パイロットの FFT ビン集合。</param>
    /// <param name="allCarriers">全キャリアビン集合。</param>
    /// <returns>推定ノイズ電力。</returns>
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

        // パイロット残差（平均からのずれ）も雑音推定に混ぜる。
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

        // 下限を設け、ZF に近い状態でも数値的に安定させる。
        return Math.Max(energy / count, 1e-4);
    }

    /// <summary>
    /// 前後シンボルの等化係数を指数平滑します。
    /// </summary>
    /// <param name="previous">直前フレームの等化。</param>
    /// <param name="current">現フレームの等化。</param>
    /// <returns>平滑化した等化係数列。</returns>
    private static Complex[] SmoothEqualizers(ref Complex[]? previous, Complex[] current)
    {
        if (previous is null || previous.Length != current.Length)
        {
            previous = (Complex[])current.Clone();
            return current;
        }

        const double alpha = 0.35; // 現シンボル寄与。残りは過去の平滑値。
        var mixed = new Complex[current.Length];
        for (var i = 0; i < current.Length; i++)
        {
            mixed[i] = (current[i] * alpha) + (previous[i] * (1.0 - alpha));
        }

        previous = mixed;
        return mixed;
    }

    /// <summary>
    /// パイロット平均から単一の複素等化係数を推定します（後方互換・スコアリング用）。
    /// </summary>
    /// <param name="freqBins">周波数領域ビン配列。</param>
    /// <param name="pilotBins">パイロットの FFT ビン集合。</param>
    /// <returns>平均パイロット等化係数。</returns>
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

        // received ≈ PilotSymbol * gain = 1 * gain → equalizer = 1/gain
        return Complex.One / average;
    }

    /// <summary>
    /// 等化後変調シンボルからハードビットを取り出します。
    /// </summary>
    /// <param name="symbol">等化後の複素シンボル。</param>
    /// <param name="modulationScheme">サブキャリアの変調方式。</param>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="bits">ビット列。</param>
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
    /// 等化後変調シンボルからソフト LLR を取り出します。
    /// </summary>
    /// <param name="symbol">等化後の複素シンボル。</param>
    /// <param name="modulationScheme">サブキャリアの変調方式。</param>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="llrs">ソフト LLR 出力バッファ。</param>
    /// <param name="noiseVariance">ノイズ分散（ソフト LLR 用）。</param>
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
                // 単位エネルギー BPSK: s = ±1。LLR>0 ⇒ bit1。
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
    /// 1 軸 PAM 振幅からソフト LLR を double 配列へ書き込みます。
    /// </summary>
    /// <param name="amplitude">PAM 軸振幅。</param>
    /// <param name="bitsPerAxis">軸あたりのビット数。</param>
    /// <param name="levels">PAM 振幅レベル列。</param>
    /// <param name="invVariance">ノイズ分散の逆数。</param>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="llrs">ソフト LLR 出力バッファ。</param>
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
    /// 1 軸 PAM 振幅からソフト LLR を計算する共通実装です。
    /// </summary>
    /// <param name="amplitude">PAM 軸振幅。</param>
    /// <param name="bitsPerAxis">軸あたりのビット数。</param>
    /// <param name="levels">PAM 振幅レベル列。</param>
    /// <param name="invVariance">ノイズ分散の逆数。</param>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="llrs">ソフト LLR 出力バッファ。</param>
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

            // LLR > 0 ⇒ ビット 1 寄り
            WriteLlr(ref bitIndex, llrs, 0.5 * (minDist0 - minDist1) * invVariance);
        }
    }

    /// <summary>
    /// 16QAM 1 軸のソフト LLR を AVX で計算します。
    /// </summary>
    /// <param name="amplitude">PAM 軸振幅。</param>
    /// <param name="invVariance">ノイズ分散の逆数。</param>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="llrs">ソフト LLR 出力バッファ。</param>
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
    /// 64QAM 1 軸のソフト LLR を AVX で計算します。
    /// </summary>
    /// <param name="amplitude">PAM 軸振幅。</param>
    /// <param name="invVariance">ノイズ分散の逆数。</param>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="llrs">ソフト LLR 出力バッファ。</param>
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
    /// 16QAM 1 軸のソフト LLR を ARM64 AdvSimd で計算します。
    /// </summary>
    /// <param name="amplitude">PAM 軸振幅。</param>
    /// <param name="invVariance">ノイズ分散の逆数。</param>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="llrs">ソフト LLR 出力バッファ。</param>
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
    /// 64QAM 1 軸のソフト LLR を ARM64 AdvSimd で計算します。
    /// </summary>
    /// <param name="amplitude">PAM 軸振幅。</param>
    /// <param name="invVariance">ノイズ分散の逆数。</param>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="llrs">ソフト LLR 出力バッファ。</param>
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
    /// バイナリ索引から Gray マップ済み PAM レベル表を構築します。
    /// </summary>
    /// <param name="bitsPerAxis">軸ビット数。</param>
    /// <param name="levels">レベル振幅列。</param>
    /// <returns>バイナリ索引→振幅の対応表。</returns>
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
    /// LLR バッファへ値を書き込みビット索引を進めます。
    /// </summary>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="llrs">ソフト LLR 出力バッファ。</param>
    /// <param name="value">書き込む LLR 値。</param>
    private static void WriteLlr(ref int bitIndex, Span<double> llrs, double value)
    {
        if (bitIndex >= llrs.Length)
        {
            return;
        }

        llrs[bitIndex++] = value;
    }

    /// <summary>
    /// 1 軸 PAM 振幅から最近傍判定でハードビットを出力します。
    /// </summary>
    /// <param name="amplitude">PAM 軸振幅。</param>
    /// <param name="bitsPerAxis">軸あたりのビット数。</param>
    /// <param name="levels">PAM 振幅レベル列。</param>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="bits">ビット列。</param>
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
    /// 振幅に最も近い PAM レベル索引を返します。
    /// </summary>
    /// <param name="amplitude">観測振幅。</param>
    /// <param name="levels">候補レベル列。</param>
    /// <returns>最近傍レベル索引。</returns>
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
    /// Gray 符号をバイナリ符号へ変換します。
    /// </summary>
    /// <param name="gray">グレイ符号の整数値。</param>
    /// <returns>バイナリ値。</returns>
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
    /// ビット配列へ値を書き込みビット索引を進めます。
    /// </summary>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="bits">ビット列。</param>
    /// <param name="value">書き込むビット値。</param>
    private static void WriteBit(ref int bitIndex, bool[] bits, bool value)
    {
        if (bitIndex >= bits.Length)
        {
            return;
        }

        bits[bitIndex++] = value;
    }

    /// <summary>
    /// CP 除去・FFT・パイロット等化までを一連で行いバッファへ格納します。
    /// </summary>
    /// <param name="symbolWithCp">CP 付き OFDM シンボル。</param>
    /// <param name="pilotBins">パイロットの FFT ビン集合。</param>
    /// <param name="useRightChannel">R チャンネル配置を使うか。</param>
    /// <param name="agcState">パイロットグループ AGC 状態。</param>
    /// <param name="timeNoCp">CP 除去後の時間領域バッファ。</param>
    /// <param name="freqBins">周波数領域ビン配列。</param>
    /// <param name="equalizers">パイロット等化係数。</param>
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
    /// 巡回プレフィックスを除いた FFT 区間サンプルを返します。
    /// </summary>
    /// <param name="withCp">CP 付きシンボル。</param>
    /// <param name="cpLength">巡回プレフィックス長。</param>
    /// <returns>CP 除去後のシンボル。</returns>
    private static Complex[] RemoveCyclicPrefix(ReadOnlySpan<Complex> withCp, int cpLength)
    {
        var result = new Complex[withCp.Length - cpLength];
        withCp.Slice(cpLength).CopyTo(result);
        return result;
    }

    /// <summary>
    /// <see cref="InverseFft"/> の逆変換です。time = InverseFft(freq) のとき freq を復元します。
    /// </summary>
    /// <param name="time">時間領域シンボル。</param>
    /// <returns>周波数領域シンボル。</returns>
    private static Complex[] ForwardFftMatchingInverse(Complex[] time)
    {
        var freq = new Complex[time.Length];
        ForwardFftMatchingInverseInto(time, freq);
        return freq;
    }

    /// <summary>
    /// IFFT と対になる前方 FFT を destination へ書き込みます。
    /// </summary>
    /// <param name="time">時間領域シンボル。</param>
    /// <param name="destination">周波数領域の書き込み先。</param>
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
    /// ランダムデータ付きの周波数領域 OFDM シンボルを構築します。
    /// </summary>
    /// <param name="channel">配置するキャリアチャンネル。</param>
    /// <returns>周波数領域シンボル。</returns>
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
    /// ビット列から指定変調の複素シンボルを消費生成します。
    /// </summary>
    /// <param name="modulationScheme">消費する変調方式。</param>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="bits">ビット列。</param>
    /// <returns>変調された複素シンボル。</returns>
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
    /// ビット列から 1bit を読み、尽きていれば 0 を返します。
    /// </summary>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="bits">ビット列。</param>
    /// <returns>ビット値（不足時は false）。</returns>
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
    /// ビット列から指定ビット幅の整数フィールドを読み取ります。
    /// </summary>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="bits">ビット列。</param>
    /// <param name="bitCount">読み取るビット幅。</param>
    /// <returns>読み取ったビットフィールド。</returns>
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
    /// ビット列から BPSK シンボルを生成します。
    /// </summary>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="bits">ビット列。</param>
    /// <returns>BPSK 複素シンボル。</returns>
    private static Complex ConsumeBpskSymbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var bit = ReadBitOrZero(ref bitIndex, bits);
        // bit1 → +1、bit0 → -1（単位エネルギー、Imag=0）
        return new Complex(bit ? 1.0 : -1.0, 0.0);
    }

    /// <summary>
    /// ビット列から QPSK シンボルを生成します。
    /// </summary>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="bits">ビット列。</param>
    /// <returns>QPSK 複素シンボル。</returns>
    private static Complex ConsumeQpskSymbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var iBit = ReadBitOrZero(ref bitIndex, bits);
        var qBit = ReadBitOrZero(ref bitIndex, bits);
        var real = iBit ? 1.0 : -1.0;
        var imag = qBit ? 1.0 : -1.0;
        return new Complex(real, imag) / Math.Sqrt(2.0);
    }

    /// <summary>
    /// ビット列から 16QAM シンボルを生成します。
    /// </summary>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="bits">ビット列。</param>
    /// <returns>16QAM 複素シンボル。</returns>
    private static Complex ConsumeQam16Symbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var real = GrayMappedPamLevel(ReadBitField(ref bitIndex, bits, 2), bitsPerAxis: 2, Qam16Levels);
        var imag = GrayMappedPamLevel(ReadBitField(ref bitIndex, bits, 2), bitsPerAxis: 2, Qam16Levels);
        return new Complex(real, imag) / Math.Sqrt(10.0);
    }

    /// <summary>
    /// ビット列から 64QAM シンボルを生成します。
    /// </summary>
    /// <param name="bitIndex">ビット書き込み／読み出し位置。</param>
    /// <param name="bits">ビット列。</param>
    /// <returns>64QAM 複素シンボル。</returns>
    private static Complex ConsumeQam64Symbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var real = GrayMappedPamLevel(ReadBitField(ref bitIndex, bits, 3), bitsPerAxis: 3, Qam64Levels);
        var imag = GrayMappedPamLevel(ReadBitField(ref bitIndex, bits, 3), bitsPerAxis: 3, Qam64Levels);
        return new Complex(real, imag) / Math.Sqrt(42.0);
    }

    /// <summary>
    /// バイナリ索引を Gray マップして PAM レベル値を返します。
    /// </summary>
    /// <param name="binaryIndex">バイナリ索引。</param>
    /// <param name="bitsPerAxis">軸あたりのビット数。</param>
    /// <param name="levels">PAM 振幅レベル列。</param>
    /// <returns>グレイ写像した PAM レベル索引。</returns>
    private static int GrayMappedPamLevel(int binaryIndex, int bitsPerAxis, int[] levels)
    {
        var grayIndex = (binaryIndex ^ (binaryIndex >> 1)) & ((1 << bitsPerAxis) - 1);
        return levels[grayIndex];
    }

    /// <summary>
    /// チャンネルのアクティブ正周波数キャリアビン一覧を返します。
    /// </summary>
    /// <param name="channel">対象キャリアチャンネル。</param>
    /// <returns>アクティブキャリアビンリスト。</returns>
    private List<int> GetActiveCarrierBins(CarrierChannel channel)
    {
        // 実 OFDM は正周波数側のみを使用（GetPositiveCarrierBins と同一配置）。
        return GetPositiveCarrierBins(channel);
    }

    /// <summary>
    /// サブキャリア群中央のパイロットビン集合を選びます。
    /// </summary>
    /// <param name="orderedBins">周波数順キャリアビン。</param>
    /// <param name="spacing">パイロット間隔。</param>
    /// <returns>パイロットビン集合。</returns>
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
    /// インスタンス乱数でリストを in-place シャッフルします。
    /// </summary>
    /// <param name="values">シャッフル対象。</param>
    private void ShuffleInPlace(List<int> values) => ShuffleInPlace(values, _random);

    /// <summary>
    /// 指定乱数でリストを in-place シャッフルします。
    /// </summary>
    /// <param name="values">シャッフル対象。</param>
    /// <param name="random">乱数生成器。</param>
    private static void ShuffleInPlace(List<int> values, Random random)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    /// <summary>
    /// 指定乱数で配列を in-place シャッフルします。
    /// </summary>
    /// <param name="values">シャッフル対象。</param>
    /// <param name="random">乱数生成器。</param>
    private static void ShuffleInPlace(int[] values, Random random)
    {
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    /// <summary>
    /// 指定変調方式の乱数シンボルを生成します。
    /// </summary>
    /// <param name="modulationScheme">生成する変調方式。</param>
    /// <returns>ランダム変調シンボル。</returns>
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
    /// 乱数 BPSK シンボルを生成します。
    /// </summary>
    /// <returns>ランダム BPSK シンボル。</returns>
    private Complex GenerateBpskSymbol()
    {
        var bit = _random.Next(2);
        return new Complex(bit == 0 ? -1.0 : 1.0, 0.0);
    }

    /// <summary>
    /// 乱数 QPSK シンボルを生成します。
    /// </summary>
    /// <returns>ランダム QPSK シンボル。</returns>
    private Complex GenerateQpskSymbol()
    {
        // 1bit/軸の Gray マッピング（00,01,11,10 の隣接で 1bit 差）を適用する。
        var iBit = _random.Next(2);
        var qBit = _random.Next(2);

        var real = iBit == 0 ? -1.0 : 1.0;
        var imag = qBit == 0 ? -1.0 : 1.0;
        return new Complex(real, imag) / Math.Sqrt(2.0);
    }

    /// <summary>
    /// 乱数 16QAM シンボルを生成します。
    /// </summary>
    /// <returns>ランダム 16QAM シンボル。</returns>
    private Complex GenerateQam16Symbol()
    {
        // 2bit/軸を Gray マッピングして 4-PAM レベルへ変換する。
        var real = GenerateGrayMappedPamLevel(bitsPerAxis: 2, Qam16Levels);
        var imag = GenerateGrayMappedPamLevel(bitsPerAxis: 2, Qam16Levels);
        return new Complex(real, imag) / Math.Sqrt(10.0);
    }

    /// <summary>
    /// 乱数 64QAM シンボルを生成します。
    /// </summary>
    /// <returns>ランダム 64QAM シンボル。</returns>
    private Complex GenerateQam64Symbol()
    {
        // 3bit/軸を Gray マッピングして 8-PAM レベルへ変換する。
        var real = GenerateGrayMappedPamLevel(bitsPerAxis: 3, Qam64Levels);
        var imag = GenerateGrayMappedPamLevel(bitsPerAxis: 3, Qam64Levels);
        return new Complex(real, imag) / Math.Sqrt(42.0);
    }

    /// <summary>
    /// 乱数ビットを Gray マップして PAM レベルを生成します。
    /// </summary>
    /// <param name="bitsPerAxis">軸ビット数。</param>
    /// <param name="levels">PAM レベル数。</param>
    /// <returns>グレイ写像 PAM レベル。</returns>
    private int GenerateGrayMappedPamLevel(int bitsPerAxis, int[] levels)
    {
        var binaryIndex = NextBits(bitsPerAxis);
        // binary -> Gray（value ^ (value >> 1)）。gray_code.cs には依存しない。
        var grayIndex = (binaryIndex ^ (binaryIndex >> 1)) & ((1 << bitsPerAxis) - 1);
        return levels[grayIndex];
    }

    /// <summary>
    /// 乱数ビットプールから指定ビット数を取り出します。
    /// </summary>
    /// <param name="bitCount">取り出すビット数。</param>
    /// <returns>取り出したビットフィールド。</returns>
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
    /// 時間シンボルへ巡回プレフィックスを付与した配列を返します。
    /// </summary>
    /// <param name="symbol">CP なしシンボル。</param>
    /// <param name="cpLength">巡回プレフィックス長。</param>
    /// <returns>CP 付きシンボル。</returns>
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
    /// IFFT 用スクラッチバッファを必要サイズへ確保します。
    /// </summary>
    /// <param name="n">必要なスクラッチ長。</param>
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
    /// 周波数ビンの逆 FFT 結果を destination へ書き込みます。
    /// </summary>
    /// <param name="frequency">周波数領域入力。</param>
    /// <param name="destination">時間領域の書き込み先。</param>
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
    /// 周波数ビンの逆 FFT 結果を新しい配列で返します。
    /// </summary>
    /// <param name="frequency">周波数領域入力。</param>
    /// <returns>時間領域変換結果。</returns>
    private Complex[] InverseFft(Complex[] frequency)
    {
        EnsureIfftScratch(frequency.Length);
        InverseFftInto(frequency, _ifftWorkScratch!);
        var result = new Complex[frequency.Length];
        Array.Copy(_ifftWorkScratch!, result, frequency.Length);
        return result;
    }

    /// <summary>
    /// 複素共役用の SIMD 符号マスクベクトルを生成します。
    /// </summary>
    /// <returns>共役用符号マスク。</returns>
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
    /// 複素配列の共役を destination へ書き込みます。
    /// </summary>
    /// <param name="source">共役元。</param>
    /// <param name="destination">共役の書き込み先。</param>
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
    /// 複素配列を共役しスケールを in-place で適用します。
    /// </summary>
    /// <param name="values">共役・スケールする配列。</param>
    /// <param name="scale">スケール係数。</param>
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
    /// ReadOnlySpan から SIMD ベクトルを非整列ロードします。
    /// </summary>
    /// <param name="source">読み出し元。</param>
    /// <param name="index">開始索引。</param>
    /// <returns>読み出したベクトル。</returns>
    private static Vector<double> LoadVector(ReadOnlySpan<double> source, int index)
    {
        ref var first = ref MemoryMarshal.GetReference(source);
        ref var at = ref Unsafe.Add(ref first, index);
        return Unsafe.ReadUnaligned<Vector<double>>(ref Unsafe.As<double, byte>(ref at));
    }

    /// <summary>
    /// Span から SIMD ベクトルを非整列ロードします。
    /// </summary>
    /// <param name="source">読み出し元。</param>
    /// <param name="index">開始索引。</param>
    /// <returns>読み出したベクトル。</returns>
    private static Vector<double> LoadVector(Span<double> source, int index)
    {
        ref var first = ref MemoryMarshal.GetReference(source);
        ref var at = ref Unsafe.Add(ref first, index);
        return Unsafe.ReadUnaligned<Vector<double>>(ref Unsafe.As<double, byte>(ref at));
    }

    /// <summary>
    /// SIMD ベクトルを double スパンの指定位置へ格納します。
    /// </summary>
    /// <param name="destination">書き込み先。</param>
    /// <param name="index">開始索引。</param>
    /// <param name="value">書き込むベクトル。</param>
    private static void StoreVector(Span<double> destination, int index, Vector<double> value)
    {
        ref var first = ref MemoryMarshal.GetReference(destination);
        ref var at = ref Unsafe.Add(ref first, index);
        Unsafe.WriteUnaligned(ref Unsafe.As<double, byte>(ref at), value);
    }

    /// <summary>
    /// 複素配列のコピーに対して前方 FFT を行い結果を返します。
    /// </summary>
    /// <param name="input">時間または周波数領域入力。</param>
    /// <returns>FFT 結果。</returns>
    private static Complex[] Fft(Complex[] input)
    {
        var output = new Complex[input.Length];
        Array.Copy(input, output, input.Length);
        FftInPlace(output);
        return output;
    }

    /// <summary>
    /// 複素配列に対して in-place 前方 FFT を実行します。
    /// </summary>
    /// <param name="output">インプレース変換バッファ。</param>
    private static void FftInPlace(Complex[] output)
    {
        var n = output.Length;

        // ビット反転並べ替えを伴う反復型 radix-2 Cooley-Tukey FFT。
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
            // 部分 DFT 長を段階的に増やすバタフライ演算。
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
    /// 指定ビット幅で整数のビット順を反転します。
    /// </summary>
    /// <param name="value">ビット反転する整数。</param>
    /// <param name="bitCount">有効ビット幅。</param>
    /// <returns>ビット反転結果。</returns>
    private static int ReverseBits(int value, int bitCount)
    {
        // バタフライ演算前のビット反転並べ替えで使用する。
        var reversed = 0;
        for (var i = 0; i < bitCount; i++)
        {
            reversed = (reversed << 1) | (value & 1);
            value >>= 1;
        }

        return reversed;
    }
}
