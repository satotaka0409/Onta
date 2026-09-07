using Onta.Core;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// ブロック時系列インターリーブ ×2 / ×3 の送出順とラウンドトリップ試験です（data_struct.mdc）。
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
        Assert.Equal([0, 1, 2, 3], FileWavCodec.GetBlockEmissionOrder(4, passIndex: 2));
    }

    [Fact]
    public void EncodeDecode_QrPng_MatchesOriginal_Mono9ScQpsk_InterleaveX2()
    {
        RoundTrip(BaseProfile with { BlockInterleaveFactor = 2 }, "QR_326213_test5_x2.wav", "QR_326213_test5_x2.png");
    }

    [Fact]
    public void EncodeDecode_QrPng_MatchesOriginal_Mono9ScQpsk_InterleaveX3()
    {
        RoundTrip(BaseProfile with { BlockInterleaveFactor = 3 }, "QR_326213_test5_x3.wav", "QR_326213_test5_x3.png");
    }

    private static void RoundTrip(FileWavCodecProfile profile, string wavName, string restoredName)
    {
        var inputPath = TestPaths.ResolveInputPng();
        var wavPath = TestPaths.ResolveOutputPath(wavName);
        var restoredPath = TestPaths.ResolveOutputPath(restoredName);

        var original = File.ReadAllBytes(inputPath);
        var codec = new FileWavCodec(profile);
        var decoded = codec.EncodeDecodeRoundTrip(inputPath, wavPath, restoredPath);

        Assert.Equal(original, decoded);
    }
}
