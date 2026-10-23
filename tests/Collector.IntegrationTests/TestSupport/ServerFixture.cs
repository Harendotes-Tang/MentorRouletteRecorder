global using Xunit;

using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Speech;
using MentorRecorder.Collector.Update;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// A real pipe server on a throwaway database.
///
/// Every test gets its own pipe name and its own database file, so the suite can run in
/// parallel and never touches the developer's real data. The pipe is a real Windows Named
/// Pipe with the production ACL: these tests exercise the shipping transport, not a mock.
/// Capture detection is isolated from the developer's installed Npcap and running game.
/// </summary>
public sealed class ServerFixture : IAsyncDisposable
{
    private readonly string _directory;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _serving;

    private ServerFixture(string directory, CollectorHost host, PipeServer server, List<string> log)
    {
        _directory = directory;
        Host = host;
        Server = server;
        Log = log;
        _serving = server.RunAsync(_stopping.Token);
    }

    /// <summary>A speech client whose transport throws before anything is sent.</summary>
    public static OnlineSpeechClient RefusingSpeechClient { get; } = new(
        (_, _) => throw new InvalidOperationException("integration tests never reach a speech service"));

    /// <summary>An update-check client whose transport throws before anything is sent.</summary>
    public static UpdateCheckClient RefusingUpdateCheckClient { get; } = new(
        (_, _) => throw new InvalidOperationException("integration tests never reach a release host"),
        readEnvironment: _ => null);

    /// <summary>Server-side diagnostics collected during the test.</summary>
    public List<string> Log { get; }

    /// <summary>
    /// The accept loop, so a test can assert it is still running. A server that stopped
    /// accepting is indistinguishable from a healthy one until the next connection attempt.
    /// </summary>
    public Task Serving => _serving;

    /// <summary>The running host.</summary>
    public CollectorHost Host { get; }

    /// <summary>The running server.</summary>
    public PipeServer Server { get; }

    /// <summary>Pipe name clients connect to.</summary>
    public string PipeName => Server.PipeName;

    /// <summary>Path of the throwaway database.</summary>
    public string DatabasePath => Host.Database.Path;

    /// <summary>Starts a server on a fresh database.</summary>
    /// <param name="seed">Optional callback run against the host before the server starts.</param>
    /// <param name="capture">
    /// Capture dependencies; the deterministic no-Npcap/no-game environment when null.
    /// <c>CaptureFakes.Ready</c> lets a transport test drive a whole capture without Npcap and
    /// without the game, which is what makes the StartCapture and StopCapture success shapes
    /// checkable (review finding M-12).
    /// </param>
    /// <param name="speech">
    /// Online speech client. When null the server gets one whose transport refuses to send, so
    /// no transport test can reach a speech service whatever settings it writes.
    /// </param>
    /// <param name="updateCheckClient">
    /// Update-check client. When null the server gets one whose transport refuses to send, so no
    /// transport test can reach a release host however the status is polled.
    /// </param>
    public static ServerFixture Start(
        Action<CollectorHost>? seed = null,
        CaptureServices? capture = null,
        OnlineSpeechClient? speech = null,
        UpdateCheckClient? updateCheckClient = null)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.IntegrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        // Supplying detector fakes alone opts out of the live protocol bridge. Keep the
        // shipping selector and bridge active so transport tests do not lose that coverage.
        var selector = new ProfileSelector(ProfileCatalog.LoadDefault());
        var host = CollectorHost.Open(
            Path.Combine(directory, "test.db"), SystemClock.Instance, capture ?? CaptureFakes.NoGame(),
            profileSelector: game => selector.Select(game.Region, game.GameBuild),
            speechClient: speech ?? RefusingSpeechClient,
            updateCheckClient: updateCheckClient ?? RefusingUpdateCheckClient);
        seed?.Invoke(host);

        // A per-test pipe name keeps parallel tests from colliding with each other, and with
        // any Collector the developer happens to have running.
        var pipeName = "MentorRecorder.test." + Guid.NewGuid().ToString("N") + ".v1";
        var log = new List<string>();
        var server = new PipeServer(
            new MessageDispatcher(host),
            pipeName,
            (message, error) =>
            {
                lock (log)
                {
                    log.Add(error is null ? message : message + " :: " + error);
                }
            });
        return new ServerFixture(directory, host, server, log);
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
