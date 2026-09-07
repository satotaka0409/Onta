using Onta.Core;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// ステレオ / 9サブキャリア / QPSK の符号化→WAV→復号ラウンドトリップ試験です。
/// </summary>
public sealed class OntaTest1
{
    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 9,
        ModulationScheme: ModulationScheme.Qpsk);

    [Fact]
    public void EncodeDecode_QrPng_MatchesOriginal_Stereo9ScQpsk()
    {
        var inputPath = TestPaths.ResolveInputPng();
        var wavPath = TestPaths.ResolveOutputPath("QR_326213_test1.wav");
        var restoredPath = TestPaths.ResolveOutputPath("QR_326213_test1.png");

        var original = File.ReadAllBytes(inputPath);
        var codec = new FileWavCodec(Profile);
        var decoded = codec.EncodeDecodeRoundTrip(inputPath, wavPath, restoredPath);

        Assert.Equal(original, decoded);
    }
}
