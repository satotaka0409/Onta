using System.Numerics;
using System.Text;

namespace Onta.Core;

public sealed partial class FileWavCodec
{
    /// <summary>
    /// 送信時間見積りの 1 セグメントです。
    /// </summary>
    /// <param name="Label">項目名。</param>
    /// <param name="Samples">サンプル数。</param>
    /// <param name="Seconds">秒数。</param>
    /// <param name="SizeBytes">表示用サイズ（bytes）。null のとき「-」。</param>
    public readonly record struct TransmissionDurationSegment(
        string Label,
        long Samples,
        double Seconds,
        long? SizeBytes);

    /// <summary>
    /// 送信全体の所要時間見積りです。
    /// </summary>
    /// <param name="SampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="TotalSamples">合計サンプル数。</param>
    /// <param name="TotalSeconds">合計秒数。</param>
    /// <param name="Segments">セグメント一覧（プリアンブル／FH／BLK／TAIL 等）。</param>
    /// <returns>record。</returns>
    public sealed record TransmissionDurationEstimate(
        int SampleRate,
        long TotalSamples,
        double TotalSeconds,
        IReadOnlyList<TransmissionDurationSegment> Segments);

    /// <summary>
    /// プロファイルとファイルサイズから送信所要時間を見積もります。
    /// </summary>
    /// <param name="profile">符号化プロファイル。</param>
    /// <param name="fileSizeBytes">送信ファイルのバイト数。</param>
    /// <returns>セグメント別・合計の送信時間見積り。</returns>
    public static TransmissionDurationEstimate EstimateTransmissionDuration(FileWavCodecProfile profile, long fileSizeBytes)
    {
        if (fileSizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileSizeBytes));
        }

        var codec = new FileWavCodec(profile);
        var payloadLengths = BuildBlockPayloadLengths(fileSizeBytes);
        var segments = new List<TransmissionDurationSegment>(payloadLengths.Length * Math.Max(1, profile.BlockInterleaveFactor) + 8);
        var totalSamples = 0L;

        totalSamples += profile.LeadingSilenceSamples;
        AddSegment(segments, "プリアンブル", profile.UnmodulatedPreambleSamples, profile.SampleRate, ref totalSamples, sizeBytes: null);

        var openingHeaderOfdm = codec.CreateHeaderOfdm();
        var openingFhSamples = HeaderPacketSamples(openingHeaderOfdm, FileHeaderBytes, profile.FileHeaderUnmodulatedSamples);
        AddSegment(
            segments,
            "ファイルヘッダ",
            openingFhSamples,
            profile.SampleRate,
            ref totalSamples,
            sizeBytes: FileHeaderBytes);

        for (var pass = 0; pass < profile.BlockInterleaveFactor; pass++)
        {
            var (passSc, passMod) = ResolveInterleavePassModulation(
                pass,
                profile.ActiveSubcarriers,
                profile.ModulationScheme);
            var headerOfdm = codec.CreateHeaderOfdm(OfdmConfig.ResolveCarrierGrid(passSc));
            var dataOfdm = codec.CreateDataOfdm(passSc, passMod);
            var fhPacketSamples = HeaderPacketSamples(headerOfdm, FileHeaderBytes, profile.FileHeaderUnmodulatedSamples);
            var bhPacketSamples = HeaderPacketSamples(headerOfdm, BlockHeaderBytes, profile.BlockHeaderUnmodulatedSamples);
            var order = GetBlockEmissionOrder(payloadLengths.Length, pass);
            for (var local = 0; local < order.Length; local++)
            {
                if (local > 0 && (local % FileHeaderRepeatIntervalBlocks) == 0)
                {
                    AddSegment(
                        segments,
                        "ファイルヘッダ",
                        fhPacketSamples,
                        profile.SampleRate,
                        ref totalSamples,
                        sizeBytes: FileHeaderBytes);
                }

                var blockIndex = order[local];
                var dataSamples = DataPacketSamples(
                    dataOfdm,
                    payloadLengths[blockIndex],
                    profile.ChannelMode,
                    passMod);
                var blkSamples = bhPacketSamples + dataSamples;
                var blkLabel = profile.BlockInterleaveFactor == 1
                    ? $"BLK-{blockIndex}"
                    : $"BLK-{blockIndex}(P{pass + 1})";
                AddSegment(
                    segments,
                    blkLabel,
                    blkSamples,
                    profile.SampleRate,
                    ref totalSamples,
                    sizeBytes: payloadLengths[blockIndex]);
            }
        }

        AddSegment(
            segments,
            "ファイルヘッダ",
            HeaderPacketSamples(openingHeaderOfdm, FileHeaderBytes, profile.FileHeaderUnmodulatedSamples),
            profile.SampleRate,
            ref totalSamples,
            sizeBytes: FileHeaderBytes);
        AddSegment(segments, "TAIL", profile.TrailingSilenceSamples, profile.SampleRate, ref totalSamples, sizeBytes: null);

        return new TransmissionDurationEstimate(
            profile.SampleRate,
            totalSamples,
            totalSamples / (double)profile.SampleRate,
            segments);
    }

    /// <summary>
    /// 送信時間見積りをラベル:秒数の行テキストへ整形します。
    /// </summary>
    /// <param name="estimate">見積り結果。</param>
    /// <param name="digits">秒数の小数桁数（0〜6）。</param>
    /// <returns>セグメント行と合計行を含む文字列。</returns>
    public static string FormatTransmissionDurationBreakdown(TransmissionDurationEstimate estimate, int digits = 3)
    {
        var sb = new StringBuilder(estimate.Segments.Count * 24);
        var fmt = "F" + Math.Clamp(digits, 0, 6);
        for (var i = 0; i < estimate.Segments.Count; i++)
        {
            var seg = estimate.Segments[i];
            sb.Append(seg.Label)
              .Append(':')
              .Append(seg.Seconds.ToString(fmt))
              .Append("s")
              .AppendLine();
        }

        sb.Append("合計:")
          .Append(estimate.TotalSeconds.ToString(fmt))
          .Append("s");
        return sb.ToString();
    }

    /// <summary>
    /// ファイルサイズを 8192 バイト単位のブロック長配列へ分割します。
    /// </summary>
    /// <param name="fileSizeBytes">ファイルバイト数。</param>
    /// <returns>各ブロックのペイロード長（末尾のみ短くなり得る）。</returns>
    private static int[] BuildBlockPayloadLengths(long fileSizeBytes)
    {
        if (fileSizeBytes == 0)
        {
            return [0];
        }

        var blockCountLong = (fileSizeBytes + DataBlockBytes - 1) / DataBlockBytes;
        if (blockCountLong > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(fileSizeBytes), "Block count exceeds supported range.");
        }

        var blockCount = (int)blockCountLong;
        var lengths = new int[blockCount];
        var remaining = fileSizeBytes;
        for (var i = 0; i < blockCount; i++)
        {
            var len = (int)Math.Min(DataBlockBytes, remaining);
            lengths[i] = len;
            remaining -= len;
        }

        return lengths;
    }

    /// <summary>
    /// ヘッダーパケット（無変調＋符号化ビット）のサンプル数を求めます。
    /// </summary>
    /// <param name="headerOfdm">ヘッダー用 OFDM 生成器。</param>
    /// <param name="payloadLength">符号化前ペイロード長（バイト）。</param>
    /// <param name="unmodulatedSamples">先頭無変調サンプル数。</param>
    /// <returns>パケット全体のサンプル数。</returns>
    private static int HeaderPacketSamples(OfdmGenerator headerOfdm, int payloadLength, int unmodulatedSamples)
    {
        var rsLength = GetReedSolomonEncodedLength(payloadLength);
        var convLength = GetConvolutionalEncodedLength(rsLength, HeaderPunctureRate);
        var totalBits = convLength * 8;
        var channelBits = headerOfdm.ChannelMode == ChannelMode.Stereo
            ? (totalBits + 1) / 2
            : totalBits;
        return unmodulatedSamples + headerOfdm.SampleCountForBitCount(channelBits);
    }

    /// <summary>
    /// データ部パケット（ターボ＋畳み込み後）のサンプル数を求めます。
    /// </summary>
    /// <param name="dataOfdm">データ用 OFDM 生成器。</param>
    /// <param name="payloadLength">ブロックペイロード長（バイト）。</param>
    /// <param name="channelMode">モノラル／ステレオ。</param>
    /// <param name="modulationScheme">データ部変調方式。</param>
    /// <returns>データパケットのサンプル数（BH は含まない）。</returns>
    private static int DataPacketSamples(
        OfdmGenerator dataOfdm,
        int payloadLength,
        ChannelMode channelMode,
        ModulationScheme modulationScheme)
    {
        var withCrcLength = payloadLength + CrcBytes;
        var paddedLength = TurboPaddedLength(withCrcLength);
        var turboLength = (paddedLength / TurboEcc1024.DataUnitBytes) * TurboEcc1024.EncodedBytes;
        var convLength = GetConvolutionalEncodedLength(turboLength, ResolveDataPunctureRate(modulationScheme));
        var totalBits = convLength * 8;
        var channelBits = channelMode == ChannelMode.Stereo
            ? (totalBits + 1) / 2
            : totalBits;
        return dataOfdm.SampleCountForBitCount(channelBits);
    }

    /// <summary>
    /// 見積りセグメントを追加し、合計サンプル数を更新します。
    /// </summary>
    /// <param name="segments">セグメント一覧。</param>
    /// <param name="label">項目名。</param>
    /// <param name="samples">当該セグメントのサンプル数。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="totalSamples">累計サンプル数（加算更新）。</param>
    /// <param name="sizeBytes">表示用サイズ。null のとき「-」。</param>
    private static void AddSegment(
        List<TransmissionDurationSegment> segments,
        string label,
        long samples,
        int sampleRate,
        ref long totalSamples,
        long? sizeBytes)
    {
        totalSamples += samples;
        segments.Add(new TransmissionDurationSegment(label, samples, samples / (double)sampleRate, sizeBytes));
    }
}
