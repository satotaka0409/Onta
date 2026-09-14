using System.Numerics;

namespace Onta.Core;

public sealed partial class OfdmGenerator
{
    /// <summary>
    /// public を実行します。
    /// </summary>
    /// <param name="Left">Left を指定します。</param>
    /// <param name="absoluteSampleOffset">absoluteSampleOffset を指定します。</param>
    public (Complex[] Left, Complex[] Right) ModulateBits(ReadOnlySpan<bool> bits, long absoluteSampleOffset = 0, int interleaveInitSeed = 0)
    {
        return ModulateBitStreams(bits, bits, absoluteSampleOffset, interleaveInitSeed);
    }

    /// <summary>
    /// public を実行します。
    /// </summary>
    /// <param name="Left">Left を指定します。</param>
    /// <param name="Right">Right を指定します。</param>
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
    /// <param name="useRightChannel">useRightChannel を指定します。</param>
    /// <param name="absoluteSampleOffset">absoluteSampleOffset を指定します。</param>
    /// <param name="interleaveInitSeed">interleaveInitSeed を指定します。</param>
    /// <returns>蜃ｦ逅・ｵ先棡縲・/returns>
    /// <summary>
    /// ModulateBitsOnChannel を実行します。
    /// </summary>
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
}
