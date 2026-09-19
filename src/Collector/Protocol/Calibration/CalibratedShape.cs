using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// What a match source takes from the traffic on top of the template. Anything a calibrated
/// profile declares that is not in this set is inherited from the template.
/// </summary>
[Flags]
public enum CalibratedPopInputs
{
    /// <summary>Nothing learned; never a complete set.</summary>
    None = 0,

    /// <summary>The pop's opcode. Every source learns it.</summary>
    Opcode = 1,

    /// <summary>The payload length of an announcement sent as a message of its own.</summary>
    Length = 2,

    /// <summary>The byte offset the traffic named for the roulette id.</summary>
    RouletteOffset = 4,

    /// <summary>The value each learnable selector carries when the queue reply means "matched".</summary>
    Selectors = 8,
}

/// <summary>The learned half of a calibrated <c>CONTENT_FINDER_POP</c>.</summary>
/// <param name="Source">Which kind of evidence named the match.</param>
/// <param name="Opcode">
/// Opcode of the server's announcement, or of the client's own request when
/// <paramref name="Source"/> is <see cref="CalibrationMatchSource.QueueRequest"/>.
/// </param>
/// <param name="Length">
/// Payload length. Learned only by <see cref="CalibrationMatchSource.Announcement"/> and
/// <see cref="CalibrationMatchSource.MarkerOffset"/>; every other source inherits it.
/// </param>
/// <param name="RouletteOffset">Byte offset of the roulette id. Learned only by <see cref="CalibrationMatchSource.MarkerOffset"/>.</param>
/// <param name="Selectors">
/// The state that means "matched", one reading per learnable selector in template order.
/// Learned only by <see cref="CalibrationMatchSource.ReplyState"/>.
/// </param>
public sealed record CalibratedPop(
    CalibrationMatchSource Source,
    ushort Opcode,
    int? Length = null,
    int? RouletteOffset = null,
    IReadOnlyList<CalibrationSelectorReading>? Selectors = null);

/// <summary>
/// Everything calibration learns about one build, and nothing else: the pop, the zone-change
/// marker and, when they could be told apart, the territory and job messages. A share code
/// carries exactly these values.
/// </summary>
/// <param name="Pop">The learned half of the pop.</param>
/// <param name="ZoneOpcode">Opcode of <c>ZONE_INITIALIZATION</c>.</param>
/// <param name="TerritoryOpcode">Opcode of <c>ZONE_TERRITORY</c>, when declared.</param>
/// <param name="JobOpcode">Opcode of <c>PLAYER_JOB</c>, when declared.</param>
public sealed record CalibratedValues(
    CalibratedPop Pop,
    ushort ZoneOpcode,
    ushort? TerritoryOpcode = null,
    ushort? JobOpcode = null)
{
    /// <summary>Which kind of evidence named the match.</summary>
    public CalibrationMatchSource MatchSource => Pop.Source;
}

/// <summary>The messages a set of learned values produces, or why it cannot produce any.</summary>
/// <param name="Messages">Messages in declaration order; empty when refused.</param>
/// <param name="Error">Short, non-sensitive reason for a refusal; null when the values were usable.</param>
public sealed record CalibratedShapeResult(IReadOnlyList<ProfileMessage> Messages, string? Error)
{
    /// <summary>A refusal.</summary>
    /// <param name="error">Why.</param>
    internal static CalibratedShapeResult Refused(string error) => new(Array.Empty<ProfileMessage>(), error);
}

/// <summary>
/// "Template + learned values -> profile messages", as one transform.
///
/// Shared calibration rebuilds a profile from a share code on another machine, so that rebuild
/// and the sharer's draft must run the same transform. The draft calls <see cref="Pop"/>,
/// <see cref="Zone"/>, <see cref="Territory"/>, <see cref="Job"/> and <see cref="IsComplete"/>;
/// a share code goes through <see cref="Messages"/>, which applies the same transforms after
/// checking that the values are the ones the source actually uses.
/// </summary>
public static class CalibratedShape
{
    /// <summary>Semantic name of the pop.</summary>
    public const string PopName = "CONTENT_FINDER_POP";

    /// <summary>Semantic name of the zone-change marker.</summary>
    public const string ZoneName = "ZONE_INITIALIZATION";

    /// <summary>Semantic name of the territory message.</summary>
    public const string TerritoryName = "ZONE_TERRITORY";

    /// <summary>Semantic name of the job message.</summary>
    public const string JobName = "PLAYER_JOB";

    /// <summary>Semantic name of the announcement recognised by its timing.</summary>
    public const string AnnouncedName = "MATCH_ANNOUNCED";

    private const string RouletteFieldName = "roulette_id";

    /// <summary>
    /// What <paramref name="source"/> reads from the traffic. A value outside this set would be
    /// ignored by the transform, so a share code carrying one is refused rather than hashed into
    /// a second identity for the same profile.
    /// </summary>
    /// <param name="source">Match source.</param>
    public static CalibratedPopInputs InputsFor(CalibrationMatchSource source) => source switch
    {
        CalibrationMatchSource.ReplyState => CalibratedPopInputs.Opcode | CalibratedPopInputs.Selectors,
        CalibrationMatchSource.Announcement => CalibratedPopInputs.Opcode | CalibratedPopInputs.Length,
        CalibrationMatchSource.MarkerOffset =>
            CalibratedPopInputs.Opcode | CalibratedPopInputs.Length | CalibratedPopInputs.RouletteOffset,
        CalibrationMatchSource.QueueRequest => CalibratedPopInputs.Opcode,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "unknown match source"),
    };

    /// <summary>
    /// True when a profile of this source is worthless without <c>ZONE_TERRITORY</c>: one that
    /// infers the match from the queue can only tell a duty from a teleport by the territory it
    /// lands in (see <see cref="ProtocolProfile.ToBinding"/>).
    /// </summary>
    /// <param name="source">Match source.</param>
    public static bool RequiresTerritory(CalibrationMatchSource source) => source == CalibrationMatchSource.QueueRequest;

    /// <summary>
    /// <c>match_window_seconds</c> of a calibrated profile: the template's, unless the queue
    /// request stands in for the announcement, in which case the window measures a queue and is
    /// the format's ceiling.
    /// </summary>
    /// <param name="template">Template lending the window.</param>
    /// <param name="source">Match source.</param>
    public static int MatchWindowSeconds(CalibrationTemplate template, CalibrationMatchSource source)
    {
        ArgumentNullException.ThrowIfNull(template);
        return source == CalibrationMatchSource.QueueRequest
            ? (int)CalibrationDraft.QueueWindow.TotalSeconds
            : (int)template.MatchWindow.TotalSeconds;
    }

    /// <summary>
    /// The template pop's selectors a reply state can be learned for, in declaration order: the
    /// same ones the observer reads off every pop-shaped message (numeric selectors other than
    /// the roulette id).
    /// </summary>
    /// <param name="template">Template lending the pop.</param>
    public static IReadOnlyList<ProfileField> LearnableSelectors(CalibrationTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.Pop.Fields
            .Where(field => field.Role == ProfileFieldRole.Selector && field.Type != ProfileFieldType.Bytes &&
                !string.Equals(field.Name, RouletteFieldName, StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>
    /// The pop a source produces. Lenient on purpose: the draft applies it to values it
    /// assembled itself. Use <see cref="Messages"/> for values from anywhere else.
    /// </summary>
    /// <param name="template">Template lending the structure.</param>
    /// <param name="pop">Learned values.</param>
    public static ProfileMessage Pop(CalibrationTemplate template, CalibratedPop pop)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(pop);
        return pop.Source switch
        {
            CalibrationMatchSource.ReplyState => WithLearnedStates(
                template.Pop, pop.Opcode, pop.Selectors ?? Array.Empty<CalibrationSelectorReading>()),
            CalibrationMatchSource.Announcement => Announcement(
                template.Pop, pop.Opcode, pop.Length ?? throw new ArgumentException("an announcement needs its length", nameof(pop))),
            CalibrationMatchSource.MarkerOffset => AnnouncementAt(
                template.Pop,
                pop.Opcode,
                pop.Length ?? throw new ArgumentException("a marker needs its length", nameof(pop)),
                pop.RouletteOffset ?? throw new ArgumentException("a marker needs its offset", nameof(pop))),
            CalibrationMatchSource.QueueRequest => QueuePop(template, pop.Opcode),
            _ => throw new ArgumentOutOfRangeException(nameof(pop), pop.Source, "unknown match source"),
        };
    }

    /// <summary>The zone-change marker: the template's structure on a learned opcode.</summary>
    /// <param name="template">Template lending the structure.</param>
    /// <param name="opcode">Learned opcode.</param>
    public static ProfileMessage Zone(CalibrationTemplate template, ushort opcode)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.ZoneInitialization with { Opcode = opcode, SegmentType = null };
    }

    /// <summary>The territory message: the template's structure on a learned opcode.</summary>
    /// <param name="template">Template lending the structure; must declare the message.</param>
    /// <param name="opcode">Learned opcode.</param>
    public static ProfileMessage Territory(CalibrationTemplate template, ushort opcode)
    {
        ArgumentNullException.ThrowIfNull(template);
        return (template.ZoneTerritory ?? throw new InvalidOperationException("the template declares no territory message"))
            with { Opcode = opcode, SegmentType = null };
    }

    /// <summary>The job message: the template's structure on a learned opcode.</summary>
    /// <param name="template">Template lending the structure; must declare the message.</param>
    /// <param name="opcode">Learned opcode.</param>
    public static ProfileMessage Job(CalibrationTemplate template, ushort opcode)
    {
        ArgumentNullException.ThrowIfNull(template);
        return (template.PlayerJob ?? throw new InvalidOperationException("the template declares no job message"))
            with { Opcode = opcode, SegmentType = null };
    }

    /// <summary>
    /// The announcement recognised by its timing: an opcode and an exact length, and nothing
    /// else. It declares no field because it has none to declare - the message carries no
    /// roulette id at any offset, which is why no search by value could find it - and it takes
    /// no structure from the template for the same reason. The message arriving is the whole
    /// observation.
    /// </summary>
    /// <param name="opcode">Opcode learned from the traffic.</param>
    /// <param name="length">Exact payload length learned from the traffic.</param>
    public static ProfileMessage Announced(ushort opcode, int length) => new(
        AnnouncedName, opcode, PacketDirection.ServerToClient, null, length, null, null,
        Array.Empty<long>(), Array.Empty<ProfileField>());

    /// <summary>
    /// True when a set of messages is enough to record with: the pop and the zone-change marker,
    /// plus the territory when the match is inferred from the queue.
    /// </summary>
    /// <param name="source">Match source the messages were learned under.</param>
    /// <param name="messages">Declared messages.</param>
    public static bool IsComplete(CalibrationMatchSource source, IEnumerable<ProfileMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var names = messages.Select(message => message.Name).ToHashSet(StringComparer.Ordinal);
        return names.Contains(PopName) && names.Contains(ZoneName) &&
            (!RequiresTerritory(source) || names.Contains(TerritoryName));
    }

    /// <summary>
    /// Every message a set of learned values produces, in the order the draft declares them
    /// (pop, zone marker, territory, job), after checking that the values are exactly what the
    /// source uses, that they fit the template, and that the result is complete.
    /// </summary>
    /// <param name="template">Template lending the structure.</param>
    /// <param name="values">Learned values from outside the draft, such as a share code.</param>
    public static CalibratedShapeResult Messages(CalibrationTemplate template, CalibratedValues values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        var pop = values.Pop;
        var source = EnumWire<CalibrationMatchSource>.Format(pop.Source);
        var inputs = InputsFor(pop.Source);
        if (Refusal(pop, inputs, source, template) is { } refused)
        {
            return CalibratedShapeResult.Refused(refused);
        }

        if (RequiresTerritory(pop.Source) && values.TerritoryOpcode is null)
        {
            return CalibratedShapeResult.Refused(source + " needs the territory message");
        }

        if (values.TerritoryOpcode is not null && template.ZoneTerritory?.ExpectedLength is null)
        {
            return CalibratedShapeResult.Refused("the template declares no territory message");
        }

        if (values.JobOpcode is not null && template.PlayerJob?.ExpectedLength is null)
        {
            return CalibratedShapeResult.Refused("the template declares no job message");
        }

        var messages = new List<ProfileMessage> { Pop(template, pop), Zone(template, values.ZoneOpcode) };
        if (values.TerritoryOpcode is { } territory)
        {
            messages.Add(Territory(template, territory));
        }

        if (values.JobOpcode is { } job)
        {
            messages.Add(Job(template, job));
        }

        if (messages.Select(message => (message.Direction, message.Opcode)).Distinct().Count() != messages.Count)
        {
            return CalibratedShapeResult.Refused("two messages claim the same direction and opcode");
        }

        var built = messages[0];
        if (built.ExpectedLength is { } length && built.Fields.Any(field => field.Offset + field.Size > length))
        {
            return CalibratedShapeResult.Refused("the pop's fields do not fit its length");
        }

        return new CalibratedShapeResult(messages, null);
    }

    /// <summary>
    /// The learned values a set of messages was built from, or null when the messages are not a
    /// calibrated shape of this template: a different structure, a message calibration never
    /// declares, or a constraint the transform would not have written.
    ///
    /// Sources are tried in the draft's own order. A marker found at exactly the template's
    /// offset writes the same bytes as an announcement, so it reads back as the announcement;
    /// one set of messages therefore has one set of learned values.
    /// </summary>
    /// <param name="template">Template the messages must have been built from.</param>
    /// <param name="messages">Declared messages of a calibrated profile.</param>
    public static CalibratedValues? Read(CalibrationTemplate template, IReadOnlyList<ProfileMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(messages);
        var byName = new Dictionary<string, ProfileMessage>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            // A share code is format v1 and carries no announcement: the timing evidence that
            // names one is this machine's, and an opcode learned from it means nothing without
            // that evidence. A profile carrying one therefore shares as the plain queue-request
            // profile it is, and the receiving machine can find its own announcement.
            if (string.Equals(message.Name, AnnouncedName, StringComparison.Ordinal))
            {
                continue;
            }

            if (message.Name is not (PopName or ZoneName or TerritoryName or JobName) ||
                !byName.TryAdd(message.Name, message))
            {
                return null;
            }
        }

        if (!byName.TryGetValue(PopName, out var pop) || !byName.TryGetValue(ZoneName, out var zone))
        {
            return null;
        }

        ushort? territory = byName.TryGetValue(TerritoryName, out var territoryMessage) ? territoryMessage.Opcode : null;
        ushort? job = byName.TryGetValue(JobName, out var jobMessage) ? jobMessage.Opcode : null;
        foreach (var candidate in PopCandidates(template, pop))
        {
            var values = new CalibratedValues(candidate, zone.Opcode, territory, job);
            var rebuilt = Messages(template, values);
            if (rebuilt.Error is null && rebuilt.Messages.Count == byName.Count &&
                rebuilt.Messages.All(message => Same(message, byName[message.Name])))
            {
                return values;
            }
        }

        return null;
    }

    private static string? Refusal(CalibratedPop pop, CalibratedPopInputs inputs, string source, CalibrationTemplate template)
    {
        if ((pop.Length is not null) != inputs.HasFlag(CalibratedPopInputs.Length))
        {
            return pop.Length is null ? source + " needs the pop length" : source + " does not use a pop length";
        }

        if ((pop.RouletteOffset is not null) != inputs.HasFlag(CalibratedPopInputs.RouletteOffset))
        {
            return pop.RouletteOffset is null ? source + " needs the roulette offset" : source + " does not use a roulette offset";
        }

        if ((pop.Selectors is not null) != inputs.HasFlag(CalibratedPopInputs.Selectors))
        {
            return pop.Selectors is null ? source + " needs the selector values" : source + " does not use selector values";
        }

        if (pop.Length is < 1 or > ushort.MaxValue)
        {
            return "the pop length is out of range";
        }

        if (pop.RouletteOffset is { } offset && (offset < 0 || offset + 1 > pop.Length))
        {
            return "the roulette offset lies outside the pop";
        }

        if (pop.Selectors is { } readings)
        {
            var learnable = LearnableSelectors(template);
            if (readings.Count != learnable.Count)
            {
                return "the selector values do not match the template's selectors";
            }

            for (var index = 0; index < readings.Count; index++)
            {
                if (!string.Equals(readings[index].Field, learnable[index].Name, StringComparison.Ordinal) ||
                    !Fits(learnable[index].Type, readings[index].Value))
                {
                    return "the selector values do not match the template's selectors";
                }
            }
        }

        return null;
    }

    private static IEnumerable<CalibratedPop> PopCandidates(CalibrationTemplate template, ProfileMessage pop)
    {
        var learnable = LearnableSelectors(template);
        var readings = new List<CalibrationSelectorReading>(learnable.Count);
        foreach (var field in learnable)
        {
            if (pop.Field(field.Name) is { Constraints.In: { Count: 1 } values })
            {
                readings.Add(new CalibrationSelectorReading(field.Name, values[0]));
            }
        }

        if (readings.Count == learnable.Count)
        {
            yield return new CalibratedPop(CalibrationMatchSource.ReplyState, pop.Opcode, Selectors: readings);
        }

        if (pop.ExpectedLength is { } length)
        {
            yield return new CalibratedPop(CalibrationMatchSource.Announcement, pop.Opcode, Length: length);
            if (pop.Field(RouletteFieldName) is { } roulette)
            {
                yield return new CalibratedPop(
                    CalibrationMatchSource.MarkerOffset, pop.Opcode, Length: length, RouletteOffset: roulette.Offset);
            }
        }

        yield return new CalibratedPop(CalibrationMatchSource.QueueRequest, pop.Opcode);
    }

    private static bool Same(ProfileMessage left, ProfileMessage right) =>
        string.Equals(
            CalibratedProfileDocument.Message(left).ToJsonString(),
            CalibratedProfileDocument.Message(right).ToJsonString(),
            StringComparison.Ordinal);

    private static bool Fits(ProfileFieldType type, long value) => type switch
    {
        ProfileFieldType.U8 => value is >= 0 and <= byte.MaxValue,
        ProfileFieldType.U16 => value is >= 0 and <= ushort.MaxValue,
        ProfileFieldType.U32 => value is >= 0 and <= uint.MaxValue,
        ProfileFieldType.I32 => value is >= int.MinValue and <= int.MaxValue,
        ProfileFieldType.U64 => value >= 0,
        _ => false,
    };

    /// <summary>
    /// The template's pop with the opcode and the state values this machine observed. Every
    /// other field keeps the template's offsets and constraints; only the selector states,
    /// which the template cannot know for a build it does not describe, are learned.
    /// </summary>
    /// <param name="pop">Pop structure from the template.</param>
    /// <param name="opcode">Opcode learned from the request/echo pairing.</param>
    /// <param name="learned">The state that means "matched" on this build.</param>
    private static ProfileMessage WithLearnedStates(
        ProfileMessage pop, ushort opcode, IReadOnlyList<CalibrationSelectorReading> learned)
    {
        var fields = new List<ProfileField>(pop.Fields.Count);
        foreach (var field in pop.Fields)
        {
            // Payload values keep the template's contract. Only declared selectors may
            // receive a newly learned state, even if a malformed snapshot names a value.
            if (field.Role != ProfileFieldRole.Selector)
            {
                fields.Add(field);
                continue;
            }

            var index = -1;
            for (var i = 0; i < learned.Count; i++)
            {
                if (string.Equals(learned[i].Field, field.Name, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            fields.Add(index < 0
                ? field
                : field with { Constraints = new ProfileFieldConstraints(null, null, new[] { learned[index].Value }) });
        }

        return pop with { Opcode = opcode, SegmentType = null, Fields = fields };
    }

    /// <summary>
    /// The pop as a message of its own. The template's selector describes a state field of the
    /// build's queue-reply message; a dedicated announcement has no such state, because the
    /// message arriving IS the state. Every value field keeps the template's contract.
    /// </summary>
    /// <param name="pop">Pop structure from the template.</param>
    /// <param name="opcode">Opcode learned from the traffic.</param>
    /// <param name="length">Payload length learned from the traffic.</param>
    private static ProfileMessage Announcement(ProfileMessage pop, ushort opcode, int length) =>
        pop with
        {
            Opcode = opcode,
            ExpectedLength = length,
            SegmentType = null,
            Fields = pop.Fields.Where(field => field.Role != ProfileFieldRole.Selector).ToArray(),
        };

    /// <summary>
    /// The announcement as a message of its own, with the roulette id where this build puts it.
    /// The template lends the field's name, type and constraints; only the offset is learned.
    /// </summary>
    /// <param name="pop">Pop structure from the template.</param>
    /// <param name="opcode">Opcode learned from the traffic.</param>
    /// <param name="length">Payload length learned from the traffic.</param>
    /// <param name="offset">Byte offset the roulette id was found at.</param>
    private static ProfileMessage AnnouncementAt(ProfileMessage pop, ushort opcode, int length, int offset) =>
        pop with
        {
            Opcode = opcode,
            ExpectedLength = length,
            MinLength = null,
            MaxLength = null,
            SegmentType = null,
            Fields = pop.Fields
                .Where(field => field.Role != ProfileFieldRole.Selector)
                .Select(field => string.Equals(field.Name, RouletteFieldName, StringComparison.Ordinal)
                    ? field with { Offset = offset, Type = ProfileFieldType.U8 }
                    : field)
                .ToArray(),
        };

    /// <summary>
    /// The player's own request standing in for the announcement: same semantic name, so the
    /// rest of the system is unchanged, but the client's message rather than the server's. The
    /// direction is what tells every later reader that this profile infers the match.
    /// </summary>
    /// <param name="template">Template lending the request shape.</param>
    /// <param name="opcode">Request opcode the pairing identified.</param>
    private static ProfileMessage QueuePop(CalibrationTemplate template, ushort opcode)
    {
        var request = template.Calibration.FinderRequest;
        return template.Pop with
        {
            Opcode = opcode,
            Direction = request.Direction,
            ExpectedLength = request.ExpectedLength,
            MinLength = null,
            MaxLength = null,
            SegmentType = null,
            Fields = new[] { request.RouletteField },
        };
    }
}
