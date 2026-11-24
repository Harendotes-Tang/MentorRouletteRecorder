using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// Character profiles over a real pipe: the three profile messages, the answer about a character
/// the Collector told apart, the profile fields of the run, filter, baseline, batch, settings and
/// dashboard messages, and every answer held to the contract.
/// </summary>
public sealed class CharacterProfileIpcTests
{
    [Fact]
    public async Task APreviouslyBoundCharacterConfirmsTheNameOfItsOriginalProfileOverThePipe()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        const string key = "0a1b2c3d";
        var digest = new string('1', 32);
        var previous = fixture.Host.Identities.Observe(Region.Cn, key, digest);
        fixture.Host.Database.RunInTransaction(tx =>
        {
            using var seed = fixture.Host.Database.CreateCommand();
            seed.Transaction = tx;
            seed.CommandText = """
                UPDATE character_profiles SET baseline_completed_count = 227 WHERE profile_id = $profile;
                INSERT INTO character_profile_registrations (profile_id) VALUES ($profile);
                """;
            seed.Parameters.AddWithValue("$profile", previous.ProfileId!.Value);
            seed.ExecuteNonQuery();
        });
        Assert.True(fixture.Host.Identities.Observe(Region.Cn, key, digest).StatisticsChanged);

        var dashboard = (await client.SendAsync("GetDashboardStats", new JsonObject())).Require();
        ContractSchema.Validate("$defs/Responses/GetDashboardStats", dashboard, "registration dashboard");
        var prompt = Assert.Single(dashboard["identities"]!.AsArray())!;
        Assert.Equal("BOUND", prompt["state"]!.GetValue<string>());
        Assert.Equal(previous.ProfileId, prompt["registration_profile_id"]!.GetValue<long>());
        var result = (await client.SendAsync("ResolveCharacterIdentity", new JsonObject
        {
            ["identity_id"] = previous.IdentityId, ["action"] = "BIND",
            ["profile_id"] = previous.ProfileId, ["display_name"] = "白露",
        })).Require();
        ContractSchema.Validate("$defs/Responses/ResolveCharacterIdentity", result, "registration result");
        Assert.Empty(result["identities"]!.AsArray());
        var profile = result["profiles"]!.AsArray().Single(row => row!["region"]!.GetValue<string>() == "CN")!;
        Assert.Equal((previous.ProfileId!.Value, "白露", 227, 1), (
            profile["profile_id"]!.GetValue<long>(), profile["display_name"]!.GetValue<string>(),
            profile["baseline_completed_count"]!.GetValue<int>(), profile["identity_count"]!.GetValue<int>()));
    }

    [Fact]
    public async Task ASecondCharacterKeepsItsOwnProgressAndRunsCanBeMovedBetweenCharacters()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        async Task<JsonObject> Send(string message, JsonObject payload) =>
            ContractSchema.Answered(await client.SendAsync(message, payload), message + " profile response");

        async Task<JsonObject> CreateRun() => (await Send("CreateManualRun", new JsonObject
        {
            ["region"] = "CN", ["result"] = "COMPLETED", ["contributes_to_goal"] = true,
            ["entered_at_utc"] = "2026-10-24T01:01:00.000Z", ["ended_at_utc"] = "2026-10-24T01:11:00.000Z",
            ["content_id"] = 5, ["job_id"] = 19, ["reason"] = "按角色补录测试",
        }))["run"]!.AsObject();

        JsonObject Targets(JsonObject run, int revision) => new()
        {
            ["run_id"] = run["run_id"]!.GetValue<string>(), ["expected_revision"] = revision,
        };

        var first = await CreateRun();
        var main = first["profile_id"]!.GetValue<int>();

        var created = await Send("CreateCharacterProfile", new JsonObject
        {
            ["region"] = "CN", ["display_name"] = "小号", ["baseline_completed_count"] = 300, ["make_current"] = true,
        });
        var alt = created["profile_id"]!.GetValue<int>();
        Assert.False(created["deleted"]!.GetValue<bool>());
        Assert.Equal(2, created["profiles"]!.AsArray().Count(row => row!["region"]!.GetValue<string>() == "CN"));

        // A new record goes to the character that is current now; the old one stays where it was.
        var second = await CreateRun();
        Assert.Equal(alt, second["profile_id"]!.GetValue<int>());
        var dashboard = await Send("GetDashboardStats", new JsonObject());
        var china = dashboard["achievements"]!.AsArray().Single(row => row!["region"]!.GetValue<string>() == "CN")!;
        Assert.Equal((alt, 2, 0, 301), (china["profile_id"]!.GetValue<int>(), china["profile_count"]!.GetValue<int>(),
            china["unassigned_run_count"]!.GetValue<int>(), china["achievement_progress"]!.GetValue<int>()));
        var profiles = dashboard["profiles"]!.AsArray().ToDictionary(row => row!["profile_id"]!.GetValue<int>(), row => row!);
        Assert.Equal((1, false, ""), (profiles[main]["achievement_progress"]!.GetValue<int>(),
            profiles[main]["is_current"]!.GetValue<bool>(), profiles[main]["display_name"]!.GetValue<string>()));
        Assert.Equal((301, true, "小号"), (profiles[alt]["achievement_progress"]!.GetValue<int>(),
            profiles[alt]["is_current"]!.GetValue<bool>(), profiles[alt]["display_name"]!.GetValue<string>()));
        var narrowed = await Send("GetDashboardStats", new JsonObject { ["filter"] = new JsonObject { ["profile_id"] = main } });
        Assert.Equal((1, 1), (narrowed["completed_count"]!.GetValue<int>(), narrowed["achievement_progress"]!.GetValue<int>()));

        // Each character has its own baseline.
        var baseline = await Send("UpdateAchievementBaseline", new JsonObject
        {
            ["profile_id"] = main, ["baseline_completed_count"] = 50, ["goal_count"] = 2000,
            ["baseline_effective_at"] = "2026-10-24T01:20:00.000Z", ["reason"] = "大号的起始次数",
        });
        Assert.Equal((main, "CN"), (baseline["profile_id"]!.GetValue<int>(), baseline["region"]!.GetValue<string>()));

        // Moving a record changes whose progress it counts in, with a revision that says so.
        var moved = await Send("BatchMutateRuns", new JsonObject
        {
            ["action"] = "assign_profile", ["profile_id"] = main, ["reason"] = "记错了角色",
            ["runs"] = new JsonArray(Targets(first, 1), Targets(second, 1)),
        });
        Assert.Equal((1, "assign_profile"), (moved["changed_count"]!.GetValue<int>(), moved["action"]!.GetValue<string>()));
        Assert.Equal(second["run_id"]!.GetValue<string>(), Assert.Single(moved["run_ids"]!.AsArray())!.GetValue<string>());
        var revisions = await Send("GetRunRevisions", new JsonObject { ["run_id"] = second["run_id"]!.GetValue<string>() });
        var change = Assert.Single(revisions["items"]!.AsArray().Last()!["changes"]!.AsArray())!;
        Assert.Equal(("profile_id", alt, main), (change["field"]!.GetValue<string>(),
            change["old_value"]!.GetValue<int>(), change["new_value"]!.GetValue<int>()));
        var ofAlt = await Send("QueryRuns", new JsonObject { ["filter"] = new JsonObject { ["profile_id"] = alt } });
        Assert.Empty(ofAlt["items"]!.AsArray());

        await Send("BatchMutateRuns", new JsonObject
        {
            ["action"] = "assign_profile", ["profile_id"] = null, ["reason"] = "不确定是哪个角色",
            ["runs"] = new JsonArray(Targets(second, 2)),
        });
        var loose = await Send("QueryRuns", new JsonObject { ["filter"] = new JsonObject { ["profile_unassigned"] = true } });
        Assert.Null(Assert.Single(loose["items"]!.AsArray())!["profile_id"]);
        dashboard = await Send("GetDashboardStats", new JsonObject());
        china = dashboard["achievements"]!.AsArray().Single(row => row!["region"]!.GetValue<string>() == "CN")!;
        Assert.Equal(1, china["unassigned_run_count"]!.GetValue<int>());

        // What the contract refuses.
        async Task<string?> Refused(string message, JsonObject payload) => (await client.SendAsync(message, payload)).ErrorCode;
        Assert.Equal(ErrorCodes.BadRequest, await Refused("QueryRuns", new JsonObject
            { ["filter"] = new JsonObject { ["profile_id"] = main, ["profile_unassigned"] = true } }));
        Assert.Equal(ErrorCodes.BadRequest, await Refused("BatchMutateRuns", new JsonObject
            { ["action"] = "assign_profile", ["reason"] = "漏了角色", ["runs"] = new JsonArray(Targets(first, 1)) }));
        Assert.Equal(ErrorCodes.BadRequest, await Refused("BatchMutateRuns", new JsonObject
            { ["action"] = "soft_delete", ["profile_id"] = main, ["reason"] = "多了角色", ["runs"] = new JsonArray(Targets(first, 1)) }));
        Assert.Equal(ErrorCodes.BadRequest, await Refused("DeleteCharacterProfile", new JsonObject { ["profile_id"] = alt }));
        Assert.Equal(ErrorCodes.BadRequest, await Refused("UpdateCharacterProfile", new JsonObject { ["profile_id"] = alt }));
        Assert.Equal(ErrorCodes.NotFound, await Refused("UpdateCharacterProfile", new JsonObject
            { ["profile_id"] = 9999, ["make_current"] = true }));

        // Renaming, switching back, and removing the character nothing belongs to any more.
        var renamed = await Send("UpdateCharacterProfile", new JsonObject
            { ["profile_id"] = main, ["display_name"] = "大号", ["make_current"] = true });
        Assert.Contains(renamed["profiles"]!.AsArray(), row =>
            row!["profile_id"]!.GetValue<int>() == main && row["is_current"]!.GetValue<bool>() &&
            row["display_name"]!.GetValue<string>() == "大号");
        var removed = await Send("DeleteCharacterProfile", new JsonObject { ["profile_id"] = alt });
        Assert.True(removed["deleted"]!.GetValue<bool>());
        Assert.DoesNotContain(removed["profiles"]!.AsArray(), row => row!["profile_id"]!.GetValue<int>() == alt);
    }

    [Fact]
    public async Task ACharacterToldApartIsShownAnsweredForgottenAndIgnoredOverThePipe()
    {
        const string key = "0a1b2c3d";
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        async Task<JsonObject> Send(string message, JsonObject payload) =>
            ContractSchema.Answered(await client.SendAsync(message, payload), message + " identity response");

        async Task<string?> Refused(string message, JsonObject payload) => (await client.SendAsync(message, payload)).ErrorCode;
        JsonNode China(JsonObject dashboard) =>
            dashboard["achievements"]!.AsArray().Single(row => row!["region"]!.GetValue<string>() == "CN")!;

        // What capture does when characters log in: the first is the region's one profile, the
        // second waits for the player.
        var main = (int)fixture.Host.Identities.Observe(Region.Cn, key, new string('1', 32)).ProfileId!.Value;
        var second = (int)fixture.Host.Identities.Observe(Region.Cn, key, new string('2', 32)).IdentityId!.Value;
        var third = (int)fixture.Host.Identities.Observe(Region.Cn, key, new string('3', 32)).IdentityId!.Value;

        var dashboard = await Send("GetDashboardStats", new JsonObject());
        Assert.Equal("AUTOMATIC", China(dashboard)["identification"]!.GetValue<string>());
        Assert.Equal(new[] { second, third }, dashboard["identities"]!.AsArray().Select(row => row!["identity_id"]!.GetValue<int>()));
        Assert.All(dashboard["identities"]!.AsArray(), row => Assert.Equal("PENDING", row!["state"]!.GetValue<string>()));
        Assert.Equal(1, dashboard["profiles"]!.AsArray().Single(row => row!["profile_id"]!.GetValue<int>() == main)!["identity_count"]!.GetValue<int>());
        Assert.DoesNotContain(key, dashboard.ToJsonString());
        Assert.DoesNotContain(new string('2', 32), dashboard.ToJsonString());

        // A new character, named and bound in one request. Nobody is logged in here, so the
        // answer does not change which profile is current.
        var created = await Send("CreateCharacterProfile", new JsonObject
            { ["region"] = "CN", ["display_name"] = "小号", ["identity_id"] = second });
        var alt = created["profile_id"]!.GetValue<int>();
        Assert.Contains(created["profiles"]!.AsArray(), row =>
            row!["profile_id"]!.GetValue<int>() == alt && !row["is_current"]!.GetValue<bool>() && row["identity_count"]!.GetValue<int>() == 1);
        // The character logging in is what makes its profile current.
        Assert.True(fixture.Host.Identities.Recognise(Region.Cn, key, new string('2', 32)).StatisticsChanged);

        // The third is the first character under another identifier.
        var bound = await Send("ResolveCharacterIdentity", new JsonObject
            { ["identity_id"] = third, ["action"] = "BIND", ["profile_id"] = main });
        Assert.Equal((main, 0, false), (bound["profile_id"]!.GetValue<int>(), bound["assigned_run_count"]!.GetValue<int>(),
            bound["idempotent_replay"]!.GetValue<bool>()));
        Assert.Empty(bound["identities"]!.AsArray());
        Assert.Contains(bound["profiles"]!.AsArray(), row =>
            row!["profile_id"]!.GetValue<int>() == main && !row["is_current"]!.GetValue<bool>() && row["identity_count"]!.GetValue<int>() == 2);

        // Released, both come back to be asked about; one is ignored and asked about again.
        await Send("UpdateCharacterProfile", new JsonObject { ["profile_id"] = main, ["forget_identities"] = true });
        dashboard = await Send("GetDashboardStats", new JsonObject());
        Assert.Equal(2, dashboard["identities"]!.AsArray().Count);
        var first = dashboard["identities"]!.AsArray().Select(row => row!["identity_id"]!.GetValue<int>()).First(id => id != third);
        var ignored = await Send("ResolveCharacterIdentity", new JsonObject { ["identity_id"] = first, ["action"] = "IGNORE" });
        Assert.Null(ignored["profile_id"]);
        Assert.Equal("IGNORED", ignored["identities"]!.AsArray().Single(row => row!["identity_id"]!.GetValue<int>() == first)!["state"]!.GetValue<string>());
        var asked = await Send("ResolveCharacterIdentity", new JsonObject { ["identity_id"] = first, ["action"] = "ASK_AGAIN" });
        Assert.All(asked["identities"]!.AsArray(), row => Assert.Equal("PENDING", row!["state"]!.GetValue<string>()));

        // What the contract refuses.
        Assert.Equal(ErrorCodes.BadRequest, await Refused("ResolveCharacterIdentity", new JsonObject
            { ["identity_id"] = first, ["action"] = "BIND" }));
        Assert.Equal(ErrorCodes.BadRequest, await Refused("ResolveCharacterIdentity", new JsonObject
            { ["identity_id"] = first, ["action"] = "IGNORE", ["profile_id"] = main }));
        Assert.Equal(ErrorCodes.BadRequest, await Refused("ResolveCharacterIdentity", new JsonObject
            { ["identity_id"] = second, ["action"] = "BIND", ["profile_id"] = main }));
        Assert.Equal(ErrorCodes.NotFound, await Refused("ResolveCharacterIdentity", new JsonObject
            { ["identity_id"] = 9999, ["action"] = "IGNORE" }));
        Assert.Equal(ErrorCodes.BadRequest, await Refused("ResolveCharacterIdentity", new JsonObject
            { ["identity_id"] = first, ["action"] = "LATER" }));

        // The switch: off makes every region manual and says so; it is a capture setting like the others.
        var settings = await Send("UpdateCaptureSettings", new JsonObject { ["character_identification_enabled"] = false });
        Assert.False(settings["character_identification_enabled"]!.GetValue<bool>());
        dashboard = await Send("GetDashboardStats", new JsonObject());
        Assert.All(dashboard["achievements"]!.AsArray(), row => Assert.Equal("MANUAL_OFF", row!["identification"]!.GetValue<string>()));
        Assert.Equal(ErrorCodes.BadRequest, await Refused("UpdateCaptureSettings", new JsonObject
            { ["character_identification_enabled"] = "yes" }));
    }
}
