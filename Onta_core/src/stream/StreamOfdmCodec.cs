using System.Numerics;
using Onta.Core;

namespace Onta.Stream;

/// <summary>
/// ストリームパケットを OFDM（畳み込みのみ）で変復調します。
/// </summary>
public sealed class StreamOfdmCodec
{
    private readonly OfdmGenerator _headerOfdm;
    private readonly OfdmGenerator _dataOfdm;
    private readonly StreamModeInfo _mode;

    /// <summary>
    /// 指定モード用コーデックを構築します。
    /// </summary>
    /// <param name="modeId">ストリーム速度 ID。</param>
    public StreamOfdmCodec(StreamModeId modeId)
    {
        _mode = StreamMode.Resolve(modeId);
        // ヘッダーは常に ID=01（48SC / 8PSK / ステレオ、R=2/3）
        _headerOfdm = CreateGenerator(StreamConstants.HeaderSubcarriers, ModulationScheme.Psk8, symbolCount: 4);
        _dataOfdm = CreateGenerator(_mode.Subcarriers, _mode.Modulation, symbolCount: 16);
    }

    /// <summary>現在のモード情報。</summary>
    public StreamModeInfo Mode => _mode;

    /// <summary>ヘッダー用 OFDM。</summary>
    public OfdmGenerator HeaderOfdm => _headerOfdm;

    /// <summary>データ用 OFDM。</summary>
    public OfdmGenerator DataOfdm => _dataOfdm;

    /// <summary>
    /// 送信スペクトル監視をヘッダー／データ OFDM の両方へ取り付けます。
    /// </summary>
    /// <param name="observer">L/R 周波数ビン通知。</param>
    /// <param name="stride">何シンボルごとに通知するか（1=毎シンボル）。</param>
    public void AttachTxSpectrumObserver(OfdmGenerator.TxSpectrumHandler observer, int stride = 1)
    {
        _headerOfdm.TxSpectrumObserver = observer;
        _dataOfdm.TxSpectrumObserver = observer;
        _headerOfdm.TxSpectrumStride = Math.Max(1, stride);
        _dataOfdm.TxSpectrumStride = Math.Max(1, stride);
    }

    /// <summary>
    /// 送信スペクトル監視を外します。
    /// </summary>
    public void ClearTxSpectrumObserver()
    {
        _headerOfdm.TxSpectrumObserver = null;
        _dataOfdm.TxSpectrumObserver = null;
    }

    /// <summary>
    /// パケット 1 個をステレオ PCM へ変調します（HDR 前・META 前に 10ms 無変調）。
    /// </summary>
    /// <param name="packet">論理パケット。</param>
    /// <param name="absoluteSampleOffset">絶対サンプル位置。</param>
    /// <returns>L/R PCM。</returns>
    public (Complex[] Left, Complex[] Right) ModulatePacket(StreamPacket packet, long absoluteSampleOffset = 0)
    {
        var wire = packet.Pack();
        var header = wire.AsSpan(0, StreamConstants.HeaderBytes).ToArray();
        var body = wire.AsSpan(StreamConstants.HeaderBytes, StreamConstants.MetaBytes + StreamConstants.PayloadBytes).ToArray();

        var (hL, hR) = ModulateSection(header, _headerOfdm, ConvolutionalCode.PunctureRate.Rate2_3, absoluteSampleOffset);
        var offsetAfterHeader = absoluteSampleOffset + StreamConstants.PreambleSamples + hL.Length;
        var (bL, bR) = ModulateSection(body, _dataOfdm, ConvolutionalCode.PunctureRate.Rate2_3, offsetAfterHeader + StreamConstants.PreambleSamples);

        var preamble = StreamConstants.PreambleSamples;
        var total = preamble + hL.Length + preamble + bL.Length;
        var left = new Complex[total];
        var right = new Complex[total];
        // preamble = zeros (already)
        var w = preamble;
        Array.Copy(hL, 0, left, w, hL.Length);
        Array.Copy(hR, 0, right, w, hR.Length);
        w += hL.Length + preamble;
        Array.Copy(bL, 0, left, w, bL.Length);
        Array.Copy(bR, 0, right, w, bR.Length);
        return (left, right);
    }

    /// <summary>
    /// ステレオ PCM からパケットを復調します（簡易カーソル追従）。
    /// </summary>
    /// <param name="left">L PCM。</param>
    /// <param name="right">R PCM。</param>
    /// <param name="cursor">読み取り位置（サンプル）。</param>
    /// <param name="packet">成功時のパケット。</param>
    /// <returns>パイロット・CRC が妥当なら true。</returns>
    public bool TryDemodulatePacket(Complex[] left, Complex[] right, ref int cursor, out StreamPacket? packet)
    {
        packet = null;
        if (left.Length != right.Length || left.Length == 0)
        {
            return false;
        }

        // Skip header preamble
        cursor = Math.Min(cursor + StreamConstants.PreambleSamples, left.Length);

        var headerCodedBits = ConvolutionalCode.GetEncodedBitLength(
            StreamConstants.HeaderBytes * 8,
            terminated: true,
            ConvolutionalCode.PunctureRate.Rate2_3);
        if (!TryDemodulateSection(
                left,
                right,
                ref cursor,
                StreamConstants.HeaderBytes,
                _headerOfdm,
                ConvolutionalCode.PunctureRate.Rate2_3,
                headerCodedBits,
                out var headerBytes))
        {
            return false;
        }

        cursor = Math.Min(cursor + StreamConstants.PreambleSamples, left.Length);

        var bodyLen = StreamConstants.MetaBytes + StreamConstants.PayloadBytes;
        var bodyCodedBits = ConvolutionalCode.GetEncodedBitLength(
            bodyLen * 8,
            terminated: true,
            ConvolutionalCode.PunctureRate.Rate2_3);
        if (!TryDemodulateSection(
                left,
                right,
                ref cursor,
                bodyLen,
                _dataOfdm,
                ConvolutionalCode.PunctureRate.Rate2_3,
                bodyCodedBits,
                out var bodyBytes))
        {
            return false;
        }

        var wire = new byte[StreamConstants.PacketBytes];
        Buffer.BlockCopy(headerBytes, 0, wire, 0, StreamConstants.HeaderBytes);
        Buffer.BlockCopy(bodyBytes, 0, wire, StreamConstants.HeaderBytes, bodyLen);
        return StreamPacket.TryUnpack(wire, out packet);
    }

    private static (Complex[] Left, Complex[] Right) ModulateSection(
        byte[] payload,
        OfdmGenerator ofdm,
        ConvolutionalCode.PunctureRate puncture,
        long absoluteSampleOffset)
    {
        var coded = ConvolutionalCode.Encode(payload, terminate: true, punctureRate: puncture);
        var exactBitCount = ConvolutionalCode.GetEncodedBitLength(
            payload.Length * 8,
            terminated: true,
            punctureRate: puncture);
        var bits = StreamBitUtil.BytesToBitsMsb(coded);
        if (bits.Length > exactBitCount)
        {
            bits = bits.AsSpan(0, exactBitCount).ToArray();
        }
        else if (bits.Length < exactBitCount)
        {
            var padded = new bool[exactBitCount];
            bits.CopyTo(padded, 0);
            bits = padded;
        }

        var (leftBits, rightBits) = StreamBitUtil.SplitStereoBits(bits);
        // L/R の OFDM シンボル数を揃え、受信側と同じビット数を送る
        var bps = Math.Max(1, ofdm.BitsPerOfdmSymbol);
        var symbols = Math.Max(
            (leftBits.Length + bps - 1) / bps,
            (rightBits.Length + bps - 1) / bps);
        var aligned = Math.Max(1, symbols) * bps;
        leftBits = PadBits(leftBits, aligned);
        rightBits = PadBits(rightBits, aligned);
        return ofdm.ModulateBitStreams(leftBits, rightBits, absoluteSampleOffset);
    }

    private static bool[] PadBits(bool[] bits, int length)
    {
        if (bits.Length == length)
        {
            return bits;
        }

        var padded = new bool[length];
        Array.Copy(bits, padded, Math.Min(bits.Length, length));
        return padded;
    }

    private static bool TryDemodulateSection(
        Complex[] left,
        Complex[] right,
        ref int cursor,
        int payloadBytes,
        OfdmGenerator ofdm,
        ConvolutionalCode.PunctureRate puncture,
        int codedBitCount,
        out byte[] payload)
    {
        payload = Array.Empty<byte>();
        try
        {
            var mid = (codedBitCount + 1) / 2;
            var rightBitCount = codedBitCount - mid;
            var bps = Math.Max(1, ofdm.BitsPerOfdmSymbol);
            var symbols = Math.Max(
                (mid + bps - 1) / bps,
                (rightBitCount + bps - 1) / bps);
            var aligned = Math.Max(1, symbols) * bps;

            var cursorL = cursor;
            var cursorR = cursor;
            var llrL = ofdm.DemodulateSoftLlrsFromStream(
                left,
                ref cursorL,
                aligned,
                useRightChannel: false,
                logicalSampleOffset: 0);
            var llrR = ofdm.DemodulateSoftLlrsFromStream(
                right,
                ref cursorR,
                aligned,
                useRightChannel: true,
                logicalSampleOffset: 0);
            cursor = Math.Max(cursorL, cursorR);

            var llrs = new double[codedBitCount];
            Array.Copy(llrL, 0, llrs, 0, Math.Min(mid, llrL.Length));
            Array.Copy(llrR, 0, llrs, mid, Math.Min(rightBitCount, llrR.Length));

            payload = ConvolutionalCode.DecodeSoft(
                llrs,
                payloadBytes,
                terminated: true,
                punctureRate: puncture);
            return payload.Length >= payloadBytes;
        }
        catch
        {
            return false;
        }
    }

    private static OfdmGenerator CreateGenerator(int subcarriers, ModulationScheme modulation, int symbolCount)
    {
        var grid = OfdmConfig.ResolveCarrierGrid(subcarriers);
        var fft = OfdmConfig.ResolveFftSize(subcarriers, ChannelMode.Stereo);
        var config = new OfdmConfig(
            fftSize: fft,
            activeSubcarriers: subcarriers,
            cyclicPrefixLength: StreamConstants.DataCyclicPrefix,
            ofdmSymbolCount: Math.Max(1, symbolCount),
            modulationScheme: modulation,
            channelMode: ChannelMode.Stereo,
            pilotSpacing: 8,
            stereoFrequencyShiftBins: 1,
            sampleRate: StreamConstants.SampleRate,
            randomSeed: 0,
            carrierGrid: grid);
        return new OfdmGenerator(config);
    }
}
