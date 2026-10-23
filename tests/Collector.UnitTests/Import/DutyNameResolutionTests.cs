using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class DutyNameResolutionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("Missing")]
    [InlineData("unique")]
    [InlineData(" Unique ")]
    public void NamesAreExactAndBlankOrUnmatchedNamesStayUnknown(string? name)
    {
        var catalog = Parse("""
            {"content_id":1,"localized_name":"Unique"}
            """);
        Assert.Null(catalog.FindByName(name, Region.Cn));
        Assert.Null(catalog.FindByName(name, Region.Unknown));
        Assert.Equal(1, catalog.FindByName("Unique", Region.Cn)!.ContentId);
        Assert.False(catalog.Find(1, Region.Cn)!.PreferredForNameMatching);
    }

    [Fact]
    public void OnlyOneCandidateOrExactlyOneAuditedPreferenceCanResolveAName()
    {
        var unique = Parse("""
            {"content_id":1,"localized_name":"Name"}
            """);
        var ambiguous = Parse("""
            {"content_id":1,"localized_name":"Name"},
            {"content_id":2,"localized_name":"Name"}
            """);
        var preferred = Parse("""
            {"content_id":1,"localized_name":"Name"},
            {"content_id":2,"localized_name":"Name","preferred_for_name_matching":true}
            """);
        var competingPreferences = Parse("""
            {"content_id":1,"localized_name":"Name","preferred_for_name_matching":true},
            {"content_id":2,"localized_name":"Name","preferred_for_name_matching":true}
            """);
        Assert.Equal(1, unique.FindByName("Name", Region.Cn)!.ContentId);
        Assert.Null(ambiguous.FindByName("Name", Region.Cn));
        Assert.Equal(2, preferred.FindByName("Name", Region.Cn)!.ContentId);
        Assert.True(preferred.Find(2, Region.Cn)!.PreferredForNameMatching);
        Assert.Equal(1, preferred.Find(1, Region.Cn)!.ContentId);
        Assert.Equal("Name", preferred.Find(1, Region.Cn)!.LocalizedName);
        Assert.Equal(2, preferred.Count);
        Assert.Null(competingPreferences.FindByName("Name", Region.Cn));
        Assert.Null(competingPreferences.FindByName("Name", Region.Unknown));
    }

    [Fact]
    public void DisabledRowsCannotSupplyNamesOrPreferences()
    {
        var catalog = Parse("""
            {"content_id":1,"localized_name":"Name"},
            {"content_id":2,"localized_name":"Name","preferred_for_name_matching":true,"enabled":false},
            {"content_id":3,"localized_name":"Disabled","preferred_for_name_matching":true,"enabled":false}
            """);
        Assert.Equal(1, catalog.FindByName("Name", Region.Cn)!.ContentId);
        Assert.Null(catalog.FindByName("Disabled", Region.Cn));
    }

    [Fact]
    public void NameResolutionUsesComposedRowsWithoutLeakingOldNamesOrPreferences()
    {
        var newer = Document("CN", "newer", """
            {"content_id":1,"localized_name":"Name"},
            {"content_id":2,"localized_name":"Name"},
            {"content_id":4,"localized_name":"New name"}
            """);
        var older = Document("CN", "older", """
            {"content_id":1,"localized_name":"Name","preferred_for_name_matching":true},
            {"content_id":3,"localized_name":"Older fallback"},
            {"content_id":4,"localized_name":"Old name"}
            """);
        var catalog = DutyCatalog.Compose(new[] { newer, older });
        Assert.Null(catalog.FindByName("Name", Region.Cn));
        Assert.False(catalog.Find(1, Region.Cn)!.PreferredForNameMatching);
        Assert.Null(catalog.FindByName("Old name", Region.Cn));
        Assert.Equal(4, catalog.FindByName("New name", Region.Cn)!.ContentId);
        Assert.Equal(3, catalog.FindByName("Older fallback", Region.Cn)!.ContentId);
        var requested = DutyCatalog.LoadFor(new[] { newer, older }, Region.Cn, "older");
        Assert.Equal(1, requested.FindByName("Name", Region.Cn)!.ContentId);
        Assert.Null(requested.FindByName("New name", Region.Cn));
    }

    [Fact]
    public void ExplicitRegionsNeverBorrowMatchingNamesFromAnotherRegion()
    {
        var catalog = DutyCatalog.Compose(new[]
        {
            Document("CN", "v1", """
                {"content_id":1,"localized_name":"Chinese"}
                """),
            Document("GLOBAL", "v1", """
                {"content_id":2,"localized_name":"English"}
                """),
        });
        Assert.Null(catalog.FindByName("English", Region.Cn));
        Assert.Null(catalog.FindByName("Chinese", Region.Global));
        Assert.Equal(1, catalog.FindByName("Chinese", Region.Unknown)!.ContentId);
        Assert.Equal(2, catalog.FindByName("English", Region.Unknown)!.ContentId);
    }

    [Fact]
    public void UnknownRegionRequiresEachMatchingNamedRegionToResolveToTheSameIdentity()
    {
        var cn = Document("CN", "v1", """
            {"content_id":1,"localized_name":"Agree"},
            {"content_id":2,"localized_name":"Conflict"},
            {"content_id":3,"localized_name":"Unresolved elsewhere"},
            {"content_id":4,"localized_name":"Preferred elsewhere","preferred_for_name_matching":true},
            {"content_id":5,"localized_name":"Preferred elsewhere"}
            """);
        var global = Document("GLOBAL", "v1", """
            {"content_id":1,"localized_name":"Agree"},
            {"content_id":6,"localized_name":"Conflict"},
            {"content_id":3,"localized_name":"Unresolved elsewhere"},
            {"content_id":7,"localized_name":"Unresolved elsewhere"},
            {"content_id":4,"localized_name":"Preferred elsewhere"}
            """);
        var unnamed = Document("UNKNOWN", "v1", """
            {"content_id":8,"localized_name":"Agree","preferred_for_name_matching":true},
            {"content_id":9,"localized_name":"Unnamed only"}
            """);
        var catalog = DutyCatalog.Compose(new[] { cn, global, unnamed });
        Assert.Equal(1, catalog.FindByName("Agree", Region.Unknown)!.ContentId);
        Assert.Equal(4, catalog.FindByName("Preferred elsewhere", Region.Unknown)!.ContentId);
        Assert.Null(catalog.FindByName("Conflict", Region.Unknown));
        Assert.Null(catalog.FindByName("Unresolved elsewhere", Region.Unknown));
        Assert.Null(catalog.FindByName("Unnamed only", Region.Unknown));
        Assert.Equal(3, catalog.FindByName("Unresolved elsewhere", Region.Cn)!.ContentId);
    }

    [Fact]
    public void NormalizerKeepsExplicitIdentityAndSourceCandidateWhileReportingResolvedRun()
    {
        var input = new JsonObject { ["content_id"] = 69, ["duty_name"] = "阿修罗歼灭战", ["region"] = "CN" };
        var normalized = Normalize(input);
        Assert.Empty(normalized.Errors);
        Assert.Equal(69, normalized.Run!.ContentId);
        Assert.Equal(69, normalized.Input["content_id"]!.GetValue<int>());
        Assert.Equal("阿修罗歼灭战", normalized.Run.DutyName);
        Assert.Equal(69, input["content_id"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("\"\"")]
    public void InferredIdentityIsNotPromotedToAnExplicitSourceId(string sourceId)
    {
        var input = new JsonObject { ["duty_name"] = "水晶塔 古代人迷宫", ["region"] = "CN" };
        if (sourceId != "missing")
        {
            input["content_id"] = JsonNode.Parse(sourceId);
            input["territory_id"] = JsonNode.Parse(sourceId);
            input["duty_category"] = JsonNode.Parse(sourceId);
        }
        var normalized = Normalize(input);
        Assert.Empty(normalized.Errors);
        Assert.Equal(92, normalized.Run!.ContentId);
        Assert.Equal(sourceId != "missing", normalized.Input.ContainsKey("content_id"));
        Assert.Equal(input["content_id"]?.ToJsonString(), normalized.Input["content_id"]?.ToJsonString());
        Assert.Equal(input["territory_id"]?.ToJsonString(), normalized.Input["territory_id"]?.ToJsonString());
        Assert.Equal(input["duty_category"]?.ToJsonString(), normalized.Input["duty_category"]?.ToJsonString());
        var changed = normalized.Input.DeepClone().AsObject();
        changed["duty_name"] = "Not in the installed reference";
        var changedRun = Normalize(changed).Run!;
        Assert.Null(changedRun.ContentId);
        Assert.Null(changedRun.TerritoryId);
        Assert.Null(changedRun.DutyCategory);
    }

    [Fact]
    public void ExplicitTerritoryAndCategoryRemainSourceFactsWhenOnlyTheNameChanges()
    {
        var normalized = Normalize(new JsonObject
        {
            ["duty_name"] = "阿修罗歼灭战", ["region"] = "CN",
            ["content_id"] = null, ["territory_id"] = 123, ["duty_category"] = "人工分类",
        });
        Assert.Equal(944, normalized.Run!.ContentId);
        Assert.Equal(123, normalized.Run.TerritoryId);
        Assert.Equal("人工分类", normalized.Run.DutyCategory);
        var changed = normalized.Input.DeepClone().AsObject();
        changed["duty_name"] = "Not in the installed reference";
        var changedRun = Normalize(changed).Run!;
        Assert.Null(changedRun.ContentId);
        Assert.Equal(123, changedRun.TerritoryId);
        Assert.Equal("人工分类", changedRun.DutyCategory);
    }

    [Fact]
    public void InstalledAsuraPreferencePreservesBothCatalogIdentitiesAndRawNames()
    {
        var catalog = DutyCatalog.Default;
        Assert.Equal("阿修罗歼灭战", catalog.Find(69, Region.Cn)!.LocalizedName);
        Assert.Equal("阿修罗歼灭战", catalog.Find(944, Region.Cn)!.LocalizedName);
        Assert.False(catalog.Find(69, Region.Cn)!.PreferredForNameMatching);
        Assert.True(catalog.Find(944, Region.Cn)!.PreferredForNameMatching);
        Assert.Equal(944, catalog.FindByName("阿修罗歼灭战", Region.Cn)!.ContentId);
        Assert.Equal(944, catalog.FindByName("阿修罗歼灭战", Region.Unknown)!.ContentId);
        Assert.Null(catalog.FindByName("阿修罗歼灭战", Region.Global));
        var normalized = Normalize(new JsonObject { ["duty_name"] = "阿修罗歼灭战" });
        Assert.Equal(944, normalized.Run!.ContentId);
        Assert.Equal("阿修罗歼灭战", normalized.Run.DutyName);
        Assert.False(normalized.Input.ContainsKey("content_id"));
    }

    [Fact]
    public void OtherSixteenInstalledDuplicateGroupsRemainAmbiguousWithoutAnExplicitId()
    {
        var catalog = DutyCatalog.Default;
        var groups = catalog.Documents.SelectMany(document => document.Rows.Keys).Distinct()
            .Select(id => catalog.Find(id, Region.Cn)).OfType<DutyInfo>()
            .GroupBy(row => row.LocalizedName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1 && group.Key != "阿修罗歼灭战").ToArray();
        Assert.Equal(16, groups.Length);
        foreach (var group in groups)
        {
            Assert.All(group, duty => Assert.False(duty.PreferredForNameMatching));
            Assert.Null(catalog.FindByName(group.Key, Region.Cn));
            Assert.Null(catalog.FindByName(group.Key, Region.Unknown));
            Assert.Null(Normalize(new JsonObject { ["duty_name"] = group.Key, ["region"] = "CN" }).Run!.ContentId);
        }
    }

    public static IEnumerable<object[]> EarlierAsuraImports() =>
        from legacyKey in new[] { false, true }
        from oldJobAlias in new[] { false, true }
        from purged in new[] { false, true }
        from explicitDisplayFacts in new[] { false, true }
        select new object[] { legacyKey, oldJobAlias, purged, explicitDisplayFacts };

    [Theory]
    [MemberData(nameof(EarlierAsuraImports))]
    public void EarlierUnknownAsuraImportsAndPurgeTombstonesStillMatchWithoutRewritingHistory(
        bool legacyKey, bool oldJobAlias, bool purged, bool explicitDisplayFacts)
    {
        using var fixture = new TestDatabase();
        var csv = "duty_name,job_name,source_recorded_at,region,territory_id,duty_category\n" + string.Join('\n', Enumerable.Range(1, 5)
            .Select(day => $"阿修罗歼灭战,{(oldJobAlias ? "白魔" : "骑士")},2026-10-{day:00},CN,"
                + (explicitDisplayFacts ? "123,人工分类" : ",")));
        var parsed = RunImportSourceParser.Parse("CSV", text: csv);
        var wholeFile = RunImportCandidateNormalizer.Hash(new JsonArray(parsed
            .Select(row => (JsonNode)row.Values.DeepClone()).ToArray()).ToJsonString(Wire.JsonOptions));
        var repository = new RunRepository(fixture.Database);
        var imports = new RunImportRepository(fixture.Database);
        var earlier = new List<MentorRun>();
        foreach (var row in parsed)
        {
            var sourceKey = RunImportCandidateNormalizer.Hash(RunImportCandidateNormalizer.CanonicalValues(row.Values)) + ":1";
            row.Values["source_key"] = sourceKey;
            var current = RunImportCandidateNormalizer.Normalize(row, "CSV", "synthetic-upgrade", null, fixture.Clock.UtcNow).Run!;
            Assert.Equal(944, current.ContentId);
            // Before the audited preference neither id won; only source-supplied map/category
            // facts survived. The old alias may also name no job.
            var old = current with
            {
                ContentId = null, TerritoryId = explicitDisplayFacts ? 123 : null,
                DutyCategory = explicitDisplayFacts ? "人工分类" : null, DutySource = null,
                JobId = oldJobAlias ? null : current.JobId,
                Role = oldJobAlias ? Role.Unknown : current.Role,
            };
            var fingerprint = RunImportCandidateNormalizer.Hash(string.Join('\n', "CSV", "synthetic-upgrade",
                legacyKey ? wholeFile + ":" + row.RowNumber : sourceKey, RunImportCandidateNormalizer.Facts(old)));
            old = old with { ImportMetadata = old.ImportMetadata! with { SourceFingerprint = fingerprint } };
            earlier.Add(old);
            fixture.Database.RunInTransaction(transaction =>
            {
                if (purged)
                {
                    using var command = fixture.Database.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO purged_run_tombstones VALUES ($id,$fingerprint,'2026-10-22T00:00:00.000Z');";
                    command.Parameters.AddWithValue("$id", old.RunId);
                    command.Parameters.AddWithValue("$fingerprint", fingerprint);
                    command.ExecuteNonQuery();
                }
                else
                {
                    repository.Insert(old, transaction);
                    imports.Insert(old.RunId, old.ImportMetadata!, transaction);
                }
            });
        }

        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("CSV", text: csv, sourceName: "synthetic-upgrade");
        Assert.All(preview["rows"]!.AsArray(), row =>
        {
            Assert.Equal(purged ? "conflict" : "duplicate", row!["status"]!.GetValue<string>());
            Assert.Equal(944, row["run"]!["content_id"]!.GetValue<int>());
        });
        // One legacy case also evicts the original file preview: each child must retain the
        // server-computed compatibility set rather than depending on that first cache entry.
        var rechecks = legacyKey && !oldJobAlias && !purged && !explicitDisplayFacts
            ? RunImportService.MaxPreviews + 1 : 2;
        for (var recheck = 0; recheck < rechecks; recheck++)
        {
            var candidates = new JsonArray(preview["rows"]!.AsArray()
                .Select(row => row!["candidate"]!.DeepClone()).ToArray());
            preview = service.PreviewSource("CSV", rows: candidates, sourceName: "synthetic-upgrade");
            Assert.All(preview["rows"]!.AsArray(), row =>
                Assert.Equal(purged ? "conflict" : "duplicate", row!["status"]!.GetValue<string>()));
        }
        var result = service.Commit(preview["preview_id"]!.GetValue<string>(),
            preview["rows"]!.AsArray().Select(row => row!["row_number"]!.GetValue<int>()).ToArray(),
            true, Guid.NewGuid().ToString("D"));
        Assert.Equal(0, result["imported_count"]!.GetValue<int>());
        Assert.Equal(5, result[purged ? "conflict_count" : "duplicate_count"]!.GetValue<int>());
        Assert.Equal(purged ? 0 : 5, repository.Query(null, null, 1, 20).Total);
        if (!purged)
            Assert.All(earlier, old => Assert.Equal(old, repository.Get(old.RunId)));

        // A newly explicit id is an additional source fact, not an older unknown row.
        var explicitRow = parsed[0].Values.DeepClone().AsObject();
        explicitRow["content_id"] = 69;
        var explicitPreview = service.PreviewSource("CSV", rows: new JsonArray(explicitRow), sourceName: "synthetic-upgrade");
        Assert.Equal(69, explicitPreview["rows"]![0]!["run"]!["content_id"]!.GetValue<int>());
        Assert.Equal("new", explicitPreview["rows"]![0]!["status"]!.GetValue<string>());
        // An explicit 944 has the same current facts hash as an inferred 944. It must not
        // inherit compatibility identities that erase this newly supplied source fact.
        explicitRow["content_id"] = 944;
        var explicit944 = service.PreviewSource("CSV", rows: new JsonArray(explicitRow.DeepClone()), sourceName: "synthetic-upgrade");
        Assert.Equal("new", explicit944["rows"]![0]!["status"]!.GetValue<string>());
        var changedCandidate = preview["rows"]![0]!["candidate"]!.DeepClone().AsObject();
        changedCandidate["note"] = "A newly supplied source fact";
        var changedFacts = service.PreviewSource("CSV", rows: new JsonArray(changedCandidate), sourceName: "synthetic-upgrade");
        Assert.Equal("new", changedFacts["rows"]![0]!["status"]!.GetValue<string>());
        if (explicitDisplayFacts)
        {
            var changed = service.PreviewSource("CSV", text: csv.Replace("人工分类", "其他分类", StringComparison.Ordinal),
                sourceName: "synthetic-upgrade");
            Assert.All(changed["rows"]!.AsArray(), row => Assert.Equal("new", row!["status"]!.GetValue<string>()));
        }
        if (!purged)
        {
            // Simulate a permanent deletion after preview. Rechecking must read current DB
            // state and see the tombstone, not carry forward a cached duplicate/matching id.
            var deleted = earlier[0];
            fixture.Database.RunInTransaction(transaction =>
            {
                using var command = fixture.Database.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM run_import_metadata WHERE run_id=$id; "
                    + "DELETE FROM mentor_runs WHERE run_id=$id; "
                    + "INSERT INTO purged_run_tombstones VALUES ($id,$fingerprint,'2026-10-22T00:00:00.000Z');";
                command.Parameters.AddWithValue("$id", deleted.RunId);
                command.Parameters.AddWithValue("$fingerprint", deleted.ImportMetadata!.SourceFingerprint);
                command.ExecuteNonQuery();
            });
            var current = service.PreviewSource("CSV", rows: new JsonArray(preview["rows"]!.AsArray()
                .Select(row => row!["candidate"]!.DeepClone()).ToArray()), sourceName: "synthetic-upgrade");
            Assert.Equal("conflict", current["rows"]![0]!["status"]!.GetValue<string>());
            Assert.Null(repository.Get(deleted.RunId));
            Assert.Equal(4, repository.Query(null, null, 1, 20).Total);
        }
    }

    private static DutyCatalog Parse(string rows) => DutyCatalog.Compose(new[] { Document("CN", "v1", rows) });

    private static DutyDocument Document(string region, string version, string rows) => DutyCatalog.ParseDocument(
        $$"""
          {"schema_version":1,"region":"{{region}}","data_version":"{{version}}","duties":[{{rows}}]}
          """);

    private static ImportCandidate Normalize(JsonObject input) => RunImportCandidateNormalizer.Normalize(
        new ImportSourceRow(1, input, Array.Empty<MentorRecorder.Collector.Contracts.Texts.UserText>()),
        "ROWS", "synthetic-name-test", null, DateTimeOffset.Parse("2026-10-22T00:00:00Z"));
}
