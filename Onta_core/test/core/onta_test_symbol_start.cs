using Xunit;

namespace Onta.Core.Tests.Core;

/// <summary>
/// データ部先頭シンボルの FFT 窓位置（EVM が小さい区間の中央）の選び方を確認します。
/// </summary>
public sealed class OntaTestSymbolStart
{
    private static readonly int[] Offsets = Enumerable.Range(-17, 35).Select(k => k * 4).ToArray();

    /// <summary>
    /// 実テープ受信で R の BD が全滅した時の EVM（BH 側がゆるやかにしか増えない）でも、CP 内の平坦部の中央を選ぶこと。
    /// </summary>
    [Fact]
    public void ShallowSlopeBeforeBlock_SelectsCpCenter()
    {
        double[] costs =
        [
            0.055, 0.060, 0.067, 0.062, 0.057, 0.052, 0.051, 0.051, 0.049, 0.048, 0.038, 0.041, 0.014,
            0.000, 0.000, 0.000, 0.000, 0.003,
            0.038, 0.059, 0.076, 0.088, 0.152, 0.114, 0.087, 0.128, 0.125, 0.103, 0.104, 0.163, 0.254, 1.331, 1.410, 0.596, 1.229,
        ];

        AssertInsideCp(OfdmGenerator.SelectLowEvmRegionCenter(Offsets, costs));
    }

    /// <summary>
    /// 探索範囲の端に桁違いに大きい EVM が 1 点あっても、区間が全体へ広がらないこと。
    /// </summary>
    [Fact]
    public void HugeOutlier_DoesNotWidenRegion()
    {
        double[] costs =
        [
            0.375, 0.208, 0.183, 0.138, 0.116, 0.074, 0.056, 0.049, 0.049, 0.049, 0.042, 0.046, 0.027,
            0.000, 0.000, 0.000, 0.000, 0.002,
            0.062, 0.069, 0.077, 0.113, 0.211, 0.454, 0.323, 0.550, 0.236, 0.282, 0.476, 168.751, 0.526, 0.309, 0.229, 0.488, 0.331,
        ];

        AssertInsideCp(OfdmGenerator.SelectLowEvmRegionCenter(Offsets, costs));
    }

    /// <summary>
    /// 雑音で CP 内の平坦部がばらつくときも、最小点（CP 端寄り）ではなく平坦部の中央付近を選ぶこと。
    /// </summary>
    [Fact]
    public void NoisyPlateau_SelectsNearCenter()
    {
        double[] costs =
        [
            0.60, 0.55, 0.50, 0.45, 0.40, 0.35, 0.30, 0.25, 0.20, 0.16, 0.12, 0.09, 0.07,
            0.055, 0.050, 0.052, 0.048, 0.045,
            0.08, 0.11, 0.15, 0.20, 0.25, 0.30, 0.35, 0.40, 0.45, 0.50, 0.55, 0.60, 0.65, 0.70, 0.75, 0.80, 0.85,
        ];

        AssertInsideCp(OfdmGenerator.SelectLowEvmRegionCenter(Offsets, costs));
    }

    /// <summary>
    /// 選んだオフセットが CP（16 サンプル）内の平坦部 [-16, 0] に収まっていることを確認します。
    /// </summary>
    /// <param name="center">選ばれたオフセット。</param>
    private static void AssertInsideCp(int center) => Assert.InRange(center, -14, -2);
}
