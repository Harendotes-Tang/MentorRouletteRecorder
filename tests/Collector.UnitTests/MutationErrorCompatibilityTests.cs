using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class MutationErrorCompatibilityTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 4, 1, 0, 0, TimeSpan.Zero);

    private sealed record ExpectedError(
        string Code,
        string Message,
        string Field,
        string? Result = null);

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Database = new TestDatabase();
            Settings = new SettingsRepository(Database.Database, Database.Clock);
            Settings.EnsureDefaults();
            Service = new RunMutationService(Database.Database, Settings, Database.Clock);
            Runs = new RunRepository(Database.Database);
            Revisions = new RunRevisionRepository(Database.Database);
        }

        public TestDatabase Database { get; }

        public SettingsRepository Settings { get; }

        public RunMutationService Service { get; }

        public RunRepository Runs { get; }

        public RunRevisionRepository Revisions { get; }

        public int RunCount => CountRows("mentor_runs");

        public int RevisionCount => CountRows("run_revisions");

        public int IdempotencyCount => CountRows("ipc_idempotency");

        public void Dispose() => Database.Dispose();

        private int CountRows(string table)
        {
            var sql = table switch
            {
                "mentor_runs" => "SELECT COUNT(*) FROM mentor_runs;",
                "run_revisions" => "SELECT COUNT(*) FROM run_revisions;",
                "ipc_idempotency" => "SELECT COUNT(*) FROM ipc_idempotency;",
                _ => throw new ArgumentOutOfRangeException(nameof(table), table, null),
            };

            return Database.Database.Read(_ =>
            {
                using var command = Database.Database.CreateCommand();
                command.CommandText = sql;
                return Convert.ToInt32(
                    command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            });
        }
    }

    [Fact]
    public void DirectServiceReasonFailure_RetainsCollectorExceptionEnvelopeAndWritesNothing()
    {
        using var fixture = new Fixture();

        AssertRejected(
            fixture,
            () => fixture.Service.CreateManualRun(
                ValidCreate() with
                {
                    Reason = "   ",
                    Result = RunResult.CancelledBeforeEntry,
                    MatchedAtUtc = null,
                    EnteredAtUtc = null,
                    EndedAtUtc = null,
                }),
            new ExpectedError(
                ErrorCodes.ReasonRequired,
                "该操作必须填写 1 到 500 字的修改理由。",
                "reason"));
    }

    [Fact]
    public void DirectServiceMatchedAfterEntered_RetainsCompleteRunFailureEnvelopeAndWritesNothing()
    {
        using var fixture = new Fixture();

        AssertRejected(
            fixture,
            () => fixture.Service.CreateManualRun(
                ValidCreate() with { MatchedAtUtc = Start.AddMinutes(2) }),
            new ExpectedError(
                ErrorCodes.TimeOrder,
                "匹配时间不能晚于进入副本的时间。",
                "matched_at_utc",
                "COMPLETED"));
    }

    [Fact]
    public void DirectServiceEnteredAfterEnded_RetainsCompleteRunFailureEnvelopeAndWritesNothing()
    {
        using var fixture = new Fixture();

        AssertRejected(
            fixture,
            () => fixture.Service.CreateManualRun(
                ValidCreate() with
                {
                    EnteredAtUtc = Start.AddMinutes(2),
                    EndedAtUtc = Start.AddMinutes(1),
                }),
            new ExpectedError(
                ErrorCodes.TimeOrder,
                "进入副本的时间不能晚于结束时间。",
                "entered_at_utc",
                "COMPLETED"));
    }

    [Fact]
    public void DirectServiceMissingEntry_RetainsCompleteRunFailureEnvelopeAndWritesNothing()
    {
        using var fixture = new Fixture();

        AssertRejected(
            fixture,
            () => fixture.Service.CreateManualRun(
                ValidCreate() with
                {
                    Result = RunResult.Interrupted,
                    EnteredAtUtc = null,
                    EndedAtUtc = null,
                    DurationMs = null,
                }),
            new ExpectedError(
                ErrorCodes.BadRequest,
                "只有「未进入副本即取消」可以没有进入时间，其余结果都必须填写进入时间。",
                "entered_at_utc",
                "INTERRUPTED"));
    }

    [Fact]
    public void DirectServiceMissingEnd_RetainsCompleteRunFailureEnvelopeAndWritesNothing()
    {
        using var fixture = new Fixture();

        AssertRejected(
            fixture,
            () => fixture.Service.CreateManualRun(
                ValidCreate() with { EndedAtUtc = null, DurationMs = null }),
            new ExpectedError(
                ErrorCodes.BadRequest,
                "判定为「已完成」的记录必须填写结束时间。",
                "ended_at_utc",
                "COMPLETED"));
    }

    [Fact]
    public void DirectServiceNegativeDuration_RetainsCompleteRunFailureEnvelopeAndWritesNothing()
    {
        using var fixture = new Fixture();

        AssertRejected(
            fixture,
            () => fixture.Service.CreateManualRun(
                ValidCreate() with { DurationMs = -1 }),
            new ExpectedError(
                ErrorCodes.NegativeDuration,
                "时长不能为负数。",
                "duration_ms",
                "COMPLETED"));
    }

    [Fact]
    public void DirectServiceNoChanges_RetainsEnvelopeAndDoesNotAppendARevision()
    {
        using var fixture = new Fixture();
        var created = fixture.Service.CreateManualRun(ValidCreate());
        var before = fixture.Runs.Get(created.RunId)!;

        AssertRejected(
            fixture,
            () => fixture.Service.CorrectRun(
                new CorrectRunCommand(
                    Guid.NewGuid().ToString("D"),
                    created.RunId,
                    before.Revision,
                    "确认结果没有实际变化",
                    new RunChangeSet
                    {
                        Specified = new HashSet<string>(StringComparer.Ordinal)
                        {
                            RunFields.Result,
                        },
                        Result = before.Result,
                    })),
            new ExpectedError(
                ErrorCodes.NoChanges,
                "未做任何修改，不会写入修订记录。",
                "changes"),
            messageType: "CorrectRun");

        var after = fixture.Runs.Get(created.RunId)!;
        Assert.Equal(before, after);
        var revisions = fixture.Revisions.ListForRun(created.RunId, page: 1, pageSize: 200);
        Assert.Equal(1, revisions.Total);
        Assert.Single(revisions.Items);
    }

    private static CreateManualRunCommand ValidCreate() => new()
    {
        RequestId = Guid.NewGuid().ToString("D"),
        Reason = "验证错误边界",
        MatchedAtUtc = Start,
        EnteredAtUtc = Start.AddMinutes(1),
        EndedAtUtc = Start.AddMinutes(31),
        Result = RunResult.Completed,
    };

    private static CollectorException AssertRejected(
        Fixture fixture,
        Action operation,
        ExpectedError expected,
        string messageType = "CreateManualRun")
    {
        var runsBefore = fixture.RunCount;
        var revisionsBefore = fixture.RevisionCount;
        var idempotencyBefore = fixture.IdempotencyCount;

        var failure = Assert.Throws<CollectorException>(operation);

        Assert.Equal(expected.Code, failure.Code);
        Assert.Equal(expected.Message, failure.Message);
        Assert.Equal(expected.Field, failure.Field);
        Assert.False(failure.Retryable);

        if (expected.Result is null)
        {
            Assert.Null(failure.Details);
        }
        else
        {
            var details = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(failure.Details);
            Assert.Equal(2, details.Count);
            var runId = Assert.IsType<string>(details["run_id"]);
            Assert.True(Guid.TryParseExact(runId, "D", out _));
            Assert.Equal(expected.Result, details["result"]);
        }

        AssertEnvelope(failure, expected, messageType);
        Assert.Equal(runsBefore, fixture.RunCount);
        Assert.Equal(revisionsBefore, fixture.RevisionCount);
        Assert.Equal(idempotencyBefore, fixture.IdempotencyCount);
        return failure;
    }

    private static void AssertEnvelope(
        CollectorException failure,
        ExpectedError expected,
        string messageType)
    {
        var requestId = "00000000-0000-4000-8000-000000000102";
        var envelope = IpcEnvelope.Failure(requestId, messageType, failure);
        var error = envelope["error"]!.AsObject();
        var payload = envelope["payload"]!.AsObject();

        Assert.Equal(IpcEnvelope.ProtocolVersion, envelope["protocol_version"]!.GetValue<int>());
        Assert.Equal(requestId, envelope["request_id"]!.GetValue<string>());
        Assert.Equal(messageType, envelope["message_type"]!.GetValue<string>());
        Assert.False(envelope["ok"]!.GetValue<bool>());
        Assert.Equal(expected.Code, error["code"]!.GetValue<string>());
        Assert.Equal(expected.Message, error["message"]!.GetValue<string>());
        Assert.Equal(expected.Field, error["field"]!.GetValue<string>());
        Assert.False(error["retryable"]!.GetValue<bool>());
        Assert.Equal(expected.Result is not null, error.ContainsKey("details"));

        if (expected.Result is not null)
        {
            var details = error["details"]!.AsObject();
            Assert.Equal(failure.Details!["run_id"], details["run_id"]!.GetValue<string>());
            Assert.Equal(expected.Result, details["result"]!.GetValue<string>());
        }

        Assert.True(JsonNode.DeepEquals(error, payload));
    }
}

