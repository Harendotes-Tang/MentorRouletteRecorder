using System.Globalization;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// Renders capture state into the shapes contracts/ipc-v1.schema.json declares.
///
/// This is the last gate before capture facts reach another process, so it is also where the
/// privacy rules are enforced: addresses are already masked by
/// <see cref="AdapterEnumerator"/> and never unmasked here, and no filesystem path, MAC
/// address or packet byte is rendered by any method in this class
/// (docs/capture-diagnostics.md section 3).
/// </summary>
public static class CaptureWire
{
    /// <summary>Installation guidance shown when Npcap is missing.</summary>
    public const string InstallHint = NpcapDetector.NotInstalledGuidance;

    /// <summary>Builds a <c>$defs/CaptureStatus</c> object.</summary>
    /// <param name="snapshot">Diagnostics reading to render.</param>
    public static JsonObject CaptureStatus(CaptureDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var status = new JsonObject
        {
            ["state"] = EnumWire<CaptureState>.Format(snapshot.ContractState),
            ["capture_session_id"] = snapshot.CaptureSessionId,
            ["npcap_installed"] = snapshot.Npcap.Installed,
            ["npcap_version"] = snapshot.Npcap.Version,
            ["ffxiv_running"] = snapshot.Game.Running,
            ["ffxiv_process_id"] = snapshot.Game.ProcessId,
            ["game_build"] = snapshot.Game.GameBuild,
            ["region"] = EnumWire<Region>.Format(snapshot.Game.Region),
            ["profile_status"] = EnumWire<ProfileStatus>.Format(snapshot.Profile.Status),
            ["candidate_validation_enabled"] = snapshot.CandidateValidationEnabled,
            ["candidate_profile_id"] = snapshot.CandidateProfileId,
            ["candidate_observation_count"] = snapshot.CandidateObservationCount,
            ["candidate_hypotheses"] = new JsonArray(
                snapshot.CandidateHypotheses.Select(h => (JsonNode?)h.Wire()).ToArray()),
            ["profile_origin"] = CalibrationWire.Origin(snapshot.ProfileOrigin),
            ["calibration_bound_at_utc"] = UtcTimestamp.ToTextOrNull(snapshot.Calibration.BoundAtUtc),
            ["calibration"] = CalibrationWire.Status(snapshot.Calibration),
            ["adapter_id"] = snapshot.AdapterId,
            ["started_at_utc"] = UtcTimestamp.ToTextOrNull(snapshot.StartedAtUtc),
            ["packets_observed"] = snapshot.PacketsObserved,

            // What the adapter delivered, before reassembly threw anything away. Zero here
            // with a RUNNING capture means the packets are not on this card at all, which no
            // other counter on this page can say (docs/capture-diagnostics.md section 5).
            ["raw_packets_observed"] = snapshot.RawPacketsObserved,
            ["preexisting_connections"] = snapshot.PreexistingConnections,
            ["silent_reason"] = EnumWire<CaptureSilentReason>.Format(snapshot.SilentReason),
            ["ingress"] = new JsonObject
            {
                ["dropped_no_stream"] = snapshot.Ingress.DroppedNoStream,
                ["dropped_no_syn"] = snapshot.Ingress.DroppedNoSyn,
                ["expired_streams"] = snapshot.Ingress.ExpiredStreams,
                ["unconfirmed_tuples"] = snapshot.Ingress.UnconfirmedTuples,
                ["stream_resets"] = snapshot.Ingress.StreamResets,
                ["adapter_dropped"] = snapshot.Ingress.AdapterDropped,
                ["handshakes"] = snapshot.Ingress.Handshakes,
                ["game_connections"] = snapshot.Ingress.GameConnections,
                ["game_connections_now"] = snapshot.Ingress.GameConnectionsNow,
            },

            // Driver loss counts as dropped: from the user's side a packet lost in the kernel
            // and one lost in our queue are the same missing record.
            ["packets_dropped"] = snapshot.DroppedCount + snapshot.Ingress.AdapterDropped,
            ["queue_depth"] = snapshot.QueueDepth,
            ["queue_capacity"] = snapshot.QueueCapacity,
            ["last_error_code"] = LastErrorCode(snapshot),

            // The pipeline counters, live on the diagnostics page so that "packets arrive but
            // nothing is recorded" is a number the user can read rather than a guess
            // (docs/capture-diagnostics.md section 5.1).
            ["connection_count"] = snapshot.ConnectionCount,
            ["messages_decoded"] = snapshot.MessagesDecoded,
            ["decode_errors"] = snapshot.DecodeErrors,
            ["parse_ok_count"] = snapshot.ParseOkCount,
            ["parse_fail_count"] = snapshot.ParseFailCount,
            ["duplicate_count"] = snapshot.DuplicateCount,
            ["ignored_count"] = snapshot.IgnoredCount,
            ["message_rate_per_second"] = Math.Round(snapshot.MessageRatePerSecond, 3),
            ["uptime_ms"] = snapshot.UptimeMs,
            ["last_valid_event_at_utc"] = UtcTimestamp.ToTextOrNull(snapshot.LastValidEventAtUtc),
            ["last_valid_event_kind"] = snapshot.LastValidEventKind,
            ["recent_parser_errors"] = ParserErrors(snapshot.RecentParserErrors),

            // Both are constants of the hard boundary, reported so the user can verify at
            // runtime that no injected hook and no raw-socket fallback exists.
            ["monitor_type"] = CaptureDiagnosticsSnapshot.MonitorType,
            ["injected_hook_enabled"] = CaptureDiagnosticsSnapshot.InjectedHookEnabled,

            // The Oodle stream is stateful and per connection, so "attached too late" is a
            // distinct failure from "nothing is arriving" and has a different fix; it is
            // reported rather than left to be inferred from a decode-error counter
            // (docs/live-validation-guide.md section 6).
            ["hint"] = snapshot.Hint,
            ["oodle_signature_source"] = snapshot.OodleSignature.Source,
            ["oodle_profile_id"] = snapshot.OodleSignature.ProfileId,
            ["oodle_profile_status"] = snapshot.OodleSignature.Status,
        };

        // Absent means "not measured", per the contract: a capture that is not running has
        // measured nothing, so false would be an unsupported claim.
        if (snapshot.MidstreamSuspected is { } midstream)
        {
            status["midstream_suspected"] = midstream;
        }

        return status;
    }

    /// <summary>
    /// Builds the <c>recent_parser_errors</c> array: the tail of the parser's bounded ring,
    /// oldest first, capped at <see cref="CaptureDiagnosticsSnapshot.MaxRecentParserErrors"/>.
    ///
    /// The opcode is rendered as a hex string because that is how a profile spells it, and
    /// the message is copied verbatim from the parser, which by construction never puts a
    /// payload byte in one (see <c>ProfileMessageParser.Fail</c> and
    /// tests/Collector.UnitTests/CaptureDiagnosticsTests.cs).
    /// </summary>
    /// <param name="errors">Refusals to render.</param>
    public static JsonArray ParserErrors(IReadOnlyList<ParserErrorView> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var rows = new JsonArray();
        var skip = Math.Max(0, errors.Count - CaptureDiagnosticsSnapshot.MaxRecentParserErrors);
        for (var i = skip; i < errors.Count; i++)
        {
            var error = errors[i];
            rows.Add(new JsonObject
            {
                ["at_utc"] = UtcTimestamp.ToTextOrNull(error.AtUtc),
                ["code"] = error.Code,
                ["opcode"] = "0x" + error.Opcode.ToString("X4", CultureInfo.InvariantCulture),
                ["direction"] = error.Direction,
                ["message"] = error.Message,
            });
        }

        return rows;
    }

    /// <summary>Builds a <c>Responses/ListCaptureAdapters</c> object.</summary>
    /// <param name="snapshot">Diagnostics reading, for the Npcap facts.</param>
    /// <param name="adapters">Adapters to list.</param>
    public static JsonObject Adapters(
        CaptureDiagnosticsSnapshot snapshot, IReadOnlyList<CaptureAdapterView> adapters)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(adapters);

        var rows = new JsonArray();
        foreach (var adapter in adapters)
        {
            rows.Add(Adapter(adapter));
        }

        return new JsonObject
        {
            ["npcap_installed"] = snapshot.Npcap.Installed,
            ["npcap_version"] = snapshot.Npcap.Version,
            ["install_hint"] = snapshot.Npcap.Usable ? null : snapshot.Npcap.Guidance,
            ["adapters"] = rows,
        };
    }

    /// <summary>Builds one <c>$defs/CaptureAdapter</c> object.</summary>
    /// <param name="adapter">Adapter to render.</param>
    public static JsonObject Adapter(CaptureAdapterView adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        var addresses = new JsonArray();
        foreach (var masked in adapter.MaskedIPv4)
        {
            // Already masked to a /24 by AdapterEnumerator. The unmasked address never leaves
            // this process.
            addresses.Add(masked);
        }

        return new JsonObject
        {
            ["adapter_id"] = adapter.Id,
            ["description"] = adapter.Description,
            ["friendly_name"] = adapter.FriendlyName,
            ["ipv4_addresses"] = addresses,
            ["is_loopback"] = adapter.IsLoopback,
            ["is_up"] = adapter.IsUp,
            ["recommended"] = adapter.Recommended,

            // True for the adapter the user chose earlier that no longer carries the game's
            // traffic while another one does -- the 加速器/VPN case. The UI can then say so
            // instead of silently capturing on a card with nothing on it.
            ["preference_stale"] = adapter.PreferenceStale,
        };
    }

    /// <summary>Builds a <c>$defs/ProtocolProfileStatus</c> object.</summary>
    /// <param name="profile">Profile status to render.</param>
    public static JsonObject Profile(ProfileStatusSnapshot profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new JsonObject
        {
            ["status"] = EnumWire<ProfileStatus>.Format(profile.Status),
            ["profile_id"] = profile.ProfileId,
            ["region"] = EnumWire<Region>.Format(profile.Region),
            ["game_build"] = profile.GameBuild,
            ["verified_at_utc"] = UtcTimestamp.ToTextOrNull(profile.VerifiedAtUtc),
            ["evidence_note"] = profile.EvidenceNote,
            ["message"] = profile.Message,
        };
    }

    /// <summary>
    /// The four facts <c>GetStatus</c> adds beyond the contract's <c>CollectorStatus</c>: the
    /// game, Npcap, and the two Oodle disclosures the first-run page must show.
    /// </summary>
    /// <param name="snapshot">Diagnostics reading to render.</param>
    public static JsonObject StatusExtras(CaptureDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new JsonObject
        {
            ["game"] = new JsonObject
            {
                ["running"] = snapshot.Game.Running,
                ["process_id"] = snapshot.Game.ProcessId,
                ["region"] = EnumWire<Region>.Format(snapshot.Game.Region),
                ["game_build"] = snapshot.Game.GameBuild,
                ["instance_count"] = snapshot.Game.InstanceCount,
                ["install_path_readable"] = snapshot.GameExecutableKnown,
            },
            ["npcap"] = new JsonObject
            {
                ["status"] = snapshot.Npcap.StatusToken,
                ["installed"] = snapshot.Npcap.Installed,
                ["version"] = snapshot.Npcap.Version,
                ["winpcap_compatible"] = snapshot.Npcap.WinPcapCompatible,
                ["admin_only"] = snapshot.Npcap.AdminOnly,
                ["process_elevated"] = snapshot.Npcap.Elevated,
                ["install_hint"] = snapshot.Npcap.Usable ? null : snapshot.Npcap.Guidance,
            },

            // DEC-OODLE-01 requires this to be visible, not buried in the implementation:
            // the default mode loads a copy of the game executable into our own process.
            ["oodle_mode"] = snapshot.Oodle.ToString(),
            ["reads_game_executable"] = snapshot.ReadsGameExecutable,
        };
    }

    /// <summary>
    /// Chooses the value of <c>last_error_code</c>.
    ///
    /// While capture is healthy this is null. When it cannot run at all the code is the
    /// sentinel <c>UNAVAILABLE</c>, which is what the diagnostics page keys on to explain
    /// that this machine has no usable Npcap rather than that a capture merely stopped.
    /// </summary>
    /// <param name="snapshot">Diagnostics reading.</param>
    public static string? LastErrorCode(CaptureDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.State == CaptureControllerState.Running && snapshot.LastErrorCode is null)
        {
            return null;
        }

        return snapshot.State == CaptureControllerState.Unavailable
            ? snapshot.LastErrorCode ?? "UNAVAILABLE"
            : snapshot.LastErrorCode;
    }
}
