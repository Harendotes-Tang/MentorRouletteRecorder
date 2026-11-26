using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Community;

internal sealed class CommunityWorkspace(CommunityRepository repository, CommunityLibrary library)
{
    internal JsonObject List(int offset)
    {
        var items = repository.List(offset);
        return new JsonObject
        {
            ["status"] = "ready",
            ["items"] = items,
            ["next"] = items.Count == CommunityContent.PageSize ? offset + items.Count : null,
        };
    }

    internal JsonObject Change(string action, string requestId, JsonObject payload)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToJsonString())));
        var remote = action is "install" or "apply" ? GetRemoteSet(Id(payload)) : [];
        return repository.Change(requestId, fingerprint, tx => action switch
        {
            "save" => Save(payload, tx),
            "delete" => Delete(payload, tx),
            "fork" => Fork(payload, tx),
            "install" => Install(remote, tx),
            "apply" => Apply(payload, remote, tx),
            "import_file" => ImportFile(payload, tx),
            _ => throw new FormatException("invalid_action"),
        });
    }

    private JsonObject Save(JsonObject payload, SqliteTransaction tx)
    {
        var draft = Object(payload, "content");
        // The stored row is read first: a field the edit leaves as it was is not judged by a
        // character rule newer than the entry (CommunityContent.ValidateLocalDraft).
        var old = draft["id"] is JsonValue key && key.TryGetValue<string>(out var storedId) &&
            CommunityContent.IsId(storedId) ? repository.Get(storedId, tx) : null;
        CommunityContent.ValidateLocalDraft(draft, old?["content"] as JsonObject);
        var id = Id(draft);
        var revision = CommunityContent.Integer(payload, "revision");
        if (Revision(old) != revision)
            return Result("local_conflict");
        if (old is null && !repository.HasRoom(1, tx))
            return Result("local_limit");
        if (old is not null && !JsonNode.DeepEquals(old["content"]!["type"], draft["type"]))
            return Result("invalid_type");
        foreach (var link in draft["links"]!.AsArray())
        {
            var target = repository.Get(link!.GetValue<string>(), tx);
            if (target is null || JsonNode.DeepEquals(target["content"]!["type"], draft["type"]))
                return Result("invalid_links");
        }
        var position = CommunityContent.Integer(payload, "position");
        if (position is < -9999 or > 9999)
            return Result("invalid_position");
        repository.Save(Row(draft, revision + 1, position,
            CommunityContent.Boolean(payload, "favorite"), old?["upstream"]), tx,
            updateOrder: !JsonNode.DeepEquals(old?["content"], draft));
        return Result("saved");
    }

    private JsonObject Fork(JsonObject payload, SqliteTransaction tx)
    {
        var old = repository.Get(Id(payload), tx);
        var newId = Id(payload, "local_id");
        if (old is null || old["upstream"] is null || Revision(old) != CommunityContent.Integer(payload, "revision") ||
            repository.Get(newId, tx) is not null)
            return Result("local_conflict");
        if (!repository.HasRoom(1, tx))
            return Result("local_limit");
        var draft = old["content"]!.DeepClone().AsObject();
        draft["id"] = newId;
        repository.Save(Row(draft, 1, CommunityContent.Integer(old, "position"),
            CommunityContent.Boolean(old, "favorite"), old["upstream"]), tx,
            sourceLinks: repository.SourceLinks(Id(payload), tx));
        return Result("saved");
    }

    private JsonObject Delete(JsonObject payload, SqliteTransaction tx)
    {
        var id = Id(payload);
        var row = repository.Get(id, tx);
        if (row is null || Revision(row) != CommunityContent.Integer(payload, "revision"))
            return Result("local_conflict");
        foreach (var node in repository.List(transaction: tx))
        {
            var linked = node!["content"]!.AsObject();
            var links = linked["links"]!.AsArray();
            var index = links.ToList().FindIndex(link => link!.GetValue<string>() == id);
            if (index < 0)
                continue;
            links.RemoveAt(index);
            node.AsObject()["revision"] = Revision(node.AsObject()) + 1;
            repository.Save(node.AsObject(), tx, updateOrder: false);
        }
        repository.Delete(id, tx);
        return Result("deleted");
    }

    private JsonObject Install(IReadOnlyList<JsonObject> items, SqliteTransaction tx)
    {
        if (items.Count == 0 || Inactive(items[0]))
            return Result("source_unavailable");
        if (repository.Get(Id(items[0]), tx) is not null)
            return Result("already_installed");
        return AddMissing(items, tx);
    }

    private JsonObject AddMissing(IReadOnlyList<JsonObject> items, SqliteTransaction tx)
    {
        var missing = items.Where(item => repository.Get(Id(item), tx) is null).ToArray();
        if (!repository.HasRoom(missing.Length, tx))
            return Result("local_limit");
        foreach (var item in items)
        {
            var existing = repository.Get(Id(item), tx);
            if (existing is null)
                continue;
            // A personal draft published from here has no upstream. Reuse it as a dependency
            // only when the published draft matches exactly; its local row stays untouched.
            var compatible = existing["upstream"] is JsonObject upstream
                ? JsonNode.DeepEquals(upstream["author"]!["id"], item["author"]!["id"])
                : JsonNode.DeepEquals(existing["content"], CommunityContent.Draft(item));
            if (!compatible)
                return Result("local_conflict");
        }
        var installing = items.Select(item => Id(item)).ToHashSet(StringComparer.Ordinal);
        // Save in reverse so the selected entry appears above its newly installed links.
        foreach (var item in missing.Reverse())
        {
            var draft = CommunityContent.Draft(item);
            var links = InstalledLinks(item, installing, tx);
            draft["links"] = links;
            repository.Save(Row(draft, 1, 0, false, item), tx, sourceLinks: links);
        }
        return Result("installed");
    }

    private JsonObject Apply(JsonObject payload, IReadOnlyList<JsonObject> items, SqliteTransaction tx)
    {
        if (items.Count == 0 || Inactive(items[0]))
            return Result("source_unavailable");
        var remote = items[0];
        var sourceId = Id(remote);
        var localId = payload["local_id"] is null ? sourceId : Id(payload, "local_id");
        var old = repository.Get(localId, tx);
        if (old?["upstream"] is not JsonObject basis || Id(basis) != sourceId ||
            Revision(old) != CommunityContent.Integer(payload, "revision") ||
            !JsonNode.DeepEquals(basis["author"]!["id"], remote["author"]!["id"]))
            return Result("local_conflict");
        var remoteRevision = CommunityContent.Integer(remote, "revision");
        if (remoteRevision != CommunityContent.Integer(payload, "source_revision") ||
            remoteRevision <= CommunityContent.Integer(basis, "revision"))
            return Result("source_changed");
        var draft = CommunityContent.Draft(remote);
        draft["id"] = localId;
        var local = old["content"]!.AsObject();
        var mode = CommunityContent.String(payload, "mode", 8);
        if (mode is not ("keep" or "replace"))
            throw new FormatException("invalid_mode");
        if (mode == "keep")
        {
            var adoptedLinks = repository.SourceLinks(localId, tx) ?? basis["links"];
            foreach (var key in CommunityContent.Fields)
            {
                if (!JsonNode.DeepEquals(key == "links" ? adoptedLinks : basis[key], local[key]))
                    draft[key] = local[key]?.DeepClone();
            }
        }
        var installed = AddMissing(items.Skip(1).ToArray(), tx);
        if (installed["status"]!.GetValue<string>() != "installed")
            return installed;
        var sourceLinks = InstalledLinks(remote, null, tx);
        draft["links"] = InstalledLinks(draft, null, tx);
        repository.Save(Row(draft, Revision(old) + 1, CommunityContent.Integer(old, "position"),
            CommunityContent.Boolean(old, "favorite"), remote), tx, sourceLinks: sourceLinks);
        return Result("updated");
    }

    private JsonObject ImportFile(JsonObject payload, SqliteTransaction tx)
    {
        // Each row is checked below; a published copy inside it keeps the rule it was published under.
        var text = CommunityContent.String(payload, "file", 700_000, multiline: true, legacy: true);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 700_000)
            throw new FormatException("invalid_file");
        var document = CommunityContent.ParseObject(bytes);
        if (document.Count != 2 || CommunityContent.Integer(document, "schema_version") != 1 ||
            document["items"] is not JsonArray rows || rows.Count is < 1 or > CommunityContent.MaxBatch)
            throw new FormatException("invalid_file");
        var imported = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in rows)
        {
            if (node is not JsonObject row || row.Count != 4)
                throw new FormatException("invalid_file");
            var draft = Object(row, "content");
            CommunityContent.ValidateLocalDraft(draft);
            if (row["upstream"] is JsonObject upstream)
            {
                CommunityContent.ValidateAsPublished(upstream);
                if (!JsonNode.DeepEquals(upstream["type"], draft["type"]))
                    throw new FormatException("invalid_type");
            }
            else if (!row.ContainsKey("upstream") || row["upstream"] is not null)
                throw new FormatException("invalid_file");
            var position = CommunityContent.Integer(row, "position");
            if (position is < -9999 or > 9999)
                throw new FormatException("invalid_position");
            if (!imported.TryAdd(Id(draft), Row(draft, 1, position,
                CommunityContent.Boolean(row, "favorite"), row["upstream"])))
                throw new FormatException("duplicate_id");
        }
        foreach (var row in imported.Values)
        {
            foreach (var link in row["content"]!["links"]!.AsArray())
            {
                var targetId = link!.GetValue<string>();
                var target = imported.GetValueOrDefault(targetId) ?? repository.Get(targetId, tx);
                if (target is null || JsonNode.DeepEquals(target["content"]!["type"], row["content"]!["type"]))
                    return Result("invalid_links");
            }
        }
        if (imported.Keys.Any(id => repository.Get(id, tx) is not null))
            return Result("local_conflict");
        if (!repository.HasRoom(imported.Count, tx))
            return Result("local_limit");
        foreach (var row in imported.Values.Reverse())
            repository.Save(row, tx);
        return Result("installed");
    }

    // The selected entry first, then the entries it links to directly that are still available.
    // A withdrawn link target is left out instead of blocking the install or the update, and the
    // links of a linked entry are not followed further: only what the user chose comes along.
    private IReadOnlyList<JsonObject> GetRemoteSet(string id)
    {
        var publication = library.Current();
        var entry = publication?.Entries.GetValueOrDefault(id);
        if (entry is null || Inactive(entry))
            return [];
        var items = new List<JsonObject> { entry };
        foreach (var link in entry["links"]!.AsArray())
        {
            var target = publication!.Entries.GetValueOrDefault(link!.GetValue<string>());
            if (target is not null && !Inactive(target))
                items.Add(target);
        }
        return items;
    }

    // An installed draft links only to entries this machine has (or is installing in the same
    // change), so that saving it later never fails on a link that was left out.
    private JsonArray InstalledLinks(JsonObject draft, IReadOnlySet<string>? installing, SqliteTransaction tx)
    {
        var links = draft["links"]!.AsArray().Select(link => link!.GetValue<string>())
            .Where(link => installing?.Contains(link) == true || repository.Get(link, tx) is not null)
            .Select(link => (JsonNode?)JsonValue.Create(link)).ToArray();
        return new JsonArray(links);
    }

    private static bool Inactive(JsonObject item) => item["status"]!.GetValue<string>() == "withdrawn";

    private static int Revision(JsonObject? row) => row is null ? 0 : CommunityContent.Integer(row, "revision");

    internal static string Id(JsonObject data, string key = "id")
    {
        var id = CommunityContent.String(data, key, 36);
        return CommunityContent.IsId(id) ? id : throw new FormatException("invalid_id");
    }

    internal static JsonObject Object(JsonObject data, string key) =>
        data[key] as JsonObject ?? throw new FormatException("invalid_" + key);

    private static JsonObject Row(JsonObject draft, int revision, int position, bool favorite, JsonNode? upstream) => new()
    {
        ["revision"] = revision,
        ["position"] = position,
        ["favorite"] = favorite,
        ["content"] = draft.DeepClone(),
        ["upstream"] = upstream?.DeepClone(),
    };

    internal static JsonObject Result(string status) => new() { ["status"] = status };
}
