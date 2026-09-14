using System.Numerics;

namespace Onta.Core;

public sealed partial class OfdmGenerator
{
    /// <summary>
    /// 単一ビット列を左右同一ストリームとして OFDM 変調します。
    /// </summary>
    /// <param name="bits">変調対象ビット列。</param>
    /// <param name="absoluteSampleOffset">ストリーム先頭からの絶対サンプル位置。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ初期シード。</param>
    public (Complex[] Left, Complex[] Right) ModulateBits(ReadOnlySpan<bool> bits, long absoluteSampleOffset = 0, int interleaveInitSeed = 0)
    {
        return ModulateBitStreams(bits, bits, absoluteSampleOffset, interleaveInitSeed);
    }

    /// <summary>
    /// 左右チャネル別のビット列を OFDM 変調して PCM シンボル列を生成します。
    /// </summary>
    /// <param name="leftBits">左チャネルに載せるビット列。</param>
    /// <param name="rightBits">右チャネルに載せるビット列。</param>
    /// <param name="absoluteSampleOffset">ストリーム先頭からの絶対サンプル位置。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ初期シード。</param>
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
    /// 指定チャネルのビット列を OFDM シンボル列へ変換します。
    /// </summary>
    /// <param name="bits">変調対象ビット列。</param>
    /// <param name="useRightChannel">右チャネル用変調かどうか。</param>
    /// <param name="absoluteSampleOffset">ストリーム先頭からの絶対サンプル位置。</param>
    /// <param name="interleaveInitSeed">周波数インターリーブ初期シード。</param>
    /// <returns>CP 付き OFDM シンボルを連結した複素 PCM 配列。</returns>
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

            // Hermitian 適用後のスペクトルを出す（受信 FFT と左右対称で比較できる）
            var observer = TxSpectrumObserver;
            var stride = Math.Max(1, TxSpectrumStride);
            if (observer is not null && (_txSpectrumCounter++ % stride) == 0)
            {
                observer(freqBins, useRightChannel);
            }

            write += symbolLength;
        }

        return samples;
    }
}
