using System.Numerics;
using Onta.Stream;
using Xunit;
using Xunit.Abstractions;

namespace Onta.Core.Tests.Stream;

/// <summary>
/// ストリーム録音・再生（PCM → Opus → パケット → OFDM → 復調 → Opus → PCM）のループバックです。
/// </summary>
public sealed class OntaTestStream
{
    private const string Mp3FileName = "1minute.mp3";
    private const string WavFileName = "1minute.wav";
    private const string CoverFileName = "onta_splash_512x256.png";

    /// <summary>送信・受信ワーカーと同じ 0.1 秒チャンク。</summary>
    private const int ChunkFrames = 4410;

    /// <summary>Opus 1 フレーム（20 ms）を 44.1 kHz へ戻したときのサンプル数。</summary>
    private const int DecodedFrameSamples = 882;

    private const string Title = "音多ストリーム試験 1minute";
    private const string Artist = "Onta Project";

    /// <summary>結果を再現できるよう送信側のストリーム ID を固定する。</summary>
    private const ushort TestStreamId = 0x4F4E;

    private readonly ITestOutputHelper _output;

    /// <summary>
    /// テスト出力（実測値のログ）を受け取ります。
    /// </summary>
    public OntaTestStream(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(Mp3FileName)]
    [InlineData(WavFileName)]
    public void AudioFileReader_SampleFiles_ReadAsSixtySecondStereo(string fileName)
    {
        var (left, right) = ReadPcm(ResolveInput(fileName), maxSeconds: 0);
        var seconds = left.Length / (double)StreamConstants.DefaultSampleRate;
        _output.WriteLine($"{fileName}: {seconds:F3} s, RMS L={Rms(left):F4} R={Rms(right):F4}");

        Assert.Equal(left.Length, right.Length);
        Assert.InRange(seconds, 59.5, 61.0);
        Assert.True(Rms(left) > 0.01, "L が無音です。");
        Assert.True(Rms(right) > 0.01, "R が無音です。");
    }

    [Theory]
    [InlineData(1808)]
    [InlineData(1800)]
    [InlineData(StreamCoverImage.MaxBytes)]
    public void MetaAssembler_Cover_CountsBlocksAndRestoresBytes(int coverLength)
    {
        var cover = BuildPng(coverLength);
        var rotator = new StreamMetaRotator(string.Empty, string.Empty, cover);
        var assembler = new StreamMetaAssembler();
        var total = (coverLength + StreamConstants.MetaBlockDataBytes - 1) / StreamConstants.MetaBlockDataBytes;

        Assert.Equal(0, assembler.CoverTotalBlocks);
        Assert.Empty(assembler.GetCoverBytes());

        for (var i = 0; i < total; i++)
        {
            var (kind, totalBlocks, blockIndex, data) = rotator.Next();
            assembler.Ingest(0x1234, kind, totalBlocks, blockIndex, data);
            Assert.Equal(i + 1, assembler.CoverReceivedBlocks);
            Assert.Equal(total, assembler.CoverTotalBlocks);
            Assert.Equal(i + 1 == total, assembler.CoverComplete);
            Assert.Equal(i + 1 == total ? coverLength : 0, assembler.GetCoverBytes().Length);
        }

        Assert.Equal(cover, assembler.GetCoverBytes());
    }

    [Fact]
    public void MetaAssembler_BrokenData_IsDiscardedAndReacquired()
    {
        const string title = "音多 タイトル テスト";
        var cover = BuildPng(200);
        var rotator = new StreamMetaRotator(title, string.Empty, cover);
        var assembler = new StreamMetaAssembler();
        var titleBlocks = (System.Text.Encoding.UTF8.GetByteCount(title) + 15) / 16;
        var coverBlocks = (cover.Length + 15) / 16;

        // 最初に届くタイトル先頭ブロックとジャケ写 3 ブロック目だけ中身を壊す（CRC は通った想定）
        var titleBroken = false;
        var coverBroken = false;
        for (var i = 0; i < 2 * coverBlocks; i++)
        {
            var (kind, totalBlocks, blockIndex, data) = rotator.Next();
            if (!titleBroken && kind == StreamMetaKind.Title && blockIndex == 0)
            {
                data[0] ^= 0xC0;
                titleBroken = true;
            }
            else if (!coverBroken && kind == StreamMetaKind.Cover && blockIndex == 2)
            {
                data[0] ^= 0xC0;
                coverBroken = true;
            }

            assembler.Ingest(0x1234, kind, totalBlocks, blockIndex, data);
        }

        Assert.False(assembler.CoverComplete);
        Assert.Empty(assembler.GetCoverBytes());
        Assert.Equal(2, assembler.DiscardedCount);

        // 2 周目は正常に届けば表示される
        for (var i = 0; i < 2 * Math.Max(titleBlocks, coverBlocks); i++)
        {
            var (kind, totalBlocks, blockIndex, data) = rotator.Next();
            assembler.Ingest(0x1234, kind, totalBlocks, blockIndex, data);
        }

        Assert.Equal(title, assembler.GetTitleText());
        Assert.Equal(cover, assembler.GetCoverBytes());
    }

    [Fact]
    public void MetaAssembler_SingleCorruptStreamId_KeepsAccumulatedMeta()
    {
        var rotator = new StreamMetaRotator(Title, Artist, null);
        var assembler = new StreamMetaAssembler();
        for (var i = 0; i < 20; i++)
        {
            var (kind, totalBlocks, blockIndex, data) = rotator.Next();
            assembler.Ingest(0x1234, kind, totalBlocks, blockIndex, data);
        }

        Assert.Equal(Title, assembler.GetTitleText());

        // 1 パケットだけ化けた ID は無視し、蓄積も消さない
        var glitch = rotator.Next();
        Assert.False(assembler.Ingest(0x1235, glitch.Kind, glitch.TotalBlocks, glitch.BlockIndex, glitch.Data));
        var next = rotator.Next();
        Assert.False(assembler.Ingest(0x1234, next.Kind, next.TotalBlocks, next.BlockIndex, next.Data));
        Assert.Equal(Title, assembler.GetTitleText());
        Assert.Equal(Artist, assembler.GetArtistText());

        // 同じ新 ID が 2 パケット続けば新しいストリームとして切り替える
        var a = rotator.Next();
        Assert.False(assembler.Ingest(0x5678, a.Kind, a.TotalBlocks, a.BlockIndex, a.Data));
        var b = rotator.Next();
        Assert.True(assembler.Ingest(0x5678, b.Kind, b.TotalBlocks, b.BlockIndex, b.Data));
        Assert.Equal((ushort)0x5678, assembler.CurrentStreamId);
    }

    /// <summary>
    /// IHDR・埋め草チャンク・IEND からなる、指定バイト数ちょうどの構造的に正しい PNG を作ります。
    /// </summary>
    /// <param name="length">全体のバイト数（57 以上）。</param>
    /// <returns>PNG バイト列。</returns>
    private static byte[] BuildPng(int length)
    {
        var png = new byte[length];
        byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        signature.CopyTo(png, 0);
        var pos = signature.Length;
        pos = WriteChunk(png, pos, "IHDR"u8, 13, new Random(length));
        pos = WriteChunk(png, pos, "zzZz"u8, length - pos - 12 - 12, new Random(length + 1));
        WriteChunk(png, pos, "IEND"u8, 0, new Random(0));
        return png;

        static int WriteChunk(byte[] dest, int offset, ReadOnlySpan<byte> type, int dataLength, Random random)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(dest.AsSpan(offset), (uint)dataLength);
            type.CopyTo(dest.AsSpan(offset + 4));
            random.NextBytes(dest.AsSpan(offset + 8, dataLength));
            var crc = Onta.Core.Crc32.Compute(dest.AsSpan(offset + 4, 4 + dataLength));
            Onta.Core.Crc32.WriteBigEndian(dest.AsSpan(offset + 8 + dataLength), crc);
            return offset + 12 + dataLength;
        }
    }

    [Theory]
    [InlineData(StreamModeId.Rate18k)]
    [InlineData(StreamModeId.Rate20k)]
    [InlineData(StreamModeId.Rate23k)]
    [InlineData(StreamModeId.Rate24k)]
    [InlineData(StreamModeId.Rate28k)]
    [InlineData(StreamModeId.Rate30k)]
    public void Codec_EachMode_SinglePacketRoundTrips(StreamModeId modeId)
    {
        var random = new Random(42);
        var payload = new byte[StreamConstants.PayloadBytes];
        random.NextBytes(payload);
        var metaData = new byte[StreamConstants.MetaBlockDataBytes];
        random.NextBytes(metaData);
        var packet = new StreamPacket
        {
            ModeId = modeId,
            StreamId = 0x1234,
            MetaKind = StreamMetaKind.Artist,
            MetaTotalBlocks = 3,
            MetaBlockIndex = 1,
            MetaData = metaData,
            Payload = payload,
        };

        var codec = new StreamOfdmCodec(modeId, StreamConstants.DefaultSampleRate);
        var (left, right) = codec.ModulatePacket(packet);
        var cursor = 0;
        var ok = codec.TryDemodulatePacket(left, right, ref cursor, out var decoded);
        _output.WriteLine($"{modeId}: samples={left.Length} ok={ok} cursor={cursor}");

        Assert.True(ok);
        Assert.NotNull(decoded);
        Assert.Equal(modeId, decoded!.ModeId);
        Assert.Equal(packet.StreamId, decoded.StreamId);
        Assert.Equal(packet.MetaKind, decoded.MetaKind);
        Assert.Equal(packet.MetaTotalBlocks, decoded.MetaTotalBlocks);
        Assert.Equal(packet.MetaBlockIndex, decoded.MetaBlockIndex);
        Assert.Equal(metaData, decoded.MetaData);
        Assert.Equal(payload, decoded.Payload);
        Assert.Equal(left.Length, cursor);
        Assert.Equal(left.Length, codec.PacketSamples);
        Assert.True(codec.TryDemodulateHeader(left, right, 0, out var headerMode));
        Assert.Equal(modeId, headerMode);
    }

    /// <summary>
    /// パケット途中までのバッファは復調せず、カーソルも動かさないこと（曲情報 CRC だけ通る誤受理の防止）。
    /// </summary>
    [Fact]
    public void Codec_PartialPacket_IsRejected()
    {
        var packet = new StreamPacket
        {
            ModeId = StreamModeId.Rate18k,
            StreamId = 0x0102,
            MetaKind = StreamMetaKind.Title,
            MetaTotalBlocks = 1,
            MetaBlockIndex = 0,
            MetaData = new byte[StreamConstants.MetaBlockDataBytes],
            Payload = new byte[StreamConstants.PayloadBytes],
        };
        var codec = new StreamOfdmCodec(StreamModeId.Rate18k, StreamConstants.DefaultSampleRate);
        var (left, right) = codec.ModulatePacket(packet);
        var half = left.Length / 2;

        var cursor = 0;
        var ok = codec.TryDemodulatePacket(left[..half], right[..half], ref cursor, out var decoded);

        Assert.False(ok);
        Assert.Null(decoded);
        Assert.Equal(0, cursor);
    }

    [Theory]
    [InlineData(StreamModeId.Rate18k)]
    [InlineData(StreamModeId.Rate20k)]
    [InlineData(StreamModeId.Rate23k)]
    [InlineData(StreamModeId.Rate24k)]
    [InlineData(StreamModeId.Rate28k)]
    [InlineData(StreamModeId.Rate30k)]
    public void Loopback_WavEachMode_DecodesEveryFrameAndMeta(StreamModeId modeId)
    {
        var (left, right) = ReadPcm(ResolveInput(WavFileName), maxSeconds: 10);

        var tx = Transmit(modeId, left, right, Title, Artist, cover: null);
        var rx = Receive(tx.Left, tx.Right);
        var airRatio = tx.Left.Length / (double)left.Length;
        var correlation = EnvelopeCorrelation(left, right, rx.Left, rx.Right);
        _output.WriteLine(
            $"{modeId}: packets={tx.PacketCount} rx={rx.Packets} err={rx.PacketErrors} air/audio={airRatio:F3} "
            + $"decoded={rx.Left.Length} expected={ExpectedDecodedSamples(left.Length)} corr={correlation:F3}");

        Assert.Equal(0, rx.PacketErrors);
        Assert.Equal(tx.PacketCount, rx.Packets);
        Assert.Equal(modeId, rx.ModeId);
        Assert.Equal(tx.StreamId, rx.StreamId);
        Assert.Equal(ExpectedDecodedSamples(left.Length), rx.Left.Length);
        Assert.Equal(Title, rx.Title);
        Assert.Equal(Artist, rx.Artist);
        // パケット自体は運ぶ音声より短く、足した無音で送出時間が曲の実時間にそろう（最終パケットだけ超え得る）
        var codec = new StreamOfdmCodec(modeId, StreamConstants.DefaultSampleRate);
        Assert.True(codec.PacketSamples < tx.Left.Length / tx.PacketCount, "パケット間に無音が入っていません。");
        Assert.InRange(tx.Left.Length, ExpectedDecodedSamples(left.Length), ExpectedDecodedSamples(left.Length) + codec.PacketSamples);
        Assert.True(correlation > 0.9, $"復号音声のエンベロープ相関が低すぎます: {correlation:F3}");
    }

    [Fact]
    public void Loopback_Mp3FullMinute_DeliversCoverInOrder()
    {
        var cover = StaThread.Run(() => StreamCoverImage.EncodeFile(ResolveInput(CoverFileName), StreamCoverFormat.Gray48, out _));
        var (left, right) = ReadPcm(ResolveInput(Mp3FileName), maxSeconds: 0);

        var tx = Transmit(StreamModeId.Rate18k, left, right, Title, Artist, cover);
        var rx = Receive(tx.Left, tx.Right);

        // タイトル → アーティスト → ジャケ写の順に 1 パケット 1 ブロックずつ回る
        var coverBlocks = (cover.Length + StreamConstants.MetaBlockDataBytes - 1) / StreamConstants.MetaBlockDataBytes;
        var sentBlocks = Math.Min(coverBlocks, tx.PacketCount / 3);
        _output.WriteLine(
            $"packets={tx.PacketCount} cover={cover.Length} B ({coverBlocks} blocks) sent={sentBlocks} blocks "
            + $"received={rx.Cover.Length} B complete={rx.CoverComplete} rx={rx.Packets} err={rx.PacketErrors}");

        Assert.Equal(0, rx.PacketErrors);
        Assert.Equal(tx.PacketCount, rx.Packets);
        Assert.Equal(ExpectedDecodedSamples(left.Length), rx.Left.Length);
        Assert.Equal(Title, rx.Title);
        Assert.Equal(Artist, rx.Artist);
        Assert.Equal(sentBlocks == coverBlocks, rx.CoverComplete);
        Assert.Equal(sentBlocks, rx.CoverBlocks);
        Assert.Equal(sentBlocks == coverBlocks ? cover : Array.Empty<byte>(), rx.Cover);
    }

    [Fact]
    public void Loopback_WithWhiteNoise_StillDecodesEveryFrame()
    {
        var (left, right) = ReadPcm(ResolveInput(WavFileName), maxSeconds: 5);
        var tx = Transmit(StreamModeId.Rate18k, left, right, Title, Artist, cover: null);

        // 送出信号 RMS に対して SNR 30 dB のホワイトノイズ
        var noiseSigma = Rms(tx.Left) * Math.Pow(10.0, -30.0 / 20.0);
        var random = new Random(1234);
        var noisyLeft = AddNoise(tx.Left, noiseSigma, random);
        var noisyRight = AddNoise(tx.Right, noiseSigma, random);
        var rx = Receive(noisyLeft, noisyRight);
        _output.WriteLine($"signal RMS={Rms(tx.Left):F4} noise σ={noiseSigma:F5} decoded={rx.Left.Length} rx={rx.Packets} err={rx.PacketErrors}");

        Assert.Equal(0, rx.PacketErrors);
        Assert.Equal(tx.PacketCount, rx.Packets);
        Assert.Equal(StreamModeId.Rate18k, rx.ModeId);
        Assert.Equal(ExpectedDecodedSamples(left.Length), rx.Left.Length);
        Assert.Equal(Title, rx.Title);
    }

    /// <summary>
    /// 受信グラフ用の報告（I-Q・ビタビ中間訂正率）がパケットごとに届き、ノイズで訂正率が上がること。
    /// </summary>
    [Theory]
    [InlineData(StreamModeId.Rate18k)]
    [InlineData(StreamModeId.Rate30k)]
    public void Pipeline_PacketReports_CarryIqAndCorrectionRate(StreamModeId modeId)
    {
        // 雑音は全帯域に広がるので、キャリアあたりの SNR はこれより高い
        const double NoisySnrDb = 10.0;
        var (left, right) = ReadPcm(ResolveInput(WavFileName), maxSeconds: 3);
        var tx = Transmit(modeId, left, right, Title, Artist, cover: null);
        var mode = StreamMode.Resolve(modeId);

        var clean = CollectReports(tx.Left, tx.Right);
        var noiseSigma = Rms(tx.Left) * Math.Pow(10.0, -NoisySnrDb / 20.0);
        var random = new Random(99);
        var noisy = CollectReports(AddNoise(tx.Left, noiseSigma, random), AddNoise(tx.Right, noiseSigma, random));

        var cleanRate = clean.Average(r => r.Body!.Value.CorrectionRate);
        var noisyRate = noisy.Where(r => r.Body is not null).Average(r => r.Body!.Value.CorrectionRate);
        _output.WriteLine(
            $"{modeId}: packets={tx.PacketCount} reports={clean.Count} iq/packet={clean[0].IqPoints.Length} "
            + $"clean={cleanRate * 100:F3}% noisy({NoisySnrDb}dB)={noisyRate * 100:F3}% "
            + $"noisyOk={noisy.Count(r => r.Success)}/{noisy.Count}");

        Assert.Equal(tx.PacketCount, clean.Count);
        Assert.All(clean, report =>
        {
            Assert.True(report.Success);
            Assert.Equal(mode, report.Mode);
            Assert.NotNull(report.Body);
            Assert.True(report.IqPoints.Length > 0);
            Assert.Equal(report.IqPoints.Length, report.IqGroups.Length);
            Assert.All(report.IqGroups, g => Assert.InRange(g, (byte)0, (byte)(mode.Subcarriers / 8 - 1)));
        });
        Assert.True(cleanRate < 0.01, $"無雑音で訂正率が高すぎます: {cleanRate:P3}");
        Assert.True(noisyRate > cleanRate, $"雑音で訂正率が上がっていません: clean={cleanRate:P3} noisy={noisyRate:P3}");
    }

    /// <summary>
    /// 受信パイプラインへ信号を流し、<see cref="StreamRxPipeline.PacketReported"/> の報告を集めます。
    /// </summary>
    private static List<StreamRxPacketReport> CollectReports(Complex[] left, Complex[] right)
    {
        var reports = new List<StreamRxPacketReport>();
        using var pipeline = new StreamRxPipeline(StreamConstants.DefaultSampleRate) { PacketReported = reports.Add };
        for (var offset = 0; offset < left.Length; offset += ChunkFrames)
        {
            var count = Math.Min(ChunkFrames, left.Length - offset);
            pipeline.PushCapture(left[offset..(offset + count)], right[offset..(offset + count)]);
            _ = pipeline.Pump(out _);
        }

        for (var i = 0; i < 5; i++)
        {
            pipeline.PushCapture(new Complex[ChunkFrames], new Complex[ChunkFrames]);
            _ = pipeline.Pump(out _);
        }

        return reports;
    }

    [Fact]
    public void Receive_StartingMidStream_RecoversAudioAndMeta()
    {
        var (left, right) = ReadPcm(ResolveInput(WavFileName), maxSeconds: 10);
        var tx = Transmit(StreamModeId.Rate18k, left, right, Title, Artist, cover: null);

        // パケット境界と無関係な位置（約 3.3 秒後）から再生を始める
        var offset = (int)(StreamConstants.DefaultSampleRate * 3.3) + 123;
        var rx = Receive(tx.Left[offset..], tx.Right[offset..]);
        // 途中から始まったパケットは捨て、次のパケット以降はすべて受かるはず
        var expectedPackets = tx.PacketStarts.Count(start => start > offset);
        _output.WriteLine(
            $"offset={offset} decoded={rx.Left.Length} title='{rx.Title}' mode={rx.ModeId} "
            + $"rx={rx.Packets}/{expectedPackets} err={rx.PacketErrors}");

        Assert.Equal(0, rx.PacketErrors);
        Assert.Equal(expectedPackets, rx.Packets);
        Assert.Equal(StreamModeId.Rate18k, rx.ModeId);
        Assert.Equal(Title, rx.Title);
        Assert.True(rx.Left.Length > DecodedFrameSamples * 100, $"復号できた音声が短すぎます: {rx.Left.Length}");
    }

    /// <summary>
    /// テープ速度のずれ・ワウ・小数サンプルの遅れがあっても、速度を推定して受信できること。
    /// </summary>
    [Theory]
    [InlineData(StreamModeId.Rate18k, 0.01, 0.0, 0.5)]
    [InlineData(StreamModeId.Rate18k, -0.01, 0.002, 0.3)]
    [InlineData(StreamModeId.Rate30k, 0.005, 0.001, 0.5)]
    [InlineData(StreamModeId.Rate30k, -0.003, 0.001, 0.25)]
    public void Receive_WithTapeSpeedErrorAndWow_TracksSpeed(StreamModeId modeId, double speedError, double wowDepth, double delay)
    {
        const double WowHz = 3.0;
        var (left, right) = ReadPcm(ResolveInput(WavFileName), maxSeconds: 6);
        var tx = Transmit(modeId, left, right, Title, Artist, cover: null);

        // 再生時に速度 (1 + speedError) で読み、ワウ（ピーク wowDepth / 3 Hz）を重ねる
        var count = (int)((tx.Left.Length - delay) / (1.0 + Math.Abs(speedError) + wowDepth)) - 64;
        var positions = new double[count];
        var pos = delay;
        for (var m = 0; m < count; m++)
        {
            positions[m] = pos;
            pos += 1.0 + speedError + (wowDepth * Math.Sin(2 * Math.PI * WowHz * m / StreamConstants.DefaultSampleRate));
        }

        var playedLeft = new Complex[count];
        var playedRight = new Complex[count];
        StreamSpeedWarp.WarpAt(tx.Left, tx.Left.Length, positions, playedLeft, count);
        StreamSpeedWarp.WarpAt(tx.Right, tx.Right.Length, positions, playedRight, count);

        using var pipeline = new StreamRxPipeline(StreamConstants.DefaultSampleRate);
        for (var offset = 0; offset < count; offset += ChunkFrames)
        {
            var n = Math.Min(ChunkFrames, count - offset);
            pipeline.PushCapture(playedLeft[offset..(offset + n)], playedRight[offset..(offset + n)]);
            _ = pipeline.Pump(out _);
        }

        for (var i = 0; i < 5; i++)
        {
            pipeline.PushCapture(new Complex[ChunkFrames], new Complex[ChunkFrames]);
            _ = pipeline.Pump(out _);
        }

        // 再生側の速度が 1 + speedError なら、受信 1 サンプルは送信 1 + speedError サンプルに当たる
        var expectedDeviation = speedError;
        _output.WriteLine(
            $"{modeId} speed={speedError:P2} wow={wowDepth:P2} delay={delay}: tx={tx.PacketCount} rx={pipeline.PacketsReceived} "
            + $"err={pipeline.PacketErrors} est={pipeline.SpeedDeviation:P3}");

        Assert.InRange(pipeline.PacketErrors, 0, 1);
        Assert.InRange(pipeline.PacketsReceived, tx.PacketCount - 2, tx.PacketCount);
        Assert.InRange(pipeline.SpeedDeviation, expectedDeviation - 0.0005, expectedDeviation + 0.0005);
        Assert.Equal(Title, pipeline.Meta.GetTitleText());
    }

    /// <summary>
    /// 44.1 kHz で復調しつつ、再生用に復号音声を Opus 本来の 48 kHz で取り出せること。
    /// </summary>
    [Fact]
    public void Receive_DecodedAt48k_KeepsOpusFrameLength()
    {
        var (left, right) = ReadPcm(ResolveInput(WavFileName), maxSeconds: 3);
        var tx = Transmit(StreamModeId.Rate18k, left, right, Title, Artist, cover: null);

        var rx44 = Receive(tx.Left, tx.Right);
        var rx48 = Receive(tx.Left, tx.Right, Onta.Stream.Opus.OpusEncoder.OpusSampleRate);
        var frames = rx44.Left.Length / DecodedFrameSamples;
        _output.WriteLine($"frames={frames} decoded44={rx44.Left.Length} decoded48={rx48.Left.Length} rx={rx48.Packets} err={rx48.PacketErrors}");

        Assert.Equal(0, rx48.PacketErrors);
        Assert.Equal(tx.PacketCount, rx48.Packets);
        Assert.True(frames > 0);
        Assert.Equal(frames * Onta.Stream.Opus.OpusEncoder.FrameSamplesPerChannel, rx48.Left.Length);
        Assert.Equal(rx48.Left.Length, rx48.Right.Length);
    }

    /// <summary>
    /// 送信パイプラインへ 0.1 秒チャンクで PCM を流し、変調済み信号を連結して返します。
    /// </summary>
    private static TxResult Transmit(StreamModeId modeId, double[] left, double[] right, string title, string artist, byte[]? cover)
    {
        using var pipeline = new StreamTxPipeline(modeId, title, artist, cover, StreamConstants.DefaultSampleRate, TestStreamId);
        var packets = new List<(Complex[] Left, Complex[] Right)>();
        for (var offset = 0; offset < left.Length; offset += ChunkFrames)
        {
            var count = Math.Min(ChunkFrames, left.Length - offset);
            packets.AddRange(pipeline.PushPcm(left.AsSpan(offset, count), right.AsSpan(offset, count)));
        }

        packets.AddRange(pipeline.Flush());
        foreach (var (l, r) in packets)
        {
            Assert.NotSame(l, r);
            Assert.Equal(l.Length, r.Length);
        }

        var starts = new int[packets.Count];
        for (var i = 1; i < packets.Count; i++)
        {
            starts[i] = starts[i - 1] + packets[i - 1].Left.Length;
        }

        return new TxResult(
            packets.SelectMany(p => p.Left).ToArray(),
            packets.SelectMany(p => p.Right).ToArray(),
            packets.Count,
            pipeline.StreamId,
            starts);
    }

    /// <summary>
    /// 受信パイプラインへ 0.1 秒チャンクで信号を流し、復号 PCM と曲情報を集めます。
    /// </summary>
    /// <param name="left">受信する L 信号。</param>
    /// <param name="right">受信する R 信号。</param>
    /// <param name="decodedSampleRate">復号音声のサンプリング周波数（0 なら 44.1 kHz）。</param>
    private static RxResult Receive(Complex[] left, Complex[] right, int decodedSampleRate = 0)
    {
        using var pipeline = new StreamRxPipeline(StreamConstants.DefaultSampleRate, decodedSampleRate);
        var outLeft = new List<double>();
        var outRight = new List<double>();

        void Pump()
        {
            foreach (var (l, r) in pipeline.Pump(out _))
            {
                outLeft.AddRange(l);
                outRight.AddRange(r);
            }
        }

        for (var offset = 0; offset < left.Length; offset += ChunkFrames)
        {
            var count = Math.Min(ChunkFrames, left.Length - offset);
            pipeline.PushCapture(left[offset..(offset + count)], right[offset..(offset + count)]);
            Pump();
        }

        // 末尾パケットを取り出すため、無音を少し足してから吐き出させる
        for (var i = 0; i < 5; i++)
        {
            pipeline.PushCapture(new Complex[ChunkFrames], new Complex[ChunkFrames]);
            Pump();
        }

        return new RxResult(
            outLeft.ToArray(),
            outRight.ToArray(),
            pipeline.DetectedModeId,
            pipeline.Meta.CurrentStreamId,
            pipeline.Meta.GetTitleText(),
            pipeline.Meta.GetArtistText(),
            pipeline.Meta.GetCoverBytes(),
            pipeline.Meta.CoverComplete,
            pipeline.Meta.CoverReceivedBlocks,
            pipeline.PacketsReceived,
            pipeline.PacketErrors);
    }

    /// <summary>
    /// 音声ファイルを 44.1 kHz ステレオ PCM として読み込みます。
    /// </summary>
    /// <param name="path">WAV / MP3 のパス。</param>
    /// <param name="maxSeconds">読み込む最大秒数（0 なら全体）。</param>
    private static (double[] Left, double[] Right) ReadPcm(string path, double maxSeconds)
    {
        var limit = maxSeconds > 0 ? (int)(maxSeconds * StreamConstants.DefaultSampleRate) : int.MaxValue;
        var left = new List<double>();
        var right = new List<double>();
        using var reader = new StreamAudioFilePcmReader(path);
        while (left.Count < limit && reader.TryRead(ChunkFrames, out var l, out var r))
        {
            var take = Math.Min(l.Length, limit - left.Count);
            for (var i = 0; i < take; i++)
            {
                left.Add(l[i].Real);
                right.Add(r[i].Real);
            }
        }

        return (left.ToArray(), right.ToArray());
    }

    /// <summary>
    /// 入力 PCM 長から、欠落なく復号できた場合の出力サンプル数を求めます。
    /// </summary>
    /// <remarks>エンコーダはチャンクごとに 44.1k→48k へ変換し、960 サンプル（20 ms）単位でフレーム化します。</remarks>
    private static int ExpectedDecodedSamples(int inputSamples)
    {
        var samples48k = 0L;
        for (var offset = 0; offset < inputSamples; offset += ChunkFrames)
        {
            var count = Math.Min(ChunkFrames, inputSamples - offset);
            samples48k += (long)Math.Ceiling(count * (48000.0 / 44100.0));
        }

        return (int)(samples48k / 960) * DecodedFrameSamples;
    }

    /// <summary>
    /// 入力と復号音声の 20 ms ごとの RMS エンベロープの相関（遅延は ±10 フレームで最良値）を求めます。
    /// </summary>
    private static double EnvelopeCorrelation(double[] inLeft, double[] inRight, double[] outLeft, double[] outRight)
    {
        var reference = Envelope(inLeft, inRight);
        var decoded = Envelope(outLeft, outRight);
        var best = double.NegativeInfinity;
        for (var lag = -10; lag <= 10; lag++)
        {
            best = Math.Max(best, Pearson(reference, decoded, lag));
        }

        return best;
    }

    /// <summary>
    /// L/R 平均の 20 ms ブロック RMS 列を求めます。
    /// </summary>
    private static double[] Envelope(double[] left, double[] right)
    {
        var blocks = Math.Min(left.Length, right.Length) / DecodedFrameSamples;
        var envelope = new double[blocks];
        for (var b = 0; b < blocks; b++)
        {
            var sum = 0.0;
            for (var i = b * DecodedFrameSamples; i < (b + 1) * DecodedFrameSamples; i++)
            {
                var mono = (left[i] + right[i]) * 0.5;
                sum += mono * mono;
            }

            envelope[b] = Math.Sqrt(sum / DecodedFrameSamples);
        }

        return envelope;
    }

    /// <summary>
    /// b を lag だけずらして a と重なる区間のピアソン相関を求めます。
    /// </summary>
    private static double Pearson(double[] a, double[] b, int lag)
    {
        var pairs = new List<(double A, double B)>();
        for (var i = 0; i < a.Length; i++)
        {
            var j = i + lag;
            if (j >= 0 && j < b.Length)
            {
                pairs.Add((a[i], b[j]));
            }
        }

        if (pairs.Count < 10)
        {
            return double.NegativeInfinity;
        }

        var meanA = pairs.Average(p => p.A);
        var meanB = pairs.Average(p => p.B);
        var cov = pairs.Sum(p => (p.A - meanA) * (p.B - meanB));
        var varA = pairs.Sum(p => (p.A - meanA) * (p.A - meanA));
        var varB = pairs.Sum(p => (p.B - meanB) * (p.B - meanB));
        return varA <= 0 || varB <= 0 ? double.NegativeInfinity : cov / Math.Sqrt(varA * varB);
    }

    /// <summary>
    /// 実数 PCM の RMS を求めます。
    /// </summary>
    private static double Rms(double[] samples)
    {
        return samples.Length == 0 ? 0.0 : Math.Sqrt(samples.Sum(s => s * s) / samples.Length);
    }

    /// <summary>
    /// 複素 PCM（実部）の RMS を求めます。
    /// </summary>
    private static double Rms(Complex[] samples)
    {
        return samples.Length == 0 ? 0.0 : Math.Sqrt(samples.Sum(s => s.Real * s.Real) / samples.Length);
    }

    /// <summary>
    /// 複素 PCM の実部にガウスノイズを加えた新しい配列を返します。
    /// </summary>
    private static Complex[] AddNoise(Complex[] samples, double sigma, Random random)
    {
        var result = new Complex[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            var gaussian = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            result[i] = new Complex(samples[i].Real + (gaussian * sigma), 0.0);
        }

        return result;
    }

    /// <summary>
    /// test/in_files 配下のサンプルファイルを解決します。
    /// </summary>
    private static string ResolveInput(string fileName)
    {
        var path = Path.Combine(Path.GetDirectoryName(TestPaths.ResolveInputPng())!, fileName);
        Assert.True(File.Exists(path), $"サンプルファイルがありません: {path}");
        return path;
    }

    /// <summary>送信結果（連結した変調済み信号）です。</summary>
    private sealed record TxResult(Complex[] Left, Complex[] Right, int PacketCount, ushort StreamId, int[] PacketStarts);

    /// <summary>受信結果（復号 PCM と曲情報）です。</summary>
    private sealed record RxResult(
        double[] Left,
        double[] Right,
        StreamModeId? ModeId,
        ushort? StreamId,
        string Title,
        string Artist,
        byte[] Cover,
        bool CoverComplete,
        int CoverBlocks,
        int Packets,
        int PacketErrors);
}
