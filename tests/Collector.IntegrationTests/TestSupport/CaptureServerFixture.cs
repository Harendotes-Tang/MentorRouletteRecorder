using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// A real pipe server whose capture layer can be substituted.
///
/// Two shapes matter. With no <c>capture</c> argument the server uses a deterministic
/// no-Npcap/no-game environment, independent of the developer's installed driver or game.
/// With a <see cref="FakeCaptureSource"/> injected, the same server can be driven all the way
/// through a capture without either. Both run the shipping dispatcher over a real Windows
/// Named Pipe with the production ACL.
/// </summary>
public static class CaptureServerFixture
{
    /// <summary>Starts a server on a fresh database.</summary>
    /// <param name="capture">Capture dependencies; the isolated no-Npcap/no-game environment when null.</param>
    /// <param name="profileSelector">
    /// Optional live-pipeline profile selector. With none, an injected capture runs without the live protocol
    /// bridge, so the profile the capture services declare stays in force.
    /// </param>
    /// <param name="validation">Optional capture-validation dependencies.</param>
    public static ServerFixture Start(
        CaptureServices? capture = null,
        Func<GameProcessDetection, ProfileSelection>? profileSelector = null,
        CaptureValidationServices? validation = null)
    {
        var directory = new TempDirectory("CaptureTests");

        var host = CollectorHost.Open(
            directory.Combine("test.db"),
            SystemClock.Instance,
            capture ?? CaptureFakes.NoGame(),
            profileSelector: profileSelector, validation: validation,
            speechClient: ServerFixture.RefusingSpeechClient,
            updateCheckClient: ServerFixture.RefusingUpdateCheckClient);
        return ServerFixture.Serve(directory, host);
    }
}
