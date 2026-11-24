using System.Text.Json;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The shipped GLOBAL profile for client build 2026.11.03 (2026-11-22), written from a tester's
/// four files: the match notice 0x03bf carries the roulette, the zone change 0x00ab carries the
/// territory, and the queue echo 0x016f carries the job. Every constant is pinned here, so a
/// silent edit of an opcode, an offset or a constraint fails a test before it reaches a player.
/// </summary>
public sealed class GlobalProfileTests
{
    private const string ProfileId = "global.2026.11.03";
    private const string Build = "2026.11.03.0000.0000";

    private static ProtocolProfile Shipped()
    {
        var entry = Assert.Single(ProfileCatalog.LoadDefault().Entries, entry => entry.Report.ProfileId == ProfileId);
        Assert.True(entry.Report.Ok, string.Join("; ", entry.Report.Errors.Select(error => error.Message)));
        return Assert.IsType<ProtocolProfile>(entry.Profile);
    }

    [Fact]
    public void ShippedGlobalProfile_IsVerifiedFromPopToExitWithoutADutyResult()
    {
        var profile = Shipped();

        Assert.Equal(ProfileCompatibilityStatus.Verified, profile.Status);
        Assert.Equal(Region.Global, profile.Region);
        Assert.Equal(Build, profile.GameBuild);
        Assert.Equal(9, profile.MentorRouletteId);
        Assert.Equal(TimeSpan.FromSeconds(120), profile.MatchWindow);
        Assert.False(profile.MatchFromQueue);
        Assert.Equal(
            new[] { "CONTENT_FINDER_POP", "ZONE_INITIALIZATION", "PLAYER_JOB" },
            profile.Messages.Select(m => m.Name).ToArray());

        var pop = profile.Message("CONTENT_FINDER_POP")!;
        Assert.Equal(0x03bf, pop.Opcode);
        Assert.Equal(PacketDirection.ServerToClient, pop.Direction);
        Assert.Equal(40, pop.ExpectedLength);
        var roulette = Assert.Single(pop.Fields);
        Assert.Equal(("roulette_id", 2, ProfileFieldType.U16, ProfileFieldRole.Value),
            (roulette.Name, roulette.Offset, roulette.Type, roulette.Role));
        Assert.Equal(new ProfileFieldConstraints(0, 255, null), roulette.Constraints);

        var zone = profile.Message("ZONE_INITIALIZATION")!;
        Assert.Equal(0x00ab, zone.Opcode);
        Assert.Equal(136, zone.ExpectedLength);
        var territory = Assert.Single(zone.Fields);
        Assert.Equal(("territory_id", 2, ProfileFieldType.U16), (territory.Name, territory.Offset, territory.Type));
        Assert.Equal(new ProfileFieldConstraints(1, null, null), territory.Constraints);
        Assert.True(profile.KnowsDutyTerritory);

        var job = profile.Message("PLAYER_JOB")!;
        Assert.Equal(0x016f, job.Opcode);
        Assert.Equal(40, job.ExpectedLength);
        var state = job.Field("finder_state")!;
        Assert.Equal((0, ProfileFieldRole.Selector), (state.Offset, state.Role));
        Assert.Equal(new long[] { 1 }, state.Constraints.In);
        var jobId = job.Field("job_id")!;
        Assert.Equal((1, ProfileFieldType.U8, 1L, 43L), (jobId.Offset, jobId.Type, jobId.Constraints.Min, jobId.Constraints.Max));

        var binding = profile.ToBinding();
        Assert.True(binding.IsUsable);
        Assert.False(binding.IsSynthetic);
        Assert.False(binding.CanDetectDutyResult);
        Assert.True(binding.ObservesDutyClear);
        Assert.True(profile.ObservesDutyStart);

        // Every constant names the files it rests on; none is a synthetic stand-in.
        using var json = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(ProfileCatalog.FindDefaultRoot()!, "global", ProfileId + ".json")));
        var evidence = json.RootElement.GetProperty("provenance").GetProperty("evidence").EnumerateArray()
            .Select(item => (Field: item.GetProperty("field").GetString()!, Method: item.GetProperty("method").GetString()!))
            .ToArray();
        Assert.DoesNotContain(evidence, item => item.Method == "SYNTHETIC");
        foreach (var field in new[]
        {
            "messages.CONTENT_FINDER_POP.opcode", "messages.CONTENT_FINDER_POP.fields.roulette_id",
            "messages.ZONE_INITIALIZATION.opcode", "messages.ZONE_INITIALIZATION.fields.territory_id",
            "messages.PLAYER_JOB.opcode", "messages.PLAYER_JOB.fields.job_id",
            "mentor_roulette_id", "match_window_seconds",
            ProfileLoader.CalibrationEvidenceKey, ProfileLoader.CalibrationReplyEvidenceKey,
        })
        {
            Assert.Contains(evidence, item => item.Field == field);
        }
    }

    /// <summary>
    /// The calibration template a later GLOBAL build re-learns its opcodes from (2026-11-22): the request
    /// is 24 bytes with the roulette at byte 0, and the reply is a 40-byte message of its own with the
    /// roulette at byte 16 that is also the job message - the popup is the other 40-byte message, found
    /// by the pop's shape (docs/protocol-profile-format.md section 11.1).
    /// </summary>
    [Fact]
    public void ShippedGlobalProfile_CarriesATwoMessageCalibrationTemplate()
    {
        var calibration = Assert.IsType<ProfileCalibration>(Shipped().Calibration);

        Assert.Equal((PacketDirection.ClientToServer, 24, 0, ProfileFieldType.U8),
            (calibration.FinderRequest.Direction, calibration.FinderRequest.ExpectedLength,
                calibration.FinderRequest.RouletteField.Offset, calibration.FinderRequest.RouletteField.Type));
        Assert.Equal(new ProfileFieldConstraints(1, 255, null), calibration.FinderRequest.RouletteField.Constraints);
        Assert.Equal(TimeSpan.FromSeconds(1), calibration.FinderReplyMax);
        var reply = Assert.IsType<CalibrationFinderReply>(calibration.FinderReply);
        Assert.Equal((PacketDirection.ServerToClient, 40, 16, ProfileFieldType.U8, true),
            (reply.Direction, reply.ExpectedLength, reply.RouletteField.Offset, reply.RouletteField.Type, reply.CarriesPlayerJob));
        Assert.Equal(new ProfileFieldConstraints(1, 255, null), reply.RouletteField.Constraints);

        var template = Assert.IsType<CalibrationTemplate>(CalibrationTemplate.Select(ProfileCatalog.LoadDefault(), Region.Global));
        Assert.Equal(ProfileId, template.Source.ProfileId);
        Assert.True(template.SeparateReply);
        Assert.True(template.ReplyCarriesJob);
        Assert.True(template.TerritoryInZone);
        Assert.Equal(0x03bf, template.Pop.Opcode);
    }

    /// <summary>
    /// The profile records its own build and nothing else: another GLOBAL build is told that no
    /// profile matches it (a region that does record), which is what arms the calibration the
    /// template above makes possible.
    /// </summary>
    [Fact]
    public void TheGlobalProfileIsChosenForItsOwnBuildOnly()
    {
        var selector = new ProfileSelector(ProfileCatalog.LoadDefault());

        var selected = selector.Select(Region.Global, Build);
        Assert.Equal(ProfileSelectionReason.Selected, selected.ReasonCode);
        Assert.True(selected.IsUsable);
        Assert.Equal(ProfileId, selected.Profile!.ProfileId);

        var later = selector.Select(Region.Global, "2026.12.01.0000.0000");
        Assert.Equal(
            (ProfileSelectionReason.NoMatch, ProfileSelector.NoProfileMatchesReason), (later.ReasonCode, later.Reason));
        Assert.False(selector.Select(Region.Cn, Build).IsUsable);
        Assert.False(selector.Select(Region.Tc, Build).IsUsable);
    }

    /// <summary>
    /// The three messages read as the testers' rounds showed them: the notice gives the roulette
    /// (9 for the mentor roulette, 5 for Expert), the zone change gives the territory, and only the
    /// queue echo whose first byte is 1 gives a job - any other state of it is ignored, not failed.
    /// </summary>
    [Fact]
    public void TheParserReadsTheRouletteTheTerritoryAndTheJobWhereTheRoundsFoundThem()
    {
        var sink = new RecordingSemanticSink();
        var parser = new ProfileMessageParser(Shipped(), sink);

        parser.Accept(Message(1, 0x016f, CalibrationObserverTests.Bytes(40, (0, 1), (1, 19), (16, 5))));
        parser.Accept(Message(2, 0x03bf, CalibrationObserverTests.Bytes(40, (0, 3), (2, 5))));
        parser.Accept(Message(3, 0x03bf, CalibrationObserverTests.Bytes(40, (0, 3), (2, 9))));
        parser.Accept(Message(4, 0x00ab, CalibrationObserverTests.Bytes(136, (2, 0x41), (3, 0x05), (6, 0xf3), (7, 0x03))));
        parser.Accept(Message(5, 0x016f, CalibrationObserverTests.Bytes(40, (0, 2), (1, 0xff))));

        Assert.Equal(19, Assert.Single(sink.Events.OfType<PlayerJob>()).JobId);
        Assert.Equal(new[] { 5, 9 }, sink.Events.OfType<ContentFinderPop>().Select(pop => pop.RouletteId).ToArray());
        Assert.Equal(1345, Assert.Single(sink.Events.OfType<ZoneInitialization>()).TerritoryId);
        var stats = parser.GetParserStats();
        Assert.Equal(0, stats.ParseFailed);
        Assert.Equal(1, stats.Ignored);

        // A duty queued directly is expected to carry no roulette: an ordinary pop of no roulette,
        // which never opens a run, rather than a parse failure on the capture page.
        parser.Accept(Message(6, 0x03bf, CalibrationObserverTests.Bytes(40, (0, 3))));
        Assert.Equal(new[] { 5, 9, 0 }, sink.Events.OfType<ContentFinderPop>().Select(pop => pop.RouletteId).ToArray());
        Assert.Equal(0, parser.GetParserStats().ParseFailed);
    }

    /// <summary>
    /// Values no round has shown are refused rather than read: a roulette wider than one byte, a
    /// job past the last one, a zone change without a territory. Each is a parse failure, so a
    /// shifted field shows on the capture page instead of filing a run under a wrong duty or job.
    /// </summary>
    [Fact]
    public void ValuesOutsideTheObservedRangesAreRefused()
    {
        var sink = new RecordingSemanticSink();
        var parser = new ProfileMessageParser(Shipped(), sink);

        parser.Accept(Message(1, 0x03bf, CalibrationObserverTests.Bytes(40, (0, 3), (3, 1))));
        parser.Accept(Message(2, 0x016f, CalibrationObserverTests.Bytes(40, (0, 1), (1, 44))));
        parser.Accept(Message(3, 0x00ab, CalibrationObserverTests.Bytes(136)));

        Assert.Empty(sink.Events);
        Assert.Equal(3, parser.GetParserStats().ParseFailed);
    }

    private static DecodedMessage Message(int second, ushort opcode, byte[] payload)
    {
        var mono = TimeSpan.FromSeconds(second);
        return new DecodedMessage("global-session", MessageDirection.Inbound,
            new DateTimeOffset(2026, 11, 22, 9, 24, 0, TimeSpan.Zero).Add(mono), mono, second * 1000L,
            3, opcode, payload, "zone");
    }
}
