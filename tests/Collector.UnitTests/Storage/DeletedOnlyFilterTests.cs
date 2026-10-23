using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.UnitTests;

public sealed class DeletedOnlyFilterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParserReadsOptionalDeletedOnly(bool only)
    {
        var parsed = RequestParsers.Filter(new PayloadReader(new JsonObject { ["deleted_only"] = only }))!;
        Assert.Equal(only, parsed.DeletedOnly);
        Assert.False(parsed.IncludeDeleted);
        Assert.False(RequestParsers.Filter(new PayloadReader(new JsonObject()))!.DeletedOnly);
    }

    [Fact]
    public void ParserRefusesNonBooleanDeletedOnly() => Assert.Throws<CollectorException>(() =>
        RequestParsers.Filter(new PayloadReader(new JsonObject { ["deleted_only"] = "true" })));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletedOnlyOverridesIncludeDeletedButStatisticsStillExcludeDeleted(bool include)
    {
        var filter = new RunFilter { DeletedOnly = true, IncludeDeleted = include };
        var history = RunFilterSql.Build(filter, false).Where;
        Assert.Contains("soft_deleted = 1", history);
        Assert.DoesNotContain("soft_deleted = 0", history);
        var statistics = RunFilterSql.Build(filter, true).Where;
        Assert.Contains("soft_deleted = 0", statistics);
        Assert.DoesNotContain("soft_deleted = 1", statistics);
    }
}
