using System.Globalization;
using System.Security.Cryptography;
using MentorRecorder.Collector.Replay;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The fixtures are the executable form of docs/state-machine.md, so their integrity is
/// itself under test: every file has a sidecar hash, every hash matches, the checked-in
/// <c>SHA256SUMS</c> agrees with both, and no fixture declares personal data or a verified
/// protocol profile.
/// </summary>
public sealed class FixtureIntegrityTests
{
    private static string Directory => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>Every fixture file, as xunit theory data.</summary>
    public static TheoryData<string> FixtureFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in System.IO.Directory.GetFiles(Directory, "*.fixture.json").Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetFileName(path));
            }

            return data;
        }
    }

    [Fact]
    public void EveryExpectedFixtureIsPresent()
    {
        var names = System.IO.Directory.GetFiles(Directory, "*.fixture.json")
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Superset(
            new HashSet<string?>(StringComparer.Ordinal)
            {
                "synthetic-completed-v1.fixture.json",
                "mentor_left_v1.fixture.json",
                "mentor_cancelled_v1.fixture.json",
                "mentor_disconnected_v1.fixture.json",
                "mentor_interrupted_v1.fixture.json",
                "mentor_unknown_v1.fixture.json",
                "non_mentor_roulette_then_zone_v1.fixture.json",
                "duplicate_events_v1.fixture.json",
                "unknown_profile_v1.fixture.json",
                "two_runs_sequence_v1.fixture.json",
            },
            names);
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void FixtureMatchesItsSidecarHash(string fileName)
    {
        var path = Path.Combine(Directory, fileName);
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        var sidecar = path + ".sha256";
        Assert.True(File.Exists(sidecar), "missing sidecar for " + fileName);
        Assert.Equal(actual, File.ReadAllText(sidecar).Trim().ToLowerInvariant());
    }

    [Fact]
    public void Sha256SumsAgreesWithEveryFixture()
    {
        var manifest = Path.Combine(Directory, "SHA256SUMS");
        Assert.True(File.Exists(manifest));

        var recorded = File.ReadAllLines(manifest)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split("  ", 2, StringSplitOptions.None))
            .ToDictionary(parts => parts[1].Trim(), parts => parts[0].Trim(), StringComparer.Ordinal);

        var files = System.IO.Directory.GetFiles(Directory, "*.fixture.json");
        Assert.Equal(files.Length, recorded.Count);

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            Assert.True(recorded.ContainsKey(name), "SHA256SUMS is missing " + name);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                recorded[name]);
        }
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void FixtureLoadsAndDeclaresNoPersonalData(string fileName)
    {
        var fixture = ReplayFixtureLoader.Load(Path.Combine(Directory, fileName));

        Assert.False(string.IsNullOrWhiteSpace(fixture.FixtureId));
        Assert.NotEmpty(fixture.Events);
        Assert.NotEqual("VERIFIED", fixture.Profile.Status, StringComparer.Ordinal);
        Assert.All(fixture.Events, ev => Assert.Equal(
            fixture.Session.CaptureSessionId, ev.Key.CaptureSessionId));
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void FixtureIdMatchesItsFileName(string fileName)
    {
        var fixture = ReplayFixtureLoader.Load(Path.Combine(Directory, fileName));

        Assert.Equal(
            fileName.Replace(".fixture.json", string.Empty, StringComparison.Ordinal),
            fixture.FixtureId);
    }

    [Fact]
    public void TamperingWithAFixtureIsDetected()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.FixtureTamper", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(Directory, "mentor_left_v1.fixture.json");
            var copy = Path.Combine(directory, "mentor_left_v1.fixture.json");
            File.Copy(source, copy);
            File.Copy(source + ".sha256", copy + ".sha256");

            File.WriteAllText(
                copy,
                File.ReadAllText(copy).Replace(
                    "\"roulette_id\": 42",
                    "\"roulette_id\": " + 43.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal));

            var error = Assert.Throws<InvalidDataException>(() => ReplayFixtureLoader.Load(copy));
            Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }
}
