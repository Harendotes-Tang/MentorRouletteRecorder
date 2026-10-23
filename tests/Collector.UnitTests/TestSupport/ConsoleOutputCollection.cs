namespace MentorRecorder.Collector.UnitTests;

/// <summary>Isolates process-wide console redirection from every other test collection.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleOutputCollection
{
    public const string Name = "Collector console output";
}
