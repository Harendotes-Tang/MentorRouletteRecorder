using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace MentorRecorder.Collector.Capture;

/// <summary>One row of the kernel's process table.</summary>
/// <param name="ProcessId">Process identifier.</param>
/// <param name="ImageName">
/// Image name as the table records it, such as <c>ffxiv_dx11.exe</c>; empty for the idle process.
/// </param>
/// <param name="CreatedAtUtc">Creation time, or null when the row carries none.</param>
internal sealed record ProcessTableEntry(int ProcessId, string ImageName, DateTimeOffset? CreatedAtUtc);

/// <summary>
/// Lists running processes, with their creation times, without opening a handle to any of them.
///
/// A process's start time is otherwise read with <c>GetProcessTimes</c>, which needs a
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> handle on the target; that is a handle on the game,
/// which docs/privacy-boundary.md section 2, rule 3b forbids. The start time is part of the
/// client's identity (a process id alone is reused by Windows), so it is read here instead:
/// <c>NtQuerySystemInformation(SystemProcessInformation)</c> returns one snapshot of the
/// kernel's process table, and each row already carries the process id, the image name and the
/// creation time. No handle is opened, nothing inside any process is read, and the target's
/// elevation does not matter. It is the same table <see cref="ProcessImagePath"/> asks for a
/// single image path.
///
/// The row layout read is the x64 one, the only build this product ships; on any other process
/// width the reader answers "unavailable" rather than guessing offsets.
/// </summary>
internal static class ProcessTable
{
    /// <summary>SYSTEM_INFORMATION_CLASS value of <c>SystemProcessInformation</c>.</summary>
    private const int SystemProcessInformation = 5;

    /// <summary>NTSTATUS returned when the buffer is too small; the needed size is echoed back.</summary>
    private const uint StatusInfoLengthMismatch = 0xC0000004;

    /// <summary>First buffer size; an ordinary desktop's table needs well under half of it.</summary>
    internal const int InitialBufferBytes = 512 * 1024;

    /// <summary>Largest buffer tried before the read is reported as failed.</summary>
    internal const int MaxBufferBytes = 64 * 1024 * 1024;

    // Offsets in the x64 SYSTEM_PROCESS_INFORMATION (winternl.h; the 48 reserved bytes after
    // NumberOfThreads are WorkingSetPrivateSize, HardFaultCount, NumberOfThreadsHighWatermark,
    // CycleTime, CreateTime, UserTime and KernelTime).

    /// <summary><c>NextEntryOffset</c>, a ULONG; zero on the last row.</summary>
    internal const int NextEntryOffsetField = 0x00;

    /// <summary><c>CreateTime</c>, a LARGE_INTEGER in FILETIME units (100 ns since 1601, UTC).</summary>
    internal const int CreateTimeField = 0x20;

    /// <summary><c>ImageName.Length</c>, a USHORT in bytes.</summary>
    internal const int ImageNameLengthField = 0x38;

    /// <summary><c>ImageName.Buffer</c>, a pointer into the same output buffer.</summary>
    internal const int ImageNameBufferField = 0x40;

    /// <summary><c>UniqueProcessId</c>, a pointer-sized HANDLE.</summary>
    internal const int UniqueProcessIdField = 0x50;

    /// <summary>Bytes of a row this reader looks at; a shorter row is a malformed table.</summary>
    internal const int EntryHeaderBytes = 0x58;

    private static readonly long MaxFileTime = DateTime.MaxValue.ToFileTimeUtc();

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint NtQuerySystemInformation(
        int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

    /// <summary>
    /// Takes one snapshot of the process table, or returns null when it could not be read. Null
    /// is not "nothing is running": a caller deciding whether a process exited must read it as
    /// "cannot tell". Never throws.
    /// </summary>
    public static IReadOnlyList<ProcessTableEntry>? TryRead()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
        {
            return null;
        }

        try
        {
            return Read();
        }
        catch (Exception)
        {
            // A table that cannot be read is a failed listing for the caller to handle, never a
            // crash of the poll that asked: out of memory for the buffer, a missing export.
            return null;
        }
    }

    private static IReadOnlyList<ProcessTableEntry>? Read()
    {
        var size = InitialBufferBytes;
        while (size <= MaxBufferBytes)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var address = pin.AddrOfPinnedObject();
                var status = NtQuerySystemInformation(SystemProcessInformation, address, buffer.Length, out var written);
                if (status == StatusInfoLengthMismatch)
                {
                    size = NextBufferBytes(buffer.Length, written);
                    continue;
                }

                if (status != 0)
                {
                    return null;
                }

                // The rows lie inside what was written; every read below is bounded by it. A
                // length the kernel did not report bounds them by the buffer, which is ours.
                var length = written > 0 && written <= buffer.Length ? written : buffer.Length;
                return Parse(buffer.AsSpan(0, length), address.ToInt64());
            }
            finally
            {
                pin.Free();
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return null;
    }

    /// <summary>
    /// Picks the next buffer size after a length-mismatch reply: what the kernel asked for plus
    /// room for processes started in between, and never less than double, so the loop always
    /// makes progress towards <see cref="MaxBufferBytes"/>.
    /// </summary>
    /// <param name="currentBytes">Size of the buffer that was too small.</param>
    /// <param name="neededBytes">Size the kernel reported, or zero when it reported none.</param>
    internal static int NextBufferBytes(int currentBytes, int neededBytes) =>
        (int)Math.Min(int.MaxValue, Math.Max(currentBytes * 2L, neededBytes + (neededBytes / 4L)));

    /// <summary>
    /// Reads the rows out of one table snapshot. Returns null when the table is malformed -- a
    /// row or a name that would reach outside it -- because a partial list would read as
    /// processes having exited.
    /// </summary>
    /// <param name="table">The bytes the kernel wrote.</param>
    /// <param name="baseAddress">
    /// Address <paramref name="table"/> had when the kernel wrote it. Image names are returned
    /// as pointers into the same buffer, so they are turned back into offsets against this.
    /// </param>
    internal static IReadOnlyList<ProcessTableEntry>? Parse(ReadOnlySpan<byte> table, long baseAddress)
    {
        var entries = new List<ProcessTableEntry>();
        var offset = 0;
        while (true)
        {
            if (table.Length - offset < EntryHeaderBytes)
            {
                return null;
            }

            var row = table[offset..];
            var next = BinaryPrimitives.ReadUInt32LittleEndian(row[NextEntryOffsetField..]);
            var created = BinaryPrimitives.ReadInt64LittleEndian(row[CreateTimeField..]);
            var nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(row[ImageNameLengthField..]);
            var nameAddress = BinaryPrimitives.ReadInt64LittleEndian(row[ImageNameBufferField..]);
            var processId = BinaryPrimitives.ReadInt64LittleEndian(row[UniqueProcessIdField..]);
            if (processId is < 0 or > int.MaxValue)
            {
                return null;
            }

            var name = string.Empty;
            if (nameBytes > 0)
            {
                var start = nameAddress - baseAddress;
                if (nameAddress == 0 || nameBytes % sizeof(char) != 0 || start < 0 || start > table.Length - nameBytes)
                {
                    return null;
                }

                name = Encoding.Unicode.GetString(table.Slice((int)start, nameBytes));
            }

            entries.Add(new ProcessTableEntry((int)processId, name, CreationTime(created)));
            if (next == 0)
            {
                return entries;
            }

            if (next < EntryHeaderBytes || next > (uint)(table.Length - offset))
            {
                return null;
            }

            offset += (int)next;
        }
    }

    /// <summary>
    /// The process name as <c>System.Diagnostics.Process</c> reports it: the image name without
    /// any directory and without a trailing <c>.exe</c>.
    /// </summary>
    /// <param name="imageName">Image name from the table.</param>
    internal static string ShortName(string imageName)
    {
        var name = imageName.AsSpan();
        var slash = name.LastIndexOf('\\');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4].ToString()
            : name.ToString();
    }

    /// <summary>A FILETIME as a UTC instant; null for a row that carries none.</summary>
    /// <param name="fileTime">100 ns intervals since 1601-01-01 UTC.</param>
    private static DateTimeOffset? CreationTime(long fileTime) =>
        fileTime > 0 && fileTime <= MaxFileTime
            ? new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero)
            : null;
}
