using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Calibration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// One candidate hypothesis as the Desktop sees it (<c>$defs/CandidateHypothesis</c>).
/// Cosmetic projection only: nothing here feeds matching or evidence.
/// </summary>
/// <param name="ProfileId">Candidate profile the hypothesis belongs to.</param>
/// <param name="Name">Hypothesis name inside the profile.</param>
/// <param name="Label">Display name; the profile's <c>label</c>, else the name.</param>
/// <param name="Opcode">Opcode as the whitelist token, <c>0x</c> plus four lower-case hex digits.</param>
/// <param name="Direction"><c>C2S</c> or <c>S2C</c>.</param>
/// <param name="Group">Observation group, or null.</param>
/// <param name="ExpectedLength">Exact payload length, or null for a range.</param>
/// <param name="ResearchEligible">Whether <see cref="ResearchPayloadPolicy"/> would accept it on the whitelist.</param>
public sealed record CandidateHypothesisView(
    string ProfileId,
    string Name,
    string Label,
    string Opcode,
    string Direction,
    string? Group,
    int? ExpectedLength,
    bool ResearchEligible)
{
    /// <summary>Projects a hypothesis of <paramref name="profile"/>.</summary>
    public static CandidateHypothesisView From(
        Protocol.Profiles.ProtocolProfile profile, Protocol.Profiles.ProfileHypothesis hypothesis)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(hypothesis);
        return new CandidateHypothesisView(
            profile.ProfileId,
            hypothesis.Name,
            hypothesis.DisplayLabel,
            "0x" + hypothesis.Opcode.ToString("x4", System.Globalization.CultureInfo.InvariantCulture),
            hypothesis.Direction == Domain.Events.PacketDirection.ServerToClient ? "S2C" : "C2S",
            hypothesis.Group,
            hypothesis.ExpectedLength,
            ResearchPayloadPolicy.IsEligible(profile, hypothesis));
    }

    /// <summary>Renders the wire object.</summary>
    public JsonObject Wire() => new()
    {
        ["profile_id"] = ProfileId,
        ["name"] = Name,
        ["label"] = Label,
        ["opcode"] = Opcode,
        ["direction"] = Direction,
        ["group"] = Group,
        ["expected_length"] = ExpectedLength,
        ["research_eligible"] = ResearchEligible,
    };
}

/// <summary>
/// Lifecycle of the capture controller. Finer-grained than the contract's
/// <c>$defs/CaptureState</c>, which has no <c>STOPPING</c> and folds "never possible here"
/// together with "stopped". <see cref="CaptureDiagnosticsSnapshot.ContractState"/> does the
/// mapping.
/// </summary>
public enum CaptureControllerState
{
    /// <summary>Capture cannot run on this machine: no usable Npcap.</summary>
    Unavailable,

    /// <summary>Capture could run and is not running.</summary>
    Idle,

    /// <summary>A start is in progress.</summary>
    Starting,

    /// <summary>Observing.</summary>
    Running,

    /// <summary>A stop is in progress.</summary>
    Stopping,

    /// <summary>The monitor failed; the last run is left to the state machine's interruption handling.</summary>
    Faulted,
}

/// <summary>
/// Why a running capture is producing nothing.
///
/// "RUNNING, 0 parse failures, no valid event" is the single worst thing this software can
/// display, because it reads as healthy. These four values are the answers the user can act
/// on, and each one has a different action (docs/capture-diagnostics.md section 5.5).
/// </summary>
public enum CaptureSilentReason
{
    /// <summary>Nothing is wrong, or not enough has been observed yet to say anything.</summary>
    None,

    /// <summary>
    /// Capture attached after the client had already opened its connection. Oodle's TCP
    /// decompressor is stateful and per connection, so this one can never be decoded.
    /// </summary>
    Midstream,

    /// <summary>No game traffic at all on the selected adapter: wrong card, a VPN or a 加速器.</summary>
    NoPacketsOnAdapter,

    /// <summary>Traffic arrives, but the OS never confirms those connections belong to the game.</summary>
    NoStreamOwnership,
}

/// <summary>
/// Everything the diagnostics page shows, taken in one consistent reading.
///
/// Exactly the field list of docs/capture-diagnostics.md section 4, plus the two facts the
/// Oodle decision requires be disclosed. Nothing in here is persisted, and the two fields
/// that could identify a machine -- <see cref="AdapterId"/> and
/// <see cref="GameExecutableKnown"/>'s underlying path -- never reach the sanitized report.
/// </summary>
public sealed record CaptureDiagnosticsSnapshot
{
    /// <summary>
    /// What live capture has been verified to do for the shipped CN profile
    /// (docs/live-validation-guide.md section 0): pops and zone changes are recorded from
    /// real traffic; the duty result is not, so exits close as UNKNOWN pending review.
    /// </summary>
    public const string LiveCaptureStatus = "VERIFIED_POP_TO_EXIT";

    /// <summary>Controller lifecycle state.</summary>
    public required CaptureControllerState State { get; init; }

    /// <summary>Session identifier of the capture in flight, when there is one.</summary>
    public string? CaptureSessionId { get; init; }

    /// <summary>Npcap verdict.</summary>
    public required NpcapDetection Npcap { get; init; }

    /// <summary>Game process verdict.</summary>
    public required GameProcessDetection Game { get; init; }

    /// <summary>Protocol profile in effect.</summary>
    public required ProfileStatusSnapshot Profile { get; init; }

    /// <summary>Whether the independent candidate observer is enabled.</summary>
    public bool CandidateValidationEnabled { get; init; }

    /// <summary>Candidate profile selected for observation; unrelated to the formal profile verdict.</summary>
    public string? CandidateProfileId { get; init; }

    /// <summary>Number of observations retained in the independent candidate ledger.</summary>
    public int CandidateObservationCount { get; init; }

    /// <summary>
    /// Hypotheses the candidate profile for this client declares, so the Desktop can label
    /// the research whitelist by purpose instead of showing bare opcodes. Empty when no
    /// candidate profile matches; unrelated to whether candidate validation is switched on.
    /// </summary>
    public IReadOnlyList<CandidateHypothesisView> CandidateHypotheses { get; init; } =
        Array.Empty<CandidateHypothesisView>();

    /// <summary>Calibration status for the running build.</summary>
    public CalibrationStatusSnapshot Calibration { get; init; } = CalibrationStatusSnapshot.Idle;

    /// <summary>Where the profile in force came from, or null when none is in force.</summary>
    public ProfileOrigin? ProfileOrigin { get; init; }

    /// <summary>
    /// <c>capture.shared_calibration_enabled</c> as it stands; its default, true, when there are no settings to read
    /// (<c>--capture-doctor</c>). Whether the one outbound request may be made at all (docs/privacy-boundary.md §8.2).
    /// </summary>
    public bool SharedCalibrationEnabled { get; init; } = true;

    /// <summary>True when <c>MR_DISABLE_SHARED_FETCH</c> stops every shared-calibration request of this process before it is sent.</summary>
    public bool SharedFetchKillSwitch { get; init; }

    /// <summary>Opaque identifier of the adapter in use.</summary>
    public string? AdapterId { get; init; }

    /// <summary>Windows name of the adapter in use.</summary>
    public string? AdapterName { get; init; }

    /// <summary>Masked addresses of the adapter in use, for display only.</summary>
    public IReadOnlyList<string> AdapterMaskedIPv4 { get; init; } = Array.Empty<string>();

    /// <summary>Distinct game connections observed this session.</summary>
    public int ConnectionCount { get; init; }

    /// <summary>Messages delivered by the capture source.</summary>
    public long PacketsObserved { get; init; }

    /// <summary>
    /// IPv4/TCP frames the adapter actually delivered, before stream reassembly.
    ///
    /// <see cref="PacketsObserved"/> counts messages that survived reassembly and framing, so
    /// it stays at zero both when nothing arrives and when everything arrives and is dropped.
    /// This one separates those two (docs/capture-diagnostics.md section 5.5).
    /// </summary>
    public long RawPacketsObserved { get; init; }

    /// <summary>Everything ingress discarded, by reason.</summary>
    public CaptureIngressCounters Ingress { get; init; } = CaptureIngressCounters.Empty;

    /// <summary>
    /// TCP connections the game already held when this capture started, or null when the
    /// table could not be read or nothing is running. Above zero means a fresh login is
    /// needed for anything on those connections to be decodable.
    /// </summary>
    public int? PreexistingConnections { get; init; }

    /// <summary>Why this running capture is producing nothing, if it is.</summary>
    public CaptureSilentReason SilentReason { get; init; } = CaptureSilentReason.None;

    /// <summary>Messages whose framing was read successfully.</summary>
    public long MessagesDecoded { get; init; }

    /// <summary>Messages whose framing could not be read.</summary>
    public long DecodeErrors { get; init; }

    /// <summary>Messages the parser understood.</summary>
    public long ParseOkCount { get; init; }

    /// <summary>Messages the parser could not interpret.</summary>
    public long ParseFailCount { get; init; }

    /// <summary>Messages discarded as duplicates.</summary>
    public long DuplicateCount { get; init; }

    /// <summary>Messages no profile message claims: ordinary traffic, neither ok nor failed.</summary>
    public long IgnoredCount { get; init; }

    /// <summary>Messages dropped because the bounded queue was full.</summary>
    public long DroppedCount { get; init; }

    /// <summary>Messages waiting to be parsed.</summary>
    public int QueueDepth { get; init; }

    /// <summary>Capacity of the parser queue.</summary>
    public int QueueCapacity { get; init; }

    /// <summary>When the parser last produced a semantic event.</summary>
    public DateTimeOffset? LastValidEventAtUtc { get; init; }

    /// <summary>Semantic event type of that last event, e.g. <c>DUTY_RESULT</c>; null when there is none.</summary>
    public string? LastValidEventKind { get; init; }

    /// <summary>
    /// The online speech request class as the report may state it (docs/privacy-boundary.md §8.3):
    /// the service token, the kill switch, and when this process last sent a request and how it
    /// ended. Filled in by the IPC layer for an exported report; never an address or a key.
    /// </summary>
    public Speech.OnlineSpeechDiagnostics OnlineSpeech { get; init; } = Speech.OnlineSpeechDiagnostics.None;

    /// <summary>
    /// The most recent parser refusals, oldest first, already reduced to the wire shape.
    /// Bounded by <see cref="MaxRecentParserErrors"/> before it leaves this process.
    /// </summary>
    public IReadOnlyList<ParserErrorView> RecentParserErrors { get; init; } =
        Array.Empty<ParserErrorView>();

    /// <summary>State of the run in flight.</summary>
    public RunState RunState { get; init; } = RunState.Idle;

    /// <summary>Smoothed message rate, per second.</summary>
    public double MessageRatePerSecond { get; init; }

    /// <summary>When the current capture started.</summary>
    public DateTimeOffset? StartedAtUtc { get; init; }

    /// <summary>How long the current capture has been running.</summary>
    public long UptimeMs { get; init; }

    /// <summary>How the Oodle decompressor is obtained.</summary>
    public OodleMode Oodle { get; init; } = OodleMode.FfxivTcp;

    /// <summary>True when the configured Oodle mode reads the game executable from disk.</summary>
    public bool ReadsGameExecutable { get; init; }

    /// <summary>True when the game's install path is readable, so a build can be identified.</summary>
    public bool GameExecutableKnown { get; init; }

    /// <summary>
    /// True when this capture almost certainly attached to a connection the game had already
    /// opened; null when the question has not been measured, which is every moment no capture
    /// is running.
    /// </summary>
    public bool? MidstreamSuspected { get; init; }

    /// <summary>One actionable zh-Hans sentence about the current capture state, or null.</summary>
    public string? Hint { get; init; }

    /// <summary>Which Oodle signature set the running capture is using.</summary>
    public OodleSignatureUse OodleSignature { get; init; } = OodleSignatureUse.None;

    /// <summary>Contract error code of the most recent failure, or null.</summary>
    public string? LastErrorCode { get; init; }


    /// <summary>User-facing message of the most recent failure, free of paths and addresses.</summary>
    public string? LastErrorMessage { get; init; }

    /// <summary>Non-sensitive notes for the diagnostics page.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// How many parser refusals the wire ever carries. The parser keeps a hundred; the
    /// diagnostics page shows the tail of them, and a bounded tail is what keeps a broken
    /// profile from turning every status frame into a kilobyte of repetition.
    /// </summary>
    public const int MaxRecentParserErrors = 20;

    /// <summary>Constant of the hard boundary, reported so the user can verify it at runtime.</summary>
    public static string MonitorType => "WinPCap";

    /// <summary>Constant of the hard boundary. Never true, in any build.</summary>
    public static bool InjectedHookEnabled => false;

    /// <summary>True when capture is running but has lost messages or is close to doing so.</summary>
    public bool IsDegraded =>
        State == CaptureControllerState.Running
        && (DroppedCount > 0 || Ingress.AdapterDropped > 0
            || (QueueCapacity > 0 && QueueDepth * 5 > QueueCapacity * 4));

    /// <summary>
    /// The contract's <c>$defs/CaptureState</c> equivalent of <see cref="State"/>.
    ///
    /// <c>UNAVAILABLE</c> and <c>STOPPING</c> have no bucket in the contract enum; both map to
    /// <c>STOPPED</c>, which is true in the only sense the client acts on -- nothing is being
    /// recorded. <c>UNAVAILABLE</c> stays distinguishable through
    /// <c>last_error_code</c> and <c>npcap_installed</c>.
    /// </summary>
    public CaptureState ContractState => State switch
    {
        CaptureControllerState.Starting => CaptureState.Starting,
        CaptureControllerState.Running => IsDegraded ? CaptureState.Degraded : CaptureState.Running,
        CaptureControllerState.Faulted => CaptureState.Failed,
        _ => CaptureState.Stopped,
    };
}

/// <summary>
/// Builds the "导出脱敏诊断报告" payload: everything a maintainer needs to diagnose a capture
/// problem, and nothing that identifies the user, their machine or their network.
///
/// The rule is a whitelist, not a filter. Only the fields named below are copied, so a field
/// added to the snapshot later cannot leak by default. Specifically excluded: every IP
/// address (including the masked ones shown in the UI), every filesystem path, the adapter's
/// GUID, the character, and any packet content whatsoever
/// (docs/privacy-boundary.md section 5, docs/capture-diagnostics.md section 6).
/// </summary>
public static class SanitizedDiagnosticsReport
{
    /// <summary>Version of this report's shape.</summary>
    /// <remarks>
    /// 2 (shared calibration): <c>boundary.outbound_connections</c>, a constant 0, gave way to <c>boundary.outbound</c>,
    /// and <c>calibration.shared</c> was added.
    /// </remarks>
    public const int ReportVersion = 2;

    /// <summary>Hex characters kept from the adapter digest.</summary>
    public const int AdapterFingerprintLength = 12;

    /// <summary>
    /// IPC protocol version this build speaks. Restated here rather than referenced so the
    /// capture layer keeps no compile-time dependency on the transport.
    /// </summary>
    public const int IpcProtocolVersion = 1;

    /// <summary>Builds the report.</summary>
    /// <param name="snapshot">Diagnostics reading to render.</param>
    /// <param name="collectorVersion">Collector version string.</param>
    /// <param name="generatedAtUtc">Report timestamp.</param>
    public static JsonObject Build(
        CaptureDiagnosticsSnapshot snapshot, string collectorVersion, DateTimeOffset generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new JsonObject
        {
            ["report_version"] = ReportVersion,
            ["generated_at_utc"] = UtcTimestamp.ToText(UtcTimestamp.Truncate(generatedAtUtc)),

            // The Desktop and the Collector ship from one version stamp, so one field names
            // the application build; the IPC version is separate because a mismatched pair is
            // exactly the failure this report has to be able to describe.
            ["app_version"] = collectorVersion,
            ["collector_version"] = collectorVersion,
            ["ipc_protocol_version"] = IpcProtocolVersion,
            ["live_capture_status"] = CaptureDiagnosticsSnapshot.LiveCaptureStatus,

            // Stated in the report itself, not only in the README: every prerequisite in
            // docs/third-party-licenses.md section 7 is met as of 0.2.0.
            ["public_distribution_ready"] = true,

            ["boundary"] = new JsonObject
            {
                ["monitor_type"] = CaptureDiagnosticsSnapshot.MonitorType,
                ["injected_hook_enabled"] = CaptureDiagnosticsSnapshot.InjectedHookEnabled,
                ["listening_ports"] = 0,
                // Replaces report version 1's constant outbound_connections = 0, which stopped
                // being true once shared calibration existed.
                ["outbound"] = Outbound(snapshot),
                ["reads_game_process_memory"] = false,
                ["reads_game_executable"] = snapshot.ReadsGameExecutable,
                ["oodle_mode"] = snapshot.Oodle.ToString(),
            },

            ["npcap"] = new JsonObject
            {
                ["status"] = snapshot.Npcap.StatusToken,
                ["installed"] = snapshot.Npcap.Installed,
                ["version"] = snapshot.Npcap.Version,
                ["winpcap_compatible"] = snapshot.Npcap.WinPcapCompatible,
                ["admin_only"] = snapshot.Npcap.AdminOnly,
                ["process_elevated"] = snapshot.Npcap.Elevated,
            },

            ["game"] = new JsonObject
            {
                ["running"] = snapshot.Game.Running,
                ["process_id"] = snapshot.Game.ProcessId,
                ["region"] = EnumWire<Region>.Format(snapshot.Game.Region),
                ["game_build"] = snapshot.Game.GameBuild,
                ["instance_count"] = snapshot.Game.InstanceCount,
                // How long the client has been up. With the capture's own uptime beside it,
                // "the software was started after the game" need not be asked of the player.
                ["uptime_ms"] = snapshot.Game.StartedAtUtc is { } startedAt
                    ? (long)Math.Max(0, (generatedAtUtc - startedAt).TotalMilliseconds)
                    : (long?)null,
                ["install_path_readable"] = snapshot.GameExecutableKnown,
            },

            // The adapter is reported as a digest, never as its GUID: the GUID is stable
            // across reboots and therefore identifies the machine.
            ["adapter"] = new JsonObject
            {
                ["selected"] = snapshot.AdapterId is not null,
                ["fingerprint"] = Fingerprint(snapshot.AdapterId),
                ["address_count"] = snapshot.AdapterMaskedIPv4.Count,
            },

            ["capture"] = new JsonObject
            {
                ["state"] = snapshot.State.ToString(),
                ["contract_state"] = EnumWire<CaptureState>.Format(snapshot.ContractState),
                ["degraded"] = snapshot.IsDegraded,
                ["uptime_ms"] = snapshot.UptimeMs,
                ["connection_count"] = snapshot.ConnectionCount,
                ["message_rate_per_second"] = Math.Round(snapshot.MessageRatePerSecond, 3),
                ["last_error_code"] = snapshot.LastErrorCode,
                // The code alone is a dead end: ERR_INTERNAL covers a monitor that died, a
                // source that would not release and a protocol thread that would not exit, and
                // a player cannot be asked to send a log file to tell them apart. Run through
                // the same scrubber the log uses; these strings are ours, but the fault path
                // can carry an exception message that is not.
                ["last_error_detail"] = snapshot.LastErrorMessage is { Length: > 0 } detail
                    ? RotatingFileLogger.Sanitize(detail)
                    : null,

                // The three silent-failure classes, so a pasted report answers "why is it
                // running and recording nothing" without a round trip to the user.
                ["silent_reason"] = EnumWire<CaptureSilentReason>.Format(snapshot.SilentReason),
                ["preexisting_connections"] = snapshot.PreexistingConnections,
            },

            ["counters"] = new JsonObject
            {
                ["packets_observed"] = snapshot.PacketsObserved,
                ["raw_packets_observed"] = snapshot.RawPacketsObserved,
                ["dropped_no_stream"] = snapshot.Ingress.DroppedNoStream,
                ["dropped_no_syn"] = snapshot.Ingress.DroppedNoSyn,
                ["expired_streams"] = snapshot.Ingress.ExpiredStreams,
                ["unconfirmed_tuples"] = snapshot.Ingress.UnconfirmedTuples,
                ["stream_resets"] = snapshot.Ingress.StreamResets,
                ["adapter_dropped"] = snapshot.Ingress.AdapterDropped,
                // The three that answer "did the client reconnect while we were watching".
                // game_connections above preexisting_connections means it did, and a report
                // that still decodes nothing is then our fault, not a late start.
                ["handshakes"] = snapshot.Ingress.Handshakes,
                ["game_connections"] = snapshot.Ingress.GameConnections,
                ["game_connections_now"] = snapshot.Ingress.GameConnectionsNow,
                ["messages_decoded"] = snapshot.MessagesDecoded,
                ["decode_errors"] = snapshot.DecodeErrors,
                ["parse_ok"] = snapshot.ParseOkCount,
                ["parse_fail"] = snapshot.ParseFailCount,
                ["ignored"] = snapshot.IgnoredCount,
                ["duplicates"] = snapshot.DuplicateCount,
                ["dropped"] = snapshot.DroppedCount,
                ["queue_depth"] = snapshot.QueueDepth,
                ["queue_capacity"] = snapshot.QueueCapacity,
            },

            ["calibration"] = CalibrationWire.Report(snapshot.Calibration),
            ["profile"] = new JsonObject
            {
                ["origin"] = CalibrationWire.Origin(snapshot.ProfileOrigin),
                ["status"] = EnumWire<ProfileStatus>.Format(snapshot.Profile.Status),
                ["profile_id"] = snapshot.Profile.ProfileId,
                ["region"] = EnumWire<Region>.Format(snapshot.Profile.Region),
                ["game_build"] = snapshot.Profile.GameBuild,
            },

            ["run"] = new JsonObject
            {
                ["state"] = EnumWire<RunState>.Format(snapshot.RunState),
                ["last_valid_event_at_utc"] = UtcTimestamp.ToTextOrNull(snapshot.LastValidEventAtUtc),
                ["last_valid_event_kind"] = snapshot.LastValidEventKind,
            },

            // The same rows the diagnostics page shows, rendered by the same builder, so a
            // pasted report and a screenshot of the page can never disagree.
            ["recent_parser_errors"] = CaptureWire.ParserErrors(snapshot.RecentParserErrors),
        };
    }

    /// <summary>
    /// The one outbound request the software may make, as it stands (docs/privacy-boundary.md §8.2,
    /// docs/capture-diagnostics.md §9): whether the setting allows it, whether the kill switch forbids it, and when
    /// this process last actually sent one and how that ended. Never where to: no address, host or source.
    /// </summary>
    /// <param name="snapshot">Diagnostics reading to render.</param>
    private static JsonObject Outbound(CaptureDiagnosticsSnapshot snapshot) => new()
    {
        ["shared_calibration_enabled"] = snapshot.SharedCalibrationEnabled,
        ["kill_switch"] = snapshot.SharedFetchKillSwitch,
        ["last_fetch_utc"] = UtcTimestamp.ToTextOrNull(snapshot.Calibration.Shared.LastSentAtUtc),
        ["last_fetch_status"] = snapshot.Calibration.Shared.LastSentStatus is { } status
            ? EnumWire<Protocol.Sharing.SharedFetchStatus>.Format(status)
            : null,

        // The second request class (§8.3). Which service, never which address; no key, no text.
        ["online_speech"] = new JsonObject
        {
            ["provider"] = snapshot.OnlineSpeech.Provider,
            ["kill_switch"] = snapshot.OnlineSpeech.KillSwitch,
            ["last_request_utc"] = UtcTimestamp.ToTextOrNull(snapshot.OnlineSpeech.LastRequestAtUtc),
            ["last_outcome"] = snapshot.OnlineSpeech.LastOutcome,
        },
    };

    /// <summary>Digests an adapter identifier so it can be compared but not resolved.</summary>
    /// <param name="adapterId">Adapter identifier, or null.</param>
    public static string? Fingerprint(string? adapterId)
    {
        if (string.IsNullOrWhiteSpace(adapterId))
        {
            return null;
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(adapterId));
        return Convert.ToHexString(digest).ToLowerInvariant()[..AdapterFingerprintLength];
    }
}
