using Onta.Core;
using System.Numerics;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// ブロック送信順序と変調ダウングレード規則の検証テストです。
/// </summary>
public sealed class OntaTest5
{
    private static readonly FileWavCodecProfile BaseProfile = new(
        ActiveSubcarriers: 8,
        ModulationScheme: ModulationScheme.Qpsk,
        ChannelMode: ChannelMode.Mono);

    [Fact]
    public void BlockEmissionOrder_MatchesSpecForFourBlocks()
    {
        Assert.Equal([0, 1, 2, 3], FileWavCodec.GetBlockEmissionOrder(4, passIndex: 0));
        Assert.Equal([1, 0, 3, 2], FileWavCodec.GetBlockEmissionOrder(4, passIndex: 1));
    }

    [Fact]
    public void InterleavePassModulation_DowngradesOnSecondPass()
    {
        Assert.Equal((8, ModulationScheme.Qpsk), FileWavCodec.ResolveInterleavePassModulation(0, 8, ModulationScheme.Qpsk));
        Assert.Equal((8, ModulationScheme.Bpsk), FileWavCodec.ResolveInterleavePassModulation(1, 8, ModulationScheme.Qpsk));
        Assert.Equal((8, ModulationScheme.Bpsk), FileWavCodec.ResolveInterleavePassModulation(1, 16, ModulationScheme.Bpsk));
        Assert.Equal((16, ModulationScheme.Qpsk), FileWavCodec.ResolveInterleavePassModulation(1, 24, ModulationScheme.Qam16));
        Assert.Equal((16, ModulationScheme.Qam16), FileWavCodec.ResolveInterleavePassModulation(1, 32, ModulationScheme.Qam64));
        Assert.Equal((16, ModulationScheme.Qam16), FileWavCodec.ResolveInterleavePassModulation(1, 40, ModulationScheme.Qam64));
        Assert.Equal((16, ModulationScheme.Qam16), FileWavCodec.ResolveInterleavePassModulation(1, 48, ModulationScheme.Qam64));
    }

    [Fact]
    public void BlockHeader_RecordsSecondPassSc16_ForBaseSc40()
    {
        var (passSc, passMod) = FileWavCodec.ResolveInterleavePassModulation(1, 40, ModulationScheme.Qam64);
        var method = typeof(FileWavCodec).GetMethod(
            "BuildBlockHeader",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var blockHash = new byte[32];
        var fileHash = new byte[64];
        blockHash[0] = 0x12;
        fileHash[0] = 0x34;

        var header = (byte[]?)method!.Invoke(
            null,
            [
                (byte)passSc,
                (byte)passMod,
                (byte)0,
                0L,
                128,
                blockHash,
                fileHash
            ]);

        Assert.NotNull(header);
        Assert.Equal(16, header![8]);
        Assert.Equal(3, header[9]);
    }

    [Fact]
    public void HeaderCarrierGrid_FollowsPassSubcarriers_ForX2Sc32()
    {
        Assert.Equal(OfdmCarrierGrid.Sc24Family, OfdmConfig.ResolveCarrierGrid(32));
        Assert.Equal(OfdmCarrierGrid.Sc24Family, OfdmConfig.ResolveCarrierGrid(24));
        var (pass1Sc, _) = FileWavCodec.ResolveInterleavePassModulation(1, 32, ModulationScheme.Qam64);
        Assert.Equal(16, pass1Sc);
        Assert.Equal(OfdmCarrierGrid.Sc8Family, OfdmConfig.ResolveCarrierGrid(pass1Sc));

        var codec = new FileWavCodec(new FileWavCodecProfile(
            ActiveSubcarriers: 32,
            ModulationScheme: ModulationScheme.Qam64,
            ChannelMode: ChannelMode.Mono,
            BlockInterleaveFactor: 2));
        var method = typeof(FileWavCodec).GetMethod(
            "CreateHeaderOfdm",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var pass0Header = (OfdmGenerator?)method!.Invoke(
            codec,
            [OfdmConfig.ResolveCarrierGrid(32)]);
        var pass1Header = (OfdmGenerator?)method.Invoke(
            codec,
            [OfdmConfig.ResolveCarrierGrid(pass1Sc)]);
        Assert.NotNull(pass0Header);
        Assert.NotNull(pass1Header);
        Assert.Equal(OfdmCarrierGrid.Sc24Family, pass0Header!.CarrierGrid);
        Assert.Equal(OfdmCarrierGrid.Sc8Family, pass1Header!.CarrierGrid);
    }

    [Fact]
    public void ConceptualLeftBins_MatchModulationGroupTable()
    {
        Assert.Equal(Enumerable.Range(9, 8), OfdmConfig.ResolveGroupBLeftBins());
        Assert.Equal(Enumerable.Range(9, 8), OfdmConfig.ResolveConceptualLeftBins(8));
        Assert.Equal(Enumerable.Range(1, 16), OfdmConfig.ResolveConceptualLeftBins(16));
        Assert.Equal(Enumerable.Range(1, 24), OfdmConfig.ResolveConceptualLeftBins(24));
        Assert.Equal(Enumerable.Range(1, 32), OfdmConfig.ResolveConceptualLeftBins(32));
        Assert.Equal(Enumerable.Range(1, 40), OfdmConfig.ResolveConceptualLeftBins(40));
        Assert.Equal(Enumerable.Range(1, 48), OfdmConfig.ResolveConceptualLeftBins(48));
        Assert.Equal((byte)3, OfdmConfig.ResolveSubcarrierGroupId(32));
        Assert.Equal((byte)4, OfdmConfig.ResolveSubcarrierGroupId(33));
        Assert.Equal((byte)4, OfdmConfig.ResolveSubcarrierGroupId(40));
        Assert.Equal((byte)5, OfdmConfig.ResolveSubcarrierGroupId(41));
        Assert.Equal((byte)5, OfdmConfig.ResolveSubcarrierGroupId(48));
    }

    [Fact]
    public void ResolveFftSize_IsAlways256()
    {
        Assert.Equal(256, OfdmConfig.ResolveFftSize(8, ChannelMode.Mono));
        Assert.Equal(256, OfdmConfig.ResolveFftSize(16, ChannelMode.Stereo));
        Assert.Equal(256, OfdmConfig.ResolveFftSize(24, ChannelMode.Mono));
        Assert.Equal(256, OfdmConfig.ResolveFftSize(32, ChannelMode.Stereo));
        Assert.Equal(256, OfdmConfig.ResolveFftSize(40, ChannelMode.Stereo));
        Assert.Equal(256, OfdmConfig.ResolveFftSize(48, ChannelMode.Stereo));
        Assert.Equal(OfdmConfig.FixedFftSize, OfdmConfig.ResolveFftSize(8, ChannelMode.Mono));
    }

    [Fact]
    public void HeaderOfdmConfig_UsesGroupBConceptualBins()
    {
        var groupB = OfdmConfig.ResolveGroupBLeftBins();
        var fft = OfdmConfig.ResolveFftSize(activeSubcarriers: 8, ChannelMode.Mono);
        var config = new OfdmConfig(
            fftSize: fft,
            activeSubcarriers: groupB.Length,
            cyclicPrefixLength: 32,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Bpsk,
            channelMode: ChannelMode.Mono,
            conceptualLeftBins: groupB,
            carrierGrid: OfdmCarrierGrid.Sc8Family);

        Assert.Equal(groupB, config.ConceptualLeftBins);
        Assert.Equal(Enumerable.Range(9, 8), config.ConceptualLeftBins);
        Assert.Equal(256, config.FftSize);
        Assert.Equal(OfdmCarrierGrid.Sc8Family, config.CarrierGrid);
        Assert.Equal(OfdmConfig.Sc8StartHz, OfdmConfig.LeftCarrierHzSc8(0), 3);
        Assert.Equal(OfdmConfig.CarrierSpacingHz, OfdmConfig.DeltaF8(), 6);
        Assert.Equal(OfdmConfig.CarrierSpacingHz, OfdmConfig.DeltaF24(), 6);
    }

    [Fact]
    public void CarrierSpacing_Is223_9_AndSc8StartsAt650()
    {
        Assert.Equal(223.9, OfdmConfig.CarrierSpacingHz, 6);
        Assert.Equal(650.0, OfdmConfig.LeftCarrierHzSc8(0), 6);
        Assert.Equal(OfdmConfig.DeltaF8(), OfdmConfig.DeltaF24(), 9);
        // B0 = i=8
        Assert.Equal(650.0 + (8 * OfdmConfig.CarrierSpacingHz), OfdmConfig.LeftCarrierHzSc8(8), 6);
        Assert.Equal(
            OfdmConfig.LeftCarrierHzSc8(8) + (OfdmConfig.CarrierSpacingHz / 2.0),
            OfdmConfig.RightCarrierHzSc8(8),
            6);
        Assert.Equal(OfdmConfig.Sc24StartHz, OfdmConfig.LeftCarrierHzSc24(1), 6);
        Assert.Equal(
            OfdmConfig.LeftCarrierHzSc24(1) + (OfdmConfig.CarrierSpacingHz / 2.0),
            OfdmConfig.RightCarrierHzSc24(1),
            6);
        Assert.Equal(550.0 + (8 * OfdmConfig.CarrierSpacingHz), OfdmConfig.LeftCarrierHzSc24(9), 6);
    }

    [Fact]
    public void CarrierFrequencies_MatchModulationTable_Sc24FamilyEndpoints()
    {
        // modulation.mdc SC-24/32/40/48 表の端点（四捨五入 0.1Hz）と実装を照合する。
        static double RoundHz(double hz) =>
            Math.Round(hz, 1, MidpointRounding.AwayFromZero);

        Assert.Equal(550.0, RoundHz(OfdmConfig.LeftCarrierHzSc24(1)), 6);
        Assert.Equal(662.0, RoundHz(OfdmConfig.RightCarrierHzSc24(1)), 6);
        Assert.Equal(9282.1, RoundHz(OfdmConfig.LeftCarrierHzSc24(40)), 6);
        Assert.Equal(9394.1, RoundHz(OfdmConfig.RightCarrierHzSc24(40)), 6);
        Assert.Equal(9506.0, RoundHz(OfdmConfig.LeftCarrierHzSc24(41)), 6);
        Assert.Equal(9618.0, RoundHz(OfdmConfig.RightCarrierHzSc24(41)), 6);
        Assert.Equal(11073.3, RoundHz(OfdmConfig.LeftCarrierHzSc24(48)), 6);
        Assert.Equal(11185.3, RoundHz(OfdmConfig.RightCarrierHzSc24(48)), 6);
    }

    [Fact]
    public void EncodeDecode_QrPng_MatchesOriginal_Mono8ScQpsk_InterleaveX2()
    {
        RoundTrip(
            BaseProfile with { BlockInterleaveFactor = 2 },
            "Sample1_test5_x2.wav",
            "Sample1_test5_x2.png",
            nameof(EncodeDecode_QrPng_MatchesOriginal_Mono8ScQpsk_InterleaveX2));
    }

    [Fact]
    public void InterleaveX2_DecodeSucceeds_AfterSilencingSomePass1Blocks()
    {
        // 2ブロックを強制し、2パス目の奇数・偶数入れ替え順（[1,0]）を必ず通す。
        var payload = new byte[9000];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 73) + 19);
        }

        var profileX2 = BaseProfile with
        {
            ActiveSubcarriers = 48,
            ModulationScheme = ModulationScheme.Qam64,
            SampleRate = 12000,
            BlockInterleaveFactor = 2
        };
        var profileX1 = profileX2 with { BlockInterleaveFactor = 1 };
        var codecX2 = new FileWavCodec(profileX2);
        var codecX1 = new FileWavCodec(profileX1);
        var tempPath = Path.Combine(Path.GetTempPath(), $"onta_test5_interleave2_{Guid.NewGuid():N}.bin");
        var wavPath = TestPaths.ResolveOutputPath($"test5_interleave_x2_degraded_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(tempPath, payload);

        try
        {
            var fileInfo = new FileInfo(tempPath);
            var frameRanges = new List<(TransmissionFrameKind Kind, int Start, int End)>();
            var emitted = 0;
            var frameStart = 0;
            var (left, right) = codecX2.EncodeFileToSamples(
                payload,
                fileInfo,
                onFrameTransmitted: kind =>
                {
                    frameRanges.Add((kind, frameStart, emitted));
                    frameStart = emitted;
                },
                onPcmChunk: (l, _) =>
                {
                    emitted += l.Length;
                },
                retainAllSamples: true);

            var blockCount = (payload.Length + 8191) / 8192;
            var bdFrames = frameRanges.Where(x => x.Kind == TransmissionFrameKind.Bd).ToArray();
            Assert.True(bdFrames.Length >= blockCount * 2, "Interleave x2 では BD が2パス分必要です。");

            // 1パス目（x1相当）の一部BD区間を1秒無音へ置換して、x1復号を失敗させる。
            var silenceTargets = Math.Min(2, blockCount);
            for (var i = 0; i < silenceTargets; i++)
            {
                var start = Math.Max(0, bdFrames[i].Start);
                var length = Math.Max(0, bdFrames[i].End - start);
                var silence = Math.Min(profileX2.SampleRate, length);
                if (silence <= 0)
                {
                    continue;
                }

                Array.Clear(left, start, silence);
                if (right.Length > 0)
                {
                    Array.Clear(right, start, Math.Min(silence, right.Length - start));
                }
            }

            WavWriter.WriteMono16(wavPath, profileX2.SampleRate, left, profileX2.SamplePeak);

            // x1失敗確認は第1パス相当の範囲だけを与えて高速化する。
            var firstPassEnd = bdFrames[blockCount - 1].End;
            var x1ProbeLen = Math.Min(left.Length, firstPassEnd + (profileX2.SampleRate / 2));
            var x1ProbeLeft = new Complex[x1ProbeLen];
            Array.Copy(left, 0, x1ProbeLeft, 0, x1ProbeLen);
            _ = Assert.Throws<InvalidDataException>(() =>
                codecX1.DecodePcmSamplesToFileBytes(x1ProbeLeft, Array.Empty<Complex>(), correctWow: false));

            var decodedX2 = codecX2.DecodeWavToFileBytes(wavPath, correctWow: false);
            Assert.Equal(payload, decodedX2);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (File.Exists(wavPath))
                {
                    File.Delete(wavPath);
                }
            }
            catch
            {
                // 一時ファイル削除失敗はテスト結果に影響させない。
            }
        }
    }

    [Fact]
    public void InterleaveX2_MiddleBlockError_DoesNotPreventFollowingBlocks()
    {
        // 3ブロック以上を作り、pass0 の中間 BLK だけ壊しても後続 BLK を含め復号完了できることを確認する。
        var payload = new byte[20000];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 29) + 7);
        }

        var profile = BaseProfile with
        {
            ActiveSubcarriers = 48,
            ModulationScheme = ModulationScheme.Qam64,
            SampleRate = 12000,
            BlockInterleaveFactor = 2
        };

        var codec = new FileWavCodec(profile);
        var tempPath = Path.Combine(Path.GetTempPath(), $"onta_test5_midblk_{Guid.NewGuid():N}.bin");
        var wavPath = TestPaths.ResolveOutputPath($"test5_midblk_resync_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(tempPath, payload);

        try
        {
            var fileInfo = new FileInfo(tempPath);
            var frameRanges = new List<(TransmissionFrameKind Kind, int Start, int End)>();
            var emitted = 0;
            var frameStart = 0;
            var (left, right) = codec.EncodeFileToSamples(
                payload,
                fileInfo,
                onFrameTransmitted: kind =>
                {
                    frameRanges.Add((kind, frameStart, emitted));
                    frameStart = emitted;
                },
                onPcmChunk: (l, _) =>
                {
                    emitted += l.Length;
                },
                retainAllSamples: true);

            var blockCount = (payload.Length + 8191) / 8192;
            Assert.True(blockCount >= 3, "このテストは 3 ブロック以上を前提とします。");

            var bdFrames = frameRanges.Where(x => x.Kind == TransmissionFrameKind.Bd).ToArray();
            Assert.True(bdFrames.Length >= blockCount * 2, "Interleave x2 のため BD は2パス分必要です。");

            // pass0 の中間ブロック（index=1）だけを強く劣化させる。
            var middlePass0 = bdFrames[1];
            var start = Math.Max(0, middlePass0.Start);
            var len = Math.Max(0, middlePass0.End - start);
            if (len > 0)
            {
                Array.Clear(left, start, len);
                if (right.Length > 0)
                {
                    Array.Clear(right, start, Math.Min(len, right.Length - start));
                }
            }

            WavWriter.WriteMono16(wavPath, profile.SampleRate, left, profile.SamplePeak);
            var decoded = codec.DecodeWavToFileBytes(wavPath, correctWow: false);
            Assert.Equal(payload, decoded);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (File.Exists(wavPath))
                {
                    File.Delete(wavPath);
                }
            }
            catch
            {
                // 一時ファイル削除失敗はテスト結果に影響させない。
            }
        }
    }

    [Fact]
    public void EncodeDecode_QrPng_MatchesOriginal_Mono40ScQpsk()
    {
        RoundTrip(
            BaseProfile with { ActiveSubcarriers = 40, BlockInterleaveFactor = 1 },
            "Sample1_test5_sc40.wav",
            "Sample1_test5_sc40.png",
            nameof(EncodeDecode_QrPng_MatchesOriginal_Mono40ScQpsk));
    }

    private static void RoundTrip(FileWavCodecProfile profile, string wavName, string restoredName, string testTitle)
    {
        var inputPath = TestPaths.ResolveInputPng();
        var wavPath = TestPaths.ResolveOutputPath(wavName);
        var restoredPath = TestPaths.ResolveOutputPath(restoredName);

        var codec = new FileWavCodec(profile);
        var decoded = codec.EncodeDecodeRoundTrip(inputPath, wavPath, restoredPath);
        var original = File.ReadAllBytes(inputPath);
        Assert.True(
            original.AsSpan().SequenceEqual(decoded),
            $"{testTitle}: restored bytes mismatch (wav={wavPath}).");
    }
}
