using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Reference;

/// <summary>One locally maintained display mapping for a content roulette.</summary>
/// <param name="RouletteId">
/// The <c>ContentRoulette</c> id, as read from the wire (S2C 0x0323, byte offset 16;
/// see protocol-profiles/cn/cn.2026.08.05.json's provenance).
/// </param>
/// <param name="LocalizedName">Localized display name.</param>
/// <param name="Region">Region whose mapping supplied this row.</param>
public sealed record RouletteInfo(int RouletteId, string LocalizedName, Region Region);

/// <summary>One reference document, as loaded from a file or an embedded resource.</summary>
/// <param name="Region">Region the document is for.</param>
/// <param name="Rows">Enabled rows, keyed by roulette id.</param>
public sealed record RouletteDocument(Region Region, IReadOnlyDictionary<int, RouletteInfo> Rows);

/// <summary>
/// Loads embedded content-roulette display mappings. These mappings are presentation-only:
/// the roulette a run belongs to is always identified from the wire (S2C 0x0323 byte 16),
/// never from this table. This catalog only turns that already-identified id into a readable
/// name; it must never be consulted to decide which roulette a run is, and it never raises a
/// run's detection confidence.
///
/// Unlike <see cref="DutyCatalog"/> there is no per-region <c>data_version</c> fallback chain:
/// the roulette id table is far more stable than the duty table, and only one file per region
/// is shipped today (data/roulettes/README.md). A lookup asks for a region directly and does
/// not fall back to another region -- the same rule <see cref="DutyCatalog"/> follows for
/// duties, and for the same reason (review finding L-10): a mapping from a different service
/// that happens to share a roulette id is not evidence about this one. The one exception is
/// <see cref="Region.Unknown"/>, which is the absence of a region rather than one of them.
/// </summary>
public sealed class RouletteCatalog
{
    /// <summary>Display name for a null or unmapped roulette id.</summary>
    public const string UnknownRouletteName = "未知随机任务";

    private const string ResourcePrefix = "MentorRecorder.Collector.Data.Roulettes.";
    private readonly IReadOnlyDictionary<(Region Region, int RouletteId), RouletteInfo> _byKey;

    private RouletteCatalog(IReadOnlyDictionary<(Region, int), RouletteInfo> byKey)
    {
        _byKey = byKey;
    }

    /// <summary>An empty mapping that always falls back to unknown.</summary>
    public static RouletteCatalog Empty { get; } = new(new Dictionary<(Region, int), RouletteInfo>());

    /// <summary>Every embedded document, one per region.</summary>
    public static IReadOnlyList<RouletteDocument> EmbeddedDocuments { get; } = LoadEmbeddedDocuments();

    /// <summary>The catalog embedded in this assembly, loaded once.</summary>
    public static RouletteCatalog Default { get; } = Compose(EmbeddedDocuments);

    /// <summary>
    /// The shipped catalog with the names this machine's player corrected laid over it.
    ///
    /// A correction is the player naming the roulette they themselves queued, so it outranks a
    /// community export of the game's text. Read fresh rather than cached: the player can make
    /// one at any time, and this is a ten-row dictionary.
    /// </summary>
    /// <param name="path">Override file; the managed one when null.</param>
    public static RouletteCatalog WithLocalNames(string? path = null) =>
        Default.Overriding(RouletteNameOverrides.Load(path ?? RouletteNameOverrides.DefaultPath));

    /// <summary>
    /// A copy of this catalog with some names replaced. An id nobody shipped is added rather
    /// than dropped: the player has seen it on their own client, which is better evidence than
    /// this project's absence of it.
    /// </summary>
    /// <param name="names">Replacement names, by region and roulette id.</param>
    public RouletteCatalog Overriding(IReadOnlyDictionary<(Region Region, int RouletteId), string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            return this;
        }

        var combined = new Dictionary<(Region, int), RouletteInfo>(
            _byKey.ToDictionary(entry => entry.Key, entry => entry.Value));
        foreach (var ((region, id), name) in names)
        {
            combined[(region, id)] = new RouletteInfo(id, name, region);
        }

        return new RouletteCatalog(combined);
    }

    /// <summary>Number of enabled mappings across every region.</summary>
    public int Count => _byKey.Count;

    /// <summary>
    /// Finds an enabled mapping for one region, or null.
    ///
    /// The lookup does not fall back to another region's file, except for
    /// <see cref="Region.Unknown"/> -- see the class doc comment.
    /// </summary>
    /// <param name="rouletteId">Roulette id to look up.</param>
    /// <param name="region">Region whose mapping must supply the answer.</param>
    public RouletteInfo? Find(int? rouletteId, Region region)
    {
        if (rouletteId is not { } id)
        {
            return null;
        }

        if (_byKey.TryGetValue((region, id), out var exact))
        {
            return exact;
        }

        return region == Region.Unknown
            ? _byKey.Values.FirstOrDefault(row => row.RouletteId == id)
            : null;
    }

    /// <summary>True when a roulette id has an enabled mapping for the given region.</summary>
    /// <param name="rouletteId">Roulette id to look up.</param>
    /// <param name="region">Region whose mapping must supply the answer.</param>
    public bool IsKnown(int rouletteId, Region region) => Find(rouletteId, region) is not null;

    /// <summary>Every known roulette id for one region, ascending.</summary>
    /// <param name="region">Region to list.</param>
    public IReadOnlyList<int> KnownIds(Region region) => _byKey.Keys
        .Where(key => key.Region == region)
        .Select(key => key.RouletteId)
        .OrderBy(id => id)
        .ToArray();

    /// <summary>Returns the mapped name, or null when the id cannot be resolved.</summary>
    /// <param name="rouletteId">Roulette id to look up.</param>
    /// <param name="region">Region whose mapping is preferred.</param>
    public string? NameOf(int? rouletteId, Region region) => Find(rouletteId, region)?.LocalizedName;

    /// <summary>Returns the mapped name, or <see cref="UnknownRouletteName"/>.</summary>
    /// <param name="rouletteId">Roulette id to look up.</param>
    /// <param name="region">Region whose mapping is preferred.</param>
    public string DisplayName(int? rouletteId, Region region) =>
        NameOf(rouletteId, region) ?? UnknownRouletteName;

    /// <summary>Parses one reference document.</summary>
    /// <param name="json">Document text.</param>
    public static RouletteDocument ParseDocument(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var file = JsonSerializer.Deserialize<RouletteFile>(json)
            ?? throw new InvalidDataException("roulette catalog is empty");
        if (file.SchemaVersion != 1 || !EnumWire<Region>.TryParse(file.Region, out var region))
        {
            throw new InvalidDataException("roulette catalog schema_version or region is unsupported");
        }

        var rows = new Dictionary<int, RouletteInfo>();
        foreach (var row in file.Roulettes ?? Array.Empty<RouletteRow>())
        {
            if (row.RouletteId < 0 || string.IsNullOrWhiteSpace(row.LocalizedName))
            {
                throw new InvalidDataException("roulette rows require a non-negative roulette_id and a name");
            }

            if (rows.ContainsKey(row.RouletteId))
            {
                throw new InvalidDataException($"duplicate roulette_id {row.RouletteId} for {file.Region}");
            }

            if (row.Enabled)
            {
                rows.Add(row.RouletteId, new RouletteInfo(row.RouletteId, row.LocalizedName, region));
            }
        }

        return new RouletteDocument(region, rows);
    }

    /// <summary>Parses one reference document into a single-document catalogue.</summary>
    /// <param name="json">Document text.</param>
    public static RouletteCatalog Parse(string json) => Compose(new[] { ParseDocument(json) });

    /// <summary>
    /// Merges documents in preference order: the first document that knows a roulette id wins,
    /// and every later one only fills gaps.
    /// </summary>
    /// <param name="documents">Documents, most preferred first.</param>
    public static RouletteCatalog Compose(IReadOnlyList<RouletteDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var combined = new Dictionary<(Region, int), RouletteInfo>();
        foreach (var document in documents)
        {
            foreach (var row in document.Rows)
            {
                combined.TryAdd((document.Region, row.Key), row.Value);
            }
        }

        return new RouletteCatalog(combined);
    }

    private static IReadOnlyList<RouletteDocument> LoadEmbeddedDocuments()
    {
        var assembly = typeof(RouletteCatalog).GetTypeInfo().Assembly;
        var documents = new List<RouletteDocument>();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                                    name.EndsWith(".json", StringComparison.Ordinal))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidDataException($"cannot open embedded roulette catalog {resource}");
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            documents.Add(ParseDocument(reader.ReadToEnd()));
        }

        return documents;
    }

    private sealed record RouletteFile
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("region")]
        public string? Region { get; init; }

        [JsonPropertyName("roulettes")]
        public RouletteRow[]? Roulettes { get; init; }
    }

    private sealed record RouletteRow
    {
        [JsonPropertyName("roulette_id")]
        public int RouletteId { get; init; }

        [JsonPropertyName("localized_name")]
        public string? LocalizedName { get; init; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; } = true;
    }
}
