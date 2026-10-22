using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Reference;

/// <summary>
/// Derives the alliance-raid display category from preferred catalogue facts without
/// changing stored history or the catalogue's raw category. Identity and SQL membership
/// use the same regional indexes, so UNKNOWN cannot silently choose a conflicting service.
/// </summary>
public sealed class DutyCategoryProjection
{
    /// <summary>Raw category shared by standard eight- and twenty-four-player raids.</summary>
    public const string RaidCategory = "大型任务";

    /// <summary>Display category for the standard twenty-four-player raids.</summary>
    public const string AllianceCategory = "团队任务";

    private readonly IReadOnlyDictionary<(Region Region, int Id), DutyInfo> _content;
    private readonly IReadOnlyDictionary<(Region Region, int Id), DutyInfo> _territories;

    internal DutyCategoryProjection(IEnumerable<DutyInfo> preferredRows)
    {
        var rows = preferredRows.ToArray();
        var content = new Dictionary<(Region, int), DutyInfo>();
        var territories = new Dictionary<(Region, int), DutyInfo>();
        foreach (var row in rows) content[(row.Region, row.ContentId)] = row;
        foreach (var group in rows.Where(row => row.TerritoryId.HasValue)
                     .GroupBy(row => (row.Region, row.TerritoryId!.Value)))
        {
            if (group.Select(row => row.ContentId).Distinct().Count() == 1)
                territories[group.Key] = group.First();
        }

        // UNKNOWN means no service was recorded. Every installed service which knows the
        // identity must agree on the raw category and party size; names may be translated.
        foreach (var group in rows.GroupBy(row => row.ContentId))
        {
            if (Consistent(group)) content[(Region.Unknown, group.Key)] = group.First();
            else content.Remove((Region.Unknown, group.Key));
        }
        foreach (var group in rows.Where(row => row.TerritoryId.HasValue).GroupBy(row => row.TerritoryId!.Value))
        {
            if (Consistent(group)) territories[(Region.Unknown, group.Key)] = group.First();
            else territories.Remove((Region.Unknown, group.Key));
        }
        _content = content;
        _territories = territories;
    }

    /// <summary>
    /// Returns the effective category. Explicit custom categories are preserved. An observed
    /// content id, even an unknown one, prevents territory fallback; territory-only evidence
    /// must identify exactly one duty in the applicable service(s).
    /// </summary>
    public string? Resolve(string? recordedCategory, int? contentId, int? territoryId, Region region)
    {
        if (!string.IsNullOrWhiteSpace(recordedCategory) && recordedCategory != RaidCategory)
            return recordedCategory;
        var index = contentId.HasValue ? _content : _territories;
        var id = contentId ?? territoryId;
        return id is { } key && index.TryGetValue((region, key), out var duty) && IsAlliance(duty)
            ? AllianceCategory
            : recordedCategory;
    }

    /// <summary>Content identities that resolve to standard alliance raids in one service.</summary>
    public IReadOnlyList<int> AllianceContentIds(Region region) => Members(_content, region);

    /// <summary>Unambiguous territory identities that resolve to alliance raids in one service.</summary>
    public IReadOnlyList<int> AllianceTerritoryIds(Region region) => Members(_territories, region);

    private static IReadOnlyList<int> Members(IReadOnlyDictionary<(Region Region, int Id), DutyInfo> index, Region region) =>
        index.Where(pair => pair.Key.Region == region && IsAlliance(pair.Value))
            .Select(pair => pair.Key.Id).OrderBy(id => id).ToArray();

    private static bool IsAlliance(DutyInfo duty) => duty.PartySize == 24 && duty.DutyCategory == RaidCategory;

    private static bool Consistent(IEnumerable<DutyInfo> candidates)
    {
        var first = candidates.First();
        return candidates.All(row => row.ContentId == first.ContentId && row.PartySize == first.PartySize
            && row.DutyCategory == first.DutyCategory);
    }
}
