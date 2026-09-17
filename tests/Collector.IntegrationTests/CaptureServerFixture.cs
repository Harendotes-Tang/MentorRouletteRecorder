using System.Text.Json.Nodes;
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
public sealed class CaptureServerFixture : IAsyncDisposable
{
    private readonly string _directory;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _serving;

    private CaptureServerFixture(string directory, CollectorHost host, PipeServer server)
    {
        _directory = directory;
        Host = host;
        Server = server;
        _serving = server.RunAsync(_stopping.Token);
    }

    /// <summary>The running host.</summary>
    public CollectorHost Host { get; }

    /// <summary>The running server.</summary>
    public PipeServer Server { get; }

    /// <summary>Pipe name clients connect to.</summary>
    public string PipeName => Server.PipeName;

    /// <summary>Starts a server on a fresh database.</summary>
    /// <param name="capture">Capture dependencies; the isolated no-Npcap/no-game environment when null.</param>
    /// <param name="profileSelector">Optional live-pipeline profile selector.</param>
    public static CaptureServerFixture Start(
        CaptureServices? capture = null,
        Func<GameProcessDetection, ProfileSelection>? profileSelector = null,
        CaptureValidationServices? validation = null)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.CaptureTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var host = CollectorHost.Open(
            Path.Combine(directory, "test.db"),
            SystemClock.Instance,
            capture ?? CaptureFakes.NoGame(),
            profileSelector: profileSelector, validation: validation,
            speechClient: ServerFixture.RefusingSpeechClient);

        var pipeName = "MentorRecorder.test." + Guid.NewGuid().ToString("N") + ".v1";
        return new CaptureServerFixture(directory, host, new PipeServer(new MessageDispatcher(host), pipeName));
    }

    /// <summary>Opens a connected client.</summary>
    public async Task<PipeClient> ConnectAsync()
    {
        var client = new PipeClient(PipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return client;
    }

    /// <summary>Sends one request on a fresh connection and returns the response.</summary>
    /// <param name="messageType">Message type from the contract.</param>
    /// <param name="payload">Request payload.</param>
    public async Task<IpcResponse> CallAsync(string messageType, JsonObject? payload = null)
    {
        await using var client = await ConnectAsync().ConfigureAwait(false);
        return await client.SendAsync(messageType, payload).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await Server.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _serving.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the accept loop ends with the token.
        }

        _stopping.Dispose();
        Host.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Windows can hold a WAL handle briefly; the temp cleaner will get it.
        }
    }
}
