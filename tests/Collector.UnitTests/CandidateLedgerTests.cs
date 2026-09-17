using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CandidateLedgerTests : IDisposable
{
    private readonly TestDatabase _fixture = new();
    private readonly CandidateObservationRepository _ledger;
    private readonly SettingsRepository _settings;
    private readonly string _session = Id();

    public CandidateLedgerTests()
    {
        _ledger = new CandidateObservationRepository(_fixture.Database, _fixture.Clock);
        _settings = new SettingsRepository(_fixture.Database, _fixture.Clock);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    public void OnlyAnExplicitBooleanTruePermitsNewObservations(string? value)
    {
        if (value is not null) _settings.SetSetting(CaptureSettingsStore.CandidateValidationSetting, value);

        Assert.False(_ledger.Add(Observation()));
        Assert.Equal(0, _ledger.Count());
        Assert.Equal(0, Count("candidate_reviews"));
    }

    [Fact]
    public void AddRoundTripsMetadataAndTurningOffStopsWritingImmediately()
    {
        Enable();
        var observation = Observation();
        Assert.True(_ledger.Add(observation));
        Assert.False(_ledger.Add(observation));
        Assert.Equal(observation, _ledger.Get(observation.ObservationId)!.Observation);
        Assert.Null(_ledger.Get(observation.ObservationId)!.ReviewVerdict);

        _settings.SetSetting(CaptureSettingsStore.CandidateValidationSetting, "false");
        Assert.False(_ledger.Add(Observation()));
        Assert.Single(_ledger.Query().Items);
        Assert.Equal(0, Count("mentor_runs"));
        Assert.Equal(0, Count("run_events"));
        Assert.Equal(0, Count("run_revisions"));
    }

    [Fact]
    public void ZoneAnchorKeepsAllFourFirstAndLastFields()
    {
        Enable();
        var now = _fixture.Clock.UtcNow;
        var anchor = new CandidateObservation(Id(), _session, "candidate-test", "ZONE_LOAD", "zone_load",
            "NONE", null, null, null, "0123456789ab", now, 3000, now.AddMilliseconds(-2500), now, 500, 3000);

        Assert.True(_ledger.Add(anchor));
        Assert.Equal(anchor, Assert.Single(_ledger.ReadEvidence().Observations).Observation);
    }

    [Fact]
    public void QueryFiltersInclusiveTimestampsAndSessionsBeforeStablePaging()
    {
        Enable();
        var now = _fixture.Clock.UtcNow;
        var first = Observation(now.AddSeconds(-3));
        var middle = Observation(now.AddSeconds(-2));
        var last = Observation(now.AddSeconds(-1));
        _ledger.Add(first);
        _ledger.Add(middle);
        _ledger.Add(last);
        _ledger.Add(Observation() with { CaptureSessionId = Id() });

        var page = _ledger.Query(_session, first.ObservedAtUtc, last.ObservedAtUtc, page: 2, pageSize: 1);
        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.PageNumber);
        Assert.Equal(1, page.PageSize);
        Assert.Equal(middle, Assert.Single(page.Items).Observation);
        Assert.Equal(3, _ledger.Count(_session));
        Assert.Equal(4, _ledger.Count());
        Assert.Empty(_ledger.Query(page: int.MaxValue, pageSize: 200).Items);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 201)]
    public void InvalidPagingIsRejected(int page, int size)
    {
        var error = Assert.Throws<CollectorException>(() => _ledger.Query(page: page, pageSize: size));
        Assert.Equal(ErrorCodes.BadRequest, error.Code);
    }

    [Fact]
    public void ReversedRangeAndInvalidObservationIdAreRejected()
    {
        Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<CollectorException>(() =>
            _ledger.Query(fromUtc: _fixture.Clock.UtcNow, toUtc: _fixture.Clock.UtcNow.AddSeconds(-1))).Code);
        Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<CollectorException>(() => _ledger.Get("invalid")).Code);
    }

    [Fact]
    public void CapacityRemovesTheOldestObservationAndItsReview()
    {
        Enable();
        Seed(CandidateObservationRepository.MaxObservations);
        var oldestId = SeedId(1);
        _ledger.Review(Id(), oldestId, "CORRECT", "oldest review");
        var newest = Observation();

        Assert.True(_ledger.Add(newest));
        Assert.Equal(CandidateObservationRepository.MaxObservations, _ledger.Count());
        Assert.Null(_ledger.Get(oldestId));
        Assert.NotNull(_ledger.Get(SeedId(2)));
        Assert.NotNull(_ledger.Get(newest.ObservationId));
        Assert.Equal(0, Count("candidate_reviews"));
    }

    [Theory]
    [InlineData("query")]
    [InlineData("count")]
    [InlineData("get")]
    [InlineData("export")]
    public void ReadsEnforceThirtyDayRetentionAndCascadeReviewHistory(string operation)
    {
        Enable();
        var observation = Observation();
        Assert.True(_ledger.Add(observation));
        _ledger.Review(Id(), observation.ObservationId, "UNSURE", "retention test");
        _fixture.Clock.UtcNow = _fixture.Clock.UtcNow.AddDays(30);
        Assert.NotNull(_ledger.Get(observation.ObservationId)); // Exactly 30 days remains within the window.
        _fixture.Clock.UtcNow = _fixture.Clock.UtcNow.AddMilliseconds(1);

        switch (operation)
        {
            case "query": Assert.Empty(_ledger.Query().Items); break;
            case "count": Assert.Equal(0, _ledger.Count()); break;
            case "get": Assert.Null(_ledger.Get(observation.ObservationId)); break;
            case "export": Assert.Empty(_ledger.ReadEvidence().Observations); break;
        }
        Assert.Equal(0, Count("candidate_observations"));
        Assert.Equal(0, Count("candidate_reviews"));
    }

    [Fact]
    public void AlreadyExpiredObservationIsNeverInserted()
    {
        Enable();
        Assert.False(_ledger.Add(Observation(_fixture.Clock.UtcNow.AddDays(-31))));
        Assert.Equal(0, _ledger.Count());
    }

    [Fact]
    public void ReviewAppendsHistoryAndReplaysItsOriginalResultAcrossNewReviewAndRestart()
    {
        Enable();
        var observation = Observation();
        _ledger.Add(observation);
        var request = Id();
        var first = _ledger.Review(request, observation.ObservationId, "CORRECT", "  confirmed  ");
        _fixture.Clock.UtcNow = _fixture.Clock.UtcNow.AddSeconds(1);
        var second = _ledger.Review(Id(), observation.ObservationId, "WRONG", "corrected judgement");
        Assert.Equal(first, _ledger.Review(request, observation.ObservationId, "CORRECT", "confirmed"));
        Assert.Equal("WRONG", _ledger.Get(observation.ObservationId)!.ReviewVerdict);
        Assert.Equal(second.ReviewedAtUtc, _ledger.Get(observation.ObservationId)!.ReviewedAtUtc);

        _fixture.Reopen();
        var reopened = new CandidateObservationRepository(_fixture.Database, _fixture.Clock);
        Assert.Equal(first, reopened.Review(request, observation.ObservationId, "CORRECT", "confirmed"));
        var evidence = reopened.ReadEvidence();
        Assert.Equal(2, evidence.Reviews.Count);
        Assert.Equal(new[] { "CORRECT", "WRONG" }, evidence.Reviews.Select(review => review.Verdict));
        Assert.Equal("confirmed", evidence.Reviews[0].Note);
        Assert.Equal(0, Count("run_revisions"));
        using var command = _fixture.Database.CreateCommand();
        command.CommandText = "SELECT response_json FROM ipc_idempotency WHERE request_id = $id;";
        command.Parameters.AddWithValue("$id", request);
        Assert.DoesNotContain("confirmed", (string)command.ExecuteScalar()!);
    }

    [Fact]
    public void RequestIdReuseWithAnotherVerdictOrAnotherMessageIsRefused()
    {
        Enable();
        var observation = Observation();
        _ledger.Add(observation);
        var request = Id();
        _ledger.Review(request, observation.ObservationId, "CORRECT");
        Assert.Equal(ErrorCodes.IdempotencyConflict, Assert.Throws<CollectorException>(() =>
            _ledger.Review(request, observation.ObservationId, "WRONG")).Code);

        var foreignRequest = Id();
        var idempotency = new IdempotencyRepository(_fixture.Database, _fixture.Clock);
        _fixture.Database.RunInTransaction(tx => idempotency.Store(foreignRequest, "CreateManualRun",
            MutationSnapshotCodec.Serialize(new MutationSnapshot("foreign", null, 1, Id(), null, null)), tx));
        Assert.Equal(ErrorCodes.IdempotencyConflict, Assert.Throws<CollectorException>(() =>
            _ledger.Review(foreignRequest, observation.ObservationId, "UNSURE")).Code);
        Assert.Single(_ledger.ReadEvidence().Reviews);
    }

    [Fact]
    public void ReviewValidatesVerdictNoteAndNotFoundWithoutWritingHistory()
    {
        Enable();
        var observation = Observation();
        _ledger.Add(observation);
        Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<CollectorException>(() =>
            _ledger.Review(Id(), observation.ObservationId, "VERIFIED")).Code);
        Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<CollectorException>(() =>
            _ledger.Review(Id(), observation.ObservationId, "CORRECT", new string('x', 2001))).Code);
        Assert.Equal(ErrorCodes.CandidateObservationNotFound, Assert.Throws<CollectorException>(() =>
            _ledger.Review(Id(), Id(), "CORRECT")).Code);
        Assert.Empty(_ledger.ReadEvidence().Reviews);
        Assert.Equal(0, Count("ipc_idempotency"));
    }

    [Fact]
    public void HistoryCannotBeUpdatedAndFailedAppendRollsBackTheSummary()
    {
        Enable();
        var observation = Observation();
        _ledger.Add(observation);
        _ledger.Review(Id(), observation.ObservationId, "CORRECT");
        using var command = _fixture.Database.CreateCommand();
        command.CommandText = "UPDATE candidate_reviews SET verdict = 'WRONG';";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());

        command.CommandText = "CREATE TRIGGER deny_test_review BEFORE INSERT ON candidate_reviews " +
            "BEGIN SELECT RAISE(ABORT, 'test append failure'); END;";
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => _ledger.Review(Id(), observation.ObservationId, "WRONG"));
        Assert.Equal("CORRECT", _ledger.Get(observation.ObservationId)!.ReviewVerdict);
        Assert.Single(_ledger.ReadEvidence().Reviews);
        Assert.Equal(1, Count("ipc_idempotency"));
    }

    [Fact]
    public void VisitEvidenceStreamsMatchingCountsInOrderAndRejectsEscapedEnumeration()
    {
        Enable();
        var expired = Observation(_fixture.Clock.UtcNow.AddDays(-30));
        var first = Observation(_fixture.Clock.UtcNow.AddSeconds(-1));
        var last = Observation();
        foreach (var observation in new[] { expired, first, last }) _ledger.Add(observation);
        _ledger.Review(Id(), expired.ObservationId, "UNSURE");
        _ledger.Review(Id(), first.ObservationId, "CORRECT");
        _ledger.Review(Id(), last.ObservationId, "WRONG");
        _fixture.Clock.UtcNow = _fixture.Clock.UtcNow.AddMilliseconds(1);
        IEnumerable<CandidateObservationEntry>? escaped = null;

        _ledger.VisitEvidence((observationCount, reviewCount, observations, reviews) =>
        {
            Assert.Equal(2, observationCount);
            Assert.Equal(2, reviewCount);
            Assert.Equal(new[] { last, first }, observations.Select(entry => entry.Observation));
            Assert.Equal(new[] { first.ObservationId, last.ObservationId }, reviews.Select(entry => entry.ObservationId));
            escaped = observations;
        });

        Assert.Equal(2, Count("candidate_observations"));
        Assert.Equal(2, Count("candidate_reviews"));
        Assert.Throws<InvalidOperationException>(() => escaped!.ToArray());
    }

    [Fact]
    public void VisitEvidenceCallbackFailureRollsBackPruningAndReleasesItsTransaction()
    {
        Enable();
        var observation = Observation(_fixture.Clock.UtcNow.AddDays(-30));
        _ledger.Add(observation);
        _ledger.Review(Id(), observation.ObservationId, "CORRECT");
        _fixture.Clock.UtcNow = _fixture.Clock.UtcNow.AddMilliseconds(1);

        Assert.Throws<IOException>(() => _ledger.VisitEvidence((observationCount, reviewCount, observations, reviews) =>
        {
            Assert.Equal(0, observationCount);
            Assert.Equal(0, reviewCount);
            Assert.Empty(observations);
            Assert.Empty(reviews);
            throw new IOException("simulated evidence write failure");
        }));

        Assert.Equal(1, Count("candidate_observations"));
        Assert.Equal(1, Count("candidate_reviews"));
        Assert.Empty(_ledger.ReadEvidence().Observations);
        Assert.Equal(0, Count("candidate_reviews"));
    }

    [Fact]
    public void FullHistoryExportUsesAnIndexInsteadOfMemorySort()
    {
        using var command = _fixture.Database.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT review_id, observation_id, request_id, verdict, note, " +
            "reviewed_at_utc FROM candidate_reviews ORDER BY reviewed_at_utc, rowid;";
        using var reader = command.ExecuteReader();
        var steps = new List<string>();
        while (reader.Read()) steps.Add(reader.GetString(3));
        Assert.Contains(steps, step => step.Contains("ix_candidate_reviews_time", StringComparison.Ordinal));
        Assert.DoesNotContain(steps, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    [Fact]
    public void OccurrencesRoundTripAndDefaultToOne()
    {
        Enable();
        var sample = Observation() with { HypothesisName = "QUEUE_WINDOW_SAMPLE", Group = "queue_window", Occurrences = 37 };
        Assert.True(_ledger.Add(sample));
        Assert.True(_ledger.Add(Observation()));
        var page = _ledger.Query(pageSize: 10);
        Assert.Equal(37, page.Items.Single(e => e.Observation.HypothesisName == "QUEUE_WINDOW_SAMPLE").Observation.Occurrences);
        Assert.Equal(1, page.Items.Single(e => e.Observation.HypothesisName == "FINDER_STATE_NOTIFICATION").Observation.Occurrences);
        Assert.Equal(37, CandidateWire.Observation(page.Items.Single(e => e.Observation.Occurrences == 37))["occurrences"]!.GetValue<int>());
        // The table refuses a zero count itself, so no write path can slip one past the code.
        Assert.ThrowsAny<Exception>(() => _ledger.Add(Observation() with { Occurrences = 0 }));
    }

    private void Enable() => _settings.SetSetting(CaptureSettingsStore.CandidateValidationSetting, "true");
    private static string Id() => Guid.NewGuid().ToString("D");
    private CandidateObservation Observation(DateTimeOffset? at = null) => new(Id(), _session,
        "candidate-test", "FINDER_STATE_NOTIFICATION", "finder", "S2C", 0x0323, 40,
        "0123456789ab", "abcdef012345", at ?? _fixture.Clock.UtcNow, 100);

    private long Count(string table)
    {
        using var command = _fixture.Database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table;
        return (long)command.ExecuteScalar()!;
    }

    private static string SeedId(int number) => "00000000-0000-0000-0000-" + number.ToString("D12");

    private void Seed(int count) => _fixture.Database.RunInTransaction(tx =>
    {
        using var command = _fixture.Database.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "WITH RECURSIVE n(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM n WHERE value < $count) " +
            "INSERT INTO candidate_observations (observation_id, capture_session_id, profile_id, hypothesis_name, " +
            "group_name, direction, opcode, payload_length, payload_hash12, connection_tag, observed_at_utc, t_ms) " +
            "SELECT printf('00000000-0000-0000-0000-%012d', value), $session, 'candidate-test', 'FINDER_STATE_NOTIFICATION', " +
            "'finder', 'S2C', 803, 40, '0123456789ab', 'abcdef012345', $observed, value FROM n;";
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$session", _session);
        command.Parameters.AddWithValue("$observed", UtcTimestamp.ToText(_fixture.Clock.UtcNow.AddHours(-1)));
        command.ExecuteNonQuery();
    });

    public void Dispose() => _fixture.Dispose();
}
