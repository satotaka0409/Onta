using Onta.Core;
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
        Assert.Equal((16, ModulationScheme.Qpsk), FileWavCodec.ResolveInterleavePassModulation(1, 32, ModulationScheme.Qam64));
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
    }

    [Fact]
    public void ResolveFftSize_IsAlways256()
    {
        Assert.Equal(256, OfdmConfig.ResolveFftSize(8, ChannelMode.Mono));
        Assert.Equal(256, OfdmConfig.ResolveFftSize(16, ChannelMode.Stereo));
        Assert.Equal(256, OfdmConfig.ResolveFftSize(24, ChannelMode.Mono));
        Assert.Equal(256, OfdmConfig.ResolveFftSize(32, ChannelMode.Stereo));
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
        Assert.Equal(500.0 + (8 * OfdmConfig.CarrierSpacingHz), OfdmConfig.LeftCarrierHzSc24(9), 6);
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
