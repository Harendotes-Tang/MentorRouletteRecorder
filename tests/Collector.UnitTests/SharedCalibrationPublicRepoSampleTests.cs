using System.Text;
using System.Text.Json;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Sharing;
using Source = MentorRecorder.Collector.Protocol.Sharing.SharedCalibrationSource;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The public calibration repository and the released client agree on the index, byte for byte.
/// Everything under <c>tests/Fixtures/shared-calibration/index-sample*</c> is written by
/// <c>tools/shared-calibration/generate_index_sample.py</c> through <c>index.add_submission</c>, the function the
/// repository's Action publishes with, and <c>test_index_sample.py</c> fails if a file is edited by hand. The
/// expected picks come from that tool's port of <see cref="SharedCalibrationIndex.Select"/>, so these tests pin
/// both the file format and the selection order across the two languages.
/// </summary>
public sealed class SharedCalibrationPublicRepoSampleTests
{
    private static readonly Lazy<byte[]> IndexBytes = new(() => File.ReadAllBytes(Fixture("index-sample.json")));

    private static readonly Lazy<JsonDocument> Expected =
        new(() => JsonDocument.Parse(File.ReadAllText(Fixture("index-sample.expected.json"))));

    private sealed record Pick(string Sha, int Submitters, string Commit, string CodePath);

    private static string Fixture(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "shared-calibration", relative);

    private static byte[] CodeFile(string codePath) =>
        File.ReadAllBytes(Fixture(Path.Combine("index-sample-codes", codePath)));

    public static TheoryData<string, string> Builds()
    {
        var data = new TheoryData<string, string>();
        foreach (var group in Expected.Value.RootElement.GetProperty("builds").EnumerateArray())
        {
            data.Add(group.GetProperty("region").GetString()!, group.GetProperty("game_build").GetString()!);
        }

        return data;
    }

    private static JsonElement Group(string region, string build) =>
        Expected.Value.RootElement.GetProperty("builds").EnumerateArray().Single(group =>
            group.GetProperty("region").GetString() == region && group.GetProperty("game_build").GetString() == build);

    private static Pick[] Picks(string region, string build) =>
        Group(region, build).GetProperty("picks").EnumerateArray()
            .Select(pick => new Pick(
                pick.GetProperty("code_sha256").GetString()!,
                pick.GetProperty("submitters").GetInt32(),
                pick.GetProperty("commit").GetString()!,
                pick.GetProperty("path").GetString()!))
            .ToArray();

    [Fact]
    public void TheIndexThePublishingToolWroteIsReadWholeWithNoSkippedEntry()
    {
        Assert.True(IndexBytes.Value.Length <= SharedCalibrationClient.MaxIndexBytes);

        var read = SharedCalibrationIndex.Read(IndexBytes.Value);

        Assert.True(read.IsReadable, read.Refusal);
        Assert.Empty(read.Skipped);
        Assert.Equal(Expected.Value.RootElement.GetProperty("entries").GetInt32(), read.Entries.Count);
        Assert.All(read.Entries, entry =>
            Assert.Equal(SharedCalibrationIndex.CodePath(entry.Region, entry.GameBuild, entry.CodeSha256), entry.Path));
        Assert.Contains(read.Entries, entry => entry.Revoked);
        Assert.Contains(read.Entries, entry => entry.Submitters > 1);
        Assert.Contains(read.Entries, entry => entry.Region == Region.Global);
        Assert.Equal(3, Expected.Value.RootElement.GetProperty("builds").GetArrayLength());
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public void SelectionPicksWhatThePublishingToolPicked(string region, string build)
    {
        var read = SharedCalibrationIndex.Read(IndexBytes.Value);
        var wire = EnumWire<Region>.Parse(region);

        var chosen = SharedCalibrationIndex.Select(read.Entries, wire, build);

        var picks = Picks(region, build);
        Assert.Equal(picks.Select(pick => pick.Sha), chosen.Select(entry => entry.CodeSha256));
        Assert.Equal(picks.Select(pick => pick.Submitters), chosen.Select(entry => entry.Submitters));
        Assert.Equal(picks.Select(pick => pick.Commit), chosen.Select(entry => entry.Commit));
        Assert.Equal(picks.Select(pick => pick.CodePath), chosen.Select(entry => entry.Path));
        Assert.Equal(
            Group(region, build).GetProperty("revoked").EnumerateArray().Select(sha => sha.GetString()!),
            SharedCalibrationIndex.Revoked(read.Entries, wire, build));
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public async Task TheClientDownloadsAndAcceptsThePublishedCodesInThatOrder(string region, string build)
    {
        var routes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [SharedCalibrationClient.IndexUri(Source.GithubRaw).AbsoluteUri] = IndexBytes.Value,
        };
        foreach (var entry in SharedCalibrationIndex.Read(IndexBytes.Value).Entries)
        {
            routes[SharedCalibrationClient.CodeUri(Source.GithubRaw, entry.Commit, entry.Path).AbsoluteUri] = CodeFile(entry.Path);
        }

        var client = new SharedCalibrationClient(Serve(routes), TimeSpan.FromSeconds(5), _ => null);

        var result = await client.FetchAsync(EnumWire<Region>.Parse(region), build);

        var picks = Picks(region, build);
        Assert.Equal(picks.Length == 0 ? SharedFetchStatus.NoneForBuild : SharedFetchStatus.Ok, result.Status);
        Assert.Empty(result.Discards);
        Assert.Empty(result.SkippedEntries);
        Assert.Equal(picks.Select(pick => pick.Sha), result.Candidates.Select(candidate => candidate.CodeSha256));
        Assert.Equal(picks.Select(pick => pick.Commit), result.Candidates.Select(candidate => candidate.Commit));
        Assert.All(result.Candidates, candidate =>
        {
            Assert.Equal(region, EnumWire<Region>.Format(candidate.Payload.Region));
            Assert.Equal(build, candidate.Payload.GameBuild);
        });
    }

    [Fact]
    public void EveryPublishedCodeFileDecodesToItsOwnEntry()
    {
        foreach (var entry in SharedCalibrationIndex.Read(IndexBytes.Value).Entries)
        {
            var bytes = CodeFile(entry.Path);
            Assert.True(bytes.Length <= SharedCalibrationClient.MaxCodeBytes);

            var decoded = ShareCode.Decode(Encoding.ASCII.GetString(bytes));

            Assert.Null(decoded.Rejection);
            Assert.Equal(entry.CodeSha256, decoded.CodeSha256);
            Assert.Equal(entry.MatchSource, decoded.Payload!.MatchSource);
            Assert.Equal(entry.Region, decoded.Payload.Region);
            Assert.Equal(entry.GameBuild, decoded.Payload.GameBuild);
        }
    }

    private static SharedCalibrationTransport Serve(IReadOnlyDictionary<string, byte[]> routes) => (uri, _) =>
        Task.FromResult(routes.TryGetValue(uri.AbsoluteUri, out var body)
            ? new SharedTransportResponse(200, uri, body.Length, new MemoryStream(body))
            : new SharedTransportResponse(404, uri, 0, new MemoryStream()));
}
