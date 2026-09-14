using System.Numerics;
using System.Text;

namespace Onta.Core;

public sealed partial class FileWavCodec
{
    /// <summary>
    /// 処理の補足説明です。
    /// </summary>
    public readonly record struct TransmissionDurationSegment(string Label, long Samples, double Seconds);

    /// <summary>
    /// 処理の補足説明です。
    /// </summary>
    public sealed record TransmissionDurationEstimate(
        int SampleRate,
        long TotalSamples,
        double TotalSeconds,
        IReadOnlyList<TransmissionDurationSegment> Segments);

    /// <returns>戻り値を返します。</returns>
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
        AddSegment(segments, "プリアンブル", profile.UnmodulatedPreambleSamples, profile.SampleRate, ref totalSamples);

        var openingHeaderOfdm = codec.CreateHeaderOfdm();
        var openingFhSamples = HeaderPacketSamples(openingHeaderOfdm, FileHeaderBytes, profile.FileHeaderUnmodulatedSamples);
        AddSegment(segments, "FH", openingFhSamples, profile.SampleRate, ref totalSamples);

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
                    AddSegment(segments, "FH", fhPacketSamples, profile.SampleRate, ref totalSamples);
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
                AddSegment(segments, blkLabel, blkSamples, profile.SampleRate, ref totalSamples);
            }
        }

        AddSegment(
            segments,
            "FH",
            HeaderPacketSamples(openingHeaderOfdm, FileHeaderBytes, profile.FileHeaderUnmodulatedSamples),
            profile.SampleRate,
            ref totalSamples);
        AddSegment(segments, "TAIL", profile.TrailingSilenceSamples, profile.SampleRate, ref totalSamples);

        return new TransmissionDurationEstimate(
            profile.SampleRate,
            totalSamples,
            totalSamples / (double)profile.SampleRate,
            segments);
    }

    /// <returns>戻り値を返します。</returns>
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

        sb.Append("蜷郁ｨ・")
          .Append(estimate.TotalSeconds.ToString(fmt))
          .Append("s");
        return sb.ToString();
    }

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

    private static void AddSegment(
        List<TransmissionDurationSegment> segments,
        string label,
        long samples,
        int sampleRate,
        ref long totalSamples)
    {
        totalSamples += samples;
        segments.Add(new TransmissionDurationSegment(label, samples, samples / (double)sampleRate));
    }
}


