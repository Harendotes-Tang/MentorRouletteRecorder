using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Payload validation: the guard that keeps a malformed or over-large request from ever
/// reaching storage.
/// </summary>
public sealed class PayloadValidationTests
{
    private static PayloadReader Reader(string json) =>
        new(JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void PageSize_AboveTwoHundred_IsRefused()
    {
        var error = Assert.Throws<CollectorException>(() =>
            RequestParsers.Paging(Reader("{\"page\":1,\"page_size\":201}")));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal("payload.page_size", error.Field);
    }

    [Fact]
    public void PageSize_AtTheCap_IsAccepted() =>
        Assert.Equal(200, RequestParsers.Paging(Reader("{\"page_size\":200}")).PageSize);

    [Fact]
    public void Paging_DefaultsToPageOneAndFifty()
    {
        var paging = RequestParsers.Paging(Reader("{}"));

        Assert.Equal(1, paging.Page);
        Assert.Equal(Paging.DefaultPageSize, paging.PageSize);
    }

    [Fact]
    public void Page_BelowOne_IsRefused()
    {
        var error = Assert.Throws<CollectorException>(() =>
            RequestParsers.Paging(Reader("{\"page\":0}")));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
    }

    [Fact]
    public void UnknownProperty_IsRefusedRatherThanIgnored()
    {
        var error = Assert.Throws<CollectorException>(() =>
            Reader("{\"page\":1,\"surprise\":true}").RejectUnknown("page"));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal("payload.surprise", error.Field);
    }

    [Fact]
    public void WrongJsonType_IsRefusedRatherThanCoerced()
    {
        var error = Assert.Throws<CollectorException>(() => Reader("{\"page\":\"1\"}").Int("page"));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
    }

    [Theory]
    [InlineData("2026-09-04T11:22:33Z")]
    [InlineData("2026-09-04 11:22:33.456Z")]
    [InlineData("2026-09-04T11:22:33.456+08:00")]
    [InlineData("not a timestamp")]
    public void NonUtcTimestamps_AreRefused(string value)
    {
        var error = Assert.Throws<CollectorException>(() =>
            Reader($"{{\"at\":\"{value}\"}}").Timestamp("at"));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
    }

    [Fact]
    public void UtcTimestampWithMilliseconds_IsAccepted() =>
        Assert.Equal(
            new DateTimeOffset(2026, 9, 4, 11, 22, 33, 456, TimeSpan.Zero),
            Reader("{\"at\":\"2026-09-04T11:22:33.456Z\"}").Timestamp("at"));

    [Fact]
    public void UnknownEnumToken_IsRefused()
    {
        var error = Assert.Throws<CollectorException>(() =>
            Reader("{\"result\":\"ALMOST_COMPLETED\"}").Enum<RunResult>("result"));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
    }

    [Fact]
    public void Filter_ReadsEveryDeclaredField()
    {
        var filter = RequestParsers.Filter(Reader(
            "{\"from_utc\":\"2026-09-01T00:00:00.000Z\",\"to_utc\":\"2026-09-30T00:00:00.000Z\"," +
            "\"date_field\":\"ended_at_utc\",\"content_id\":[900001,900002],\"job_id\":[19]," +
            "\"duty_category\":[\"迷宫挑战\"],\"result\":[\"COMPLETED\"],\"source\":[\"MANUAL\"]," +
            "\"corrected_only\":true,\"include_deleted\":true,\"text\":\"样例\"}"))!;

        Assert.Equal(RunDateField.EndedAtUtc, filter.DateField);
        Assert.Equal(new[] { 900001, 900002 }, filter.ContentIds);
        Assert.Equal(new[] { 19 }, filter.JobIds);
        Assert.Equal(new[] { RunResult.Completed }, filter.Results);
        Assert.Equal(new[] { RunSource.Manual }, filter.Sources);
        Assert.True(filter.CorrectedOnly);
        Assert.True(filter.IncludeDeleted);
        Assert.Equal("样例", filter.Text);
    }

    [Fact]
    public void Filter_WithReversedRange_IsRefused()
    {
        var error = Assert.Throws<CollectorException>(() => RequestParsers.Filter(Reader(
            "{\"from_utc\":\"2026-09-30T00:00:00.000Z\",\"to_utc\":\"2026-09-01T00:00:00.000Z\"}")));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
    }

    [Fact]
    public void Filter_WithTooManyContentIds_IsRefused()
    {
        var ids = string.Join(',', Enumerable.Range(1, 501));

        var error = Assert.Throws<CollectorException>(() =>
            RequestParsers.Filter(Reader($"{{\"content_id\":[{ids}]}}")));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
    }

    [Fact]
    public void Sort_WithAnUndeclaredField_IsRefused()
    {
        var error = Assert.Throws<CollectorException>(() =>
            RequestParsers.Sort(Reader("{\"field\":\"run_id\"}")));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
    }

    [Fact]
    public void Changes_RecordExplicitNullsAsSpecified()
    {
        var changes = RequestParsers.Changes(Reader("{\"content_id\":null,\"duty_name\":\"甲\"}"));

        Assert.True(changes.Has(RunFields.ContentId));
        Assert.Null(changes.ContentId);
        Assert.True(changes.Has(RunFields.DutyName));
        Assert.False(changes.Has(RunFields.JobId));
    }

    [Fact]
    public void Changes_WithAnUncorrectableField_AreRefused()
    {
        var error = Assert.Throws<CollectorException>(() =>
            RequestParsers.Changes(Reader("{\"source\":\"MANUAL\"}")));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
    }

    [Fact]
    public void Changes_WithANegativeDuration_AreRefusedWithTheDurationCode()
    {
        var error = Assert.Throws<CollectorException>(() =>
            RequestParsers.Changes(Reader("{\"duration_ms\":-5}")));

        Assert.Equal(ErrorCodes.NegativeDuration, error.Code);
    }

    [Fact]
    public void CreateManualRun_WithoutAReason_IsRefusedBeforeStorage()
    {
        var error = Assert.Throws<CollectorException>(() => RequestParsers.CreateManualRun(
            Guid.NewGuid().ToString("D"), Reader("{\"result\":\"COMPLETED\"}")));

        Assert.Equal(ErrorCodes.ReasonRequired, error.Code);
    }

    [Fact]
    public void CorrectRun_WithANonUuidRunId_IsRefused()
    {
        var error = Assert.Throws<CollectorException>(() => RequestParsers.CorrectRun(
            Guid.NewGuid().ToString("D"),
            Reader("{\"run_id\":\"abc\",\"expected_revision\":1,\"reason\":\"x\",\"changes\":{}}")));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
    }

    [Fact]
    public void UpdateBaseline_WithAGoalBelowOne_IsRefused()
    {
        var error = Assert.Throws<CollectorException>(() => RequestParsers.UpdateBaseline(
            Guid.NewGuid().ToString("D"),
            Reader("{\"goal_count\":0,\"baseline_completed_count\":1," +
                "\"baseline_effective_at\":\"2026-01-01T00:00:00.000Z\",\"reason\":\"x\"}")));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
    }

    [Fact]
    public void EmptyOnlyMessages_RejectAnyArgument()
    {
        var error = Assert.Throws<CollectorException>(() => Reader("{\"x\":1}").RequireEmpty());

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
    }

    [Fact]
    public void StatsSort_RejectsAnUndeclaredColumn()
    {
        var error = Assert.Throws<CollectorException>(() =>
            StatsSorting.Read(Reader("{\"field\":\"; DROP TABLE mentor_runs\"}")));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
    }

    [Fact]
    public void StatsSort_DefaultsToMostAttemptsFirst()
    {
        var sort = StatsSorting.Read(null);

        Assert.Equal("attempt_count", sort.Field);
        Assert.Equal(SortDirection.Desc, sort.Direction);
    }
}
