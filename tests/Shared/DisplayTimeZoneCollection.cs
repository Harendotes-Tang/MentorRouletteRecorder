namespace MentorRecorder.Collector.Tests;

/// <summary>
/// The tests that put another 显示时区 in force (<see cref="MentorRecorder.Collector.Domain.Time.DisplayTimeZone.Use"/>).
/// The zone is process-wide, and every other test that cuts a day or formats a clock expects the
/// computer's zone, so these run alone, after the parallel collections, and restore the zone they
/// found before they end.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DisplayTimeZoneCollection
{
    public const string Name = "Collector display time zone";
}
