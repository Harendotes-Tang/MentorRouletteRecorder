using MentorRecorder.Collector.Protocol.Parsing;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The watch that catches a declared match message which is not one. Nothing here touches the
/// clock, the disk or a profile: it is given monotonic readings and roulette ids and answers
/// whether the traffic has disproved the declaration.
/// </summary>
public sealed class PopContradictionWatchTests
{
    private static TimeSpan At(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    /// <summary>
    /// The real machine's bug: the retainer bell's rows carry their slot number, 0 to 9, at the
    /// byte the profile reads the roulette id from, and the whole list arrives inside one second.
    /// The duty finder offers one duty at a time, so the third different roulette settles it.
    /// </summary>
    [Fact]
    public void AListThatCountsThroughTheSmallNumbersContradictsTheDeclaration()
    {
        var watch = new PopContradictionWatch();

        Assert.False(watch.Observe(At(0), 1));
        Assert.False(watch.Observe(At(1), 2));
        Assert.True(watch.Observe(At(2), 3));
        Assert.True(watch.Contradicted);
    }

    /// <summary>A re-pop of the same roulette is ordinary play, however often it repeats.</summary>
    [Fact]
    public void TheSameRouletteOverAndOverNeverContradicts()
    {
        var watch = new PopContradictionWatch();

        for (var i = 0; i < 5; i++)
        {
            Assert.False(watch.Observe(At(i), 9));
        }

        Assert.False(watch.Contradicted);
    }

    /// <summary>Three roulettes over three seconds is a player queueing three times, not a list.</summary>
    [Fact]
    public void ThreeRoulettesSpreadOverSecondsNeverContradict()
    {
        var watch = new PopContradictionWatch();

        Assert.False(watch.Observe(At(0), 1));
        Assert.False(watch.Observe(At(1_500), 2));
        Assert.False(watch.Observe(At(3_000), 3));
        Assert.False(watch.Contradicted);
    }

    /// <summary>Two is not enough: a re-queue answered twice in a second must not cost a profile.</summary>
    [Fact]
    public void TwoRoulettesInOneSecondNeverContradict()
    {
        var watch = new PopContradictionWatch();

        Assert.False(watch.Observe(At(0), 1));
        Assert.False(watch.Observe(At(10), 2));
        Assert.False(watch.Observe(At(20), 2));
        Assert.False(watch.Contradicted);
    }

    /// <summary>
    /// A new capture session starts a new stopwatch, so the readings begin again from zero. The
    /// sightings of the session before must not be read as if they had arrived in the same second
    /// as the first sighting of the new one.
    /// </summary>
    [Fact]
    public void ReadingsGoingBackwardsStartOverInsteadOfFiring()
    {
        var watch = new PopContradictionWatch();

        Assert.False(watch.Observe(At(600_000), 5));
        Assert.False(watch.Observe(At(0), 1));
        Assert.False(watch.Observe(At(10), 2));
        Assert.False(watch.Contradicted);
    }

    /// <summary>Memory is bounded: a build that sends this shape all evening cannot grow the queue.</summary>
    [Fact]
    public void SightingsAreBounded()
    {
        var watch = new PopContradictionWatch();

        for (var i = 0; i < 1_000; i++)
        {
            watch.Observe(At(i * 0.1), 9);
        }

        Assert.False(watch.Contradicted);
        Assert.True(watch.Sighted <= PopContradictionWatch.MaxSightings);
    }

    /// <summary>Once fired it stays fired, so every later pop of the same shape is refused too.</summary>
    [Fact]
    public void ItStaysFiredUntilItIsReset()
    {
        var watch = new PopContradictionWatch();
        watch.Observe(At(0), 1);
        watch.Observe(At(1), 2);
        Assert.True(watch.Observe(At(2), 3));

        Assert.True(watch.Observe(At(120_000), 9));

        watch.Reset();
        Assert.False(watch.Contradicted);
        Assert.False(watch.Observe(At(180_000), 9));
    }
}
