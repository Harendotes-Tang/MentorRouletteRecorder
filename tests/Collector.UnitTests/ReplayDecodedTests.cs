using System.Security.Cryptography;
using System.Text.Json;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Replay;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The decoded fixtures carry invented bytes for an invented profile, and their integrity is
/// itself under test: sidecar hashes, the manifest, and the declaration that they are
/// synthetic. A file that could pass for a real capture must not load.
/// </summary>
[Collection(ConsoleOutputCollection.Name)]
public sealed class ReplayDecodedTests
{
    private static string Directory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "decoded");

    private static string ProfilePath => Path.Combine(
        AppContext.BaseDirectory, "protocol-profiles", "synthetic", "synthetic-v1.json");

    /// <summary>Every invented profile a decoded fixture is allowed to name.</summary>
    private static readonly string[] SyntheticProfileIds = { "synthetic-v1", "synthetic-cn-shape-v1" };

    /// <summary>Every decoded fixture file, as xunit theory data.</summary>
    public static TheoryData<string> DecodedFixtures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in System.IO.Directory
                         .GetFiles(Directory, "*.decoded.json")
                         .Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetFileName(path));
            }

            return data;
        }
    }

    [Fact]
    public void EveryExpectedDecodedFixtureIsPresent()
    {
        var names = System.IO.Directory.GetFiles(Directory, "*.decoded.json")
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
            },
            names);
    }

    [Theory]
    [MemberData(nameof(DecodedFixtures))]
    public void DecodedFixtureMatchesItsSidecarHash(string fileName)
    {
        var path = Path.Combine(Directory, fileName);
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        Assert.True(File.Exists(path + ".sha256"), "missing sidecar for " + fileName);
        Assert.Equal(actual, File.ReadAllText(path + ".sha256").Trim().ToLowerInvariant());
    }

    [Fact]
    public void Sha256SumsAgreesWithEveryDecodedFixture()
    {
        var manifest = Path.Combine(Directory, "SHA256SUMS");
        Assert.True(File.Exists(manifest));
        var recorded = File.ReadAllLines(manifest)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split("  ", 2, StringSplitOptions.None))
            .ToDictionary(parts => parts[1].Trim(), parts => parts[0].Trim(), StringComparer.Ordinal);

        var files = System.IO.Directory.GetFiles(Directory, "*.decoded.json");
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
    [MemberData(nameof(DecodedFixtures))]
    public void DecodedFixtureLoadsAndDeclaresItselfSynthetic(string fileName)
    {
        var fixture = DecodedFixtureLoader.Load(Path.Combine(Directory, fileName));

        Assert.Equal(fileName.Replace(".decoded.json", string.Empty, StringComparison.Ordinal),
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
        var directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.DecodedTamper", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "fake.decoded.json");
            var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(Directory, "synthetic_complete.decoded.json")));
            var text = document.RootElement.GetRawText()
                .Replace("\"synthetic\":true", "\"synthetic\":false", StringComparison.Ordinal)
                .Replace("\"synthetic\": true", "\"synthetic\": false", StringComparison.Ordinal);
            File.WriteAllText(path, text);
            File.WriteAllText(
                path + ".sha256",
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());

            var error = Assert.Throws<InvalidDataException>(() => DecodedFixtureLoader.Load(path));

            Assert.Contains("synthetic", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EditingADecodedFixtureIsDetected()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.DecodedTamper", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(Directory, "synthetic_complete.decoded.json");
            var copy = Path.Combine(directory, "synthetic_complete.decoded.json");
            File.Copy(source, copy);
            File.Copy(source + ".sha256", copy + ".sha256");
            File.WriteAllText(copy, File.ReadAllText(copy).Replace("61441", "61442", StringComparison.Ordinal));

            var error = Assert.Throws<InvalidDataException>(() => DecodedFixtureLoader.Load(copy));

            Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DecodedReplayOfTheCompleteFixtureProducesACompletedRun()
    {
        var result = DecodedReplayRunner.Run(
            Path.Combine(Directory, "synthetic_complete.decoded.json"), ProfilePath, TempDatabase());

        Assert.Equal(RunState.Completed, result.FinalState);
        Assert.True(result.BuildMatched);
        Assert.True(result.ProfileUsable);
        Assert.Equal(4, result.Parser.ParseOk);
        Assert.Equal(0, result.Parser.ParseFailed);
        var run = Assert.Single(result.Runs);
        Assert.Equal(RunResult.Completed, run.Result);
        Assert.Equal(120_000, run.DurationMs);
        Assert.Equal(19, run.JobId);
    }

    [Fact]
    public void DecodedReplayIsIdempotent()
    {
        var databasePath = TempDatabase();
        var first = DecodedReplayRunner.Run(
            Path.Combine(Directory, "synthetic_complete.decoded.json"), ProfilePath, databasePath);

        var second = DecodedReplayRunner.Run(
            Path.Combine(Directory, "synthetic_complete.decoded.json"), ProfilePath, databasePath);

        Assert.Equal(first.Runs, second.Runs);
        Assert.True(second.Writes.IdempotentReplay);
        Assert.Equal(0, second.Writes.RunsCreated);
        Assert.Equal(0, second.Writes.EventsAppended);
        Assert.Equal(1, second.Statistics.AttemptCount);
    }

    [Fact]
    public void DecodedReplayResolvesTheProfileNamedByTheFixtureWhenNoneIsGiven()
    {
        var result = DecodedReplayRunner.Run(
            Path.Combine(Directory, "synthetic_complete.decoded.json"), null, TempDatabase());

        Assert.Equal("synthetic-v1", result.ProfileId);
        Assert.Equal(RunState.Completed, result.FinalState);
    }

    [Fact]
    public void Program_ReplayDecodedModeWritesJsonResult()
    {
        var original = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var exitCode = Program.Main(new[]
            {
                "--replay-decoded", Path.Combine(Directory, "synthetic_complete.decoded.json"),
                "--profile", ProfilePath,
                "--db", TempDatabase(),
            });
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Contains("\"final_state\": \"COMPLETED\"", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"parse_ok\": 4", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Program_ValidateProfileReportsSuccessAndFailure()
    {
        Assert.Equal(0, RunSilently("--validate-profile", ProfilePath));
        Assert.Equal(1, RunSilently(
            "--validate-profile",
            Path.Combine(Directory, "synthetic_complete.decoded.json")));
    }

    [Fact]
    public void Program_ListProfilesPrintsEveryInstalledProfile()
    {
        var original = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            Assert.Equal(0, Program.Main(new[] { "--list-profiles" }));
        }
        finally
        {
            Console.SetOut(original);
        }

        var text = output.ToString();
        Assert.Contains("synthetic-v1", text, StringComparison.Ordinal);
        Assert.Contains("cn-unsupported", text, StringComparison.Ordinal);
        Assert.Contains("global-unsupported", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_RejectsProfileFlagOutsideDecodedReplay()
    {
        Assert.Throws<FormatException>(() =>
            CommandLineOptions.Parse(new[] { "--replay", "x.fixture.json", "--profile", "p.json" }));
    }

    private static int RunSilently(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            return Program.Main(args);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static string TempDatabase() => Path.Combine(
        Path.GetTempPath(),
        "MentorRecorder.DecodedReplay",
        Guid.NewGuid().ToString("N"),
        "replay.db");
}
