using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

public sealed class GameProcessSelectionTests
{
    private const string GamePath = @"D:\SdoA\game\ffxiv_dx11.exe";

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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ASoleRestartFromTheSameInstallationContinuesWithAFreshToken(bool reusedPid, bool observedExit)
    {
        var processes = new Processes(Client(42, 0));
        var selection = Selector(processes);
        var old = Assert.Single(selection.Refresh().Processes);
        selection.Select(old.ProcessId, old.Token);
        if (observedExit)
        {
            processes.Set();
            Assert.Equal("EXITED", selection.Refresh().SelectionReason);
            Assert.False(selection.Refresh().Running);
        }
        var nextPid = reusedPid ? 42 : 43;
        processes.Set(Client(nextPid, 1) with { ExecutablePath = GamePath.ToUpperInvariant() });
        var restarted = selection.Refresh();
        Assert.True(restarted.Running);
        Assert.False(restarted.SelectionRequired);
        Assert.Equal(nextPid, restarted.ProcessId);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(1), restarted.StartedAtUtc);
        Assert.NotEqual(old.Token, Assert.Single(restarted.Processes).Token);
        Assert.Throws<CollectorException>(() => selection.Select(old.ProcessId, old.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherClientAlreadySeenWhileSelectedNeverInheritsTheSelection(bool initiallyUnidentified)
    {
        var processes = new Processes(Client(42, 0));
        var selection = Selector(processes);
        selection.Refresh();
        processes.Set(Client(42, 0), Client(99, 1) with
        {
            StartedAtUtc = initiallyUnidentified ? null : DateTimeOffset.UnixEpoch.AddMinutes(1),
        });
        Assert.Equal(42, selection.Refresh().ProcessId);
        processes.Set(Client(99, 1));
        Assert.Equal("EXITED", selection.Refresh().SelectionReason);
        Assert.False(selection.Refresh().Running);
        var other = Assert.Single(selection.Refresh().Processes);
        selection.Select(other.ProcessId, other.Token);
        Assert.Equal(99, selection.Refresh().ProcessId);
    }

    [Fact]
    public void ExplicitChoiceAmongMultipleClientsDoesNotMakeItsExistingPeerAReplacement()
    {
        var processes = new Processes(Client(42, 0), Client(99, 1));
        var selection = Selector(processes);
        var original = selection.Refresh().Processes.Single(p => p.ProcessId == 42);
        selection.Select(original.ProcessId, original.Token);
        processes.Set(Client(99, 1));
        Assert.Equal("EXITED", selection.Refresh().SelectionReason);
        Assert.False(selection.Refresh().Running);
    }

    [Fact]
    public void MultipleRestartCandidatesStayPausedAfterOneExits()
    {
        var processes = new Processes(Client(42, 0));
        var selection = Selector(processes);
        selection.Refresh();
        processes.Set(Client(43, 1), Client(44, 2) with { ExecutablePath = @"D:\Other\ffxiv_dx11.exe" });
        Assert.False(selection.Refresh().Running);
        processes.Set(Client(43, 1));
        Assert.True(selection.Refresh().SelectionRequired);
        Assert.False(selection.Refresh().Running);
        var choice = Assert.Single(selection.Refresh().Processes);
        selection.Select(choice.ProcessId, choice.Token);
        Assert.Equal(43, selection.Refresh().ProcessId);
    }

    [Theory]
    [InlineData("different_path")]
    [InlineData("unreadable_path")]
    [InlineData("unreadable_start")]
    [InlineData("older_start")]
    [InlineData("same_start")]
    [InlineData("original_path_unknown")]
    public void AnUnconfirmedRestartKeepsWaiting(string reason)
    {
        var processes = new Processes(Client(42, 0) with
        {
            ExecutablePath = reason == "original_path_unknown" ? null : GamePath,
        });
        var selection = Selector(processes);
        selection.Refresh();
        processes.Set(Client(43, 1) with
        {
            ExecutablePath = reason switch
            {
                "different_path" => @"D:\Other\game\ffxiv_dx11.exe",
                "unreadable_path" => null,
                _ => GamePath,
            },
            StartedAtUtc = reason switch
            {
                "unreadable_start" => null,
                "older_start" => DateTimeOffset.UnixEpoch.AddMinutes(-1),
                "same_start" => DateTimeOffset.UnixEpoch,
                _ => DateTimeOffset.UnixEpoch.AddMinutes(1),
            },
        });
        Assert.Equal("EXITED", selection.Refresh().SelectionReason);
        Assert.False(selection.Refresh().Running);
    }

    [Fact]
    public void APathLearnedAfterStartupAllowsTheNextSingleClientRestart()
    {
        var processes = new Processes(Client(42, 0) with { ExecutablePath = null });
        var selection = Selector(processes);
        selection.Refresh();
        processes.Set(Client(42, 0));
        selection.Refresh();
        processes.Set(Client(43, 1));
        Assert.Equal(43, selection.Refresh().ProcessId);
    }

    [Fact]
    public void ValidationCanStillSelectOneNewClientBesideAnExistingPeerAfterAnOrdinaryRescan()
    {
        var processes = new Processes(Client(42, 0));
        var selection = Selector(processes);
        selection.Refresh();
        processes.Set(Client(42, 0), Client(99, 1));
        var previous = selection.Refresh();
        processes.Set(Client(99, 1), Client(43, 2));
        Assert.False(selection.Refresh().Running);
        Assert.Equal(43, selection.RefreshAfterValidationRestart(previous).ProcessId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidationRemembersAmbiguousNewClientsSeenByAnOrdinaryRescan(bool unknownPath)
    {
        var processes = new Processes(Client(42, 0));
        var selection = Selector(processes);
        var previous = selection.Refresh();
        processes.Set(Client(43, 1), Client(44, 2) with { ExecutablePath = unknownPath ? null : GamePath });
        Assert.False(selection.Refresh().Running);
        processes.Set(Client(43, 1));
        var waiting = selection.RefreshAfterValidationRestart(previous);
        Assert.False(waiting.Running);
        Assert.Equal("MULTIPLE", waiting.SelectionReason);
        Assert.False(selection.RefreshAfterValidationRestart(previous).Running);
    }

    private static GameProcessSelection Selector(Processes processes) =>
        new(new GameProcessLocator(processes, new FakeGameFileReader()));

    private static GameProcessCandidate Client(int pid, int minute) => new(
        pid, GameProcessLocator.Dx11ProcessName, DateTimeOffset.UnixEpoch.AddMinutes(minute), GamePath, false);

    private sealed class Processes(params GameProcessCandidate[] clients) : IGameProcessProvider
    {
        private GameProcessCandidate[] _clients = clients;
        public void Set(params GameProcessCandidate[] clients) => _clients = clients;
        public IReadOnlyList<GameProcessCandidate> ByName(string name) =>
            name == GameProcessLocator.Dx11ProcessName ? _clients : [];
    }

    private sealed class UnknownTime : IGameProcessProvider
    {
        public IReadOnlyList<GameProcessCandidate> ByName(string name) => name == GameProcessLocator.Dx11ProcessName
            ? new[] { new GameProcessCandidate(1, name, null, null, false) } : Array.Empty<GameProcessCandidate>();
    }
}
