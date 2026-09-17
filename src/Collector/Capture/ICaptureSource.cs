using System.Net;
using System.Security.Cryptography;
using System.Text;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// How the Oodle decompressor is obtained. Decided in docs/privacy-boundary.md section 4.1
/// as <c>DEC-OODLE-01</c>; both values are disclosed to the user through <c>GetStatus</c>.
/// </summary>
public enum OodleMode
{
    /// <summary>
    /// Default. Machina copies the game executable to a temporary file and loads that copy
    /// into <em>our</em> process to signature-scan it. This reads the game executable
    /// <em>from disk</em>; it never reads the game process's memory.
    /// </summary>
    FfxivTcp,

    /// <summary>
    /// Uses a user-supplied <c>oo2net_9_win64.dll</c>. Preferred when the user provides one,
    /// because it does not touch the game binary at all. Never shipped by this software.
    /// </summary>
    LibraryTcp,
}

/// <summary>Everything a capture source needs to start observing one game session.</summary>
/// <param name="CaptureSessionId">Session identifier stamped on every decoded message.</param>
/// <param name="ProcessId">Game process id whose connections are observed.</param>
/// <param name="BindAddress">IPv4 address of the already-selected adapter; live capture refuses null.</param>
/// <param name="AdapterId">Selected adapter identity, matched with its address before opening Npcap.</param>
/// <param name="Oodle">How the decompressor is obtained.</param>
/// <param name="OodleLibraryPath">Path of the user-supplied Oodle library, when <see cref="OodleMode.LibraryTcp"/>.</param>
/// <param name="GameExecutablePath">Game executable path, needed by <see cref="OodleMode.FfxivTcp"/>.</param>
/// <param name="Region">Region detected for the same process.</param>
/// <param name="GameBuild">Build read for the same process.</param>
/// <param name="AllowCandidateOodleSignature">
/// True only for an explicit research/trace run that is validating a CANDIDATE signature
/// profile. Normal Collector capture keeps this false and accepts VERIFIED profiles only.
/// </param>
public sealed record CaptureStartOptions(
    string CaptureSessionId,
    int ProcessId,
    IPAddress? BindAddress,
    string? AdapterId,
    OodleMode Oodle,
    string? OodleLibraryPath,
    string? GameExecutablePath,
    Region Region = Region.Unknown,
    string? GameBuild = null,
    bool AllowCandidateOodleSignature = false);

/// <summary>
/// What a capture source reports back. Every method is called from the capture callback
/// thread and must return immediately: blocking here drops packets at the driver, which is
/// far harder to diagnose than dropping them in our own bounded queue
/// (docs/architecture.md section 4, rule 1).
/// </summary>
public interface ICaptureSourceObserver
{
    /// <summary>One message arrived and its framing was read.</summary>
    /// <param name="message">Decoded message, ready for the parser queue.</param>
    void OnMessage(DecodedMessage message);

    /// <summary>A message arrived whose framing could not be read. Counted, never thrown.</summary>
    void OnDecodeError();

    /// <summary>The monitor failed and this capture is over.</summary>
    /// <param name="reason">Short, user-facing reason with no path or address in it.</param>
    /// <param name="error">Underlying exception, for the local log only.</param>
    void OnFault(string reason, Exception? error);

    /// <summary>
    /// A game connection that had delivered decoded messages ended: closed with FIN or RST, or
    /// no longer listed for the game process by the operating system. Capture itself continues
    /// -- the client may open a new connection -- but a run that was inside a duty when this
    /// happened is DISCONNECTED rather than merely interrupted
    /// (docs/state-machine.md section 3.6).
    ///
    /// Only connections that produced decoded messages are reported: pre-login lobby churn says
    /// nothing about a run (review finding H-6). Defaulted to nothing so a diagnostics-only
    /// observer does not have to care.
    /// </summary>
    void OnConnectionClosed()
    {
    }
}

/// <summary>
/// Which Oodle signature set a running capture is using.
///
/// Reported to the diagnostics page: which signatures are in force decides whether anything
/// can be decompressed at all, and a CANDIDATE profile must never be mistaken for a verified
/// one on the strength of a green capture indicator.
/// </summary>
/// <param name="Source">"builtin", "profile", "pattern-fallback", or null when nothing is running.</param>
/// <param name="ProfileId">Profile identifier, or null for the built-in table.</param>
/// <param name="Status">CANDIDATE or VERIFIED, or null for the built-in table.</param>
public sealed record OodleSignatureUse(string? Source, string? ProfileId, string? Status)
{
    /// <summary>Nothing is running, or this source does not decompress.</summary>
    public static OodleSignatureUse None { get; } = new(null, null, null);

    /// <summary>Machina's own built-in signature table.</summary>
    public static OodleSignatureUse Builtin { get; } = new("builtin", null, null);
}

/// <summary>
/// What the ingress path actually saw, below the message level.
///
/// <see cref="ICaptureSourceObserver.OnMessage"/> and
/// <see cref="ICaptureSourceObserver.OnDecodeError"/> only fire for packets that survived
/// stream reassembly. These counters cover everything that never got that far -- a
/// continuation with no observed SYN, a tuple the OS never confirmed, a stream that aged out --
/// and are what tells "nothing arrived" apart from "everything arrived and was thrown away"
/// (docs/capture-diagnostics.md section 5).
/// </summary>
/// <param name="RawPackets">IPv4/TCP frames read from the adapter and structurally accepted.</param>
/// <param name="DroppedNoStream">Frames dropped because their tuple had no tracked stream.</param>
/// <param name="DroppedNoSyn">Frames dropped because their direction never showed its own SYN.</param>
/// <param name="ExpiredStreams">Streams released after waiting too long for ownership.</param>
/// <param name="UnconfirmedTuples">Distinct tuples the OS TCP table never confirmed as the game's.</param>
/// <param name="StreamResets">Directions abandoned after a gap could not be filled in time.</param>
/// <param name="AdapterDropped">Packets Npcap reported as lost, at the driver or the interface.</param>
/// <param name="Handshakes">Streams opened because their TCP handshake was observed, any program.</param>
/// <param name="GameConnections">Distinct connections the OS attributed to the game since the start.</param>
/// <param name="GameConnectionsNow">Connections the OS attributes to the game in the latest reading.</param>
public sealed record CaptureIngressCounters(
    long RawPackets = 0,
    long DroppedNoStream = 0,
    long DroppedNoSyn = 0,
    long ExpiredStreams = 0,
    long UnconfirmedTuples = 0,
    long StreamResets = 0,
    long AdapterDropped = 0,
    long Handshakes = 0,
    long GameConnections = 0,
    long GameConnectionsNow = 0)
{
    /// <summary>Nothing measured: the reading of a source that does not observe an adapter.</summary>
    public static CaptureIngressCounters Empty { get; } = new();

    /// <summary>Frames that reached the reassembler and were discarded there.</summary>
    public long DroppedBeforeDecode => DroppedNoStream + DroppedNoSyn;

    /// <summary>True when frames arrived and every one of them was discarded before decoding.</summary>
    public bool AllDroppedBeforeDecode => RawPackets > 0 && DroppedBeforeDecode >= RawPackets;

    /// <summary>
    /// True when the game opened a connection after this capture started and it still cannot
    /// be read.
    ///
    /// This separates the two situations that otherwise report identically. Below the
    /// threshold the software was started too late and returning to the title screen supplies
    /// the missing handshake. Above it the player already did that - the operating system
    /// listed the new connection - and the handshake was lost on the way in, so repeating the
    /// login achieves nothing.
    /// </summary>
    /// <param name="preexisting">Connections the game already held when capture started.</param>
    public bool ReconnectedUnreadable(int? preexisting) =>
        GameConnections > (preexisting ?? 0);
}

/// <summary>
/// A source of decoded FF14 messages.
///
/// Two implementations exist: <see cref="MachinaCaptureSource"/> over Npcap, and
/// <see cref="FakeCaptureSource"/> for tests. Everything above this interface -- the bounded
/// queue, the controller, the diagnostics, the IPC surface -- is therefore testable on a
/// machine with neither Npcap nor the game.
/// </summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>Short identifier of the implementation, shown in diagnostics.</summary>
    string Kind { get; }

    /// <summary>True while the source is observing.</summary>
    bool IsRunning { get; }

    /// <summary>True when starting this source reads the game executable from disk.</summary>
    bool ReadsGameExecutable { get; }

    /// <summary>
    /// How many TCP connections the game process already had when this source started, or
    /// null when the table could not be read.
    ///
    /// Zero is the good answer: Oodle's TCP decompressor is stateful and per connection, so a
    /// capture that attaches to a connection the client opened earlier can never decode
    /// anything on it. The CN client keeps its zone connection across teleports, making this
    /// the difference between "start the capture, then log in" and a session where every frame
    /// is rejected (docs/live-validation-guide.md section 6).
    /// </summary>
    int? PreexistingTcpConnections => null;

    /// <summary>
    /// What the ingress path saw below the message level, for the three-way distinction
    /// between "no packets", "packets that are not the game's" and "the game's packets that
    /// cannot be decoded". <see cref="CaptureIngressCounters.Empty"/> for a source that does
    /// not read an adapter at all.
    /// </summary>
    CaptureIngressCounters IngressCounters => CaptureIngressCounters.Empty;

    /// <summary>
    /// Which Oodle signature set this source is using, once it has started.
    /// <see cref="OodleSignatureUse.None"/> before a start and for sources that do not
    /// decompress at all.
    /// </summary>
    OodleSignatureUse SignatureUse => OodleSignatureUse.None;

    /// <summary>Starts observing. Throws <c>CollectorException</c> when it cannot.</summary>
    /// <param name="options">Session and adapter selection.</param>
    /// <param name="observer">Receives messages, decode errors and faults.</param>
    void Start(CaptureStartOptions options, ICaptureSourceObserver observer);

    /// <summary>Stops observing and releases everything the source acquired. Idempotent.</summary>
    void Stop();
}

/// <summary>
/// Turns a TCP tuple into a stable, opaque key.
///
/// The pipeline needs to tell one connection from another (the game opens several), but must
/// never carry an address. A truncated SHA-256 over the tuple does both: equal tuples give
/// equal keys within a session, and the key reveals nothing about the network
/// (docs/privacy-boundary.md section 5).
/// </summary>
public static class ConnectionKey
{
    /// <summary>Hex characters kept from the digest.</summary>
    public const int KeyLength = 16;

    /// <summary>Builds a key from the four parts of a TCP tuple plus a session salt.</summary>
    /// <param name="captureSessionId">Session identifier, so keys do not correlate across runs.</param>
    /// <param name="localIp">Local address as the capture layer saw it.</param>
    /// <param name="localPort">Local port.</param>
    /// <param name="remoteIp">Remote address as the capture layer saw it.</param>
    /// <param name="remotePort">Remote port.</param>
    public static string From(
        string captureSessionId, uint localIp, ushort localPort, uint remoteIp, ushort remotePort)
    {
        var text = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{captureSessionId}|{localIp}|{localPort}|{remoteIp}|{remotePort}");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(digest).ToLowerInvariant()[..KeyLength];
    }
}
