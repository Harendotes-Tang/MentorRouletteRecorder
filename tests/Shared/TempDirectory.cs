namespace MentorRecorder.Collector.Tests;

/// <summary>
/// A folder of its own for one test or one test class,
/// <c>&lt;parent&gt;\MentorRecorder.&lt;name&gt;\&lt;guid&gt;</c>; the parent is %TEMP% unless a test needs
/// another drive. Dispose removes the folder with everything in it. By default a file a child
/// process or SQLite's WAL still holds open leaves the folder behind instead of failing the test:
/// what is left is debris, not a result (the temp cleaner gets it under %TEMP%; under another
/// parent it stays until that folder is cleaned). A strict folder fails the test instead, for the
/// tests whose code under test must have closed every file it wrote there.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    private readonly bool _strict;

    /// <summary>Creates a fresh, empty folder.</summary>
    /// <param name="name">Names the debris: the folder sits in <c>MentorRecorder.&lt;name&gt;</c>.</param>
    /// <param name="parent">Where that folder sits; %TEMP% when null.</param>
    /// <param name="strict">Whether a folder Dispose cannot remove fails the test.</param>
    public TempDirectory(string name = "Tests", string? parent = null, bool strict = false)
        : this(Directory.CreateDirectory(UniquePath(name, parent)), strict)
    {
    }

    private TempDirectory(DirectoryInfo directory, bool strict)
    {
        Path = directory.FullName;
        _strict = strict;
    }

    /// <summary>The folder's full path.</summary>
    public string Path { get; }

    /// <summary>
    /// A unique path that nothing has created yet, for the tests that assert whether the code under
    /// test creates it. Dispose removes it if something did.
    /// </summary>
    /// <param name="name">Names the debris, as for the constructor.</param>
    /// <param name="parent">Where it would sit; %TEMP% when null.</param>
    /// <param name="strict">Whether a folder Dispose cannot remove fails the test.</param>
    public static TempDirectory Reserve(string name = "Tests", string? parent = null, bool strict = false) =>
        new(new DirectoryInfo(UniquePath(name, parent)), strict);

    /// <summary>A path inside the folder.</summary>
    /// <param name="parts">Path segments below <see cref="Path"/>.</param>
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        if (_strict)
        {
            // A handle the code under test left open throws here and fails the test.
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }

            return;
        }

        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never created, or a handle still open: debris for the temp cleaner, not a test failure.
        }
    }

    private static string UniquePath(string name, string? parent) =>
        System.IO.Path.Combine(
            parent ?? System.IO.Path.GetTempPath(), "MentorRecorder." + name, Guid.NewGuid().ToString("N"));
}
