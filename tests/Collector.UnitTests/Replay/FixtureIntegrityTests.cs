using System.Security.Cryptography;
using System.Text.Json;
using MentorRecorder.Collector.Replay;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The fixtures are the executable form of docs/state-machine.md, so their integrity is
/// itself under test: every file has a sidecar hash, every hash matches, the checked-in
/// <c>SHA256SUMS</c> agrees with both, and no fixture declares personal data or a verified
/// protocol profile. The decoded fixtures carry invented bytes for an invented profile, and
/// must also declare that they are synthetic: a file that could pass for a real capture must
/// not load.
/// </summary>
public sealed class FixtureIntegrityTests
{
    private static string Directory => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>Every invented profile a decoded fixture is allowed to name.</summary>
    private static readonly string[] SyntheticProfileIds = { "synthetic-v1", "synthetic-cn-shape-v1" };

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

    /// <summary>Every decoded fixture file, relative to the fixture directory, as xunit theory data.</summary>
    public static TheoryData<string> DecodedFixtures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in System.IO.Directory
                         .GetFiles(Path.Combine(Directory, "decoded"), "*.decoded.json")
                         .Order(StringComparer.Ordinal))
            {
                data.Add("decoded/" + Path.GetFileName(path));
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

    [Fact]
    public void EveryExpectedDecodedFixtureIsPresent()
    {
        var names = System.IO.Directory.GetFiles(Path.Combine(Directory, "decoded"), "*.decoded.json")
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Superset(
            new HashSet<string?>(StringComparer.Ordinal)
            {
                "synthetic_complete.decoded.json",
                "synthetic_left.decoded.json",
                "synthetic_cancelled.decoded.json",
                "synthetic_non_mentor.decoded.json",
                "synthetic_duplicates.decoded.json",
                "synthetic_len_mismatch.decoded.json",
                "synthetic_offset_oob.decoded.json",
                "synthetic_unknown_opcode.decoded.json",
                "synthetic_constraint_fail.decoded.json",
                "synthetic_build_mismatch.decoded.json",
                "synthetic_territory_duty.decoded.json",
                "synthetic_duty_clear.decoded.json",
                "synthetic_duty_no_clear.decoded.json",
                "synthetic_duty_joined.decoded.json",
            },
            names);
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    [MemberData(nameof(DecodedFixtures))]
    public void FixtureMatchesItsSidecarHash(string relativePath)
    {
        var path = Path.Combine(Directory, relativePath);

        var sidecar = path + ".sha256";
        Assert.True(File.Exists(sidecar), "missing sidecar for " + relativePath);
        Assert.Equal(Sha256Hex(path), File.ReadAllText(sidecar).Trim().ToLowerInvariant());
    }

    [Theory]
    [InlineData("", "*.fixture.json")]
    [InlineData("decoded", "*.decoded.json")]
    public void Sha256SumsAgreesWithEveryFixture(string subdirectory, string pattern)
    {
        var directory = Path.Combine(Directory, subdirectory);
        var manifest = Path.Combine(directory, "SHA256SUMS");
        Assert.True(File.Exists(manifest));

        var recorded = File.ReadAllLines(manifest)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split("  ", 2, StringSplitOptions.None))
            .ToDictionary(parts => parts[1].Trim(), parts => parts[0].Trim(), StringComparer.Ordinal);

        var files = System.IO.Directory.GetFiles(directory, pattern);
        Assert.Equal(files.Length, recorded.Count);

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            Assert.True(recorded.ContainsKey(name), "SHA256SUMS is missing " + name);
            Assert.Equal(Sha256Hex(path), recorded[name]);
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

    [Theory]
    [MemberData(nameof(DecodedFixtures))]
    public void DecodedFixtureLoadsAndDeclaresItselfSynthetic(string relativePath)
    {
        var fixture = DecodedFixtureLoader.Load(Path.Combine(Directory, relativePath));

        Assert.Equal(Path.GetFileName(relativePath).Replace(".decoded.json", string.Empty, StringComparison.Ordinal),
            fixture.FixtureId);

        // A decoded fixture may only ask for one of the invented profiles. Naming a shipped
        // one would make a file in the test tree replayable against real opcodes, which
        // tests/Fixtures/README.md forbids.
        Assert.Contains(fixture.ProfileId, SyntheticProfileIds);
        Assert.NotEmpty(fixture.Messages);
        Assert.All(fixture.Messages, message =>
            Assert.Equal(fixture.CaptureSessionId, message.CaptureSessionId));
    }

    [Fact]
    public void AFixtureThatDoesNotDeclareItselfSyntheticIsRefused()
    {
        using var temp = new TempDirectory("DecodedTamper", strict: true);
        var path = temp.Combine("fake.decoded.json");
        var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Directory, "decoded", "synthetic_complete.decoded.json")));
        var text = document.RootElement.GetRawText()
            .Replace("\"synthetic\":true", "\"synthetic\":false", StringComparison.Ordinal)
            .Replace("\"synthetic\": true", "\"synthetic\": false", StringComparison.Ordinal);
        File.WriteAllText(path, text);
        File.WriteAllText(path + ".sha256", Sha256Hex(path));

        var error = Assert.Throws<InvalidDataException>(() => DecodedFixtureLoader.Load(path));

        Assert.Contains("synthetic", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "malformed")]
    [InlineData(true, "malformed")]
    [InlineData(false, "uppercase")]
    [InlineData(true, "uppercase")]
    public void LoadChecksSidecarBeforeParsingAndAcceptsNormalizedHash(bool decoded, string sidecar)
    {
        using var temp = new TempDirectory("FixtureSidecar", strict: true);
        var source = decoded
            ? Path.Combine(Directory, "decoded", "synthetic_complete.decoded.json")
            : Path.Combine(Directory, "mentor_left_v1.fixture.json");
        var copy = temp.Combine(Path.GetFileName(source));
        // Invalid JSON distinguishes integrity checking from document parsing on failure.
        File.WriteAllBytes(copy, sidecar == "uppercase" ? File.ReadAllBytes(source) : "not JSON"u8.ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(copy)));
        if (sidecar != "missing")
            File.WriteAllText(copy + ".sha256", sidecar == "uppercase" ? "  " + hash + "\r\n" : "not-a-hash");

        string LoadHash() => decoded ? DecodedFixtureLoader.Load(copy).Sha256 : ReplayFixtureLoader.Load(copy).Sha256;
        if (sidecar == "uppercase")
        {
            Assert.Equal(hash.ToLowerInvariant(), LoadHash());
        }
        else
        {
            var prefix = decoded ? "decoded fixture" : "fixture";
            var error = Assert.Throws<InvalidDataException>(() => LoadHash());
            Assert.Equal(prefix + (sidecar == "missing"
                ? " SHA-256 sidecar is missing: " + copy + ".sha256"
                : " SHA-256 mismatch"), error.Message);
        }
    }

    [Theory]
    [InlineData("mentor_left_v1.fixture.json", "\"roulette_id\": 42", "\"roulette_id\": 43")]
    [InlineData("decoded/synthetic_complete.decoded.json", "61441", "61442")]
    public void TamperingWithAFixtureIsDetected(string relativePath, string original, string edited)
    {
        using var temp = new TempDirectory("FixtureTamper", strict: true);
        var source = Path.Combine(Directory, relativePath);
        var copy = temp.Combine(Path.GetFileName(source));
        File.Copy(source, copy);
        File.Copy(source + ".sha256", copy + ".sha256");

        File.WriteAllText(copy, File.ReadAllText(copy).Replace(original, edited, StringComparison.Ordinal));

        var decoded = relativePath.EndsWith(".decoded.json", StringComparison.Ordinal);
        var error = Assert.Throws<InvalidDataException>(() =>
            decoded ? DecodedFixtureLoader.Load(copy).Sha256 : ReplayFixtureLoader.Load(copy).Sha256);
        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
    }

    private static string Sha256Hex(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
