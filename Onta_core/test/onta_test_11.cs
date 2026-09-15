using Onta.Core;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// ステレオ 48SC / QPSK の往復テストです。
/// Group E/F の変調1段下げ（QPSK→BPSK）を含む SC-48 構成を検証します。
/// </summary>
public sealed class OntaTest11
{
    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 48,
        ModulationScheme: ModulationScheme.Qpsk,
        ChannelMode: ChannelMode.Stereo);

    [Fact]
    public void BitsPerOfdmSymbol_Sc48Qpsk_AppliesGroupEFDowngrade()
    {
        var config = new OfdmConfig(
            fftSize: OfdmConfig.ResolveFftSize(48, ChannelMode.Stereo),
            activeSubcarriers: 48,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Qpsk,
            channelMode: ChannelMode.Stereo,
            pilotSpacing: 8,
            randomSeed: 7,
            carrierGrid: OfdmCarrierGrid.Sc24Family);

        var ofdm = new OfdmGenerator(config);

        // 48SC: 12 pilot + 36 data。A–D は QPSK、E/F は BPSK → 60bit/symbol。
        Assert.Equal(60, ofdm.BitsPerOfdmSymbol);
        Assert.Equal(Enumerable.Range(1, 48), OfdmConfig.ResolveConceptualLeftBins(48));
        Assert.Equal((byte)5, OfdmConfig.ResolveSubcarrierGroupId(41));
        Assert.Equal((byte)5, OfdmConfig.ResolveSubcarrierGroupId(48));
    }

    [Fact]
    public void EncodeDecode_QrPng_MatchesOriginal_Stereo48ScQpsk()
    {
        const string testTitle = "test11:" + nameof(EncodeDecode_QrPng_MatchesOriginal_Stereo48ScQpsk);
        var inputPath = TestPaths.ResolveInputPng();
        var wavPath = TestPaths.ResolveOutputPath("Sample1_test11.wav");
        var restoredPath = TestPaths.ResolveOutputPath("Sample1_test11.png");

        var original = File.ReadAllBytes(inputPath);
        var codec = new FileWavCodec(Profile);
        var decoded = codec.EncodeDecodeRoundTrip(inputPath, wavPath, restoredPath);

        PrintDecodeStageMetrics(codec.LastDecodeStageMetrics, testTitle);

        Assert.Equal(original, decoded);
        HistoryAssert.SaveSendAndAssertRegistered(testTitle, inputPath, wavPath);
    }

    /// <summary>
    /// 復号段階メトリクスを標準出力へ書き出します。
    /// </summary>
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
