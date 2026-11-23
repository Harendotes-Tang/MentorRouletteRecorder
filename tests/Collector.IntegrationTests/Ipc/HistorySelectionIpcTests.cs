using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>Verifies frozen and staged history selections over the production named pipe.</summary>
public sealed class HistorySelectionIpcTests
{
    private const int PageSize = 200;
    private const int LargeCount = 2003;
    private const string GroupNote = "history selection fixture";
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FrozenPagesKeepMembershipAndRevisionsAfterAnotherClientWrites()
    {
        await using var fixture = SeedRuns(LargeCount);
        await using var reader = await fixture.ConnectAsync();
        await using var writer = await fixture.ConnectAsync();
        var first = await FreezeAsync(reader);
        var id = first["selection_id"]!.GetValue<string>();

        var movedAt = Start.AddDays(3);
        (await writer.SendAsync("CorrectRun", new JsonObject
        {
            ["run_id"] = RunId(0),
            ["expected_revision"] = 1,
            ["reason"] = "move the first record to the front",
            ["changes"] = new JsonObject
            {
                ["matched_at_utc"] = Timestamp(movedAt.AddMinutes(-1)),
                ["entered_at_utc"] = Timestamp(movedAt),
                ["ended_at_utc"] = Timestamp(movedAt.AddMinutes(1)),
            },
        })).Require();
        (await writer.SendAsync("SoftDeleteRun", new JsonObject
        {
            ["run_id"] = RunId(LargeCount - 1), ["expected_revision"] = 1, ["reason"] = "remove a first-page record",
        })).Require();
        var insertedAt = movedAt.AddHours(1);
        var inserted = (await writer.SendAsync("CreateManualRun", new JsonObject
        {
            ["region"] = "CN",
            ["result"] = "COMPLETED",
            ["matched_at_utc"] = Timestamp(insertedAt.AddMinutes(-1)),
            ["entered_at_utc"] = Timestamp(insertedAt),
            ["ended_at_utc"] = Timestamp(insertedAt.AddMinutes(1)),
            ["note"] = GroupNote,
            ["reason"] = "insert a new first-page record",
        })).Require()["run_id"]!.GetValue<string>();

        var live = (await reader.SendAsync("QueryRuns", new JsonObject { ["page_size"] = 2 })).Require();
        Assert.Collection(live["items"]!.AsArray(),
            row => Assert.Equal(inserted, row!["run_id"]!.GetValue<string>()),
            row =>
            {
                Assert.Equal(RunId(0), row!["run_id"]!.GetValue<string>());
                Assert.Equal(2, row["revision"]!.GetValue<int>());
            });

        var frozen = await ReadFrozenRowsAsync(reader, first);
        Assert.All(frozen, row =>
        {
            Assert.Equal(1, row["revision"]!.GetValue<int>());
            Assert.False(row["soft_deleted"]!.GetValue<bool>());
            Assert.Equal("MANUAL", row["source"]!.GetValue<string>());
            Assert.Equal("CN", row["region"]!.GetValue<string>());
            Assert.Null(row["capture_session_id"]);
            Assert.False(row.ContainsKey("note"));
        });
        var seen = frozen.Select(row => row["run_id"]!.GetValue<string>()).ToArray();
        Assert.Equal(Enumerable.Range(0, LargeCount).Reverse().Select(RunId), seen);
        Assert.DoesNotContain(inserted, seen);
        var pastEnd = (await reader.SendAsync("QueryHistorySelection", ReadPage(id, int.MaxValue))).Require();
        Assert.Empty(pastEnd["items"]!.AsArray());
        await ReleaseAsync(reader, id);
        await ReleaseAsync(reader, id);
        Assert.Equal(ErrorCodes.NotFound, (await reader.SendAsync("QueryHistorySelection", ReadPage(id, 1))).ErrorCode);
    }

    [Fact]
    public async Task StagedPreviewAndSortedExportsIncludeOnlyTheChosenLargeSet()
    {
        await using var fixture = SeedRuns(LargeCount + 1);
        const string reflectionPrefix = "=练习,\"引号\"";
        var longReflection = reflectionPrefix + new string('记', ReflectionText.MaxLength - reflectionPrefix.Length);
        var reflections = new RunReflectionRepository(fixture.Host.Database);
        fixture.Host.Database.RunInTransaction(tx =>
        {
            for (var i = 0; i <= LargeCount; i++)
            {
                reflections.Upsert(RunId(i), ReflectionMood.Good, longReflection, Start, tx);
            }
        });
        await using var client = await fixture.ConnectAsync();
        var individualPage = (await client.SendAsync("QueryRuns", new JsonObject
        {
            ["filter"] = new JsonObject { ["run_id"] = new JsonArray(RunId(LargeCount)) },
        })).Require();
        var individual = Assert.Single(individualPage["items"]!.AsArray())!.AsObject();
        var first = await FreezeAsync(client);
        var frozen = await ReadFrozenRowsAsync(client, first);
        var chosen = frozen
            .Where(row => row["run_id"]!.GetValue<string>() != RunId(1))
            .Append(individual)
            .Select(row => new Target(row["run_id"]!.GetValue<string>(), row["revision"]!.GetValue<int>()));
        var selection = await StageAsync(client, chosen, checkRetries: true);
        await ReleaseAsync(client, first["selection_id"]!.GetValue<string>());
        var ordinals = Enumerable.Range(0, LargeCount + 1).Where(i => i != 1).ToArray();

        var profile = (await client.SendAsync("CreateCharacterProfile", new JsonObject
        {
            ["region"] = "GLOBAL",
            ["display_name"] = "selection destination",
            ["goal_count"] = 2000,
            ["baseline_completed_count"] = 3,
        })).Require()["profile_id"]!.GetValue<long>();
        var preview = (await client.SendAsync("PreviewRunRegionMigration", new JsonObject
        {
            ["selection_id"] = selection,
            ["region"] = "GLOBAL",
            ["profile_id"] = profile,
        })).Require();
        ContractSchema.Validate("$defs/Responses/PreviewRunRegionMigration", preview, "staged migration preview");
        Assert.Equal("CN", preview["source_region"]!.GetValue<string>());
        Assert.Equal(ordinals.Length, preview["contributing_count"]!.GetValue<int>());
        Assert.Equal(3, preview["target_baseline"]!.GetValue<int>());
        Assert.Equal(Region.Cn, fixture.Host.Runs.Get(RunId(LargeCount))!.Region);

        // Exports use checked membership, even if a selected row has entered the
        // recycle bin. Migration above was checked before that row became ineligible.
        (await client.SendAsync("SoftDeleteRun", new JsonObject
        {
            ["run_id"] = RunId(2), ["expected_revision"] = 1, ["reason"] = "selected record enters the recycle bin",
        })).Require();

        var jsonPath = await ExportAsync(client, fixture, selection, "json", "asc", ordinals.Length);
        var jsonRows = JsonNode.Parse(await File.ReadAllTextAsync(jsonPath))!.AsArray();
        Assert.Equal(ordinals.Select(RunId), jsonRows.Select(row => row!["run_id"]!.GetValue<string>()));
        Assert.True(Assert.Single(jsonRows, row => row!["run_id"]!.GetValue<string>() == RunId(2))!
            ["soft_deleted"]!.GetValue<bool>());
        Assert.All(jsonRows, row => Assert.Equal(longReflection, row!["reflection"]!["text"]!.GetValue<string>()));

        var csvPath = await ExportAsync(client, fixture, selection, "csv", "desc", ordinals.Length);
        var csvLines = await File.ReadAllLinesAsync(csvPath);
        Assert.Equal(RunExporter.CsvHeader, csvLines[0].Split(','));
        var runIdColumn = Array.IndexOf(csvLines[0].Split(','), "run_id");
        // Every field before run_id is unquoted. The following reflection contains a comma and
        // embedded quotes, so verify its serialized form separately from the run_id column.
        Assert.Equal(Enumerable.Reverse(ordinals).Select(RunId),
            csvLines.Skip(1).Select(row => row.Split(',', runIdColumn + 2)[runIdColumn]));
        var quotedReflection = "\"'=" + longReflection[1..].Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        Assert.All(csvLines.Skip(1), row => Assert.Contains(quotedReflection, row, StringComparison.Ordinal));

        await ReleaseAsync(client, selection);
        var missing = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "missing.csv");
        Assert.Equal(ErrorCodes.NotFound, (await client.SendAsync("ExportCsv",
            new JsonObject { ["target_path"] = missing, ["selection_id"] = selection })).ErrorCode);
        Assert.False(File.Exists(missing));

        var partial = await StageAsync(client, ordinals.Select(i => new Target(RunId(i), 1))
            .Append(new Target(RunId(LargeCount + 1), 1)));
        var previousBytes = await File.ReadAllBytesAsync(csvPath);
        var reply = await client.SendAsync("ExportCsv", new JsonObject
        {
            ["target_path"] = csvPath, ["selection_id"] = partial, ["overwrite"] = true,
        });
        Assert.Equal(ErrorCodes.NotFound, reply.ErrorCode);
        Assert.Equal(previousBytes, await File.ReadAllBytesAsync(csvPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(csvPath)!, ".mentor-export-*.tmp"));
    }

    [Fact]
    public async Task ConflictInTheLastChunkLeavesEveryEarlierSelectionUnchanged()
    {
        await using var fixture = SeedRuns(LargeCount);
        await using var client = await fixture.ConnectAsync();
        var selection = await StageAsync(client, Enumerable.Range(0, LargeCount).Select(i => new Target(RunId(i), 1)));
        // A previously checked revision must not be replaced with the newer revision.
        (await client.SendAsync("CorrectRun", new JsonObject
        {
            ["run_id"] = RunId(LargeCount - 1),
            ["expected_revision"] = 1,
            ["reason"] = "edit after the selection was staged",
            ["changes"] = new JsonObject { ["note"] = "newer revision" },
        })).Require();

        var requestId = Guid.NewGuid().ToString("D");
        var refused = await client.SendAsync("BatchMutateRuns", DeleteSelection(selection), requestId);
        Assert.Equal(ErrorCodes.RevisionConflict, refused.ErrorCode);
        await AssertNoDeleteWasCommittedAsync(client, fixture, requestId);
        Assert.Equal(2, fixture.Host.Runs.Get(RunId(LargeCount - 1))!.Revision);
    }

    [Fact]
    public async Task FailedFinalWriteRollsBackEarlierWritesAndTheirReceipt()
    {
        await using var fixture = SeedRuns(LargeCount);
        await using var client = await fixture.ConnectAsync();
        var selection = await StageAsync(client, Enumerable.Range(0, LargeCount).Select(i => new Target(RunId(i), 1)));
        fixture.Host.Database.RunInTransaction(tx =>
        {
            using var trigger = fixture.Host.Database.CreateCommand();
            trigger.Transaction = tx;
            // The guard fires only after the first staged record was actually written.
            trigger.CommandText = $"""
                CREATE TEMP TRIGGER fail_final_delete BEFORE UPDATE OF soft_deleted ON mentor_runs
                WHEN NEW.run_id = '{RunId(LargeCount - 1)}' AND NEW.soft_deleted = 1
                     AND EXISTS (SELECT 1 FROM mentor_runs WHERE run_id = '{RunId(0)}' AND soft_deleted = 1)
                BEGIN SELECT RAISE(ABORT, 'synthetic final history write failure'); END;
                """;
            trigger.ExecuteNonQuery();
        });

        var requestId = Guid.NewGuid().ToString("D");
        var refused = await client.SendAsync("BatchMutateRuns", DeleteSelection(selection), requestId);
        Assert.Equal(ErrorCodes.Internal, refused.ErrorCode);
        lock (fixture.Log)
            Assert.Contains(fixture.Log, line => line.Contains("synthetic final history write failure", StringComparison.Ordinal));
        await AssertNoDeleteWasCommittedAsync(client, fixture, requestId);
        Assert.Equal(1, fixture.Host.Runs.Get(RunId(LargeCount - 1))!.Revision);
        var revisions = (await client.SendAsync("GetRunRevisions", new JsonObject { ["run_id"] = RunId(0) })).Require();
        Assert.Single(revisions["items"]!.AsArray());
    }

    [Fact]
    public async Task CommitWithAnUnreadReplyReplaysAfterReleaseWithoutReapplying()
    {
        await using var fixture = SeedRuns(LargeCount + 1);
        await using var staging = await fixture.ConnectAsync();
        var selection = await StageAsync(staging, Enumerable.Range(0, LargeCount).Select(i => new Target(RunId(i), 1)));
        var requestId = Guid.NewGuid().ToString("D");
        var repository = new IdempotencyRepository(fixture.Host.Database, fixture.Host.Clock);
        var batch = DeleteSelection(selection);

        // This connection sends a real framed request but never reads a reply.
        // Wait for the durable receipt so the drop is definitely after commit.
        using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            await using var oneWay = new NamedPipeClientStream(".", fixture.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await oneWay.ConnectAsync(budget.Token);
            await oneWay.WriteAsync(IpcTestFrames.Request(requestId, "BatchMutateRuns", batch.DeepClone().AsObject()), budget.Token);
            await oneWay.FlushAsync(budget.Token);
            while (repository.TryGetResponse(requestId) is null)
                await Task.Delay(TimeSpan.FromMilliseconds(20), budget.Token);
        }

        await using var retry = await fixture.ConnectAsync();
        await ReleaseAsync(retry, selection);
        var replay = (await retry.SendAsync("BatchMutateRuns", batch.DeepClone().AsObject(), requestId)).Require();
        ContractSchema.Validate("$defs/Responses/BatchMutateRuns", replay, "large mutation replay");
        Assert.True(replay["idempotent_replay"]!.GetValue<bool>());
        Assert.Equal("soft_delete", replay["action"]!.GetValue<string>());
        Assert.Equal(LargeCount, replay["changed_count"]!.GetValue<int>());
        Assert.False(replay.ContainsKey("run_ids"));
        Assert.Equal(2, fixture.Host.Runs.Get(RunId(0))!.Revision);
        Assert.Equal(2, fixture.Host.Runs.Get(RunId(LargeCount - 1))!.Revision);
        var remaining = (await retry.SendAsync("QueryRuns")).Require();
        Assert.Equal(RunId(LargeCount), Assert.Single(remaining["items"]!.AsArray())!["run_id"]!.GetValue<string>());

        Assert.Equal(ErrorCodes.NotFound, (await retry.SendAsync("BatchMutateRuns", batch.DeepClone().AsObject())).ErrorCode);
        batch["reason"] = "same request id, different intent";
        Assert.Equal(ErrorCodes.IdempotencyConflict, (await retry.SendAsync("BatchMutateRuns", batch, requestId)).ErrorCode);
    }

    [Fact]
    public async Task BackToBackReleaseAndStageDoNotDiscardTheNewSet()
    {
        await using var fixture = SeedRuns(2);
        await using var client = await fixture.ConnectAsync();
        var targets = new[] { new Target(RunId(0), 1), new Target(RunId(1), 1) };
        var selection = await StageAsync(client, targets);

        var releaseRequestId = Guid.NewGuid().ToString("D");
        var stageRequestId = Guid.NewGuid().ToString("D");
        var release = IpcTestFrames.Request(releaseRequestId, "QueryHistorySelection", ReleaseRequest(selection));
        var stage = IpcTestFrames.Request(stageRequestId, "StageHistorySelection",
            StageRequest(selection, 0, targets.Take(1), last: false));

        // Put both requests on the same connection without waiting for release.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var pipe = new NamedPipeClientStream(".", fixture.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token);
        await pipe.WriteAsync(release.Concat(stage).ToArray(), deadline.Token);
        await pipe.FlushAsync(deadline.Token);
        var channel = new FrameChannel(pipe);
        var first = JsonNode.Parse(Assert.IsType<byte[]>(await channel.ReadFrameAsync(deadline.Token)))!.AsObject();
        Assert.Equal(releaseRequestId, first["request_id"]!.GetValue<string>());
        Assert.True(first["ok"]!.GetValue<bool>());
        Assert.True(first["payload"]!["released"]!.GetValue<bool>());
        var second = JsonNode.Parse(Assert.IsType<byte[]>(await channel.ReadFrameAsync(deadline.Token)))!.AsObject();
        Assert.Equal(stageRequestId, second["request_id"]!.GetValue<string>());
        Assert.True(second["ok"]!.GetValue<bool>());
        Assert.False(second["payload"]!["sealed"]!.GetValue<bool>());

        var sealedReply = (await client.SendAsync("StageHistorySelection",
            StageRequest(selection, 1, targets.Skip(1), last: true))).Require();
        Assert.True(sealedReply["sealed"]!.GetValue<bool>());
        Assert.Equal(2, sealedReply["next_chunk_index"]!.GetValue<int>());
        await ReleaseAsync(client, selection);
    }

    private static async Task AssertNoDeleteWasCommittedAsync(PipeClient client, ServerFixture fixture, string requestId)
    {
        var live = (await client.SendAsync("QueryRuns", new JsonObject { ["page_size"] = 1 })).Require();
        Assert.Equal(LargeCount, live["page_info"]!["total"]!.GetValue<int>());
        var deleted = (await client.SendAsync("QueryRuns", new JsonObject
        {
            ["filter"] = new JsonObject { ["deleted_only"] = true },
        })).Require();
        Assert.Empty(deleted["items"]!.AsArray());
        Assert.Equal(0, deleted["page_info"]!["total"]!.GetValue<int>());
        Assert.Equal(1, fixture.Host.Runs.Get(RunId(0))!.Revision);
        Assert.Null(new IdempotencyRepository(fixture.Host.Database, fixture.Host.Clock).TryGetResponse(requestId));
    }

    private static ServerFixture SeedRuns(int count) => ServerFixture.Start(host => host.Database.RunInTransaction(tx =>
    {
        var profileId = host.Settings.GetAchievementSettings(tx, region: Region.Cn).ProfileId;
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var at = Start.AddMinutes(ordinal);
            host.Runs.Insert(new MentorRun
            {
                RunId = RunId(ordinal),
                Revision = 1,
                Source = RunSource.Manual,
                Region = Region.Cn,
                ProfileId = profileId,
                ManuallyCreated = true,
                Result = RunResult.Completed,
                CreatedAtUtc = at,
                UpdatedAtUtc = at,
                MatchedAtUtc = at.AddMinutes(-1),
                EnteredAtUtc = at,
                EndedAtUtc = at.AddMinutes(1),
                DurationMs = 60_000,
                Note = ordinal < LargeCount ? GroupNote : "outside the filter",
            }, tx);
            host.Revisions.Append(new RunRevision
            {
                RevisionId = Guid.NewGuid().ToString("D"),
                RunId = RunId(ordinal),
                Revision = 1,
                ChangedAtUtc = at,
                ChangeKind = ChangeKind.CreateManual,
                Actor = RevisionActor.User,
                Reason = "seed history",
                RequestId = Guid.NewGuid().ToString("D"),
                Changes = [],
            }, tx);
        }
    }));

    private static string RunId(int ordinal) => "e0000000-0000-4000-8000-" + ordinal.ToString("x12", CultureInfo.InvariantCulture);
    private static string Timestamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static async Task<JsonObject> FreezeAsync(PipeClient client)
    {
        var first = (await client.SendAsync("QueryHistorySelection", new JsonObject
        {
            ["filter"] = new JsonObject { ["text"] = GroupNote },
        })).Require();
        Assert.True(Guid.TryParseExact(first["selection_id"]!.GetValue<string>(), "D", out _));
        ContractSchema.Validate("$defs/Responses/QueryHistorySelection", first, "frozen history page");
        return first;
    }

    private static async Task<IReadOnlyList<JsonObject>> ReadFrozenRowsAsync(PipeClient client, JsonObject first)
    {
        var id = first["selection_id"]!.GetValue<string>();
        var seen = new List<JsonObject>(LargeCount);
        var pages = (LargeCount + PageSize - 1) / PageSize;
        for (var page = 1; page <= pages; page++)
        {
            var response = page == 1
                ? first
                : (await client.SendAsync("QueryHistorySelection", ReadPage(id, page))).Require();
            Assert.Equal(id, response["selection_id"]!.GetValue<string>());
            var pageInfo = response["page_info"]!.AsObject();
            Assert.Equal(page, pageInfo["page"]!.GetValue<int>());
            Assert.Equal(PageSize, pageInfo["page_size"]!.GetValue<int>());
            Assert.Equal(LargeCount, pageInfo["total"]!.GetValue<int>());
            var items = response["items"]!.AsArray();
            Assert.Equal(Math.Min(PageSize, LargeCount - seen.Count), items.Count);
            seen.AddRange(items.Select(item => item!.AsObject()));
        }
        return seen;
    }

    private static JsonObject ReadPage(string id, int page) => new() { ["selection_id"] = id, ["page"] = page };
    private static JsonObject DeleteSelection(string id) => new()
    {
        ["action"] = "soft_delete", ["selection_id"] = id, ["reason"] = "delete this checked set",
    };

    private static JsonObject ReleaseRequest(string selection) =>
        new() { ["selection_id"] = selection, ["release"] = true };

    private static async Task ReleaseAsync(PipeClient client, string selection)
    {
        var response = (await client.SendAsync("QueryHistorySelection", ReleaseRequest(selection))).Require();
        Assert.True(response["released"]!.GetValue<bool>());
    }

    private static JsonObject StageRequest(string id, int index, IEnumerable<Target> targets, bool last) => new()
    {
        ["selection_id"] = id,
        ["chunk_index"] = index,
        ["last"] = last,
        ["runs"] = new JsonArray(targets.Select(target => (JsonNode)new JsonObject
        {
            ["run_id"] = target.Id, ["expected_revision"] = target.Revision,
        }).ToArray()),
    };

    private static async Task<string> StageAsync(PipeClient client, IEnumerable<Target> targets, bool checkRetries = false)
    {
        var sorted = targets.OrderBy(target => target.Id, StringComparer.Ordinal).ToArray();
        var canonical = string.Concat(sorted.Select(target =>
            target.Id + ":" + target.Revision.ToString(CultureInfo.InvariantCulture) + "\n"));
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var chunks = sorted.Chunk(PageSize).ToArray();
        for (var index = 0; index < chunks.Length; index++)
        {
            var last = index == chunks.Length - 1;
            var request = StageRequest(id, index, chunks[index], last);
            var response = (await client.SendAsync("StageHistorySelection", request)).Require();
            Assert.Equal(id, response["selection_id"]!.GetValue<string>());
            Assert.Equal(index + 1, response["next_chunk_index"]!.GetValue<int>());
            Assert.Equal(last, response["sealed"]!.GetValue<bool>());
            if (checkRetries && index == 0)
            {
                ContractSchema.Validate("$defs/Responses/StageHistorySelection", response, "staged upload chunk");
                var retry = (await client.SendAsync("StageHistorySelection", request.DeepClone().AsObject())).Require();
                Assert.True(JsonNode.DeepEquals(response, retry));
                request["runs"]![0]!["expected_revision"] = 2;
                Assert.Equal(ErrorCodes.BadRequest, (await client.SendAsync("StageHistorySelection", request.DeepClone().AsObject())).ErrorCode);
                Assert.Equal(ErrorCodes.NotFound, (await client.SendAsync("BatchMutateRuns", DeleteSelection(id))).ErrorCode);
            }
        }
        return id;
    }

    private static async Task<string> ExportAsync(
        PipeClient client, ServerFixture fixture, string selection, string format, string direction, int expectedCount)
    {
        var path = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, $"checked.{format}");
        var type = format == "csv" ? "ExportCsv" : "ExportJson";
        var result = (await client.SendAsync(type, new JsonObject
        {
            ["target_path"] = path,
            ["selection_id"] = selection,
            ["sort"] = new JsonObject { ["field"] = "history_date", ["direction"] = direction },
        })).Require();
        Assert.Equal(expectedCount, result["row_count"]!.GetValue<int>());
        Assert.Equal(new FileInfo(path).Length, result["byte_count"]!.GetValue<long>());
        return path;
    }

    private sealed record Target(string Id, int Revision);
}
