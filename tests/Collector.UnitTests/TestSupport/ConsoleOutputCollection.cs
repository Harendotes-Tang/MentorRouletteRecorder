namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Isolates process-wide state - console redirection and a changed <c>MR_DATA_DIR</c> - from every other test
/// collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleOutputCollection
{
    public const string Name = "Collector console output";
}
