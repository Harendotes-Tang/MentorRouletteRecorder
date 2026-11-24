using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Parsing;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>Keeps every semantic event a parser emits, in order.</summary>
internal sealed class RecordingSemanticSink : ISemanticEventSink
{
    public List<SemanticEvent> Events { get; } = new();

    public void Accept(SemanticEvent semanticEvent) => Events.Add(semanticEvent);
}
