using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Community;

namespace MentorRecorder.Collector.Storage.Repositories;

internal sealed class CommunityRepository(SqliteDatabase database)
{
    private const int MaxItems = 400;

    internal JsonArray List(int offset = 0, SqliteTransaction? transaction = null) => database.Read(_ =>
    {
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision, position, favorite, content_json, upstream_json
            FROM community_local ORDER BY edit_sequence DESC, position, id LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$limit", transaction is null ? CommunityContent.PageSize : MaxItems);
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var items = new JsonArray();
        while (reader.Read())
            items.Add(ReadItem(reader));
        return items;
    });

    internal JsonObject Change(string requestId, string fingerprint, Func<SqliteTransaction, JsonObject> change)
    {
        return database.RunInTransaction(transaction =>
        {
            using var command = database.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT fingerprint, response_json FROM community_receipts WHERE request_id = $id";
            command.Parameters.AddWithValue("$id", requestId);
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                    return reader.GetString(0) == fingerprint
                        ? JsonNode.Parse(reader.GetString(1))!.AsObject()
                        : new JsonObject { ["status"] = "request_conflict" };
            }

            var response = change(transaction);
            command.CommandText = """
                INSERT INTO community_receipts(request_id, fingerprint, response_json)
                VALUES ($id, $fingerprint, $response)
                """;
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.Parameters.AddWithValue("$response", response.ToJsonString());
            command.ExecuteNonQuery();
            return response;
        });
    }

    internal JsonObject? Get(string id, SqliteTransaction transaction)
    {
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision, position, favorite, content_json, upstream_json
            FROM community_local WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }

    internal bool HasRoom(int additional, SqliteTransaction transaction)
    {
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM community_local";
        return (long)command.ExecuteScalar()! + additional <= MaxItems;
    }

    internal void Save(JsonObject item, SqliteTransaction transaction, bool updateOrder = true)
    {
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO community_local(id, revision, position, favorite, content_json, upstream_json, edit_sequence)
            VALUES ($id, $revision, $position, $favorite, $content, $upstream,
                CASE WHEN $updateOrder THEN (SELECT COALESCE(MAX(edit_sequence), 0) + 1 FROM community_local) END)
            ON CONFLICT(id) DO UPDATE SET revision=excluded.revision, position=excluded.position,
                favorite=excluded.favorite, content_json=excluded.content_json, upstream_json=excluded.upstream_json,
                edit_sequence=COALESCE(excluded.edit_sequence, community_local.edit_sequence)
            """;
        command.Parameters.AddWithValue("$id", item["content"]!["id"]!.GetValue<string>());
        command.Parameters.AddWithValue("$revision", item["revision"]!.GetValue<int>());
        command.Parameters.AddWithValue("$position", item["position"]!.GetValue<int>());
        command.Parameters.AddWithValue("$favorite", item["favorite"]!.GetValue<bool>());
        command.Parameters.AddWithValue("$content", item["content"]!.ToJsonString());
        command.Parameters.AddWithValue("$upstream", (object?)item["upstream"]?.ToJsonString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$updateOrder", updateOrder);
        command.ExecuteNonQuery();
    }

    internal void Delete(string id, SqliteTransaction transaction)
    {
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM community_local WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    internal (byte[] Index, byte[] Snapshot)? ReadCache() => database.Read<(byte[], byte[])?>(_ =>
    {
        using var command = database.CreateCommand();
        command.CommandText = "SELECT index_json, snapshot_json FROM community_cache WHERE singleton = 1";
        using var reader = command.ExecuteReader();
        return reader.Read() ? ((byte[])reader[0], (byte[])reader[1]) : null;
    });

    internal void SaveCache(byte[] index, byte[] snapshot) => database.RunInTransaction(transaction =>
    {
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO community_cache(singleton, index_json, snapshot_json) VALUES (1, $index, $snapshot)
            ON CONFLICT(singleton) DO UPDATE SET index_json=excluded.index_json, snapshot_json=excluded.snapshot_json
            """;
        command.Parameters.AddWithValue("$index", index);
        command.Parameters.AddWithValue("$snapshot", snapshot);
        command.ExecuteNonQuery();
    });

    private static JsonObject ReadItem(SqliteDataReader reader) => new()
    {
        ["revision"] = reader.GetInt32(0),
        ["position"] = reader.GetInt32(1),
        ["favorite"] = reader.GetBoolean(2),
        ["content"] = JsonNode.Parse(reader.GetString(3)),
        ["upstream"] = reader.IsDBNull(4) ? null : JsonNode.Parse(reader.GetString(4)),
    };
}
