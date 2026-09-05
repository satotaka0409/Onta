using Onta.Core;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// ステレオ / 36サブキャリア / 64QAM の符号化→WAV→復号ラウンドトリップ試験です。
/// </summary>
public sealed class OntaTest2
{
    private static readonly FileWavCodecProfile Profile = new(
        FftSize: 128,
        CyclicPrefixLength: 32,
        ActiveSubcarriers: 36,
        ModulationScheme: ModulationScheme.Qam64);

    [Fact]
    public void EncodeDecode_QrPng_MatchesOriginal_Stereo36Sc64Qam()
    {
        var inputPath = TestPaths.ResolveInputPng();
        var outputDir = Path.GetDirectoryName(inputPath)!;
        var wavPath = Path.Combine(outputDir, "QR_326213_test2.wav");
        var restoredPath = Path.Combine(outputDir, "QR_326213_test2__.png");

        var original = File.ReadAllBytes(inputPath);
        var codec = new FileWavCodec(Profile);
        var decoded = codec.EncodeDecodeRoundTrip(inputPath, wavPath, restoredPath);

        Assert.Equal(original, decoded);
    }
}
