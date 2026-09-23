using System.Numerics;
using Onta.Core;
using Xunit;

namespace Onta.Core.Tests.Core;

/// <summary>
/// OFDM FFT（AVX / AdvSimd 共通経路）の健全性です。
/// </summary>
public sealed class OntaTestFft
{
    [Fact]
    public void RectangularFft_1kHzSine_PeaksAtExpectedBin()
    {
        const int n = 256;
        const int sr = 44100;
        const double hz = 1000.0;
        var pcm = new Complex[n];
        for (var i = 0; i < n; i++)
        {
            pcm[i] = new Complex(Math.Sin(2.0 * Math.PI * hz * i / sr), 0.0);
        }

        var dest = new Complex[n];
        OfdmGenerator.ComputeRectangularForwardFftFromRealPcm(pcm, dest);

        var expected = (int)Math.Round(hz * n / sr);
        var bestBin = 1;
        var bestMag = -1.0;
        for (var bin = 1; bin < n / 2; bin++)
        {
            var mag = dest[bin].Magnitude;
            if (mag > bestMag)
            {
                bestMag = mag;
                bestBin = bin;
            }
        }

        Assert.InRange(bestBin, expected - 1, expected + 1);
    }
}
