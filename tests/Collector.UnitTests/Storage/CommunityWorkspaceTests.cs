using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Community;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Speech;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CommunityWorkspaceTests
{
    private const string NoteId = "3bdf068a-1753-4c27-b9de-adfae2d048f8";
    private const string MacroId = "241ff32e-2e0f-4730-9f70-558a9308297b";

    [Fact]
    public void SavedContentIsRestoredAndTheSameRequestDoesNotAdvanceRevision()
    {
        using var fixture = new TestDatabase();
        var workspace = Open(fixture);
        var request = Guid.NewGuid().ToString();
        var macro = Draft(MacroId, "macro", "/p 全角　<t>\r\n/wait 1\r\n/p <me>  ");
        var payload = Save(macro, 0, true);
        Assert.Equal("saved", Status(workspace.Change("save", request, payload)));
        Assert.Equal("saved", Status(workspace.Change("save", request, payload)));
        Assert.Equal("local_conflict", Status(workspace.Change("save", Guid.NewGuid().ToString(), payload)));

        fixture.Reopen();
        workspace = Open(fixture);
        var row = Assert.Single(workspace.List(0)["items"]!.AsArray())!;
        Assert.Equal(1, row["revision"]!.GetValue<int>());
        Assert.True(row["favorite"]!.GetValue<bool>());
        Assert.Equal(macro["body"]!.GetValue<string>(), row["content"]!["body"]!.GetValue<string>());
    }

    [Fact]
    public void ImportsLinksAndPreservesPersonalEditsWhenAnAuthorUpdates()
    {
        using var fixture = new TestDatabase();
        var repository = new CommunityRepository(fixture.Database);
        var note = Draft(NoteId, "reminder", "First revision");
        note["links"] = new JsonArray(MacroId);
        var macro = Draft(MacroId, "macro", "/p <t>  ");
        Cache(repository, 1, Published(note, 1), Published(macro, 1));
        var workspace = Open(fixture);
        Assert.Equal("installed", Status(workspace.Change("install", Guid.NewGuid().ToString(), new JsonObject { ["id"] = NoteId })));
        Assert.Equal(2, workspace.List(0)["items"]!.AsArray().Count);

        var local = note.DeepClone().AsObject();
        local["body"] = "My own wording";
        Assert.Equal("saved", Status(workspace.Change("save", Guid.NewGuid().ToString(), Save(local, 1))));
        var next = note.DeepClone().AsObject();
        next["title"] = "New title";
        next["body"] = "Author's new wording";
        Cache(repository, 2, Published(next, 2), Published(macro, 1));
        workspace = Open(fixture);
        Assert.Equal("updated", Status(workspace.Change("apply", Guid.NewGuid().ToString(), new JsonObject
        {
            ["id"] = NoteId, ["revision"] = 2, ["source_revision"] = 2, ["mode"] = "keep",
        })));
        var result = workspace.List(0)["items"]!.AsArray().Single(row => row!["content"]!["id"]!.GetValue<string>() == NoteId)!;
        Assert.Equal("My own wording", result["content"]!["body"]!.GetValue<string>());
        Assert.Equal("New title", result["content"]!["title"]!.GetValue<string>());
        Assert.Equal("Author's new wording", result["upstream"]!["body"]!.GetValue<string>());

        var withdrawn = Published(next, 3);
        withdrawn["status"] = "withdrawn";
        Cache(repository, 3, withdrawn, Published(macro, 1));
        workspace = Open(fixture);
        Assert.Equal("source_unavailable", Status(workspace.Change("install", Guid.NewGuid().ToString(), new JsonObject { ["id"] = NoteId })));
        Assert.Equal(2, workspace.List(0)["items"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("reminder", "完整  <t>\r\n下一行", "copy_ready")]
    [InlineData("chat", "/a 全角　<t>  ", "copy_ready")]
    [InlineData("macro", "/p <t>　<wait.1>\r\n/p <me>  ", "copy_ready")]
    [InlineData("chat", "/p 第一行\n/p 第二行", "single_chat_required")]
    [InlineData("chat", "/p   ", "single_chat_required")]
    public async Task ClipboardValidationReturnsTheExactAcceptedText(string mode, string text, string expected)
    {
        using var fixture = new TestDatabase();
        using var host = CollectorHost.Open(fixture.Path, fixture.Clock);
        var reply = await new MessageDispatcher(host).DispatchAsync(new IpcRequest(Guid.NewGuid().ToString(), "DutyCommunity",
            new JsonObject { ["action"] = "copy", ["mode"] = mode, ["body"] = text }), CancellationToken.None);
        Assert.Equal(expected, Status(reply));
        if (expected == "copy_ready")
            Assert.Equal(text, reply["body"]!.GetValue<string>());
    }

    [Fact]
    public void RejectsOversizeContentAndTamperedOrDuplicatePublication()
    {
        Assert.Equal("macro_lines", CommunityContent.MacroError(string.Join('\n', Enumerable.Repeat("/p 1", 16)), "macro"));
        Assert.Equal("macro_length", CommunityContent.MacroError(new string('全', 61), "macro"));
        Assert.Throws<FormatException>(() => CommunityContent.ParseObject(Encoding.UTF8.GetBytes("""{"x":1,"x":2}""")));
        var pointer = new CommunityLibrary.PublicationPointer(new string('a', 40), new string('b', 64), 1);
        Assert.Throws<FormatException>(() => CommunityLibrary.ReadPublication(pointer, "{}"u8.ToArray()));
    }

    [Fact]
    public async Task RefreshKeepsTheLastValidCacheWhenDownloadFails()
    {
        using var fixture = new TestDatabase();
        var repository = new CommunityRepository(fixture.Database);
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        var note = Published(Draft(NoteId, "reminder", "Offline text"), 1);
        var (index, bytes) = Publication(1, note);
        var calls = new List<(string Revision, string File)>();
        var clock = new TestTime();
        var failing = false;
        var library = new CommunityLibrary(repository, settings, (revision, file, _, _) =>
        {
            calls.Add((revision, file));
            if (failing)
                throw new HttpRequestException();
            return Task.FromResult<byte[]?>(file == "index.json" ? index : bytes);
        }, clock);
        Assert.Equal("offline", Status(library.Browse()));
        Assert.Empty(calls);
        library.SetOnline(true);
        if (Environment.GetEnvironmentVariable("MR_DISABLE_DUTY_COMMUNITY") == "1")
        {
            Assert.Equal("offline", Status(await library.RefreshAsync(CancellationToken.None)));
            Assert.Empty(calls);
            return;
        }
        var result = await library.RefreshAsync(CancellationToken.None);
        Assert.Equal("ready", Status(result));
        Assert.Equal(new[] { ("main", "index.json"), (new string('a', 40), "snapshot.json") }, calls);
        Assert.Single(result["entries"]!.AsArray());
        failing = true;
        clock.Now = clock.Now.AddMinutes(5);
        result = await library.RefreshAsync(CancellationToken.None);
        Assert.Equal("temporarily_unavailable", Status(result));
        Assert.True(result["cached"]!.GetValue<bool>());
        Assert.Single(result["entries"]!.AsArray());
        fixture.Reopen();
        var persisted = new CommunityLibrary(new CommunityRepository(fixture.Database),
            new SettingsRepository(fixture.Database, fixture.Clock));
        Assert.Single(persisted.Browse()["entries"]!.AsArray());
    }

    [Fact]
    public async Task DeviceFlowUsesOnlyThePlayerTokenAndRefreshesItWithoutASecret()
    {
        using var fixture = new TestDatabase();
        var clock = new TestTime();
        var requests = new List<(string Url, string? Token, string Body)>();
        var poll = 0;
        Task<JsonObject> Answer(string json) => Task.FromResult(JsonNode.Parse(json)!.AsObject());
        var github = new CommunityGithub(Path.GetDirectoryName(fixture.Path)!, "publicClientId", async
            (method, url, token, content, cancellation) =>
        {
            using var body = content;
            var text = body is null ? "" : await body.ReadAsStringAsync(cancellation);
            requests.Add((url, token, text));
            if (url.EndsWith("/device/code", StringComparison.Ordinal))
                return await Answer("""{"device_code":"private-device-code","user_code":"ABCD-EFGH","interval":5,"expires_in":60}""");
            if (url.EndsWith("/user", StringComparison.Ordinal))
                return await Answer("""{"login":"player","id":75}""");
            if (url.EndsWith("/issues", StringComparison.Ordinal) && method == HttpMethod.Post)
                return await Answer("""{"number":19}""");
            if (text.Contains("refresh_token", StringComparison.Ordinal))
                return await Answer("""{"access_token":"refreshed-player-token","expires_in":28800}""");
            return ++poll == 1
                ? await Answer("""{"error":"slow_down"}""")
                : await Answer("""{"access_token":"player-token","refresh_token":"refresh-token","expires_in":100}""");
        }, clock, new TestProtector());

        Task<JsonObject> Command(string action, JsonObject? data = null) =>
            github.RequestAsync(action, data ?? new JsonObject(), CancellationToken.None);
        var grant = await Command("sign_in");
        Assert.Equal("ABCD-EFGH", grant["user_code"]!.GetValue<string>());
        Assert.DoesNotContain("private-device-code", grant.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("authorizing", Status(await Command("poll")));
        Assert.Single(requests);
        clock.Now = clock.Now.AddSeconds(5);
        Assert.Equal(10, (await Command("poll"))["interval"]!.GetValue<int>());
        clock.Now = clock.Now.AddSeconds(10);
        var account = await Command("poll");
        Assert.Equal("signed_in", Status(account));
        Assert.DoesNotContain("player-token", account.ToJsonString(), StringComparison.Ordinal);
        clock.Now = clock.Now.AddMinutes(3);
        var submitted = await Command("submit", new JsonObject
        {
            ["license"] = "CC-BY-4.0", ["consent"] = true, ["author_id"] = 75,
            ["base_revisions"] = new JsonObject(), ["items"] = new JsonArray(Draft(NoteId, "reminder", "Test")),
        });
        Assert.Equal("submitted", Status(submitted));
        Assert.Equal("refreshed-player-token", requests[^1].Token);
        Assert.Contains("refresh_token=refresh-token", requests[^2].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", requests[^2].Body, StringComparison.Ordinal);
        Assert.All(requests.Where(r => r.Url.StartsWith("https://github.com/", StringComparison.Ordinal)),
            request => Assert.Null(request.Token));
        Assert.Equal("signed_out", Status(await Command("sign_out")));
        Assert.Equal("signed_out", Status(await Command("account")));
    }

    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Select(value => (byte)(value ^ 0x42)).ToArray();
        public byte[] Unprotect(byte[] ciphertext) => Protect(ciphertext);
    }

    private sealed class TestTime : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string Status(JsonObject result) => result["status"]!.GetValue<string>();

    private static CommunityWorkspace Open(TestDatabase fixture)
    {
        var repository = new CommunityRepository(fixture.Database);
        return new(repository, new CommunityLibrary(repository, new SettingsRepository(fixture.Database, fixture.Clock)));
    }

    private static JsonObject Draft(string id, string type, string body) => new()
    {
        ["schema_version"] = 1, ["id"] = id, ["type"] = type, ["content_id"] = 4, ["region"] = "CN",
        ["language"] = "zh-Hans", ["title"] = "Example", ["body"] = body, ["boss"] = "", ["phase"] = "",
        ["role"] = "", ["tags"] = new JsonArray(), ["links"] = new JsonArray(), ["source"] = "", ["game_version"] = "",
    };

    private static JsonObject Save(JsonObject draft, int revision, bool favorite = false) => new()
    {
        ["action"] = "save", ["content"] = draft.DeepClone(), ["revision"] = revision,
        ["position"] = 0, ["favorite"] = favorite,
    };

    private static JsonObject Published(JsonObject original, int revision)
    {
        var draft = original.DeepClone().AsObject();
        draft["revision"] = revision;
        draft["status"] = "community";
        draft["updated_at"] = "2026-11-18T10:00:00Z";
        draft["author"] = new JsonObject { ["login"] = "user-one", ["id"] = 321 };
        draft["issue"] = 53;
        draft["license"] = "CC-BY-4.0";
        return draft;
    }

    private static void Cache(CommunityRepository repository, int generation, params JsonObject[] items)
    {
        var (index, bytes) = Publication(generation, items);
        repository.SaveCache(index, bytes);
    }

    private static (byte[] Index, byte[] Bytes) Publication(int generation, params JsonObject[] items)
    {
        var bytes = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schema_version"] = 1, ["generation"] = generation, ["entries"] = new JsonArray(items),
        }.ToJsonString());
        var index = new JsonObject
        {
            ["schema_version"] = 1, ["generation"] = generation, ["snapshot_commit"] = new string('a', 40),
            ["snapshot_sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        };
        return (Encoding.UTF8.GetBytes(index.ToJsonString()), bytes);
    }
}
