namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// <c>--pipe</c> exists so a test harness can run a Collector on a throw-away database
/// without ever meeting the Collector the user's Desktop is talking to. The serving mode is
/// the only mode that accepts it, and its argument must be a bare pipe name.
/// </summary>
public sealed class PipeOptionTests
{
    [Fact]
    public void ServeAcceptsABarePipeNameAndDefaultsToNone()
    {
        var options = CommandLineOptions.Parse(
            new[] { "--serve", "--db", "x.db", "--pipe", "MentorRecorder.test-abc.v1" });
        Assert.Equal(CollectorMode.Serve, options.Mode);
        Assert.Equal("MentorRecorder.test-abc.v1", options.PipeName);

        // Omitted means the per-user pipe, which is what the Desktop launches.
        Assert.Null(CommandLineOptions.Parse(new[] { "--serve" }).PipeName);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--pipe-name-only")]
    [InlineData("--capture-doctor")]
    [InlineData("--list-profiles")]
    public void EveryOneShotModeRefusesThePipeSwitch(string mode)
    {
        Assert.Throws<FormatException>(
            () => CommandLineOptions.Parse(new[] { mode, "--pipe", "MentorRecorder.test.v1" }));
    }

    [Fact]
    public void ReplayRefusesThePipeSwitchToo()
    {
        Assert.Throws<FormatException>(
            () => CommandLineOptions.Parse(new[] { "--replay", "f.json", "--pipe", "x" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\.\pipe\MentorRecorder.test.v1")]
    [InlineData("a/b")]
    [InlineData("has space")]
    [InlineData("tab\there")]
    public void ANameThatIsNotABarePipeNameIsRefused(string value)
    {
        Assert.Throws<FormatException>(
            () => CommandLineOptions.Parse(new[] { "--serve", "--pipe", value }));
    }

    [Fact]
    public void AnOverlongNameIsRefused()
    {
        var value = new string('a', CommandLineOptions.MaxPipeNameLength + 1);
        Assert.Throws<FormatException>(
            () => CommandLineOptions.Parse(new[] { "--serve", "--pipe", value }));
        Assert.Equal(
            CommandLineOptions.MaxPipeNameLength,
            CommandLineOptions.Parse(
                new[] { "--serve", "--pipe", new string('a', CommandLineOptions.MaxPipeNameLength) })
                .PipeName!.Length);
    }

    [Fact]
    public void AMissingNameIsRefusedRatherThanTreatedAsDefault()
    {
        Assert.Throws<FormatException>(() => CommandLineOptions.Parse(new[] { "--serve", "--pipe" }));
    }
}
