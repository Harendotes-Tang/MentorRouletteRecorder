using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// The request side of the contract, checked against the samples the Qt client generates.
///
/// tests/Fixtures/ipc-requests/*.json are written by the Desktop test
/// <c>MentorRecorderIpcRequestTests</c> straight out of the shipping <c>IBackend</c>
/// wrappers, and pinned there. This suite validates each of them against the schema and
/// then feeds them to a real Collector, so a request the client can build but the Collector
/// refuses -- or that the contract does not describe -- fails here rather than in the field.
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
            if (messageType == "ExportCandidateEvidence")
                payload["target_path"] = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "sample-candidate-evidence.json");
            var response = await client.SendAsync(messageType, payload);

            // A business refusal is fine. ERR_BAD_REQUEST means the Collector could not read
            // the request at all, which is the client/contract drift this test exists to catch.
            Assert.False(
                response.ErrorCode == ErrorCodes.BadRequest,
                $"{messageType} 样本被 Collector 判为 ERR_BAD_REQUEST：{response.ErrorMessage}");
        }
    }
}
