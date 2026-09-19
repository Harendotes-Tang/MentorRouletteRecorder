using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The rules the profile format puts around <c>MATCH_ANNOUNCED</c>.
///
/// The message exists for one situation only: a build whose announcement carries no roulette id,
/// where the profile already stands the player's own queue request in for the match. Beside a
/// profile that reads the server's announcement properly it would be a second, weaker opinion
/// about the same moment, so it is refused there; and since a match is something the server tells
/// the client, it may only ever travel that way.
/// </summary>
public sealed class MatchAnnouncedProfileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.Profiles", Guid.NewGuid().ToString("N"));

    public MatchAnnouncedProfileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Test debris in the OS temp folder is not worth failing a test over.
        }
    }

    /// <summary>A profile document with the pop travelling the given way and an announcement beside it.</summary>
    /// <param name="popFromClient">True to make CONTENT_FINDER_POP the player's own request.</param>
    /// <param name="announcedFromClient">True to declare the announcement the wrong way round.</param>
    private static Dictionary<string, object> Document(bool popFromClient, bool announcedFromClient = false)
    {
        var document = ProfileTestFiles.Valid();
        var messages = (List<object>)document["messages"];
        if (popFromClient)
        {
            ((Dictionary<string, object>)messages[0])["direction"] = "CLIENT_TO_SERVER";
        }

        messages.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["name"] = "MATCH_ANNOUNCED",
            ["opcode"] = 150,
            ["direction"] = announcedFromClient ? "CLIENT_TO_SERVER" : "SERVER_TO_CLIENT",
            ["expected_length"] = 12,
            ["fields"] = new List<object>(),
        });
        return document;
    }

    /// <summary>
    /// The case the message was invented for: a queue-inferred profile that also knows when the
    /// popup appears. It declares no field, because the message carries nothing worth reading.
    /// </summary>
    [Fact]
    public void AQueueInferredProfileMayDeclareTheAnnouncement()
    {
        var path = ProfileTestFiles.Write(_directory, "test-announced", Document(popFromClient: true));

        var report = ProfileLoader.Validate(path);

        Assert.True(report.Ok, string.Join("; ", report.Errors.Select(error => error.Message)));
        var announced = report.Profile!.Message("MATCH_ANNOUNCED");
        Assert.NotNull(announced);
        Assert.Equal(PacketDirection.ServerToClient, announced!.Direction);
        Assert.Equal(12, announced.ExpectedLength);
        Assert.Empty(announced.Fields);
    }

    /// <summary>
    /// Beside a pop the server sends, the announcement has nothing to add and no evidence behind
    /// it: the match is already read from the server's own message, and two messages claiming the
    /// same moment would open a run twice.
    /// </summary>
    [Fact]
    public void AProfileThatReadsTheServersOwnPopMayNotDeclareIt()
    {
        var path = ProfileTestFiles.Write(_directory, "test-announced-ctx", Document(popFromClient: false));

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Contains(report.Errors, error => error.Code == "E_PROFILE_MESSAGE_CONTEXT");
    }

    /// <summary>A match is something the server tells the client, whatever a profile claims.</summary>
    [Fact]
    public void AnAnnouncementTravellingTheOtherWayIsRefused()
    {
        var path = ProfileTestFiles.Write(
            _directory, "test-announced-dir", Document(popFromClient: true, announcedFromClient: true));

        var report = ProfileLoader.Validate(path);

        Assert.False(report.Ok);
        Assert.Contains(report.Errors, error => error.Code == "E_PROFILE_MESSAGE_CONTEXT");
    }

    /// <summary>
    /// The share code stays format v1. An opcode learned from this machine's timing evidence
    /// means nothing on another machine without that evidence, so a profile carrying an
    /// announcement is shared as the plain queue-request profile underneath it, and the receiving
    /// machine goes looking for its own.
    /// </summary>
    [Fact]
    public void AProfileCarryingAnAnnouncementSharesAsAPlainQueueRequestProfile()
    {
        var template = CalibrationObserverTests.Template();
        var draft = CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequestAnnounced);
        Assert.Contains(draft.Messages, message => message.Name == "MATCH_ANNOUNCED");

        var values = CalibratedShape.Read(template, draft.Messages);

        Assert.NotNull(values);
        Assert.Equal(CalibrationMatchSource.QueueRequest, values!.MatchSource);
        var rebuilt = CalibratedShape.Messages(template, values);
        Assert.Null(rebuilt.Error);
        Assert.DoesNotContain(rebuilt.Messages, message => message.Name == "MATCH_ANNOUNCED");
        Assert.Equal(
            draft.Messages.Where(message => message.Name != "MATCH_ANNOUNCED").Select(message => message.Name),
            rebuilt.Messages.Select(message => message.Name));
    }
}
