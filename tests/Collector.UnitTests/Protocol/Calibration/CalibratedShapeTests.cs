using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// "Template + learned values -> profile messages" as one transform.
/// The draft writes a profile with it and a share code rebuilds one with it, so a code
/// can only ever describe what the draft itself could have written.
/// </summary>
public sealed class CalibratedShapeTests
{
    private static string Json(IEnumerable<ProfileMessage> messages) =>
        string.Join("\n", messages.Select(message => CalibratedProfileDocument.Message(message).ToJsonString()));

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var name in CalibrationTrafficCases.All)
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>
    /// Whatever the draft declared reads back as a handful of learned values, and those values
    /// rebuild exactly the draft's messages. Share codes rest on this property.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryReadyDraftReadsBackAsLearnedValuesThatRebuildIt(string name)
    {
        var template = CalibrationObserverTests.Template();
        var draft = CalibrationTrafficCases.Derive(name);

        var values = CalibratedShape.Read(template, draft.Messages);

        Assert.NotNull(values);
        Assert.Equal(CalibrationTrafficCases.Source(name), values!.MatchSource);
        var rebuilt = CalibratedShape.Messages(template, values);
        Assert.Null(rebuilt.Error);
        // Apart from the announcement, which a share code deliberately does not carry: the
        // opcode is named by this machine's timing evidence, which does not travel with the
        // code, so a profile holding one shares as the plain queue-request profile underneath.
        Assert.Equal(
            Json(draft.Messages.Where(message => message.Name != "MATCH_ANNOUNCED").ToArray()),
            Json(rebuilt.Messages));
        Assert.True(CalibratedShape.IsComplete(values.MatchSource, rebuilt.Messages));
    }

    /// <summary>What each source needs from the traffic; everything else comes from the template.</summary>
    [Fact]
    public void EachSourceNeedsExactlyTheValuesItsTransformReads()
    {
        Assert.Equal(CalibratedPopInputs.Opcode | CalibratedPopInputs.Selectors,
            CalibratedShape.InputsFor(CalibrationMatchSource.ReplyState));
        Assert.Equal(CalibratedPopInputs.Opcode | CalibratedPopInputs.Length,
            CalibratedShape.InputsFor(CalibrationMatchSource.Announcement));
        Assert.Equal(CalibratedPopInputs.Opcode | CalibratedPopInputs.Length | CalibratedPopInputs.RouletteOffset,
            CalibratedShape.InputsFor(CalibrationMatchSource.MarkerOffset));
        Assert.Equal(CalibratedPopInputs.Opcode, CalibratedShape.InputsFor(CalibrationMatchSource.QueueRequest));
        Assert.True(CalibratedShape.RequiresTerritory(CalibrationMatchSource.QueueRequest));
        Assert.False(CalibratedShape.RequiresTerritory(CalibrationMatchSource.Announcement));
    }

    [Fact]
    public void AnnouncementDropsTheSelectorsAndKeepsTheTemplateLengthRules()
    {
        var template = CalibrationObserverTests.Template();

        var pop = CalibratedShape.Pop(template, new CalibratedPop(CalibrationMatchSource.Announcement, 0xF00D, Length: 64));

        Assert.Equal(0xF00D, pop.Opcode);
        Assert.Equal(64, pop.ExpectedLength);
        Assert.Null(pop.SegmentType);
        Assert.DoesNotContain(pop.Fields, field => field.Role == ProfileFieldRole.Selector);
        Assert.Equal(16, pop.Field("roulette_id")!.Offset);
    }

    [Fact]
    public void AMarkerForcesTheRouletteIdToOneByteAtTheLearnedOffset()
    {
        var template = CalibrationObserverTests.Template();

        var pop = CalibratedShape.Pop(template,
            new CalibratedPop(CalibrationMatchSource.MarkerOffset, 0xF00D, Length: 24, RouletteOffset: 8));

        Assert.Equal(ProfileFieldType.U8, pop.Field("roulette_id")!.Type);
        Assert.Equal(8, pop.Field("roulette_id")!.Offset);
        Assert.Null(pop.MinLength);
        Assert.Null(pop.MaxLength);
    }

    [Fact]
    public void TheQueueRequestIsTheTemplatesRequestShapeTravellingToTheServer()
    {
        var template = CalibrationObserverTests.Template();

        var pop = CalibratedShape.Pop(template, new CalibratedPop(CalibrationMatchSource.QueueRequest, 0xC001));

        Assert.Equal(PacketDirection.ClientToServer, pop.Direction);
        Assert.Equal(template.Calibration.FinderRequest.ExpectedLength, pop.ExpectedLength);
        Assert.Same(template.Calibration.FinderRequest.RouletteField, Assert.Single(pop.Fields));
    }

    [Fact]
    public void TheMatchWindowIsTheTemplatesUnlessTheQueueStandsIn()
    {
        var template = CalibrationObserverTests.Template();

        Assert.Equal(120, CalibratedShape.MatchWindowSeconds(template, CalibrationMatchSource.ReplyState));
        Assert.Equal(120, CalibratedShape.MatchWindowSeconds(template, CalibrationMatchSource.MarkerOffset));
        Assert.Equal(3600, CalibratedShape.MatchWindowSeconds(template, CalibrationMatchSource.QueueRequest));
    }

    [Fact]
    public void AQueueInferredShapeWithoutTheTerritoryIsNeitherCompleteNorBuildable()
    {
        var template = CalibrationObserverTests.Template();
        var values = new CalibratedValues(new CalibratedPop(CalibrationMatchSource.QueueRequest, 0xC001), 0xA107);
        var messages = new[]
        {
            CalibratedShape.Pop(template, values.Pop),
            CalibratedShape.Zone(template, 0xA107),
        };

        Assert.False(CalibratedShape.IsComplete(CalibrationMatchSource.QueueRequest, messages));
        Assert.True(CalibratedShape.IsComplete(CalibrationMatchSource.ReplyState, messages));
        Assert.NotNull(CalibratedShape.Messages(template, values).Error);
    }

    public static TheoryData<string, CalibratedValues> Refused() => new()
    {
        { "reply state with a length", new CalibratedValues(
            new CalibratedPop(CalibrationMatchSource.ReplyState, 0xC002, Length: 40,
                Selectors: new[] { new CalibrationSelectorReading("finder_state", 3) }), 0xA107) },
        { "reply state without its selector", new CalibratedValues(
            new CalibratedPop(CalibrationMatchSource.ReplyState, 0xC002,
                Selectors: Array.Empty<CalibrationSelectorReading>()), 0xA107) },
        { "reply state naming another field", new CalibratedValues(
            new CalibratedPop(CalibrationMatchSource.ReplyState, 0xC002,
                Selectors: new[] { new CalibrationSelectorReading("guess", 3) }), 0xA107) },
        { "selector value too large for its type", new CalibratedValues(
            new CalibratedPop(CalibrationMatchSource.ReplyState, 0xC002,
                Selectors: new[] { new CalibrationSelectorReading("finder_state", 256) }), 0xA107) },
        { "announcement without a length", new CalibratedValues(
            new CalibratedPop(CalibrationMatchSource.Announcement, 0xF00D), 0xA107) },
        { "announcement too short for the roulette id", new CalibratedValues(
            new CalibratedPop(CalibrationMatchSource.Announcement, 0xF00D, Length: 16), 0xA107) },
        { "marker without an offset", new CalibratedValues(
            new CalibratedPop(CalibrationMatchSource.MarkerOffset, 0xF00D, Length: 24), 0xA107) },
        { "marker past the end", new CalibratedValues(
            new CalibratedPop(CalibrationMatchSource.MarkerOffset, 0xF00D, Length: 24, RouletteOffset: 24), 0xA107) },
        { "queue request with a length", new CalibratedValues(
            new CalibratedPop(CalibrationMatchSource.QueueRequest, 0xC001, Length: 24), 0xA107, 0xA108) },
        { "two messages on one opcode", new CalibratedValues(
            new CalibratedPop(CalibrationMatchSource.Announcement, 0xA107, Length: 64), 0xA107) },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void LearnedValuesTheTransformCannotUseAreRefused(string why, CalibratedValues values)
    {
        var result = CalibratedShape.Messages(CalibrationObserverTests.Template(), values);

        Assert.True(result.Error is not null, why);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public void ATemplateWithoutTheOptionalMessagesCannotDeclareThem()
    {
        var full = CalibrationObserverTests.Template();
        var bare = CalibrationTemplate.From(full.Source with
        {
            Messages = full.Source.Messages
                .Where(message => message.Name is "CONTENT_FINDER_POP" or "ZONE_INITIALIZATION").ToArray(),
        })!;
        var pop = new CalibratedPop(CalibrationMatchSource.Announcement, 0xF00D, Length: 64);

        Assert.NotNull(CalibratedShape.Messages(bare, new CalibratedValues(pop, 0xA107, TerritoryOpcode: 0xA108)).Error);
        Assert.NotNull(CalibratedShape.Messages(bare, new CalibratedValues(pop, 0xA107, JobOpcode: 0xA109)).Error);
        Assert.Null(CalibratedShape.Messages(bare, new CalibratedValues(pop, 0xA107)).Error);
    }

    /// <summary>
    /// A marker found at exactly the template's offset writes the same bytes as an announcement,
    /// so the two cannot be told apart from a profile. Reading back picks the earlier source,
    /// which makes one set of messages one share code.
    /// </summary>
    [Fact]
    public void AMarkerAtTheTemplatesOffsetReadsBackAsAnAnnouncement()
    {
        var template = CalibrationObserverTests.Template();
        var marker = CalibratedShape.Messages(template, new CalibratedValues(
            new CalibratedPop(CalibrationMatchSource.MarkerOffset, 0xF00D, Length: 64, RouletteOffset: 16), 0xA107));

        var values = CalibratedShape.Read(template, marker.Messages);

        Assert.Equal(CalibrationMatchSource.Announcement, values!.MatchSource);
        Assert.Equal(64, values.Pop.Length);
        Assert.Null(values.Pop.RouletteOffset);
    }

    [Fact]
    public void MessagesThatAreNotACalibratedShapeOfTheTemplateDoNotReadBack()
    {
        var template = CalibrationObserverTests.Template();
        var draft = CalibrationTrafficCases.Derive(CalibrationTrafficCases.ReplyState);
        var messages = draft.Messages.ToArray();

        var widened = messages.Select(message => message.Name == "ZONE_INITIALIZATION"
            ? message with { ExpectedLength = 460 }
            : message).ToArray();
        var extraField = messages.Select(message => message.Name == "CONTENT_FINDER_POP"
            ? message with { Fields = message.Fields.Append(message.Fields[0] with { Name = "extra", Offset = 20 }).ToArray() }
            : message).ToArray();
        var withResult = messages.Append(new ProfileMessage("DUTY_RESULT", 0xB001, PacketDirection.ServerToClient,
            null, 8, null, null, new long[] { 1 }, Array.Empty<ProfileField>())).ToArray();

        Assert.Null(CalibratedShape.Read(template, widened));
        Assert.Null(CalibratedShape.Read(template, extraField));
        Assert.Null(CalibratedShape.Read(template, withResult));
        Assert.Null(CalibratedShape.Read(template, messages.Where(message => message.Name != "ZONE_INITIALIZATION").ToArray()));
    }
}
