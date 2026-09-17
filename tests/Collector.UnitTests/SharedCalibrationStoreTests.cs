using System.Text.Json;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Where downloaded codes and the fetch bookkeeping live (plans/shared-calibration.md §3.3, §4.1 step 6).
/// The store is strictly monotonic about codes - nothing a failed or empty fetch says removes one - and
/// tolerates a state file it cannot understand. Every test works in its own temp directory.
/// </summary>
public sealed class SharedCalibrationStoreTests : IDisposable
{
    private const string Build = SharedCalibrationIndexTests.Build;
    private const string OtherBuild = "2026.08.05.0000.0000";
    private static readonly string Template = new('0', 64);
    private static readonly string OtherTemplate = new('1', 64);
    private static readonly DateTimeOffset T0 = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "MentorRecorder.Tests", Guid.NewGuid().ToString("N"));
    private readonly SharedCalibrationStore _store;

    public SharedCalibrationStoreTests()
    {
        _store = new SharedCalibrationStore(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    private string Directory_ => SharedCalibrationStore.DirectoryFor(_root, Region.Cn, Build);

    private string StateFile => Path.Combine(Directory_, SharedCalibrationStore.StateFileName);

    private string FileOf(SharedCalibrationCandidate candidate) => Path.Combine(Directory_, candidate.CodeSha256[..12] + ".mrc");

    private static SharedCalibrationCandidate Candidate(int opcode, int submitters = 1, string build = Build)
    {
        var payload = ShareCodeTests.Payload(CalibrationMatchSource.Announcement, new ShareCodePop((ushort)opcode, Length: 64)) with
        {
            GameBuild = build,
        };
        return new SharedCalibrationCandidate(
            ShareCode.Sha256(payload), ShareCode.Encode(payload), payload, submitters, T0.AddDays(-submitters),
            SharedCalibrationIndexTests.Commit());
    }

    private static SharedCalibrationFetchResult Fetched(SharedFetchStatus status, params SharedCalibrationCandidate[] candidates)
    {
        var reached = status is not (SharedFetchStatus.IndexUnavailable or SharedFetchStatus.Cancelled);
        return new SharedCalibrationFetchResult(
            status,
            new[] { new SharedSourceAttempt(SharedCalibrationSource.GithubRaw, reached ? SharedFetchOutcome.Ok : SharedFetchOutcome.Timeout, reached ? 200 : null) },
            candidates,
            Array.Empty<SharedCodeDiscard>(),
            Array.Empty<SharedIndexSkip>(),
            Array.Empty<string>());
    }

    // ------------------------------------------------------------------------ layout

    [Fact]
    public void CodesLiveUnderRegionThenBuildInTheSharedCalibrationsDirectory()
    {
        Assert.Equal(Path.Combine(DatabasePaths.RootDirectory, "shared-calibrations"), SharedCalibrationStore.RootPath);
        Assert.Equal(Path.Combine("r", "cn", Build), SharedCalibrationStore.DirectoryFor("r", Region.Cn, Build));
        Assert.Equal(Path.Combine("r", "global", "2026-a.b"), SharedCalibrationStore.DirectoryFor("r", Region.Global, "2026_A.b"));
        // Windows drops a trailing dot from a directory name, which would merge two builds.
        Assert.Equal(Path.Combine("r", "cn", "2026-"), SharedCalibrationStore.DirectoryFor("r", Region.Cn, "2026."));
        Assert.Throws<ArgumentException>(() => SharedCalibrationStore.DirectoryFor("r", Region.Unknown, Build));
        Assert.Throws<ArgumentException>(() => SharedCalibrationStore.DirectoryFor("r", Region.Cn, ".."));
    }

    [Fact]
    public void ArgumentsThatCannotNameABuildOrCodeAreRefusedBeforeTouchingTheDisk()
    {
        Assert.Throws<ArgumentException>(() => _store.LoadCandidates(Region.Unknown, Build, Template));
        Assert.Throws<ArgumentException>(() => _store.LoadCandidates(Region.Cn, "..", Template));
        Assert.Throws<ArgumentException>(() => _store.LoadCandidates(Region.Cn, Build, "abc"));
        Assert.Throws<ArgumentException>(() => _store.RecordContradiction(Region.Cn, Build, Template, "abc", "session", T0));
        Assert.Throws<ArgumentException>(() => _store.RecordContradiction(Region.Cn, Build, Template, Template, " ", T0));
        Assert.False(Directory.Exists(_root));
    }

    // -------------------------------------------------------------------------- codes

    [Fact]
    public void AFetchedCodeIsStoredAsItsOwnFileAndLoadsBackAsACandidate()
    {
        var few = Candidate(0xF001, submitters: 1);
        var many = Candidate(0xF002, submitters: 3);

        var written = _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, few, many), T0);

        Assert.True(written.Recorded);
        Assert.Equal(2, written.CodesWritten);
        Assert.Empty(written.Refused);
        Assert.Equal(few.Code, File.ReadAllText(FileOf(few)));
        Assert.True(File.Exists(StateFile));
        Assert.Empty(Directory.GetFiles(Directory_, "*.tmp*"));

        var loaded = _store.LoadCandidates(Region.Cn, Build, Template);

        Assert.Equal(new[] { many.CodeSha256, few.CodeSha256 }, loaded.Select(candidate => candidate.CodeSha256));
        Assert.Equal(many.Code, loaded[0].Code);
        Assert.Equal(many.Payload, loaded[0].Payload);
        Assert.Equal(3, loaded[0].Submitters);
        Assert.Equal(many.FirstPublishedAtUtc, loaded[0].FirstPublishedAtUtc);
    }

    [Fact]
    public void CandidatesAreOnlyThoseForThisRegionBuildAndTemplate()
    {
        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, Candidate(0xF001)), T0);

        Assert.Single(_store.LoadCandidates(Region.Cn, Build, Template));
        Assert.Empty(_store.LoadCandidates(Region.Cn, Build, OtherTemplate));
        Assert.Empty(_store.LoadCandidates(Region.Cn, OtherBuild, Template));
        Assert.Empty(_store.LoadCandidates(Region.Global, Build, Template));
    }

    [Fact]
    public void OnlyACodeThatDecodesAndHashesToWhatItClaimsIsWritten()
    {
        var good = Candidate(0xF001);
        var wrongHash = Candidate(0xF002) with { CodeSha256 = new string('e', 64) };
        var garbage = Candidate(0xF003) with { Code = "MRC1.not-a-code" };
        var otherBuild = Candidate(0xF004, build: OtherBuild);

        var result = _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, good, wrongHash, garbage, otherBuild), T0);

        Assert.Equal(1, result.CodesWritten);
        Assert.Equal(3, result.Refused.Count);
        Assert.Contains(result.Refused, reason => reason == "eeeeeeeeeeee:HASH_MISMATCH");
        Assert.Contains(result.Refused, reason => reason.StartsWith(garbage.CodeSha256[..12] + ":UNDECODABLE:", StringComparison.Ordinal));
        Assert.Contains(result.Refused, reason => reason == otherBuild.CodeSha256[..12] + ":PAYLOAD_MISMATCH:game_build");
        Assert.Equal(new[] { FileOf(good) }, Directory.GetFiles(Directory_, "*.mrc"));
    }

    [Fact]
    public void AFailedOrEmptyFetchNeverRemovesWhatIsStored()
    {
        var code = Candidate(0xF001);
        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, code), T0);

        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.IndexUnavailable), T0.AddHours(1));
        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.CodesUnavailable), T0.AddHours(2));
        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Cancelled), T0.AddHours(3));

        Assert.Equal(code.CodeSha256, Assert.Single(_store.LoadCandidates(Region.Cn, Build, Template)).CodeSha256);
        var record = _store.LastFetch(Region.Cn, Build, Template)!;
        Assert.Equal(T0.AddHours(3), record.LastAttemptAtUtc);
        Assert.Equal(T0, record.LastSuccessAtUtc);
        Assert.Equal(SharedFetchStatus.Cancelled, record.Status);

        // An index that no longer lists the code is an answer, not a reason to forget it.
        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.NoneForBuild), T0.AddHours(4));

        Assert.Single(_store.LoadCandidates(Region.Cn, Build, Template));
        Assert.Equal(T0.AddHours(4), _store.LastFetch(Region.Cn, Build, Template)!.LastSuccessAtUtc);
    }

    [Fact]
    public void AStoredCodeIsNotRewrittenButACorruptFileOfTheSameNameIsReplaced()
    {
        var code = Candidate(0xF001);

        Assert.Equal(1, _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, code), T0).CodesWritten);
        Assert.Equal(0, _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, code), T0.AddHours(7)).CodesWritten);

        File.WriteAllText(FileOf(code), "scribbled over");
        Assert.Empty(_store.LoadCandidates(Region.Cn, Build, Template));

        Assert.Equal(1, _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, code), T0.AddHours(14)).CodesWritten);
        Assert.Equal(code.Code, File.ReadAllText(FileOf(code)));
    }

    [Fact]
    public void FilesThatAreNotWhatTheirNameSaysAreIgnoredOnLoad()
    {
        var code = Candidate(0xF001);
        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, code), T0);
        var renamed = Candidate(0xF002);
        var foreign = Candidate(0xF003, build: OtherBuild);
        File.WriteAllText(Path.Combine(Directory_, "000000000000.mrc"), renamed.Code);
        File.WriteAllText(Path.Combine(Directory_, foreign.CodeSha256[..12] + ".mrc"), foreign.Code);
        File.WriteAllText(Path.Combine(Directory_, "dddddddddddd.mrc"), new string(' ', SharedCalibrationClient.MaxCodeBytes) + renamed.Code);
        File.WriteAllText(Path.Combine(Directory_, "notes.txt"), renamed.Code);

        Assert.Equal(code.CodeSha256, Assert.Single(_store.LoadCandidates(Region.Cn, Build, Template)).CodeSha256);
    }

    [Fact]
    public void ACodeWhoseTextIsNotWellFormedUnicodeIsNeitherWrittenNorLoaded()
    {
        var code = Candidate(0xF001);
        var illFormed = ShareCodeTests.IllFormedCode();
        var damaged = new SharedCalibrationCandidate(
            new string('e', 64), illFormed, code.Payload, 1, T0, SharedCalibrationIndexTests.Commit());

        var write = _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, code, damaged), T0);
        File.WriteAllText(Path.Combine(Directory_, "dddddddddddd.mrc"), illFormed);

        Assert.Equal(1, write.CodesWritten);
        Assert.Equal(new[] { "eeeeeeeeeeee:UNDECODABLE:E_SHARE_CODE_JSON" }, write.Refused);
        Assert.False(File.Exists(Path.Combine(Directory_, "eeeeeeeeeeee.mrc")));
        Assert.Equal(code.CodeSha256, Assert.Single(_store.LoadCandidates(Region.Cn, Build, Template)).CodeSha256);
    }

    // ------------------------------------------------------------------ fetch records

    [Fact]
    public void TheLastAttemptIsRememberedPerTemplateWithEachSourcesOutcome()
    {
        var failed = new SharedCalibrationFetchResult(
            SharedFetchStatus.CodesUnavailable,
            new[]
            {
                new SharedSourceAttempt(SharedCalibrationSource.GithubRaw, SharedFetchOutcome.DnsOrConnect, null, "NAME_RESOLUTION_ERROR"),
                new SharedSourceAttempt(SharedCalibrationSource.CdnPrimary, SharedFetchOutcome.Ok, 200),
            },
            Array.Empty<SharedCalibrationCandidate>(),
            new[]
            {
                new SharedCodeDiscard(new string('e', 64), "HASH_MISMATCH", new[]
                {
                    new SharedSourceAttempt(SharedCalibrationSource.CdnPrimary, SharedFetchOutcome.Malformed, 200, "HASH_MISMATCH"),
                    new SharedSourceAttempt(SharedCalibrationSource.GithubRaw, SharedFetchOutcome.Timeout),
                }),
            },
            Array.Empty<SharedIndexSkip>(),
            Array.Empty<string>());

        _store.RecordFetch(Region.Cn, Build, Template, failed, T0);
        var record = _store.LastFetch(Region.Cn, Build, Template);

        Assert.NotNull(record);
        Assert.Equal(Template, record!.TemplateSha256);
        Assert.Equal(T0, record.LastAttemptAtUtc);
        Assert.Null(record.LastSuccessAtUtc);
        Assert.Equal(SharedFetchStatus.CodesUnavailable, record.Status);
        Assert.Equal(failed.IndexAttempts, record.IndexAttempts);
        var discard = Assert.Single(record.Discards);
        Assert.Equal(failed.Discards[0].CodeSha256, discard.CodeSha256);
        Assert.Equal("HASH_MISMATCH", discard.Reason);
        Assert.Equal(failed.Discards[0].Attempts, discard.Attempts);
        Assert.Null(_store.LastFetch(Region.Cn, Build, OtherTemplate));
        Assert.Null(_store.LastFetch(Region.Global, Build, Template));
        Assert.False(SharedCalibrationStore.ShouldAutoFetch(record.LastAttemptAtUtc, T0.AddHours(1), manual: false));
        Assert.True(SharedCalibrationStore.ShouldAutoFetch(_store.LastFetch(Region.Cn, Build, OtherTemplate)?.LastAttemptAtUtc, T0.AddHours(1), manual: false));
    }

    [Fact]
    public void AFetchThatNeverWentOutRecordsNothingAndCreatesNoDirectory()
    {
        var result = _store.RecordFetch(Region.Cn, Build, Template, SharedCalibrationFetchResult.Disabled, T0);

        Assert.False(result.Recorded);
        Assert.Equal(0, result.CodesWritten);
        Assert.False(Directory.Exists(_root));
        Assert.Null(_store.LastFetch(Region.Cn, Build, Template));
    }

    public static TheoryData<string, TimeSpan?, bool, bool> Throttle() => new()
    {
        { "never fetched", null, false, true },
        { "fetched just now", TimeSpan.Zero, false, false },
        { "fetched 5 h 59 min ago", TimeSpan.FromHours(6) - TimeSpan.FromMinutes(1), false, false },
        { "fetched exactly 6 h ago", TimeSpan.FromHours(6), false, true },
        { "fetched a day ago", TimeSpan.FromDays(1), false, true },
        { "a manual check a minute later", TimeSpan.FromMinutes(1), true, true },
        { "a stamp from the future after the clock moved back", TimeSpan.FromHours(-2), false, true },
    };

    [Theory]
    [MemberData(nameof(Throttle))]
    public void AnAutomaticFetchIsDueEverySixHoursAndAManualCheckAlways(string why, TimeSpan? ago, bool manual, bool due)
    {
        DateTimeOffset? last = ago is { } elapsed ? T0 - elapsed : null;

        Assert.True(due == SharedCalibrationStore.ShouldAutoFetch(last, T0, manual), why);
        Assert.Equal(TimeSpan.FromHours(6), SharedCalibrationStore.AutoFetchInterval);
    }

    [Fact]
    public void ACodeTheIndexRevokedIsNotLoadedUntilAReadableIndexStopsRevokingIt()
    {
        var code = Candidate(0xF001);
        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, code), T0);

        _store.RecordFetch(Region.Cn, Build, Template,
            Fetched(SharedFetchStatus.NoneForBuild) with { RevokedCodeSha256s = new[] { code.CodeSha256 } }, T0.AddHours(7));
        Assert.Empty(_store.LoadCandidates(Region.Cn, Build, Template));

        // A fetch that never read an index says nothing either way about revocation.
        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.IndexUnavailable), T0.AddHours(14));
        Assert.Empty(_store.LoadCandidates(Region.Cn, Build, Template));

        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.NoneForBuild), T0.AddHours(21));
        Assert.Single(_store.LoadCandidates(Region.Cn, Build, Template));
        Assert.True(File.Exists(FileOf(code)));
    }

    // -------------------------------------------------------------------- rejections

    [Fact]
    public void ContradictionsCountDistinctSessionsAndRejectAtTheSecond()
    {
        var code = Candidate(0xF001);
        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, code), T0);

        Assert.Equal(new SharedContradictionResult(1, false, true),
            _store.RecordContradiction(Region.Cn, Build, Template, code.CodeSha256, "session-a", T0));
        Assert.Equal(new SharedContradictionResult(1, false, true),
            _store.RecordContradiction(Region.Cn, Build, Template, code.CodeSha256, "session-a", T0.AddMinutes(5)));
        Assert.False(_store.IsRejected(Region.Cn, Build, Template, code.CodeSha256));
        Assert.Single(_store.LoadCandidates(Region.Cn, Build, Template));

        Assert.Equal(new SharedContradictionResult(2, true, true),
            _store.RecordContradiction(Region.Cn, Build, Template, code.CodeSha256, "session-b", T0.AddHours(1)));

        Assert.Equal(2, SharedCalibrationStore.RejectionThreshold);
        Assert.True(_store.IsRejected(Region.Cn, Build, Template, code.CodeSha256));
        Assert.Empty(_store.LoadCandidates(Region.Cn, Build, Template));
        // Keyed by template too: under another template the same code is still worth a try.
        Assert.False(_store.IsRejected(Region.Cn, Build, OtherTemplate, code.CodeSha256));
        // Rejection hides a code; it never deletes it.
        Assert.True(File.Exists(FileOf(code)));
    }

    [Fact]
    public void ClearingRejectionsForABuildLetsItsCodesBeTriedAgain()
    {
        var code = Candidate(0xF001);
        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, code), T0);
        _store.RecordContradiction(Region.Cn, Build, Template, code.CodeSha256, "session-a", T0);
        _store.RecordContradiction(Region.Cn, Build, Template, code.CodeSha256, "session-b", T0);
        Assert.True(_store.IsRejected(Region.Cn, Build, Template, code.CodeSha256));

        Assert.True(_store.ClearRejections(Region.Cn, Build));

        Assert.False(_store.IsRejected(Region.Cn, Build, Template, code.CodeSha256));
        Assert.Single(_store.LoadCandidates(Region.Cn, Build, Template));
        Assert.Equal(1, _store.RecordContradiction(Region.Cn, Build, Template, code.CodeSha256, "session-a", T0.AddHours(1)).Sessions);
        Assert.NotNull(_store.LastFetch(Region.Cn, Build, Template));
        Assert.True(_store.ClearRejections(Region.Global, Build));
    }

    // ------------------------------------------------------------- damaged state file

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[]")]
    [InlineData("{\"schema_version\":1,\"\\ud800\":1,\"fetches\":[]}")]
    [InlineData("{\"schema_version\":1,\"revoked\":[\"\\udc00\"],\"codes\":[{\"code_sha256\":\"\\ud800\"}]}")]
    [InlineData("{\"schema_version\":99,\"fetches\":[]}")]
    [InlineData("{\"schema_version\":1,\"fetches\":7,\"codes\":{\"x\":1},\"revoked\":[1,2],\"rejections\":[{\"template_sha256\":5,\"sessions\":\"a\"}]}")]
    public void AStateFileThatCannotBeUnderstoodReadsAsEmptyAndNeverCostsACode(string text)
    {
        var code = Candidate(0xF001, submitters: 5);
        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, code), T0);
        File.WriteAllText(StateFile, text);

        Assert.Null(_store.LastFetch(Region.Cn, Build, Template));
        Assert.False(_store.IsRejected(Region.Cn, Build, Template, code.CodeSha256));
        var loaded = Assert.Single(_store.LoadCandidates(Region.Cn, Build, Template));
        Assert.Equal(0, loaded.Submitters);

        var contradiction = _store.RecordContradiction(Region.Cn, Build, Template, code.CodeSha256, "session-a", T0.AddHours(1));

        Assert.True(contradiction.Persisted);
        Assert.Equal(1, contradiction.Sessions);
        using (JsonDocument.Parse(File.ReadAllText(StateFile)))
        {
        }

        Assert.True(File.Exists(FileOf(code)));
    }

    [Fact]
    public void AStateFileThatCannotBeOpenedIsNeitherTrustedNorOverwritten()
    {
        var code = Candidate(0xF001);
        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, code), T0);
        var before = File.ReadAllText(StateFile);

        using (new FileStream(StateFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(_store.RecordContradiction(Region.Cn, Build, Template, code.CodeSha256, "session-a", T0).Persisted);
            Assert.False(_store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, code), T0.AddHours(7)).Recorded);
            Assert.False(_store.ClearRejections(Region.Cn, Build));
            Assert.Null(_store.LastFetch(Region.Cn, Build, Template));
            Assert.Single(_store.LoadCandidates(Region.Cn, Build, Template));
        }

        Assert.Equal(before, File.ReadAllText(StateFile));
    }

    // -------------------------------------------------------------------------- inert

    [Fact]
    public void TheInertStoreRemembersNothingAndTouchesNoDisk()
    {
        var inert = SharedCalibrationStore.Inert;

        Assert.IsNotType<SharedCalibrationStore>(inert);
        Assert.False(inert.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, Candidate(0xF001)), T0).Recorded);
        Assert.Empty(inert.LoadCandidates(Region.Cn, Build, Template));
        Assert.Null(inert.LastFetch(Region.Cn, Build, Template));
        Assert.False(inert.RecordContradiction(Region.Cn, Build, Template, Template, "session-a", T0).Persisted);
        Assert.False(inert.IsRejected(Region.Cn, Build, Template, Template));
        Assert.False(inert.ClearRejections(Region.Cn, Build));
        Assert.Equal(SharedPublication.Unknown, inert.Publication(Region.Cn, Build, Template));
    }

    // ------------------------------------------------------------------------ publication (plan §18.5)

    [Fact]
    public void PublicationReadsTheLastIndexOnlyAndNeverTheNetwork()
    {
        var stored = Candidate(0xF001);
        var discarded = Candidate(0xF002);
        var revoked = Candidate(0xF003);
        var unknown = Candidate(0xF004);
        Assert.Equal(SharedPublication.Unknown, _store.Publication(Region.Cn, Build, stored.CodeSha256));

        var result = Fetched(SharedFetchStatus.Ok, stored) with
        {
            Discards = new[]
            {
                new SharedCodeDiscard(discarded.CodeSha256, "TIMEOUT",
                    new[] { new SharedSourceAttempt(SharedCalibrationSource.GithubRaw, SharedFetchOutcome.Timeout) }),
            },
            RevokedCodeSha256s = new[] { revoked.CodeSha256 },
        };
        _store.RecordFetch(Region.Cn, Build, Template, result, T0);

        Assert.Equal(SharedPublication.Published, _store.Publication(Region.Cn, Build, stored.CodeSha256));
        // Listed by the index though its file never arrived: published all the same.
        Assert.Equal(SharedPublication.Published, _store.Publication(Region.Cn, Build, discarded.CodeSha256));
        Assert.Equal(SharedPublication.Revoked, _store.Publication(Region.Cn, Build, revoked.CodeSha256));
        Assert.Equal(SharedPublication.Unknown, _store.Publication(Region.Cn, Build, unknown.CodeSha256));
        // Another build's bookkeeping says nothing about this one.
        Assert.Equal(SharedPublication.Unknown, _store.Publication(Region.Cn, OtherBuild, stored.CodeSha256));
        Assert.Throws<ArgumentException>(() => _store.Publication(Region.Cn, Build, "not-a-sha"));
    }

    /// <summary>Plan §18.6: the index's conflict mark survives in the state file and puts a stored code last.</summary>
    [Fact]
    public void AConflictingCodeIsOfferedLastHoweverManySubmitters()
    {
        var conflicting = Candidate(0xF001, submitters: 9) with { Conflicting = true };
        var lone = Candidate(0xF002, submitters: 1);
        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, conflicting, lone), T0);

        var offered = _store.LoadCandidates(Region.Cn, Build, Template);

        Assert.Equal(new[] { lone.CodeSha256, conflicting.CodeSha256 }, offered.Select(candidate => candidate.CodeSha256).ToArray());
        Assert.True(offered[1].Conflicting);
        Assert.False(offered[0].Conflicting);
        Assert.Contains("\"conflicting\": true", File.ReadAllText(StateFile), StringComparison.Ordinal);
    }

    /// <summary>Plan §18.4: the settled mark names one profile document and survives the other bookkeeping.</summary>
    [Fact]
    public void SettledIsKeyedByTheProfileDocumentAndSurvivesOtherWrites()
    {
        var document = new string('a', 64);
        var other = new string('b', 64);
        Assert.False(_store.IsSettled(Region.Cn, Build, document));

        Assert.True(_store.RecordSettled(Region.Cn, Build, document, T0));

        Assert.True(_store.IsSettled(Region.Cn, Build, document));
        Assert.False(_store.IsSettled(Region.Cn, Build, other));
        Assert.False(_store.IsSettled(Region.Cn, OtherBuild, document));
        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, Candidate(0xF001)), T0.AddHours(1));
        _store.ClearRejections(Region.Cn, Build);
        Assert.True(_store.IsSettled(Region.Cn, Build, document));
        // A newly written document replaces the mark; the old one is no longer settled.
        Assert.True(_store.RecordSettled(Region.Cn, Build, other, T0.AddHours(2)));
        Assert.False(_store.IsSettled(Region.Cn, Build, document));
        Assert.True(_store.IsSettled(Region.Cn, Build, other));
        Assert.Throws<ArgumentException>(() => _store.IsSettled(Region.Cn, Build, "not-a-sha"));
    }

    [Fact]
    public void AFetchThatReachedNoSourceChangesNoPublication()
    {
        var code = Candidate(0xF001);
        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.Ok, code), T0);

        _store.RecordFetch(Region.Cn, Build, Template, Fetched(SharedFetchStatus.IndexUnavailable), T0.AddHours(1));

        Assert.Equal(SharedPublication.Published, _store.Publication(Region.Cn, Build, code.CodeSha256));
    }
}
