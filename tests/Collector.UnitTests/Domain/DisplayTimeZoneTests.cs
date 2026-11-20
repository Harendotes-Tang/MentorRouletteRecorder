using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Tests;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The 显示时区 the Collector holds (<see cref="DisplayTimeZone"/>): following the computer by default, a
/// chosen IANA zone, an unknown id refused without a change, and the first instant of a day across daylight
/// saving - a midnight that does not exist and one that happens twice. Every test restores the zone.
/// </summary>
[Collection(DisplayTimeZoneCollection.Name)]
public sealed class DisplayTimeZoneTests
{
    private static readonly DateTimeOffset Instant = new(2026, 9, 15, 16, 30, 0, TimeSpan.Zero);

    [Fact]
    public void ByDefaultTheComputersZoneIsFollowed()
    {
        Assert.Null(DisplayTimeZone.Id);
        Assert.Equal(TimeZoneInfo.Local.Id, DisplayTimeZone.Current.Id);
        Assert.Equal(
            Instant.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            DisplayTimeZone.DayOf(Instant));
        Assert.Equal(
            Instant.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            DisplayTimeZone.ClockOf(Instant));
    }

    [Fact]
    public void AChosenZoneIsInForceUntilTheScopeEnds()
    {
        using (DisplayTimeZone.Use("Asia/Tokyo"))
        {
            Assert.Equal("Asia/Tokyo", DisplayTimeZone.Id);
            Assert.Equal("2026-09-16", DisplayTimeZone.DayOf(Instant));
            Assert.Equal("01:30", DisplayTimeZone.ClockOf(Instant));
            Assert.Equal(TimeSpan.FromHours(9), DisplayTimeZone.Convert(Instant).Offset);
            Assert.Equal(540, DisplayTimeZone.UtcOffsetMinutes(Instant));

            using (DisplayTimeZone.Use("  America/New_York "))
            {
                Assert.Equal("America/New_York", DisplayTimeZone.Id);
                Assert.Equal("2026-09-15", DisplayTimeZone.DayOf(Instant));
                Assert.Equal("12:30", DisplayTimeZone.ClockOf(Instant));
            }

            Assert.Equal("Asia/Tokyo", DisplayTimeZone.Id);
        }

        Assert.Null(DisplayTimeZone.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoIdFollowsTheComputerAgain(string? id)
    {
        using var restore = DisplayTimeZone.Use("Asia/Tokyo");

        var zone = DisplayTimeZone.Set(id);

        Assert.Null(DisplayTimeZone.Id);
        Assert.Equal(TimeZoneInfo.Local.Id, zone.Id);
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("Etc/GMT+99")]
    public void AnUnknownIdIsRefusedAndChangesNothing(string id)
    {
        using var restore = DisplayTimeZone.Use("Asia/Tokyo");

        Assert.Throws<TimeZoneNotFoundException>(() => DisplayTimeZone.Set(id));
        Assert.Throws<TimeZoneNotFoundException>(() => DisplayTimeZone.Set(new string('A', DisplayTimeZone.MaxIdLength + 1)));

        Assert.Equal("Asia/Tokyo", DisplayTimeZone.Id);
    }

    [Fact]
    public void TheDayStartsAtItsMidnightOnEitherSideOfDaylightSaving()
    {
        using var restore = DisplayTimeZone.Use("America/New_York");

        Assert.Equal(Utc(2026, 3, 8, 5), DisplayTimeZone.DayStartUtc(new DateOnly(2026, 3, 8)));
        Assert.Equal(Utc(2026, 3, 9, 4), DisplayTimeZone.DayStartUtc(new DateOnly(2026, 3, 9)));
        Assert.Equal(Utc(2026, 11, 1, 4), DisplayTimeZone.DayStartUtc(new DateOnly(2026, 11, 1)));
        Assert.Equal(Utc(2026, 11, 2, 5), DisplayTimeZone.DayStartUtc(new DateOnly(2026, 11, 2)));
    }

    [Fact]
    public void AMidnightThatDoesNotExistStartsTheDayAtTheChange()
    {
        // Clocks jump from 00:00 to 01:00 on 2026-09-06, and fall back from 01:00 to 00:00 on 2027-04-04.
        var zone = MidnightChangeZone();

        Assert.True(zone.IsInvalidTime(new DateTime(2026, 9, 6, 0, 30, 0)));
        Assert.Equal(Utc(2026, 9, 6, 4), DisplayTimeZone.DayStartUtc(new DateOnly(2026, 9, 6), zone));
        Assert.Equal(new DateTime(2026, 9, 6, 1, 0, 0), TimeZoneInfo.ConvertTime(Utc(2026, 9, 6, 4), zone).DateTime);
        Assert.Equal(Utc(2026, 9, 5, 4), DisplayTimeZone.DayStartUtc(new DateOnly(2026, 9, 5), zone));
        Assert.Equal(Utc(2026, 9, 7, 3), DisplayTimeZone.DayStartUtc(new DateOnly(2026, 9, 7), zone));
    }

    [Fact]
    public void AMidnightThatHappensTwiceStartsTheDayAtItsFirst()
    {
        var zone = MidnightChangeZone();

        Assert.True(zone.IsAmbiguousTime(new DateTime(2027, 4, 4, 0, 30, 0)));
        Assert.Equal(Utc(2027, 4, 4, 3), DisplayTimeZone.DayStartUtc(new DateOnly(2027, 4, 4), zone));
        Assert.Equal(Utc(2027, 4, 5, 4), DisplayTimeZone.DayStartUtc(new DateOnly(2027, 4, 5), zone));
    }

    [Fact]
    public void CollectorSentencesWriteTheirClockInTheZone()
    {
        // A sentence is rendered once, when first read: each zone gets its own.
        static UserText Text() => new("test.clock", "于 {at}", UserTextArg.Clock("at", Instant));
        using (DisplayTimeZone.Use("Asia/Tokyo"))
        {
            var text = Text();
            Assert.Equal("于 01:30", text.Chinese);
            // The reference keeps the instant in UTC; the Desktop formats it in its own zone.
            Assert.Equal("2026-09-15T16:30:00.000Z", text.ToJson()["args"]!["at"]!.GetValue<string>());
        }

        using (DisplayTimeZone.Use("America/New_York"))
        {
            Assert.Equal("于 12:30", Text().Chinese);
        }
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    /// <summary>UTC-4, with daylight saving (UTC-3) from 2026-09-06 00:00 to 2027-04-04 01:00 local time.</summary>
    private static TimeZoneInfo MidnightChangeZone()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1),
            new DateTime(2027, 12, 31),
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 9, 6),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 4, 4));
        return TimeZoneInfo.CreateCustomTimeZone(
            "Test/Midnight_Change", TimeSpan.FromHours(-4), "Test midnight change", "Test standard", "Test daylight",
            new[] { rule });
    }
}
