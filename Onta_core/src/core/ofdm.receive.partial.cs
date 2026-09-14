using System.Numerics;

namespace Onta.Core;

public sealed partial class OfdmGenerator
{
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
    /// 読み取りカーソルを追従させながら指定シンボル数だけスキップします。
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
}
