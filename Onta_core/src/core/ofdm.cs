using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Otofa.Core;

public static class OfdmProgram
{
    public static void Main(string[] args)
    {
        // Parameters: FFT size, used carriers, cyclic prefix, and OFDM symbol count.
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

public sealed record OfdmConfig
{
    public int FftSize { get; }
    public int ActiveSubcarriers { get; }
    public int CyclicPrefixLength { get; }
    public int OfdmSymbolCount { get; }
    public int RandomSeed { get; }

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

public sealed class OfdmGenerator
{
    private readonly OfdmConfig _config;
    private readonly Random _random;

    public OfdmGenerator(OfdmConfig config)
    {
        _config = config;
        _random = config.RandomSeed == 0 ? Random.Shared : new Random(config.RandomSeed);
    }

    public Complex[] GenerateFrame()
    {
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

        // Symmetrically place active QPSK carriers around DC, excluding the DC carrier.
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
        var reversed = 0;
        for (var i = 0; i < bitCount; i++)
        {
            reversed = (reversed << 1) | (value & 1);
            value >>= 1;
        }

        return reversed;
    }
}
