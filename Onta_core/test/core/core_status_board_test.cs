using Onta.Core;
using Xunit;

namespace Onta.Core.Tests.Core;

public sealed class CoreStatusBoardTests
{
    [Fact]
    public void Read_ReturnsProgressErrorRateAndIq()
    {
        var board = new CoreExecutionStatusBoard(iqCapacity: 4);
        board.BeginRun("demo.wav");
        board.SetProgress(new CoreProgressInfo(CoreFrameKind.Bd, 2, 0, 1, 4, 40.0));
        board.SetErrorRate(12.5, CoreFrameKind.Bd);
        board.PushIq(0.7, -0.2);
        board.PushIq(-0.5, 0.4);

        var status = board.Read();
        Assert.True(status.IsRunning);
        Assert.Equal(CoreFrameKind.Bd, status.Progress.CurrentFrame);
        Assert.Equal(2, status.Progress.CurrentBlockIndex);
        Assert.Equal(40.0, status.Progress.ProgressPercent);
        Assert.Equal(12.5, status.ErrorRate.LatestPercent);
        Assert.Single(status.ErrorRateSamples);
        Assert.Equal(12.5, status.ErrorRateSamples[0].LatestPercent);
        Assert.Equal(2, status.IqGraph.Points.Count);
        Assert.Equal(0.7, status.IqGraph.Points[0].I, 6);
    }

    [Fact]
    public void Read_ConsumesErrorRateSamplesOnce()
    {
        var board = new CoreExecutionStatusBoard();
        board.BeginRun("demo.wav");
        board.SetErrorRate(10.0, CoreFrameKind.Bh, CoreEccDecoderKind.Viterbi);
        board.SetErrorRate(20.0, CoreFrameKind.Bh, CoreEccDecoderKind.ReedSolomon);

        var first = board.Read();
        Assert.Equal(2, first.ErrorRateSamples.Count);

        var second = board.Read();
        Assert.Empty(second.ErrorRateSamples);
        Assert.Equal(20.0, second.ErrorRate.LatestPercent);
    }

    [Fact]
    public void IqSnapshot_IsNotClearedByRead()
    {
        var board = new CoreExecutionStatusBoard(iqCapacity: 8);
        board.BeginRun("demo.wav");
        board.PushIq(0.1, 0.2);
        board.PushIq(0.3, 0.4);

        _ = board.Read();
        var again = board.Read();
        Assert.Equal(2, again.IqGraph.Points.Count);
    }
}
