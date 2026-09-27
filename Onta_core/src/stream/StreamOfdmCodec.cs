using System.Numerics;
using Onta.Core;

namespace Onta.Stream;

/// <summary>
/// 曲情報／データ部をビタビ復号したときの中間訂正率です（CRC の合否によらず算出）。
/// </summary>
/// <param name="CorrectionRate">全符号ビットで、受信硬判定と再符号化結果が食い違った割合（0〜1）。</param>
/// <param name="LeftCorrectionRate">L チャネル分の符号ビットでの同割合（0〜1）。</param>
/// <param name="RightCorrectionRate">R チャネル分の符号ビットでの同割合（0〜1）。</param>
public readonly record struct StreamBodyDiagnostics(
    double CorrectionRate,
    double LeftCorrectionRate,
    double RightCorrectionRate);

/// <summary>
/// ストリームパケットを OFDM（畳み込みのみ）で変復調します。
/// </summary>
public sealed class StreamOfdmCodec
{
    private const int BodyBytes = StreamConstants.MetaBytes + StreamConstants.PayloadBytes;

    private readonly OfdmGenerator _headerOfdm;
    private readonly OfdmGenerator _dataOfdm;
    private readonly StreamModeInfo _mode;
    private readonly int _headerCodedBits;
    private readonly int _bodyCodedBits;
    private readonly int _headerSectionSamples;
    private readonly int _packetSamples;
    private bool[] _bits = Array.Empty<bool>();
    private bool[] _leftBits = Array.Empty<bool>();
    private bool[] _rightBits = Array.Empty<bool>();
    private double[] _llrL = Array.Empty<double>();
    private double[] _llrR = Array.Empty<double>();
    private double[] _llrJoined = Array.Empty<double>();
    private readonly byte[] _wire = new byte[StreamConstants.PacketBytes];

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

        _headerCodedBits = ConvolutionalCode.GetEncodedBitLength(
            StreamConstants.HeaderBytes * 8,
            terminated: true,
            ConvolutionalCode.PunctureRate.Rate2_3);
        _bodyCodedBits = ConvolutionalCode.GetEncodedBitLength(
            BodyBytes * 8,
            terminated: true,
            ConvolutionalCode.PunctureRate.Rate2_3);
        _headerSectionSamples = StreamConstants.PreambleSamples + SectionSamples(_headerOfdm, _headerCodedBits);
        _packetSamples = _headerSectionSamples + StreamConstants.PreambleSamples + SectionSamples(_dataOfdm, _bodyCodedBits);
    }

    /// <summary>現在のモード情報。</summary>
    public StreamModeInfo Mode => _mode;

    /// <summary>パケット先頭からヘッダー末尾までのサンプル数（プリアンブル＋ヘッダー）。全モード共通。</summary>
    public int HeaderSectionSamples => _headerSectionSamples;

    /// <summary>1 パケットのサンプル数（プリアンブル＋ヘッダー＋プリアンブル＋曲情報／データ）。</summary>
    public int PacketSamples => _packetSamples;

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
        var body = wire.AsSpan(StreamConstants.HeaderBytes, BodyBytes).ToArray();

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
    /// <param name="cursor">パケット先頭のサンプル位置。成功時のみパケット末尾へ進める。</param>
    /// <param name="packet">成功時のパケット。</param>
    /// <returns>パケット全体がバッファにあり、パイロット・CRC が妥当なら true。</returns>
    public bool TryDemodulatePacket(Complex[] left, Complex[] right, ref int cursor, out StreamPacket? packet)
    {
        return TryDemodulatePacket(left, right, ref cursor, out packet, out _, onBodyIqFrame: null);
    }

    /// <summary>
    /// ステレオ PCM からパケットを復調し、曲情報／データ部の I-Q とビタビ中間訂正率も取り出します。
    /// </summary>
    /// <param name="left">L PCM。</param>
    /// <param name="right">R PCM。</param>
    /// <param name="cursor">パケット先頭のサンプル位置。成功時のみパケット末尾へ進める。</param>
    /// <param name="packet">成功時のパケット。</param>
    /// <param name="body">曲情報／データ部をビタビ復号できた場合の訂正率（CRC 不一致でも設定）。復号前に失敗したら null。</param>
    /// <param name="onBodyIqFrame">曲情報／データ部の 1 OFDM シンボルごとの等化後シンボル・グループ・点数（配列は再利用されるため呼び出し側で複製する）。</param>
    /// <returns>パケット全体がバッファにあり、パイロット・CRC が妥当なら true。</returns>
    public bool TryDemodulatePacket(
        Complex[] left,
        Complex[] right,
        ref int cursor,
        out StreamPacket? packet,
        out StreamBodyDiagnostics? body,
        Action<Complex[], byte[], int>? onBodyIqFrame,
        int sampleCount = -1)
    {
        packet = null;
        body = null;
        var length = SampleLength(left, right, sampleCount);
        // 途中までしか無いパケットを復調すると、先頭の曲情報 CRC だけ通って欠けたペイロードを受理してしまう
        if (length < 0 || cursor < 0 || cursor + _packetSamples > length)
        {
            return false;
        }

        var position = cursor + StreamConstants.PreambleSamples;
        if (!TryDemodulateSection(
                left,
                right,
                ref position,
                StreamConstants.HeaderBytes,
                _headerOfdm,
                ConvolutionalCode.PunctureRate.Rate2_3,
                _headerCodedBits,
                onIqFrame: null,
                sampleLength: length,
                measureCorrection: false,
                out var headerBytes,
                out _))
        {
            return false;
        }

        position += StreamConstants.PreambleSamples;
        if (!TryDemodulateSection(
                left,
                right,
                ref position,
                BodyBytes,
                _dataOfdm,
                ConvolutionalCode.PunctureRate.Rate2_3,
                _bodyCodedBits,
                onBodyIqFrame,
                sampleLength: length,
                measureCorrection: true,
                out var bodyBytes,
                out var bodyDiagnostics))
        {
            return false;
        }

        body = bodyDiagnostics;

        Buffer.BlockCopy(headerBytes, 0, _wire, 0, StreamConstants.HeaderBytes);
        Buffer.BlockCopy(bodyBytes, 0, _wire, StreamConstants.HeaderBytes, BodyBytes);
        if (!StreamPacket.TryUnpack(_wire, out packet))
        {
            return false;
        }

        cursor = position;
        return true;
    }

    /// <summary>
    /// パケット先頭（ヘッダー前プリアンブルの先頭）からヘッダーだけを復調し、パイロットと速度 ID を検証します。
    /// </summary>
    /// <remarks>ヘッダーは全モード共通の変調なので、どのモードのコーデックでも復調できます。</remarks>
    /// <param name="left">L PCM。</param>
    /// <param name="right">R PCM。</param>
    /// <param name="packetStart">パケット先頭のサンプル位置。</param>
    /// <param name="modeId">成功時のストリーム速度 ID。</param>
    /// <returns>パイロット 3 バイトと速度 ID が妥当なら true。</returns>
    public bool TryDemodulateHeader(
        Complex[] left,
        Complex[] right,
        int packetStart,
        out StreamModeId modeId,
        int sampleCount = -1)
    {
        modeId = default;
        var length = SampleLength(left, right, sampleCount);
        if (length < 0 || packetStart < 0 || packetStart + _headerSectionSamples > length)
        {
            return false;
        }

        var position = packetStart + StreamConstants.PreambleSamples;
        if (!TryDemodulateSection(
                left,
                right,
                ref position,
                StreamConstants.HeaderBytes,
                _headerOfdm,
                ConvolutionalCode.PunctureRate.Rate2_3,
                _headerCodedBits,
                onIqFrame: null,
                sampleLength: length,
                measureCorrection: false,
                out var header,
                out _))
        {
            return false;
        }

        if (header[0] != StreamConstants.HeaderPilot[0]
            || header[1] != StreamConstants.HeaderPilot[1]
            || header[2] != StreamConstants.HeaderPilot[2])
        {
            return false;
        }

        var mode = StreamMode.TryResolve(header[3]);
        if (mode is null)
        {
            return false;
        }

        modeId = mode.Value.Id;
        return true;
    }

    /// <summary>
    /// 1 セクション（ヘッダーまたは曲情報／データ）の OFDM サンプル数を求めます。
    /// </summary>
    /// <remarks>L/R に符号化ビットを前半／後半で分け、多い方のシンボル数に揃える送信側と同じ計算です。</remarks>
    /// <param name="ofdm">セクションの OFDM。</param>
    /// <param name="codedBitCount">畳み込み後のビット数。</param>
    private static int SectionSamples(OfdmGenerator ofdm, int codedBitCount)
    {
        var mid = (codedBitCount + 1) / 2;
        var bps = Math.Max(1, ofdm.BitsPerOfdmSymbol);
        var symbols = Math.Max(
            (mid + bps - 1) / bps,
            (codedBitCount - mid + bps - 1) / bps);
        return Math.Max(1, symbols) * ofdm.SamplesPerOfdmSymbol;
    }

    private (Complex[] Left, Complex[] Right) ModulateSection(
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
        Ensure(_bits, exactBitCount, out _bits);
        var written = StreamBitUtil.WriteBytesToBitsMsb(coded, _bits.AsSpan(0, exactBitCount));
        if (written < exactBitCount)
        {
            Array.Clear(_bits, written, exactBitCount - written);
        }

        var mid = (exactBitCount + 1) / 2;
        var bps = Math.Max(1, ofdm.BitsPerOfdmSymbol);
        var symbols = Math.Max(
            (mid + bps - 1) / bps,
            (exactBitCount - mid + bps - 1) / bps);
        var aligned = Math.Max(1, symbols) * bps;
        Ensure(_leftBits, aligned, out _leftBits);
        Ensure(_rightBits, aligned, out _rightBits);
        CopyChannelBits(_bits, 0, mid, _leftBits, aligned);
        CopyChannelBits(_bits, mid, exactBitCount - mid, _rightBits, aligned);
        return ofdm.ModulateBitStreams(_leftBits.AsSpan(0, aligned), _rightBits.AsSpan(0, aligned), absoluteSampleOffset);
    }

    /// <summary>
    /// 作業配列が足りなければ取り直します。
    /// </summary>
    private static void Ensure<T>(T[] current, int needed, out T[] buffer)
    {
        buffer = current.Length >= needed ? current : new T[needed];
    }

    /// <summary>
    /// 符号ビットの一部をチャネル用バッファへコピーし、余りは 0 で埋めます。
    /// </summary>
    private static void CopyChannelBits(bool[] source, int sourceOffset, int count, bool[] destination, int aligned)
    {
        var n = Math.Max(0, Math.Min(count, source.Length - sourceOffset));
        if (n > 0)
        {
            Array.Copy(source, sourceOffset, destination, 0, n);
        }

        if (n < aligned)
        {
            Array.Clear(destination, n, aligned - n);
        }
    }

    /// <summary>
    /// 復号結果を再符号化し、受信 LLR の硬判定と食い違った符号ビットの割合を L/R 別に求めます。
    /// </summary>
    /// <remarks>訂正後の残差ではなく、ビタビが訂正したビットの割合（画面のエラー率グラフ用）。</remarks>
    /// <param name="llrs">L（前半 mid ビット）→ R の順に連結した符号ビット LLR（正がビット 1）。</param>
    /// <param name="mid">L チャネルに載せた符号ビット数。</param>
    /// <param name="payload">復号したバイト列。</param>
    /// <param name="puncture">パンクチャ率。</param>
    private static StreamBodyDiagnostics MeasureCorrection(
        ReadOnlySpan<double> llrs,
        int mid,
        byte[] payload,
        ConvolutionalCode.PunctureRate puncture)
    {
        var reencoded = ConvolutionalCode.Encode(payload, terminate: true, punctureRate: puncture);
        var count = Math.Min(llrs.Length, reencoded.Length * 8);
        var leftErrors = 0;
        var rightErrors = 0;
        for (var i = 0; i < count; i++)
        {
            var sent = ((reencoded[i >> 3] >> (7 - (i & 7))) & 1) != 0;
            if ((llrs[i] >= 0.0) == sent)
            {
                continue;
            }

            if (i < mid)
            {
                leftErrors++;
            }
            else
            {
                rightErrors++;
            }
        }

        var leftCount = Math.Min(mid, count);
        var rightCount = count - leftCount;
        return new StreamBodyDiagnostics(
            count == 0 ? 0.0 : (double)(leftErrors + rightErrors) / count,
            leftCount == 0 ? 0.0 : (double)leftErrors / leftCount,
            rightCount == 0 ? 0.0 : (double)rightErrors / rightCount);
    }

    /// <summary>
    /// 1 セクションを L/R 別に復調してソフト LLR を連結し、ビタビ復号します。
    /// </summary>
    /// <param name="left">L PCM。</param>
    /// <param name="right">R PCM。</param>
    /// <param name="cursor">セクション先頭。終了位置で更新されます。</param>
    /// <param name="payloadBytes">復号後のバイト数。</param>
    /// <param name="ofdm">セクションの OFDM。</param>
    /// <param name="puncture">パンクチャ率。</param>
    /// <param name="codedBitCount">畳み込み後のビット数。</param>
    /// <param name="onIqFrame">等化後シンボルのコールバック（不要なら null）。</param>
    /// <param name="payload">復号したバイト列。</param>
    /// <param name="diagnostics">L/R 別のビタビ中間訂正率。</param>
    /// <returns>復号できたら true（CRC は呼び出し側で検証）。</returns>
    private bool TryDemodulateSection(
        Complex[] left,
        Complex[] right,
        ref int cursor,
        int payloadBytes,
        OfdmGenerator ofdm,
        ConvolutionalCode.PunctureRate puncture,
        int codedBitCount,
        Action<Complex[], byte[], int>? onIqFrame,
        int sampleLength,
        bool measureCorrection,
        out byte[] payload,
        out StreamBodyDiagnostics diagnostics)
    {
        payload = Array.Empty<byte>();
        diagnostics = default;
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
            Ensure(_llrL, aligned, out _llrL);
            Ensure(_llrR, aligned, out _llrR);
            var llrL = ofdm.DemodulateSoftLlrsFromStream(
                left,
                ref cursorL,
                aligned,
                useRightChannel: false,
                logicalSampleOffset: 0,
                onEqualizedDataSymbolFrame: onIqFrame,
                sampleCount: sampleLength,
                llrDestination: _llrL);
            var llrR = ofdm.DemodulateSoftLlrsFromStream(
                right,
                ref cursorR,
                aligned,
                useRightChannel: true,
                logicalSampleOffset: 0,
                onEqualizedDataSymbolFrame: onIqFrame,
                sampleCount: sampleLength,
                llrDestination: _llrR);
            cursor = Math.Max(cursorL, cursorR);

            Ensure(_llrJoined, codedBitCount, out _llrJoined);
            Array.Clear(_llrJoined, 0, codedBitCount);
            Array.Copy(llrL, 0, _llrJoined, 0, Math.Min(mid, llrL.Length));
            Array.Copy(llrR, 0, _llrJoined, mid, Math.Min(rightBitCount, llrR.Length));
            var llrs = _llrJoined.AsSpan(0, codedBitCount);

            payload = ConvolutionalCode.DecodeSoft(
                llrs,
                payloadBytes,
                terminated: true,
                punctureRate: puncture);
            if (payload.Length < payloadBytes)
            {
                return false;
            }

            if (measureCorrection)
            {
                diagnostics = MeasureCorrection(llrs, mid, payload, puncture);
            }

            return true;
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

    /// <summary>
    /// 復調に使う有効サンプル数を返します。左右の長さが違うときは -1 です。
    /// </summary>
    private static int SampleLength(Complex[] left, Complex[] right, int sampleCount)
    {
        if (left.Length != right.Length)
        {
            return -1;
        }

        return sampleCount < 0 ? left.Length : Math.Min(left.Length, sampleCount);
    }
}
