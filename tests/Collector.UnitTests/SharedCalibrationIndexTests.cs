using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Sharing;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The public repository's <c>index.json</c> is untrusted
/// input: a strict schema, a bounded entry count and fixed formats; unknown fields are ignored and
/// anything malformed is skipped with a reason instead of being guessed at. Choosing what to download
/// is a pure function of the entries.
/// </summary>
public sealed class SharedCalibrationIndexTests
{
    internal const string Build = "2026.09.01.0000.0000";

    internal static string Sha(char digit) => new(digit, 64);

    internal static string Sha(int number) => number.ToString("x64");

    internal static string Commit(char digit = 'c') => new(digit, 40);

    internal static JsonObject Entry(
        string sha,
        int submitters = 1,
        string published = "2026-09-15T08:00:00Z",
        string region = "CN",
        string build = Build,
        bool revoked = false,
        string matchSource = "ANNOUNCEMENT",
        string? commit = null) => new()
    {
        ["region"] = region,
        ["game_build"] = build,
        ["code_sha256"] = sha,
        ["match_source"] = matchSource,
        ["submitters"] = submitters,
        ["first_published_at"] = published,
        ["path"] = region.ToLowerInvariant() + "/" + build + "/" + (sha.Length >= 12 ? sha[..12] : sha) + ".mrc",
        ["commit"] = commit ?? Commit(),
        ["revoked"] = revoked,
    };

    internal static byte[] Index(params JsonNode[] entries) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["schema_version"] = 1,
        ["entries"] = new JsonArray(entries),
    }.ToJsonString());

    [Fact]
    public void AWellFormedIndexReadsEveryEntry()
    {
        var read = SharedCalibrationIndex.Read(Index(
            Entry(Sha('a'), submitters: 3),
            Entry(Sha('b'), region: "GLOBAL", revoked: true, matchSource: "REPLY_STATE")));

        Assert.True(read.IsReadable);
        Assert.Null(read.Refusal);
        Assert.Empty(read.Skipped);
        Assert.Equal(2, read.Entries.Count);
        var first = read.Entries[0];
        Assert.Equal(Region.Cn, first.Region);
        Assert.Equal(Build, first.GameBuild);
        Assert.Equal(Sha('a'), first.CodeSha256);
        Assert.Equal(CalibrationMatchSource.Announcement, first.MatchSource);
        Assert.Equal(3, first.Submitters);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero), first.FirstPublishedAtUtc);
        Assert.Equal(SharedCalibrationIndex.CodePath(Region.Cn, Build, Sha('a')), first.Path);
        Assert.Equal(Commit(), first.Commit);
        Assert.False(first.Revoked);
        Assert.Equal(Region.Global, read.Entries[1].Region);
        Assert.Equal(CalibrationMatchSource.ReplyState, read.Entries[1].MatchSource);
        Assert.True(read.Entries[1].Revoked);
    }

    [Fact]
    public void ACodeLivesUnderItsRegionAndBuildNamedByTwelveHexDigits()
    {
        Assert.Equal("cn/" + Build + "/aaaaaaaaaaaa.mrc", SharedCalibrationIndex.CodePath(Region.Cn, Build, Sha('a')));
        Assert.Equal("global/" + Build + "/bbbbbbbbbbbb.mrc", SharedCalibrationIndex.CodePath(Region.Global, Build, Sha('b')));
        Assert.Throws<ArgumentException>(() => SharedCalibrationIndex.CodePath(Region.Unknown, Build, Sha('a')));
        Assert.Throws<ArgumentException>(() => SharedCalibrationIndex.CodePath(Region.Cn, "..", Sha('a')));
        Assert.Throws<ArgumentException>(() => SharedCalibrationIndex.CodePath(Region.Cn, Build, "abc"));
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var entry = Entry(Sha('a'));
        entry["note"] = "anything at all";
        entry["template_sha256"] = Sha('f');
        var document = new JsonObject
        {
            ["schema_version"] = 1,
            ["generated_at"] = "whenever",
            ["entries"] = new JsonArray(entry),
        };

        var read = SharedCalibrationIndex.Read(Encoding.UTF8.GetBytes(document.ToJsonString()));

        Assert.Single(read.Entries);
        Assert.Empty(read.Skipped);
    }

    public static TheoryData<string, string, string?> MalformedEntries() => new()
    {
        // field, the reason it is skipped for, replacement JSON (null removes the field)
        { "region", "MISSING:region", null },
        { "region", "INVALID:region", "\"MARS\"" },
        { "region", "INVALID:region", "\"UNKNOWN\"" },
        { "game_build", "INVALID:game_build", "\"..\"" },
        { "game_build", "INVALID:game_build", "\"2026/09\"" },
        { "game_build", "INVALID:game_build", "20260901" },
        { "code_sha256", "INVALID:code_sha256", "\"" + new string('A', 64) + "\"" },
        { "code_sha256", "INVALID:code_sha256", "\"abc\"" },
        { "match_source", "INVALID:match_source", "\"GUESSED\"" },
        { "submitters", "INVALID:submitters", "0" },
        { "submitters", "INVALID:submitters", "\"3\"" },
        { "submitters", "INVALID:submitters", "1.5" },
        { "first_published_at", "INVALID:first_published_at", "\"yesterday\"" },
        { "first_published_at", "INVALID:first_published_at", "\"2026-09-15T08:00:00\"" },
        { "first_published_at", "INVALID:first_published_at", "\"2026-13-15T08:00:00Z\"" },
        { "path", "INVALID:path", "\"../index.json\"" },
        { "path", "INVALID:path", "\"cn/" + Build + "/bbbbbbbbbbbb.mrc\"" },
        { "path", "INVALID:path", "\"CN/" + Build + "/aaaaaaaaaaaa.mrc\"" },
        { "commit", "MISSING:commit", null },
        { "commit", "INVALID:commit", "\"main\"" },
        { "revoked", "MISSING:revoked", null },
        { "revoked", "INVALID:revoked", "\"false\"" },
    };

    [Theory]
    [MemberData(nameof(MalformedEntries))]
    public void AMalformedEntryIsSkippedWithAReasonAndTheRestIsStillRead(string field, string reason, string? replacement)
    {
        var bad = Entry(Sha('a'));
        if (replacement is null)
        {
            bad.Remove(field);
        }
        else
        {
            bad[field] = JsonNode.Parse(replacement);
        }

        var read = SharedCalibrationIndex.Read(Index(bad, Entry(Sha('b'))));

        Assert.True(read.IsReadable);
        var skip = Assert.Single(read.Skipped);
        Assert.Equal(0, skip.Position);
        Assert.Equal(reason, skip.Reason);
        Assert.Equal(Sha('b'), Assert.Single(read.Entries).CodeSha256);
    }

    [Fact]
    public void AnEntryThatIsNotAnObjectOrRepeatsAKeyIsSkipped()
    {
        var duplicated = Entry(Sha('a')).ToJsonString().TrimEnd('}') + ",\"submitters\":900}";
        var text = "{\"schema_version\":1,\"entries\":[5," + duplicated + "," + Entry(Sha('b')).ToJsonString() + "]}";

        var read = SharedCalibrationIndex.Read(Encoding.UTF8.GetBytes(text));

        Assert.Equal(
            new[] { (0, "NOT_AN_OBJECT"), (1, "DUPLICATE_KEY") },
            read.Skipped.Select(skip => (skip.Position, skip.Reason)).ToArray());
        Assert.Equal(Sha('b'), Assert.Single(read.Entries).CodeSha256);
    }

    public static TheoryData<string, string> UnusableIndexes() => new()
    {
        { "", "NOT_JSON" },
        { "not json at all", "NOT_JSON" },
        { "[]", "NOT_AN_OBJECT" },
        { "{\"entries\":[]}", "SCHEMA_VERSION" },
        { "{\"schema_version\":2,\"entries\":[]}", "SCHEMA_VERSION" },
        { "{\"schema_version\":\"1\",\"entries\":[]}", "SCHEMA_VERSION" },
        { "{\"schema_version\":1}", "NO_ENTRIES" },
        { "{\"schema_version\":1,\"entries\":{}}", "NO_ENTRIES" },
        { "{\"schema_version\":1,\"schema_version\":1,\"entries\":[]}", "DUPLICATE_KEY" },
    };

    [Theory]
    [MemberData(nameof(UnusableIndexes))]
    public void AnIndexThatCannotBeReadIsRefusedWhole(string text, string reason)
    {
        var read = SharedCalibrationIndex.Read(Encoding.UTF8.GetBytes(text));

        Assert.False(read.IsReadable);
        Assert.Equal(reason, read.Refusal);
        Assert.Empty(read.Entries);
    }

    [Fact]
    public void BytesThatAreNotUtf8AreRefused()
    {
        Assert.Equal("NOT_JSON", SharedCalibrationIndex.Read(new byte[] { 0x7B, 0xFF, 0xFE, 0x7D }).Refusal);
    }

    public static TheoryData<string, string, string> IllFormedText() => new()
    {
        // why, text of a one-entry index to replace, what replaces it (JSON escapes, not characters)
        { "a key inside an entry", "{\"region\"", "{\"\\ud800\":1,\"region\"" },
        { "a value the entry needs", "\"game_build\":\"" + Build + "\"", "\"game_build\":\"\\ud800\"" },
        { "a field nobody reads", "{\"schema_version\"", "{\"generated_at\":\"\\udfff\",\"schema_version\"" },
        { "the value of a repeated key", "{\"schema_version\":1", "{\"schema_version\":\"\\ud800\",\"schema_version\":1" },
        { "a string in an unknown entry field", "\"revoked\":false", "\"revoked\":false,\"note\":[\"\\ud800\"]" },
    };

    /// <summary>
    /// .NET cannot read a key or string holding a lone surrogate, so the whole index is refused before anything
    /// in it is looked at - the same text <c>tools/shared-calibration/index.py</c> refuses (test_index.py).
    /// </summary>
    [Theory]
    [MemberData(nameof(IllFormedText))]
    public void TextThatIsNotWellFormedUnicodeRefusesTheWholeIndex(string why, string anchor, string replacement)
    {
        var text = Encoding.UTF8.GetString(Index(Entry(Sha('a'))));
        Assert.Contains(anchor, text, StringComparison.Ordinal);

        var read = SharedCalibrationIndex.Read(Encoding.UTF8.GetBytes(text.Replace(anchor, replacement, StringComparison.Ordinal)));

        Assert.True(read.Refusal == "NOT_JSON", why + ": " + (read.Refusal ?? "readable"));
        Assert.Empty(read.Entries);
        Assert.Empty(read.Skipped);
    }

    [Fact]
    public void BytesThatAreNotUtf8InsideAStringRefuseTheWholeIndex()
    {
        var text = Encoding.UTF8.GetString(Index(Entry(Sha('a'))));
        var at = text.IndexOf("ANNOUNCEMENT\"", StringComparison.Ordinal) + "ANNOUNCEMENT".Length;
        var bytes = Encoding.UTF8.GetBytes(text[..at]).Append((byte)0xFF).Concat(Encoding.UTF8.GetBytes(text[at..])).ToArray();

        Assert.Equal("NOT_JSON", SharedCalibrationIndex.Read(bytes).Refusal);
    }

    [Fact]
    public void AByteOrderMarkIsTolerated()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Index(Entry(Sha('a')))).ToArray();

        Assert.Single(SharedCalibrationIndex.Read(bytes).Entries);
    }

    [Fact]
    public void AnIndexWithMoreEntriesThanTheLimitIsRefusedWhole()
    {
        JsonNode[] Entries(int count) => Enumerable.Range(0, count).Select(i => (JsonNode)Entry(Sha(i))).ToArray();

        var over = SharedCalibrationIndex.Read(Index(Entries(SharedCalibrationIndex.MaxEntries + 1)));
        var atLimit = SharedCalibrationIndex.Read(Index(Entries(SharedCalibrationIndex.MaxEntries)));

        Assert.Equal("TOO_MANY_ENTRIES", over.Refusal);
        Assert.Empty(over.Entries);
        Assert.True(atLimit.IsReadable);
        Assert.Equal(SharedCalibrationIndex.MaxEntries, atLimit.Entries.Count);
    }

    [Fact]
    public void SelectionTakesThisRegionAndBuildSkipsRevokedOrdersBySubmittersThenAgeAndStopsAtEight()
    {
        var read = SharedCalibrationIndex.Read(Index(
            Entry(Sha('0'), submitters: 99, revoked: true),
            Entry(Sha('1'), submitters: 50, build: "2026.08.05.0000.0000"),
            Entry(Sha('2'), submitters: 50, region: "GLOBAL"),
            Entry(Sha(16), submitters: 1, published: "2026-09-01T00:00:00Z"),
            Entry(Sha(17), submitters: 3, published: "2026-09-05T00:00:00Z"),
            Entry(Sha(18), submitters: 2, published: "2026-09-02T00:00:00Z"),
            Entry(Sha(19), submitters: 3, published: "2026-09-03T00:00:00Z"),
            Entry(Sha(20), submitters: 1, published: "2026-09-02T00:00:00Z"),
            Entry(Sha(21), submitters: 2, published: "2026-09-01T00:00:00Z"),
            Entry(Sha(22), submitters: 1, published: "2026-09-03T00:00:00Z"),
            Entry(Sha(23), submitters: 5, published: "2026-09-09T00:00:00Z"),
            Entry(Sha(24), submitters: 1, published: "2026-09-04T00:00:00Z"),
            // Same submitters and age as 18: the lower hash goes first, so the order is stable.
            Entry(Sha(25), submitters: 2, published: "2026-09-02T00:00:00Z")));

        var chosen = SharedCalibrationIndex.Select(read.Entries, Region.Cn, Build);

        Assert.Equal(SharedCalibrationIndex.MaxCandidates, chosen.Count);
        Assert.Equal(
            new[] { Sha(23), Sha(19), Sha(17), Sha(21), Sha(18), Sha(25), Sha(16), Sha(20) },
            chosen.Select(entry => entry.CodeSha256).ToArray());
        Assert.Equal(new[] { Sha('0') }, SharedCalibrationIndex.Revoked(read.Entries, Region.Cn, Build));
        Assert.Empty(SharedCalibrationIndex.Revoked(read.Entries, Region.Global, Build));
    }

    [Fact]
    public void ARevocationWinsOverAnotherEntryForTheSameCodeAndADuplicateIsChosenOnce()
    {
        var read = SharedCalibrationIndex.Read(Index(
            Entry(Sha('a'), submitters: 5),
            Entry(Sha('a'), revoked: true),
            Entry(Sha('b'), submitters: 4),
            Entry(Sha('b'), submitters: 2)));

        var chosen = SharedCalibrationIndex.Select(read.Entries, Region.Cn, Build);

        var only = Assert.Single(chosen);
        Assert.Equal(Sha('b'), only.CodeSha256);
        Assert.Equal(4, only.Submitters);
        Assert.Equal(new[] { Sha('a') }, SharedCalibrationIndex.Revoked(read.Entries, Region.Cn, Build));
    }

    /// <summary>Plan §18.6: codes the repository marked as contradicting each other are picked last, whatever their submitters.</summary>
    [Fact]
    public void ConflictingEntriesAreOptionalReadAsFalseWhenAbsentAndPickedLast()
    {
        var conflicting = Entry(Sha('c'), submitters: 9);
        conflicting["conflicting"] = true;
        var cleared = Entry(Sha('d'), submitters: 3);
        cleared["conflicting"] = false;
        var read = SharedCalibrationIndex.Read(Index(conflicting, cleared, Entry(Sha('e'), submitters: 1)));

        Assert.Empty(read.Skipped);
        Assert.True(Assert.Single(read.Entries, entry => entry.CodeSha256 == Sha('c')).Conflicting);
        Assert.False(Assert.Single(read.Entries, entry => entry.CodeSha256 == Sha('d')).Conflicting);
        Assert.False(Assert.Single(read.Entries, entry => entry.CodeSha256 == Sha('e')).Conflicting);
        Assert.Equal(
            new[] { Sha('d'), Sha('e'), Sha('c') },
            SharedCalibrationIndex.Select(read.Entries, Region.Cn, Build).Select(entry => entry.CodeSha256).ToArray());
    }

    [Fact]
    public void AConflictingFlagThatIsNotABooleanSkipsTheEntry()
    {
        var entry = Entry(Sha('c'));
        entry["conflicting"] = "yes";

        var read = SharedCalibrationIndex.Read(Index(entry));

        Assert.Empty(read.Entries);
        Assert.Equal("INVALID:conflicting", Assert.Single(read.Skipped).Reason);
    }
}
