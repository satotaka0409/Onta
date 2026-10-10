using Onta.Core;
using Xunit;

namespace Onta.Core.Tests.Core;

/// <summary>
/// SC-72（Group A〜I）のテストです。
/// 1. Group G/H/I の変調 1 段下げを含めた 1 シンボルのビット数
/// 2. ステレオ 72SC / 16QAM のファイル往復
/// </summary>
public sealed class OntaTest17
{
    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 72,
        ModulationScheme: ModulationScheme.Qam16,
        ChannelMode: ChannelMode.Stereo);

    /// <summary>
    /// 1. 72SC / QPSK は 18 パイロット + 54 データで、A〜F（36 本）が QPSK、G〜I（18 本）が BPSK の 90 bit/シンボルになること。
    /// </summary>
    [Fact]
    public void BitsPerOfdmSymbol_Sc72Qpsk_AppliesGroupGHIDowngrade()
    {
        var config = new OfdmConfig(
            fftSize: OfdmConfig.ResolveFftSize(72, ChannelMode.Stereo),
            activeSubcarriers: 72,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Qpsk,
            channelMode: ChannelMode.Stereo,
            pilotSpacing: 8,
            randomSeed: 7,
            carrierGrid: OfdmCarrierGrid.Sc24Family);

        var ofdm = new OfdmGenerator(config);

        Assert.Equal(90, ofdm.BitsPerOfdmSymbol);
        Assert.Equal(OfdmCarrierGrid.Sc24Family, config.CarrierGrid);
    }

    /// <summary>
    /// 2. ステレオ 72SC / 16QAM で符号化した WAV から元のファイルを復元できること。
    /// </summary>
    [Fact]
    public void EncodeDecode_QrPng_MatchesOriginal_Stereo72ScQam16()
    {
        const string testTitle = "test17:" + nameof(EncodeDecode_QrPng_MatchesOriginal_Stereo72ScQam16);
        var inputPath = TestPaths.ResolveInputPng();
        var wavPath = TestPaths.ResolveOutputPath("Sample1_test17.wav");
        var restoredPath = TestPaths.ResolveOutputPath("Sample1_test17.png");

        var original = File.ReadAllBytes(inputPath);
        var codec = new FileWavCodec(Profile);
        var decoded = codec.EncodeDecodeRoundTrip(inputPath, wavPath, restoredPath);

        Assert.Equal(original, decoded);
        HistoryAssert.SaveSendAndAssertRegistered(testTitle, inputPath, wavPath);
    }
}
