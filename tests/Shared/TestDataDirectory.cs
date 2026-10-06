using System.Runtime.CompilerServices;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.Tests;

/// <summary>
/// Keeps every test run out of the real %LOCALAPPDATA%\MentorRecorder.
///
/// scripts/test.ps1 points <c>MR_DATA_DIR</c> at a scratch folder before it starts the test
/// projects; a bare <c>dotnet test</c> -- an IDE, one class run from the command line -- did not,
/// and everything that resolves <see cref="DatabasePaths.RootDirectory"/> then read and wrote the
/// real user's folder: the profile catalogues, the calibration and shared-calibration stores, the
/// default export folder (the stray candidate-evidence files of 2026-10). A module initializer
/// runs before any code of the test assembly, so no test reaches the root first. It gives such a
/// run a fresh folder of its own under %TEMP% and removes it when the test host exits. A value
/// already set, scripts/test.ps1's, is left alone; the tests that point the variable elsewhere for
/// a moment restore whatever they found, so they restore this one.
/// </summary>
internal static class TestDataDirectory
{
    [ModuleInitializer]
    internal static void Isolate()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DatabasePaths.DataDirectoryVariable)))
        {
            return;
        }

        var directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.TestDataDir." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable(DatabasePaths.DataDirectoryVariable, directory);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Remove(directory);
    }

    private static void Remove(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A handle a child process still holds; the temp cleaner will get it.
        }
    }
}
