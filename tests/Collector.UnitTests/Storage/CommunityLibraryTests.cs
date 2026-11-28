using static MentorRecorder.Collector.UnitTests.CommunityTestData;
using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Community;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CommunityLibraryTests
{
    private const string NoteId = "3bdf068a-1753-4c27-b9de-adfae2d048f8";
    private const string MacroId = "241ff32e-2e0f-4730-9f70-558a9308297b";

    // likes.json is read at main on every refresh, also when the snapshot is unchanged. Its counts
    // decorate the first page for listed entries only; a broken file shows none and a failed
    // download keeps the last ones, and neither changes the library's status.
    [Fact]
    public async Task RefreshReadsLikesBesideTheSnapshotWithoutDependingOnThem()
    {
        using var fixture = new TestDatabase();
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        var withdrawn = Published(Draft(MacroId, "macro", "/p Gone"), 2);
        withdrawn["status"] = "withdrawn";
        var (index, bytes) = Publication(1, Published(Draft(NoteId, "reminder", "Liked text"), 1), withdrawn);
        var clock = new TestTime();
        JsonObject Like(int count, long comment) => new() { ["count"] = count, ["issue"] = 53, ["comment"] = comment };
        string Likes(int count) => new JsonObject { ["schema_version"] = 1, ["likes"] = new JsonObject
            { [NoteId] = Like(count, 2_400_000_101L), [MacroId] = Like(4, 2_400_000_102L) } }.ToJsonString();
        object served = Likes(12);
        var library = new CommunityLibrary(new CommunityRepository(fixture.Database), settings, (_, file, _, _, _) =>
        {
            if (file != "likes.json")
                return Task.FromResult<byte[]?>(file == "index.json" ? index : bytes);
            if (served is Exception error)
                throw error;
            if (served is CancellationTokenSource stop)
                stop.Cancel();
            return served is CancellationTokenSource cancelled ? Task.FromCanceled<byte[]?>(cancelled.Token)
                : Task.FromResult(served is string text ? Encoding.UTF8.GetBytes(text) : null);
        }, clock, killSwitch: () => false);
        JsonNode? LikesOf(JsonObject result) => result["likes"];
        async Task<JsonObject> Refresh(object reply, CancellationToken token = default)
        {
            served = reply;
            clock.Now = clock.Now.AddMinutes(5);
            var result = await library.RefreshAsync(token);
            Assert.Equal("ready", Status(result));
            return result;
        }

        library.SetOnline(true);
        var first = await Refresh(Likes(12));
        Assert.Equal($$$"""{"{{{NoteId}}}":{"count":12,"issue":53,"comment":2400000101}}""", LikesOf(first)!.ToJsonString());
        Assert.Equal(13, LikesOf(await Refresh(Likes(13)))![NoteId]!["count"]!.GetValue<int>());
        Assert.Equal(13, LikesOf(await Refresh(new HttpRequestException()))![NoteId]!["count"]!.GetValue<int>());
        // Cancelled while likes.json downloads, the refresh still ends "ready" and keeps the counts.
        using var cancel = new CancellationTokenSource();
        Assert.Equal(13, LikesOf(await Refresh(cancel, cancel.Token))![NoteId]!["count"]!.GetValue<int>());
        Assert.Empty(LikesOf(await Refresh("""{"schema_version":1,"likes":{"x":{}}}"""))!.AsObject());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Refresh(new InvalidOperationException("unexpected transport failure")));
        Assert.Empty(LikesOf(await Refresh(Likes(-1)))!.AsObject());
        Assert.Equal(7, LikesOf(await Refresh(Likes(7)))![NoteId]!["count"]!.GetValue<int>());
        Assert.Empty(LikesOf(await Refresh(new object()))!.AsObject());
        Assert.Null(library.Browse(offset: 40)["likes"]);
    }

    [Fact]
    public async Task RefreshNeverRollsBackToAnOlderGenerationAndTellsTheSourceWhichRepliesAreUsable()
    {
        using var fixture = new TestDatabase();
        var repository = new CommunityRepository(fixture.Database);
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        var clock = new TestTime();
        var newer = Publication(2, Published(Draft(NoteId, "reminder", "Newer text"), 2));
        var older = Publication(1, Published(Draft(NoteId, "reminder", "Older text"), 1));
        var served = newer;
        var refused = new List<string>();
        var library = new CommunityLibrary(repository, settings, (_, file, _, accept, _) =>
        {
            if (file == "likes.json")
                return Task.FromResult<byte[]?>(null);
            var bytes = file == "index.json" ? served.Index : served.Bytes;
            Assert.Equal(served == newer || file != "index.json", accept(bytes));
            if (accept("<html>not yet mirrored</html>"u8.ToArray()))
                refused.Add(file);
            return Task.FromResult<byte[]?>(bytes);
        }, clock, killSwitch: () => false);
        library.SetOnline(true);
        Assert.Equal("ready", Status(await library.RefreshAsync(CancellationToken.None)));
        served = older;
        clock.Now = clock.Now.AddMinutes(5);
        var result = await library.RefreshAsync(CancellationToken.None);
        Assert.Equal("stale_mirror", Status(result));
        Assert.Equal(2, result["generation"]!.GetValue<int>());
        Assert.Equal("Newer text", result["entries"]![0]!["body"]!.GetValue<string>());
        Assert.Empty(refused);
    }

    [Fact]
    public void CommunityJsonReaderRejectsTextThatIsNotUtf8()
    {
        var latin1 = Encoding.Latin1.GetBytes("""{"title":"café"}""");
        Assert.Throws<System.Text.Json.JsonException>(() => CommunityContent.ParseObject(latin1));
    }

    [Theory]
    [InlineData("older")]
    [InlineData("conflicting")]
    [InlineData("empty")]
    public async Task RefreshWaitsForTheOtherSourceWhenTheFirstIndexCannotAdvanceTheCache(string first)
    {
        using var fixture = new TestDatabase();
        var repository = new CommunityRepository(fixture.Database);
        Cache(repository, 2);
        var newer = Publication(3);
        var firstIndex = first switch
        {
            "older" => Publication(1).Index,
            "conflicting" => Publication(2, Published(Draft(NoteId, "reminder", "Conflicting text"), 1)).Index,
            _ => """{"schema_version":1,"generation":0,"snapshot_commit":null,"snapshot_sha256":null}"""u8.ToArray(),
        };
        var requests = 0;
        var library = new CommunityLibrary(repository, new SettingsRepository(fixture.Database, fixture.Clock),
            (revision, file, _, accept, token) => file == "index.json"
                ? CommunityHttp.GetPublicationAsync(revision, file,
                    (_, _) => Task.FromResult<byte[]?>(++requests == 1 ? firstIndex : newer.Index),
                    TimeSpan.FromMinutes(1), token, accept)
                : Task.FromResult<byte[]?>(file == "snapshot.json" ? newer.Bytes : null), killSwitch: () => false);

        library.SetOnline(true);
        var result = await library.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, requests);
        Assert.Equal("ready", Status(result));
        Assert.Equal(3, result["generation"]!.GetValue<int>());
    }

    private static string Status(JsonObject result) => result["status"]!.GetValue<string>();
}
