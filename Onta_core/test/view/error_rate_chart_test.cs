using LiveChartsCore.Defaults;
using Onta.Core;
using Onta.View.Core;
using Xunit;

namespace Onta.Core.Tests.View;

/// <summary>
/// 誤り訂正率チャートの時間軸の検証です。
/// </summary>
public sealed class ErrorRateChartTest
{
    /// <summary>
    /// 一時停止した間の時間は横軸に入らず、次のサンプルは止めた時点のすぐ後に並ぶこと。
    /// </summary>
    [Fact]
    public void Pause_DoesNotAdvanceTimeAxisUntilNextSample()
    {
        var chart = new ErrorRateChartModel();
        chart.AddSample(1.0, CoreEccDecoderKind.Viterbi);
        chart.Pause();
        Thread.Sleep(600);
        chart.Tick();
        chart.AddSample(2.0, CoreEccDecoderKind.Viterbi);

        var points = ((IEnumerable<ObservablePoint>)chart.Series[0].Values!).ToList();
        Assert.Equal(2, points.Count);
        Assert.InRange(points[1].X!.Value - points[0].X!.Value, 0.0, 0.3);
        Assert.InRange(chart.XAxes[0].MaxLimit!.Value, 0.0, 0.3);
    }

    /// <summary>
    /// 一時停止しなければ、サンプルの間隔は経過時間どおりに開くこと。
    /// </summary>
    [Fact]
    public void WithoutPause_TimeAxisFollowsElapsedTime()
    {
        var chart = new ErrorRateChartModel();
        chart.AddSample(1.0, CoreEccDecoderKind.Viterbi);
        Thread.Sleep(600);
        chart.AddSample(2.0, CoreEccDecoderKind.Viterbi);

        var points = ((IEnumerable<ObservablePoint>)chart.Series[0].Values!).ToList();
        Assert.Equal(2, points.Count);
        Assert.True(points[1].X!.Value - points[0].X!.Value >= 0.5);
    }
}
