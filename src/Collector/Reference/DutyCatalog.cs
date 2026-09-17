using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Reference;

/// <summary>One locally maintained display mapping for a duty.</summary>
/// <param name="ContentId">Stable content id used for aggregation.</param>
/// <param name="TerritoryId">Territory id, when supplied by the reference file.</param>
/// <param name="LocalizedName">Localized display name.</param>
/// <param name="DutyCategory">Localized category.</param>
/// <param name="Region">Region whose mapping supplied this row.</param>
/// <param name="IsSyntheticSample">True when this is test/sample data, never protocol evidence.</param>
public sealed record DutyInfo(
    int ContentId,
    int? TerritoryId,
    string LocalizedName,
    string? DutyCategory,
    Region Region,
    bool IsSyntheticSample);

/// <summary>One versioned reference document, as loaded from a file or an embedded resource.</summary>
/// <param name="Region">Region the document is for.</param>
/// <param name="DataVersion">Version label; files are named <c>&lt;region&gt;.&lt;version&gt;.json</c>.</param>
/// <param name="IsSample">True for a synthetic sample, which is always ranked last.</param>
/// <param name="Rows">Enabled rows, keyed by content id.</param>
public sealed record DutyDocument(
    Region Region,
    string DataVersion,
    bool IsSample,
    IReadOnlyDictionary<int, DutyInfo> Rows);

/// <summary>
/// Loads embedded duty display mappings. These mappings are presentation-only: they never
/// identify a roulette and never make a protocol profile usable.
///
/// The files are versioned per region (<c>cn.2026-09-04.json</c>,
/// <c>global.2026-09-04.json</c>, …). A lookup asks for a region and a data version; when
/// that exact version is not installed the newest one for the region is used instead, and a
/// content id that no installed version knows becomes <see cref="UnknownDutyName"/> rather
/// than a guess. Synthetic samples always rank below real data, so adding a sample file can
/// never shadow a real name.
/// </summary>
public sealed class DutyCatalog
{
    /// <summary>Display name for a null or unmapped content id.</summary>
    public const string UnknownDutyName = "未知副本";

    private const string ResourcePrefix = "MentorRecorder.Collector.Data.Duties.";
    private readonly IReadOnlyDictionary<(Region Region, int ContentId), DutyInfo> _byKey;
    private readonly IReadOnlyDictionary<(Region Region, int TerritoryId), DutyInfo> _byTerritory;

    private DutyCatalog(
        IReadOnlyDictionary<(Region, int), DutyInfo> byKey,
        IReadOnlyList<DutyDocument> documents)
    {
        _byKey = byKey;
        _byTerritory = IndexByTerritory(byKey.Values);
        Documents = documents;
    }

    /// <summary>An empty mapping that always falls back to unknown.</summary>
    public static DutyCatalog Empty { get; } =
        new(new Dictionary<(Region, int), DutyInfo>(), Array.Empty<DutyDocument>());

    /// <summary>Every embedded document, most preferred first within each region.</summary>
    public static IReadOnlyList<DutyDocument> EmbeddedDocuments { get; } = LoadEmbeddedDocuments();

    /// <summary>The best installed mapping for every region.</summary>
    public static DutyCatalog Default { get; } = Compose(EmbeddedDocuments);

    /// <summary>Number of enabled mappings.</summary>
    public int Count => _byKey.Count;

    /// <summary>Documents that make up this catalogue, most preferred first.</summary>
    public IReadOnlyList<DutyDocument> Documents { get; }

    /// <summary>
    /// Builds a catalogue for one region and data version: the exact version when it is
    /// installed, otherwise the newest one available for that region.
    /// </summary>
    /// <param name="region">Region to load.</param>
    /// <param name="dataVersion">Requested version label, or null for the newest.</param>
    public static DutyCatalog LoadFor(Region region, string? dataVersion) =>
        LoadFor(EmbeddedDocuments, region, dataVersion);

    /// <summary>Builds a catalogue for one region out of an explicit set of documents.</summary>
    /// <param name="documents">Documents to choose from.</param>
    /// <param name="region">Region to load.</param>
    /// <param name="dataVersion">Requested version label, or null for the newest.</param>
    public static DutyCatalog LoadFor(
        IReadOnlyList<DutyDocument> documents, Region region, string? dataVersion)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var forRegion = Rank(documents.Where(document => document.Region == region)).ToArray();
        if (forRegion.Length == 0)
        {
            return Empty;
        }

        if (!string.IsNullOrWhiteSpace(dataVersion))
        {
            var exact = forRegion.FirstOrDefault(document =>
                string.Equals(document.DataVersion, dataVersion, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                // The requested version wins, and the remaining ones stay available underneath
                // it so a duty the new file dropped is still nameable.
                return Compose(new[] { exact }
                    .Concat(forRegion.Where(document => !ReferenceEquals(document, exact)))
                    .ToArray());
            }
        }

        return Compose(forRegion);
    }

    /// <summary>
    /// Finds an enabled mapping for one region, or null.
    ///
    /// The lookup does not fall back to another region's file: a mapping from a different
    /// service that happens to share a content id is not evidence about this one, and a CN
    /// table lagging behind the international one would otherwise give a CN record an English
    /// duty name (review finding L-10). A duty this region's file does not know keeps its id
    /// and no name, which a later reference-file version can still resolve. The one exception
    /// is UNKNOWN, which is the absence of a region rather than one of them.
    /// </summary>
    /// <param name="contentId">Content id to look up.</param>
    /// <param name="region">Region whose mapping must supply the answer.</param>
    public DutyInfo? Find(int? contentId, Region region)
    {
        if (contentId is not { } id)
        {
            return null;
        }

        if (_byKey.TryGetValue((region, id), out var exact))
        {
            return exact;
        }

        // UNKNOWN is not a service: it is what a hand-entered run carries when the user never
        // said which client they play. There is no region to stay inside, so any installed
        // mapping is the best available answer.
        return region == Region.Unknown
            ? _byKey.Values.FirstOrDefault(row => row.ContentId == id)
            : null;
    }

    /// <summary>
    /// Finds the duty a territory belongs to, preferring the requested region.
    ///
    /// Territories are not unique: 21 of the 632 mapped CN territories host several duties,
    /// because a variant arena or a set of numbered stages shares one map. The tie is broken
    /// by the lowest content id, which is stable across reference-file versions and, for the
    /// shared-arena case, is a row whose name every duty on that territory also carries. It
    /// is still an inference from reference data rather than an observation, so the caller
    /// must not raise a run's detection confidence because of it.
    ///
    /// Like <see cref="Find"/>, the lookup stays inside the requested region, except for
    /// UNKNOWN, which is the absence of a region rather than one of them.
    /// </summary>
    /// <param name="territoryId">Territory id to look up.</param>
    /// <param name="region">Region whose mapping must supply the answer.</param>
    public DutyInfo? FindByTerritory(int? territoryId, Region region)
    {
        if (territoryId is not { } id)
        {
            return null;
        }

        if (_byTerritory.TryGetValue((region, id), out var exact))
        {
            return exact;
        }

        return region == Region.Unknown
            ? _byTerritory
                .Where(pair => pair.Key.TerritoryId == id)
                .OrderBy(pair => pair.Value.ContentId)
                .Select(pair => pair.Value)
                .FirstOrDefault()
            : null;
    }

    /// <summary>Returns the mapped name, or <see cref="UnknownDutyName"/>.</summary>
    /// <param name="contentId">Content id to look up.</param>
    /// <param name="region">Region whose mapping is preferred.</param>
    public string NameOf(int? contentId, Region region) =>
        Find(contentId, region)?.LocalizedName ?? UnknownDutyName;

    /// <summary>Parses one reference document.</summary>
    /// <param name="json">Document text.</param>
    /// <param name="dataVersion">Version label to use when the document does not declare one.</param>
    public static DutyDocument ParseDocument(string json, string? dataVersion = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        var file = JsonSerializer.Deserialize<DutyFile>(json)
            ?? throw new InvalidDataException("duty catalog is empty");
        if (file.SchemaVersion != 1 || !EnumWire<Region>.TryParse(file.Region, out var region))
        {
            throw new InvalidDataException("duty catalog schema_version or region is unsupported");
        }

        var rows = new Dictionary<int, DutyInfo>();
        var everyRowIsSample = true;
        foreach (var row in file.Duties ?? Array.Empty<DutyRow>())
        {
            if (row.ContentId < 0 || string.IsNullOrWhiteSpace(row.LocalizedName))
            {
                throw new InvalidDataException("duty rows require a non-negative content_id and a name");
            }

            everyRowIsSample &= row.Sample;
            if (rows.ContainsKey(row.ContentId))
            {
                throw new InvalidDataException($"duplicate duty content_id {row.ContentId} for {file.Region}");
            }

            if (row.Enabled)
            {
                rows.Add(row.ContentId, new DutyInfo(
                    row.ContentId,
                    row.TerritoryId,
                    row.LocalizedName,
                    row.DutyCategory,
                    region,
                    row.Sample));
            }
        }

        return new DutyDocument(
            region,
            file.DataVersion ?? dataVersion ?? "unversioned",
            file.Sample ?? (everyRowIsSample && rows.Count > 0),
            rows);
    }

    /// <summary>Parses one reference document into a single-document catalogue.</summary>
    /// <param name="json">Document text.</param>
    public static DutyCatalog Parse(string json) => Compose(new[] { ParseDocument(json) });

    /// <summary>
    /// Merges documents in preference order: the first document that knows a content id wins,
    /// and every later one only fills gaps. That is what makes the fallback to an older
    /// version additive rather than destructive.
    /// </summary>
    /// <param name="documents">Documents, most preferred first.</param>
    public static DutyCatalog Compose(IReadOnlyList<DutyDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var combined = new Dictionary<(Region, int), DutyInfo>();
        foreach (var document in documents)
        {
            foreach (var row in document.Rows)
            {
                combined.TryAdd((document.Region, row.Key), row.Value);
            }
        }

        return new DutyCatalog(combined, documents);
    }

    /// <summary>
    /// One duty per (region, territory), chosen by the lowest content id. Built once because
    /// the alternative -- scanning 857 rows on every duty entry -- runs on the capture thread.
    /// </summary>
    /// <param name="rows">Every enabled mapping in the catalogue.</param>
    private static IReadOnlyDictionary<(Region, int), DutyInfo> IndexByTerritory(
        IEnumerable<DutyInfo> rows)
    {
        var index = new Dictionary<(Region, int), DutyInfo>();
        foreach (var row in rows)
        {
            if (row.TerritoryId is not { } territoryId)
            {
                continue;
            }

            var key = (row.Region, territoryId);
            if (!index.TryGetValue(key, out var existing) || row.ContentId < existing.ContentId)
            {
                index[key] = row;
            }
        }

        return index;
    }

    private static IEnumerable<DutyDocument> Rank(IEnumerable<DutyDocument> documents) => documents
        .OrderBy(document => document.IsSample ? 1 : 0)
        .ThenByDescending(document => document.DataVersion, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<DutyDocument> LoadEmbeddedDocuments()
    {
        var assembly = typeof(DutyCatalog).GetTypeInfo().Assembly;
        var documents = new List<DutyDocument>();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                                    name.EndsWith(".json", StringComparison.Ordinal))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidDataException($"cannot open embedded duty catalog {resource}");
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            documents.Add(ParseDocument(reader.ReadToEnd(), VersionFromResourceName(resource)));
        }

        return Rank(documents).ToArray();
    }

    /// <summary>
    /// Version label carried by a file name of the form <c>&lt;region&gt;.&lt;version&gt;.json</c>.
    /// </summary>
    /// <param name="resourceName">Embedded resource name or file name.</param>
    public static string VersionFromResourceName(string resourceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        var name = resourceName.EndsWith(".json", StringComparison.Ordinal)
            ? resourceName[..^5]
            : resourceName;
        var lastDot = name.LastIndexOf('.');
        return lastDot < 0 ? "unversioned" : name[(lastDot + 1)..];
    }

    private sealed record DutyFile
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("region")]
        public string? Region { get; init; }

        [JsonPropertyName("data_version")]
        public string? DataVersion { get; init; }

        [JsonPropertyName("sample")]
        public bool? Sample { get; init; }

        [JsonPropertyName("duties")]
        public DutyRow[]? Duties { get; init; }
    }

    private sealed record DutyRow
    {
        [JsonPropertyName("content_id")]
        public int ContentId { get; init; }

        [JsonPropertyName("territory_id")]
        public int? TerritoryId { get; init; }

        [JsonPropertyName("localized_name")]
        public string? LocalizedName { get; init; }

        [JsonPropertyName("duty_category")]
        public string? DutyCategory { get; init; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; } = true;

        [JsonPropertyName("sample")]
        public bool Sample { get; init; }
    }
}
