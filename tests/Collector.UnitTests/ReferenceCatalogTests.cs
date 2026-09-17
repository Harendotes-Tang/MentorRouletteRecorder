using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Reference;

namespace MentorRecorder.Collector.UnitTests;

public sealed class ReferenceCatalogTests
{
    [Fact]
    public void DefaultJobCatalog_MapsKnownAndUnknownJobs()
    {
        var catalog = JobCatalog.Default;

        Assert.True(catalog.Count >= 40);
        Assert.Equal("骑士", catalog.NameOf(19));
        Assert.Equal(Role.Tank, catalog.RoleOf(19));
        Assert.Equal("未知", catalog.NameOf(999999));
        Assert.Equal(Role.Unknown, catalog.RoleOf(null));
    }

    [Fact]
    public void ParseJobCatalog_RejectsDuplicateIds()
    {
        const string json = """
            {"version":1,"jobs":[
              {"job_id":19,"abbreviation":"PLD","name_en":"paladin","name_zh":"骑士","role":"坦克"},
              {"job_id":19,"abbreviation":"XXX","name_en":"duplicate","name_zh":"重复","role":"其他"}
            ]}
            """;

        Assert.Throws<InvalidDataException>(() => JobCatalog.Parse(json));
    }

    [Fact]
    public void DefaultDutyCatalog_LoadsSyntheticSampleAndFallsBackToUnknown()
    {
        var catalog = DutyCatalog.Default;

        var known = catalog.Find(900001, Region.Cn);
        Assert.NotNull(known);
        Assert.Equal("样例迷宫挑战 A", known.LocalizedName);
        Assert.True(known.IsSyntheticSample);
        Assert.Equal(DutyCatalog.UnknownDutyName, catalog.NameOf(null, Region.Cn));
        Assert.Null(catalog.Find(900005, Region.Cn));
    }

    /// <summary>
    /// Territory lookup turns the CN territory announcement into a duty name. Both of its
    /// cases are pinned: a territory that hosts one duty, and one of the 21 CN territories
    /// that host several (docs/state-machine.md section 3.11).
    /// </summary>
    [Fact]
    public void DefaultDutyCatalog_ResolvesATerritoryToADuty()
    {
        var catalog = DutyCatalog.Default;

        var unique = catalog.FindByTerritory(1036, Region.Cn);
        Assert.NotNull(unique);
        Assert.Equal(4, unique.ContentId);
        Assert.Equal("天然要害沙斯塔夏溶洞", unique.LocalizedName);

        // Territory 792 carries nine numbered stages that all share one name, so the lowest
        // content id is both the stable choice and the right name.
        var shared = catalog.FindByTerritory(792, Region.Cn);
        Assert.NotNull(shared);
        Assert.Equal(600, shared.ContentId);
        Assert.Equal("虚景跳跳乐大挑战", shared.LocalizedName);

        Assert.Null(catalog.FindByTerritory(null, Region.Cn));
        Assert.Null(catalog.FindByTerritory(7_000_001, Region.Cn));
    }

    /// <summary>
    /// Review finding L-10. A mapping from another service that happens to share a content id
    /// is not evidence about this one, so a CN record must not pick up an international name
    /// just because the CN reference file lags behind.
    /// </summary>
    [Fact]
    public void DutyCatalog_DoesNotFallBackToAnotherRegion()
    {
        var catalog = DutyCatalog.Compose(new[]
        {
            DutyCatalog.ParseDocument("""
                {"schema_version":1,"region":"GLOBAL","data_version":"2026-09-04","duties":[
                  {"content_id":4242,"territory_id":9042,"localized_name":"Global Only","enabled":true}
                ]}
                """),
            DutyCatalog.ParseDocument("""
                {"schema_version":1,"region":"CN","data_version":"2026-09-04","duties":[
                  {"content_id":11,"territory_id":9011,"localized_name":"国服副本","enabled":true}
                ]}
                """),
        });

        Assert.Null(catalog.Find(4242, Region.Cn));
        Assert.Null(catalog.FindByTerritory(9042, Region.Cn));
        Assert.Equal(DutyCatalog.UnknownDutyName, catalog.NameOf(4242, Region.Cn));

        Assert.Equal("Global Only", catalog.Find(4242, Region.Global)!.LocalizedName);
        Assert.Equal("国服副本", catalog.Find(11, Region.Cn)!.LocalizedName);
        Assert.Null(catalog.Find(11, Region.Global));
    }

    /// <summary>
    /// UNKNOWN is the absence of a region -- what a hand-entered run carries when the user
    /// never said which client they play -- so any installed mapping is still the best answer.
    /// </summary>
    [Fact]
    public void DutyCatalog_StillAnswersForTheUnknownRegion()
    {
        var catalog = DutyCatalog.Compose(new[]
        {
            DutyCatalog.ParseDocument("""
                {"schema_version":1,"region":"CN","data_version":"2026-09-04","duties":[
                  {"content_id":11,"territory_id":9011,"localized_name":"国服副本","enabled":true}
                ]}
                """),
        });

        Assert.Equal("国服副本", catalog.Find(11, Region.Unknown)!.LocalizedName);
        Assert.Equal("国服副本", catalog.FindByTerritory(9011, Region.Unknown)!.LocalizedName);
    }

    [Fact]
    public void ParseDutyCatalog_RejectsDuplicateContentIds()
    {
        const string json = """
            {"schema_version":1,"region":"CN","language":"zh-Hans","duties":[
              {"content_id":1,"localized_name":"A","duty_category":"迷宫挑战","enabled":true},
              {"content_id":1,"localized_name":"B","duty_category":"讨伐战","enabled":true}
            ]}
            """;

        Assert.Throws<InvalidDataException>(() => DutyCatalog.Parse(json));
    }

    [Fact]
    public void DefaultRouletteCatalog_MapsKnownAndUnknownRoulettesPerRegion()
    {
        var catalog = RouletteCatalog.Default;

        Assert.True(catalog.IsKnown(1, Region.Cn));
        Assert.Equal("练级迷宫", catalog.NameOf(1, Region.Cn));
        Assert.Equal("Leveling", catalog.NameOf(1, Region.Global));

        Assert.False(catalog.IsKnown(999, Region.Cn));
        Assert.Null(catalog.NameOf(999, Region.Cn));
        Assert.Equal(RouletteCatalog.UnknownRouletteName, catalog.DisplayName(999, Region.Cn));
        Assert.Equal(RouletteCatalog.UnknownRouletteName, catalog.DisplayName(null, Region.Cn));
    }

    /// <summary>
    /// Pins every CN roulette id from the CSV cited in
    /// protocol-profiles/cn/cn.2026.08.05.json's provenance, including the two that are easy
    /// to get wrong: 9 (mentor, never observed as a popup) and 17 (the second main-scenario
    /// roulette, easily confused with 3).
    /// </summary>
    [Fact]
    public void DefaultRouletteCatalog_KnownIdsCoverAllTenCnRoulettes()
    {
        var catalog = RouletteCatalog.Default;

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 8, 9, 15, 17 }, catalog.KnownIds(Region.Cn));
        Assert.Equal("指导者任务", catalog.NameOf(9, Region.Cn));
        // The names must be the ones the client sends, not the CSV's row order. Ids 5, 6, 8
        // and 17 are easy to transpose, and a transposition mislabels a player's own runs.
        Assert.Equal("大型任务", catalog.NameOf(17, Region.Cn));
        Assert.Equal("顶级迷宫", catalog.NameOf(5, Region.Cn));
        Assert.Equal("满级迷宫", catalog.NameOf(8, Region.Cn));
        Assert.Equal("讨伐歼灭战", catalog.NameOf(6, Region.Cn));
    }

    /// <summary>
    /// Mirrors <see cref="DutyCatalog_DoesNotFallBackToAnotherRegion"/> (review finding L-10):
    /// a roulette id that only the other region's table happens to know is not evidence about
    /// this region, so the lookup must stay inside the requested region.
    /// </summary>
    [Fact]
    public void RouletteCatalog_DoesNotFallBackToAnotherRegion()
    {
        var catalog = RouletteCatalog.Compose(new[]
        {
            RouletteCatalog.ParseDocument("""
                {"schema_version":1,"region":"GLOBAL","roulettes":[
                  {"roulette_id":42,"localized_name":"Global Only","enabled":true}
                ]}
                """),
            RouletteCatalog.ParseDocument("""
                {"schema_version":1,"region":"CN","roulettes":[
                  {"roulette_id":1,"localized_name":"练级迷宫","enabled":true}
                ]}
                """),
        });

        Assert.Null(catalog.NameOf(42, Region.Cn));
        Assert.Equal("Global Only", catalog.NameOf(42, Region.Global));
        Assert.Null(catalog.NameOf(1, Region.Global));
    }

    /// <summary>
    /// UNKNOWN is the absence of a region, not one of them, so any installed mapping is still
    /// the best available answer -- same rule as <see cref="DutyCatalog_StillAnswersForTheUnknownRegion"/>.
    /// </summary>
    [Fact]
    public void RouletteCatalog_StillAnswersForTheUnknownRegion()
    {
        var catalog = RouletteCatalog.Compose(new[]
        {
            RouletteCatalog.ParseDocument("""
                {"schema_version":1,"region":"CN","roulettes":[
                  {"roulette_id":1,"localized_name":"练级迷宫","enabled":true}
                ]}
                """),
        });

        Assert.Equal("练级迷宫", catalog.NameOf(1, Region.Unknown));
    }

    [Fact]
    public void ParseRouletteCatalog_RejectsDuplicateIds()
    {
        const string json = """
            {"schema_version":1,"region":"CN","roulettes":[
              {"roulette_id":1,"localized_name":"A","enabled":true},
              {"roulette_id":1,"localized_name":"B","enabled":true}
            ]}
            """;

        Assert.Throws<InvalidDataException>(() => RouletteCatalog.Parse(json));
    }

    [Fact]
    public void ParseRouletteCatalog_RejectsMissingName()
    {
        const string json = """
            {"schema_version":1,"region":"CN","roulettes":[
              {"roulette_id":1,"localized_name":"","enabled":true}
            ]}
            """;

        Assert.Throws<InvalidDataException>(() => RouletteCatalog.Parse(json));
    }

    [Fact]
    public void ParseRouletteCatalog_RejectsUnsupportedSchemaOrRegion()
    {
        const string wrongSchema = """{"schema_version":2,"region":"CN","roulettes":[]}""";
        const string wrongRegion = """{"schema_version":1,"region":"NOWHERE","roulettes":[]}""";

        Assert.Throws<InvalidDataException>(() => RouletteCatalog.Parse(wrongSchema));
        Assert.Throws<InvalidDataException>(() => RouletteCatalog.Parse(wrongRegion));
    }

    [Fact]
    public void ParseRouletteCatalog_SkipsDisabledRows()
    {
        const string json = """
            {"schema_version":1,"region":"CN","roulettes":[
              {"roulette_id":1,"localized_name":"停用项","enabled":false}
            ]}
            """;

        var catalog = RouletteCatalog.Parse(json);

        Assert.False(catalog.IsKnown(1, Region.Cn));
        Assert.Empty(catalog.KnownIds(Region.Cn));
    }
}
