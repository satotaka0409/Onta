using Onta.Core;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// ステレオ / 36サブキャリア / 64QAM の符号化→WAV→復号ラウンドトリップ試験です。
/// </summary>
public sealed class OntaTest2
{
    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 36,
        ModulationScheme: ModulationScheme.Qam64);

    [Fact]
    public void EncodeDecode_QrPng_MatchesOriginal_Stereo36Sc64Qam()
    {
        var inputPath = TestPaths.ResolveInputPng();
        var wavPath = TestPaths.ResolveOutputPath("Sample1_test2.wav");
        var restoredPath = TestPaths.ResolveOutputPath("Sample1_test2.png");

        var original = File.ReadAllBytes(inputPath);
        var codec = new FileWavCodec(Profile);
        var decoded = codec.EncodeDecodeRoundTrip(inputPath, wavPath, restoredPath);

        Assert.Equal(original, decoded);
    }
}
