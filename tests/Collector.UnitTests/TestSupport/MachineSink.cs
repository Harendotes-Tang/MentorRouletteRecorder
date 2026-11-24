using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Protocol.Parsing;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The state machine as a parser's sink, with no storage behind it. Given <paramref name="seen"/>, it also records every
/// event that reached the machine, in order.
/// </summary>
/// <param name="machine">Machine every event is handed to.</param>
/// <param name="seen">Where the events are recorded, if anywhere.</param>
internal sealed class MachineSink(MentorRunStateMachine machine, List<SemanticEvent>? seen = null) : ISemanticEventSink
{
    public void Accept(SemanticEvent semanticEvent)
    {
        seen?.Add(semanticEvent);
        machine.Handle(semanticEvent);
    }
}
