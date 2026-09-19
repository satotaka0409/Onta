using Onta.View.Performance;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// 性能測定オシロの AUTO 起立トリガーです。
/// </summary>
public sealed class OntaTestScopeTrigger
{
    [Fact]
    public void Find_1kHzSine_LocksRisingEdgeAndSixCycles()
    {
        const int sr = 44100;
        const double hz = 1000.0;
        var samples = MakeSine(sr, hz, count: 4096, phase: 0.3);
        var cap = OscilloscopeTrigger.Find(samples, sr);

        Assert.True(cap.Triggered);
        Assert.InRange(cap.Length, 240, 320);
        Assert.InRange(cap.TriggerOffset, 40, 80);

        var i = cap.Start + cap.TriggerOffset;
        Assert.True(samples[i - 1] < cap.Level);
        Assert.True(samples[i] >= cap.Level);
    }

    [Fact]
    public void Find_ExplicitWindow_UsesRequestedLength()
    {
        const int sr = 44100;
        var samples = MakeSine(sr, 1000.0, count: 4096, phase: 0.3);
        var cap = OscilloscopeTrigger.Find(samples, sr, displaySamples: 441);

        Assert.True(cap.Triggered);
        Assert.Equal(441, cap.Length);
    }

    [Fact]
    public void Find_Silence_IsFreeRun()
    {
        var samples = new double[2048];
        var cap = OscilloscopeTrigger.Find(samples, 44100);

        Assert.False(cap.Triggered);
        Assert.Equal(OscilloscopeTrigger.DefaultDisplaySamples, cap.Length);
        Assert.Equal(0, cap.TriggerOffset);
    }

    /// <summary>
    /// 正弦波 PCM を生成します。
    /// </summary>
    private static double[] MakeSine(int sampleRate, double hz, int count, double phase)
    {
        var samples = new double[count];
        var w = 2.0 * Math.PI * hz / sampleRate;
        for (var i = 0; i < count; i++)
        {
            samples[i] = 0.7 * Math.Sin((w * i) + phase);
        }

        return samples;
    }
}
