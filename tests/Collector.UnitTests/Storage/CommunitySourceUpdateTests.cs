using System.Text.Json.Nodes;
using MentorRecorder.Collector.Community;
using MentorRecorder.Collector.Storage.Repositories;
using static MentorRecorder.Collector.UnitTests.CommunityTestData;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CommunitySourceUpdateTests
{
    private const string Reminder = "00000000-0000-4000-8000-000000000001";
    private const string Macro = "00000000-0000-4000-8000-000000000002";
    private const string Other = "00000000-0000-4000-8000-000000000003";
    private const string Fork = "00000000-0000-4000-8000-000000000004";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UntouchedProjectedLinksAdoptTheUpdateAfterReopening(bool fork)
    {
        using var fixture = new TestDatabase();
        var reminder = Draft(Reminder, "reminder", "See macro");
        reminder["links"] = new JsonArray(Macro);
        var macro = Draft(Macro, "macro", "/p First");
        macro["links"] = new JsonArray(Other);
        var other = Published(Draft(Other, "reminder", "Further note"), 1);
        var repository = new CommunityRepository(fixture.Database);
        Cache(repository, 1, Published(reminder, 1), Published(macro, 1), other);
        var workspace = Open(fixture);
        Assert.Equal("installed", Change(workspace, "install", new() { ["id"] = Reminder }));
        var localId = fork ? Fork : Macro;
        if (fork)
            Assert.Equal("saved", Change(workspace, "fork", new() { ["id"] = Macro, ["local_id"] = Fork, ["revision"] = 1 }));
        Assert.Empty(Row(workspace, localId)["content"]!["links"]!.AsArray());

        macro["links"] = new JsonArray(Other, Reminder);
        Cache(repository, 2, Published(reminder, 1), Published(macro, 2), other);
        fixture.Reopen();
        workspace = Open(fixture);
        Assert.Equal("updated", Apply(workspace, localId, 1, 2, "keep"));
        Assert.True(JsonNode.DeepEquals(macro["links"], Row(workspace, localId)["content"]!["links"]));
        Assert.False(Row(workspace, localId).ContainsKey("source_links"));

        // An explicit user removal survives multiple successive source revisions.
        var edited = Row(workspace, localId)["content"]!.DeepClone().AsObject();
        edited["links"] = new JsonArray(Reminder);
        Assert.Equal("saved", Change(workspace, "save", Save(edited, 2)));
        repository = new CommunityRepository(fixture.Database);
        for (var revision = 3; revision <= 4; revision++)
        {
            macro["title"] = "Revision " + revision;
            Cache(repository, revision, Published(reminder, 1), Published(macro, revision), other);
            workspace = Open(fixture);
            Assert.Equal("updated", Apply(workspace, localId, revision, revision, "keep"));
            Assert.True(JsonNode.DeepEquals(edited["links"], Row(workspace, localId)["content"]!["links"]));
        }
    }

    [Theory]
    [InlineData("keep", false)]
    [InlineData("replace", false)]
    [InlineData("keep", true)]
    [InlineData("replace", true)]
    public void FileAndUpgradedRowsWithNoLinkBaselineRespectTheChosenMode(string mode, bool fromSchema19)
    {
        using var fixture = new TestDatabase();
        var workspace = Open(fixture);
        var before = Draft(Macro, "macro", "/p First");
        before["links"] = new JsonArray(Other);
        var local = before.DeepClone().AsObject();
        local["links"] = new JsonArray();
        var file = new JsonObject { ["schema_version"] = 1, ["items"] = new JsonArray(new JsonObject
        {
            ["content"] = local, ["upstream"] = Published(before, 1), ["position"] = 0, ["favorite"] = false,
        }) };
        Assert.Equal("installed", Change(workspace, "import_file", new() { ["file"] = file.ToJsonString() }));
        if (fromSchema19)
        {
            using (var downgrade = fixture.Database.CreateCommand())
            {
                downgrade.CommandText = """
                    ALTER TABLE community_local DROP COLUMN source_links_json;
                    DELETE FROM schema_migrations WHERE version = 20;
                    """;
                downgrade.ExecuteNonQuery();
            }
            fixture.Reopen();
            Assert.Equal(20, fixture.Database.SchemaVersion);
        }
        before["links"] = new JsonArray(Other, Reminder);
        Cache(new CommunityRepository(fixture.Database), 2, Published(before, 2),
            Published(Draft(Other, "reminder", "Other"), 1), Published(Draft(Reminder, "reminder", "Note"), 1));
        workspace = Open(fixture);
        Assert.Equal("updated", Apply(workspace, Macro, 1, 2, mode));
        Assert.True(JsonNode.DeepEquals(mode == "keep" ? local["links"] : before["links"],
            Row(workspace, Macro)["content"]!["links"]));
    }

    [Theory]
    [InlineData(false, "installed")]
    [InlineData(true, "local_conflict")]
    public void AuthoredDependencyIsReusedOnlyWhenAllDraftFieldsMatch(bool changed, string expected)
    {
        using var fixture = new TestDatabase();
        var repository = new CommunityRepository(fixture.Database);
        var note = Draft(Reminder, "reminder", "See macro");
        note["links"] = new JsonArray(Macro);
        var macro = Draft(Macro, "macro", "/p First");
        Cache(repository, 1, Published(note, 1), Published(macro, 1));
        var workspace = Open(fixture);
        if (changed) macro["title"] = "My title";
        Assert.Equal("saved", Change(workspace, "save", Save(macro, 0, favorite: true)));
        var original = Row(workspace, Macro);
        Assert.Equal(expected, Change(workspace, "install", new() { ["id"] = Reminder }));
        Assert.True(JsonNode.DeepEquals(original, Row(workspace, Macro)));
        Assert.Equal(changed ? 1 : 2, workspace.List(0)["items"]!.AsArray().Count);
    }

    private static JsonObject Row(CommunityWorkspace workspace, string id) =>
        workspace.List(0)["items"]!.AsArray().Single(row => row!["content"]!["id"]!.GetValue<string>() == id)!.AsObject();

    private static string Apply(CommunityWorkspace workspace, string localId, int revision, int sourceRevision, string mode) =>
        Change(workspace, "apply", new() { ["id"] = Macro, ["local_id"] = localId,
            ["revision"] = revision, ["source_revision"] = sourceRevision, ["mode"] = mode });

    private static string Change(CommunityWorkspace workspace, string action, JsonObject payload) =>
        workspace.Change(action, Guid.NewGuid().ToString(), payload)["status"]!.GetValue<string>();
}
