using System.Runtime.Intrinsics.X86;
using Onta.Core;
using Xunit;

namespace Onta.Core.Tests.Core;

public sealed class TurboSisoSimdTests
{
    /// <summary>
    /// AVX2 版 Max-Log-MAP SISO の外部 LLR が、スカラ版と完全に一致することを確かめます。
    /// </summary>
    [Theory]
    [InlineData(8192, 0.3, 0.0)]
    [InlineData(8192, 1.0, 2.0)]
    [InlineData(8192, 3.0, 8.0)]
    [InlineData(37, 1.5, 1.0)]
    [InlineData(1, 1.0, 0.5)]
    public void Avx2_MatchesScalar(int length, double noise, double aprioriScale)
    {
        if (!Avx2.IsSupported)
        {
            return;
        }

        var rng = new Random(20261003 + length);
        var systematic = new double[length];
        var parity = new double[length];
        var apriori = new double[length];
        for (var i = 0; i < length; i++)
        {
            var bit = rng.Next(2) == 0 ? 1.0 : -1.0;
            systematic[i] = (bit * 2.0) + (Gaussian(rng) * noise * 2.0);
            parity[i] = ((rng.Next(2) == 0 ? 1.0 : -1.0) * 2.0) + (Gaussian(rng) * noise * 2.0);
            apriori[i] = Gaussian(rng) * aprioriScale;
        }

        var expected = new double[length];
        var actual = new double[length];
        TurboEcc1024.DecodeSisoMaxLogMapScalar(systematic, parity, apriori, expected);
        TurboEcc1024.DecodeSisoMaxLogMapAvx2(systematic, parity, apriori, actual);
        for (var i = 0; i < length; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }

    /// <summary>
    /// 標準正規乱数を返します。
    /// </summary>
    /// <param name="rng">乱数源。</param>
    /// <returns>標準正規分布に従う値。</returns>
    private static double Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
