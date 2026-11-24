using System.Text.Json.Nodes;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// The request side of the contract, checked against the samples the Qt client generates.
///
/// tests/Fixtures/ipc-requests/*.json are written by the Desktop test suite
/// <c>IpcRequest</c> (ctest <c>MentorRecorderIpcRequests</c>) straight out of the shipping <c>IBackend</c>
/// wrappers, and pinned there. This suite validates each of them against the schema and
/// then feeds them to a real Collector, so a request the client can build but the Collector
/// refuses -- or that the contract does not describe -- fails here rather than in the field.
/// tests/Fixtures/ipc-requests/variants/*.json are the further shapes one message can take
/// (a paged import, its next page, an import from a file); they are held to the schema too
/// (audit 2026-10-20, G-3). Single request fields whose accepted values the schema and the
/// Collector must agree on are held to both here as well.
/// </summary>
public sealed class ContractRequestSampleTests
{
    private static string SampleDirectory =>
        Path.Combine(ContractSchema.RepositoryRoot, "tests", "Fixtures", "ipc-requests");

    private static IReadOnlyList<(string MessageType, JsonObject Envelope)> Samples()
    {
        var samples = new List<(string, JsonObject)>();
        foreach (var path in Directory.GetFiles(SampleDirectory, "*.json").OrderBy(
                     name => name, StringComparer.Ordinal))
        {
            var envelope = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidOperationException(path + " 不是一个 JSON 对象。");
            samples.Add((Path.GetFileNameWithoutExtension(path), envelope));
        }

        return samples;
    }

    /// <summary>The variant samples, each named <c>&lt;MessageType&gt;.&lt;variant&gt;.json</c>; the message type is the envelope's.</summary>
    private static IReadOnlyList<(string Name, JsonObject Envelope)> Variants()
    {
        var variants = new List<(string, JsonObject)>();
        foreach (var path in Directory.GetFiles(Path.Combine(SampleDirectory, "variants"), "*.json").OrderBy(
                     name => name, StringComparer.Ordinal))
        {
            var envelope = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidOperationException(path + " 不是一个 JSON 对象。");
            variants.Add((Path.GetFileNameWithoutExtension(path), envelope));
        }

        return variants;
    }

    [Fact]
    public void OneSampleExistsForEveryBusinessMessage() =>
        Assert.Equal(
            ContractSchema.BusinessMessageTypes.OrderBy(name => name, StringComparer.Ordinal),
            Samples().Select(sample => sample.MessageType).OrderBy(name => name, StringComparer.Ordinal));

    [Fact]
    public void EverySampleMatchesTheRequestEnvelopeSchema()
    {
        foreach (var (messageType, envelope) in Samples())
        {
            Assert.Equal(messageType, envelope["message_type"]!.GetValue<string>());
            ContractSchema.Validate(string.Empty, envelope, $"{messageType} request envelope");
            ContractSchema.Validate(
                "$defs/RequestEnvelope", envelope, $"{messageType} request envelope");
        }
    }

    [Fact]
    public void EveryVariantSampleMatchesTheRequestEnvelopeSchema()
    {
        var variants = Variants();
        Assert.NotEmpty(variants);
        foreach (var (name, envelope) in variants)
        {
            var messageType = envelope["message_type"]!.GetValue<string>();
            Assert.Contains(messageType, ContractSchema.BusinessMessageTypes);
            Assert.StartsWith(messageType + ".", name, StringComparison.Ordinal);
            ContractSchema.Validate(string.Empty, envelope, $"{name} request envelope");
            ContractSchema.Validate("$defs/RequestEnvelope", envelope, $"{name} request envelope");
        }
    }

    /// <summary>
    /// Audit 2026-10-03 OF-7. Two request fields the contract offered and the Collector did not
    /// honour: GetResultStats shared a definition that allowed trend_granularity, which the handler
    /// refused, and the export requests declared include_revisions, which the handler read and threw
    /// away. Contract and Collector now agree, both ways, on each.
    /// </summary>
    [Theory]
    [InlineData("GetResultStats", "{\"filter\":{},\"trend_granularity\":\"week\"}", "{\"filter\":{}}")]
    [InlineData("ExportJson", "{\"target_path\":\"{0}\",\"include_revisions\":true}", "{\"target_path\":\"{0}\"}")]
    [InlineData("ExportCsv", "{\"target_path\":\"{0}\",\"include_revisions\":false}", "{\"target_path\":\"{0}\"}")]
    public async Task TheContractAndTheCollectorAgreeOnWhichRequestFieldsExist(
        string messageType, string refused, string accepted)
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var target = Path.Combine(fixture.DataDirectory, messageType + ".out")
            .Replace("\\", "\\\\", StringComparison.Ordinal);

        JsonObject Payload(string template) =>
            JsonNode.Parse(template.Replace("{0}", target, StringComparison.Ordinal))!.AsObject();

        Assert.False(
            ContractSchema.Evaluate(
                "$defs/RequestEnvelope", ContractSchema.RequestEnvelope(messageType, Payload(refused))).IsValid,
            messageType + " 的契约仍然接受 " + refused);
        var refusal = await client.SendAsync(messageType, Payload(refused));
        Assert.Equal(ErrorCodes.BadRequest, refusal.ErrorCode);

        ContractSchema.Validate(
            "$defs/RequestEnvelope", ContractSchema.RequestEnvelope(messageType, Payload(accepted)), messageType);
        Assert.True((await client.SendAsync(messageType, Payload(accepted))).Ok);
    }

    [Fact]
    public async Task EverySampleIsAcceptedByTheCollector()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        foreach (var (messageType, envelope) in Samples())
        {
            // The StartCapture sample names a synthetic adapter so the field is exercised
            // without binding a real device; its refusal describes this machine, not the
            // contract. Marker acceptance requires RECORDING, and CaptureValidationIpcTests
            // sends all five markers over a real pipe in an isolated synthetic session.
            if (messageType is "StartCapture" or "AddCaptureValidationMarker")
            {
                continue;
            }

            var payload = envelope["payload"]!.AsObject().DeepClone().AsObject();
            if (messageType == "CommitRunImport")
            {
                var preview = (await client.SendAsync("PreviewRunImport", new JsonObject
                {
                    ["source_kind"] = "ROWS", ["rows"] = new JsonArray(new JsonObject { ["duty_name"] = "合成样本" }),
                })).Require();
                payload["preview_id"] = preview["preview_id"]!.DeepClone();
            }
            if (messageType == "ExportCandidateEvidence")
                payload["target_path"] = Path.Combine(fixture.DataDirectory, "sample-candidate-evidence.json");
            var response = await client.SendAsync(messageType, payload);

            // A business refusal is fine. ERR_BAD_REQUEST means the Collector could not read
            // the request at all, which is the client/contract drift this test exists to catch.
            Assert.False(
                response.ErrorCode == ErrorCodes.BadRequest,
                $"{messageType} 样本被 Collector 判为 ERR_BAD_REQUEST：{response.ErrorMessage}");
        }
    }

    /// <summary>
    /// contracts/CHANGELOG.md 2026-11-03: ExportRequest.sort is the RunSort QueryRuns takes. The
    /// schema and the Collector agree on what passes and what is refused, and an accepted sort
    /// orders the file: ascending history_date puts the earliest run on the first data row.
    /// </summary>
    [Theory]
    [InlineData("history_date", "asc", true)]
    [InlineData("history_date", "desc", true)]
    [InlineData("history_date", "sideways", false)]
    [InlineData("run_id", "asc", false)]
    public async Task ExportSort_MatchesSchemaAndRealPipe(string field, string direction, bool accepted)
    {
        var request = new JsonObject
        {
            ["target_path"] = "placeholder",
            ["filter"] = new JsonObject(),
            ["sort"] = new JsonObject { ["field"] = field, ["direction"] = direction },
        };
        Assert.Equal(accepted, ContractSchema.Evaluate("$defs/ExportRequest", request).IsValid);

        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var earliest = await ManualRuns.CreateAsync(client, 5, enteredAtUtc: "2026-09-01T01:00:00.000Z");
        var latest = await ManualRuns.CreateAsync(client, 5, enteredAtUtc: "2026-09-03T01:00:00.000Z");
        var target = Path.Combine(fixture.DataDirectory, $"sorted-{field}-{direction}.json");
        request["target_path"] = target;

        var response = await client.SendAsync("ExportJson", request);
        Assert.Equal(accepted, response.Ok);
        if (!accepted)
        {
            Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
            return;
        }

        ContractSchema.Validate("$defs/Responses/ExportJson", response.Require(), "sorted export");
        var ids = JsonNode.Parse(File.ReadAllText(target))!.AsArray()
            .Select(item => item!["run_id"]!.GetValue<string>()).ToArray();
        Assert.Equal(direction == "asc" ? new[] { earliest, latest } : new[] { latest, earliest }, ids);
    }

    [Theory]
    [InlineData(1000, true)]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public async Task ExactNameLimit_MatchesSchemaAndRealPipe(int count, bool accepted)
    {
        var names = new JsonArray(Enumerable.Range(0, count)
            .Select(index => (JsonNode?)JsonValue.Create("name-" + index)).ToArray());
        await AssertQueryRunsFilterAgreesAsync(new JsonObject { ["name_exact"] = names }, accepted, "bounded exact names");
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public async Task ReflectionTextLimit_MatchesSchemaAndRealPipe(int length, bool accepted)
    {
        Assert.True(ContractSchema.Evaluate("$defs/RunFilter", new JsonObject { ["reflection_text"] = null }).IsValid);
        var filter = new JsonObject { ["reflection_text"] = new string('字', length), ["with_reflection"] = true };
        await AssertQueryRunsFilterAgreesAsync(filter, accepted, "bounded reflection text");
    }

    /// <summary>The schema and a real Collector both accept <paramref name="filter"/> on QueryRuns, or both refuse it.</summary>
    /// <param name="filter">The RunFilter under test.</param>
    /// <param name="accepted">Whether both must accept it.</param>
    /// <param name="what">What the filter is, for the failure message.</param>
    private static async Task AssertQueryRunsFilterAgreesAsync(JsonObject filter, bool accepted, string what)
    {
        Assert.Equal(accepted, ContractSchema.Evaluate("$defs/RunFilter", filter).IsValid);
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var response = await client.SendAsync("QueryRuns", new JsonObject
        {
            ["filter"] = filter, ["page"] = 1, ["page_size"] = 1,
        });
        Assert.Equal(accepted, response.Ok);
        if (accepted)
            ContractSchema.Validate("$defs/Responses/QueryRuns", response.Require(), what);
        else
            Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
    }
}
