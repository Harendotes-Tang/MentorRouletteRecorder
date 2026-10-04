using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace MentorRecorder.Collector.Update;

/// <summary>
/// The installers the user asked to download, in <c>updates\</c> beside the database.
///
/// Every name is built here from a version that passed <see cref="UpdateMetadata.IsVersion"/> - three runs of ASCII
/// digits - and from nothing else: no part of a path ever comes from a server. <c>updates</c> must be a real
/// directory of ours: it is created when missing and refused when it is a link of any kind (a junction or a symbolic
/// link is a reparse point), so nothing is written or deleted through one. A file is only ever written under a name
/// that was first removed as a directory entry and then created new, so an existing link or hard link in its place is
/// replaced, never written through. Leftovers are removed by name only: a <c>.part</c> or an installer named like
/// ours, as a plain file - never anything else, never an entry that is itself a link.
///
/// A check by path proves nothing about the next use by path, so <c>updates</c> is held open for as long as it is
/// used: by a download from <see cref="Prepare"/> until it is done, and by each clean-up, removal and hash for its own
/// duration. Held means opened as the entry itself, never through a link, confirmed through that handle to be a plain
/// directory, and shared for reading and writing but not for deleting - so while it is held nobody can rename, remove
/// or replace it, nor any folder above it. That open is the one native call of the download (<see cref="Hold"/>).
/// </summary>
public sealed class UpdateDownloadFiles
{
    /// <summary>Folder name inside the data directory.</summary>
    public const string FolderName = "updates";

    /// <summary>Suffix of the file a download writes into before it is verified.</summary>
    public const string PartSuffix = ".part";

    private const string Prefix = "MentorRecorder-";
    private const string Suffix = "-setup.exe";
    private const int BufferBytes = 81920;

    // CreateFileW, for the one thing .NET does not open: a directory, as the entry itself. FILE_LIST_DIRECTORY is a
    // data access, which is what makes the sharing below binding on others; an open for attributes alone is not.
    private const uint ListDirectory = 0x0001;
    private const uint ReadAttributes = 0x0080;
    private const uint Synchronize = 0x0010_0000;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint OpenReparsePoint = 0x0020_0000;

    // Case-insensitive because the file system is: "MENTORRECORDER-1.0.0-SETUP.EXE" is the same file as ours.
    private static readonly Regex OwnName = new(
        @"^MentorRecorder-([0-9]+\.[0-9]+\.[0-9]+)-setup\.exe(\.part)?\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly EnumerationOptions OneLevel = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        MatchType = MatchType.Simple,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Creates the store for one data directory. Touches nothing.</summary>
    /// <param name="dataDirectory">The Collector's data directory: the folder holding the database.</param>
    public UpdateDownloadFiles(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory = Path.Combine(Path.GetFullPath(dataDirectory), FolderName);
    }

    /// <summary>Full path of <c>updates\</c>.</summary>
    public string Directory { get; }

    /// <summary>
    /// Called by <see cref="Promote"/> just before the rename, once the <c>.part</c> has been closed. A seam for the
    /// test that changes the file in that instant; null in the shipping Collector.
    /// </summary>
    internal Action? BeforePromote { get; set; }

    /// <summary>
    /// Called by <see cref="RemoveLeftovers"/> between listing <c>updates</c> and removing what it found. A seam for
    /// the test that tries to swap the folder in that instant; null in the shipping Collector.
    /// </summary>
    internal Action? BeforeRemoving { get; set; }

    /// <summary><c>MentorRecorder-&lt;version&gt;-setup.exe</c>, the installer's published name.</summary>
    /// <param name="version">A published version: three plain numbers.</param>
    /// <exception cref="ArgumentException">Anything else.</exception>
    public static string FileName(string version) =>
        UpdateMetadata.IsVersion(version)
            ? Prefix + version + Suffix
            : throw new ArgumentException("not a published version", nameof(version));

    /// <summary>Where the verified installer of a version is kept.</summary>
    /// <param name="version">A published version.</param>
    public string InstallerPath(string version) => Path.Combine(Directory, FileName(version));

    /// <summary>Where the installer of a version is written while it downloads.</summary>
    /// <param name="version">A published version.</param>
    public string PartPath(string version) => InstallerPath(version) + PartSuffix;

    /// <summary>
    /// Makes sure <c>updates</c> is a real directory, creating it when it is missing, and holds it (see the class
    /// summary) until the result is disposed.
    /// </summary>
    /// <returns>The hold; dispose it once the download is done with the folder.</returns>
    /// <exception cref="IOException">
    /// It exists as a file, or as a link of any kind; or it could not be created or opened.
    /// </exception>
    public IDisposable Prepare()
    {
        if (Attributes(Directory) is null)
        {
            System.IO.Directory.CreateDirectory(Directory);
        }

        return Hold(Directory) ?? throw new IOException("the updates folder is not a plain directory");
    }

    /// <summary>
    /// Opens a fresh <c>.part</c> for a version, for this process alone. Whatever stood under that name is removed as
    /// an entry first, so a link left there is not followed.
    /// </summary>
    /// <param name="version">A published version.</param>
    public FileStream CreatePart(string version)
    {
        var part = PartPath(version);
        File.Delete(part);
        return new FileStream(
            part, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    /// <summary>Gives the verified <c>.part</c> its final name, replacing an older file of that name.</summary>
    /// <param name="version">A published version.</param>
    /// <returns>The installer's path.</returns>
    public string Promote(string version)
    {
        var installer = InstallerPath(version);
        BeforePromote?.Invoke();
        File.Move(PartPath(version), installer, overwrite: true);
        return installer;
    }

    /// <summary>The length of a version's installer when it is there as a plain file; null otherwise.</summary>
    /// <param name="version">A published version.</param>
    public long? InstallerLength(string version)
    {
        try
        {
            var info = new FileInfo(InstallerPath(version));
            return info.Exists && (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                ? info.Length
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The SHA-256 of a version's installer as it is on disk now, in lower-case hex; null when it is not there as a
    /// plain file in a plain <c>updates</c>, or cannot be read. The folder is held and the file is open for reading
    /// with no other sharing than reading while it is hashed, so neither can change under the hash; a file someone
    /// else holds open for writing or deleting cannot be opened that way and has no hash.
    /// </summary>
    /// <param name="version">A published version.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<string?> HashInstallerAsync(string version, CancellationToken cancellationToken)
    {
        using var folder = Hold(Directory);
        if (folder is null || InstallerLength(version) is null)
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                InstallerPath(version), FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Removes what downloads left behind: every <c>.part</c> of ours, and every installer of ours except that of
    /// <paramref name="keep"/>. Nothing at all when <c>updates</c> is missing or is itself a link; held from the
    /// listing to the last removal. Best effort: an entry that cannot be removed - an installer that is running, say -
    /// is left for the next time.
    /// </summary>
    /// <param name="keep">The version whose installer stays; null to remove every installer.</param>
    /// <returns>How many entries were removed.</returns>
    public int RemoveLeftovers(string? keep = null)
    {
        using var folder = Hold(Directory);
        if (folder is null)
        {
            return 0;
        }

        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(Directory).EnumerateFileSystemInfos("*", OneLevel).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        BeforeRemoving?.Invoke();
        var removed = 0;
        foreach (var entry in entries)
        {
            if (IsLeftover(entry, keep) && TryDelete(entry.FullName))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Deletes a version's installer, with <c>updates</c> held for the removal; nothing when the folder is missing or
    /// is a link. Best effort, like every removal here.
    /// </summary>
    /// <param name="version">A published version.</param>
    public void DiscardInstaller(string version)
    {
        using var folder = Hold(Directory);
        if (folder is not null)
        {
            TryDelete(InstallerPath(version));
        }
    }

    /// <summary>Deletes a file; false when it could not be.</summary>
    /// <param name="path">File to delete; nothing happens when it is not there.</param>
    public static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsLeftover(FileSystemInfo entry, string? keep)
    {
        if ((entry.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            return false;
        }

        var match = OwnName.Match(entry.Name);
        return match.Success &&
               (match.Groups[2].Success || !string.Equals(match.Groups[1].Value, keep, StringComparison.Ordinal));
    }

    // The attributes of the entry itself: for a junction or a symbolic link, those of the link, never its target's.
    // Null when there is no such entry or it cannot be looked at, which every caller treats as "not ours to use".
    private static FileAttributes? Attributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens a folder for as long as the caller keeps the handle: the entry itself, never what a link there points
    /// to, and looked at through that handle. Shared for reading and writing but not for deleting, so while it is
    /// open the folder cannot be renamed, removed or replaced, nor can any folder above it; what is inside stays free
    /// to create, rename and delete. Null when it is missing, is a file or a link of any kind, or cannot be opened.
    /// </summary>
    /// <param name="path">Full path of the folder.</param>
    private static SafeFileHandle? Hold(string path)
    {
        var handle = CreateFileW(
            path, ListDirectory | ReadAttributes | Synchronize, FileShare.Read | FileShare.Write, IntPtr.Zero,
            FileMode.Open, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        try
        {
            var attributes = handle.IsInvalid ? (FileAttributes?)null : File.GetAttributes(handle);
            if (attributes is { } held && held.HasFlag(FileAttributes.Directory) &&
                !held.HasFlag(FileAttributes.ReparsePoint))
            {
                return handle;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be looked at through its own handle is not ours to use.
        }

        handle.Dispose();
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
