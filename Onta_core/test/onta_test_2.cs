using Onta.Core;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// ステレオ 36SC / 64QAM の往復テストです。
/// </summary>
public sealed class OntaTest2
{
    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 36,
        ModulationScheme: ModulationScheme.Qam64);

    [Fact]
    public void EncodeDecode_QrPng_MatchesOriginal_Stereo36Sc64Qam()
    {
        const string testTitle = "test2:" + nameof(EncodeDecode_QrPng_MatchesOriginal_Stereo36Sc64Qam);
        var inputPath = TestPaths.ResolveInputPng();
        var wavPath = TestPaths.ResolveOutputPath("Sample1_test2.wav");
        var restoredPath = TestPaths.ResolveOutputPath("Sample1_test2.png");

        var original = File.ReadAllBytes(inputPath);
        var codec = new FileWavCodec(Profile);
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

