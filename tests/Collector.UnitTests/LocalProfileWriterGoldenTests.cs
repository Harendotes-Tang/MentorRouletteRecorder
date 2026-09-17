using MentorRecorder.Collector.Protocol.Calibration;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Pins what <see cref="LocalProfileWriter"/> writes, byte for byte, for every way a draft can
/// name the match and for the drafts that leave the optional messages out.
///
/// Shared calibration rebuilds a profile from a handful of learned values by running the same
/// "template + learned values -> messages" transform the draft uses. That transform lives
/// outside <see cref="CalibrationDraft"/>, and moving it must not change a single byte of a
/// profile a player already has on disk; these goldens are what holds that.
/// </summary>
public sealed class LocalProfileWriterGoldenTests : IDisposable
{
    private static readonly DateTimeOffset Confirmed = new(2026, 9, 9, 12, 30, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.Tests", "golden-" + Guid.NewGuid().ToString("N"), "protocol-profiles");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var name in CalibrationTrafficCases.All)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheWrittenProfileIsByteForByteTheGolden(string name)
    {
        var template = CalibrationObserverTests.Template();
        var draft = CalibrationTrafficCases.Derive(name);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(CalibrationTrafficCases.Source(name), draft.MatchSource);
        Assert.Equal(CalibrationTrafficCases.ExpectedMessages(name), draft.Messages.Select(message => message.Name));

        var result = LocalProfileWriter.Write(draft, template, CalibrationTrafficCases.Build, Confirmed, _root);
        var actual = File.ReadAllBytes(result.Path);

        var golden = Path.Combine(AppContext.BaseDirectory, "Fixtures", "calibration-golden", name + ".json");
        if (File.Exists(golden) && File.ReadAllBytes(golden).AsSpan().SequenceEqual(actual))
        {
            return;
        }

        // Leave the actual bytes where a diff tool can reach them; the assertion stays strict.
        var dump = Path.Combine(Path.GetTempPath(), "MentorRecorder.Tests", "golden-actual");
        Directory.CreateDirectory(dump);
        File.WriteAllBytes(Path.Combine(dump, name + ".json"), actual);
        Assert.Fail($"profile for '{name}' differs from {golden}; actual output written to {dump}");
    }
}
