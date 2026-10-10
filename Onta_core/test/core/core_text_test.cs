using System.Globalization;
using Onta.Core;
using Xunit;

namespace Onta.Core.Tests.Core;

/// <summary>
/// コアが作る表示文言（送信詳細の項目名など）が UI カルチャに従うことを検証するテストです。
/// </summary>
public sealed class CoreTextTest
{
    [Theory]
    [InlineData("ja-JP", "プリアンブル", "ファイルヘッダ", "(未受信)")]
    [InlineData("en-US", "Preamble", "File Header", "(Not received)")]
    [InlineData("fr-FR", "Preamble", "File Header", "(Not received)")]
    public void TransmissionSegments_FollowUiLanguage(string culture, string preamble, string fileHeader, string notReceived)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var estimate = FileWavCodec.EstimateTransmissionDuration(
                new FileWavCodecProfile(
                    ActiveSubcarriers: 16,
                    ModulationScheme: ModulationScheme.Qpsk,
                    ChannelMode: ChannelMode.Mono,
                    BlockInterleaveFactor: 1),
                fileSizeBytes: 20000);

            Assert.Equal(preamble, estimate.Segments[0].Label);
            Assert.Equal(fileHeader, estimate.Segments[1].Label);
            Assert.Equal(fileHeader, estimate.Segments[^2].Label);
            Assert.Equal(notReceived, CoreText.NotReceived);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
