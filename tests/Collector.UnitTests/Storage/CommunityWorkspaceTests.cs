using static MentorRecorder.Collector.UnitTests.CommunityTestData;
using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Community;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Speech;
using MentorRecorder.Collector.Storage;
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
    public async Task ALongLocalDraftSurvivesReloadAndImportButCannotBeCopiedOrSubmitted()
    {
        using var fixture = new TestDatabase();
        var body = string.Join("\r\n", Enumerable.Repeat("/p 全角　<t>  ", 16));
        var macro = Draft(MacroId, "macro", body);
        var workspace = Open(fixture);
        Assert.Equal("saved", Status(workspace.Change("save", Guid.NewGuid().ToString(), Save(macro, 0))));

        fixture.Reopen();
        var row = Assert.Single(Open(fixture).List(0)["items"]!.AsArray())!.DeepClone().AsObject();
        Assert.Equal(body, row["content"]!["body"]!.GetValue<string>());
        row.Remove("revision");

        using var importedFixture = new TestDatabase();
        var imported = Open(importedFixture);
        var file = new JsonObject { ["schema_version"] = 1, ["items"] = new JsonArray(row) };
        Assert.Equal("installed", Status(imported.Change("import_file", Guid.NewGuid().ToString(),
            new JsonObject { ["file"] = file.ToJsonString() })));
        var importedRow = Assert.Single(imported.List(0)["items"]!.AsArray())!;
        Assert.Equal(body, importedRow["content"]!["body"]!.GetValue<string>());

        using var host = CollectorHost.Open(fixture.Path, fixture.Clock);
        var copy = await new MessageDispatcher(host).DispatchAsync(new IpcRequest(Guid.NewGuid().ToString(), "DutyCommunity",
            new JsonObject { ["action"] = "copy", ["mode"] = "macro", ["body"] = body }), CancellationToken.None);
        Assert.Equal("macro_lines", Status(copy));
        var submission = new JsonObject
        {
            ["license"] = "CC-BY-4.0", ["consent"] = true,
            ["items"] = new JsonArray(macro), ["base_revisions"] = new JsonObject(),
        };
        Assert.Equal("macro_lines", Assert.Throws<FormatException>(() => CommunityGithub.BuildIssueBody(submission)).Message);
        var upstream = Published(macro, 1);
        row["upstream"] = upstream;
        Assert.Equal("macro_lines", Assert.Throws<FormatException>(() => imported.Change("import_file", Guid.NewGuid().ToString(),
            new JsonObject { ["file"] = file.ToJsonString() })).Message);
    }

    [Fact]
    public void RecentUpdatesRetainLegacyOrderAcrossPagesRestartsAndReceiptReplay()
    {
        using var fixture = new TestDatabase();
        var legacyIds = Enumerable.Range(1, 41).Select(number => $"00000000-0000-4000-8000-{number:D12}").ToArray();
        using (var prepare = fixture.Database.CreateCommand())
        {
            prepare.CommandText = """
                DROP INDEX community_local_order;
                ALTER TABLE community_local DROP COLUMN edit_sequence;
                ALTER TABLE community_local DROP COLUMN source_links_json;
                CREATE INDEX community_local_order ON community_local(position, id);
                DELETE FROM schema_migrations WHERE version >= 19;
                """;
            prepare.ExecuteNonQuery();
        }
        using (var seed = fixture.Database.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO community_local(id, revision, position, favorite, content_json)
                VALUES ($id, 1, $position, 0, $content)
                """;
            var id = seed.Parameters.AddWithValue("$id", "");
            var position = seed.Parameters.AddWithValue("$position", 0);
            var content = seed.Parameters.AddWithValue("$content", "");
            foreach (var legacyId in Enumerable.Reverse(legacyIds))
            {
                id.Value = legacyId;
                position.Value = legacyId == legacyIds[1] ? -2 : 0;
                content.Value = Draft(legacyId, "reminder", "Legacy text").ToJsonString();
                seed.ExecuteNonQuery();
            }
        }

        fixture.Reopen();
        Assert.Equal(MigrationRunner.LatestVersion, fixture.Database.SchemaVersion);
        var workspace = Open(fixture);
        var legacyOrder = new[] { legacyIds[1], legacyIds[0] }.Concat(legacyIds.Skip(2)).ToArray();
        Assert.Equal(legacyOrder, ReadPagedLocalRows(workspace).Select(LocalRowId));

        long Generation(int offset) => workspace.List(offset)["generation"]!.GetValue<long>();
        var firstGeneration = Generation(0);
        var editRequest = Guid.NewGuid().ToString();
        var edit = Save(Draft(legacyIds[^1], "reminder", "Edited text"), 1);
        Assert.Equal("saved", Status(workspace.Change("save", editRequest, edit)));
        Assert.NotEqual(firstGeneration, Generation(CommunityContent.PageSize));
        Assert.Equal(legacyIds[^1], LocalRowId(ReadPagedLocalRows(workspace)[0]));
        Assert.Equal("saved", Status(workspace.Change("save", Guid.NewGuid().ToString(),
            Save(Draft(MacroId, "macro", "/p New macro"), 0))));
        var beforeFavorite = Generation(0);
        var favorite = Save(Draft(legacyIds[1], "reminder", "Legacy text"), 1, favorite: true);
        favorite["position"] = -2;
        Assert.Equal("saved", Status(workspace.Change("save", Guid.NewGuid().ToString(), favorite)));

        fixture.Reopen();
        workspace = Open(fixture);
        var afterFavorite = Generation(CommunityContent.PageSize);
        Assert.NotEqual(beforeFavorite, afterFavorite);
        Assert.Equal("saved", Status(workspace.Change("save", editRequest, edit)));
        Assert.Equal(afterFavorite, Generation(0));
        var rows = ReadPagedLocalRows(workspace);
        Assert.Equal(new[] { MacroId, legacyIds[^1] }.Concat(legacyOrder[..^1]), rows.Select(LocalRowId));
        Assert.Equal(2, rows[1]["revision"]!.GetValue<int>());
        Assert.Equal(-2, rows[2]["position"]!.GetValue<int>());
        Assert.True(rows[2]["favorite"]!.GetValue<bool>());
        Assert.Equal("deleted", Status(workspace.Change("delete", Guid.NewGuid().ToString(),
            new JsonObject { ["id"] = MacroId, ["revision"] = 1 })));
        Assert.NotEqual(afterFavorite, Generation(0));
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
        Assert.Equal(new[] { NoteId, MacroId }, workspace.List(0)["items"]!.AsArray().Select(LocalRowId));

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
        Assert.Equal("macro_length", Assert.Throws<FormatException>(() =>
            CommunityContent.ValidateLocalDraft(Draft(MacroId, "macro", new string('全', 61)))).Message);
        var oversizeDraft = Draft(MacroId, "macro", string.Join('\n', Enumerable.Repeat(new string('a', 100), 16)));
        Assert.Equal("invalid_body", Assert.Throws<FormatException>(() => CommunityContent.ValidateLocalDraft(oversizeDraft)).Message);
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
        var library = new CommunityLibrary(repository, settings, (revision, file, _, _, _) =>
        {
            calls.Add((revision, file));
            if (failing)
                throw new HttpRequestException();
            return Task.FromResult<byte[]?>(file == "index.json" ? index : bytes);
        }, clock, killSwitch: () => false);
        Assert.Equal("offline", Status(library.Browse()));
        Assert.Empty(calls);
        library.SetOnline(true);
        var result = await library.RefreshAsync(CancellationToken.None);
        Assert.Equal("ready", Status(result));
        // The diagnostics report states the request just sent and how it ended (A2b-2).
        Assert.Equal<(DateTimeOffset?, string?)>((clock.Now, "ready"), LastRequest(library));
        Assert.Equal(new[] { ("main", "index.json"), ("main", "likes.json"), (new string('a', 40), "snapshot.json") }, calls);
        Assert.Single(result["entries"]!.AsArray());
        failing = true;
        clock.Now = clock.Now.AddMinutes(5);
        result = await library.RefreshAsync(CancellationToken.None);
        Assert.Equal("temporarily_unavailable", Status(result));
        Assert.Equal<(DateTimeOffset?, string?)>((clock.Now, "temporarily_unavailable"), LastRequest(library));
        Assert.True(result["cached"]!.GetValue<bool>());
        Assert.Single(result["entries"]!.AsArray());
        fixture.Reopen();
        var persisted = new CommunityLibrary(new CommunityRepository(fixture.Database),
            new SettingsRepository(fixture.Database, fixture.Clock));
        Assert.Single(persisted.Browse()["entries"]!.AsArray());
    }

    [Fact]
    public async Task PublicationSwitchesToTheMirrorWhenGithubIsSlow()
    {
        var urls = new List<string>();
        var github = new TaskCompletionSource<byte[]?>();
        var githubCancelled = false;
        var result = await CommunityHttp.GetPublicationAsync(new string('a', 40), "snapshot.json", (url, token) =>
        {
            urls.Add(url);
            if (urls.Count > 1)
                return Task.FromResult<byte[]?>("mirror"u8.ToArray());
            token.Register(() => githubCancelled = true);
            return github.Task;
        }, TimeSpan.FromMilliseconds(1), CancellationToken.None);

        Assert.Equal("mirror"u8.ToArray(), result);
        Assert.Equal(new[]
        {
            $"{CommunityHttp.RawRoot}{new string('a', 40)}/community/v1/snapshot.json",
            $"{CommunityHttp.MirrorRoot}community/v1/snapshot.json?ref={new string('a', 40)}",
        }, urls);
        Assert.True(githubCancelled);
    }

    // A slow GitHub still wins when the mirror has not synchronised the file or cannot be reached.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationWaitsForSlowGithubWhenTheMirrorHasNothing(bool mirrorFails)
    {
        var github = new TaskCompletionSource<byte[]?>();
        var calls = 0;
        var pending = CommunityHttp.GetPublicationAsync("main", "index.json", (_, _) =>
        {
            if (++calls == 1)
                return github.Task;
            github.SetResult("github"u8.ToArray());
            return mirrorFails ? Task.FromException<byte[]?>(new HttpRequestException()) : Task.FromResult<byte[]?>(null);
        }, TimeSpan.FromMilliseconds(1), CancellationToken.None);

        Assert.Equal("github"u8.ToArray(), await pending);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("content", "unused", "content", 1)]
    [InlineData("failure", "content", "content", 2)]
    [InlineData("missing", "content", "content", 2)]
    [InlineData("missing", "missing", "missing", 2)]
    [InlineData("missing", "failure", "failure", 2)]
    [InlineData("failure", "missing", "failure", 2)]
    public async Task PublicationAsksTheMirrorOnlyWhenPromptGithubHasNoContent(string github, string mirror,
        string expected, int expectedCalls)
    {
        var calls = 0;
        Task<byte[]?> Answer(string kind) => kind switch
        {
            "content" => Task.FromResult<byte[]?>("content"u8.ToArray()),
            "missing" => Task.FromResult<byte[]?>(null),
            _ => Task.FromException<byte[]?>(new HttpRequestException()),
        };
        Task<byte[]?> Fetch() => CommunityHttp.GetPublicationAsync("main", "index.json",
            (_, _) => Answer(++calls == 1 ? github : mirror), TimeSpan.FromMinutes(1), CancellationToken.None);

        if (expected == "failure")
            await Assert.ThrowsAsync<HttpRequestException>(Fetch);
        else
            Assert.Equal(expected == "missing" ? null : "content"u8.ToArray(), await Fetch());
        Assert.Equal(expectedCalls, calls);
    }

    [Fact]
    public async Task DeviceFlowUsesOnlyThePlayerTokenAndRefreshesItWithoutASecret()
    {
        using var fixture = new TestDatabase();
        var clock = new TestTime();
        var requests = new List<(string Url, string? Token, string Body)>();
        var postStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var postReply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            {
                postStarted.SetResult();
                return await postReply.Task;
            }
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
        var pending = Command("submit", new JsonObject
        {
            ["license"] = "CC-BY-4.0", ["consent"] = true, ["author_id"] = 75,
            ["base_revisions"] = new JsonObject(), ["items"] = new JsonArray(Draft(NoteId, "reminder", "Test")),
        });
        await postStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("signed_out", Status(await Command("sign_out")));
        postReply.SetResult(new JsonObject { ["number"] = 19 });
        Assert.Equal("submitted", Status(await pending));
        Assert.Equal("refreshed-player-token", requests[^1].Token);
        Assert.Contains("refresh_token=refresh-token", requests[^2].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", requests[^2].Body, StringComparison.Ordinal);
        Assert.All(requests.Where(r => r.Url.StartsWith("https://github.com/", StringComparison.Ordinal)),
            request => Assert.Null(request.Token));
        Assert.Equal("signed_out", Status(await Command("account")));
    }

    [Fact]
    public async Task SignOutReportsWhenTheCredentialPathCannotBeDeleted()
    {
        using var fixture = new TestDatabase();
        var directory = Path.GetDirectoryName(fixture.Path)!;
        var credentialPath = Path.Combine(directory, "community-github.bin");
        Directory.CreateDirectory(credentialPath);
        var github = new CommunityGithub(directory, "publicClientId",
            (_, _, _, _, _) => throw new InvalidOperationException("Sign out should only touch local credentials."),
            new TestTime(), new TestProtector());

        Task<JsonObject> SignOut() => github.RequestAsync("sign_out", new JsonObject(), CancellationToken.None);
        Assert.Equal("temporarily_unavailable", Status(await SignOut()));
        Directory.Delete(credentialPath);
        Assert.Equal("signed_out", Status(await SignOut()));
    }

    [Theory]
    [InlineData("cancel", "token", false)]
    [InlineData("cancel", "account", false)]
    [InlineData("cancel", "account", true)]
    [InlineData("sign_out", "token", true)]
    [InlineData("sign_out", "account", true)]
    [InlineData("sign_out", "unauthorized", true)]
    public async Task StoppingDeviceAuthorizationDiscardsLateReplies(string stop, string delayAt, bool hasSavedAccount)
    {
        using var fixture = new TestDatabase();
        var directory = Path.GetDirectoryName(fixture.Path)!;
        var clock = new TestTime();
        var protector = new TestProtector();
        const string clientId = "publicClientId";
        const string saved = """{"access":"saved-token","refresh":"","expires":4102444800}""";
        var tokens = new SpeechKeyStore(directory, protector, "community-github.bin");
        if (hasSavedAccount)
            tokens.Write(clientId, saved);
        var oldReply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var atDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newAccount = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextDevice = 0;

        var github = new CommunityGithub(directory, clientId, async (_, url, access, content, cancellation) =>
        {
            using var form = content;
            if (url.EndsWith("/device/code", StringComparison.Ordinal))
            {
                var device = ++nextDevice;
                return new JsonObject { ["device_code"] = $"device-{device}", ["user_code"] = $"USER-{device}",
                    ["interval"] = 5, ["expires_in"] = 60 };
            }
            if (url.EndsWith("/user", StringComparison.Ordinal))
            {
                if (access == "new-token")
                    return await newAccount.Task;
                Assert.Equal("old-token", access);
                Assert.NotEqual("token", delayAt);
                atDelay.SetResult();
                return await oldReply.Task;
            }
            Assert.EndsWith("/access_token", url, StringComparison.Ordinal);
            var body = await form!.ReadAsStringAsync(cancellation);
            if (body.Contains("device_code=device-1", StringComparison.Ordinal))
            {
                if (delayAt == "token")
                {
                    atDelay.SetResult();
                    return await oldReply.Task;
                }
                return new JsonObject { ["access_token"] = "old-token" };
            }
            Assert.Contains("device_code=device-2", body, StringComparison.Ordinal);
            return new JsonObject { ["access_token"] = "new-token" };
        }, clock, protector);

        Task<JsonObject> Command(string action) => github.RequestAsync(action, new JsonObject(), CancellationToken.None);
        Assert.Equal("USER-1", (await Command("sign_in"))["user_code"]!.GetValue<string>());
        clock.Now = clock.Now.AddSeconds(5);
        var outdated = Command("poll");
        await atDelay.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(hasSavedAccount ? saved : null, tokens.Read(clientId));
        Assert.Equal(stop == "cancel" ? "cancelled" : "signed_out", Status(await Command(stop)));
        Assert.Equal(stop == "cancel" && hasSavedAccount ? saved : null, tokens.Read(clientId));

        Assert.Equal("USER-2", (await Command("sign_in"))["user_code"]!.GetValue<string>());
        clock.Now = clock.Now.AddSeconds(5);
        var replacement = Command("poll");
        var replacementAccount = new JsonObject { ["login"] = "replacement", ["id"] = 84 };
        if (delayAt == "unauthorized")
        {
            newAccount.SetResult(replacementAccount);
            Assert.Equal("signed_in", Status(await replacement));
            oldReply.SetException(new HttpRequestException(null, null, System.Net.HttpStatusCode.Unauthorized));
            Assert.Equal("cancelled", Status(await outdated));
        }
        else
        {
            oldReply.SetResult(delayAt == "token" ? new JsonObject { ["access_token"] = "old-token" }
                : new JsonObject { ["login"] = "obsolete", ["id"] = 42 });
            Assert.Equal("cancelled", Status(await outdated));
            Assert.Equal("auth_busy", Status(await Command("account")));
            newAccount.SetResult(replacementAccount);
            Assert.Equal("signed_in", Status(await replacement));
        }
        var account = await Command("account");
        Assert.Equal("replacement", account["account"]!["login"]!.GetValue<string>());
        Assert.Equal("new-token", JsonNode.Parse(tokens.Read(clientId)!)!["access"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("poll", "signed_in", 1, "access-1")]
    [InlineData("cancel", "cancelled", 1, null)]
    [InlineData("sign_out", "signed_out", 1, null)]
    [InlineData("sign_in", "authorizing", 2, "access-2")]
    public async Task TransientAccountLookupFailureKeepsTheTokenProvisional(string action, string status,
        int expectedExchanges, string? expectedAccess)
    {
        using var fixture = new TestDatabase();
        var directory = Path.GetDirectoryName(fixture.Path)!;
        var protector = new TestProtector();
        var clock = new TestTime();
        const string clientId = "publicClientId";
        var tokens = new SpeechKeyStore(directory, protector, "community-github.bin");
        var exchanges = 0;
        var lookups = 0;
        var github = new CommunityGithub(directory, clientId, (_, url, access, content, _) =>
        {
            content?.Dispose();
            if (url.EndsWith("/device/code", StringComparison.Ordinal))
                return Task.FromResult(new JsonObject
                {
                    ["device_code"] = $"private-{exchanges + 1}", ["user_code"] = $"USER-{exchanges + 1}",
                    ["interval"] = 5, ["expires_in"] = 60,
                });
            if (url.EndsWith("/access_token", StringComparison.Ordinal))
                return Task.FromResult(new JsonObject { ["access_token"] = $"access-{++exchanges}", ["expires_in"] = 3600 });
            Assert.EndsWith("/user", url, StringComparison.Ordinal);
            Assert.Equal($"access-{exchanges}", access);
            return ++lookups == 1
                ? Task.FromException<JsonObject>(new HttpRequestException(null, null, System.Net.HttpStatusCode.ServiceUnavailable))
                : Task.FromResult(new JsonObject { ["login"] = "player", ["id"] = 75 });
        }, clock, protector);

        Task<JsonObject> Command(string value) => github.RequestAsync(value, new JsonObject(), CancellationToken.None);
        Assert.Equal("authorizing", Status(await Command("sign_in")));
        clock.Now = clock.Now.AddSeconds(5);
        Assert.Equal("temporarily_unavailable", Status(await Command("poll")));
        Assert.Null(tokens.Read(clientId));

        // The consumed device code has now expired, but its token can still verify the account.
        clock.Now = clock.Now.AddSeconds(60);
        Assert.Equal(status, Status(await Command(action)));
        if (action == "sign_in")
        {
            clock.Now = clock.Now.AddSeconds(5);
            Assert.Equal("signed_in", Status(await Command("poll")));
        }
        else if (action != "poll")
        {
            Assert.Equal("signed_out", Status(await Command("poll")));
        }
        Assert.Equal(expectedExchanges, exchanges);
        var persisted = tokens.Read(clientId);
        Assert.Equal(expectedAccess, persisted is null ? null : JsonNode.Parse(persisted)!["access"]!.GetValue<string>());
    }

    // 投稿进度 reads the publishing workflow's labels; a submission whose items were all already
    // in the library is labelled community-duplicate and published nothing.
    [Theory]
    [InlineData("community-duplicate", "duplicate")]
    [InlineData("community-withdrawn", "withdrawn")]
    [InlineData("community-published", "published")]
    [InlineData("community-failed", "processing_failed")]
    [InlineData("", "processing")]
    public async Task IssueStatusReadsTheWorkflowLabels(string label, string expected)
    {
        using var fixture = new TestDatabase();
        var labels = label.Length == 0 ? "[]" : $$"""[{"name":"{{label}}"}]""";
        var github = new CommunityGithub(Path.GetDirectoryName(fixture.Path)!, "publicClientId",
            (method, url, token, content, cancellation) =>
            {
                content?.Dispose();
                return Task.FromResult(JsonNode.Parse(
                    $$"""{"state":"open","body":"{{CommunityGithub.IssueMarker}}","labels":{{labels}}}""")!.AsObject());
            }, new TestTime(), new TestProtector());
        var result = await github.RequestAsync("issue_status", new JsonObject { ["issue"] = 18 }, CancellationToken.None);
        Assert.Equal(expected, Status(result));
    }

    // An author withdraws published entries with a block pasted into the same Issue form; the
    // workflow reads it (test_community_publish.py, CommunityWithdrawalTests).
    [Fact]
    public async Task WithdrawalBlockNamesEachIdOnce()
    {
        using var fixture = new TestDatabase();
        using var host = CollectorHost.Open(fixture.Path, fixture.Clock);
        async Task<JsonObject> Prepare(JsonArray ids) => await new MessageDispatcher(host).DispatchAsync(new IpcRequest(
            Guid.NewGuid().ToString(), "DutyCommunity", new JsonObject { ["action"] = "prepare_withdrawal", ["withdraw"] = ids }),
            CancellationToken.None);
        var prepared = await Prepare(new JsonArray(MacroId, NoteId));
        Assert.Equal("prepared", Status(prepared));
        Assert.Equal(CommunityGithub.IssueMarker + "\n\n```json\n{\"schema_version\":1,\"withdraw\":[\"" + MacroId + "\",\"" +
            NoteId + "\"]}\n```", prepared["body"]!.GetValue<string>());
        var many = Enumerable.Range(1, 21).Select(n => (JsonNode?)$"00000000-0000-4000-8000-{n:D12}").ToArray();
        foreach (var ids in new[] { new JsonArray(), new JsonArray(MacroId, MacroId), new JsonArray("macro"), new JsonArray(many) })
            Assert.Equal("invalid_withdrawal", Status(await Prepare(ids)));
    }

    // The publisher runs the same vectors (test_community_publish.py, CommunityRepeatTests).
    [Fact]
    public void TextFieldsFollowTheSharedVectors()
    {
        var vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures",
            "shared-calibration", "community-text-vectors.json")))!;
        var cases = vectors["field_text"]!.AsArray();
        Assert.NotEmpty(cases);
        foreach (var vector in cases)
        {
            var text = vector![0]!.GetValue<string>();
            var multiline = vector[1]!.GetValue<bool>();
            var accepted = true;
            try
            {
                CommunityContent.String(new JsonObject { ["text"] = text }, "text", 1600, multiline);
            }
            catch (FormatException)
            {
                accepted = false;
            }
            var codes = string.Join(' ', text.Select(c => ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));
            Assert.True(vector[2]!.GetValue<bool>() == accepted, $"multiline={multiline}: {codes}");
        }
        var title = Draft(MacroId, "macro", "/p x");
        title["title"] = "Example\u202e";
        Assert.Equal("invalid_title", Assert.Throws<FormatException>(() => CommunityContent.Validate(title)).Message);
    }

    // GitCode can answer before it has synchronised a new commit; its reply must not hide GitHub's.
    [Theory]
    [InlineData("unusable")]
    [InlineData("oversize")]
    public async Task PublicationWaitsForSlowGithubWhenTheMirrorReplyIsUnusable(string mirror)
    {
        var github = new TaskCompletionSource<byte[]?>();
        var calls = 0;
        var result = await CommunityHttp.GetPublicationAsync(new string('a', 40), "snapshot.json", (_, _) =>
        {
            if (++calls == 1)
                return github.Task;
            // GitHub delivers only after the mirror has answered, so the mirror's reply comes first.
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                github.SetResult("good"u8.ToArray());
            });
            return mirror == "oversize"
                ? Task.FromException<byte[]?>(new FormatException("download_limit"))
                : Task.FromResult<byte[]?>("placeholder"u8.ToArray());
        }, TimeSpan.FromMilliseconds(1), CancellationToken.None, bytes => bytes.SequenceEqual("good"u8.ToArray()));
        Assert.Equal("good"u8.ToArray(), result);
        Assert.Equal(2, calls);

        // When neither source has a usable file, the refused reply reaches the caller's own check.
        var both = await CommunityHttp.GetPublicationAsync("main", "index.json",
            (_, _) => Task.FromResult<byte[]?>("placeholder"u8.ToArray()), TimeSpan.FromMinutes(1),
            CancellationToken.None, _ => false);
        Assert.Equal("placeholder"u8.ToArray(), both);
    }

    [Fact]
    public void InstallTakesOnlyAvailableDirectLinksAndAnUpdateAppliesPastAWithdrawnLink()
    {
        using var fixture = new TestDatabase();
        var repository = new CommunityRepository(fixture.Database);
        const string WithdrawnId = "00000000-0000-4000-8000-000000000001";
        const string FurtherId = "00000000-0000-4000-8000-000000000002";
        var note = Draft(NoteId, "reminder", "Follow the macro");
        note["links"] = new JsonArray(WithdrawnId, MacroId);
        var macro = Draft(MacroId, "macro", "/p <t>");
        macro["links"] = new JsonArray(FurtherId);
        var withdrawn = Published(Draft(WithdrawnId, "macro", "/p old"), 1);
        withdrawn["status"] = "withdrawn";
        var further = Published(Draft(FurtherId, "reminder", "Not selected"), 1);
        Cache(repository, 1, Published(note, 1), Published(macro, 1), withdrawn, further);
        var workspace = Open(fixture);
        Assert.Equal("installed", Status(workspace.Change("install", Guid.NewGuid().ToString(), new JsonObject { ["id"] = NoteId })));
        var rows = workspace.List(0)["items"]!.AsArray();
        Assert.Equal(new[] { NoteId, MacroId }, rows.Select(LocalRowId));
        Assert.Equal(new[] { MacroId }, rows[0]!["content"]!["links"]!.AsArray().Select(link => link!.GetValue<string>()));
        Assert.Empty(rows[1]!["content"]!["links"]!.AsArray());

        var next = note.DeepClone().AsObject();
        next["body"] = "Follow the new macro";
        Cache(repository, 2, Published(next, 2), Published(macro, 1), withdrawn, further);
        workspace = Open(fixture);
        Assert.Equal("updated", Status(workspace.Change("apply", Guid.NewGuid().ToString(), new JsonObject
        {
            ["id"] = NoteId, ["revision"] = 1, ["source_revision"] = 2, ["mode"] = "replace",
        })));
        var updated = workspace.List(0)["items"]!.AsArray().Single(row => LocalRowId(row) == NoteId)!;
        Assert.Equal("Follow the new macro", updated["content"]!["body"]!.GetValue<string>());
        Assert.Equal(new[] { MacroId }, updated["content"]!["links"]!.AsArray().Select(link => link!.GetValue<string>()));
        // The edited entry can be saved again: none of its links points at something missing.
        Assert.Equal("saved", Status(workspace.Change("save", Guid.NewGuid().ToString(),
            Save(updated["content"]!.AsObject(), 2))));
    }

    [Theory]
    [InlineData("keep", true)]
    [InlineData("replace", false)]
    public void WithdrawnSourceLinkAlreadyInstalledIsUsedOnlyWhenKeptAsALocalEdit(string mode, bool kept)
    {
        using var fixture = new TestDatabase();
        var repository = new CommunityRepository(fixture.Database);
        var macro = Published(Draft(MacroId, "macro", "/p Former macro"), 1);
        Cache(repository, 1, macro);
        var workspace = Open(fixture);
        string Change(string action, JsonObject payload) =>
            Status(workspace.Change(action, Guid.NewGuid().ToString(), payload));
        JsonObject Note() => workspace.List(0)["items"]!.AsArray()
            .Single(row => LocalRowId(row) == NoteId)!["content"]!.AsObject();
        Assert.Equal("installed", Change("install", new() { ["id"] = MacroId }));

        macro["status"] = "withdrawn";
        var note = Draft(NoteId, "reminder", "A note");
        note["links"] = new JsonArray(MacroId);
        Cache(repository, 2, Published(note, 1), macro);
        workspace = Open(fixture);
        Assert.Equal("installed", Change("install", new() { ["id"] = NoteId }));
        Assert.Empty(Note()["links"]!.AsArray());

        var edited = Note().DeepClone().AsObject();
        edited["links"] = new JsonArray(MacroId);
        Assert.Equal("saved", Change("save", Save(edited, 1)));
        note["body"] = "A new revision";
        Cache(repository, 3, Published(note, 2), macro);
        workspace = Open(fixture);
        Assert.Equal("updated", Change("apply", new()
        {
            ["id"] = NoteId, ["revision"] = 2, ["source_revision"] = 2, ["mode"] = mode,
        }));
        Assert.True(JsonNode.DeepEquals(Note()["links"], kept ? new JsonArray(MacroId) : new JsonArray()));
    }

    [Fact]
    public void DeleteDropsLinksToTheEntryWithoutMovingTheLinkingEntry()
    {
        using var fixture = new TestDatabase();
        var workspace = Open(fixture);
        const string OtherId = "00000000-0000-4000-8000-000000000003";
        Assert.Equal("saved", Status(workspace.Change("save", Guid.NewGuid().ToString(), Save(Draft(MacroId, "macro", "/p x"), 0))));
        var note = Draft(NoteId, "reminder", "Linked");
        note["links"] = new JsonArray(MacroId);
        Assert.Equal("saved", Status(workspace.Change("save", Guid.NewGuid().ToString(), Save(note, 0))));
        Assert.Equal("saved", Status(workspace.Change("save", Guid.NewGuid().ToString(), Save(Draft(OtherId, "reminder", "Newest"), 0))));

        var delete = new JsonObject { ["id"] = MacroId, ["revision"] = 2 };
        Assert.Equal("local_conflict", Status(workspace.Change("delete", Guid.NewGuid().ToString(), delete)));
        delete["revision"] = 1;
        Assert.Equal("deleted", Status(workspace.Change("delete", Guid.NewGuid().ToString(), delete)));
        var rows = workspace.List(0)["items"]!.AsArray();
        Assert.Equal(new[] { OtherId, NoteId }, rows.Select(LocalRowId));
        Assert.Empty(rows[1]!["content"]!["links"]!.AsArray());
        Assert.Equal(2, rows[1]!["revision"]!.GetValue<int>());
    }

    [Fact]
    public void ForkCopiesAnInstalledEntryUnderANewIdAndKeepsItsSource()
    {
        using var fixture = new TestDatabase();
        var repository = new CommunityRepository(fixture.Database);
        const string ForkId = "00000000-0000-4000-8000-000000000004";
        Cache(repository, 1, Published(Draft(MacroId, "macro", "/p <t>"), 1));
        var workspace = Open(fixture);
        Assert.Equal("installed", Status(workspace.Change("install", Guid.NewGuid().ToString(), new JsonObject { ["id"] = MacroId })));
        Assert.Equal("local_conflict", Status(workspace.Change("fork", Guid.NewGuid().ToString(),
            new JsonObject { ["id"] = MacroId, ["local_id"] = ForkId, ["revision"] = 2 })));
        Assert.Equal("saved", Status(workspace.Change("fork", Guid.NewGuid().ToString(),
            new JsonObject { ["id"] = MacroId, ["local_id"] = ForkId, ["revision"] = 1 })));
        var fork = workspace.List(0)["items"]!.AsArray().Single(row => LocalRowId(row) == ForkId)!;
        Assert.Equal(1, fork["revision"]!.GetValue<int>());
        Assert.Equal(MacroId, fork["upstream"]!["id"]!.GetValue<string>());
        Assert.Equal("/p <t>", fork["content"]!["body"]!.GetValue<string>());
        Assert.Equal("local_conflict", Status(workspace.Change("fork", Guid.NewGuid().ToString(),
            new JsonObject { ["id"] = MacroId, ["local_id"] = ForkId, ["revision"] = 1 })));

        Assert.Equal("saved", Status(workspace.Change("save", Guid.NewGuid().ToString(), Save(Draft(NoteId, "reminder", "Mine"), 0))));
        Assert.Equal("local_conflict", Status(workspace.Change("fork", Guid.NewGuid().ToString(),
            new JsonObject { ["id"] = NoteId, ["local_id"] = "00000000-0000-4000-8000-000000000005", ["revision"] = 1 })));
    }

    [Fact]
    public void ARequestIdIsAnsweredOnlyForItsOwnPayloadAndOnlyTheNewestReceiptsAreKept()
    {
        using var fixture = new TestDatabase();
        var workspace = Open(fixture);
        var request = Guid.NewGuid().ToString();
        Assert.Equal("saved", Status(workspace.Change("save", request, Save(Draft(MacroId, "macro", "/p a"), 0))));
        Assert.Equal("request_conflict", Status(workspace.Change("save", request, Save(Draft(MacroId, "macro", "/p b"), 0))));

        var repository = new CommunityRepository(fixture.Database);
        var ids = Enumerable.Range(0, CommunityRepository.KeptReceipts + 5).Select(_ => Guid.NewGuid().ToString()).ToArray();
        foreach (var id in ids)
            repository.Change(id, "first", _ => CommunityWorkspace.Result("done"));
        using (var count = fixture.Database.CreateCommand())
        {
            count.CommandText = "SELECT count(*) FROM community_receipts";
            Assert.Equal(CommunityRepository.KeptReceipts, (long)count.ExecuteScalar()!);
        }
        Assert.Equal("request_conflict", Status(repository.Change(ids[^1], "second", _ => CommunityWorkspace.Result("again"))));
        Assert.Equal("again", Status(repository.Change(ids[0], "second", _ => CommunityWorkspace.Result("again"))));
    }

    [Fact]
    public async Task SubmitAfterARestartReadsTheAccountOfTheStoredToken()
    {
        using var fixture = new TestDatabase();
        var directory = Path.GetDirectoryName(fixture.Path)!;
        var protector = new TestProtector();
        const string clientId = "publicClientId";
        new SpeechKeyStore(directory, protector, "community-github.bin")
            .Write(clientId, """{"access":"saved-token","refresh":"","expires":4102444800}""");
        var github = new CommunityGithub(directory, clientId, (method, url, token, content, _) =>
        {
            content?.Dispose();
            Assert.Equal("saved-token", token);
            return Task.FromResult(url.EndsWith("/user", StringComparison.Ordinal)
                ? new JsonObject { ["login"] = "player", ["id"] = 75 }
                : new JsonObject { ["number"] = 21 });
        }, new TestTime(), protector);
        JsonObject Submission(long author) => new()
        {
            ["license"] = "CC-BY-4.0", ["consent"] = true, ["author_id"] = author,
            ["base_revisions"] = new JsonObject(), ["items"] = new JsonArray(Draft(NoteId, "reminder", "Test")),
        };
        Assert.Equal("account_changed", Status(await github.RequestAsync("submit", Submission(76), CancellationToken.None)));
        var submitted = await github.RequestAsync("submit", Submission(75), CancellationToken.None);
        Assert.Equal("submitted", Status(submitted));
        Assert.Equal(21, submitted["issue"]!.GetValue<int>());
    }

    // Nothing reached GitHub when the token or account lookup failed; after the POST it may have.
    [Theory]
    [InlineData("/user", "submit_not_sent")]
    [InlineData("/issues", "submit_unconfirmed")]
    public async Task ASubmitThatFailsBeforeItsIssueIsPostedSaysNothingWasSent(string failing, string expected)
    {
        using var fixture = new TestDatabase();
        var directory = Path.GetDirectoryName(fixture.Path)!;
        var protector = new TestProtector();
        var clock = new TestTime();
        new SpeechKeyStore(directory, protector, "community-github.bin")
            .Write("publicClientId", """{"access":"saved-token","refresh":"","expires":4102444800}""");
        var github = new CommunityGithub(directory, "publicClientId", (_, url, _, content, _) =>
        {
            content?.Dispose();
            if (url.EndsWith(failing, StringComparison.Ordinal))
                return Task.FromException<JsonObject>(new HttpRequestException());
            return Task.FromResult(new JsonObject { ["login"] = "player", ["id"] = 75 });
        }, clock, protector);
        var submission = new JsonObject
        {
            ["license"] = "CC-BY-4.0", ["consent"] = true, ["author_id"] = 75,
            ["base_revisions"] = new JsonObject(), ["items"] = new JsonArray(Draft(NoteId, "reminder", "Test")),
        };
        Assert.Equal(expected, Status(await github.RequestAsync("submit", submission, CancellationToken.None)));
        Assert.Equal(new CommunityLibrary.RequestRecord(clock.Now, expected), github.LastRequest);
    }

    // The report names the community request sent last, the library fetch or a GitHub request.
    [Fact]
    public async Task TheReportKeepsTheMostRecentlySentCommunityRequest()
    {
        using var fixture = new TestDatabase();
        var clock = new TestTime();
        var github = new CommunityGithub(Path.GetDirectoryName(fixture.Path)!, "publicClientId", (_, _, _, content, _) =>
        {
            content?.Dispose();
            return Task.FromResult(JsonNode.Parse(
                $$"""{"state":"open","body":"{{CommunityGithub.IssueMarker}}","labels":[{"name":"community-published"}]}""")!.AsObject());
        }, clock, new TestProtector());
        Assert.Equal("cancelled", Status(await github.RequestAsync("cancel", new JsonObject(), CancellationToken.None)));
        Assert.Null(github.LastRequest);
        var library = new CommunityLibrary(new CommunityRepository(fixture.Database),
            new SettingsRepository(fixture.Database, fixture.Clock), killSwitch: () => false);
        library.RecordRequest(new(clock.Now, "ready"));
        clock.Now = clock.Now.AddMinutes(1);
        await github.RequestAsync("issue_status", new JsonObject { ["issue"] = 18 }, CancellationToken.None);
        library.RecordRequest(github.LastRequest);
        Assert.Equal<(DateTimeOffset?, string?)>((clock.Now, "published"), LastRequest(library));
        library.RecordRequest(new(clock.Now.AddMinutes(-5), "ready"));
        Assert.Equal<(DateTimeOffset?, string?)>((clock.Now, "published"), LastRequest(library));
    }

    // Entries published before the character rule are not judged again by it: the client leaves
    // such an entry out (and the links to it), and a local entry holding one stays editable.
    [Fact]
    public void ContentOlderThanTheCharacterRuleIsLeftOutOrStaysEditable()
    {
        using var fixture = new TestDatabase();
        var repository = new CommunityRepository(fixture.Database);
        var legacy = Draft(MacroId, "macro", "/p <t>");
        legacy["title"] = "Example \u200d";
        var note = Draft(NoteId, "reminder", "Follow the macro");
        note["links"] = new JsonArray(MacroId);
        Cache(repository, 1, Published(note, 1), Published(legacy, 1));
        var workspace = Open(fixture);
        Assert.Equal("source_unavailable", Status(workspace.Change("install", Guid.NewGuid().ToString(), new JsonObject { ["id"] = MacroId })));
        Assert.Equal("installed", Status(workspace.Change("install", Guid.NewGuid().ToString(), new JsonObject { ["id"] = NoteId })));
        var installed = Assert.Single(workspace.List(0)["items"]!.AsArray())!;
        Assert.Empty(installed["content"]!["links"]!.AsArray());

        // Any other failure still rejects the whole snapshot: a control character, or a link
        // from a left-out entry to one of its own type.
        var control = Published(Draft(MacroId, "macro", "/p <t>"), 1);
        control["title"] = "Example \u0001";
        var sameType = Published(Draft("00000000-0000-4000-8000-000000000006", "reminder", "x"), 1);
        sameType["title"] = "Example \u200b";
        sameType["links"] = new JsonArray(NoteId);
        var plain = Published(Draft(NoteId, "reminder", "Plain"), 1);
        foreach (var items in new[] { new[] { plain, control }, new[] { plain, sameType } })
        {
            var (index, bytes) = Publication(1, items);
            Assert.Throws<FormatException>(() => CommunityLibrary.ReadPublication(CommunityLibrary.ReadPointer(index), bytes));
        }

        var old = Draft(MacroId, "macro", "/p <t>");
        old["tags"] = new JsonArray("标\u200b签");
        repository.Change(Guid.NewGuid().ToString(), "seed", tx =>
        {
            repository.Save(new JsonObject
            {
                ["revision"] = 1, ["position"] = 0, ["favorite"] = false, ["content"] = old.DeepClone(), ["upstream"] = null,
            }, tx);
            return CommunityWorkspace.Result("done");
        });
        var edited = old.DeepClone().AsObject();
        edited["body"] = "/p <me>";
        Assert.Equal("saved", Status(workspace.Change("save", Guid.NewGuid().ToString(), Save(edited, 1))));
        edited["title"] = "Example \u202e";
        Assert.Equal("invalid_title", Assert.Throws<FormatException>(() =>
            workspace.Change("save", Guid.NewGuid().ToString(), Save(edited, 2))).Message);
    }

    private static (DateTimeOffset?, string?) LastRequest(CommunityLibrary library)
    {
        var diagnostics = library.Diagnostics();
        return (diagnostics.LastRequestAtUtc, diagnostics.LastOutcome);
    }

    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Select(value => (byte)(value ^ 0x42)).ToArray();
        public byte[] Unprotect(byte[] ciphertext) => Protect(ciphertext);
    }

    private static string Status(JsonObject result) => result["status"]!.GetValue<string>();

    private static string LocalRowId(JsonNode? row) => row!["content"]!["id"]!.GetValue<string>();

    private static JsonNode[] ReadPagedLocalRows(CommunityWorkspace workspace)
    {
        var first = workspace.List(0);
        Assert.Equal(CommunityContent.PageSize, first["items"]!.AsArray().Count);
        Assert.Equal(CommunityContent.PageSize, first["next"]!.GetValue<int>());
        var second = workspace.List(first["next"]!.GetValue<int>());
        Assert.Null(second["next"]);
        Assert.Equal(first["generation"]!.GetValue<long>(), second["generation"]!.GetValue<long>());
        return first["items"]!.AsArray().Concat(second["items"]!.AsArray()).Select(row => row!).ToArray();
    }

}
