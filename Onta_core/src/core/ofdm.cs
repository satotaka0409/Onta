using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace Otofa.Core;

/// <summary>
/// OFDM フレームを生成し、複素サンプルを CSV に出力する CLI エントリポイントです。
/// 変調方式 QPSK 16QAM 64QAM の3通りに対応しています。
/// モノラル / ステレオの切り替えが可能です。(パラメータで指定)
/// サブキャリア周波数は、ステレオの L R でずらして配置します。モノラルはLチャンネルを使う。
/// 周波数インターリーブを行います。(サブキャリアの順序をランダムに入れ替えます)
/// 9サブキャリアごとに1つのパイロットキャリアを配置します。（パイロットは9サブキャリアの周波数中央に固定で配置します）
/// 9/18/27/36 のサブキャリア本数のいずれかを使用します。
/// パイロットは、ワウフラッターや周波数ドリフトの影響を補償するために使用されます。
/// </summary>
public static class OfdmProgram
{
    /// <summary>
    /// OFDM フレームを 1 つ生成し、CSV ファイルへ保存します。
    /// </summary>
    /// <param name="args">省略可能な引数です。先頭要素は出力 CSV パスとして扱われます。</param>
    public static void Main(string[] args)
    {
        // パラメータ: FFT サイズ、使用サブキャリア数、巡回プレフィックス長、OFDM シンボル数。
        var config = new OfdmConfig(
            fftSize: 64,
            activeSubcarriers: 52,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: 10,
            modulationScheme: ModulationScheme.Qpsk,
            randomSeed: 42);

        var generator = new OfdmGenerator(config);
        var timeDomainSamples = generator.GenerateFrame();

        Console.WriteLine("OFDM frame generated.");
        Console.WriteLine($"FFT Size            : {config.FftSize}");
        Console.WriteLine($"Active Subcarriers  : {config.ActiveSubcarriers}");
        Console.WriteLine($"Cyclic Prefix Length: {config.CyclicPrefixLength}");
        Console.WriteLine($"OFDM Symbol Count   : {config.OfdmSymbolCount}");
        Console.WriteLine($"Modulation Scheme   : {config.ModulationScheme}");
        Console.WriteLine($"Total Samples       : {timeDomainSamples.Length}");

        var outputPath = args.Length > 0 ? args[0] : "ofdm_output.csv";
        SaveSamplesAsCsv(outputPath, timeDomainSamples);
        Console.WriteLine($"Saved: {Path.GetFullPath(outputPath)}");
    }

    private static void SaveSamplesAsCsv(string path, Complex[] samples)
    {
        using var writer = new StreamWriter(path, false);
        writer.WriteLine("index,real,imag,magnitude");

        // 後段処理しやすいよう、カルチャ非依存かつ再現性のある数値書式で保存する。
        for (var i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            writer.WriteLine(string.Join(",",
                i.ToString(CultureInfo.InvariantCulture),
                s.Real.ToString("G17", CultureInfo.InvariantCulture),
                s.Imaginary.ToString("G17", CultureInfo.InvariantCulture),
                s.Magnitude.ToString("G17", CultureInfo.InvariantCulture)));
        }
    }
}

/// <summary>
/// OFDM サブキャリアに割り当てる変調方式です。
/// </summary>
public enum ModulationScheme : byte
{
    /// <summary>
    /// QPSK。
    /// </summary>
    Qpsk = 0,

    /// <summary>
    /// 16QAM。
    /// </summary>
    Qam16 = 1,

    /// <summary>
    /// 64QAM。
    /// </summary>
    Qam64 = 2
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
    /// ステレオ（L/R でサブキャリア配置をずらす）。
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
    /// </summary>
    public int StereoFrequencyShiftBins { get; }

    /// <summary>
    /// 乱数シードを取得します。0 の場合は <see cref="Random.Shared"/> を使用します。
    /// </summary>
    public int RandomSeed { get; }

    /// <summary>
    /// 妥当性検証済みの OFDM 設定を初期化します。
    /// </summary>
    /// <param name="fftSize">FFT サイズ。0 より大きい 2 のべき乗を指定します。</param>
    /// <param name="activeSubcarriers">有効サブキャリア数。0 より大きく FFT サイズ未満を指定します。</param>
    /// <param name="cyclicPrefixLength">巡回プレフィックス長。0 以上かつ FFT サイズ未満を指定します。</param>
    /// <param name="ofdmSymbolCount">OFDM シンボル数。0 より大きい値を指定します。</param>
    /// <param name="modulationScheme">変調方式。QPSK / 16QAM / 64QAM を指定します。</param>
    /// <param name="channelMode">チャンネル構成。モノラル/ステレオを指定します。</param>
    /// <param name="enableFrequencyInterleaving">周波数インターリーブを有効にする場合は true。</param>
    /// <param name="pilotSpacing">パイロット挿入間隔（N サブキャリアごとに 1 パイロット）。</param>
    /// <param name="stereoFrequencyShiftBins">ステレオ時の R チャンネル周波数シフト量（ビン）。</param>
    /// <param name="randomSeed">乱数シード。0 を指定すると <see cref="Random.Shared"/> を使用します。</param>
    /// <exception cref="ArgumentException">いずれかの引数が有効範囲外の場合にスローされます。</exception>
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

        if (modulationScheme is not ModulationScheme.Qpsk and not ModulationScheme.Qam16 and not ModulationScheme.Qam64)
        {
            throw new ArgumentException("Modulation scheme must be QPSK, 16QAM, or 64QAM.", nameof(modulationScheme));
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
    private static readonly int[] Qam16Levels = [-3, -1, 1, 3];
    private static readonly int[] Qam64Levels = [-7, -5, -3, -1, 1, 3, 5, 7];
    private static readonly Complex PilotSymbol = Complex.One;

    /// <summary>
    /// 新しいジェネレータを初期化します。
    /// </summary>
    /// <param name="config">妥当性検証済みの OFDM 設定。</param>
    public OfdmGenerator(OfdmConfig config)
    {
        _config = config;
        _random = config.RandomSeed == 0 ? Random.Shared : new Random(config.RandomSeed);
    }

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

    private void ShuffleInPlace(List<int> values)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    private Complex GenerateModulatedSymbol()
    {
        return _config.ModulationScheme switch
        {
            ModulationScheme.Qpsk => GenerateQpskSymbol(),
            ModulationScheme.Qam16 => GenerateQam16Symbol(),
            ModulationScheme.Qam64 => GenerateQam64Symbol(),
            _ => throw new InvalidOperationException("Unsupported modulation scheme.")
        };
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
        var grayIndex = GrayCode.Encode((byte)binaryIndex) & ((1 << bitsPerAxis) - 1);
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
