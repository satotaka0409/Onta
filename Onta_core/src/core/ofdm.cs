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
    /// 8PSK（3 bit/symbol）。
    /// </summary>
    Psk8 = 6,

    /// <summary>
    /// 16QAM（4 bit/symbol）。
    /// </summary>
    Qam16 = 3,

    /// <summary>
    /// 64QAM（6 bit/symbol）。
    /// </summary>
    Qam64 = 4,

    /// <summary>
    /// 256QAM（8 bit/symbol）。性能測定時のみ使用。
    /// </summary>
    Qam256 = 5
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
    Sc8Family = 0,

    Sc24Family = 1
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
    /// 有効サブキャリア数（16/24/32/40/48/56/64）。
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
    /// <param name="pilotSpacing">パイロット間隔。</param>
    /// <param name="stereoFrequencyShiftBins">ステレオ時の左右キャリアずらし量。</param>
    /// <param name="sampleRate">サンプルレート（Hz）。</param>
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
        int pilotSpacing = 8,
        int stereoFrequencyShiftBins = 1,
        int sampleRate = 44100,
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
        PilotSpacing = pilotSpacing;
        StereoFrequencyShiftBins = stereoFrequencyShiftBins;
        SampleRate = sampleRate;
        RandomSeed = randomSeed;
        ConceptualLeftBins = conceptualLeftBins ?? ResolveConceptualLeftBins(activeSubcarriers);
        CarrierGrid = carrierGrid ?? ResolveCarrierGrid(activeSubcarriers);

        if (FftSize <= 0 || (FftSize & (FftSize - 1)) != 0)
        {
            throw new ArgumentException("FFT size must be a power of two and > 0.");
        }

        if (!IsSupportedActiveSubcarriers(ActiveSubcarriers))
        {
            throw new ArgumentException(
            "Active subcarriers must be 16, 24, 32, 40, 48, 56, or 64.",
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
            and not ModulationScheme.Psk8
            and not ModulationScheme.Qam16
            and not ModulationScheme.Qam64
            and not ModulationScheme.Qam256)
        {
            throw new ArgumentException(
                "Modulation scheme must be BPSK, QPSK, 8PSK, 16QAM, 64QAM, or 256QAM.",
                nameof(modulationScheme));
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

        ValidateCarrierBinsFitFft();
    }

    /// <summary>
    /// 概念キャリア周波数を FFT 正周波数ビンへ割り当て可能か検証します。範囲外なら例外です。
    /// </summary>
    private void ValidateCarrierBinsFitFft()
    {
        foreach (var k in ConceptualLeftBins)
        {
            double leftHz;
            double rightHz;
            if (CarrierGrid == OfdmCarrierGrid.Sc8Family)
            {
                leftHz = LeftCarrierHzSc8(k - 1);
                rightHz = RightCarrierHzSc8(k - 1);
            }
            else
            {
                leftHz = LeftCarrierHzSc24(k);
                rightHz = RightCarrierHzSc24(k);
            }

            var leftBin = HzToPositiveBin(leftHz, FftSize, SampleRate);
            var rightBin = HzToPositiveBin(rightHz, FftSize, SampleRate);
            if (leftBin <= 0 || leftBin >= FftSize / 2 || rightBin <= 0 || rightBin >= FftSize / 2)
            {
                throw new ArgumentException(
                    $"Carrier Hz (L={leftHz:F1}, R={rightHz:F1}) does not fit FFT={FftSize}.",
                    nameof(FftSize));
            }
        }
    }

    /// <summary>
    /// Group A の左チャネルキャリア番号を返します。
    /// </summary>
    /// <returns>Group A のキャリア番号配列。</returns>
    public static int[] ResolveGroupALeftBins() => CopyBins(GroupALeftBins);

    /// <summary>
    /// Group B の左チャネルキャリア番号を返します。
    /// </summary>
    /// <returns>Group B のキャリア番号配列。</returns>
    public static int[] ResolveGroupBLeftBins() => CopyBins(GroupBLeftBins);

    /// <summary>
    /// Group C の左チャネルキャリア番号を返します。
    /// </summary>
    /// <returns>Group C のキャリア番号配列。</returns>
    public static int[] ResolveGroupCLeftBins() => CopyBins(GroupCLeftBins);

    /// <summary>
    /// Group D の左チャネルキャリア番号を返します。
    /// </summary>
    /// <returns>Group D のキャリア番号配列。</returns>
    public static int[] ResolveGroupDLeftBins() => CopyBins(GroupDLeftBins);

    /// <summary>
    /// Group E の左チャネルキャリア番号を返します。
    /// </summary>
    /// <returns>Group E のキャリア番号配列。</returns>
    public static int[] ResolveGroupELeftBins() => CopyBins(GroupELeftBins);

    /// <summary>
    /// Group F の左チャネルキャリア番号を返します。
    /// </summary>
    /// <returns>Group F のキャリア番号配列。</returns>
    public static int[] ResolveGroupFLeftBins() => CopyBins(GroupFLeftBins);

    /// <summary>
    /// Group G の左チャネルキャリア番号を返します。
    /// </summary>
    /// <returns>Group G のキャリア番号配列。</returns>
    public static int[] ResolveGroupGLeftBins() => CopyBins(GroupGLeftBins);

    /// <summary>
    /// Group H の左チャネルキャリア番号を返します。
    /// </summary>
    /// <returns>Group H のキャリア番号配列。</returns>
    public static int[] ResolveGroupHLeftBins() => CopyBins(GroupHLeftBins);

    /// <summary>
    /// サブキャリア数に応じた概念左キャリア番号列を返します。
    /// </summary>
    /// <param name="activeSubcarriers">有効サブキャリア数。</param>
    /// <returns>概念左キャリア番号配列。</returns>
    public static int[] ResolveConceptualLeftBins(int activeSubcarriers) =>
        activeSubcarriers switch
        {
            16 => CopyBins(ConceptualLeftBins16),
            24 => CopyBins(ConceptualLeftBins24),
            32 => CopyBins(ConceptualLeftBins32),
            40 => CopyBins(ConceptualLeftBins40),
            48 => CopyBins(ConceptualLeftBins48),
            56 => CopyBins(ConceptualLeftBins56),
            64 => CopyBins(ConceptualLeftBins64),
            _ => throw new ArgumentOutOfRangeException(
                nameof(activeSubcarriers),
                activeSubcarriers,
                "Active subcarriers must be 16, 24, 32, 40, 48, 56, or 64.")
        };

    private static readonly int[] GroupALeftBins = CreateRange(1, 8);
    private static readonly int[] GroupBLeftBins = CreateRange(9, 8);
    private static readonly int[] GroupCLeftBins = CreateRange(17, 8);
    private static readonly int[] GroupDLeftBins = CreateRange(25, 8);
    private static readonly int[] GroupELeftBins = CreateRange(33, 8);
    private static readonly int[] GroupFLeftBins = CreateRange(41, 8);
    private static readonly int[] GroupGLeftBins = CreateRange(49, 8);
    private static readonly int[] GroupHLeftBins = CreateRange(57, 8);
    private static readonly int[] ConceptualLeftBins16 = ConcatBins(GroupALeftBins, GroupBLeftBins);
    private static readonly int[] ConceptualLeftBins24 = ConcatBins(GroupALeftBins, GroupBLeftBins, GroupCLeftBins);
    private static readonly int[] ConceptualLeftBins32 = ConcatBins(GroupALeftBins, GroupBLeftBins, GroupCLeftBins, GroupDLeftBins);
    private static readonly int[] ConceptualLeftBins40 = ConcatBins(GroupALeftBins, GroupBLeftBins, GroupCLeftBins, GroupDLeftBins, GroupELeftBins);
    private static readonly int[] ConceptualLeftBins48 = ConcatBins(GroupALeftBins, GroupBLeftBins, GroupCLeftBins, GroupDLeftBins, GroupELeftBins, GroupFLeftBins);
    private static readonly int[] ConceptualLeftBins56 = ConcatBins(GroupALeftBins, GroupBLeftBins, GroupCLeftBins, GroupDLeftBins, GroupELeftBins, GroupFLeftBins, GroupGLeftBins);
    private static readonly int[] ConceptualLeftBins64 = ConcatBins(GroupALeftBins, GroupBLeftBins, GroupCLeftBins, GroupDLeftBins, GroupELeftBins, GroupFLeftBins, GroupGLeftBins, GroupHLeftBins);

    /// <summary>
    /// start から count 個の連続整数配列を生成します。
    /// </summary>
    /// <param name="start">開始値。</param>
    /// <param name="count">要素数。</param>
    /// <returns>連続整数配列。</returns>
    private static int[] CreateRange(int start, int count)
    {
        var bins = new int[count];
        for (var i = 0; i < count; i++)
        {
            bins[i] = start + i;
        }

        return bins;
    }

    /// <summary>
    /// 複数のキャリア番号配列を連結して 1 本にします。
    /// </summary>
    /// <param name="groups">連結するキャリア番号配列の並び。</param>
    /// <returns>連結後のキャリア番号配列。</returns>
    private static int[] ConcatBins(params int[][] groups)
    {
        var length = 0;
        for (var i = 0; i < groups.Length; i++)
        {
            length += groups[i].Length;
        }

        var result = new int[length];
        var offset = 0;
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            Array.Copy(group, 0, result, offset, group.Length);
            offset += group.Length;
        }

        return result;
    }

    /// <summary>
    /// キャリア番号配列の防御的コピーを返します。
    /// </summary>
    /// <param name="source">コピー元配列。</param>
    /// <returns>コピーされた配列。</returns>
    private static int[] CopyBins(int[] source)
    {
        var copy = new int[source.Length];
        Array.Copy(source, copy, source.Length);
        return copy;
    }

    /// <summary>
    /// サブキャリア構成の最大概念左キャリア番号を返します。
    /// </summary>
    /// <param name="activeSubcarriers">有効サブキャリア数。</param>
    /// <returns>最大概念左キャリア番号。</returns>
    public static int MaxConceptualLeftBin(int activeSubcarriers) =>
        ResolveConceptualLeftBins(activeSubcarriers)[^1];

    /// <summary>全 SC 共通の搬送波間隔（Hz）。</summary>
    public const double CarrierSpacingHz = 223.9;

    /// <summary>SC-24/32/40/48/56/64 系列のキャリア間隔（Hz）を返します（共通間隔）。</summary>
    /// <param name="sampleRate">サンプルレート（Hz）。互換のため残置。未使用。</param>
    /// <returns>キャリア間隔（Hz）。</returns>
    public static double DeltaF24(int sampleRate = 44100) => CarrierSpacingHz;

    /// <summary>SC-8/16 系列のキャリア間隔（Hz）を返します（共通間隔）。</summary>
    /// <param name="sampleRate">サンプルレート（Hz）。互換のため残置。未使用。</param>
    /// <returns>キャリア間隔（Hz）。</returns>
    public static double DeltaF8(int sampleRate = 44100) => CarrierSpacingHz;

    /// <summary>SC-8/16 系列の下限周波数（GROUP A CH0 の L、Hz）。</summary>
    public const double Sc8StartHz = 650.0;

    /// <summary>SC-24/32/40/48/56/64 系列の下限周波数（GROUP A CH0 の L、Hz）。</summary>
    public const double Sc24StartHz = 550.0;

    /// <summary>SC-8/16 系列の左チャネルキャリア周波数を返します。</summary>
    /// <param name="zeroBasedChannelIndex">0 起点のチャネル番号（A0=0, B0=8）。</param>
    /// <param name="sampleRate">サンプルレート（Hz）。互換のため残置。未使用。</param>
    /// <returns>キャリア周波数（Hz）。</returns>
    public static double LeftCarrierHzSc8(int zeroBasedChannelIndex, int sampleRate = 44100) =>
        Sc8StartHz + zeroBasedChannelIndex * CarrierSpacingHz;

    /// <summary>SC-8/16 系列の右チャネルキャリア周波数を返します。</summary>
    /// <param name="zeroBasedChannelIndex">0 起点のチャネル番号。</param>
    /// <param name="sampleRate">サンプルレート（Hz）。互換のため残置。未使用。</param>
    /// <returns>キャリア周波数（Hz）。</returns>
    public static double RightCarrierHzSc8(int zeroBasedChannelIndex, int sampleRate = 44100) =>
        LeftCarrierHzSc8(zeroBasedChannelIndex, sampleRate) + (CarrierSpacingHz / 2.0);

    /// <summary>SC-24/32/40/48/56/64 系列の左チャネルキャリア周波数を返します。</summary>
    /// <param name="conceptualLeftBin">概念左キャリア番号（1..64）。</param>
    /// <param name="sampleRate">サンプルレート（Hz）。互換のため残置。未使用。</param>
    /// <returns>キャリア周波数（Hz）。</returns>
    public static double LeftCarrierHzSc24(int conceptualLeftBin, int sampleRate = 44100) =>
        Sc24StartHz + (conceptualLeftBin - 1) * CarrierSpacingHz;

    /// <summary>SC-24/32/40/48/56/64 系列の右チャネルキャリア周波数を返します。</summary>
    /// <param name="conceptualLeftBin">概念左キャリア番号（1..64）。</param>
    /// <param name="sampleRate">サンプルレート（Hz）。互換のため残置。未使用。</param>
    /// <returns>キャリア周波数（Hz）。</returns>
    public static double RightCarrierHzSc24(int conceptualLeftBin, int sampleRate = 44100) =>
        LeftCarrierHzSc24(conceptualLeftBin, sampleRate) + (CarrierSpacingHz / 2.0);

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
    /// ファイル送受信または性能測定で使えるサブキャリア数か判定します。
    /// </summary>
    /// <param name="activeSubcarriers">有効サブキャリア数。</param>
    /// <returns>16/24/32/40/48/56/64 なら true。</returns>
    public static bool IsSupportedActiveSubcarriers(int activeSubcarriers) =>
        activeSubcarriers is 16 or 24 or 32 or 40 or 48 or 56 or 64;

    /// <summary>
    /// サブキャリア数からキャリアグリッド種別を解決します。
    /// </summary>
    /// <param name="activeSubcarriers">有効サブキャリア数。</param>
    /// <returns>キャリアグリッド種別。</returns>
    public static OfdmCarrierGrid ResolveCarrierGrid(int activeSubcarriers) =>
        activeSubcarriers == 16 ? OfdmCarrierGrid.Sc8Family : OfdmCarrierGrid.Sc24Family;

    /// <summary>
    /// 概念左キャリア番号からサブキャリアグループ ID（0=A..7=H）を返します。
    /// </summary>
    /// <param name="conceptualLeftBin">概念左キャリア番号（1..64）。</param>
    /// <returns>グループ ID。</returns>
    public static byte ResolveSubcarrierGroupId(int conceptualLeftBin) =>
        conceptualLeftBin switch
        {
            >= 1 and <= 8 => 0,
            >= 9 and <= 16 => 1,
            >= 17 and <= 24 => 2,
            >= 25 and <= 32 => 3,
            >= 33 and <= 40 => 4,
            >= 41 and <= 48 => 5,
            >= 49 and <= 56 => 6,
            >= 57 and <= 64 => 7,
            _ => throw new ArgumentOutOfRangeException(
                nameof(conceptualLeftBin),
                conceptualLeftBin,
                "Conceptual left bin must be in range 1..64.")
        };

    /// <summary>
    /// 固定 FFT サイズ（SC 数・チャネル構成によらない）。
    /// </summary>
    public const int FixedFftSize = 256;

    /// <summary>
    /// サブキャリア構成とチャネルモードから推奨FFTサイズを返します（常に 256）。
    /// </summary>
    /// <param name="activeSubcarriers">有効サブキャリア数（互換のため残置。未使用）。</param>
    /// <param name="channelMode">チャネルモード（互換のため残置。未使用）。</param>
    /// <returns>固定 FFT サイズ 256。</returns>
    public static int ResolveFftSize(int activeSubcarriers, ChannelMode channelMode) => FixedFftSize;
}

/// <summary>
/// OFDM 変調/復調の本体実装です。
/// </summary>
public sealed partial class OfdmGenerator
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
    private readonly Dictionary<int, byte> _leftDataCarrierGroupByBin;
    private readonly int[][] _leftPilotGroupedCarriers;
    private readonly List<int> _rightAllCarrierBins;
    private readonly List<int> _rightPilotBins;
    private readonly List<int> _rightDataCarrierBase;
    private readonly Dictionary<int, ModulationScheme> _rightDataCarrierModulationByBin;
    private readonly Dictionary<int, byte> _rightDataCarrierGroupByBin;
    private readonly int[][] _rightPilotGroupedCarriers;
    private readonly Complex[] _scoreTimeNoCpScratch;
    private readonly Complex[] _scoreFreqBinsScratch;
    private Complex[]? _ifftConjugateScratch;
    private Complex[]? _ifftWorkScratch;
    private Complex[]? _freqSymbolScratchA;
    private Complex[]? _freqSymbolScratchB;
    private Complex[]? _unmodulatedLeftSymbol;
    private Complex[]? _unmodulatedRightSymbol;
    private readonly int _bitsPerOfdmSymbol;
    private ulong _randomBitPool;
    private int _randomBitCount;
    private static readonly int[] Qam16Levels = [-3, -1, 1, 3];
    private static readonly int[] Qam64Levels = [-7, -5, -3, -1, 1, 3, 5, 7];
    private static readonly int[] Qam256Levels = [-15, -13, -11, -9, -7, -5, -3, -1, 1, 3, 5, 7, 9, 11, 13, 15];
    private static readonly double[] Qam16PamByBinary = BuildPamByBinary(2, Qam16Levels);
    private static readonly double[] Qam64PamByBinary = BuildPamByBinary(3, Qam64Levels);
    private static readonly double[] Qam256PamByBinary = BuildPamByBinary(4, Qam256Levels);
    private static readonly byte[] Psk8BitsByPhaseIndex =
    [
        0b000,
        0b001,
        0b011,
        0b010,
        0b110,
        0b111,
        0b101,
        0b100
    ];
    private static readonly byte[] Psk8PhaseIndexByBits = BuildPsk8PhaseIndexByBits();
    private static readonly Complex[] Psk8Symbols = BuildPsk8Symbols();
    private static readonly double InvSqrt2 = 1.0 / Math.Sqrt(2.0);
    private static readonly double InvSqrt10 = 1.0 / Math.Sqrt(10.0);
    private static readonly double InvSqrt42 = 1.0 / Math.Sqrt(42.0);
    private static readonly double InvSqrt170 = 1.0 / Math.Sqrt(170.0);
    private static readonly Complex PilotSymbol = Complex.One;
    private static readonly Vector256<double> RealLaneMask = Vector256.Create(1.0, 0.0, 1.0, 0.0);
    private static readonly Vector<double> ConjugateSignMask = CreateConjugateSignMask();
    private const int TrigQuarterTableSize = 4096;
    private const double HalfPi = Math.PI * 0.5;
    private const double TwoPi = Math.PI * 2.0;
    private static readonly double[] SinQuarterLut = BuildSinQuarterLut();
    /// <summary>
    /// sin 四分円テーブルの添字変換係数です。
    /// </summary>
    private static readonly double TrigQuarterIndexScale = TrigQuarterTableSize / HalfPi;

    /// <summary>
    /// 現在のチャネルモードを返します。
    /// </summary>
    public ChannelMode ChannelMode => _config.ChannelMode;

    /// <summary>
    /// 有効サブキャリア数を返します。
    /// </summary>
    public int ActiveSubcarriers => _config.ActiveSubcarriers;

    /// <summary>
    /// キャリア周波数グリッド種別を返します。
    /// </summary>
    public OfdmCarrierGrid CarrierGrid => _config.CarrierGrid;

    /// <summary>
    /// 無変調サブキャリアを表す単位複素数です。
    /// </summary>
    private static readonly Complex UnmodulatedCarrierSymbol = Complex.One;

    /// <summary>
    /// OFDM 生成／復調器を設定から初期化します。
    /// </summary>
    /// <param name="config">FFT／SC／変調などの OFDM 設定。</param>
    public OfdmGenerator(OfdmConfig config)
    {
        _config = config;
        _random = config.RandomSeed == 0 ? Random.Shared : new Random(config.RandomSeed);

        (_leftAllCarrierBins, _leftPilotBins, _leftDataCarrierBase, _leftDataCarrierModulationByBin, _leftDataCarrierGroupByBin) =
            BuildChannelLayout(CarrierChannel.Left);
        (_rightAllCarrierBins, _rightPilotBins, _rightDataCarrierBase, _rightDataCarrierModulationByBin, _rightDataCarrierGroupByBin) =
            BuildChannelLayout(CarrierChannel.Right);
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
    /// 送信側 FFT 可視化用。OFDM シンボルの周波数ビンを通知します（第2引数は右チャネルなら true）。
    /// </summary>
    /// <param name="bins">通知する周波数領域ビン。</param>
    /// <param name="isRightChannel">右チャネルの場合 true、左チャネルの場合 false。</param>
    public delegate void TxSpectrumHandler(ReadOnlySpan<Complex> bins, bool isRightChannel);

    /// <summary>
    /// 送信スペクトル通知ハンドラです。
    /// </summary>
    public TxSpectrumHandler? TxSpectrumObserver { get; set; }

    /// <summary>
    /// 送信スペクトル通知の間引き間隔（シンボル数）。1 で毎シンボル。
    /// </summary>
    public int TxSpectrumStride { get; set; } = 4;

    private int _txSpectrumCounter;

    /// <summary>
    /// L または R のキャリア配置（全ビン／パイロット／データ／変調／グループ）を構築します。
    /// </summary>
    /// <param name="channel">L/R キャリアチャネル。</param>
    /// <returns>全キャリア、パイロット、データ基底、変調辞書、グループ辞書。</returns>
    private (List<int> All, List<int> Pilots, List<int> DataBase, Dictionary<int, ModulationScheme> DataModulationByBin, Dictionary<int, byte> DataGroupByBin)
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
        var dataGroupByBin = new Dictionary<int, byte>(all.Count);
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
            dataGroupByBin[bin] = OfdmConfig.ResolveSubcarrierGroupId(conceptualLeftBin);
        }

        return (all, pilots, data, dataModulationByBin, dataGroupByBin);
    }

    /// <summary>
    /// パイロットが担当する下側チャネル数（自身を含まない）。
    /// </summary>
    private const int PilotCoverChannelsBelow = 2;

    /// <summary>
    /// パイロットが担当する上側チャネル数（自身を含まない）。
    /// </summary>
    private const int PilotCoverChannelsAbove = 1;

    /// <summary>
    /// 各パイロットが担当する下 2CH・自身・上 1CH のキャリア束を構築します。
    /// </summary>
    /// <param name="allCarriers">チャネル内の全キャリアビン（昇順）。</param>
    /// <param name="orderedPilots">パイロットビン一覧（CH2/CH6 相当）。</param>
    /// <returns>パイロットごとの担当キャリアビン配列。</returns>
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

        var carrierIndexByBin = new Dictionary<int, int>(allCarriers.Count);
        for (var i = 0; i < allCarriers.Count; i++)
        {
            carrierIndexByBin[allCarriers[i]] = i;
        }

        var groupSize = 8;
        for (var pi = 0; pi < orderedPilots.Count; pi++)
        {
            if (!carrierIndexByBin.TryGetValue(orderedPilots[pi], out var pilotIndex))
            {
                continue;
            }

            var groupStart = (pilotIndex / groupSize) * groupSize;
            var groupEnd = Math.Min(groupStart + groupSize, allCarriers.Count) - 1;
            var coverStart = Math.Max(groupStart, pilotIndex - PilotCoverChannelsBelow);
            var coverEnd = Math.Min(groupEnd, pilotIndex + PilotCoverChannelsAbove);
            for (var i = coverStart; i <= coverEnd; i++)
            {
                grouped[pi].Add(allCarriers[i]);
            }
        }

        var result = new int[grouped.Length][];
        for (var i = 0; i < grouped.Length; i++)
        {
            result[i] = grouped[i].ToArray();
        }

        return result;
    }

    /// <summary>
    /// Group G/H など、変調を 1 段下げる対象の概念左ビンか判定します。
    /// </summary>
    /// <param name="conceptualLeftBin">概念左キャリア番号。</param>
    /// <returns>変調ダウングレード対象なら true。</returns>
    private static bool IsDowngradedGroupConceptualLeftBin(int conceptualLeftBin) =>
        conceptualLeftBin is >= 49 and <= 64;

    /// <summary>
    /// 設定変調と概念ビンから当該キャリアの実効変調を決定します（E/F 等は 1 段下げ）。
    /// </summary>
    /// <param name="configuredScheme">データ部の設定変調。</param>
    /// <param name="conceptualLeftBin">概念左キャリア番号。</param>
    /// <returns>実効変調方式。</returns>
    private static ModulationScheme ResolveEffectiveCarrierModulation(
        ModulationScheme configuredScheme,
        int conceptualLeftBin)
    {
        if (!IsDowngradedGroupConceptualLeftBin(conceptualLeftBin))
        {
            return configuredScheme;
        }

        return configuredScheme switch
        {
            ModulationScheme.Qam256 => ModulationScheme.Qam64,
            ModulationScheme.Qam64 => ModulationScheme.Qam16,
            ModulationScheme.Qam16 => ModulationScheme.Psk8,
            ModulationScheme.Psk8 => ModulationScheme.Qpsk,
            ModulationScheme.Qpsk => ModulationScheme.Bpsk,
            ModulationScheme.Bpsk => ModulationScheme.Bpsk,
            _ => configuredScheme
        };
    }

    /// <summary>
    /// 変調方式 1 シンボルあたりのデータビット数を返します。
    /// </summary>
    /// <param name="modulationScheme">変調方式。</param>
    /// <returns>ビット数。</returns>
    private static int BitsPerModulation(ModulationScheme modulationScheme) => modulationScheme switch
    {
        ModulationScheme.Bpsk => 1,
        ModulationScheme.Qpsk => 2,
        ModulationScheme.Psk8 => 3,
        ModulationScheme.Qam16 => 4,
        ModulationScheme.Qam64 => 6,
        ModulationScheme.Qam256 => 8,
        _ => throw new InvalidOperationException("Unsupported modulation scheme.")
    };

    /// <summary>
    /// データキャリアの走査順（パイロット除外・キャッシュ済み）を返します。
    /// </summary>
    /// <param name="useRightChannel">R 搬送波レイアウトを使うか。</param>
    /// <returns>データキャリアビン順。</returns>
    private List<int> ResolveDataCarrierOrder(bool useRightChannel) =>
        useRightChannel ? _rightDataCarrierBase : _leftDataCarrierBase;

    /// <summary>
    /// 概念左ビンを正周波数 FFT ビンへ割り当て、チャネルのキャリア一覧を返します。
    /// </summary>
    /// <param name="channel">L/R キャリアチャネル。</param>
    /// <returns>正周波数キャリアビン一覧（昇順）。</returns>
    private List<int> GetPositiveCarrierBins(CarrierChannel channel)
    {
        var conceptBins = _config.ConceptualLeftBins;
        var bins = new List<int>(conceptBins.Count);
        var used = new HashSet<int>();
        var useRight = channel == CarrierChannel.Right
            && _config.ChannelMode == ChannelMode.Stereo;

        foreach (var k in conceptBins)
        {
            double hz;
            if (_config.CarrierGrid == OfdmCarrierGrid.Sc8Family)
            {
                hz = useRight
                    ? OfdmConfig.RightCarrierHzSc8(k - 1, _config.SampleRate)
                    : OfdmConfig.LeftCarrierHzSc8(k - 1, _config.SampleRate);
            }
            else
            {
                hz = useRight
                    ? OfdmConfig.RightCarrierHzSc24(k, _config.SampleRate)
                    : OfdmConfig.LeftCarrierHzSc24(k, _config.SampleRate);
            }

            var bin = OfdmConfig.HzToPositiveBin(hz, _config.FftSize, _config.SampleRate);
            bin = EnsureUniquePositiveBin(bin, used);
            AddPositiveBin(bins, bin);
        }

        return bins;
    }

    /// <summary>
    /// 希望ビンが未使用ならそれを、使用済みなら近傍の未使用正周波数ビンを割り当てます。
    /// </summary>
    /// <param name="preferred">希望する正周波数ビン。</param>
    /// <param name="used">既に割当済みのビン集合。</param>
    /// <returns>割当した正周波数ビン。</returns>
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
    /// 正周波数ビンをリストへ追加します（無効値は無視）。
    /// </summary>
    /// <param name="bins">追加先リスト。</param>
    /// <param name="bin">追加するビン番号。</param>
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
    /// 実信号化のため、正周波数ビンの複素共役を負周波数側へミラーします。
    /// </summary>
    /// <param name="bins">周波数領域シンボル（破壊的）。</param>
    private static void ApplyHermitianSymmetry(Complex[] bins)
    {
        var n = bins.Length;
        bins[0] = new Complex(bins[0].Real, 0.0);
        if ((n & 1) == 0)
        {
            bins[n / 2] = new Complex(bins[n / 2].Real, 0.0);
        }

        var half = n / 2;
        for (var k = 1; k < half; k++)
        {
            var c = bins[k];
            bins[n - k] = new Complex(c.Real, -c.Imaginary);
        }
    }

    /// <summary>
    /// 周波数領域シンボルにエルミート対称を付け IFFT し、実時間シンボルを返します。
    /// </summary>
    /// <param name="freqBins">周波数領域ビン。</param>
    /// <returns>時間領域の実シンボル（CP なし）。</returns>
    private Complex[] ToRealTimeSymbol(Complex[] freqBins)
    {
        ApplyHermitianSymmetry(freqBins);
        EnsureIfftScratch(freqBins.Length);
        InverseFftInto(freqBins, _ifftWorkScratch!);
        var time = new Complex[freqBins.Length];
        Array.Copy(_ifftWorkScratch!, time, time.Length);
        SimdMath.ZeroImagInPlace(time);
        return time;
    }

    /// <summary>
    /// 周波数領域から実時間シンボルを生成し、CP 付きで destination へ書きます。
    /// </summary>
    /// <param name="freqBins">周波数領域ビン。</param>
    /// <param name="destinationWithCp">CP 付き出力バッファ。</param>
    private void EmitRealTimeSymbolWithCp(Complex[] freqBins, Span<Complex> destinationWithCp)
    {
        ApplyHermitianSymmetry(freqBins);
        EnsureIfftScratch(freqBins.Length);
        InverseFftInto(freqBins, _ifftWorkScratch!);
        var time = _ifftWorkScratch!;
        SimdMath.ZeroImagInPlace(time);
        CopyWithCyclicPrefix(time, _config.CyclicPrefixLength, destinationWithCp);
    }

    /// <summary>
    /// 内部パラメータです。
    /// </summary>
    public int BitsPerModulationSymbol => _config.ModulationScheme switch
    {
        ModulationScheme.Bpsk => 1,
        ModulationScheme.Qpsk => 2,
        ModulationScheme.Psk8 => 3,
        ModulationScheme.Qam16 => 4,
        ModulationScheme.Qam64 => 6,
        ModulationScheme.Qam256 => 8,
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
    /// ランダムビット列から OFDM フレーム（複数シンボル）を生成します。
    /// </summary>
    /// <returns>CP 付き時間領域サンプル列。</returns>
    public Complex[] GenerateFrame()
    {
        if (_config.ChannelMode == ChannelMode.Stereo)
        {
            throw new InvalidOperationException("Stereo mode requires GenerateStereoFrame().");
        }

        var symbolLength = SamplesPerOfdmSymbol;
        var frame = new Complex[_config.OfdmSymbolCount * symbolLength];
        var write = 0;
        var freqBins = EnsureFreqSymbolScratch(ref _freqSymbolScratchA);

        for (var i = 0; i < _config.OfdmSymbolCount; i++)
        {
            BuildFrequencyDomainSymbol(CarrierChannel.Left, freqBins);
            EmitRealTimeSymbolWithCp(freqBins, frame.AsSpan(write, symbolLength));
            write += symbolLength;
        }

        return frame;
    }

    /// <summary>
    /// ステレオ用に L/R 別キャリアの OFDM フレームを生成します。
    /// </summary>
    /// <returns>L/R の CP 付き時間領域サンプル列。</returns>
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
        var leftBins = EnsureFreqSymbolScratch(ref _freqSymbolScratchA);
        var rightBins = EnsureFreqSymbolScratch(ref _freqSymbolScratchB);

        for (var i = 0; i < _config.OfdmSymbolCount; i++)
        {
            BuildFrequencyDomainSymbol(CarrierChannel.Left, leftBins);
            BuildFrequencyDomainSymbol(CarrierChannel.Right, rightBins);

            EmitRealTimeSymbolWithCp(leftBins, left.AsSpan(write, symbolLength));
            EmitRealTimeSymbolWithCp(rightBins, right.AsSpan(write, symbolLength));
            write += symbolLength;
        }

        return (left, right);
    }

    /// <summary>
    /// 全キャリア無変調の連続波形を生成します（プリアンブル等）。
    /// </summary>
    /// <param name="sampleCount">出力サンプル数。</param>
    /// <returns>L（およびステレオ時 R）の無変調 PCM。モノラル時 R は空配列。</returns>
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
    /// 指定キャリアを無変調で立て、指定サンプル数の無変調波形を生成します。
    /// </summary>
    /// <param name="sampleCount">出力サンプル数。</param>
    /// <param name="carrierBins">無変調キャリアビン。</param>
    /// <returns>無変調 PCM。</returns>
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
    /// 無変調 OFDM シンボル（CP 付き）をキャッシュから取得、なければ構築します。
    /// </summary>
    /// <param name="carrierBins">無変調キャリアビン。</param>
    /// <returns>CP 付き無変調シンボル。</returns>
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
    /// 指定キャリアを無変調で埋めた CP 付き OFDM シンボルを構築します。
    /// </summary>
    /// <param name="carrierBins">無変調キャリアビン。</param>
    /// <returns>CP 付き無変調シンボル。</returns>
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
    /// 時間領域シンボル末尾を先頭へ複製し、CP 付きシンボルを destination へ書きます。
    /// </summary>
    /// <param name="symbol">CP なし時間領域シンボル。</param>
    /// <param name="cpLength">CP 長。</param>
    /// <param name="destination">CP 付き出力バッファ。</param>
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
    /// 指定ビット数の送信に必要な OFDM サンプル数（CP 込み）を計算します。
    /// </summary>
    /// <param name="bitCount">送信ビット数。</param>
    /// <returns>必要サンプル数。</returns>
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
    /// 診断用に、指定ワウ／フラッターパラメータでのプリアンブル相関スコアを返します。
    /// </summary>
    /// <param name="samples">入力 PCM。</param>
    /// <param name="useRightChannel">R 搬送波レイアウトを使うか。</param>
    /// <param name="analysisStartSample">解析開始サンプル。</param>
    /// <param name="analysisSampleCount">解析サンプル数。</param>
    /// <param name="amount">ワウ量。</param>
    /// <param name="wowPhase">ワウ位相。</param>
    /// <param name="flutterPhase">フラッター位相。</param>
    /// <returns>相関スコア（大きいほど一致）。</returns>
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
        var idealReals = new double[analysisSampleCount];
        SimdMath.CopyComplexReals(ideal.AsSpan(0, analysisSampleCount), idealReals);
        var refCorr = BuildCorrelationReferenceReals(idealReals);
        var sumScratch = System.Buffers.ArrayPool<double>.Shared.Rent(analysisSampleCount);
        var countScratch = System.Buffers.ArrayPool<int>.Shared.Rent(analysisSampleCount);
        try
        {
            // Apply/Correct と同じ散乱モデルで採点する（区間リサンプルだと位相最適がずれる）。
            return WowFlutterWarp.CorrectPrefixCorrelateReal(
                samples,
                samples.Length,
                analysisStartSample,
                analysisSampleCount,
                Math.Max(1, _config.SampleRate),
                amount,
                wowPhase,
                flutterPhase,
                idealReals,
                refCorr.Mean,
                refCorr.Energy,
                sumScratch.AsSpan(0, analysisSampleCount),
                countScratch.AsSpan(0, analysisSampleCount));
        }
        finally
        {
            System.Buffers.ArrayPool<double>.Shared.Return(sumScratch);
            System.Buffers.ArrayPool<int>.Shared.Return(countScratch);
        }
    }

    /// <summary>
    /// 診断用にプリアンブル相関でワウ／フラッターパラメータを探索します。
    /// </summary>
    /// <param name="samples">入力 PCM。</param>
    /// <param name="useRightChannel">R 搬送波レイアウトを使うか。</param>
    /// <param name="analysisStartSample">解析開始サンプル。</param>
    /// <param name="analysisSampleCount">解析サンプル数。</param>
    /// <param name="onProgress">探索進捗 (done, total)。省略可。</param>
    /// <returns>基準スコアと最良パラメータ。失敗時 null。</returns>
    public (double Baseline, double BestScore, double Amount, double WowPhase, double FlutterPhase)?
        MatchWowParametersForDiagnostics(
            Complex[] samples,
            bool useRightChannel,
            int analysisStartSample,
            int analysisSampleCount,
            Action<int, int>? onProgress = null)
    {
        return MatchWowByPreambleCorrelationParams(
            samples, useRightChannel, analysisStartSample, analysisSampleCount, onProgress);
    }

    /// <summary>
    /// Match 結果を散乱 Correct（Apply の逆）モデルで局所精密化します。
    /// 採点は全長 scale のままプリアンブル近傍だけ散乱逆補正し、全波形 Correct より大幅に軽くします。
    /// </summary>
    /// <param name="samples">入力 PCM。</param>
    /// <param name="useRightChannel">R 搬送波レイアウトを使うか。</param>
    /// <param name="analysisStartSample">解析開始サンプル。</param>
    /// <param name="analysisSampleCount">解析サンプル数。</param>
    /// <param name="amount">初期ワウ量。</param>
    /// <param name="wowPhase">初期ワウ位相。</param>
    /// <param name="flutterPhase">初期フラッター位相。</param>
    /// <param name="onProgress">探索進捗 (done, total)。省略可。</param>
    /// <returns>精密化後の (amount, wowPhase, flutterPhase)。</returns>
    public (double Amount, double WowPhase, double FlutterPhase) RefineWowParametersForCorrectModel(
        Complex[] samples,
        bool useRightChannel,
        int analysisStartSample,
        int analysisSampleCount,
        double amount,
        double wowPhase,
        double flutterPhase,
        Action<int, int>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        analysisStartSample = Math.Clamp(analysisStartSample, 0, Math.Max(0, samples.Length - 1));
        analysisSampleCount = Math.Clamp(
            analysisSampleCount,
            SamplesPerOfdmSymbol,
            Math.Max(SamplesPerOfdmSymbol, samples.Length - analysisStartSample));

        var carrierBins = useRightChannel ? _rightAllCarrierBins : _leftAllCarrierBins;
        var ideal = GenerateUnmodulatedChannel(analysisSampleCount, carrierBins);
        var idealReals = new double[analysisSampleCount];
        SimdMath.CopyComplexReals(ideal.AsSpan(0, analysisSampleCount), idealReals);

        var sampleRate = Math.Max(1, _config.SampleRate);
        var refCorr = BuildCorrelationReferenceReals(idealReals);
        var referenceLength = samples.Length;
        var sumScratch = System.Buffers.ArrayPool<double>.Shared.Rent(analysisSampleCount);
        var countScratch = System.Buffers.ArrayPool<int>.Shared.Rent(analysisSampleCount);

        try
        {
            /// <summary>
            /// 指定ワウパラメータでプリアンブル相関スコアを計算します。
            /// </summary>
            /// <param name="a">ワウ量。</param>
            /// <param name="w">wow 位相（ラジアン）。</param>
            /// <param name="f">flutter 位相（ラジアン）。</param>
            /// <returns>正規化相関スコア。</returns>
            double Score(double a, double w, double f) =>
                WowFlutterWarp.CorrectPrefixCorrelateReal(
                    samples,
                    referenceLength,
                    analysisStartSample,
                    analysisSampleCount,
                    sampleRate,
                    a,
                    w,
                    f,
                    idealReals,
                    refCorr.Mean,
                    refCorr.Energy,
                    sumScratch.AsSpan(0, analysisSampleCount),
                    countScratch.AsSpan(0, analysisSampleCount));

            var bestAmount = amount;
            var bestWow = wowPhase;
            var bestFlutter = flutterPhase;
            var bestScore = Score(bestAmount, bestWow, bestFlutter);

            var amounts = new[] { 0.003, 0.004, 0.005, 0.006, 0.007, 0.008, 0.009, 0.01, 0.011, 0.012, 0.015 };
            // Match 残差が ~0.15 rad でも拾える幅
            const int coarseRadius = 24;
            const int fineRadius = 40;
            const int nanoRadius = 16;
            const int jointPasses = 2;
            const int jointRadius = 12;
            var jointCells = ((2 * jointRadius) + 1) * ((2 * jointRadius) + 1);
            var total = amounts.Length
                + (((2 * coarseRadius) + 1) * 2)
                + (jointPasses * (((2 * fineRadius) + 1) * 2))
                + (((2 * nanoRadius) + 1) * 2)
                + jointCells;
            var done = 0;

            /// <summary>
            /// 候補パラメータを採点し、最良スコアなら best を更新して進捗を進めます。
            /// </summary>
            /// <param name="a">ワウ量。</param>
            /// <param name="w">wow 位相（ラジアン）。</param>
            /// <param name="f">flutter 位相（ラジアン）。</param>
            void Consider(double a, double w, double f)
            {
                var score = Score(a, w, f);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestAmount = a;
                    bestWow = w;
                    bestFlutter = f;
                }

                done++;
                onProgress?.Invoke(done, total);
            }

            foreach (var trialAmount in amounts)
            {
                Consider(trialAmount, bestWow, bestFlutter);
            }

            var centerFlutter = bestFlutter;
            for (var dF = -coarseRadius; dF <= coarseRadius; dF++)
            {
                Consider(bestAmount, bestWow, centerFlutter + (dF * Math.PI / 200.0));
            }

            var centerWow = bestWow;
            for (var dW = -coarseRadius; dW <= coarseRadius; dW++)
            {
                Consider(bestAmount, centerWow + (dW * Math.PI / 200.0), bestFlutter);
            }

            // 軸ごとに中心固定。wow/flutter は結合最適なので交互に複数パスする。
            for (var pass = 0; pass < jointPasses; pass++)
            {
                centerFlutter = bestFlutter;
                for (var dF = -fineRadius; dF <= fineRadius; dF++)
                {
                    Consider(bestAmount, bestWow, centerFlutter + (dF * Math.PI / 4000.0));
                }

                centerWow = bestWow;
                for (var dW = -fineRadius; dW <= fineRadius; dW++)
                {
                    Consider(bestAmount, centerWow + (dW * Math.PI / 4000.0), bestFlutter);
                }
            }

            var centerFlutterNano = bestFlutter;
            for (var dF = -nanoRadius; dF <= nanoRadius; dF++)
            {
                Consider(bestAmount, bestWow, centerFlutterNano + (dF * Math.PI / 20000.0));
            }

            var centerWowNano = bestWow;
            for (var dW = -nanoRadius; dW <= nanoRadius; dW++)
            {
                Consider(bestAmount, centerWowNano + (dW * Math.PI / 20000.0), bestFlutter);
            }

            // 最終 2D 微調整（結合ズレの残りを潰す）
            var jointWow = bestWow;
            var jointFlutter = bestFlutter;
            for (var dW = -jointRadius; dW <= jointRadius; dW++)
            {
                for (var dF = -jointRadius; dF <= jointRadius; dF++)
                {
                    Consider(
                        bestAmount,
                        jointWow + (dW * Math.PI / 8000.0),
                        jointFlutter + (dF * Math.PI / 8000.0));
                }
            }

            return (bestAmount, WrapPhase(bestWow), WrapPhase(bestFlutter));
        }
        finally
        {
            System.Buffers.ArrayPool<double>.Shared.Return(sumScratch);
            System.Buffers.ArrayPool<int>.Shared.Return(countScratch);
        }
    }

    /// <summary>
    /// 診断用にヒント近傍でワウ／フラッターパラメータを局所精密化します。
    /// </summary>
    /// <param name="samples">入力 PCM。</param>
    /// <param name="useRightChannel">R 搬送波レイアウトを使うか。</param>
    /// <param name="analysisStartSample">解析開始サンプル。</param>
    /// <param name="analysisSampleCount">解析サンプル数。</param>
    /// <param name="hintAmount">探索中心のワウ量。</param>
    /// <param name="hintWowPhase">探索中心のワウ位相。</param>
    /// <param name="hintFlutterPhase">探索中心のフラッター位相。</param>
    /// <param name="phaseRangeRad">位相探索幅（ラジアン）。</param>
    /// <param name="amountRange">ワウ量の探索幅。</param>
    /// <returns>基準スコアと最良パラメータ。失敗時 null。</returns>
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

        /// <summary>
        /// ワウ補正後プリアンブルと理想波形の相関を、指定ストライドで評価します。
        /// </summary>
        /// <param name="amount">ワウ量。</param>
        /// <param name="wowPhase">wow 位相（ラジアン）。</param>
        /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
        /// <param name="corrStride">相関計算のサンプルストライド。</param>
        /// <returns>正規化相関スコア。</returns>
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
    /// プリアンブル相関でワウ／フラッターを推定し、逆写像リサンプリングで補正した PCM を返します。
    /// </summary>
    /// <param name="samples">入力 PCM。</param>
    /// <param name="useRightChannel">R 搬送波レイアウトを使うか。</param>
    /// <param name="analysisStartSample">解析開始サンプル。</param>
    /// <param name="analysisSampleCount">解析サンプル数。</param>
    /// <param name="passes">推定パス数。</param>
    /// <returns>ワウ補正後 PCM。</returns>
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
    /// 既知のワウ／フラッター量・位相でセグメントを逆写像リサンプリングし output へ書きます。
    /// </summary>
    /// <param name="source">入力 PCM。</param>
    /// <param name="start">セグメント開始。</param>
    /// <param name="length">セグメント長。</param>
    /// <param name="destination">出力バッファ。</param>
    /// <param name="amount">ワウ量。</param>
    /// <param name="wowPhase">ワウ位相。</param>
    /// <param name="flutterPhase">フラッター位相。</param>
    /// <param name="streamBaseSample">ストリーム基準サンプル位置。</param>
    public void CorrectWowFlutterSegmentTo(
        Complex[] source,
        int start,
        int length,
        Span<Complex> destination,
        double amount,
        double wowPhase,
        double flutterPhase,
        long streamBaseSample = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (length < SamplesPerOfdmSymbol || amount == 0.0)
        {
            if (length > 0)
            {
                source.AsSpan(start, length).CopyTo(destination);
            }

            return;
        }

        if (start < 0 || length <= 0 || start + length > source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (destination.Length < length)
        {
            throw new ArgumentException("Destination is shorter than segment length.", nameof(destination));
        }

        ResampleSegmentWithInverseSpeed(
            source,
            start,
            length,
            Math.Max(1, _config.SampleRate),
            amount,
            wowPhase,
            flutterPhase,
            destination.Slice(0, length),
            stride: 1,
            streamBaseSample: streamBaseSample);
    }

    /// <summary>
    /// 全波形上の絶対時刻を保ったまま、指定区間だけをワウ逆補正します。
    /// 適応ワウのテール再ワープ用（先頭切り出し補正は位相がずれるため使わない）。
    /// </summary>
    /// <param name="samples">全PCM（補正対象区間を含む）。</param>
    /// <param name="start">補正開始サンプル（絶対位置）。</param>
    /// <param name="length">補正サンプル長。</param>
    /// <param name="amount">ワウ量。</param>
    /// <param name="wowPhase">wow 初期位相。</param>
    /// <param name="flutterPhase">flutter 初期位相。</param>
    /// <param name="streamBaseSample">samples[0] のストリーム絶対位置。</param>
    public void CorrectWowFlutterSegmentInPlace(
        Complex[] samples,
        int start,
        int length,
        double amount,
        double wowPhase,
        double flutterPhase,
        long streamBaseSample = 0)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (length < SamplesPerOfdmSymbol || amount == 0.0)
        {
            return;
        }

        if (start < 0 || length <= 0 || start + length > samples.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        var rented = System.Buffers.ArrayPool<Complex>.Shared.Rent(length);
        try
        {
            CorrectWowFlutterSegmentTo(
                samples,
                start,
                length,
                rented.AsSpan(0, length),
                amount,
                wowPhase,
                flutterPhase,
                streamBaseSample);
            Array.Copy(rented, 0, samples, start, length);
        }
        finally
        {
            System.Buffers.ArrayPool<Complex>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>
    /// 無変調プリアンブルと理想波形の相関が最大になるワウ／フラッターパラメータを探索します。
    /// </summary>
    /// <param name="samples">入力 PCM。</param>
    /// <param name="useRightChannel">R 搬送波レイアウトを使うか。</param>
    /// <param name="analysisStartSample">解析開始サンプル。</param>
    /// <param name="analysisSampleCount">解析サンプル数。</param>
    /// <returns>最良パラメータ [amount, wowPhase, flutterPhase]。失敗時 null。</returns>
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
    /// プリアンブル相関でワウ／フラッターを探索し、基準スコア付きで返します。
    /// </summary>
    /// <param name="samples">入力 PCM。</param>
    /// <param name="useRightChannel">R 搬送波レイアウトを使うか。</param>
    /// <param name="analysisStartSample">解析開始サンプル。</param>
    /// <param name="analysisSampleCount">解析サンプル数。</param>
    /// <param name="onProgress">探索進捗 (done, total)。省略可。</param>
    /// <returns>基準スコアと最良パラメータ。失敗時 null。</returns>
    private (double Baseline, double BestScore, double Amount, double WowPhase, double FlutterPhase)?
        MatchWowByPreambleCorrelationParams(
            Complex[] samples,
            bool useRightChannel,
            int analysisStartSample,
            int analysisSampleCount,
            Action<int, int>? onProgress = null)
    {
        if (analysisSampleCount < SamplesPerOfdmSymbol * 16)
        {
            return null;
        }

        analysisStartSample = Math.Clamp(analysisStartSample, 0, Math.Max(0, samples.Length - SamplesPerOfdmSymbol));
        analysisSampleCount = Math.Clamp(analysisSampleCount, SamplesPerOfdmSymbol, samples.Length - analysisStartSample);

        var carrierBins = useRightChannel ? _rightAllCarrierBins : _leftAllCarrierBins;
        var ideal = GenerateUnmodulatedChannel(analysisSampleCount, carrierBins);
        var idealReals = new double[analysisSampleCount];
        SimdMath.CopyComplexReals(ideal.AsSpan(0, analysisSampleCount), idealReals);

        var sampleRate = Math.Max(1, _config.SampleRate);
        var refCorr = BuildCorrelationReferenceReals(idealReals);
        var bestScore = 0.0;
        var bestAmount = 0.01;
        var bestWowPhase = 0.0;
        var bestFlutterPhase = 0.0;
        // 全長 scale の Correct と同じモデル。プリアンブル近傍だけ散乱するので高速。
        var referenceLength = samples.Length;

        var sumScratch = System.Buffers.ArrayPool<double>.Shared.Rent(analysisSampleCount);
        var countScratch = System.Buffers.ArrayPool<int>.Shared.Rent(analysisSampleCount);
        try
        {
            for (var i = 0; i < analysisSampleCount; i++)
            {
                sumScratch[i] = samples[analysisStartSample + i].Real;
            }

            var baseline = WowFlutterWarp.CorrelateDenseReals(
                sumScratch.AsSpan(0, analysisSampleCount),
                idealReals,
                refCorr.Mean,
                refCorr.Energy);
            bestScore = baseline;

            /// <summary>
            /// ワウパラメータを評価し、最良なら best を更新してスコアを返します。
            /// </summary>
            /// <param name="amount">ワウ量。</param>
            /// <param name="wowPhase">wow 位相（ラジアン）。</param>
            /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
            /// <returns>正規化相関スコア。</returns>
            double Evaluate(double amount, double wowPhase, double flutterPhase)
            {
                var score = WowFlutterWarp.CorrectPrefixCorrelateReal(
                    samples,
                    referenceLength,
                    analysisStartSample,
                    analysisSampleCount,
                    sampleRate,
                    amount,
                    wowPhase,
                    flutterPhase,
                    idealReals,
                    refCorr.Mean,
                    refCorr.Energy,
                    sumScratch.AsSpan(0, analysisSampleCount),
                    countScratch.AsSpan(0, analysisSampleCount));
                if (score > bestScore)
                {
                    bestScore = score;
                    bestAmount = amount;
                    bestWowPhase = wowPhase;
                    bestFlutterPhase = flutterPhase;
                }

                return score;
            }

            const double earlyExitScore = 0.97;
            const int progressTotal = 1200;
            var progressDone = 0;
            /// <summary>
            /// ワウ探索の進捗カウンタを進め、間引きして onProgress へ通知します。
            /// </summary>
            void ReportProgress()
            {
                progressDone++;
                if ((progressDone & 7) == 0 || progressDone >= progressTotal)
                {
                    onProgress?.Invoke(Math.Min(progressDone, progressTotal), progressTotal);
                }
            }

            var coarseHits = new List<(double Score, double Wow, double Flutter)>(64);

            // 粗探索: amount=0.01 は π/9 で十分広い。amount=0.005 は相関ピークが鋭く
            // π/9 では真値近傍でも baseline を下回るため、失敗時は π/18 で再掃引する。
            /// <summary>
            /// 固定ワウ量で位相平面を粗格子掃引し、有望ヒットを収集します。
            /// </summary>
            /// <param name="amount">掃引するワウ量。</param>
            /// <param name="halfTurnDivisions">半回転あたりの格子分割数（π / 分割）。</param>
            void CoarsePhaseSweep(double amount, int halfTurnDivisions)
            {
                var step = Math.PI / halfTurnDivisions;
                var count = halfTurnDivisions * 2;
                for (var wi = 0; wi < count; wi++)
                {
                    var wowPhase = wi * step;
                    for (var fi = 0; fi < count; fi++)
                    {
                        var flutterPhase = fi * step;
                        // 粗格子も Correct 真値付近を落とさないよう stride=1 で採点する。
                        var score = Evaluate(amount, wowPhase, flutterPhase);
                        ReportProgress();
                        if (score > baseline + 0.02)
                        {
                            coarseHits.Add((score, wowPhase, flutterPhase));
                        }

                        if (bestScore >= earlyExitScore)
                        {
                            return;
                        }
                    }
                }
            }

            CoarsePhaseSweep(0.01, halfTurnDivisions: 9);
            // amount=0.01 の弱い偽ピークに捕まると 0.005 真値を逃すため、
            // 高スコア未達なら鋭峰向けの密格子を必ず追加する。
            if (bestScore < 0.90)
            {
                CoarsePhaseSweep(0.005, halfTurnDivisions: 18);
            }

            if (bestScore < 0.90)
            {
                CoarsePhaseSweep(0.003, halfTurnDivisions: 18);
            }

            if (coarseHits.Count == 0)
            {
                onProgress?.Invoke(progressTotal, progressTotal);
                return null;
            }

            coarseHits.Sort((a, b) => b.Score.CompareTo(a.Score));
            var seeds = new List<(double Wow, double Flutter)>(3);
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

            // 以降の位相精密化は粗探索で更新された bestAmount を使う（0.01 固定だと 0.005 真値を壊す）。
            /// <summary>
            /// 位相精密化に使うワウ量を返します（未確定時は 0.01）。
            /// </summary>
            /// <returns>位相掃引に用いるワウ量。</returns>
            double PhaseAmount() => bestAmount > 1e-12 ? bestAmount : 0.01;

            // π/9 粗格子の隙間（鋭いピーク）を埋める。シード周辺を密に掃引する。
            foreach (var seed in seeds)
            {
                for (var dW = -6; dW <= 6; dW++)
                {
                    for (var dF = -6; dF <= 6; dF++)
                    {
                        if (dW == 0 && dF == 0)
                        {
                            continue;
                        }

                        Evaluate(
                            PhaseAmount(),
                            seed.Wow + (dW * Math.PI / 54.0),
                            seed.Flutter + (dF * Math.PI / 54.0));
                        ReportProgress();
                    }
                }

                if (bestScore >= earlyExitScore)
                {
                    break;
                }
            }

            foreach (var seed in seeds)
            {
                var wow = seed.Wow;
                var flutter = seed.Flutter;
                // 密探索後の最良点に近いシードから交互探索を始める。
                if (Math.Abs(WrapPhase(bestWowPhase - seed.Wow)) < 0.35
                    && Math.Abs(WrapPhase(bestFlutterPhase - seed.Flutter)) < 0.35)
                {
                    wow = bestWowPhase;
                    flutter = bestFlutterPhase;
                }

                for (var pass = 0; pass < 2; pass++)
                {
                    // CorrectPrefix 採点は軽いので stride=1。π/45 だと鋭い真ピークを外す。
                    var bestLocal = double.NegativeInfinity;
                    var bestFlutter = flutter;
                    for (var fi = 0; fi < 180; fi++)
                    {
                        var trial = fi * Math.PI / 90.0;
                        var score = Evaluate(PhaseAmount(), wow, trial);
                        ReportProgress();
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
                        var score = Evaluate(PhaseAmount(), trial, flutter);
                        ReportProgress();
                        if (score > bestLocal)
                        {
                            bestLocal = score;
                            bestWow = trial;
                        }
                    }

                    wow = bestWow;
                }

                Evaluate(PhaseAmount(), wow, flutter);
                ReportProgress();
                for (var dW = -12; dW <= 12; dW++)
                {
                    for (var dF = -12; dF <= 12; dF++)
                    {
                        Evaluate(
                            PhaseAmount(),
                            wow + (dW * Math.PI / 900.0),
                            flutter + (dF * Math.PI / 900.0));
                        ReportProgress();
                    }
                }

                if (bestScore >= earlyExitScore)
                {
                    break;
                }
            }

            if (bestScore <= baseline)
            {
                onProgress?.Invoke(progressTotal, progressTotal);
                return null;
            }

            // 0.005（test7/8/9）〜 0.012（従来中心）をカバー。粗探索が拾った量の近傍も再掃引。
            foreach (var amount in new[]
                     {
                         0.003, 0.004, 0.005, 0.006, 0.007, 0.008, 0.009, 0.01, 0.011, 0.012, 0.015
                     })
            {
                Evaluate(amount, bestWowPhase, bestFlutterPhase);
                ReportProgress();
            }

            var fineWow = bestWowPhase;
            var fineFlutter = bestFlutterPhase;
            for (var dF = -20; dF <= 20; dF++)
            {
                Evaluate(bestAmount, fineWow, fineFlutter + (dF * Math.PI / 4000.0));
                ReportProgress();
            }

            fineFlutter = bestFlutterPhase;
            for (var dW = -20; dW <= 20; dW++)
            {
                Evaluate(bestAmount, fineWow + (dW * Math.PI / 4000.0), fineFlutter);
                ReportProgress();
            }

            onProgress?.Invoke(progressTotal, progressTotal);

            // 呼び出し側は >=0.50。旧 0.85 だとノイズ付きで Match 失敗→超重い FH 推定へ落ちる。
            if (bestScore < baseline + 0.02 || bestScore < 0.50)
            {
                return null;
            }

            return (baseline, bestScore, bestAmount, bestWowPhase, bestFlutterPhase);
        }
        finally
        {
            System.Buffers.ArrayPool<double>.Shared.Return(sumScratch);
            System.Buffers.ArrayPool<int>.Shared.Return(countScratch);
        }
    }

    /// <summary>
    /// 実数参照波形の平均・エネルギー・長さを計算します（相関の正規化用）。
    /// </summary>
    /// <param name="reference">参照実数系列。</param>
    /// <returns>平均、エネルギー（平均除去後二乗和）、要素数。</returns>
    private static (double Mean, double Energy, int Count) BuildCorrelationReferenceReals(ReadOnlySpan<double> reference)
    {
        if (reference.Length <= 1)
        {
            return (0.0, 0.0, reference.Length);
        }

        if (Avx.IsSupported && reference.Length >= 8)
        {
            return BuildCorrelationReferenceRealsAvx(reference);
        }

        if (AdvSimd.Arm64.IsSupported && reference.Length >= 4)
        {
            return BuildCorrelationReferenceRealsAdvSimd(reference);
        }

        var sum = 0.0;
        for (var i = 0; i < reference.Length; i++)
        {
            sum += reference[i];
        }

        var mean = sum / reference.Length;
        var energy = 0.0;
        for (var i = 0; i < reference.Length; i++)
        {
            var centered = reference[i] - mean;
            energy += centered * centered;
        }

        return (mean, energy, reference.Length);
    }

    /// <summary>
    /// 実数参照波形の平均・エネルギーを AVX で計算します。
    /// </summary>
    /// <param name="reference">参照実数系列。</param>
    /// <returns>平均、エネルギー（平均除去後二乗和）、要素数。</returns>
    private static (double Mean, double Energy, int Count) BuildCorrelationReferenceRealsAvx(ReadOnlySpan<double> reference)
    {
        ref var first = ref MemoryMarshal.GetReference(reference);
        var sumVec = Vector256<double>.Zero;
        var i = 0;
        for (; i + 4 <= reference.Length; i += 4)
        {
            var vec = Unsafe.ReadUnaligned<Vector256<double>>(
                ref Unsafe.As<double, byte>(ref Unsafe.Add(ref first, i)));
            sumVec = Avx.Add(sumVec, vec);
        }

        var sum = sumVec.GetElement(0) + sumVec.GetElement(1) + sumVec.GetElement(2) + sumVec.GetElement(3);
        for (; i < reference.Length; i++)
        {
            sum += reference[i];
        }

        var mean = sum / reference.Length;
        var meanVec = Vector256.Create(mean);
        var energyVec = Vector256<double>.Zero;
        i = 0;
        for (; i + 4 <= reference.Length; i += 4)
        {
            var vec = Unsafe.ReadUnaligned<Vector256<double>>(
                ref Unsafe.As<double, byte>(ref Unsafe.Add(ref first, i)));
            var centered = Avx.Subtract(vec, meanVec);
            energyVec = Avx.Add(energyVec, Avx.Multiply(centered, centered));
        }

        var energy = energyVec.GetElement(0) + energyVec.GetElement(1)
            + energyVec.GetElement(2) + energyVec.GetElement(3);
        for (; i < reference.Length; i++)
        {
            var centered = reference[i] - mean;
            energy += centered * centered;
        }

        return (mean, energy, reference.Length);
    }

    /// <summary>
    /// 実数参照波形の平均・エネルギーを AdvSimd で計算します。
    /// </summary>
    /// <param name="reference">参照実数系列。</param>
    /// <returns>平均、エネルギー（平均除去後二乗和）、要素数。</returns>
    private static (double Mean, double Energy, int Count) BuildCorrelationReferenceRealsAdvSimd(ReadOnlySpan<double> reference)
    {
        ref var first = ref MemoryMarshal.GetReference(reference);
        var sumVec = Vector128<double>.Zero;
        var i = 0;
        for (; i + 2 <= reference.Length; i += 2)
        {
            var vec = Unsafe.ReadUnaligned<Vector128<double>>(
                ref Unsafe.As<double, byte>(ref Unsafe.Add(ref first, i)));
            sumVec = AdvSimd.Arm64.Add(sumVec, vec);
        }

        var sum = sumVec.GetElement(0) + sumVec.GetElement(1);
        for (; i < reference.Length; i++)
        {
            sum += reference[i];
        }

        var mean = sum / reference.Length;
        var meanVec = Vector128.Create(mean);
        var energyVec = Vector128<double>.Zero;
        i = 0;
        for (; i + 2 <= reference.Length; i += 2)
        {
            var vec = Unsafe.ReadUnaligned<Vector128<double>>(
                ref Unsafe.As<double, byte>(ref Unsafe.Add(ref first, i)));
            var centered = AdvSimd.Arm64.Subtract(vec, meanVec);
            energyVec = AdvSimd.Arm64.Add(energyVec, AdvSimd.Arm64.Multiply(centered, centered));
        }

        var energy = energyVec.GetElement(0) + energyVec.GetElement(1);
        for (; i < reference.Length; i++)
        {
            var centered = reference[i] - mean;
            energy += centered * centered;
        }

        return (mean, energy, reference.Length);
    }

    /// <summary>
    /// 位相を (−π, π] へ折り返します。
    /// </summary>
    /// <param name="phase">位相（ラジアン）。</param>
    /// <returns>折り返し後の位相。</returns>
    private static double WrapPhase(double phase)
    {
        phase %= TwoPi;
        if (phase > Math.PI)
        {
            phase -= TwoPi;
        }
        else if (phase < -Math.PI)
        {
            phase += TwoPi;
        }

        return phase;
    }

    /// <summary>
    /// 第 1 象限の sin LUT を構築します（高速 sin/cos 用）。
    /// </summary>
    /// <returns>四分円 sin テーブル。</returns>
    private static double[] BuildSinQuarterLut()
    {
        var table = new double[TrigQuarterTableSize + 1];
        for (var i = 0; i <= TrigQuarterTableSize; i++)
        {
            table[i] = Math.Sin((i / (double)TrigQuarterTableSize) * HalfPi);
        }

        return table;
    }

    /// <summary>
    /// 四分円 LUT から sin を求めます。
    /// </summary>
    /// <param name="angle">角度（ラジアン）。</param>
    /// <returns>sin(angle)。</returns>
    private static double SinFromQuarterLut(double angle)
    {
        var x = angle % TwoPi;
        if (x < 0.0)
        {
            x += TwoPi;
        }

        var quadrant = (int)(x / HalfPi);
        if (quadrant >= 4)
        {
            quadrant = 3;
        }

        var offset = x - (quadrant * HalfPi);
        if ((quadrant & 1) != 0)
        {
            offset = HalfPi - offset;
        }

        var index = offset * TrigQuarterIndexScale;
        var i0 = (int)index;
        if (i0 >= TrigQuarterTableSize)
        {
            i0 = TrigQuarterTableSize - 1;
        }

        var frac = index - i0;
        var v0 = SinQuarterLut[i0];
        var v1 = SinQuarterLut[i0 + 1];
        var baseValue = v0 + ((v1 - v0) * frac);
        return quadrant >= 2 ? -baseValue : baseValue;
    }

    /// <summary>
    /// 四分円 LUT から sin と cos を同時に求めます。
    /// </summary>
    /// <param name="angle">角度（ラジアン）。</param>
    /// <param name="sin">sin 出力。</param>
    /// <param name="cos">cos 出力。</param>
    private static void SinCosFromQuarterLut(double angle, out double sin, out double cos)
    {
        sin = SinFromQuarterLut(angle);
        cos = SinFromQuarterLut(angle + HalfPi);
    }

    /// <summary>
    /// 複素参照の実部から平均・エネルギーを計算します（ストライド対応の入口）。
    /// </summary>
    /// <param name="reference">参照複素系列。</param>
    /// <param name="stride">サンプル間隔。</param>
    /// <returns>平均、エネルギー（平均除去後二乗和）、有効要素数。</returns>
    private static (double Mean, double Energy, int Count) BuildCorrelationReference(Complex[] reference, int stride)
    {
        return BuildCorrelationReference(reference.AsSpan(), stride);
    }

    /// <summary>
    /// 複素参照の実部から平均・エネルギーを計算します（ストライド対応）。
    /// </summary>
    /// <param name="reference">参照複素系列。</param>
    /// <param name="stride">サンプル間隔。</param>
    /// <returns>平均、エネルギー（平均除去後二乗和）、有効要素数。</returns>
    private static (double Mean, double Energy, int Count) BuildCorrelationReference(ReadOnlySpan<Complex> reference, int stride)
    {
        stride = Math.Max(1, stride);

        if (stride == 1)
        {
            if (Avx.IsSupported && reference.Length >= 4)
            {
                return BuildCorrelationReferenceDenseAvx(reference);
            }

            if (AdvSimd.Arm64.IsSupported && reference.Length >= 2)
            {
                return BuildCorrelationReferenceDenseAdvSimd(reference);
            }
        }

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
    /// 複素参照の実部から平均・エネルギーを AVX で計算します（密ストライド）。
    /// </summary>
    /// <param name="reference">参照複素系列。</param>
    /// <returns>平均、エネルギー（平均除去後二乗和）、要素数。</returns>
    private static (double Mean, double Energy, int Count) BuildCorrelationReferenceDenseAvx(ReadOnlySpan<Complex> reference)
    {
        var count = reference.Length;
        if (count <= 1)
        {
            return (0.0, 0.0, count);
        }

        var doubles = MemoryMarshal.Cast<Complex, double>(reference);
        ref var baseRef = ref MemoryMarshal.GetReference(doubles);
        var sumVec = Vector256<double>.Zero;
        var i = 0;
        for (; i + 2 <= count; i += 2)
        {
            var vec = Unsafe.ReadUnaligned<Vector256<double>>(
                ref Unsafe.As<double, byte>(ref Unsafe.Add(ref baseRef, i * 2)));
            sumVec = Avx.Add(sumVec, Avx.Multiply(vec, RealLaneMask));
        }

        var sum = sumVec.GetElement(0) + sumVec.GetElement(1) + sumVec.GetElement(2) + sumVec.GetElement(3);
        for (; i < count; i++)
        {
            sum += reference[i].Real;
        }

        var mean = sum / count;
        var meanRealLaneVec = Vector256.Create(mean, 0.0, mean, 0.0);
        var energyVec = Vector256<double>.Zero;
        i = 0;
        for (; i + 2 <= count; i += 2)
        {
            var vec = Unsafe.ReadUnaligned<Vector256<double>>(
                ref Unsafe.As<double, byte>(ref Unsafe.Add(ref baseRef, i * 2)));
            var realOnly = Avx.Multiply(vec, RealLaneMask);
            var centered = Avx.Subtract(realOnly, meanRealLaneVec);
            energyVec = Avx.Add(energyVec, Avx.Multiply(centered, centered));
        }

        var energy = energyVec.GetElement(0) + energyVec.GetElement(1)
            + energyVec.GetElement(2) + energyVec.GetElement(3);
        for (; i < count; i++)
        {
            var centered = reference[i].Real - mean;
            energy += centered * centered;
        }

        return (mean, energy, count);
    }

    /// <summary>
    /// 複素参照の実部から平均・エネルギーを AdvSimd で計算します（密ストライド）。
    /// </summary>
    /// <param name="reference">参照複素系列。</param>
    /// <returns>平均、エネルギー（平均除去後二乗和）、要素数。</returns>
    private static (double Mean, double Energy, int Count) BuildCorrelationReferenceDenseAdvSimd(ReadOnlySpan<Complex> reference)
    {
        var count = reference.Length;
        if (count <= 1)
        {
            return (0.0, 0.0, count);
        }

        var doubles = MemoryMarshal.Cast<Complex, double>(reference);
        var sumVec = Vector128<double>.Zero;
        var i = 0;
        for (; i + 2 <= count; i += 2)
        {
            var baseIdx = i * 2;
            var realPair = Vector128.Create(doubles[baseIdx], doubles[baseIdx + 2]);
            sumVec = AdvSimd.Arm64.Add(sumVec, realPair);
        }

        var sum = sumVec.GetElement(0) + sumVec.GetElement(1);
        for (; i < count; i++)
        {
            sum += reference[i].Real;
        }

        var mean = sum / count;
        var meanVec = Vector128.Create(mean);
        var energyVec = Vector128<double>.Zero;
        i = 0;
        for (; i + 2 <= count; i += 2)
        {
            var baseIdx = i * 2;
            var realPair = Vector128.Create(doubles[baseIdx], doubles[baseIdx + 2]);
            var centered = AdvSimd.Arm64.Subtract(realPair, meanVec);
            energyVec = AdvSimd.Arm64.Add(energyVec, AdvSimd.Arm64.Multiply(centered, centered));
        }

        var energy = energyVec.GetElement(0) + energyVec.GetElement(1);
        for (; i < count; i++)
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
        var requiredLength = ((count - 1) * stride) + 1;
        if (a.Length < requiredLength || b.Length < requiredLength)
        {
            return double.NegativeInfinity;
        }

        if (stride == 1 && a.Length >= count && b.Length >= count && count >= 8)
        {
            if (Avx.IsSupported)
            {
                return CorrelateRealDenseAvx(a, b, meanB, energyB, count);
            }

            if (AdvSimd.Arm64.IsSupported)
            {
                return CorrelateRealDenseAdvSimd(a, b, meanB, energyB, count);
            }
        }

        if (stride > 1)
        {
            if (Avx.IsSupported && count >= 8)
            {
                return CorrelateRealStridedAvx(a, b, stride, meanB, energyB, count);
            }

            if (AdvSimd.Arm64.IsSupported && count >= 4)
            {
                return CorrelateRealStridedAdvSimd(a, b, stride, meanB, energyB, count);
            }
        }

        var sumA = 0.0;
        for (var i = 0; i < count; i++)
        {
            sumA += a[i * stride].Real;
        }

        var meanA = sumA / count;
        var num = 0.0;
        var energyA = 0.0;
        for (var i = 0; i < count; i++)
        {
            var idx = i * stride;
            var xa = a[idx].Real - meanA;
            var xb = b[idx].Real - meanB;
            num += xa * xb;
            energyA += xa * xa;
        }

        return num / Math.Sqrt((energyA * energyB) + 1e-18);
    }

    /// <summary>
    /// 実数ベクトルの正規化相関を AVX で計算します（密）。
    /// </summary>
    /// <param name="a">入力 A。</param>
    /// <param name="b">入力 B。</param>
    /// <param name="meanB">B の平均。</param>
    /// <param name="energyB">B のエネルギー。</param>
    /// <param name="count">相関に使うサンプル数。</param>
    /// <returns>正規化相関値。</returns>
    private static double CorrelateRealDenseAvx(
        ReadOnlySpan<Complex> a,
        ReadOnlySpan<Complex> b,
        double meanB,
        double energyB,
        int count)
    {
        var ad = MemoryMarshal.Cast<Complex, double>(a);
        var bd = MemoryMarshal.Cast<Complex, double>(b);
        ref var aRef = ref MemoryMarshal.GetReference(ad);
        ref var bRef = ref MemoryMarshal.GetReference(bd);
        var sumVec = Vector256<double>.Zero;
        var i = 0;
        // Complex = [R,I,R,I,...]。2複素×2ロード後、VSHUFPD(0) で Real だけ取り出す。
        for (; i + 4 <= count; i += 4)
        {
            var baseIdx = i * 2;
            var a0 = Unsafe.ReadUnaligned<Vector256<double>>(
                ref Unsafe.As<double, byte>(ref Unsafe.Add(ref aRef, baseIdx)));
            var a1 = Unsafe.ReadUnaligned<Vector256<double>>(
                ref Unsafe.As<double, byte>(ref Unsafe.Add(ref aRef, baseIdx + 4)));
            // a0=[R0,I0,R1,I1], a1=[R2,I2,R3,I3] → [R0,R1,R2,R3]
            sumVec = Avx.Add(sumVec, Avx.Shuffle(a0, a1, 0b0000));
        }

        var sumA = sumVec.GetElement(0) + sumVec.GetElement(1) + sumVec.GetElement(2) + sumVec.GetElement(3);
        for (; i < count; i++)
        {
            sumA += ad[i * 2];
        }

        var meanA = sumA / count;
        var meanAVec = Vector256.Create(meanA);
        var meanBVec = Vector256.Create(meanB);
        var numVec = Vector256<double>.Zero;
        var energyAVec = Vector256<double>.Zero;
        i = 0;
        for (; i + 4 <= count; i += 4)
        {
            var baseIdx = i * 2;
            var a0 = Unsafe.ReadUnaligned<Vector256<double>>(
                ref Unsafe.As<double, byte>(ref Unsafe.Add(ref aRef, baseIdx)));
            var a1 = Unsafe.ReadUnaligned<Vector256<double>>(
                ref Unsafe.As<double, byte>(ref Unsafe.Add(ref aRef, baseIdx + 4)));
            var b0 = Unsafe.ReadUnaligned<Vector256<double>>(
                ref Unsafe.As<double, byte>(ref Unsafe.Add(ref bRef, baseIdx)));
            var b1 = Unsafe.ReadUnaligned<Vector256<double>>(
                ref Unsafe.As<double, byte>(ref Unsafe.Add(ref bRef, baseIdx + 4)));
            var ra = Avx.Shuffle(a0, a1, 0b0000);
            var rb = Avx.Shuffle(b0, b1, 0b0000);
            var xa = Avx.Subtract(ra, meanAVec);
            var xb = Avx.Subtract(rb, meanBVec);
            numVec = Avx.Add(numVec, Avx.Multiply(xa, xb));
            energyAVec = Avx.Add(energyAVec, Avx.Multiply(xa, xa));
        }

        var num = numVec.GetElement(0) + numVec.GetElement(1) + numVec.GetElement(2) + numVec.GetElement(3);
        var energyA = energyAVec.GetElement(0) + energyAVec.GetElement(1)
            + energyAVec.GetElement(2) + energyAVec.GetElement(3);
        for (; i < count; i++)
        {
            var xa = ad[i * 2] - meanA;
            var xb = bd[i * 2] - meanB;
            num += xa * xb;
            energyA += xa * xa;
        }

        return num / Math.Sqrt((energyA * energyB) + 1e-18);
    }

    /// <summary>
    /// 実数ベクトルの正規化相関を AdvSimd で計算します（密）。
    /// </summary>
    /// <param name="a">入力 A。</param>
    /// <param name="b">入力 B。</param>
    /// <param name="meanB">B の平均。</param>
    /// <param name="energyB">B のエネルギー。</param>
    /// <param name="count">相関に使うサンプル数。</param>
    /// <returns>正規化相関値。</returns>
    private static double CorrelateRealDenseAdvSimd(
        ReadOnlySpan<Complex> a,
        ReadOnlySpan<Complex> b,
        double meanB,
        double energyB,
        int count)
    {
        var ad = MemoryMarshal.Cast<Complex, double>(a);
        var bd = MemoryMarshal.Cast<Complex, double>(b);
        var sumVec = Vector128<double>.Zero;
        var i = 0;
        for (; i + 2 <= count; i += 2)
        {
            var baseIdx = i * 2;
            var ra = Vector128.Create(ad[baseIdx], ad[baseIdx + 2]);
            sumVec = AdvSimd.Arm64.Add(sumVec, ra);
        }

        var sumA = sumVec.GetElement(0) + sumVec.GetElement(1);
        for (; i < count; i++)
        {
            sumA += ad[i * 2];
        }

        var meanA = sumA / count;
        var meanAVec = Vector128.Create(meanA);
        var meanBVec = Vector128.Create(meanB);
        var numVec = Vector128<double>.Zero;
        var energyAVec = Vector128<double>.Zero;
        i = 0;
        for (; i + 2 <= count; i += 2)
        {
            var baseIdx = i * 2;
            var ra = Vector128.Create(ad[baseIdx], ad[baseIdx + 2]);
            var rb = Vector128.Create(bd[baseIdx], bd[baseIdx + 2]);
            var xa = AdvSimd.Arm64.Subtract(ra, meanAVec);
            var xb = AdvSimd.Arm64.Subtract(rb, meanBVec);
            numVec = AdvSimd.Arm64.Add(numVec, AdvSimd.Arm64.Multiply(xa, xb));
            energyAVec = AdvSimd.Arm64.Add(energyAVec, AdvSimd.Arm64.Multiply(xa, xa));
        }

        var num = numVec.GetElement(0) + numVec.GetElement(1);
        var energyA = energyAVec.GetElement(0) + energyAVec.GetElement(1);
        for (; i < count; i++)
        {
            var xa = ad[i * 2] - meanA;
            var xb = bd[i * 2] - meanB;
            num += xa * xb;
            energyA += xa * xa;
        }

        return num / Math.Sqrt((energyA * energyB) + 1e-18);
    }

    /// <summary>
    /// ストライド付き実数ベクトルの正規化相関を AVX で計算します。
    /// </summary>
    /// <param name="a">入力 A。</param>
    /// <param name="b">入力 B。</param>
    /// <param name="stride">サンプル間隔。</param>
    /// <param name="meanB">B の平均。</param>
    /// <param name="energyB">B のエネルギー。</param>
    /// <param name="count">相関に使うサンプル数。</param>
    /// <returns>正規化相関値。</returns>
    private static double CorrelateRealStridedAvx(
        ReadOnlySpan<Complex> a,
        ReadOnlySpan<Complex> b,
        int stride,
        double meanB,
        double energyB,
        int count)
    {
        var ad = MemoryMarshal.Cast<Complex, double>(a);
        var bd = MemoryMarshal.Cast<Complex, double>(b);
        var sumVec = Vector256<double>.Zero;
        var i = 0;

        for (; i + 4 <= count; i += 4)
        {
            var s0 = i * stride;
            var ra = Vector256.Create(
                ad[s0 * 2],
                ad[(s0 + stride) * 2],
                ad[(s0 + (2 * stride)) * 2],
                ad[(s0 + (3 * stride)) * 2]);
            sumVec = Avx.Add(sumVec, ra);
        }

        var sumA = sumVec.GetElement(0) + sumVec.GetElement(1) + sumVec.GetElement(2) + sumVec.GetElement(3);
        for (; i < count; i++)
        {
            sumA += ad[(i * stride) * 2];
        }

        var meanA = sumA / count;
        var meanAVec = Vector256.Create(meanA);
        var meanBVec = Vector256.Create(meanB);
        var numVec = Vector256<double>.Zero;
        var energyAVec = Vector256<double>.Zero;
        i = 0;

        for (; i + 4 <= count; i += 4)
        {
            var s0 = i * stride;
            var ra = Vector256.Create(
                ad[s0 * 2],
                ad[(s0 + stride) * 2],
                ad[(s0 + (2 * stride)) * 2],
                ad[(s0 + (3 * stride)) * 2]);
            var rb = Vector256.Create(
                bd[s0 * 2],
                bd[(s0 + stride) * 2],
                bd[(s0 + (2 * stride)) * 2],
                bd[(s0 + (3 * stride)) * 2]);
            var xa = Avx.Subtract(ra, meanAVec);
            var xb = Avx.Subtract(rb, meanBVec);
            numVec = Avx.Add(numVec, Avx.Multiply(xa, xb));
            energyAVec = Avx.Add(energyAVec, Avx.Multiply(xa, xa));
        }

        var num = numVec.GetElement(0) + numVec.GetElement(1) + numVec.GetElement(2) + numVec.GetElement(3);
        var energyA = energyAVec.GetElement(0) + energyAVec.GetElement(1)
            + energyAVec.GetElement(2) + energyAVec.GetElement(3);
        for (; i < count; i++)
        {
            var realIndex = (i * stride) * 2;
            var xa = ad[realIndex] - meanA;
            var xb = bd[realIndex] - meanB;
            num += xa * xb;
            energyA += xa * xa;
        }

        return num / Math.Sqrt((energyA * energyB) + 1e-18);
    }

    /// <summary>
    /// ストライド付き実数ベクトルの正規化相関を AdvSimd で計算します。
    /// </summary>
    /// <param name="a">入力 A。</param>
    /// <param name="b">入力 B。</param>
    /// <param name="stride">サンプル間隔。</param>
    /// <param name="meanB">B の平均。</param>
    /// <param name="energyB">B のエネルギー。</param>
    /// <param name="count">相関に使うサンプル数。</param>
    /// <returns>正規化相関値。</returns>
    private static double CorrelateRealStridedAdvSimd(
        ReadOnlySpan<Complex> a,
        ReadOnlySpan<Complex> b,
        int stride,
        double meanB,
        double energyB,
        int count)
    {
        var ad = MemoryMarshal.Cast<Complex, double>(a);
        var bd = MemoryMarshal.Cast<Complex, double>(b);
        var sumVec = Vector128<double>.Zero;
        var i = 0;

        for (; i + 2 <= count; i += 2)
        {
            var s0 = i * stride;
            var ra = Vector128.Create(
                ad[s0 * 2],
                ad[(s0 + stride) * 2]);
            sumVec = AdvSimd.Arm64.Add(sumVec, ra);
        }

        var sumA = sumVec.GetElement(0) + sumVec.GetElement(1);
        for (; i < count; i++)
        {
            sumA += ad[(i * stride) * 2];
        }

        var meanA = sumA / count;
        var meanAVec = Vector128.Create(meanA);
        var meanBVec = Vector128.Create(meanB);
        var numVec = Vector128<double>.Zero;
        var energyAVec = Vector128<double>.Zero;
        i = 0;

        for (; i + 2 <= count; i += 2)
        {
            var s0 = i * stride;
            var ra = Vector128.Create(
                ad[s0 * 2],
                ad[(s0 + stride) * 2]);
            var rb = Vector128.Create(
                bd[s0 * 2],
                bd[(s0 + stride) * 2]);
            var xa = AdvSimd.Arm64.Subtract(ra, meanAVec);
            var xb = AdvSimd.Arm64.Subtract(rb, meanBVec);
            numVec = AdvSimd.Arm64.Add(numVec, AdvSimd.Arm64.Multiply(xa, xb));
            energyAVec = AdvSimd.Arm64.Add(energyAVec, AdvSimd.Arm64.Multiply(xa, xa));
        }

        var num = numVec.GetElement(0) + numVec.GetElement(1);
        var energyA = energyAVec.GetElement(0) + energyAVec.GetElement(1);
        for (; i < count; i++)
        {
            var realIndex = (i * stride) * 2;
            var xa = ad[realIndex] - meanA;
            var xb = bd[realIndex] - meanB;
            num += xa * xb;
            energyA += xa * xa;
        }

        return num / Math.Sqrt((energyA * energyB) + 1e-18);
    }

    /// <summary>
    /// カセット想定のワウ／フラッターからサンプルごとの相対速度を生成します。
    /// </summary>
    /// <param name="sampleCount">サンプル数。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">ワウ量。</param>
    /// <param name="wowPhase">ワウ位相。</param>
    /// <param name="flutterPhase">フラッター位相。</param>
    /// <returns>サンプルごとの相対速度。</returns>
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
        var sr = Math.Max(1, sampleRate);
        var wowStep = 2.0 * Math.PI * wowHz / sr;
        var flutterStep = 2.0 * Math.PI * flutterHz / sr;
        var wowSinStep = Math.Sin(wowStep);
        var wowCosStep = Math.Cos(wowStep);
        var flutterSinStep = Math.Sin(flutterStep);
        var flutterCosStep = Math.Cos(flutterStep);
        var wowGain = amount * 0.65;
        var flutterGain = amount * 0.35;
        var wowSin = Math.Sin(wowPhase);
        var wowCos = Math.Cos(wowPhase);
        var flutterSin = Math.Sin(flutterPhase);
        var flutterCos = Math.Cos(flutterPhase);

        for (var i = 0; i < sampleCount; i++)
        {
            var speed = 1.0 + (wowGain * wowSin) + (flutterGain * flutterSin);
            profile[i] = speed < 0.05 ? 0.05 : speed;

            var nextWowSin = (wowSin * wowCosStep) + (wowCos * wowSinStep);
            var nextWowCos = (wowCos * wowCosStep) - (wowSin * wowSinStep);
            wowSin = nextWowSin;
            wowCos = nextWowCos;

            var nextFlutterSin = (flutterSin * flutterCosStep) + (flutterCos * flutterSinStep);
            var nextFlutterCos = (flutterCos * flutterCosStep) - (flutterSin * flutterSinStep);
            flutterSin = nextFlutterSin;
            flutterCos = nextFlutterCos;
        }

        return profile;
    }

    /// <summary>
    /// シンボル単位のカセット相対速度プロファイルを生成します。
    /// </summary>
    /// <param name="symbolCount">シンボル数。</param>
    /// <param name="firstSymbol">先頭シンボル番号。</param>
    /// <param name="symbolLength">1 シンボルのサンプル数。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">ワウ量。</param>
    /// <param name="wowPhase">ワウ位相。</param>
    /// <param name="flutterPhase">フラッター位相。</param>
    /// <returns>シンボルごとの相対速度。</returns>
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
        var sr = Math.Max(1, sampleRate);
        var wowStep = 2.0 * Math.PI * wowHz * symbolLength / sr;
        var flutterStep = 2.0 * Math.PI * flutterHz * symbolLength / sr;
        var startWow = wowPhase + (firstSymbol * wowStep);
        var startFlutter = flutterPhase + (firstSymbol * flutterStep);
        var wowSinStep = Math.Sin(wowStep);
        var wowCosStep = Math.Cos(wowStep);
        var flutterSinStep = Math.Sin(flutterStep);
        var flutterCosStep = Math.Cos(flutterStep);
        var wowGain = amount * 0.65;
        var flutterGain = amount * 0.35;
        var wowSin = Math.Sin(startWow);
        var wowCos = Math.Cos(startWow);
        var flutterSin = Math.Sin(startFlutter);
        var flutterCos = Math.Cos(startFlutter);

        for (var s = 0; s < symbolCount; s++)
        {
            profile[s] = 1.0 + (wowGain * wowSin) + (flutterGain * flutterSin);

            var nextWowSin = (wowSin * wowCosStep) + (wowCos * wowSinStep);
            var nextWowCos = (wowCos * wowCosStep) - (wowSin * wowSinStep);
            wowSin = nextWowSin;
            wowCos = nextWowCos;

            var nextFlutterSin = (flutterSin * flutterCosStep) + (flutterCos * flutterSinStep);
            var nextFlutterCos = (flutterCos * flutterCosStep) - (flutterSin * flutterSinStep);
            flutterSin = nextFlutterSin;
            flutterCos = nextFlutterCos;
        }

        NormalizeSpeedProfileMean(profile);
        return profile;
    }

    /// <summary>
    /// 2 実数系列の正規化相関を計算します。
    /// </summary>
    /// <param name="a">入力 A。</param>
    /// <param name="b">入力 B。</param>
    /// <returns>正規化相関値。</returns>
    private static double CorrelateReal(Complex[] a, Complex[] b)
    {
        return CorrelateRealStrided(a, b, 1);
    }

    /// <summary>
    /// ストライド付き 2 実数系列の正規化相関を計算します。
    /// </summary>
    /// <param name="a">入力 A。</param>
    /// <param name="b">入力 B。</param>
    /// <param name="stride">サンプル間隔。</param>
    /// <returns>正規化相関値。</returns>
    private static double CorrelateRealStrided(Complex[] a, Complex[] b, int stride)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n <= 0)
        {
            return double.NegativeInfinity;
        }

        stride = Math.Max(1, stride);
        var aSpan = a.AsSpan(0, n);
        var bSpan = b.AsSpan(0, n);
        var reference = BuildCorrelationReference(bSpan, stride);
        return CorrelateRealStridedWithReference(
            aSpan,
            bSpan,
            stride,
            reference.Mean,
            reference.Energy,
            reference.Count);
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
    /// プリアンブル区間の速度プロファイルを全体サンプル長へ外挿します。
    /// </summary>
    /// <param name="preambleSpeeds">プリアンブル速度。</param>
    /// <param name="sampleCount">全体サンプル数。</param>
    /// <param name="analysisStartSample">解析開始位置。</param>
    /// <returns>全体長の速度プロファイル。</returns>
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
    /// 速度プロファイルの偏差からワウ補正が必要か判定します。
    /// </summary>
    /// <param name="speedProfile">相対速度プロファイル。</param>
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
        return maxDeviation >= 0.002
            && meanAbs >= 0.0007
            && above >= 8
            && below >= 8;
    }

    /// <summary>
    /// CP 相関／パイロットから受信相対速度プロファイルを推定します。
    /// </summary>
    /// <param name="samples">入力 PCM。</param>
    /// <param name="useRightChannel">R 搬送波レイアウトを使うか。</param>
    /// <param name="analysisStartSample">解析開始サンプル。</param>
    /// <param name="analysisSampleCount">解析サンプル数。</param>
    /// <param name="allowSymbolOffsetSearch">シンボルオフセット探索を許すか。</param>
    /// <param name="requireStrongCpImprovement">強い CP 改善を要求するか。</param>
    /// <returns>推定相対速度プロファイル。</returns>
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
    /// 測定速度をカセット・ワウモデルへ最小二乗フィットします。
    /// </summary>
    /// <param name="measured">測定速度。</param>
    /// <param name="firstSymbol">先頭シンボル。</param>
    /// <param name="symbolLength">シンボル長。</param>
    /// <returns>フィット後の速度プロファイル。</returns>
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

        // 正規方程式を構築（4x4）
        var ata = new double[4, 4];
        var atb = new double[4];
        Span<double> row = stackalloc double[4];
        for (var i = 0; i < measured.Length; i++)
        {
            var t = ((firstSymbol + i) * symbolLength) / (double)sampleRate;
            var y = measured[i] - 1.0;
            SinCosFromQuarterLut(w1 * t, out row[0], out row[1]);
            SinCosFromQuarterLut(w2 * t, out row[2], out row[3]);
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
            SinCosFromQuarterLut(w1 * t, out var sinW1, out var cosW1);
            SinCosFromQuarterLut(w2 * t, out var sinW2, out var cosW2);
            fitted[i] = 1.0
                + (coef[0] * sinW1)
                + (coef[1] * cosW1)
                + (coef[2] * sinW2)
                + (coef[3] * cosW2);
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
    /// 4×4 連立方程式 Ax=b を解きます。
    /// </summary>
    /// <param name="a">係数行列（16 要素）。</param>
    /// <param name="b">右辺。</param>
    /// <param name="x">解の出力。</param>
    /// <returns>解けた場合 true。</returns>
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
    /// <param name="speeds">相対速度（破壊的）。</param>
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
    /// 窓内で CP 相関が最大になるシンボル先頭オフセットを探します。
    /// </summary>
    /// <param name="window">探索窓 PCM。</param>
    /// <param name="maxSearch">最大探索サンプル数。</param>
    /// <param name="requireStrongCpImprovement">強い改善が無い場合は 0 を返すか。</param>
    /// <returns>最良オフセット（サンプル）。</returns>
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
    /// OFDM シンボルの CP と末尾の正規化相関スコアを計算します。
    /// </summary>
    /// <param name="window">CP 付きシンボル窓。</param>
    /// <param name="offset">候補シンボル先頭オフセット。</param>
    /// <param name="fftSize">FFT 長（データ部長）。</param>
    /// <param name="cp">CP 長。</param>
    /// <returns>正規化相関スコア。</returns>
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
    /// パイロット位相回転から相対速度（サンプリング速度比）を推定します。
    /// </summary>
    /// <param name="freqBins">受信周波数ビン。</param>
    /// <param name="pilotBins">速度推定に使うパイロットビン。</param>
    /// <param name="carrierBins">パイロット含むキャリアビン。</param>
    /// <returns>推定相対速度。</returns>
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
    /// 複素数の振幅 |z| を返します。
    /// </summary>
    /// <param name="value">複素数。</param>
    /// <returns>振幅。</returns>
    private static double ComplexMagnitude(Complex value)
    {
        return Math.Sqrt((value.Real * value.Real) + (value.Imaginary * value.Imaginary));
    }

    /// <summary>
    /// 速度プロファイルの逆写像で PCM 全体をリサンプリングします（ワウ補正）。
    /// </summary>
    /// <param name="samples">入力 PCM。</param>
    /// <param name="speedProfile">相対速度。</param>
    /// <returns>補正後 PCM。</returns>
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
    /// 速度プロファイルの逆写像で指定セグメントをリサンプリングします。
    /// </summary>
    /// <param name="samples">入力 PCM。</param>
    /// <param name="speedProfile">相対速度。</param>
    /// <param name="segmentStart">セグメント開始。</param>
    /// <param name="segmentLength">セグメント長。</param>
    /// <returns>補正後セグメント。</returns>
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
    /// ワウ／フラッターパラメータの逆写像で指定セグメントをリサンプリングします。
    /// </summary>
    /// <param name="samples">入力 PCM。</param>
    /// <param name="segmentStart">セグメント開始。</param>
    /// <param name="segmentLength">セグメント長。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">ワウ量。</param>
    /// <param name="wowPhase">ワウ位相。</param>
    /// <param name="flutterPhase">フラッター位相。</param>
    /// <param name="stride">間引きストライド。</param>
    /// <returns>補正後セグメント。</returns>
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
    /// ワウ／フラッターパラメータの逆写像でセグメントを output バッファへ書き込みます。
    /// </summary>
    /// <param name="samples">入力 PCM。</param>
    /// <param name="segmentStart">セグメント開始。</param>
    /// <param name="segmentLength">セグメント長。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">ワウ量。</param>
    /// <param name="wowPhase">ワウ位相。</param>
    /// <param name="flutterPhase">フラッター位相。</param>
    /// <param name="output">出力バッファ。</param>
    /// <param name="stride">間引きストライド。</param>
    /// <param name="streamBaseSample">ストリーム基準サンプル。</param>
    private void ResampleSegmentWithInverseSpeed(
        Complex[] samples,
        int segmentStart,
        int segmentLength,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase,
        Span<Complex> output,
        int stride = 1,
        long streamBaseSample = 0)
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
        var baseAbs = Math.Max(0L, streamBaseSample);

        /// <summary>
        /// カセット速度モデルの絶対サンプル位置における累積時間を返します。
        /// </summary>
        /// <param name="absIndex">ストリーム絶対サンプル位置。</param>
        /// <returns>累積サンプル相当値。</returns>
        double CumulAt(long absIndex)
        {
            if (absIndex <= 0)
            {
                return 0.0;
            }

            return CassetteCumulAtCached(
                (int)Math.Min(absIndex, int.MaxValue),
                wowGain,
                flutterGain,
                wowPhase,
                wowOmega,
                flutterPhase,
                flutterOmega);
        }

        // 圧縮バッファでもストリーム絶対時刻の scale を使う
        var absEnd = Math.Max(1L, baseAbs + n - 1);
        var cumulEnd = CumulAt(absEnd);
        var scale = cumulEnd > 1e-12 ? absEnd / cumulEnd : 1.0;

        var i = 0;
        var c0 = CumulAt(baseAbs);
        var c1 = CumulAt(baseAbs + 1);
        for (var j = 0; j < segmentLength; j += stride)
        {
            var absOut = baseAbs + segmentStart + j;
            var target = absOut / scale;
            if (i >= n - 1)
            {
                output[j] = samples[^1];
                continue;
            }

            var guess = (int)(target - baseAbs);
            if (guess > i + 8 && guess < n - 1)
            {
                i = guess;
                c0 = CumulAt(baseAbs + i);
                c1 = CumulAt(baseAbs + i + 1);
            }

            while (i < n - 2 && c1 <= target)
            {
                i++;
                c0 = c1;
                c1 = CumulAt(baseAbs + i + 1);
            }

            while (i > 0 && c0 > target)
            {
                i--;
                c1 = c0;
                c0 = CumulAt(baseAbs + i);
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
    /// カセット速度モデルの累積サンプル位置を計算します。
    /// </summary>
    /// <param name="i">サンプル指数。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">ワウ量。</param>
    /// <param name="wowPhase">ワウ位相。</param>
    /// <param name="flutterPhase">フラッター位相。</param>
    /// <returns>累積位置。</returns>
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
    /// 事前計算ゲイン／角周波数を用いて累積サンプル位置を計算します。
    /// </summary>
    /// <param name="i">サンプル指数。</param>
    /// <param name="wowGain">ワウ振幅ゲイン。</param>
    /// <param name="flutterGain">フラッター振幅ゲイン。</param>
    /// <param name="wowPhase">ワウ位相。</param>
    /// <param name="wowOmega">ワウ角周波数。</param>
    /// <param name="flutterPhase">フラッター位相。</param>
    /// <param name="flutterOmega">フラッター角周波数。</param>
    /// <returns>累積位置。</returns>
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
    /// 等差位相の sin 和を閉形式で計算します。
    /// </summary>
    /// <param name="phase0">初項位相。</param>
    /// <param name="omega">位相刻み。</param>
    /// <param name="count">加算する項数。</param>
    /// <returns>sin の総和。</returns>
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
    /// 累積位置 target に達する最小サンプル指数を二分探索します。
    /// </summary>
    /// <param name="target">目標累積位置。</param>
    /// <param name="sampleCount">探索上限。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">ワウ量。</param>
    /// <param name="wowPhase">ワウ位相。</param>
    /// <param name="flutterPhase">フラッター位相。</param>
    /// <returns>サンプル指数。</returns>
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
    /// 速度プロファイルから逆写像用の累積位置テーブルを構築します。
    /// </summary>
    /// <param name="sampleCount">サンプル数。</param>
    /// <param name="speedProfile">相対速度。</param>
    /// <param name="cumul">累積出力バッファ。</param>
    /// <param name="scale">スケール。</param>
    /// <param name="speedHop">速度プロファイルのホップ（1=毎サンプル、それ以外=シンボル長）。</param>
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
    /// 累積位置テーブル上でエルミート補間し、歪み補正後サンプルを得ます。
    /// </summary>
    /// <param name="samples">入力 PCM。</param>
    /// <param name="cumul">累積位置テーブル。</param>
    /// <param name="scale">スケール。</param>
    /// <param name="outputIndex">出力指数。</param>
    /// <param name="warpedIndex">ワープ指数（探索用）。</param>
    /// <returns>補間サンプル。</returns>
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
    /// 実 PCM をエルミート補間で読みます。
    /// </summary>
    /// <param name="samples">入力 PCM。</param>
    /// <param name="position">実数位置。</param>
    /// <returns>補間サンプル。</returns>
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
    /// 単一シンボルの CP 相関ロックスコアを計算します。
    /// </summary>
    /// <param name="symbolWithCp">CP 付きシンボル。</param>
    /// <returns>ロックスコア。</returns>
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
    /// L チャネル前提で単一シンボルの品質スコアを計算します。
    /// </summary>
    /// <param name="symbolWithCp">CP 付きシンボル。</param>
    /// <param name="pilotBins">パイロットビン。</param>
    /// <returns>品質スコア。</returns>
    private double ScoreSingleSymbolLock(ReadOnlySpan<Complex> symbolWithCp, List<int> pilotBins)
    {
        var timeNoCp = _scoreTimeNoCpScratch;
        var freqBins = _scoreFreqBinsScratch;
        return ScoreSingleSymbolLock(symbolWithCp, pilotBins, timeNoCp, freqBins);
    }

    /// <summary>
    /// L チャネル前提で単一シンボルの品質スコアを計算します。
    /// </summary>
    /// <param name="symbolWithCp">CP 付きシンボル。</param>
    /// <param name="pilotBins">パイロットビン。</param>
    /// <param name="timeNoCp">CP 除去作業バッファ。</param>
    /// <param name="freqBins">FFT 作業バッファ。</param>
    /// <returns>品質スコア。</returns>
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
    /// ストリーム上の OFDM からソフト LLR を復調する中核処理です（L/R 結合可）。
    /// </summary>
    /// <param name="samples">主チャネル PCM。</param>
    /// <param name="secondarySamples">副チャネル PCM（ステレオ結合時）。null 可。</param>
    /// <param name="cursor">読み取り位置（更新あり）。</param>
    /// <param name="bitCount">取り出す情報ビット数。</param>
    /// <param name="useRightChannel">主側で R 搬送波を使うか。</param>
    /// <param name="secondaryUseRightChannel">副側で R 搬送波を使うか。</param>
    /// <param name="logicalSampleOffset">論理サンプル位置。</param>
    /// <param name="searchRadius">シンボル先頭探索半径。</param>
    /// <param name="noiseVariance">雑音分散の初期値。</param>
    /// <param name="estimateNoiseFromPilots">パイロットから雑音を推定するか。</param>
    /// <param name="onEqualizedDataSymbol">等化後データシンボルのコールバック。</param>
    /// <param name="onEqualizedDataSymbolFrame">シンボル単位の等化後フレーム・コールバック。</param>
    /// <param name="onFftSymbolFrame">FFT 後ビンのコールバック。</param>
    /// <param name="onOfdmSymbolProgress">進捗コールバック。</param>
    /// <returns>ビットごとのソフト LLR。</returns>
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
        Action<Complex>? onEqualizedDataSymbol,
        Action<Complex[], byte[], int>? onEqualizedDataSymbolFrame,
        Action<Complex[], int>? onFftSymbolFrame,
        Action<int, int, int>? onOfdmSymbolProgress)
    {
        if (bitCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        }

        ArgumentNullException.ThrowIfNull(samples);
        if (secondarySamples is not null && secondarySamples.Length != samples.Length)
        {
            throw new ArgumentException("Primary/secondary sample lengths must match.", nameof(secondarySamples));
        }

        var primaryPilotBins = useRightChannel ? _rightPilotBins : _leftPilotBins;
        var secondaryPilotBins = secondaryUseRightChannel ? _rightPilotBins : _leftPilotBins;
        var symbolLength = SamplesPerOfdmSymbol;
        var symbolCount = bitCount == 0
            ? 1
            : (bitCount + BitsPerOfdmSymbol - 1) / BitsPerOfdmSymbol;

        var llrs = new double[bitCount];
        var bitIndex = 0;
        _ = logicalSampleOffset;
        var primaryAgcState = new PilotGroupAgcState(primaryPilotBins.Count);
        var secondaryAgcState = new PilotGroupAgcState(secondaryPilotBins.Count);
        var fftSize = _config.FftSize;
        var primaryTimeNoCp = new Complex[fftSize];
        var primaryFreqBins = new Complex[fftSize];
        var primaryEqualizers = new Complex[fftSize];
        var secondaryTimeNoCp = new Complex[fftSize];
        var secondaryFreqBins = new Complex[fftSize];
        var secondaryEqualizers = new Complex[fftSize];
        var position = cursor;

        for (var s = 0; s < symbolCount && bitIndex < bitCount; s++)
        {
            var start = FindBestSymbolStart(samples, position, searchRadius, useRightChannel);
            if (start + symbolLength > samples.Length)
            {
                throw new InvalidDataException("WAV ended while synchronizing OFDM symbol.");
            }

            var symbol = samples.AsSpan(start, symbolLength);
            PrepareSymbolFrequency(
                symbol,
                primaryPilotBins,
                useRightChannel,
                primaryAgcState,
                primaryTimeNoCp,
                primaryFreqBins,
                primaryEqualizers);

            var effectiveVariance = noiseVariance;
            if (estimateNoiseFromPilots)
            {
                var noiseAccum = 0.0;
                var noiseCount = 0;
                AccumulatePilotNoiseFromPrepared(
                    primaryFreqBins,
                    primaryEqualizers,
                    primaryPilotBins,
                    ref noiseAccum,
                    ref noiseCount);

                if (secondarySamples is not null)
                {
                    var secondarySymbol = secondarySamples.AsSpan(start, symbolLength);
                    PrepareSymbolFrequency(
                        secondarySymbol,
                        secondaryPilotBins,
                        secondaryUseRightChannel,
                        secondaryAgcState,
                        secondaryTimeNoCp,
                        secondaryFreqBins,
                        secondaryEqualizers);
                    AccumulatePilotNoiseFromPrepared(
                        secondaryFreqBins,
                        secondaryEqualizers,
                        secondaryPilotBins,
                        ref noiseAccum,
                        ref noiseCount);
                }

                if (noiseCount > 0)
                {
                    effectiveVariance = Math.Clamp(noiseAccum / noiseCount, 1e-4, 0.5);
                }
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
                onEqualizedDataSymbol,
                onEqualizedDataSymbolFrame);

            if (secondarySamples is not null)
            {
                var secondarySymbol = secondarySamples.AsSpan(start, symbolLength);
                PrepareSymbolFrequency(
                    secondarySymbol,
                    secondaryPilotBins,
                    secondaryUseRightChannel,
                    secondaryAgcState,
                    secondaryTimeNoCp,
                    secondaryFreqBins,
                    secondaryEqualizers);

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
                    onEqualizedDataSymbol,
                    onEqualizedDataSymbolFrame: null);
            }

            onOfdmSymbolProgress?.Invoke(s, symbolCount, start + symbolLength);
            position = start + symbolLength;
        }

        cursor = position;
        return llrs;
    }

    /// <summary>
    /// 1 OFDM シンボルを等化し、データキャリアのソフト LLR を llrs へ書き出します。
    /// </summary>
    /// <param name="symbolWithCp">CP 付きシンボル。</param>
    /// <param name="pilotBins">パイロットビン。</param>
    /// <param name="useRightChannel">R 搬送波を使うか。</param>
    /// <param name="agcState">パイロット AGC 状態。</param>
    /// <param name="logical">論理サンプル位置。</param>
    /// <param name="timeNoCp">CP 除去作業バッファ。</param>
    /// <param name="freqBins">FFT 作業バッファ。</param>
    /// <param name="equalizers">等化係数作業バッファ。</param>
    /// <param name="bitIndex">書き込みビット位置（更新あり）。</param>
    /// <param name="llrs">LLR 出力。</param>
    /// <param name="noiseVariance">雑音分散。</param>
    /// <param name="addToExisting">既存 LLR に加算するか（ステレオ結合）。</param>
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
        bool addToExisting)
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
            onEqualizedDataSymbol: null,
            onEqualizedDataSymbolFrame: null);
    }

    /// <summary>
    /// 既に FFT／等化済みの周波数ビンからデータキャリア LLR を書き出します。
    /// </summary>
    /// <param name="freqBins">周波数ビン。</param>
    /// <param name="equalizers">等化係数。</param>
    /// <param name="useRightChannel">R 搬送波を使うか。</param>
    /// <param name="logical">論理サンプル位置。</param>
    /// <param name="bitIndex">書き込みビット位置（更新あり）。</param>
    /// <param name="llrs">LLR 出力。</param>
    /// <param name="noiseVariance">雑音分散。</param>
    /// <param name="addToExisting">既存 LLR に加算するか。</param>
    /// <param name="onEqualizedDataSymbol">等化後シンボル・コールバック。</param>
    /// <param name="onEqualizedDataSymbolFrame">フレーム・コールバック。</param>
    private void EmitSymbolSoftLlrsFromPrepared(
        Complex[] freqBins,
        Complex[] equalizers,
        bool useRightChannel,
        long logical,
        ref int bitIndex,
        double[] llrs,
        double noiseVariance,
        bool addToExisting,
        Action<Complex>? onEqualizedDataSymbol,
        Action<Complex[], byte[], int>? onEqualizedDataSymbolFrame)
    {
        _ = logical;
        var dataOrder = ResolveDataCarrierOrder(useRightChannel);
        var dataModulationByBin = useRightChannel
            ? _rightDataCarrierModulationByBin
            : _leftDataCarrierModulationByBin;
        var dataGroupByBin = useRightChannel
            ? _rightDataCarrierGroupByBin
            : _leftDataCarrierGroupByBin;
        Span<double> softLlrScratch = stackalloc double[6];
        Complex[]? frameSnapshot = onEqualizedDataSymbolFrame is null ? null : new Complex[dataOrder.Count];
        byte[]? groupSnapshot = onEqualizedDataSymbolFrame is null ? null : new byte[dataOrder.Count];
        var frameCount = 0;
        foreach (var dataBin in dataOrder)
        {
            if (bitIndex >= llrs.Length)
            {
                break;
            }

            var equalized = freqBins[dataBin] * equalizers[dataBin];
            onEqualizedDataSymbol?.Invoke(equalized);
            if (frameSnapshot is not null && groupSnapshot is not null)
            {
                frameSnapshot[frameCount] = equalized;
                groupSnapshot[frameCount] = dataGroupByBin.TryGetValue(dataBin, out var group) ? group : (byte)0;
                frameCount++;
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

        if (frameSnapshot is not null && groupSnapshot is not null && frameCount > 0)
        {
            onEqualizedDataSymbolFrame?.Invoke(frameSnapshot, groupSnapshot, frameCount);
        }
    }

    /// <summary>
    /// パイロット残差から雑音電力を累積します。
    /// </summary>
    /// <param name="symbolWithCp">CP 付きシンボル。</param>
    /// <param name="pilotBins">パイロットビン。</param>
    /// <param name="useRightChannel">R 搬送波を使うか。</param>
    /// <param name="timeNoCp">CP 除去作業バッファ。</param>
    /// <param name="freqBins">FFT 作業バッファ。</param>
    /// <param name="equalizers">等化係数作業バッファ。</param>
    /// <param name="noiseAccum">雑音電力累積（更新あり）。</param>
    /// <param name="noiseCount">累積回数（更新あり）。</param>
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
    /// パイロット位置の等化誤差から雑音電力を加算します。
    /// </summary>
    /// <param name="freqBins">受信FFTビン列。</param>
    /// <param name="equalizers">推定済み等化係数。</param>
    /// <param name="pilotBins">パイロットビンの位置一覧。</param>
    /// <param name="noiseAccum">雑音電力の累積値。</param>
    /// <param name="noiseCount">累積サンプル数。</param>
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
    /// パイロットビンから等化係数を推定して返します。
    /// </summary>
    /// <param name="freqBins">受信FFTビン列。</param>
    /// <param name="pilotBins">パイロットビンの位置一覧。</param>
    /// <param name="useRightChannel">右チャネルを使う場合は true。</param>
    /// <param name="agcState">パイロット群ごとのAGC状態。未指定時は null。</param>
    /// <returns>推定した等化係数配列。</returns>
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
    /// パイロットビンから等化係数を推定し、既存バッファへ書き込みます。
    /// </summary>
    /// <param name="freqBins">受信FFTビン列。</param>
    /// <param name="pilotBins">パイロットビンの位置一覧。</param>
    /// <param name="useRightChannel">右チャネルを使う場合は true。</param>
    /// <param name="equalizers">推定結果の書き込み先バッファ。</param>
    /// <param name="agcState">パイロット群ごとのAGC状態。未指定時は null。</param>
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
    /// 指定キャリアが属するパイロット群インデックスを返します。
    /// 担当範囲は「下2CH + 自身 + 上1CH」（8本グループ内でクランプ）です。
    /// </summary>
    /// <param name="carrierBin">対象キャリアの周波数ビン。</param>
    /// <param name="allCarriers">概念順の全キャリアビン。</param>
    /// <param name="orderedPilots">昇順に並んだパイロットビン列。</param>
    /// <returns>パイロット群インデックス。</returns>
    private static int ResolvePilotGroupIndex(
        int carrierBin,
        List<int> allCarriers,
        List<int> orderedPilots)
    {
        if (orderedPilots.Count <= 1)
        {
            return 0;
        }

        var carrierIndex = allCarriers.IndexOf(carrierBin);
        if (carrierIndex < 0)
        {
            return 0;
        }

        const int groupSize = 8;
        var groupStart = (carrierIndex / groupSize) * groupSize;
        var groupEnd = Math.Min(groupStart + groupSize, allCarriers.Count) - 1;
        var best = 0;
        var bestDist = int.MaxValue;
        for (var pi = 0; pi < orderedPilots.Count; pi++)
        {
            var pilotIndex = allCarriers.IndexOf(orderedPilots[pi]);
            if (pilotIndex < groupStart || pilotIndex > groupEnd)
            {
                continue;
            }

            var coverStart = Math.Max(groupStart, pilotIndex - PilotCoverChannelsBelow);
            var coverEnd = Math.Min(groupEnd, pilotIndex + PilotCoverChannelsAbove);
            if (carrierIndex < coverStart || carrierIndex > coverEnd)
            {
                continue;
            }

            var dist = Math.Abs(carrierIndex - pilotIndex);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = pi;
            }
        }

        return bestDist == int.MaxValue ? 0 : best;
    }

    private sealed class PilotGroupAgcState
    {
        private readonly Complex[] _smoothed;
        private readonly bool[] _initialized;

        /// <summary>
        /// パイロットグループ別 AGC 状態を初期化します。
        /// </summary>
        /// <param name="groupCount">サブキャリアグループ数。</param>
        public PilotGroupAgcState(int groupCount)
        {
            var size = Math.Max(1, groupCount);
            _smoothed = new Complex[size];
            _initialized = new bool[size];
        }

        /// <summary>
        /// パイロット瞬間チャネル推定を平滑化し、グループ AGC 係数を更新して返します。
        /// </summary>
        /// <param name="groupIndex">パイロットグループ指数。</param>
        /// <param name="instantaneous">瞬間チャネル推定。</param>
        /// <returns>平滑化後のチャネル／AGC 係数。</returns>
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
    /// 隣接パイロットのチャネル推定をデータキャリア位置へ線形補間します。
    /// </summary>
    /// <param name="bin">データキャリアのビン番号。</param>
    /// <param name="orderedPilots">パイロットビン（昇順）。</param>
    /// <param name="channels">パイロットごとのチャネル推定。</param>
    /// <returns>補間されたチャネル係数。</returns>
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
    /// 等化後パイロット残差から雑音電力を推定します。
    /// </summary>
    /// <param name="freqBins">周波数ビン。</param>
    /// <param name="pilotBins">パイロットビン。</param>
    /// <param name="allCarriers">全キャリアビン。</param>
    /// <returns>推定雑音電力。</returns>
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
    /// 前後シンボルの等化係数を平滑化します。
    /// </summary>
    /// <param name="previous">前シンボル等化。</param>
    /// <param name="current">現シンボル等化。</param>
    /// <returns>平滑化後の等化係数。</returns>
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
    /// パイロット観測値と既知パイロットから等化係数（1/チャネル）を推定します。
    /// </summary>
    /// <param name="freqBins">受信周波数ビン。</param>
    /// <param name="pilotBins">平均に使うパイロットビン。</param>
    /// <returns>等化係数。</returns>
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
    /// 等化後データシンボルを硬判定し、ビット列へ書き出します。
    /// </summary>
    /// <param name="symbol">等化後複素シンボル。</param>
    /// <param name="modulationScheme">変調方式。</param>
    /// <param name="bitIndex">書き込みビット位置（更新あり）。</param>
    /// <param name="bits">ビット出力。</param>
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
            case ModulationScheme.Psk8:
                EmitPsk8Bits(symbol, ref bitIndex, bits);
                break;
            case ModulationScheme.Qam16:
                EmitPamAxisBits(symbol.Real * Math.Sqrt(10.0), bitsPerAxis: 2, Qam16Levels, ref bitIndex, bits);
                EmitPamAxisBits(symbol.Imaginary * Math.Sqrt(10.0), bitsPerAxis: 2, Qam16Levels, ref bitIndex, bits);
                break;
            case ModulationScheme.Qam64:
                EmitPamAxisBits(symbol.Real * Math.Sqrt(42.0), bitsPerAxis: 3, Qam64Levels, ref bitIndex, bits);
                EmitPamAxisBits(symbol.Imaginary * Math.Sqrt(42.0), bitsPerAxis: 3, Qam64Levels, ref bitIndex, bits);
                break;
            case ModulationScheme.Qam256:
                EmitPamAxisBits(symbol.Real * Math.Sqrt(170.0), bitsPerAxis: 4, Qam256Levels, ref bitIndex, bits);
                EmitPamAxisBits(symbol.Imaginary * Math.Sqrt(170.0), bitsPerAxis: 4, Qam256Levels, ref bitIndex, bits);
                break;
            default:
                throw new NotSupportedException("Demodulation for this scheme is not implemented yet.");
        }
    }

    /// <summary>
    /// 等化後データシンボルからソフト LLR を書き出します。
    /// </summary>
    /// <param name="symbol">等化後複素シンボル。</param>
    /// <param name="modulationScheme">変調方式。</param>
    /// <param name="bitIndex">書き込みビット位置（更新あり）。</param>
    /// <param name="llrs">LLR 出力。</param>
    /// <param name="noiseVariance">雑音分散。</param>
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
            case ModulationScheme.Psk8:
                EmitPsk8SoftLlrs(symbol, invVar, ref bitIndex, llrs);
                break;
            case ModulationScheme.Qam16:
                EmitPamAxisSoftLlrsCore(symbol.Real * Math.Sqrt(10.0), bitsPerAxis: 2, Qam16Levels, invVar, ref bitIndex, llrs);
                EmitPamAxisSoftLlrsCore(symbol.Imaginary * Math.Sqrt(10.0), bitsPerAxis: 2, Qam16Levels, invVar, ref bitIndex, llrs);
                break;
            case ModulationScheme.Qam64:
                EmitPamAxisSoftLlrsCore(symbol.Real * Math.Sqrt(42.0), bitsPerAxis: 3, Qam64Levels, invVar, ref bitIndex, llrs);
                EmitPamAxisSoftLlrsCore(symbol.Imaginary * Math.Sqrt(42.0), bitsPerAxis: 3, Qam64Levels, invVar, ref bitIndex, llrs);
                break;
            case ModulationScheme.Qam256:
                EmitPamAxisSoftLlrsCore(symbol.Real * Math.Sqrt(170.0), bitsPerAxis: 4, Qam256Levels, invVar, ref bitIndex, llrs);
                EmitPamAxisSoftLlrsCore(symbol.Imaginary * Math.Sqrt(170.0), bitsPerAxis: 4, Qam256Levels, invVar, ref bitIndex, llrs);
                break;
            default:
                throw new NotSupportedException("Soft demodulation for this scheme is not implemented yet.");
        }
    }

    /// <summary>
    /// QAM の I または Q 軸（PAM）についてソフト LLR を書き出します。
    /// </summary>
    /// <param name="amplitude">軸振幅。</param>
    /// <param name="bitsPerAxis">軸あたりビット数。</param>
    /// <param name="levels">PAM レベル表。</param>
    /// <param name="invVariance">雑音分散の逆数。</param>
    /// <param name="bitIndex">書き込みビット位置（更新あり）。</param>
    /// <param name="llrs">LLR 出力。</param>
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
    /// PAM 軸ソフト LLR のスカラー実装です。
    /// </summary>
    /// <param name="amplitude">軸振幅。</param>
    /// <param name="bitsPerAxis">軸あたりビット数。</param>
    /// <param name="levels">PAM レベル表。</param>
    /// <param name="invVariance">雑音分散の逆数。</param>
    /// <param name="bitIndex">書き込みビット位置（更新あり）。</param>
    /// <param name="llrs">LLR 出力。</param>
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
    /// 8PSK シンボルを最近傍位相へ硬判定し、3bit を書き込みます。
    /// </summary>
    /// <param name="symbol">受信複素シンボル。</param>
    /// <param name="bitIndex">書き込みビット位置（更新あり）。</param>
    /// <param name="bits">硬判定ビット出力。</param>
    private static void EmitPsk8Bits(Complex symbol, ref int bitIndex, bool[] bits)
    {
        var phaseIndex = ResolveNearestPsk8PhaseIndex(symbol);
        var packed = Psk8BitsByPhaseIndex[phaseIndex];
        WriteBit(ref bitIndex, bits, (packed & 0b100) != 0);
        WriteBit(ref bitIndex, bits, (packed & 0b010) != 0);
        WriteBit(ref bitIndex, bits, (packed & 0b001) != 0);
    }

    /// <summary>
    /// 8PSK のソフト LLR（Max-Log 近似）を 3bit 分書き込みます。
    /// </summary>
    /// <param name="symbol">受信複素シンボル。</param>
    /// <param name="invVariance">雑音分散の逆数。</param>
    /// <param name="bitIndex">書き込みビット位置（更新あり）。</param>
    /// <param name="llrs">LLR 出力。</param>
    private static void EmitPsk8SoftLlrs(Complex symbol, double invVariance, ref int bitIndex, Span<double> llrs)
    {
        for (var bitOffset = 2; bitOffset >= 0; bitOffset--)
        {
            var best0 = double.PositiveInfinity;
            var best1 = double.PositiveInfinity;
            for (var i = 0; i < Psk8Symbols.Length; i++)
            {
                var d = symbol - Psk8Symbols[i];
                var metric = (d.Real * d.Real) + (d.Imaginary * d.Imaginary);
                if (((Psk8BitsByPhaseIndex[i] >> bitOffset) & 1) == 0)
                {
                    best0 = Math.Min(best0, metric);
                }
                else
                {
                    best1 = Math.Min(best1, metric);
                }
            }

            WriteLlr(ref bitIndex, llrs, (best0 - best1) * 0.5 * invVariance);
        }
    }

    /// <summary>
    /// 8PSK の最近傍位相インデックス（0..7）を返します。
    /// </summary>
    /// <param name="symbol">受信複素シンボル。</param>
    /// <returns>最近傍の位相インデックス（0..7）。</returns>
    private static int ResolveNearestPsk8PhaseIndex(Complex symbol)
    {
        var bestIndex = 0;
        var bestMetric = double.PositiveInfinity;
        for (var i = 0; i < Psk8Symbols.Length; i++)
        {
            var d = symbol - Psk8Symbols[i];
            var metric = (d.Real * d.Real) + (d.Imaginary * d.Imaginary);
            if (metric < bestMetric)
            {
                bestMetric = metric;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    /// <summary>
    /// 16QAM の PAM 軸ソフト LLR を AVX で計算します。
    /// </summary>
    /// <param name="amplitude">軸振幅。</param>
    /// <param name="invVariance">雑音分散の逆数。</param>
    /// <param name="bitIndex">書き込みビット位置（更新あり）。</param>
    /// <param name="llrs">LLR 出力。</param>
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
    /// 64QAM の PAM 軸ソフト LLR を AVX で計算します。
    /// </summary>
    /// <param name="amplitude">軸振幅。</param>
    /// <param name="invVariance">雑音分散の逆数。</param>
    /// <param name="bitIndex">書き込みビット位置（更新あり）。</param>
    /// <param name="llrs">LLR 出力。</param>
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
    /// 16QAM の PAM 軸ソフト LLR を AdvSimd で計算します。
    /// </summary>
    /// <param name="amplitude">軸振幅。</param>
    /// <param name="invVariance">雑音分散の逆数。</param>
    /// <param name="bitIndex">書き込みビット位置（更新あり）。</param>
    /// <param name="llrs">LLR 出力。</param>
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
    /// 64QAM の PAM 軸ソフト LLR を AdvSimd で計算します。
    /// </summary>
    /// <param name="amplitude">軸振幅。</param>
    /// <param name="invVariance">雑音分散の逆数。</param>
    /// <param name="bitIndex">書き込みビット位置（更新あり）。</param>
    /// <param name="llrs">LLR 出力。</param>
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
    /// Gray 符号対応の PAM レベル表をビット数から構築します。
    /// </summary>
    /// <param name="bitsPerAxis">軸あたりビット数。</param>
    /// <param name="levels">レベル数（通常 2^bits）。</param>
    /// <returns>昇順 PAM 振幅レベル。</returns>
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
    /// LLR を指定ビット位置へ書き込みます（範囲外は無視）。
    /// </summary>
    /// <param name="bitIndex">ビット位置。</param>
    /// <param name="llrs">LLR 配列。</param>
    /// <param name="value">LLR 値。</param>
    private static void WriteLlr(ref int bitIndex, Span<double> llrs, double value)
    {
        if (bitIndex >= llrs.Length)
        {
            return;
        }

        llrs[bitIndex++] = value;
    }

    /// <summary>
    /// PAM 軸振幅を最近傍レベル硬判定し、Gray→binary でビットを書き出します。
    /// </summary>
    /// <param name="amplitude">軸振幅。</param>
    /// <param name="bitsPerAxis">軸あたりビット数。</param>
    /// <param name="levels">PAM レベル表。</param>
    /// <param name="bitIndex">書き込みビット位置（更新あり）。</param>
    /// <param name="bits">ビット出力。</param>
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
    /// 振幅に最も近い PAM レベル指数を返します。
    /// </summary>
    /// <param name="amplitude">軸振幅。</param>
    /// <param name="levels">PAM レベル表。</param>
    /// <returns>レベル指数。</returns>
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
    /// Gray 符号を通常の二進値へ変換します。
    /// </summary>
    /// <param name="gray">Gray 符号値。</param>
    /// <returns>二進値。</returns>
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
    /// ビットを指定位置へ書き込みます（範囲外は無視）。
    /// </summary>
    /// <param name="bitIndex">ビット位置。</param>
    /// <param name="bits">ビット配列。</param>
    /// <param name="value">ビット値。</param>
    private static void WriteBit(ref int bitIndex, bool[] bits, bool value)
    {
        if (bitIndex >= bits.Length)
        {
            return;
        }

        bits[bitIndex++] = value;
    }

    /// <summary>
    /// CP 除去→FFT→パイロット等化／AGC を行い、周波数ビンと等化係数を埋めます。
    /// </summary>
    /// <param name="symbolWithCp">CP 付き時間シンボル。</param>
    /// <param name="pilotBins">パイロットビン。</param>
    /// <param name="useRightChannel">R 搬送波レイアウトを使うか。</param>
    /// <param name="agcState">パイロット AGC 状態。</param>
    /// <param name="timeNoCp">CP 除去後の作業バッファ。</param>
    /// <param name="freqBins">FFT 出力ビン。</param>
    /// <param name="equalizers">等化係数出力。</param>
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
    /// CP 付きシンボルからデータ部だけを切り出します。
    /// </summary>
    /// <param name="withCp">CP 付きシンボル。</param>
    /// <param name="cpLength">CP 長。</param>
    /// <returns>CP 除去後の時間領域。</returns>
    private static Complex[] RemoveCyclicPrefix(ReadOnlySpan<Complex> withCp, int cpLength)
    {
        var result = new Complex[withCp.Length - cpLength];
        withCp.Slice(cpLength).CopyTo(result);
        return result;
    }

    /// <summary>
    /// 送信 IFFT と対になる正規化のフォワード FFT を実行します。
    /// </summary>
    /// <param name="time">時間領域。</param>
    /// <returns>周波数領域ビン。</returns>
    private static Complex[] ForwardFftMatchingInverse(Complex[] time)
    {
        var freq = new Complex[time.Length];
        ForwardFftMatchingInverseInto(time, freq);
        return freq;
    }

    /// <summary>
    /// 送信 IFFT と対になる正規化のフォワード FFT を destination へ書き込みます。
    /// </summary>
    /// <param name="time">時間領域。</param>
    /// <param name="destination">周波数領域出力。</param>
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
    /// 実数 PCM に Hann 窓を掛けフォワード FFT し、表示用スペクトルを destination へ書きます。
    /// </summary>
    /// <param name="timePcm">実数 PCM（Imag=0）。末尾 destination 長を使用。</param>
    /// <param name="destination">スペクトル出力。</param>
    public static void ComputeForwardSpectrumFromRealPcm(
        ReadOnlySpan<Complex> timePcm,
        Complex[] destination)
    {
        if (destination.Length == 0)
        {
            throw new ArgumentException("Destination is empty.", nameof(destination));
        }

        if (timePcm.Length < destination.Length)
        {
            throw new ArgumentException("PCM window is shorter than FFT size.", nameof(timePcm));
        }

        // Hann 窓（1-cos = 標準 Hann の 2 倍。コヒーレントゲイン 0.5 を打ち消す）
        ApplyHannWindowFromRealPcm(timePcm, destination);
        FftInPlace(destination);
    }

    /// <summary>
    /// 実数 PCM を矩形窓のままフォワード FFT し、destination へ書きます（I-Q 用）。
    /// </summary>
    /// <param name="timePcm">実数 PCM（Imag=0）。末尾 destination 長を使用。</param>
    /// <param name="destination">FFT 出力。</param>
    public static void ComputeRectangularForwardFftFromRealPcm(
        ReadOnlySpan<Complex> timePcm,
        Complex[] destination)
    {
        if (destination.Length == 0)
        {
            throw new ArgumentException("Destination is empty.", nameof(destination));
        }

        if (timePcm.Length < destination.Length)
        {
            throw new ArgumentException("PCM window is shorter than FFT size.", nameof(timePcm));
        }

        CopyRealPcmToFftInput(timePcm, destination);
        FftInPlace(destination);
    }

    /// <summary>
    /// サンプリング周波数を返します。
    /// </summary>
    public int SampleRate => _config.SampleRate;

    /// <summary>
    /// キャリア配置に従いパイロット／データシンボルを周波数ビンへ配置します。
    /// </summary>
    /// <param name="channel">L/R チャネル。</param>
    /// <param name="bins">周波数ビン出力。</param>
    private void BuildFrequencyDomainSymbol(CarrierChannel channel, Complex[] bins)
    {
        if (bins.Length < _config.FftSize)
        {
            throw new ArgumentException("Frequency bin buffer is too short.", nameof(bins));
        }

        Array.Clear(bins, 0, _config.FftSize);
        var useRight = channel == CarrierChannel.Right;
        var pilotBins = useRight ? _rightPilotBins : _leftPilotBins;
        var dataBins = useRight ? _rightDataCarrierBase : _leftDataCarrierBase;
        var dataModulationByBin = useRight
            ? _rightDataCarrierModulationByBin
            : _leftDataCarrierModulationByBin;

        foreach (var pilotBin in pilotBins)
        {
            bins[pilotBin] = PilotSymbol;
        }

        foreach (var dataBin in dataBins)
        {
            bins[dataBin] = GenerateModulatedSymbol(dataModulationByBin[dataBin]);
        }
    }

    /// <summary>
    /// 周波数シンボル作業バッファを必要長で確保（または再利用）します。
    /// </summary>
    /// <param name="scratch">既存バッファ。null 可。</param>
    /// <returns>長さ FftSize のバッファ。</returns>
    private Complex[] EnsureFreqSymbolScratch(ref Complex[]? scratch)
    {
        if (scratch is null || scratch.Length != _config.FftSize)
        {
            scratch = new Complex[_config.FftSize];
        }

        return scratch;
    }

    /// <summary>
    /// 変調方式に応じてビット列から 1 複素シンボルを消費・生成します。
    /// </summary>
    /// <param name="modulationScheme">変調方式。</param>
    /// <param name="bitIndex">読み取り位置（更新あり）。</param>
    /// <param name="bits">ビット列。</param>
    /// <returns>複素シンボル。</returns>
    private static Complex ConsumeModulatedSymbol(
        ModulationScheme modulationScheme,
        ref int bitIndex,
        ReadOnlySpan<bool> bits)
    {
        return modulationScheme switch
        {
            ModulationScheme.Bpsk => ConsumeBpskSymbol(ref bitIndex, bits),
            ModulationScheme.Qpsk => ConsumeQpskSymbol(ref bitIndex, bits),
            ModulationScheme.Psk8 => ConsumePsk8Symbol(ref bitIndex, bits),
            ModulationScheme.Qam16 => ConsumeQam16Symbol(ref bitIndex, bits),
            ModulationScheme.Qam64 => ConsumeQam64Symbol(ref bitIndex, bits),
            ModulationScheme.Qam256 => ConsumeQam256Symbol(ref bitIndex, bits),
            _ => throw new InvalidOperationException("Unsupported modulation scheme.")
        };
    }

    /// <summary>
    /// ビット列から 1 ビットを読みます。末尾超過時は false を返します。
    /// </summary>
    /// <param name="bitIndex">読み取り位置（更新あり）。</param>
    /// <param name="bits">ビット列。</param>
    /// <returns>読み取ったビット。末尾を超えた場合は false。</returns>
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
    /// ビット列から bitCount ビットを MSB 先で整数として読みます。
    /// </summary>
    /// <param name="bitIndex">読み取り位置（更新あり）。</param>
    /// <param name="bits">ビット列。</param>
    /// <param name="bitCount">読むビット数。</param>
    /// <returns>読み取った整数値。</returns>
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
    /// ビット列から BPSK シンボルを 1 つ生成します。
    /// </summary>
    /// <param name="bitIndex">読み取り位置（更新あり）。</param>
    /// <param name="bits">ビット列。</param>
    /// <returns>BPSK 複素シンボル。</returns>
    private static Complex ConsumeBpskSymbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var bit = ReadBitOrZero(ref bitIndex, bits);
        return new Complex(bit ? 1.0 : -1.0, 0.0);
    }

    /// <summary>
    /// ビット列から QPSK シンボルを 1 つ生成します。
    /// </summary>
    /// <param name="bitIndex">読み取り位置（更新あり）。</param>
    /// <param name="bits">ビット列。</param>
    /// <returns>QPSK 複素シンボル。</returns>
    private static Complex ConsumeQpskSymbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var iBit = ReadBitOrZero(ref bitIndex, bits);
        var qBit = ReadBitOrZero(ref bitIndex, bits);
        var real = iBit ? InvSqrt2 : -InvSqrt2;
        var imag = qBit ? InvSqrt2 : -InvSqrt2;
        return new Complex(real, imag);
    }

    /// <summary>
    /// ビット列から 8PSK シンボルを 1 つ生成します。
    /// </summary>
    /// <param name="bitIndex">読み取り位置（更新あり）。</param>
    /// <param name="bits">ビット列。</param>
    /// <returns>8PSK 複素シンボル。</returns>
    private static Complex ConsumePsk8Symbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var packed = ReadBitField(ref bitIndex, bits, 3);
        var phaseIndex = Psk8PhaseIndexByBits[packed & 0b111];
        return Psk8Symbols[phaseIndex];
    }

    /// <summary>
    /// ビット列から 16QAM シンボルを 1 つ生成します。
    /// </summary>
    /// <param name="bitIndex">読み取り位置（更新あり）。</param>
    /// <param name="bits">ビット列。</param>
    /// <returns>16QAM 複素シンボル。</returns>
    private static Complex ConsumeQam16Symbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var real = Qam16PamByBinary[ReadBitField(ref bitIndex, bits, 2)] * InvSqrt10;
        var imag = Qam16PamByBinary[ReadBitField(ref bitIndex, bits, 2)] * InvSqrt10;
        return new Complex(real, imag);
    }

    /// <summary>
    /// ビット列から 64QAM シンボルを 1 つ生成します。
    /// </summary>
    /// <param name="bitIndex">読み取り位置（更新あり）。</param>
    /// <param name="bits">ビット列。</param>
    /// <returns>64QAM 複素シンボル。</returns>
    private static Complex ConsumeQam64Symbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var real = Qam64PamByBinary[ReadBitField(ref bitIndex, bits, 3)] * InvSqrt42;
        var imag = Qam64PamByBinary[ReadBitField(ref bitIndex, bits, 3)] * InvSqrt42;
        return new Complex(real, imag);
    }

    /// <summary>
    /// ビット列から 256QAM シンボルを 1 つ生成します（性能測定用）。
    /// </summary>
    /// <param name="bitIndex">読み取り位置（更新あり）。</param>
    /// <param name="bits">ビット列。</param>
    /// <returns>256QAM 複素シンボル。</returns>
    private static Complex ConsumeQam256Symbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var real = Qam256PamByBinary[ReadBitField(ref bitIndex, bits, 4)] * InvSqrt170;
        var imag = Qam256PamByBinary[ReadBitField(ref bitIndex, bits, 4)] * InvSqrt170;
        return new Complex(real, imag);
    }

    /// <summary>
    /// キャリア列から spacing 間隔でパイロットビン集合を選びます（CH2/CH6 相当）。
    /// </summary>
    /// <param name="orderedBins">昇順キャリアビン。</param>
    /// <param name="spacing">パイロット間隔（通常 8）。</param>
    /// <returns>パイロットビン集合。</returns>
    private static HashSet<int> SelectPilotBins(List<int> orderedBins, int spacing)
    {
        // グループ内 CH2/CH6（0起点で index 2 と 6）をパイロットにする（modulation.mdc）。
        var pilots = new HashSet<int>();
        var groupSize = spacing > 0 ? spacing : 8;
        for (var start = 0; start < orderedBins.Count; start += groupSize)
        {
            var length = Math.Min(groupSize, orderedBins.Count - start);
            if (length <= 1)
            {
                continue;
            }

            if (2 < length)
            {
                pilots.Add(orderedBins[start + 2]);
            }

            if (6 < length)
            {
                pilots.Add(orderedBins[start + 6]);
            }
            else if (length >= 3)
            {
                // 短いグループ向けフォールバック（通常は groupSize=8）
                pilots.Add(orderedBins[start + (length / 2)]);
            }
        }

        return pilots;
    }

    /// <summary>
    /// 配列をインスタンス乱数で Fisher–Yates シャッフルします。
    /// </summary>
    /// <param name="values">シャッフル対象。</param>
    private void ShuffleInPlace(List<int> values) => ShuffleInPlace(values, _random);

    /// <summary>
    /// 配列を指定 Random で Fisher–Yates シャッフルします。
    /// </summary>
    /// <param name="values">シャッフル対象。</param>
    /// <param name="random">乱数源。</param>
    private static void ShuffleInPlace(List<int> values, Random random)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    /// <summary>
    /// 配列を指定 Random で Fisher–Yates シャッフルします。
    /// </summary>
    /// <param name="values">シャッフル対象。</param>
    /// <param name="random">乱数源。</param>
    private static void ShuffleInPlace(int[] values, Random random)
    {
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    /// <summary>
    /// 乱数から指定変調の複素シンボルを 1 つ生成します。
    /// </summary>
    /// <param name="modulationScheme">変調方式。</param>
    /// <returns>複素シンボル。</returns>
    private Complex GenerateModulatedSymbol(ModulationScheme modulationScheme)
    {
        return modulationScheme switch
        {
            ModulationScheme.Bpsk => GenerateBpskSymbol(),
            ModulationScheme.Qpsk => GenerateQpskSymbol(),
            ModulationScheme.Psk8 => GeneratePsk8Symbol(),
            ModulationScheme.Qam16 => GenerateQam16Symbol(),
            ModulationScheme.Qam64 => GenerateQam64Symbol(),
            ModulationScheme.Qam256 => GenerateQam256Symbol(),
            _ => throw new InvalidOperationException("Unsupported modulation scheme.")
        };
    }

    /// <summary>
    /// 乱数から BPSK シンボルを生成します。
    /// </summary>
    /// <returns>BPSK 複素シンボル。</returns>
    private Complex GenerateBpskSymbol()
    {
        var bit = _random.Next(2);
        return new Complex(bit == 0 ? -1.0 : 1.0, 0.0);
    }

    /// <summary>
    /// 乱数から QPSK シンボルを生成します。
    /// </summary>
    /// <returns>QPSK 複素シンボル。</returns>
    private Complex GenerateQpskSymbol()
    {
        var iBit = _random.Next(2);
        var qBit = _random.Next(2);

        var real = iBit == 0 ? -InvSqrt2 : InvSqrt2;
        var imag = qBit == 0 ? -InvSqrt2 : InvSqrt2;
        return new Complex(real, imag);
    }

    /// <summary>
    /// 乱数から 8PSK シンボルを生成します。
    /// </summary>
    /// <returns>8PSK 複素シンボル。</returns>
    private Complex GeneratePsk8Symbol()
    {
        var packed = NextBits(3);
        var phaseIndex = Psk8PhaseIndexByBits[packed & 0b111];
        return Psk8Symbols[phaseIndex];
    }

    /// <summary>
    /// 乱数から 16QAM シンボルを生成します。
    /// </summary>
    /// <returns>16QAM 複素シンボル。</returns>
    private Complex GenerateQam16Symbol()
    {
        var real = Qam16PamByBinary[NextBits(2)] * InvSqrt10;
        var imag = Qam16PamByBinary[NextBits(2)] * InvSqrt10;
        return new Complex(real, imag);
    }

    /// <summary>
    /// 乱数から 64QAM シンボルを生成します。
    /// </summary>
    /// <returns>64QAM 複素シンボル。</returns>
    private Complex GenerateQam64Symbol()
    {
        var real = Qam64PamByBinary[NextBits(3)] * InvSqrt42;
        var imag = Qam64PamByBinary[NextBits(3)] * InvSqrt42;
        return new Complex(real, imag);
    }

    /// <summary>
    /// 乱数から 256QAM シンボルを生成します。
    /// </summary>
    /// <returns>256QAM 複素シンボル。</returns>
    private Complex GenerateQam256Symbol()
    {
        var real = Qam256PamByBinary[NextBits(4)] * InvSqrt170;
        var imag = Qam256PamByBinary[NextBits(4)] * InvSqrt170;
        return new Complex(real, imag);
    }

    /// <summary>
    /// 内部乱数から bitCount ビットの整数を生成します。
    /// </summary>
    /// <param name="bitCount">ビット数。</param>
    /// <returns>乱数ビットフィールド。</returns>
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
    /// Gray 符号化 8PSK のビット→位相逆引き表を構築します。
    /// </summary>
    /// <returns>ビット値（0..7）から位相インデックスへの対応表。</returns>
    private static byte[] BuildPsk8PhaseIndexByBits()
    {
        var table = new byte[8];
        for (byte phase = 0; phase < Psk8BitsByPhaseIndex.Length; phase++)
        {
            table[Psk8BitsByPhaseIndex[phase]] = phase;
        }

        return table;
    }

    /// <summary>
    /// 8PSK の位相点（単位円）を生成します。
    /// </summary>
    /// <returns>位相インデックス順の 8PSK 複素シンボル配列。</returns>
    private static Complex[] BuildPsk8Symbols()
    {
        var symbols = new Complex[8];
        for (var i = 0; i < symbols.Length; i++)
        {
            var phase = (2.0 * Math.PI * i) / symbols.Length;
            symbols[i] = new Complex(Math.Cos(phase), Math.Sin(phase));
        }

        return symbols;
    }

    /// <summary>
    /// 時間領域シンボルにサイクリックプレフィックスを付与します。
    /// </summary>
    /// <param name="symbol">CP なし時間領域シンボル。</param>
    /// <param name="cpLength">CP 長。</param>
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

}


