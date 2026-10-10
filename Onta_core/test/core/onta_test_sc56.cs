using Onta.Core;
using Xunit;

namespace Onta.Core.Tests.Core;

/// <summary>
/// ステレオ 56SC / 16QAM（高域 G グループは 8PSK）の、15 ブロック（229,876 バイト）の往復テストです。
/// </summary>
public sealed class OntaTestSc56
{
    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 56,
        ModulationScheme: ModulationScheme.Qam16,
        ChannelMode: ChannelMode.Stereo);

    /// <summary>
    /// 雑音なしの WAV を通して、元のバイト列が戻ること。
    /// </summary>
    [Fact]
    public void EncodeDecode_MatchesOriginal_Stereo56Sc16Qam()
    {
        var inputPath = TestPaths.ResolveOutputPath("onta_sc56_input.bin");
        var wavPath = TestPaths.ResolveOutputPath("onta_sc56.wav");
        var restoredPath = TestPaths.ResolveOutputPath("onta_sc56.bin");

        var original = new byte[229876];
        new Random(56).NextBytes(original);
        File.WriteAllBytes(inputPath, original);

        var codec = new FileWavCodec(Profile);
        var decoded = codec.EncodeDecodeRoundTrip(inputPath, wavPath, restoredPath);

        Assert.Equal(original, decoded);
    }
}
