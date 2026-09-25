using System.Numerics;

namespace Onta.Core;

public sealed partial class OfdmGenerator
{
    /// <summary>
    /// ストリーム上の OFDM シンボルからハード判定ビットを復調します（シンボル同期付き）。
    /// </summary>
    /// <param name="samples">入力 PCM（複素、Imag=0 可）。</param>
    /// <param name="cursor">読み取り開始位置（サンプル）。終了位置で更新されます。</param>
    /// <param name="bitCount">取り出すビット数。</param>
    /// <param name="useRightChannel">R 搬送波を使うか。</param>
    /// <param name="logicalSampleOffset">論理サンプル位置（互換用。現状未使用）。</param>
    /// <param name="searchRadius">シンボル先頭探索半径（サンプル）。</param>
    /// <returns>復調したハードビット列。</returns>
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
        var dataOrder = ResolveDataCarrierOrder(useRightChannel);

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
    /// ストリーム上の OFDM からソフト LLR を復調します（パイロット雑音推定あり）。
    /// </summary>
    /// <param name="samples">入力 PCM。</param>
    /// <param name="cursor">読み取り開始位置。終了位置で更新されます。</param>
    /// <param name="bitCount">取り出す情報ビット数。</param>
    /// <param name="useRightChannel">R 搬送波を使うか。</param>
    /// <param name="logicalSampleOffset">論理サンプル位置（互換用。現状未使用）。</param>
    /// <param name="searchRadius">シンボル先頭探索半径（サンプル）。</param>
    /// <param name="noiseVariance">雑音分散の初期値（パイロット推定のフォールバック）。</param>
    /// <param name="onEqualizedDataSymbol">等化後データシンボルごとのコールバック。</param>
    /// <param name="onEqualizedDataSymbolFrame">1 OFDM シンボル分の等化後シンボル／グループのコールバック。</param>
    /// <param name="onFftSymbolFrame">FFT 後ビン配列のコールバック。</param>
    /// <param name="onOfdmSymbolProgress">シンボル進捗コールバック（index, total, cursor）。</param>
    /// <returns>ビットごとのソフト LLR。</returns>
    public double[] DemodulateSoftLlrsFromStream(
        Complex[] samples,
        ref int cursor,
        int bitCount,
        bool useRightChannel,
        long logicalSampleOffset,
        int searchRadius = 16,
        double noiseVariance = 0.05,
        Action<Complex>? onEqualizedDataSymbol = null,
        Action<Complex[], byte[], int>? onEqualizedDataSymbolFrame = null,
        Action<Complex[], int>? onFftSymbolFrame = null,
        Action<int, int, int>? onOfdmSymbolProgress = null)
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
            onEqualizedDataSymbol,
            onEqualizedDataSymbolFrame,
            onFftSymbolFrame,
            onOfdmSymbolProgress);
    }

    /// <summary>
    /// ステレオ L/R を結合してソフト LLR を復調します。
    /// </summary>
    /// <param name="leftSamples">L チャネル PCM。</param>
    /// <param name="rightSamples">R チャネル PCM。</param>
    /// <param name="cursor">読み取り開始位置。終了位置で更新されます。</param>
    /// <param name="bitCount">取り出す情報ビット数。</param>
    /// <param name="logicalSampleOffset">論理サンプル位置（互換用。現状未使用）。</param>
    /// <param name="searchRadius">シンボル先頭探索半径（サンプル）。</param>
    /// <param name="noiseVariance">雑音分散の初期値（パイロット推定のフォールバック）。</param>
    /// <param name="estimateNoiseFromPilots">パイロットから雑音分散を推定するか。</param>
    /// <returns>ビットごとのソフト LLR。</returns>
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
                estimateNoiseFromPilots,
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
            onEqualizedDataSymbol: null,
            onEqualizedDataSymbolFrame: null,
            onFftSymbolFrame: null,
            onOfdmSymbolProgress: null);
    }

    /// <summary>
    /// プレアンブル等の OFDM シンボルをタイミング追従しながら読み飛ばします。
    /// </summary>
    /// <param name="samples">入力 PCM。</param>
    /// <param name="cursor">読み取り開始位置。終了位置で更新されます。</param>
    /// <param name="symbolCount">読み飛ばすシンボル数。</param>
    /// <param name="useRightChannel">R 搬送波で同期するか。</param>
    /// <param name="searchRadius">シンボル先頭探索半径。</param>
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
    /// 固定長サンプル列からハード判定ビットを復調します（シンボル境界は既知）。
    /// </summary>
    /// <param name="samples">OFDM シンボル長の整数倍の PCM。</param>
    /// <param name="bitCount">取り出すビット数。</param>
    /// <param name="useRightChannel">R 搬送波を使うか。</param>
    /// <param name="absoluteSampleOffset">絶対サンプル位置（互換用。現状未使用）。</param>
    /// <returns>復調したハードビット列。</returns>
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
        var dataOrder = ResolveDataCarrierOrder(useRightChannel);

        for (var s = 0; s < symbolCount && bitIndex < bitCount; s++)
        {
            _ = absoluteSampleOffset;

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
