using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// A mutation context with the character profile service and statistics that know the clock, for the tests of
/// character profiles and of the identities bound to them.
/// </summary>
internal abstract class CharacterProfileTestContext : RunMutationTestContext
{
    protected CharacterProfileTestContext()
    {
        Profiles = new CharacterProfileService(Database.Database, Settings, Database.Clock);
        Statistics = new StatisticsRepository(Database.Database, Settings, clock: Database.Clock);
    }

    public CharacterProfileService Profiles { get; }

    public StatisticsRepository Statistics { get; }

    public static string NewId() => Guid.NewGuid().ToString("D");

    /// <summary>The current profile of <paramref name="region"/>.</summary>
    public long Current(Region region = Region.Cn) => Settings.Profiles.FindCurrent(region)!.ProfileId;

    /// <summary>Creates a profile with a goal of 2000 and returns its id.</summary>
    public long Add(
        string name, Region region = Region.Cn, int baseline = 0, bool makeCurrent = false, long? identityId = null) =>
        Profiles.Create(new CreateCharacterProfileCommand(NewId(), region, name, 2000, baseline, makeCurrent, identityId))
            .ProfileId;
}
