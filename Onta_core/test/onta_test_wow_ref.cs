using Onta.View.Performance;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// 性能測定ワウの送信周波数ロックです。
/// </summary>
public sealed class OntaTestWowReference
{
    [Fact]
    public void FindNearest_1005Hz_SnapsTo1kHz()
    {
        var nearest = PerformanceWowReference.FindNearest(
            1005,
            PerformanceWowReference.ToneFrequenciesHz);
        Assert.Equal(1000, nearest);
    }

    [Fact]
    public void TryLock_HoldsThroughOnePercentWow()
    {
        var locked = 0.0;
        Assert.True(PerformanceWowReference.TryLock(
            1000,
            PerformanceWowReference.ToneFrequenciesHz,
            ref locked));
        Assert.Equal(1000, locked);

        Assert.True(PerformanceWowReference.TryLock(
            1010,
            PerformanceWowReference.ToneFrequenciesHz,
            ref locked));
        Assert.Equal(1000, locked);
        Assert.InRange(PerformanceWowReference.ToWowPercent(1010, locked), 0.9, 1.1);
    }

    [Fact]
    public void TryLock_RelocksWhenToneJumps()
    {
        var locked = 1000.0;
        Assert.True(PerformanceWowReference.TryLock(
            8000,
            PerformanceWowReference.ToneFrequenciesHz,
            ref locked));
        Assert.Equal(8000, locked);
    }

    [Fact]
    public void InterpolatePeakOffset_SymmetricNeighbors_IsZero()
    {
        Assert.Equal(0, PerformanceWowReference.InterpolatePeakOffset(1, 2, 1), 6);
    }
}
