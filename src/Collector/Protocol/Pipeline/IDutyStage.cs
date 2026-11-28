using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.Protocol.Pipeline;

/// <summary>In-memory stage projection for one entered run on its game connection.</summary>
internal interface IDutyStage
{
    /// <summary>Current overlay token, or unknown before a stage is confirmed.</summary>
    string Phase { get; }

    /// <summary>Consumes one decoded message and reports whether the visible stage changed.</summary>
    bool Observe(DecodedMessage message);
}
