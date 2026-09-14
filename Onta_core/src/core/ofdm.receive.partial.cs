using System.Numerics;

namespace Onta.Core;

public sealed partial class OfdmGenerator
{
    /// <summary>
    /// DemodulateBitsFromStream を実行します。
    /// </summary>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="cursor">cursor を指定します。</param>
    /// <param name="bitCount">bitCount を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。</param>
    /// <param name="logicalSampleOffset">logicalSampleOffset を指定します。</param>
    /// <param name="searchRadius">searchRadius を指定します。</param>
    /// <param name="interleaveInitSeed">interleaveInitSeed を指定します。</param>
    /// <returns>戻り値を返します。</returns>
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
    /// DemodulateSoftLlrsFromStream を実行します。
    /// </summary>
    /// <param name="onOfdmSymbolProgress">onOfdmSymbolProgress を指定します。</param>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="cursor">cursor を指定します。</param>
    /// <param name="bitCount">bitCount を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。</param>
    /// <param name="logicalSampleOffset">logicalSampleOffset を指定します。</param>
    /// <param name="searchRadius">searchRadius を指定します。</param>
    /// <param name="noiseVariance">譌｢遏･髮鷹浹蛻・淵縲・/param>
    /// <param name="interleaveInitSeed">interleaveInitSeed を指定します。</param>
    /// <param name="onEqualizedDataSymbol">onEqualizedDataSymbol を指定します。</param>
    /// <param name="onEqualizedDataSymbolFrame">onEqualizedDataSymbolFrame を指定します。</param>
    /// <param name="onFftSymbolFrame">onFftSymbolFrame を指定します。</param>
    /// <returns>戻り値を返します。</returns>
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
        Action<Complex[], byte[], int>? onEqualizedDataSymbolFrame = null,
        Action<Complex[], int>? onFftSymbolFrame = null,
        Action<int, int>? onOfdmSymbolProgress = null)
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
            onFftSymbolFrame,
            onOfdmSymbolProgress);
    }

    /// <summary>
    /// DemodulateSoftLlrsStereoCombined を実行します。
    /// </summary>
    /// <param name="leftSamples">leftSamples を指定します。</param>
    /// <param name="rightSamples">rightSamples を指定します。</param>
    /// <param name="cursor">cursor を指定します。</param>
    /// <param name="bitCount">bitCount を指定します。</param>
    /// <param name="logicalSampleOffset">logicalSampleOffset を指定します。</param>
    /// <param name="searchRadius">searchRadius を指定します。</param>
    /// <param name="noiseVariance">譌｢遏･髮鷹浹蛻・淵縲・/param>
    /// <param name="estimateNoiseFromPilots">estimateNoiseFromPilots を指定します。</param>
    /// <param name="interleaveInitSeed">interleaveInitSeed を指定します。</param>
    /// <returns>戻り値を返します。</returns>
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
                onFftSymbolFrame: null,
                onOfdmSymbolProgress: null);
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
            onFftSymbolFrame: null,
            onOfdmSymbolProgress: null);
    }

    /// <summary>
    /// SkipSymbolsWithTimingTracking を実行します。
    /// </summary>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="cursor">cursor を指定します。</param>
    /// <param name="symbolCount">symbolCount を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。</param>
    /// <param name="searchRadius">searchRadius を指定します。</param>
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
    /// DemodulateBits を実行します。
    /// </summary>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="bitCount">bitCount を指定します。</param>
    /// <param name="useRightChannel">useRightChannel を指定します。</param>
    /// <param name="absoluteSampleOffset">absoluteSampleOffset を指定します。</param>
    /// <param name="interleaveInitSeed">interleaveInitSeed を指定します。</param>
    /// <returns>戻り値を返します。</returns>
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
}
