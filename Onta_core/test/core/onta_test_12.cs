using Onta.Core;
using Xunit;

namespace Onta.Core.Tests.Core;

/// <summary>
/// スプラッシュ画像（約 236KB）の後ろに乱数を足して 17 ブロックにした入力の、ステレオ 48SC / 16QAM 往復テストです。
/// 16 ブロックを超えるため、途中のファイルヘッダー再送（16 ブロックごと）を含む構成を通します。
/// </summary>
public sealed class OntaTest12
{
    private const string InputFileName = "onta_splash_512x256.png";
    private const int BlockPayloadBytes = FileWavCodec.DataBlockBytes;

    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 48,
        ModulationScheme: ModulationScheme.Qam16,
        ChannelMode: ChannelMode.Stereo);

    [Fact]
    public void EncodeDecode_SplashPng_MatchesOriginal_Stereo48Sc16Qam()
    {
        const string testTitle = "test12:" + nameof(EncodeDecode_SplashPng_MatchesOriginal_Stereo48Sc16Qam);
        var inputPath = TestPaths.ResolveOutputPath("onta_splash_test12_input.bin");
        var wavPath = TestPaths.ResolveOutputPath("onta_splash_test12.wav");
        var restoredPath = TestPaths.ResolveOutputPath("onta_splash_test12.bin");

        var original = BuildInput(File.ReadAllBytes(TestPaths.ResolveInputFile(InputFileName)));
        File.WriteAllBytes(inputPath, original);
        var blockCount = (original.Length + BlockPayloadBytes - 1) / BlockPayloadBytes;
        Assert.True(
            blockCount > FileWavCodec.FileHeaderRepeatIntervalBlocks,
            $"途中 FH 再送を通すには {FileWavCodec.FileHeaderRepeatIntervalBlocks} ブロック超が必要です: {blockCount}");

        var codec = new FileWavCodec(Profile);
        var decoded = codec.EncodeDecodeRoundTrip(inputPath, wavPath, restoredPath);

        PrintDecodeStageMetrics(codec.LastDecodeStageMetrics, testTitle, blockCount);

        Assert.Equal(original, decoded);
        Assert.Equal(original, File.ReadAllBytes(restoredPath));
        HistoryAssert.SaveSendAndAssertRegistered(testTitle, inputPath, wavPath);
    }

    /// <summary>
    /// 画像の後ろに乱数を足し、途中 FH を 1 回通す長さ（16 ブロック + 1000 バイト）の入力を作ります。
    /// </summary>
    /// <param name="image">スプラッシュ画像のバイト列。</param>
    /// <returns>往復テストの入力。</returns>
    private static byte[] BuildInput(byte[] image)
    {
        var length = Math.Max(image.Length, (FileWavCodec.FileHeaderRepeatIntervalBlocks * BlockPayloadBytes) + 1000);
        var input = new byte[length];
        new Random(12).NextBytes(input);
        Buffer.BlockCopy(image, 0, input, 0, image.Length);
        return input;
    }

    /// <summary>
    /// 復号段階メトリクスを標準出力へ書き出します。
    /// </summary>
    private static void PrintDecodeStageMetrics(DecodeStageMetrics metrics, string testTitle, int blockCount)
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
            $"[DECODE-STAGE] test={testTitle} blocks={blockCount} rsHeaderDecode={metrics.HeaderRsDecodeCount} dataDecoded={metrics.DataBlocksDecoded} dataAccepted={metrics.DataBlocksAccepted} acceptPercent={acceptPercent:F2}% viterbiAccepted={metrics.DataAcceptedViaViterbi} turboAccepted={metrics.DataAcceptedViaTurbo} fallbackUsed={metrics.DataFallbackUsed} attemptsPerBlock={attemptsPerBlock:F2}");
        Console.WriteLine(
            $"[DECODE-STAGE-RATE] test={testTitle} viterbiShare={viterbiPercent:F2}% turboShare={turboPercent:F2}%");
    }
}
