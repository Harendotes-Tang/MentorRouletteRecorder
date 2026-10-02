using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

public sealed class GameProcessSelectionTests
{
    [Fact]
    public void UnreadableStartTimeCannotGrantOwnership()
    {
        var selection = new GameProcessSelection(new GameProcessLocator(new UnknownTime(), new FakeGameFileReader()));
        var game = selection.Refresh();
        Assert.False(game.Running);
        Assert.Equal("IDENTITY_UNAVAILABLE", game.SelectionReason);
        var choice = Assert.Single(game.Processes);
        var error = Assert.Throws<CollectorException>(() => selection.Select(choice.ProcessId, choice.Token));
        Assert.Contains("启动时间", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AmbiguousChoiceStaysPausedUntilExplicitlySelected()
    {
        var processes = new FakeGameProcessProvider()
            .Add(GameProcessLocator.Dx11ProcessName, 1)
            .Add(GameProcessLocator.Dx11ProcessName, 2);
        var selection = new GameProcessSelection(new GameProcessLocator(processes, new FakeGameFileReader()));
        Assert.Equal("MULTIPLE", selection.Refresh().SelectionReason);
        processes.Clear();
        processes.Add(GameProcessLocator.Dx11ProcessName, 2);
        var remaining = selection.Refresh();
        Assert.True(remaining.SelectionRequired);
        Assert.Null(remaining.ProcessId);
        var choice = Assert.Single(remaining.Processes);
        selection.Select(choice.ProcessId, choice.Token);
        Assert.Equal(2, selection.Refresh().ProcessId);
    }

    private sealed class UnknownTime : IGameProcessProvider
    {
        public IReadOnlyList<GameProcessCandidate> ByName(string name) => name == GameProcessLocator.Dx11ProcessName
            ? new[] { new GameProcessCandidate(1, name, null, null, false) } : Array.Empty<GameProcessCandidate>();
    }
}
