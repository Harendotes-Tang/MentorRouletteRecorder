using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Reference;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The duty tables are versioned per region, so the selection rules are under test: the
/// requested version wins, an uninstalled version falls back to the newest one, a
/// synthetic sample never shadows real data, and an unmapped content id stays unknown rather
/// than becoming a guess.
/// </summary>
public sealed class ReferenceDutyVersionTests
{
    private const string Older = """
        {"schema_version":1,"region":"CN","language":"zh-Hans","data_version":"2026-01-01",
         "duties":[
           {"content_id":1,"territory_id":11,"localized_name":"旧名 A","duty_category":"四人迷宫"},
           {"content_id":2,"territory_id":12,"localized_name":"只在旧表里的 B","duty_category":"讨伐歼灭战"}
         ]}
        """;

    private const string Newer = """
        {"schema_version":1,"region":"CN","language":"zh-Hans","data_version":"2026-09-04",
         "duties":[
           {"content_id":1,"territory_id":11,"localized_name":"新名 A","duty_category":"四人迷宫"},
           {"content_id":3,"territory_id":13,"localized_name":"新增的 C","duty_category":"大型任务"}
         ]}
        """;

    private const string Sample = """
        {"schema_version":1,"region":"CN","language":"zh-Hans","data_version":"sample","sample":true,
         "duties":[
           {"content_id":1,"localized_name":"样例 A","duty_category":"迷宫挑战","sample":true},
           {"content_id":900001,"localized_name":"样例迷宫挑战 A","duty_category":"迷宫挑战","sample":true}
         ]}
        """;

    private static IReadOnlyList<DutyDocument> Documents() => new[]
    {
        DutyCatalog.ParseDocument(Older),
        DutyCatalog.ParseDocument(Newer),
        DutyCatalog.ParseDocument(Sample),
    };

    [Fact]
    public void WithoutARequestedVersionTheNewestRealTableWins()
    {
        var catalog = DutyCatalog.LoadFor(Documents(), Region.Cn, null);

        Assert.Equal("新名 A", catalog.NameOf(1, Region.Cn));
        Assert.Equal("新增的 C", catalog.NameOf(3, Region.Cn));
    }

    [Fact]
    public void AnOlderVersionStillFillsGapsTheNewerTableDropped()
    {
        var catalog = DutyCatalog.LoadFor(Documents(), Region.Cn, null);

        Assert.Equal("只在旧表里的 B", catalog.NameOf(2, Region.Cn));
    }

    [Fact]
    public void TheRequestedVersionWinsWhenItIsInstalled()
    {
        var catalog = DutyCatalog.LoadFor(Documents(), Region.Cn, "2026-01-01");

        Assert.Equal("旧名 A", catalog.NameOf(1, Region.Cn));
        Assert.Equal("新增的 C", catalog.NameOf(3, Region.Cn));
    }

    [Fact]
    public void AnUninstalledVersionFallsBackToTheNewestAvailable()
    {
        var catalog = DutyCatalog.LoadFor(Documents(), Region.Cn, "2027-12-31");

        Assert.Equal("新名 A", catalog.NameOf(1, Region.Cn));
    }

    [Fact]
    public void ASyntheticSampleNeverShadowsRealData()
    {
        var catalog = DutyCatalog.LoadFor(Documents(), Region.Cn, null);

        Assert.Equal("新名 A", catalog.NameOf(1, Region.Cn));
        Assert.Equal("样例迷宫挑战 A", catalog.NameOf(900001, Region.Cn));
    }

    [Fact]
    public void AnUnmappedContentIdIsUnknownRatherThanAGuess()
    {
        var catalog = DutyCatalog.LoadFor(Documents(), Region.Cn, null);

        Assert.Equal(DutyCatalog.UnknownDutyName, catalog.NameOf(4242, Region.Cn));
        Assert.Equal(DutyCatalog.UnknownDutyName, catalog.NameOf(null, Region.Cn));
        Assert.Null(catalog.Find(4242, Region.Cn));
    }

    [Fact]
    public void ARegionWithNoTableIsEmptyRatherThanBorrowingAnother()
    {
        var catalog = DutyCatalog.LoadFor(Documents(), Region.Global, null);

        Assert.Equal(0, catalog.Count);
        Assert.Equal(DutyCatalog.UnknownDutyName, catalog.NameOf(1, Region.Global));
    }

    [Fact]
    public void VersionIsTakenFromTheFileName()
    {
        Assert.Equal("2026-09-04", DutyCatalog.VersionFromResourceName("cn.2026-09-04.json"));
        Assert.Equal("sample", DutyCatalog.VersionFromResourceName("cn.sample.json"));
        Assert.Equal(
            "2026-09-04",
            DutyCatalog.VersionFromResourceName("MentorRecorder.Collector.Data.Duties.global.2026-09-04.json"));
    }

    [Fact]
    public void GeneratedTablesAreEmbeddedForBothRegions()
    {
        var cn = DutyCatalog.LoadFor(Region.Cn, null);
        var global = DutyCatalog.LoadFor(Region.Global, null);

        Assert.True(cn.Count > 100, "the generated CN duty table should be embedded");
        Assert.True(global.Count > 100, "the generated GLOBAL duty table should be embedded");
        Assert.All(
            cn.Documents.Where(document => !document.IsSample),
            document => Assert.Equal(Region.Cn, document.Region));
    }

    [Fact]
    public void GeneratedTablesCarryCategoriesAndTerritories()
    {
        var cn = DutyCatalog.LoadFor(Region.Cn, null);

        var mapped = cn.Find(1, Region.Cn);
        Assert.NotNull(mapped);
        Assert.False(mapped.IsSyntheticSample);
        Assert.False(string.IsNullOrWhiteSpace(mapped.DutyCategory));
        Assert.NotNull(mapped.TerritoryId);
    }
}
