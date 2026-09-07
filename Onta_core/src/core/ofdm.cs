using System.Numerics;

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
    /// ステレオ時に R チャンネルの各サブキャリアを外側へずらすビン数を取得します。
    /// L/R は別データ・各 ActiveSubcarriers 本（合計 2 倍）です。
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
    /// 妥当性検証済みの OFDM 設定を初期化します。
    /// </summary>
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
        int randomSeed = 0)
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

        if (FftSize <= 0 || (FftSize & (FftSize - 1)) != 0)
        {
            throw new ArgumentException("FFT size must be a power of two and > 0.");
        }

        if (ActiveSubcarriers <= 0 || ActiveSubcarriers >= FftSize)
        {
            throw new ArgumentException("Active subcarriers must be > 0 and < FFT size.");
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

        var maxOffset = ActiveSubcarriers / 2;
        if (channelMode == ChannelMode.Stereo && maxOffset + stereoFrequencyShiftBins >= FftSize / 2)
        {
            throw new ArgumentException("Stereo frequency shift is too large for current FFT size and active subcarriers.", nameof(stereoFrequencyShiftBins));
        }
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
    private readonly List<int> _rightAllCarrierBins;
    private readonly List<int> _rightPilotBins;
    private readonly List<int> _rightDataCarrierBase;
    private static readonly int[] Qam16Levels = [-3, -1, 1, 3];
    private static readonly int[] Qam64Levels = [-7, -5, -3, -1, 1, 3, 5, 7];
    private static readonly Complex PilotSymbol = Complex.One;

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

        (_leftAllCarrierBins, _leftPilotBins, _leftDataCarrierBase) =
            BuildChannelLayout(CarrierChannel.Left);
        (_rightAllCarrierBins, _rightPilotBins, _rightDataCarrierBase) =
            BuildChannelLayout(CarrierChannel.Right);

        if (_leftDataCarrierBase.Count != _rightDataCarrierBase.Count)
        {
            throw new InvalidOperationException(
                $"L/R data carrier count mismatch: L={_leftDataCarrierBase.Count}, R={_rightDataCarrierBase.Count}.");
        }
    }

    private (List<int> All, List<int> Pilots, List<int> DataBase) BuildChannelLayout(CarrierChannel channel)
    {
        // 2ch 実信号 WAV 向け: 正周波数側に ActiveSubcarriers 本を配置し、
        // 負周波数は共役対称で埋めて IFFT 結果を実数化する。
        var all = GetPositiveCarrierBins(channel);
        if (all.Count != _config.ActiveSubcarriers)
        {
            throw new InvalidOperationException(
                $"Expected {_config.ActiveSubcarriers} positive carriers, got {all.Count}.");
        }

        var pilots = SelectPilotBins(all, _config.PilotSpacing).OrderBy(x => x).ToList();
        // データ順のベース（未シャッフル）。FrequencyInterleaveIntervalSymbols ごとに並べ替える。
        var data = all.Where(bin => !pilots.Contains(bin)).ToList();
        return (all, pilots, data);
    }

    private int InterleaveIntervalSymbols =>
        Math.Max(1, _config.FrequencyInterleaveIntervalSymbols);

    private int[] ResolveDataCarrierOrder(bool useRightChannel, long absoluteSamplePosition)
    {
        var baseOrder = useRightChannel ? _rightDataCarrierBase : _leftDataCarrierBase;
        if (!_config.EnableFrequencyInterleaving)
        {
            return baseOrder.ToArray();
        }

        var absoluteSymbolPosition = absoluteSamplePosition / SamplesPerOfdmSymbol;
        var epoch = absoluteSymbolPosition / InterleaveIntervalSymbols;
        if (baseOrder.Count <= 1)
        {
            return baseOrder.ToArray();
        }

        var order = baseOrder.ToArray();
        var state = CreateMSequenceState(epoch, useRightChannel);
        var keys = new uint[order.Length];

        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = NextMSequenceWord(ref state);
        }

        Array.Sort(keys, order);
        return order;
    }

    private uint CreateMSequenceState(long epoch, bool useRightChannel)
    {
        // SplitMix で初期状態を拡散し、31bit LFSR のゼロ状態を避ける。
        ulong x = (uint)(_config.RandomSeed == 0 ? 1 : _config.RandomSeed);
        x ^= useRightChannel ? 0xA5A5A5A5u : 0x5A5A5A5Au;
        x ^= unchecked((ulong)epoch * 0x9E3779B97F4A7C15UL);
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        x ^= x >> 31;

        var state = (uint)(x & 0x7FFFFFFF);
        return state == 0 ? 1u : state;
    }

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

    private static uint AdvanceMSequence31(uint state)
    {
        // Primitive polynomial: x^31 + x^28 + 1
        var feedback = ((state >> 30) ^ (state >> 27)) & 1u;
        state = ((state << 1) & 0x7FFFFFFF) | feedback;
        return state == 0 ? 1u : state;
    }

    /// <summary>
    /// 正周波数側のアクティブサブキャリアビンを返します（Hermitian 実 OFDM 用）。
    /// </summary>
    private List<int> GetPositiveCarrierBins(CarrierChannel channel)
    {
        var shift = 0;
        if (_config.ChannelMode == ChannelMode.Stereo && channel == CarrierChannel.Right)
        {
            shift = _config.StereoFrequencyShiftBins;
        }

        // modulation.mdc のグループ規約:
        // SC-9=B, SC-18=A+B, SC-27=A+B+C, SC-36=A+B+C+D。
        var baseBins = ResolveCarrierGroupBins(_config.ActiveSubcarriers);
        var bins = new List<int>(baseBins.Count);
        for (var i = 0; i < baseBins.Count; i++)
        {
            var bin = baseBins[i] + shift;
            if (bin <= 0 || bin >= _config.FftSize / 2)
            {
                throw new InvalidOperationException(
                    $"Carrier bin {bin} is out of positive-frequency range for FFT={_config.FftSize}.");
            }

            bins.Add(bin);
        }

        return bins;
    }

    private static List<int> ResolveCarrierGroupBins(int activeSubcarriers)
    {
        return activeSubcarriers switch
        {
            9 => Enumerable.Range(10, 9).ToList(),
            18 => Enumerable.Range(1, 18).ToList(),
            27 => Enumerable.Range(1, 27).ToList(),
            36 => Enumerable.Range(1, 36).ToList(),
            _ => Enumerable.Range(1, activeSubcarriers).ToList()
        };
    }

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

    private Complex[] ToRealTimeSymbol(Complex[] freqBins)
    {
        ApplyHermitianSymmetry(freqBins);
        var time = InverseFft(freqBins);
        for (var i = 0; i < time.Length; i++)
        {
            time[i] = new Complex(time[i].Real, 0.0);
        }

        return time;
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
    public int BitsPerOfdmSymbol => _leftDataCarrierBase.Count * BitsPerModulationSymbol;

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

        // 出力フレームは、CP 付与後の OFDM シンボルを連結した配列。
        var symbolsWithCp = new List<Complex>(_config.OfdmSymbolCount * (_config.FftSize + _config.CyclicPrefixLength));

        for (var i = 0; i < _config.OfdmSymbolCount; i++)
        {
            var freqBins = BuildFrequencyDomainSymbol(CarrierChannel.Left);
            var timeSymbol = InverseFft(freqBins);
            var withCp = AddCyclicPrefix(timeSymbol, _config.CyclicPrefixLength);
            symbolsWithCp.AddRange(withCp);
        }

        return symbolsWithCp.ToArray();
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

        var frameLength = _config.OfdmSymbolCount * (_config.FftSize + _config.CyclicPrefixLength);
        var left = new List<Complex>(frameLength);
        var right = new List<Complex>(frameLength);

        for (var i = 0; i < _config.OfdmSymbolCount; i++)
        {
            var leftBins = BuildFrequencyDomainSymbol(CarrierChannel.Left);
            var rightBins = BuildFrequencyDomainSymbol(CarrierChannel.Right);

            left.AddRange(AddCyclicPrefix(InverseFft(leftBins), _config.CyclicPrefixLength));
            right.AddRange(AddCyclicPrefix(InverseFft(rightBins), _config.CyclicPrefixLength));
        }

        return (left.ToArray(), right.ToArray());
    }

    /// <summary>
    /// 全アクティブサブキャリアを無変調（固定参照点）で並べた OFDM を、指定サンプル数ぶん生成します。
    /// 全体先頭の同期用プリアンブル、および FH（1 秒）／BH（0.5 秒）先頭の無変調区間に使用します。
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

    private Complex[] GenerateUnmodulatedChannel(int sampleCount, List<int> carrierBins)
    {
        var symbolLength = SamplesPerOfdmSymbol;
        var symbolCount = (sampleCount + symbolLength - 1) / symbolLength;
        var samples = new List<Complex>(symbolCount * symbolLength);

        for (var i = 0; i < symbolCount; i++)
        {
            var freqBins = new Complex[_config.FftSize];
            foreach (var carrierBin in carrierBins)
            {
                freqBins[carrierBin] = UnmodulatedCarrierSymbol;
            }

            var timeSymbol = ToRealTimeSymbol(freqBins);
            samples.AddRange(AddCyclicPrefix(timeSymbol, _config.CyclicPrefixLength));
        }

        if (samples.Count > sampleCount)
        {
            return samples.GetRange(0, sampleCount).ToArray();
        }

        return samples.ToArray();
    }

    /// <summary>
    /// ペイロードビット列を OFDM 変調し、巡回プレフィックス付き時間領域サンプルを返します。
    /// ステレオ時は同一ビット列を L/R それぞれ異なるキャリア配置・インターリーブで送信します。
    /// </summary>
    /// <param name="bits">変調するビット列。</param>
    /// <param name="absoluteSampleOffset">WAV 全体先頭からのサンプル位置（インターリーブ epoch 算出用）。</param>
    public (Complex[] Left, Complex[] Right) ModulateBits(ReadOnlySpan<bool> bits, long absoluteSampleOffset = 0)
    {
        return ModulateBitStreams(bits, bits, absoluteSampleOffset);
    }

    /// <summary>
    /// L/R に異なるビット列を載せて OFDM 変調します（ステレオヘッダー並び用）。
    /// 両ストリームの生成サンプル長は一致する必要があります。
    /// </summary>
    /// <param name="leftBits">L チャンネルビット列。</param>
    /// <param name="rightBits">R チャンネルビット列。</param>
    /// <param name="absoluteSampleOffset">WAV 全体先頭からのサンプル位置（インターリーブ epoch 算出用）。</param>
    public (Complex[] Left, Complex[] Right) ModulateBitStreams(
        ReadOnlySpan<bool> leftBits,
        ReadOnlySpan<bool> rightBits,
        long absoluteSampleOffset = 0)
    {
        if (BitsPerOfdmSymbol <= 0)
        {
            throw new InvalidOperationException("No data carriers available for modulation.");
        }

        var left = ModulateBitsOnChannel(leftBits, useRightChannel: false, absoluteSampleOffset);
        if (_config.ChannelMode == ChannelMode.Mono)
        {
            return (left, Array.Empty<Complex>());
        }

        var right = ModulateBitsOnChannel(rightBits, useRightChannel: true, absoluteSampleOffset);
        if (left.Length != right.Length)
        {
            throw new InvalidOperationException(
                $"L/R modulated length mismatch: L={left.Length}, R={right.Length}.");
        }

        return (left, right);
    }

    private Complex[] ModulateBitsOnChannel(
        ReadOnlySpan<bool> bits,
        bool useRightChannel,
        long absoluteSampleOffset)
    {
        var pilotBins = useRightChannel ? _rightPilotBins : _leftPilotBins;
        var symbolCount = (bits.Length + BitsPerOfdmSymbol - 1) / BitsPerOfdmSymbol;
        if (symbolCount == 0)
        {
            symbolCount = 1;
        }

        var samples = new List<Complex>(symbolCount * SamplesPerOfdmSymbol);
        var bitIndex = 0;

        for (var s = 0; s < symbolCount; s++)
        {
            var symbolOffset = absoluteSampleOffset + ((long)s * SamplesPerOfdmSymbol);
            var dataCarrierOrder = ResolveDataCarrierOrder(useRightChannel, symbolOffset);

            var freqBins = new Complex[_config.FftSize];
            foreach (var pilotBin in pilotBins)
            {
                freqBins[pilotBin] = PilotSymbol;
            }

            foreach (var dataBin in dataCarrierOrder)
            {
                freqBins[dataBin] = ConsumeModulatedSymbol(ref bitIndex, bits);
            }

            var timeSymbol = ToRealTimeSymbol(freqBins);
            samples.AddRange(AddCyclicPrefix(timeSymbol, _config.CyclicPrefixLength));
        }

        return samples.ToArray();
    }

    /// <summary>
    /// 指定ビット数を載せるのに必要な時間領域サンプル数です。
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
    /// 診断用: 指定ワウパラメータでのプリアンブル区間スコアを返します。
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
        var corrected = ResampleSegmentWithInverseSpeed(
            samples,
            analysisStartSample,
            analysisSampleCount,
            Math.Max(1, _config.SampleRate),
            amount,
            wowPhase,
            flutterPhase);
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

        var baseline = ScoreWowParamsForDiagnostics(
            samples,
            useRightChannel,
            analysisStartSample,
            analysisSampleCount,
            hintAmount,
            hintWowPhase,
            hintFlutterPhase);

        var best = baseline;
        var bestAmount = hintAmount;
        var bestWow = hintWowPhase;
        var bestFlutter = hintFlutterPhase;

        var phaseStep = Math.Max(0.01, phaseRangeRad / 6.0);
        var amountStep = Math.Max(0.0005, amountRange / 2.0);

        for (var wow = hintWowPhase - phaseRangeRad; wow <= hintWowPhase + phaseRangeRad; wow += phaseStep)
        {
            for (var flutter = hintFlutterPhase - phaseRangeRad; flutter <= hintFlutterPhase + phaseRangeRad; flutter += phaseStep)
            {
                for (var amount = Math.Max(0.001, hintAmount - amountRange); amount <= hintAmount + amountRange; amount += amountStep)
                {
                    var score = ScoreWowParamsForDiagnostics(
                        samples,
                        useRightChannel,
                        analysisStartSample,
                        analysisSampleCount,
                        amount,
                        wow,
                        flutter);
                    if (score > best)
                    {
                        best = score;
                        bestAmount = amount;
                        bestWow = wow;
                        bestFlutter = flutter;
                    }
                }
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

        // 既知プリアンブルとの相関最大化でワウパラメータを同定する。
        var matched = MatchWowByPreambleCorrelation(
            samples, useRightChannel, analysisStartSample, analysisSampleCount);
        if (matched is null)
        {
            return samples;
        }

        var current = samples;
        for (var pass = 0; pass < passes; pass++)
        {
            var speedProfile = pass == 0
                ? matched
                : MatchWowByPreambleCorrelation(
                    current, useRightChannel, analysisStartSample, analysisSampleCount)
                    ?? matched;
            // matched はすでにファイル全長のシンボル速度。
            current = ResampleWithInverseSpeed(current, (double[])speedProfile.Clone());
        }

        return current;
    }

    /// <summary>
    /// 既知のワウ・フラッターパラメータで可逆写像の逆補正を行います。
    /// </summary>
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
    /// 既知の無変調プリアンブルとの相関が最大になるワウ・フラッターを探索します。
    /// NoisePlus と同じ合成式: speed = 1 + A*(0.65*sin(wow)+0.35*sin(flutter))。
    /// </summary>
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

        double Evaluate(double amount, double wowPhase, double flutterPhase, int corrStride = 1)
        {
            var correctedPreamble = ResampleSegmentWithInverseSpeed(
                samples,
                analysisStartSample,
                analysisSampleCount,
                sampleRate,
                amount,
                wowPhase,
                flutterPhase,
                corrStride);
            var score = CorrelateRealStrided(correctedPreamble, ideal, corrStride);
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

        // 尖った相関面向け: 粗格子でシード → 座標降下で wow/flutter を交互に精密化。
        const int coarseStride = 32;
        var coarseHits = new List<(double Score, double Wow, double Flutter)>(64);
        for (var wi = 0; wi < 36; wi++)
        {
            var wowPhase = wi * Math.PI / 18.0;
            for (var fi = 0; fi < 36; fi++)
            {
                var flutterPhase = fi * Math.PI / 18.0;
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
        var seeds = new List<(double Wow, double Flutter)>(6);
        for (var i = 0; i < coarseHits.Count && seeds.Count < 5; i++)
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
            // 座標降下: flutter → wow → flutter → wow
            for (var pass = 0; pass < 2; pass++)
            {
                var bestLocal = double.NegativeInfinity;
                var bestFlutter = flutter;
                for (var fi = 0; fi < 360; fi++)
                {
                    var trial = fi * Math.PI / 180.0;
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
                for (var wi = 0; wi < 360; wi++)
                {
                    var trial = wi * Math.PI / 180.0;
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

            // 局所の超精密研磨（幅 ±0.03 rad 程度）
            for (var dW = -8; dW <= 8; dW++)
            {
                for (var dF = -8; dF <= 8; dF++)
                {
                    Evaluate(
                        0.01,
                        wow + (dW * Math.PI / 1200.0),
                        flutter + (dF * Math.PI / 1200.0),
                        corrStride: 1);
                }
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

    private static double CorrelateReal(Complex[] a, Complex[] b)
    {
        return CorrelateRealStrided(a, b, 1);
    }

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
    public Complex[] ResampleWithInverseSpeedForDiagnostics(Complex[] samples, double[] speedProfile) =>
        ResampleWithInverseSpeed(samples, speedProfile);

    /// <summary>
    /// 診断用: 無変調プリアンブル理想波形との相関を返します。
    /// </summary>
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

    private static double ScoreCpCorrelation(ReadOnlySpan<Complex> window, int offset, int fftSize, int cp)
    {
        var score = 0.0;
        for (var i = 0; i < cp; i++)
        {
            var a = window[offset + i].Real;
            var b = window[offset + fftSize + i].Real;
            score += a * b;
        }

        return score;
    }

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

    private static double ComplexMagnitude(Complex value)
    {
        return Math.Sqrt((value.Real * value.Real) + (value.Imaginary * value.Imaginary));
    }

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
        if (segmentLength <= 0)
        {
            return [];
        }

        stride = Math.Max(1, stride);
        var n = samples.Length;
        if (n <= 1)
        {
            return new Complex[segmentLength];
        }

        var cumulEnd = CassetteCumulAt(n - 1, sampleRate, amount, wowPhase, flutterPhase);
        var scale = cumulEnd > 1e-12 ? (n - 1) / cumulEnd : 1.0;
        var output = new Complex[segmentLength];
        for (var j = 0; j < segmentLength; j += stride)
        {
            var outputIndex = segmentStart + j;
            var target = outputIndex / scale;
            var i = FindCassetteCumulIndex(target, n, sampleRate, amount, wowPhase, flutterPhase);
            if (i >= n - 1)
            {
                output[j] = samples[^1];
                continue;
            }

            var c0 = CassetteCumulAt(i, sampleRate, amount, wowPhase, flutterPhase);
            var c1 = CassetteCumulAt(i + 1, sampleRate, amount, wowPhase, flutterPhase);
            var span = c1 - c0;
            var frac = span > 1e-12 ? (target - c0) / span : 0.0;
            frac = Math.Clamp(frac, 0.0, 1.0);
            var a = samples[i].Real;
            var b = samples[i + 1].Real;
            output[j] = new Complex(a + ((b - a) * frac), 0.0);
        }

        return output;
    }

    /// <summary>
    /// NoisePlus と同じ速度モデルの累積 sum_{k=0}^{i-1} speed[k] を閉形式で返します。
    /// </summary>
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
        return i
            + (amount * 0.65 * SumOfSines(wowPhase, wowOmega, i))
            + (amount * 0.35 * SumOfSines(flutterPhase, flutterOmega, i));
    }

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
        var score = 0.0;
        for (var s = 0; s < symbolCount; s++)
        {
            var symbolStart = start + (s * symbolLength);
            score += ScoreSingleSymbolLock(samples.AsSpan(symbolStart, symbolLength), pilotBins);
        }

        return score / symbolCount;
    }

    /// <summary>
    /// CP 相関とパイロット品質が最大になるシンボル開始位置を、期待位置近傍から探します。
    /// 期待位置とのスコア差が小さい場合は期待位置を維持し、クリーン信号の誤ロックを防ぎます。
    /// </summary>
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
        var back = Math.Min(searchRadius, expectedStart);
        var forward = Math.Min(searchRadius, samples.Length - symbolLength - expectedStart);
        var scoreAtExpected = ScoreSingleSymbolLock(samples.AsSpan(expectedStart, symbolLength), pilotBins);
        var bestDelta = 0;
        var bestScore = scoreAtExpected;

        for (var delta = -back; delta <= forward; delta++)
        {
            if (delta == 0)
            {
                continue;
            }

            var score = ScoreSingleSymbolLock(samples.AsSpan(expectedStart + delta, symbolLength), pilotBins);
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

    private double ScoreSingleSymbolLock(ReadOnlySpan<Complex> symbolWithCp, List<int> pilotBins)
    {
        var fftSize = _config.FftSize;
        var cp = _config.CyclicPrefixLength;
        if (symbolWithCp.Length < fftSize + cp || cp <= 0)
        {
            return double.NegativeInfinity;
        }

        var cpScore = 0.0;
        var energy = 0.0;
        for (var i = 0; i < cp; i++)
        {
            var a = symbolWithCp[i].Real;
            var b = symbolWithCp[fftSize + i].Real;
            cpScore += a * b;
            energy += (a * a) + (b * b);
        }

        if (energy < 1e-12)
        {
            return double.NegativeInfinity;
        }

        // 正規化 CP 相関（-1..1 程度）
        var normalizedCp = cpScore / (energy * 0.5);

        var time = RemoveCyclicPrefix(symbolWithCp, cp);
        var freqBins = ForwardFftMatchingInverse(time);
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
    public bool[] DemodulateBitsFromStream(
        Complex[] samples,
        ref int cursor,
        int bitCount,
        bool useRightChannel,
        long logicalSampleOffset,
        int searchRadius = 16)
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
        var symbolLength = SamplesPerOfdmSymbol;
        var symbolCount = bitCount == 0
            ? 1
            : (bitCount + BitsPerOfdmSymbol - 1) / BitsPerOfdmSymbol;

        var bits = new bool[bitCount];
        var bitIndex = 0;
        var logical = logicalSampleOffset;
        var agcState = new PilotGroupAgcState(pilotBins.Count);
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
            var time = RemoveCyclicPrefix(symbol, _config.CyclicPrefixLength);
            var freqBins = ForwardFftMatchingInverse(time);
            var equalizers = EstimatePilotEqualizers(freqBins, pilotBins, useRightChannel, agcState);
            var dataOrder = ResolveDataCarrierOrder(useRightChannel, logical);

            foreach (var dataBin in dataOrder)
            {
                if (bitIndex >= bitCount)
                {
                    break;
                }

                EmitSymbolBits(freqBins[dataBin] * equalizers[dataBin], ref bitIndex, bits);
            }

            position = start + symbolLength;
            logical += symbolLength;
        }

        cursor = position;
        return bits;
    }

    /// <summary>
    /// ストリーム上で CP/パイロット同期しながらソフト LLR を取り出します。
    /// </summary>
    public double[] DemodulateSoftLlrsFromStream(
        Complex[] samples,
        ref int cursor,
        int bitCount,
        bool useRightChannel,
        long logicalSampleOffset,
        int searchRadius = 16,
        double noiseVariance = 0.05)
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
            estimateNoiseFromPilots: false);
    }

    /// <summary>
    /// ステレオ時に L/R 同一ペイロードのソフト LLR を加算合成します。
    /// タイミングは L のパイロット同期に合わせ、R も同じシンボル境界で復調します。
    /// </summary>
    public double[] DemodulateSoftLlrsStereoCombined(
        Complex[] leftSamples,
        Complex[] rightSamples,
        ref int cursor,
        int bitCount,
        long logicalSampleOffset,
        int searchRadius = 16,
        double noiseVariance = 0.05,
        bool estimateNoiseFromPilots = true)
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
                estimateNoiseFromPilots);
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
            estimateNoiseFromPilots);
    }

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
        bool estimateNoiseFromPilots)
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
        var logical = logicalSampleOffset;
        var primaryAgcState = new PilotGroupAgcState(pilotBins.Count);
        var secondaryAgcState = new PilotGroupAgcState(secondaryPilotBins.Count);
        var followRadius = Math.Max(searchRadius, 2);
        var position = cursor;
        var noiseAccum = 0.0;
        var noiseCount = 0;

        // 1 パス目: パイロット残差から雑音分散を推定（任意）。
        var effectiveVariance = Math.Max(1e-6, noiseVariance);
        if (estimateNoiseFromPilots)
        {
            var probePosition = position;
            for (var s = 0; s < symbolCount; s++)
            {
                var start = FindBestSymbolStart(samples, probePosition, followRadius, useRightChannel);
                if (start + symbolLength > samples.Length)
                {
                    break;
                }

                AccumulatePilotNoise(
                    samples.AsSpan(start, symbolLength),
                    pilotBins,
                    useRightChannel,
                    ref noiseAccum,
                    ref noiseCount);
                if (secondarySamples is not null && start + symbolLength <= secondarySamples.Length)
                {
                    AccumulatePilotNoise(
                        secondarySamples.AsSpan(start, symbolLength),
                        secondaryPilotBins,
                        secondaryUseRightChannel,
                        ref noiseAccum,
                        ref noiseCount);
                }

                probePosition = start + symbolLength;
            }

            if (noiseCount > 0)
            {
                // 等化後パイロットの I/Q 分散。下限・上限で LLR スケールを安定化。
                effectiveVariance = Math.Clamp(noiseAccum / noiseCount, 1e-4, 0.5);
            }
        }

        for (var s = 0; s < symbolCount && bitIndex < bitCount; s++)
        {
            var start = FindBestSymbolStart(samples, position, followRadius, useRightChannel);
            if (start + symbolLength > samples.Length)
            {
                throw new InvalidDataException("WAV ended while synchronizing OFDM symbol.");
            }

            var symbolBitStart = bitIndex;
            EmitSymbolSoftLlrsForChannel(
                samples.AsSpan(start, symbolLength),
                pilotBins,
                useRightChannel,
                primaryAgcState,
                logical,
                ref bitIndex,
                llrs,
                effectiveVariance,
                addToExisting: false);

            if (secondarySamples is not null)
            {
                if (start + symbolLength > secondarySamples.Length)
                {
                    throw new InvalidDataException("Secondary WAV ended while synchronizing OFDM symbol.");
                }

                var secondaryBitIndex = symbolBitStart;
                EmitSymbolSoftLlrsForChannel(
                    secondarySamples.AsSpan(start, symbolLength),
                    secondaryPilotBins,
                    secondaryUseRightChannel,
                    secondaryAgcState,
                    logical,
                    ref secondaryBitIndex,
                    llrs,
                    effectiveVariance,
                    addToExisting: true);
            }

            position = start + symbolLength;
            logical += symbolLength;
        }

        cursor = position;
        return llrs;
    }

    private void EmitSymbolSoftLlrsForChannel(
        ReadOnlySpan<Complex> symbolWithCp,
        List<int> pilotBins,
        bool useRightChannel,
        PilotGroupAgcState agcState,
        long logical,
        ref int bitIndex,
        double[] llrs,
        double noiseVariance,
        bool addToExisting)
    {
        var time = RemoveCyclicPrefix(symbolWithCp, _config.CyclicPrefixLength);
        var freqBins = ForwardFftMatchingInverse(time);
        var equalizers = EstimatePilotEqualizers(freqBins, pilotBins, useRightChannel, agcState);
        var dataOrder = ResolveDataCarrierOrder(useRightChannel, logical);
        foreach (var dataBin in dataOrder)
        {
            if (bitIndex >= llrs.Length)
            {
                break;
            }

            if (addToExisting)
            {
                var tmp = new double[BitsPerModulationSymbol];
                var tmpIndex = 0;
                EmitSymbolSoftLlrs(freqBins[dataBin] * equalizers[dataBin], ref tmpIndex, tmp, noiseVariance);
                for (var i = 0; i < tmpIndex && (bitIndex + i) < llrs.Length; i++)
                {
                    llrs[bitIndex + i] += tmp[i];
                }

                bitIndex += tmpIndex;
            }
            else
            {
                EmitSymbolSoftLlrs(freqBins[dataBin] * equalizers[dataBin], ref bitIndex, llrs, noiseVariance);
            }
        }
    }

    private void AccumulatePilotNoise(
        ReadOnlySpan<Complex> symbolWithCp,
        List<int> pilotBins,
        bool useRightChannel,
        ref double noiseAccum,
        ref int noiseCount)
    {
        var time = RemoveCyclicPrefix(symbolWithCp, _config.CyclicPrefixLength);
        var freqBins = ForwardFftMatchingInverse(time);
        var equalizers = EstimatePilotEqualizers(freqBins, pilotBins, useRightChannel);
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
    public bool[] DemodulateBits(
        ReadOnlySpan<Complex> samples,
        int bitCount,
        bool useRightChannel = false,
        long absoluteSampleOffset = 0)
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

        var symbolLength = SamplesPerOfdmSymbol;
        if (samples.Length % symbolLength != 0)
        {
            throw new ArgumentException("Sample length must be a multiple of OFDM symbol length.", nameof(samples));
        }

        var bits = new bool[bitCount];
        var bitIndex = 0;
        var symbolCount = samples.Length / symbolLength;
        var agcState = new PilotGroupAgcState(pilotBins.Count);

        for (var s = 0; s < symbolCount && bitIndex < bitCount; s++)
        {
            var symbolOffset = absoluteSampleOffset + ((long)s * symbolLength);
            var dataOrder = ResolveDataCarrierOrder(useRightChannel, symbolOffset);

            var symbol = samples.Slice(s * symbolLength, symbolLength);
            var time = RemoveCyclicPrefix(symbol, _config.CyclicPrefixLength);
            var freqBins = ForwardFftMatchingInverse(time);
            var equalizers = EstimatePilotEqualizers(freqBins, pilotBins, useRightChannel, agcState);

            foreach (var dataBin in dataOrder)
            {
                if (bitIndex >= bitCount)
                {
                    break;
                }

                EmitSymbolBits(freqBins[dataBin] * equalizers[dataBin], ref bitIndex, bits);
            }
        }

        return bits;
    }

    /// <summary>
    /// パイロット配置グループごとに等化係数を推定します。
    /// 同一グループのキャリアへ同一係数を適用し、シンボルごとにAGC状態を更新します。
    /// </summary>
    private Complex[] EstimatePilotEqualizers(
        Complex[] freqBins,
        List<int> pilotBins,
        bool useRightChannel,
        PilotGroupAgcState? agcState = null)
    {
        var equalizers = new Complex[freqBins.Length];
        Array.Fill(equalizers, Complex.One);
        if (pilotBins.Count == 0)
        {
            return equalizers;
        }

        var orderedPilots = pilotBins.OrderBy(b => b).ToList();
        var channels = new Complex[orderedPilots.Count];
        for (var i = 0; i < orderedPilots.Count; i++)
        {
            channels[i] = freqBins[orderedPilots[i]];
        }

        var allCarriers = useRightChannel ? _rightAllCarrierBins : _leftAllCarrierBins;
        var noisePower = EstimateNoisePower(freqBins, orderedPilots, allCarriers);
        var regularization = Math.Max(noisePower * 0.10, 1e-4);

        var groupedCarriers = new List<int>[orderedPilots.Count];
        for (var i = 0; i < groupedCarriers.Length; i++)
        {
            groupedCarriers[i] = new List<int>();
        }

        foreach (var carrier in allCarriers)
        {
            var groupIndex = ResolvePilotGroupIndex(carrier, orderedPilots);
            groupedCarriers[groupIndex].Add(carrier);
        }

        for (var i = 0; i < orderedPilots.Count; i++)
        {
            var h = channels[i];
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

        return equalizers;
    }

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

        public PilotGroupAgcState(int groupCount)
        {
            var size = Math.Max(1, groupCount);
            _smoothed = new Complex[size];
            _initialized = new bool[size];
        }

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

    private void EmitSymbolBits(Complex symbol, ref int bitIndex, bool[] bits)
    {
        switch (_config.ModulationScheme)
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

    private void EmitSymbolSoftLlrs(Complex symbol, ref int bitIndex, double[] llrs, double noiseVariance)
    {
        var invVar = 1.0 / Math.Max(1e-6, noiseVariance);
        switch (_config.ModulationScheme)
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
                EmitPamAxisSoftLlrs(symbol.Real * Math.Sqrt(10.0), bitsPerAxis: 2, Qam16Levels, invVar, ref bitIndex, llrs);
                EmitPamAxisSoftLlrs(symbol.Imaginary * Math.Sqrt(10.0), bitsPerAxis: 2, Qam16Levels, invVar, ref bitIndex, llrs);
                break;
            case ModulationScheme.Qam64:
                EmitPamAxisSoftLlrs(symbol.Real * Math.Sqrt(42.0), bitsPerAxis: 3, Qam64Levels, invVar, ref bitIndex, llrs);
                EmitPamAxisSoftLlrs(symbol.Imaginary * Math.Sqrt(42.0), bitsPerAxis: 3, Qam64Levels, invVar, ref bitIndex, llrs);
                break;
            default:
                throw new NotSupportedException("Soft demodulation for this scheme is not implemented yet.");
        }
    }

    private static void EmitPamAxisSoftLlrs(
        double amplitude,
        int bitsPerAxis,
        int[] levels,
        double invVariance,
        ref int bitIndex,
        double[] llrs)
    {
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

    private static void WriteLlr(ref int bitIndex, double[] llrs, double value)
    {
        if (bitIndex >= llrs.Length)
        {
            return;
        }

        llrs[bitIndex++] = value;
    }

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

    private static int GrayToBinary(int gray)
    {
        var binary = gray;
        for (var shift = gray >> 1; shift != 0; shift >>= 1)
        {
            binary ^= shift;
        }

        return binary;
    }

    private static void WriteBit(ref int bitIndex, bool[] bits, bool value)
    {
        if (bitIndex >= bits.Length)
        {
            return;
        }

        bits[bitIndex++] = value;
    }

    private static Complex[] RemoveCyclicPrefix(ReadOnlySpan<Complex> withCp, int cpLength)
    {
        var result = new Complex[withCp.Length - cpLength];
        withCp.Slice(cpLength).CopyTo(result);
        return result;
    }

    /// <summary>
    /// <see cref="InverseFft"/> の逆変換です。time = InverseFft(freq) のとき freq を復元します。
    /// </summary>
    private static Complex[] ForwardFftMatchingInverse(Complex[] time)
    {
        var n = time.Length;
        var input = new Complex[n];
        for (var i = 0; i < n; i++)
        {
            input[i] = Complex.Conjugate(time[i] * n);
        }

        var u = InverseFft(input);
        var freq = new Complex[n];
        for (var i = 0; i < n; i++)
        {
            freq[i] = Complex.Conjugate(u[i]);
        }

        return freq;
    }

    private Complex[] BuildFrequencyDomainSymbol(CarrierChannel channel)
    {
        var bins = new Complex[_config.FftSize];
        var carrierBins = GetActiveCarrierBins(channel);
        var pilotBins = SelectPilotBins(carrierBins, _config.PilotSpacing);
        var dataBins = carrierBins.Where(bin => !pilotBins.Contains(bin)).ToList();

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
            bins[dataBin] = GenerateModulatedSymbol();
        }

        return bins;
    }

    private Complex ConsumeModulatedSymbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        return _config.ModulationScheme switch
        {
            ModulationScheme.Bpsk => ConsumeBpskSymbol(ref bitIndex, bits),
            ModulationScheme.Qpsk => ConsumeQpskSymbol(ref bitIndex, bits),
            ModulationScheme.Qam16 => ConsumeQam16Symbol(ref bitIndex, bits),
            ModulationScheme.Qam64 => ConsumeQam64Symbol(ref bitIndex, bits),
            _ => throw new InvalidOperationException("Unsupported modulation scheme.")
        };
    }

    private static bool ReadBitOrZero(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        if (bitIndex >= bits.Length)
        {
            bitIndex++;
            return false;
        }

        return bits[bitIndex++];
    }

    private static int ReadBitField(ref int bitIndex, ReadOnlySpan<bool> bits, int bitCount)
    {
        var value = 0;
        for (var i = 0; i < bitCount; i++)
        {
            value = (value << 1) | (ReadBitOrZero(ref bitIndex, bits) ? 1 : 0);
        }

        return value;
    }

    private Complex ConsumeBpskSymbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var bit = ReadBitOrZero(ref bitIndex, bits);
        // bit1 → +1、bit0 → -1（単位エネルギー、Imag=0）
        return new Complex(bit ? 1.0 : -1.0, 0.0);
    }

    private Complex ConsumeQpskSymbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var iBit = ReadBitOrZero(ref bitIndex, bits);
        var qBit = ReadBitOrZero(ref bitIndex, bits);
        var real = iBit ? 1.0 : -1.0;
        var imag = qBit ? 1.0 : -1.0;
        return new Complex(real, imag) / Math.Sqrt(2.0);
    }

    private Complex ConsumeQam16Symbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var real = GrayMappedPamLevel(ReadBitField(ref bitIndex, bits, 2), bitsPerAxis: 2, Qam16Levels);
        var imag = GrayMappedPamLevel(ReadBitField(ref bitIndex, bits, 2), bitsPerAxis: 2, Qam16Levels);
        return new Complex(real, imag) / Math.Sqrt(10.0);
    }

    private Complex ConsumeQam64Symbol(ref int bitIndex, ReadOnlySpan<bool> bits)
    {
        var real = GrayMappedPamLevel(ReadBitField(ref bitIndex, bits, 3), bitsPerAxis: 3, Qam64Levels);
        var imag = GrayMappedPamLevel(ReadBitField(ref bitIndex, bits, 3), bitsPerAxis: 3, Qam64Levels);
        return new Complex(real, imag) / Math.Sqrt(42.0);
    }

    private static int GrayMappedPamLevel(int binaryIndex, int bitsPerAxis, int[] levels)
    {
        var grayIndex = (binaryIndex ^ (binaryIndex >> 1)) & ((1 << bitsPerAxis) - 1);
        return levels[grayIndex];
    }

    private List<int> GetActiveCarrierBins(CarrierChannel channel)
    {
        var half = _config.ActiveSubcarriers / 2;
        var offsets = new List<int>(_config.ActiveSubcarriers);

        for (var k = half; k >= 1; k--)
        {
            offsets.Add(-k);
        }

        for (var k = 1; k <= half; k++)
        {
            offsets.Add(k);
        }

        if (_config.ActiveSubcarriers % 2 != 0)
        {
            offsets.Add(half + 1);
        }

        if (_config.ChannelMode == ChannelMode.Stereo && channel == CarrierChannel.Right)
        {
            var shift = _config.StereoFrequencyShiftBins;
            for (var i = 0; i < offsets.Count; i++)
            {
                offsets[i] += offsets[i] < 0 ? -shift : shift;
            }
        }

        var bins = new List<int>(offsets.Count);
        foreach (var offset in offsets)
        {
            if (offset == 0)
            {
                continue;
            }

            var bin = offset < 0 ? _config.FftSize + offset : offset;
            if (bin <= 0 || bin >= _config.FftSize)
            {
                continue;
            }

            bins.Add(bin);
        }

        return bins;
    }

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

    private void ShuffleInPlace(List<int> values) => ShuffleInPlace(values, _random);

    private static void ShuffleInPlace(List<int> values, Random random)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    private static void ShuffleInPlace(int[] values, Random random)
    {
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    private Complex GenerateModulatedSymbol()
    {
        return _config.ModulationScheme switch
        {
            ModulationScheme.Bpsk => GenerateBpskSymbol(),
            ModulationScheme.Qpsk => GenerateQpskSymbol(),
            ModulationScheme.Qam16 => GenerateQam16Symbol(),
            ModulationScheme.Qam64 => GenerateQam64Symbol(),
            _ => throw new InvalidOperationException("Unsupported modulation scheme.")
        };
    }

    private Complex GenerateBpskSymbol()
    {
        var bit = _random.Next(2);
        return new Complex(bit == 0 ? -1.0 : 1.0, 0.0);
    }

    private Complex GenerateQpskSymbol()
    {
        // 1bit/軸の Gray マッピング（00,01,11,10 の隣接で 1bit 差）を適用する。
        var iBit = _random.Next(2);
        var qBit = _random.Next(2);

        var real = iBit == 0 ? -1.0 : 1.0;
        var imag = qBit == 0 ? -1.0 : 1.0;
        return new Complex(real, imag) / Math.Sqrt(2.0);
    }

    private Complex GenerateQam16Symbol()
    {
        // 2bit/軸を Gray マッピングして 4-PAM レベルへ変換する。
        var real = GenerateGrayMappedPamLevel(bitsPerAxis: 2, Qam16Levels);
        var imag = GenerateGrayMappedPamLevel(bitsPerAxis: 2, Qam16Levels);
        return new Complex(real, imag) / Math.Sqrt(10.0);
    }

    private Complex GenerateQam64Symbol()
    {
        // 3bit/軸を Gray マッピングして 8-PAM レベルへ変換する。
        var real = GenerateGrayMappedPamLevel(bitsPerAxis: 3, Qam64Levels);
        var imag = GenerateGrayMappedPamLevel(bitsPerAxis: 3, Qam64Levels);
        return new Complex(real, imag) / Math.Sqrt(42.0);
    }

    private int GenerateGrayMappedPamLevel(int bitsPerAxis, int[] levels)
    {
        var binaryIndex = NextBits(bitsPerAxis);
        // binary -> Gray（value ^ (value >> 1)）。gray_code.cs には依存しない。
        var grayIndex = (binaryIndex ^ (binaryIndex >> 1)) & ((1 << bitsPerAxis) - 1);
        return levels[grayIndex];
    }

    private int NextBits(int bitCount)
    {
        var value = 0;
        for (var i = 0; i < bitCount; i++)
        {
            value = (value << 1) | _random.Next(2);
        }

        return value;
    }

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

    private static Complex[] InverseFft(Complex[] frequency)
    {
        // 共役を用いた IFFT: IFFT(x) = conj( FFT(conj(x)) ) / N。
        var conjugated = frequency.Select(Complex.Conjugate).ToArray();
        var fft = Fft(conjugated);
        var n = frequency.Length;
        var scale = 1.0 / n;

        for (var i = 0; i < n; i++)
        {
            fft[i] = Complex.Conjugate(fft[i]) * scale;
        }

        return fft;
    }

    private static Complex[] Fft(Complex[] input)
    {
        var n = input.Length;
        var output = new Complex[n];
        Array.Copy(input, output, n);

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

            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                var halfLen = len >> 1;
                for (var j = 0; j < halfLen; j++)
                {
                    var u = output[i + j];
                    var v = output[i + j + halfLen] * w;
                    output[i + j] = u + v;
                    output[i + j + halfLen] = u - v;
                    w *= wLen;
                }
            }
        }

        return output;
    }

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
