namespace MentorRecorder.Collector.UnitTests;

/// <summary>Tests that touch Machina's OodleFactory, which owns one process-wide native instance, run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MachinaOodleRuntimeCollection
{
    public const string Name = "Machina Oodle runtime";
}
