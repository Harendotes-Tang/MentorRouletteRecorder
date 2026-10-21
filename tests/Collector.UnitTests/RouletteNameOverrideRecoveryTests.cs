using System.Text;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Reference;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>损坏的可选显示映射回退，并允许下一次有效改名修复文件。</summary>
public sealed class RouletteNameOverrideRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MentorRecorder.RouletteNames." + Guid.NewGuid().ToString("N"));
    public RouletteNameOverrideRecoveryTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("\"text\"")]
    [InlineData("{\"names\":null}")]
    [InlineData("{\"names\":[{\"region\":\"CN\",\"roulette_id\":1,\"name\":\"\\uD800\"}]}")]
    public void DamagedMappingFallsBackWithoutThrowing(string json)
    {
        var path = Write(json);
        Assert.Empty(RouletteNameOverrides.Load(path));
        var known = RouletteCatalog.Default.KnownIds(Region.Cn).First();
        Assert.Equal(RouletteCatalog.Default.Find(known, Region.Cn), RouletteCatalog.WithLocalNames(path).Find(known, Region.Cn));
    }

    [Fact]
    public void NonObjectRowsAreSkippedAndValidOverridesStillWin()
    {
        var known = RouletteCatalog.Default.KnownIds(Region.Cn).First();
        var path = Write("{\"names\":[null,[],1,\"text\",{}," +
            "{\"region\":\"CN\",\"roulette_id\":" + known + ",\"name\":\"本机有效名称\"}]}");

        var loaded = RouletteNameOverrides.Load(path);
        Assert.Single(loaded);
        Assert.Equal("本机有效名称", loaded[(Region.Cn, known)]);
        Assert.Equal("本机有效名称", RouletteCatalog.WithLocalNames(path).Find(known, Region.Cn)!.LocalizedName);
    }

    [Fact]
    public void ANewValidCorrectionReplacesAnUnreadableOptionalMapping()
    {
        var path = Write("[]");
        var now = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        Assert.True(RouletteNameOverrides.Record(path, Region.Cn, 42, "恢复后的本机名称", now));
        Assert.Equal("恢复后的本机名称", RouletteNameOverrides.Load(path)[(Region.Cn, 42)]);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
    }

    /// <summary>
    /// Audit 2026-10-20 H2-6: an entry this build cannot read (a region it does not know, an id that is not a
    /// whole number, a name that is not valid text) is skipped on its own; the other corrections still show.
    /// </summary>
    [Fact]
    public void OneUnreadableEntryDoesNotHideTheOtherCorrections()
    {
        var path = Write("{\"names\":[{\"region\":\"CN\",\"roulette_id\":1,\"name\":\"本机名称甲\"}," +
            "{\"region\":\"KR\",\"roulette_id\":2,\"name\":\"新版本的区域\"}," +
            "{\"region\":\"CN\",\"roulette_id\":1.5,\"name\":\"不是整数\"}," +
            "{\"region\":\"CN\",\"roulette_id\":4,\"name\":\"\\uD800\"}," +
            "{\"region\":\"CN\",\"roulette_id\":5,\"name\":\"本机名称乙\"}]}");
        var loaded = RouletteNameOverrides.Load(path);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("本机名称甲", loaded[(Region.Cn, 1)]);
        Assert.Equal("本机名称乙", loaded[(Region.Cn, 5)]);
    }

    /// <summary>
    /// Writing a new correction rewrites the whole file from what was read. When part of the file could not be
    /// read, that rewrite would erase it, so the file is left exactly as it is and the new name is not kept.
    /// </summary>
    [Theory]
    [InlineData("{\"names\":[{\"region\":\"CN\",\"roulette_id\":1,\"name\":\"本机名称甲\"},{\"region\":\"KR\",\"roulette_id\":2,\"name\":\"新版本的区域\"}]}")]
    [InlineData("{\"names\":[{\"region\":\"CN\",\"roulette_id\":1,\"name\":\"本机名称甲\"},{\"region\":\"CN\",\"roulette_id\":2}]}")]
    [InlineData("{\"names\":[{\"region\":\"CN\",\"roulette_id\":1,\"name\":\"本机名称甲\"}")]
    public void ACorrectionIsNeverWrittenOverAFileThatCouldNotBeFullyRead(string json)
    {
        var path = Write(json);
        var before = File.ReadAllBytes(path);
        Assert.False(RouletteNameOverrides.Record(path, Region.Cn, 42, "新的本机名称", new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
    }

    [Fact]
    public void ACorrectionKeepsEveryReadableOneBesideIt()
    {
        var path = Write("{\"names\":[null,{\"region\":\"CN\",\"roulette_id\":1,\"name\":\"本机名称甲\"}]}");
        Assert.True(RouletteNameOverrides.Record(path, Region.Cn, 42, "新的本机名称", new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero)));
        var loaded = RouletteNameOverrides.Load(path);
        Assert.Equal("本机名称甲", loaded[(Region.Cn, 1)]);
        Assert.Equal("新的本机名称", loaded[(Region.Cn, 42)]);
    }

    private string Write(string json)
    {
        var path = Path.Combine(_directory, "roulette-names.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
