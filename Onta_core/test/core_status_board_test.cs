using Onta.Core;
using Xunit;

namespace Onta.Core.Tests;

public sealed class CoreStatusBoardTests
{
    [Fact]
    public void Query_ReturnsProgressErrorRateAndIq()
    {
        var board = new CoreExecutionStatusBoard(iqCapacity: 4);
        board.BeginRun("demo.wav");
        board.SetProgress(new CoreProgressInfo(CoreFrameKind.Bd, 2, 0, 1, 4, 40.0));
        board.SetErrorRate(12.5, CoreFrameKind.Bd);
        board.PushIq(0.7, -0.2);
        board.PushIq(-0.5, 0.4);

        var status = board.Query();
        Assert.True(status.IsRunning);
        Assert.Equal(CoreFrameKind.Bd, status.Progress.CurrentFrame);
        Assert.Equal(2, status.Progress.CurrentBlockIndex);
        Assert.Equal(40.0, status.Progress.ProgressPercent);
        Assert.Equal(12.5, status.ErrorRate.LatestPercent);
        Assert.Equal(2, status.IqGraph.Points.Count);
        Assert.Equal(0.7, status.IqGraph.Points[0].I, 6);
    }
}

