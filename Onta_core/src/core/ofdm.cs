using System.Globalization;
using System.Numerics;

namespace Otofa.Core;

/// <summary>
/// OFDM フレームを生成し、複素サンプルを CSV に出力する CLI エントリポイントです。
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
            randomSeed: 42);

        var generator = new OfdmGenerator(config);
        var timeDomainSamples = generator.GenerateFrame();

        Console.WriteLine("OFDM frame generated.");
        Console.WriteLine($"FFT Size            : {config.FftSize}");
        Console.WriteLine($"Active Subcarriers  : {config.ActiveSubcarriers}");
        Console.WriteLine($"Cyclic Prefix Length: {config.CyclicPrefixLength}");
        Console.WriteLine($"OFDM Symbol Count   : {config.OfdmSymbolCount}");
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
    /// <param name="randomSeed">乱数シード。0 を指定すると <see cref="Random.Shared"/> を使用します。</param>
    /// <exception cref="ArgumentException">いずれかの引数が有効範囲外の場合にスローされます。</exception>
    public OfdmConfig(
        int fftSize,
        int activeSubcarriers,
        int cyclicPrefixLength,
        int ofdmSymbolCount,
        int randomSeed = 0)
    {
        FftSize = fftSize;
        ActiveSubcarriers = activeSubcarriers;
        CyclicPrefixLength = cyclicPrefixLength;
        OfdmSymbolCount = ofdmSymbolCount;
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
    }
}

/// <summary>
/// <see cref="OfdmConfig"/> に基づいて時間領域 OFDM サンプルを生成します。
/// </summary>
public sealed class OfdmGenerator
{
    private readonly OfdmConfig _config;
    private readonly Random _random;

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
        // 出力フレームは、CP 付与後の OFDM シンボルを連結した配列。
        var symbolsWithCp = new List<Complex>(_config.OfdmSymbolCount * (_config.FftSize + _config.CyclicPrefixLength));

        for (var i = 0; i < _config.OfdmSymbolCount; i++)
        {
            var freqBins = BuildFrequencyDomainSymbol();
            var timeSymbol = InverseFft(freqBins);
            var withCp = AddCyclicPrefix(timeSymbol, _config.CyclicPrefixLength);
            symbolsWithCp.AddRange(withCp);
        }

        return symbolsWithCp.ToArray();
    }

    private Complex[] BuildFrequencyDomainSymbol()
    {
        var bins = new Complex[_config.FftSize];
        var half = _config.ActiveSubcarriers / 2;

        // DC を除き、QPSK サブキャリアを DC 周りに対称配置する。
        for (var k = 1; k <= half; k++)
        {
            bins[k] = GenerateQpskSymbol();
            bins[_config.FftSize - k] = GenerateQpskSymbol();
        }

        if (_config.ActiveSubcarriers % 2 != 0)
        {
            bins[half + 1] = GenerateQpskSymbol();
        }

        return bins;
    }

    private Complex GenerateQpskSymbol()
    {
        // ここではグレイマッピングは行わず、QPSK シンボルをランダム生成する。
        var real = _random.Next(2) == 0 ? -1.0 : 1.0;
        var imag = _random.Next(2) == 0 ? -1.0 : 1.0;
        return new Complex(real, imag) / Math.Sqrt(2.0);
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
