using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Community;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

internal static class CommunityTestData
{
    internal static CommunityWorkspace Open(TestDatabase fixture)
    {
        var repository = new CommunityRepository(fixture.Database);
        return new(repository, new CommunityLibrary(repository, new SettingsRepository(fixture.Database, fixture.Clock)));
    }

    internal static JsonObject Draft(string id, string type, string body) => new()
    {
        ["schema_version"] = 1, ["id"] = id, ["type"] = type, ["content_id"] = 4, ["region"] = "CN",
        ["language"] = "zh-Hans", ["title"] = "Example", ["body"] = body, ["boss"] = "", ["phase"] = "",
        ["role"] = "", ["tags"] = new JsonArray(), ["links"] = new JsonArray(), ["source"] = "", ["game_version"] = "",
    };

    internal static JsonObject Save(JsonObject draft, int revision, bool favorite = false) => new()
    {
        ["action"] = "save", ["content"] = draft.DeepClone(), ["revision"] = revision,
        ["position"] = 0, ["favorite"] = favorite,
    };

    internal static JsonObject Published(JsonObject original, int revision)
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

    internal static void Cache(CommunityRepository repository, int generation, params JsonObject[] items)
    {
        var (index, bytes) = Publication(generation, items);
        repository.SaveCache(index, bytes);
    }

    internal static (byte[] Index, byte[] Bytes) Publication(int generation, params JsonObject[] items)
    {
        var bytes = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schema_version"] = 1, ["generation"] = generation, ["entries"] = new JsonArray(items.Select(item => item.DeepClone()).ToArray()),
        }.ToJsonString());
        var index = new JsonObject
        {
            ["schema_version"] = 1, ["generation"] = generation, ["snapshot_commit"] = new string('a', 40),
            ["snapshot_sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        };
        return (Encoding.UTF8.GetBytes(index.ToJsonString()), bytes);
    }
}
