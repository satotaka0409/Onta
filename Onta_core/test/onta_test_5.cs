using Onta.Core;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// ブロック送信順序と変調ダウングレード規則の検証テストです。
/// </summary>
public sealed class OntaTest5
{
    private static readonly FileWavCodecProfile BaseProfile = new(
        ActiveSubcarriers: 9,
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
        Assert.Equal((9, ModulationScheme.Qpsk), FileWavCodec.ResolveInterleavePassModulation(0, 9, ModulationScheme.Qpsk));
        Assert.Equal((9, ModulationScheme.Bpsk), FileWavCodec.ResolveInterleavePassModulation(1, 9, ModulationScheme.Qpsk));
        Assert.Equal((9, ModulationScheme.Bpsk), FileWavCodec.ResolveInterleavePassModulation(1, 18, ModulationScheme.Bpsk));
        Assert.Equal((18, ModulationScheme.Qpsk), FileWavCodec.ResolveInterleavePassModulation(1, 27, ModulationScheme.Qam16));
        Assert.Equal((18, ModulationScheme.Qpsk), FileWavCodec.ResolveInterleavePassModulation(1, 36, ModulationScheme.Qam64));
    }

    [Fact]
    public void ConceptualLeftBins_MatchModulationGroupTable()
    {
        Assert.Equal(Enumerable.Range(10, 9), OfdmConfig.ResolveGroupBLeftBins());
        Assert.Equal(Enumerable.Range(10, 9), OfdmConfig.ResolveConceptualLeftBins(9));
        Assert.Equal(Enumerable.Range(1, 18), OfdmConfig.ResolveConceptualLeftBins(18));
        Assert.Equal(Enumerable.Range(1, 27), OfdmConfig.ResolveConceptualLeftBins(27));
        Assert.Equal(Enumerable.Range(1, 36), OfdmConfig.ResolveConceptualLeftBins(36));
    }

    [Fact]
    public void HeaderOfdmConfig_UsesGroupBConceptualBins()
    {
        var groupB = OfdmConfig.ResolveGroupBLeftBins();
        var fft = OfdmConfig.ResolveFftSize(activeSubcarriers: 9, ChannelMode.Mono);
        var config = new OfdmConfig(
            fftSize: fft,
            activeSubcarriers: groupB.Length,
            cyclicPrefixLength: 32,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Bpsk,
            channelMode: ChannelMode.Mono,
            conceptualLeftBins: groupB,
            carrierGrid: OfdmCarrierGrid.Sc9Family);

        Assert.Equal(groupB, config.ConceptualLeftBins);
        Assert.Equal(Enumerable.Range(10, 9), config.ConceptualLeftBins);
        Assert.Equal(128, config.FftSize);
        Assert.Equal(OfdmCarrierGrid.Sc9Family, config.CarrierGrid);
        Assert.Equal(440.0, OfdmConfig.LeftCarrierHzSc9(0), 3);
        Assert.Equal(OfdmConfig.DeltaF27() * 1.3, OfdmConfig.DeltaF9(), 6);
    }

    [Fact]
    public void Sc9Family_LeftStartsAt440_AndSpacingIs1_3xSc27()
    {
        Assert.Equal(440.0, OfdmConfig.LeftCarrierHzSc9(0), 6);
        var df9 = OfdmConfig.DeltaF9();
        var df27 = OfdmConfig.DeltaF27();
        Assert.Equal(df27 * 1.3, df9, 9);
        Assert.Equal(440.0 + (9 * df9), OfdmConfig.LeftCarrierHzSc9(9), 6);
        Assert.Equal(OfdmConfig.LeftCarrierHzSc9(9) + (df9 / 2.0), OfdmConfig.RightCarrierHzSc9(9), 6);
    }

    [Fact]
    public void EncodeDecode_QrPng_MatchesOriginal_Mono9ScQpsk_InterleaveX2()
    {
        RoundTrip(
            BaseProfile with { BlockInterleaveFactor = 2 },
            "Sample1_test5_x2.wav",
            "Sample1_test5_x2.png",
            nameof(EncodeDecode_QrPng_MatchesOriginal_Mono9ScQpsk_InterleaveX2));
    }

    private static void RoundTrip(FileWavCodecProfile profile, string wavName, string restoredName, string testTitle)
    {
        var inputPath = TestPaths.ResolveInputPng();
        var wavPath = TestPaths.ResolveOutputPath(wavName);
        var restoredPath = TestPaths.ResolveOutputPath(restoredName);

        var original = File.ReadAllBytes(inputPath);
        var codec = new FileWavCodec(profile);
        var decoded = codec.EncodeDecodeRoundTrip(inputPath, wavPath, restoredPath);

        PrintDecodeStageMetrics(codec.LastDecodeStageMetrics, testTitle);

        Assert.Equal(original, decoded);
    }

    private static void PrintDecodeStageMetrics(DecodeStageMetrics metrics, string testTitle)
    {
        var accepted = Math.Max(1, metrics.DataBlocksAccepted);
        var decoded = Math.Max(1, metrics.DataBlocksDecoded);
        var viterbiPercent = metrics.DataAcceptedViaViterbi * 100.0 / accepted;
        var turboPercent = metrics.DataAcceptedViaTurbo * 100.0 / accepted;
        var acceptPercent = metrics.DataBlocksAccepted * 100.0 / decoded;
        var attemptsPerBlock = metrics.DataBlocksDecoded > 0
            ? metrics.DataTotalAttempts / (double)metrics.DataBlocksDecoded
            : 0.0;
        Console.WriteLine(
            $"[DECODE-STAGE] test={testTitle} rsHeaderDecode={metrics.HeaderRsDecodeCount} dataDecoded={metrics.DataBlocksDecoded} dataAccepted={metrics.DataBlocksAccepted} acceptPercent={acceptPercent:F2}% viterbiAccepted={metrics.DataAcceptedViaViterbi} turboAccepted={metrics.DataAcceptedViaTurbo} fallbackUsed={metrics.DataFallbackUsed} attemptsPerBlock={attemptsPerBlock:F2}");
        Console.WriteLine(
            $"[DECODE-STAGE-RATE] test={testTitle} viterbiShare={viterbiPercent:F2}% turboShare={turboPercent:F2}%");
    }
}

