using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Community;

internal sealed class CommunityLibrary(CommunityRepository repository, SettingsRepository settings,
    Func<string, string, int, Func<byte[], bool>, CancellationToken, Task<byte[]?>>? fetch = null,
    TimeProvider? timeProvider = null, Func<bool>? killSwitch = null)
{
    private const string Enabled = "community.online_enabled";
    private readonly Func<string, string, int, Func<byte[], bool>, CancellationToken, Task<byte[]?>> _fetch =
        fetch ?? CommunityHttp.GetPublicationAsync;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    // MR_DISABLE_DUTY_COMMUNITY by default; tests that fake the fetch pass their own.
    private readonly Func<bool> _killSwitch = killSwitch ??
        (() => Environment.GetEnvironmentVariable("MR_DISABLE_DUTY_COMMUNITY") == "1");
    private readonly SemaphoreSlim _refreshGate = new(1);
    private readonly object _cacheGate = new();
    private Publication? _cache;
    private bool _loaded;
    private DateTimeOffset _nextRefresh;
    private string _lastStatus = "not_refreshed";
    // Replaced as a whole (one reference write), so a report read on another thread never sees
    // the time of one request with the outcome of another.
    private RequestRecord? _lastRequest;

    internal bool Online => !_killSwitch() && settings.GetSetting(Enabled) == "true";

    internal void SetOnline(bool enabled) => settings.SetSetting(Enabled, enabled ? "true" : "false");

    internal JsonObject Browse(int offset = 0) => Response(Online ? _lastStatus : "offline", offset);

    internal async Task<JsonObject> RefreshAsync(CancellationToken cancellationToken)
    {
        if (!Online)
            return Response("offline");
        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Response("refreshing");
        DateTimeOffset? sentAt = null;
        string? outcome = null;
        JsonObject Finish(string status)
        {
            outcome = _lastStatus = status;
            return Response(status);
        }
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (now < _nextRefresh)
                return Response(_lastStatus);
            _nextRefresh = now.AddSeconds(30);
            // An unusable reply from one source (a mirror that has not synchronised yet answering
            // with a placeholder, say) does not decide while the other can still deliver the file.
            sentAt = _timeProvider.GetUtcNow();
            var index = await _fetch("main", "index.json", 4096,
                bytes => Usable(() => ReadPointer(bytes)), cancellationToken).ConfigureAwait(false);
            if (index is null)
                return Finish("not_deployed");
            var pointer = ReadPointer(index);
            if (pointer.Commit is null)
                return Finish(ReadCache() is null ? "empty" : "invalid_index");
            var previous = ReadCache();
            if (previous?.Commit == pointer.Commit && previous.Hash == pointer.Hash)
            {
                if (previous.Generation != pointer.Generation)
                    return Finish("invalid_index");
                _nextRefresh = now.AddMinutes(2);
                return Finish("ready");
            }

            sentAt = _timeProvider.GetUtcNow();
            var bundle = await _fetch(pointer.Commit, "snapshot.json", CommunityContent.MaxSnapshotBytes,
                bytes => Usable(() => ReadPublication(pointer, bytes)), cancellationToken).ConfigureAwait(false);
            if (bundle is null)
                return Finish("temporarily_unavailable");
            var result = ReadPublication(pointer, bundle);
            // A lagging mirror must never roll back an already installed source.
            if (previous is not null && result.Generation <= previous.Generation)
                return Finish("stale_mirror");
            lock (_cacheGate)
            {
                repository.SaveCache(index, bundle);
                _cache = result;
                _loaded = true;
            }
            _nextRefresh = now.AddMinutes(2);
            return Finish("ready");
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            return Finish("invalid_index");
        }
        catch (Exception error) when (error is HttpRequestException or IOException ||
                                      error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Finish("temporarily_unavailable");
        }
        finally
        {
            if (sentAt is { } at)
                RecordRequest(new(at, outcome ?? (cancellationToken.IsCancellationRequested ? "cancelled" : "failed")));
            _refreshGate.Release();
        }
    }

    internal Publication? Current() => ReadCache();

    /// <summary>
    /// Records a community request this process sent (the library fetch here; the GitHub
    /// sign-in, submission and issue-status requests through CommunityHandlers). The report
    /// states the most recently sent one.
    /// </summary>
    internal void RecordRequest(RequestRecord? record)
    {
        if (record is null)
            return;
        RequestRecord? seen;
        do
        {
            seen = Volatile.Read(ref _lastRequest);
            if (seen is not null && seen.At > record.At)
                return;
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref _lastRequest, record, seen), seen));
    }

    internal Capture.DutyCommunityDiagnostics Diagnostics()
    {
        var last = Volatile.Read(ref _lastRequest);
        return new(settings.GetSetting(Enabled) == "true", _killSwitch(), last?.At, last?.Outcome);
    }

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
        // An entry published before the publisher refused the characters CommunityContent.Refused
        // names stays valid as published, but this client neither shows nor installs it: it is
        // left out and links to it are dropped, as for a withdrawn entry. Any other failure,
        // a wrong hash or a broken link included, still rejects the whole snapshot.
        var skipped = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in entries)
        {
            if (node is not JsonObject item)
                throw new FormatException("invalid_content");
            CommunityContent.ValidateAsPublished(item);
            var id = item["id"]!.GetValue<string>();
            var kept = CommunityContent.NewlyRefused(item) is null ? byId : skipped;
            if (byId.ContainsKey(id) || skipped.ContainsKey(id) || !kept.TryAdd(id, item))
                throw new FormatException("duplicate_id");
        }

        JsonObject? Target(JsonNode? link)
        {
            var linkId = link!.GetValue<string>();
            return byId.GetValueOrDefault(linkId) ?? skipped.GetValueOrDefault(linkId);
        }
        foreach (var item in byId.Values.Concat(skipped.Values))
        {
            if (item["links"]!.AsArray().Any(link => Target(link) is not { } target
                  || JsonNode.DeepEquals(item["type"], target["type"])))
                throw new FormatException("invalid_links");
        }
        foreach (var item in byId.Values)
        {
            var links = item["links"]!.AsArray();
            if (links.Any(link => skipped.ContainsKey(link!.GetValue<string>())))
                item["links"] = new JsonArray(links.Where(link => !skipped.ContainsKey(link!.GetValue<string>()))
                    .Select(link => link!.DeepClone()).ToArray());
        }
        return new(pointer.Commit, pointer.Hash, pointer.Generation, byId);
    }

    private static bool Usable(Action read)
    {
        try
        {
            read();
            return true;
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            return false;
        }
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>When a community request was sent and how it ended, as a status token.</summary>
    internal sealed record RequestRecord(DateTimeOffset At, string Outcome);
    internal sealed record PublicationPointer(string? Commit, string Hash, int Generation);
    internal sealed record Publication(string Commit, string Hash, int Generation, Dictionary<string, JsonObject> Entries);
}
