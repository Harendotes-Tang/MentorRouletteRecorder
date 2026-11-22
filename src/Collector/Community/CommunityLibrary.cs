using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Community;

internal sealed class CommunityLibrary(CommunityRepository repository, SettingsRepository settings,
    Func<string, string, int, CancellationToken, Task<byte[]?>>? fetch = null,
    TimeProvider? timeProvider = null)
{
    private const string Enabled = "community.online_enabled";
    private readonly Func<string, string, int, CancellationToken, Task<byte[]?>> _fetch =
        fetch ?? CommunityHttp.GetPublicationAsync;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _refreshGate = new(1);
    private readonly object _cacheGate = new();
    private Publication? _cache;
    private bool _loaded;
    private DateTimeOffset _nextRefresh;
    private string _lastStatus = "not_refreshed";

    internal bool Online => Environment.GetEnvironmentVariable("MR_DISABLE_DUTY_COMMUNITY") != "1" &&
        settings.GetSetting(Enabled) == "true";

    internal void SetOnline(bool enabled) => settings.SetSetting(Enabled, enabled ? "true" : "false");

    internal JsonObject Browse(int offset = 0) => Response(Online ? _lastStatus : "offline", offset);

    internal async Task<JsonObject> RefreshAsync(CancellationToken cancellationToken)
    {
        if (!Online)
            return Response("offline");
        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Response("refreshing");
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (now < _nextRefresh)
                return Response(_lastStatus);
            _nextRefresh = now.AddSeconds(30);
            var index = await _fetch("main", "index.json", 4096, cancellationToken)
                .ConfigureAwait(false);
            if (index is null)
                return Response(_lastStatus = "not_deployed");
            var pointer = ReadPointer(index);
            if (pointer.Commit is null)
                return Response(_lastStatus = ReadCache() is null ? "empty" : "invalid_index");
            var previous = ReadCache();
            if (previous?.Commit == pointer.Commit && previous.Hash == pointer.Hash)
            {
                if (previous.Generation != pointer.Generation)
                    return Response(_lastStatus = "invalid_index");
                _nextRefresh = now.AddMinutes(2);
                return Response(_lastStatus = "ready");
            }

            var bundle = await _fetch(pointer.Commit, "snapshot.json",
                CommunityContent.MaxSnapshotBytes, cancellationToken).ConfigureAwait(false);
            if (bundle is null)
                return Response(_lastStatus = "temporarily_unavailable");
            var result = ReadPublication(pointer, bundle);
            // A lagging mirror must never roll back an already installed source.
            if (previous is not null && result.Generation <= previous.Generation)
                return Response(_lastStatus = "stale_mirror");
            lock (_cacheGate)
            {
                repository.SaveCache(index, bundle);
                _cache = result;
                _loaded = true;
            }
            _nextRefresh = now.AddMinutes(2);
            return Response(_lastStatus = "ready");
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            return Response(_lastStatus = "invalid_index");
        }
        catch (Exception error) when (error is HttpRequestException or IOException ||
                                      error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Response(_lastStatus = "temporarily_unavailable");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    internal Publication? Current() => ReadCache();

    private JsonObject Response(string status, int offset = 0)
    {
        var cache = ReadCache();
        var entries = new JsonArray(cache?.Entries.Values.Skip(offset).Take(CommunityContent.PageSize)
            .Select(entry => entry.DeepClone()).ToArray() ?? []);
        return new JsonObject
        {
            ["status"] = status,
            ["online"] = Online,
            ["cached"] = cache is not null,
            ["entries"] = entries,
            ["next"] = entries.Count == CommunityContent.PageSize ? offset + entries.Count : null,
            ["generation"] = cache?.Generation ?? 0,
        };
    }

    private Publication? ReadCache()
    {
        lock (_cacheGate)
        {
            if (_loaded)
                return _cache;
            _loaded = true;
            if (repository.ReadCache() is not { } bytes)
                return null;
            try
            {
                _cache = ReadPublication(ReadPointer(bytes.Index), bytes.Snapshot);
            }
            catch (Exception error) when (error is JsonException or FormatException)
            {
                // A damaged local copy is discarded in memory until an explicit successful refresh.
            }
            return _cache;
        }
    }

    internal static PublicationPointer ReadPointer(byte[] data)
    {
        if (data.Length > 4096)
            throw new FormatException("index_limit");
        var index = CommunityContent.ParseObject(data);
        if (index.Count != 4 || CommunityContent.Integer(index, "schema_version") != 1 ||
            !index.ContainsKey("snapshot_commit") || !index.ContainsKey("snapshot_sha256"))
            throw new FormatException("invalid_index");
        var generation = CommunityContent.Integer(index, "generation");
        if (generation == 0 && index["snapshot_commit"] is null && index["snapshot_sha256"] is null)
            return new(null, "", 0);
        var commit = CommunityContent.String(index, "snapshot_commit", 40);
        var hash = CommunityContent.String(index, "snapshot_sha256", 64);
        if (generation < 1 || !IsLowerHex(commit, 40) || !IsLowerHex(hash, 64))
            throw new FormatException("invalid_index");
        return new(commit, hash, generation);
    }

    internal static Publication ReadPublication(PublicationPointer pointer, byte[] bytes)
    {
        if (pointer.Commit is null || bytes.Length > CommunityContent.MaxSnapshotBytes ||
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(pointer.Hash, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("invalid_hash");
        var snapshot = CommunityContent.ParseObject(bytes);
        if (snapshot.Count != 3 || CommunityContent.Integer(snapshot, "schema_version") != 1 ||
            CommunityContent.Integer(snapshot, "generation") != pointer.Generation ||
            snapshot["entries"] is not JsonArray entries || entries.Count > CommunityContent.MaxEntries)
            throw new FormatException("invalid_snapshot");
        var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in entries)
        {
            if (node is not JsonObject item)
                throw new FormatException("invalid_content");
            CommunityContent.Validate(item, published: true);
            if (!byId.TryAdd(item["id"]!.GetValue<string>(), item))
                throw new FormatException("duplicate_id");
        }

        foreach (var item in byId.Values)
        {
            if (item["links"]!.AsArray().Any(link => !byId.TryGetValue(link!.GetValue<string>(), out var target)
                  || JsonNode.DeepEquals(item["type"], target["type"])))
                throw new FormatException("invalid_links");
        }
        return new(pointer.Commit, pointer.Hash, pointer.Generation, byId);
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal sealed record PublicationPointer(string? Commit, string Hash, int Generation);
    internal sealed record Publication(string Commit, string Hash, int Generation, Dictionary<string, JsonObject> Entries);
}
