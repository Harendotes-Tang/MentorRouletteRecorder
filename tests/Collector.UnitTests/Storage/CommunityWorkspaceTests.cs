using System.Security.Cryptography;
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
                CREATE INDEX community_local_order ON community_local(position, id);
                DELETE FROM schema_migrations WHERE version = 19;
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
            foreach (var legacyId in legacyIds.Reverse())
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

        var editRequest = Guid.NewGuid().ToString();
        var edit = Save(Draft(legacyIds[^1], "reminder", "Edited text"), 1);
        Assert.Equal("saved", Status(workspace.Change("save", editRequest, edit)));
        Assert.Equal(legacyIds[^1], LocalRowId(ReadPagedLocalRows(workspace)[0]));
        Assert.Equal("saved", Status(workspace.Change("save", Guid.NewGuid().ToString(),
            Save(Draft(MacroId, "macro", "/p New macro"), 0))));
        var favorite = Save(Draft(legacyIds[1], "reminder", "Legacy text"), 1, favorite: true);
        favorite["position"] = -2;
        Assert.Equal("saved", Status(workspace.Change("save", Guid.NewGuid().ToString(), favorite)));

        fixture.Reopen();
        workspace = Open(fixture);
        Assert.Equal("saved", Status(workspace.Change("save", editRequest, edit)));
        var rows = ReadPagedLocalRows(workspace);
        Assert.Equal(new[] { MacroId, legacyIds[^1] }.Concat(legacyOrder[..^1]), rows.Select(LocalRowId));
        Assert.Equal(2, rows[1]["revision"]!.GetValue<int>());
        Assert.Equal(-2, rows[2]["position"]!.GetValue<int>());
        Assert.True(rows[2]["favorite"]!.GetValue<bool>());
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

    private static string LocalRowId(JsonNode? row) => row!["content"]!["id"]!.GetValue<string>();

    private static JsonNode[] ReadPagedLocalRows(CommunityWorkspace workspace)
    {
        var first = workspace.List(0);
        Assert.Equal(CommunityContent.PageSize, first["items"]!.AsArray().Count);
        Assert.Equal(CommunityContent.PageSize, first["next"]!.GetValue<int>());
        var second = workspace.List(first["next"]!.GetValue<int>());
        Assert.Null(second["next"]);
        return first["items"]!.AsArray().Concat(second["items"]!.AsArray()).Select(row => row!).ToArray();
    }

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
