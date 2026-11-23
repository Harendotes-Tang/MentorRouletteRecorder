namespace MentorRecorder.Collector.Tests;

/// <summary>Files of the repository the tests read where they are checked in, found from the test binary.</summary>
internal static class RepositoryFiles
{
    /// <summary>
    /// The file at <paramref name="parts"/> below the nearest folder above the test binary that has it.
    /// </summary>
    /// <param name="parts">Path segments from the repository root, for example <c>"contracts", "ipc-v1.schema.json"</c>.</param>
    public static string Find(params string[] parts)
    {
        var relative = Path.Combine(parts);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, relative);
            if (File.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(relative + " not found above " + AppContext.BaseDirectory, relative);
    }
}
