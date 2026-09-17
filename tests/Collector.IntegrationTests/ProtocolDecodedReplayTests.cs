using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Replay;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// Every decoded fixture replayed through the real parser, the real state machine and a real
/// database. The expected outcome of each one is written out by hand from
/// docs/protocol-profile-format.md and docs/state-machine.md rather than copied from what the
/// code happens to produce, so a change in parsing behaviour has to be argued for here first.
/// </summary>
public sealed class ProtocolDecodedReplayTests
{
    /// <summary>What one decoded fixture is expected to produce.</summary>
    /// <param name="Fixture">Fixture id and file stem.</param>
    /// <param name="FinalState">State the machine must end in.</param>
    /// <param name="RunCount">Runs the replay must have touched.</param>
    /// <param name="AttemptCount">Expected attempt_count.</param>
    /// <param name="CompletedCount">Expected completed_count.</param>
    /// <param name="Results">Expected result of each run, in creation order.</param>
    /// <param name="ParseOk">Messages that must have produced a semantic event.</param>
    /// <param name="ParseFailed">Messages that must have been refused.</param>
    /// <param name="ParserDuplicates">Messages the parser must have seen as repeats.</param>
    /// <param name="ErrorCode">Refusal code the fixture is built to trigger, when any.</param>
    /// <param name="BuildMatched">Whether the fixture build matches the profile build.</param>
    /// <param name="ParserIgnored">Messages of an undeclared opcode the parser must have ignored.</param>
    /// <param name="Profile">Invented profile the fixture is replayed against.</param>
    public sealed record Expectation(
        string Fixture,
        RunState FinalState,
        int RunCount,
        int AttemptCount,
        int CompletedCount,
        RunResult[] Results,
        long ParseOk,
        long ParseFailed,
        long ParserDuplicates = 0,
        string? ErrorCode = null,
        bool BuildMatched = true,
        long ParserIgnored = 0,
        string Profile = "synthetic-v1");

    private static readonly Expectation[] Expectations =
    {
        // Pop, zone, job, victory: the only path to COMPLETED.
        new("synthetic_complete", RunState.Completed, 1, 1, 1,
            new[] { RunResult.Completed }, ParseOk: 4, ParseFailed: 0),

        // Leaving the zone without a victory can only ever be LEFT_OR_ABANDONED.
        new("synthetic_left", RunState.LeftOrAbandoned, 1, 1, 0,
            new[] { RunResult.LeftOrAbandoned }, ParseOk: 4, ParseFailed: 0),

        // No entry means no attempt, so this never reaches a rate denominator.
        new("synthetic_cancelled", RunState.CancelledBeforeEntry, 1, 0, 0,
            new[] { RunResult.CancelledBeforeEntry }, ParseOk: 2, ParseFailed: 0),

        // Both messages parse; neither creates anything, because the roulette is not ours.
        new("synthetic_non_mentor", RunState.Idle, 0, 0, 0,
            Array.Empty<RunResult>(), ParseOk: 2, ParseFailed: 0),

        // Seven messages parse, three of them repeat, and one run comes out.
        new("synthetic_duplicates", RunState.Completed, 1, 1, 1,
            new[] { RunResult.Completed }, ParseOk: 7, ParseFailed: 0, ParserDuplicates: 3),

        // A wrong length is refused outright: no entry, so the run stays matched and is not
        // an attempt. The state machine never saw the message at all.
        new("synthetic_len_mismatch", RunState.MentorMatched, 1, 0, 0,
            new[] { RunResult.Unknown }, ParseOk: 2, ParseFailed: 1, ErrorCode: "E_LEN_MISMATCH"),

        // A field that would read past a short payload is refused, so the duty never ends.
        new("synthetic_offset_oob", RunState.EnteredDuty, 1, 1, 0,
            new[] { RunResult.Unknown }, ParseOk: 2, ParseFailed: 1, ErrorCode: "E_OFFSET_OOB"),

        // An undeclared opcode is counted as ignored, not refused; the run completes normally.
        new("synthetic_unknown_opcode", RunState.Completed, 1, 1, 1,
            new[] { RunResult.Completed }, ParseOk: 3, ParseFailed: 0, ParserIgnored: 1),

        // roulette_id = 0 violates the declared constraint, so no run is ever created.
        new("synthetic_constraint_fail", RunState.Idle, 0, 0, 0,
            Array.Empty<RunResult>(), ParseOk: 1, ParseFailed: 1, ErrorCode: "E_FIELD_CONSTRAINT"),

        // A different client build removes the profile entirely: everything is refused.
        new("synthetic_build_mismatch", RunState.Idle, 0, 0, 0,
            Array.Empty<RunResult>(), ParseOk: 0, ParseFailed: 3,
            ErrorCode: "E_PROFILE_UNSUPPORTED", BuildMatched: false),

        // The CN shape: five messages parse, the entry marker names nothing, and the run
        // still ends up naming its duty because of the territory announcement before it.
        // The second zone change ends it; an offline synthetic binding is always treated as
        // outcome-capable, so it closes as LEFT_OR_ABANDONED rather than UNKNOWN.
        new("synthetic_territory_duty", RunState.LeftOrAbandoned, 1, 1, 0,
            new[] { RunResult.LeftOrAbandoned }, ParseOk: 5, ParseFailed: 0,
            Profile: "synthetic-cn-shape-v1"),
    };

    /// <summary>Expectations as xunit theory data.</summary>
    public static TheoryData<string> FixtureNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var expectation in Expectations)
            {
                data.Add(expectation.Fixture);
            }

            return data;
        }
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "decoded", name + ".decoded.json");

    private static string ProfilePath => ProfilePathFor("synthetic-v1");

    /// <summary>Path of one invented profile in the build output.</summary>
    /// <param name="profileId">Profile identifier and file stem.</param>
    private static string ProfilePathFor(string profileId) => Path.Combine(
        AppContext.BaseDirectory, "protocol-profiles", "synthetic", profileId + ".json");

    private static string TempDatabase() => Path.Combine(
        Path.GetTempPath(),
        "MentorRecorder.DecodedIt",
        Guid.NewGuid().ToString("N"),
        "replay.db");

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void DecodedReplayProducesTheHandComputedOutcome(string name)
    {
        var expected = Expectations.Single(row => row.Fixture == name);

        var result = DecodedReplayRunner.Run(
            FixturePath(name), ProfilePathFor(expected.Profile), TempDatabase());

        Assert.Equal(expected.BuildMatched, result.BuildMatched);
        Assert.Equal(expected.FinalState, result.FinalState);
        Assert.Equal(expected.RunCount, result.Runs.Count);
        Assert.Equal(expected.Results, result.Runs.Select(run => run.Result));
        Assert.Equal(expected.AttemptCount, result.Statistics.AttemptCount);
        Assert.Equal(expected.CompletedCount, result.Statistics.CompletedCount);
        Assert.Equal(expected.ParseOk, result.Parser.ParseOk);
        Assert.Equal(expected.ParseFailed, result.Parser.ParseFailed);
        Assert.Equal(expected.ParserDuplicates, result.Parser.Duplicates);
        Assert.Equal(expected.ParserIgnored, result.Parser.Ignored);
        Assert.Equal(expected.RunCount, result.Writes.RunsCreated);
        Assert.Equal(expected.RunCount, result.Writes.RevisionsAppended);

        if (expected.ErrorCode is null)
        {
            Assert.Empty(result.Parser.Errors);
        }
        else
        {
            Assert.Contains(result.Parser.Errors, error => error.Code == expected.ErrorCode);
        }
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void ReplayingTheSameFixtureTwiceWritesNothingTheSecondTime(string name)
    {
        var expected = Expectations.Single(row => row.Fixture == name);
        var databasePath = TempDatabase();

        var profile = ProfilePathFor(expected.Profile);
        var first = DecodedReplayRunner.Run(FixturePath(name), profile, databasePath);
        var second = DecodedReplayRunner.Run(FixturePath(name), profile, databasePath);

        Assert.Equal(first.Runs, second.Runs);
        Assert.Equal(0, second.Writes.RunsCreated);
        Assert.Equal(0, second.Writes.EventsAppended);
        Assert.Equal(expected.AttemptCount, second.Statistics.AttemptCount);
        Assert.Equal(expected.CompletedCount, second.Statistics.CompletedCount);
        Assert.Equal(expected.RunCount > 0, second.Writes.IdempotentReplay);
    }

    /// <summary>
    /// Bytes to a named duty, without a content id anywhere in the traffic. This is the shape
    /// the CN client produces: the pop names only the roulette, the entry marker names
    /// nothing at all, and the territory arrives 50 ms before it in a message of its own. The
    /// stored run must still come out with a duty name and a job name, which is the whole
    /// point of docs/state-machine.md section 3.11.
    /// </summary>
    [Fact]
    public void ATerritoryAnnouncementAndALoginJobFillInTheStoredRun()
    {
        var result = DecodedReplayRunner.Run(
            FixturePath("synthetic_territory_duty"),
            ProfilePathFor("synthetic-cn-shape-v1"),
            TempDatabase());

        var run = Assert.Single(result.Runs);
        Assert.Equal(800001, run.TerritoryId);
        Assert.Equal("样例迷宫挑战 A", run.DutyName);

        // The territory is an observation; the content id it maps to is not. Writing the
        // mapped id into the column every duty statistic aggregates on would file the attempt
        // under a duty the traffic never named (review finding M-5), so the run keeps the
        // territory, the display name, and a note of where its identity came from.
        Assert.Null(run.ContentId);
        Assert.Equal(DutySource.Territory, run.DutySource);
        Assert.Equal(19, run.JobId);
        Assert.Equal("骑士", run.JobName);

        // The duty name came from a local display mapping, never from protocol evidence, so
        // it may not raise the confidence of a run whose outcome was never observed.
        Assert.Equal(RunResult.LeftOrAbandoned, run.Result);
        Assert.NotEqual(DetectionConfidence.High, run.DetectionConfidence);
        Assert.Empty(result.Parser.Errors);
    }

    [Fact]
    public void ABuildMismatchWritesNoRunAndLeavesTheStateMachineUntouched()
    {
        var result = DecodedReplayRunner.Run(
            FixturePath("synthetic_build_mismatch"), ProfilePath, TempDatabase());

        Assert.False(result.ProfileUsable);
        Assert.Null(result.ProfileId);
        Assert.Equal("UNSUPPORTED", result.ProfileStatus);
        Assert.Empty(result.Runs);
        Assert.Empty(result.ParsedEvents);
        Assert.Empty(result.Transitions);
        Assert.Equal(RunState.Idle, result.FinalState);

        // The refusals are the parser's, not the state machine's: nothing ever reached it.
        Assert.Equal(0, result.Writes.ParserErrors);
        Assert.Equal(3, result.Parser.ParseFailed);
    }

    [Fact]
    public void UndeclaredOpcodesNeverBecomeRunEventsAndAreIgnoredNotRefused()
    {
        var result = DecodedReplayRunner.Run(
            FixturePath("synthetic_unknown_opcode"), ProfilePath, TempDatabase());

        Assert.Equal(3, result.ParsedEvents.Count);
        Assert.DoesNotContain(result.ParsedEvents, row => row.EventType.Contains("60000", StringComparison.Ordinal));
        // Ordinary undeclared traffic is not a refusal: no error row, no parser_errors write.
        Assert.Empty(result.Parser.Errors);
        Assert.Equal(1, result.Parser.Ignored);
        Assert.Equal(0, result.Writes.ParserErrors);
    }

    [Fact]
    public void ADecodedFixtureCannotBeReplayedWithAFailClosedRegionProfile()
    {
        var regionProfile = Path.Combine(
            AppContext.BaseDirectory, "protocol-profiles", "cn", "cn-unsupported.json");

        var result = DecodedReplayRunner.Run(
            FixturePath("synthetic_complete"), regionProfile, TempDatabase());

        Assert.False(result.BuildMatched);
        Assert.False(result.ProfileUsable);
        Assert.Empty(result.Runs);
        Assert.Equal(4, result.Parser.ParseFailed);
    }
}
