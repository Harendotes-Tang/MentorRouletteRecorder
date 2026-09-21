using System.Runtime.InteropServices;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// Reads a process's executable path without opening a handle to that process.
///
/// <see cref="System.Diagnostics.Process.MainModule"/> (BOUNDARY-ALLOW: named here only to say
/// why it is not used) enumerates the target's modules and so needs
/// <c>PROCESS_QUERY_INFORMATION | PROCESS_VM_READ</c>. That is a handle on the game, which
/// docs/privacy-boundary.md section 2, rule 3b forbids outright, so it is not a matter of
/// preference: rule INJ-008 of the static boundary check refuses the property anywhere in the
/// repository, and the last fallback that still called it was deleted by the 2026-09-21 audit
/// (finding 1). It would in any case fail on a client started by an elevated launcher (the CN
/// launcher runs as administrator and the game inherits that token), which denies those rights
/// to an ordinary user process.
///
/// <c>NtQuerySystemInformation(SystemProcessIdInformation)</c> returns the image's NT path from
/// the kernel's process table instead: no handle is opened, nothing inside the target is read,
/// and the target's elevation does not matter. <c>QueryDosDevice</c> maps that NT path
/// (<c>\Device\HarddiskVolume3\...</c>) back to a drive letter. A process-table lookup without
/// a process handle stays inside docs/privacy-boundary.md section 2, item 3b.
/// </summary>
internal static class ProcessImagePath
{
    /// <summary>SYSTEM_INFORMATION_CLASS value of <c>SystemProcessIdInformation</c>.</summary>
    private const int SystemProcessIdInformation = 88;

    /// <summary>NTSTATUS returned when the name buffer is too small; the needed size is echoed back.</summary>
    private const uint StatusInfoLengthMismatch = 0xC0000004;

    /// <summary>First name buffer size in bytes (512 UTF-16 characters covers every normal path).</summary>
    private const int InitialNameBytes = 1024;

    /// <summary>
    /// Largest name buffer tried. Half of <see cref="ushort.MaxValue"/> on purpose: the size is
    /// stored in a UNICODE_STRING's 16-bit MaximumLength, and one more doubling from here still
    /// fits, so the cast in <see cref="ReadNtPath"/> can never wrap.
    /// </summary>
    internal const int MaxNameBytes = 32 * 1024;

    /// <summary>Longest device mapping accepted from <c>QueryDosDevice</c>, in characters.</summary>
    private const int DeviceNameChars = 512;

    /// <summary>Native <c>SYSTEM_PROCESS_ID_INFORMATION</c>: a pid followed by a <c>UNICODE_STRING</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemProcessIdInformationData
    {
        public IntPtr ProcessId;
        public ushort ImageNameLength;
        public ushort ImageNameMaximumLength;
        public IntPtr ImageNameBuffer;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint NtQuerySystemInformation(
        int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string deviceName, char[] targetPath, uint targetPathChars);

    /// <summary>
    /// Returns the drive-letter path of the given process's executable, or null when the kernel
    /// does not answer (no such process, a non-Windows host, or a volume with no drive letter).
    /// Never throws.
    /// </summary>
    /// <param name="processId">Process identifier.</param>
    public static string? TryRead(int processId)
    {
        if (processId <= 0 || !OperatingSystem.IsWindows())
        {
            return null;
        }

        string? ntPath;
        try
        {
            ntPath = ReadNtPath(processId);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or MarshalDirectiveException or OutOfMemoryException)
        {
            return null;
        }

        return ntPath is null ? null : ToDosPath(ntPath, QueryDosDevice);
    }

    /// <summary>
    /// Maps an NT image path (<c>\Device\HarddiskVolume3\Games\...</c>) to a drive-letter path
    /// by asking each drive letter what device it points at. Returns null when no drive letter
    /// maps to the path's device.
    /// </summary>
    /// <param name="ntPath">Path as returned by the kernel.</param>
    /// <param name="queryDosDevice">Resolves <c>"C:"</c> to its device name; null when unmapped.</param>
    internal static string? ToDosPath(string ntPath, Func<string, string?> queryDosDevice)
    {
        ArgumentNullException.ThrowIfNull(queryDosDevice);
        if (string.IsNullOrEmpty(ntPath))
        {
            return null;
        }

        if (ntPath.Length >= 3 && char.IsAsciiLetter(ntPath[0]) && ntPath[1] == ':' && ntPath[2] == '\\')
        {
            return ntPath;
        }

        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var drive = string.Create(2, letter, static (span, c) => { span[0] = c; span[1] = ':'; });
            var device = SafeQuery(queryDosDevice, drive);
            if (string.IsNullOrEmpty(device))
            {
                continue;
            }

            // The separator check keeps HarddiskVolume1 from claiming HarddiskVolume10's paths.
            if (ntPath.Length > device.Length
                && ntPath.StartsWith(device, StringComparison.OrdinalIgnoreCase)
                && ntPath[device.Length] == '\\')
            {
                return string.Concat(drive, ntPath.AsSpan(device.Length));
            }
        }

        return null;
    }

    /// <summary>Asks the kernel for the image path of a pid. Grows the buffer once when told to.</summary>
    /// <param name="processId">Process identifier.</param>
    internal static string? ReadNtPath(int processId)
    {
        var headerSize = Marshal.SizeOf<SystemProcessIdInformationData>();
        var nameBytes = InitialNameBytes;
        while (nameBytes <= MaxNameBytes)
        {
            var block = Marshal.AllocHGlobal(headerSize + nameBytes);
            try
            {
                var request = new SystemProcessIdInformationData
                {
                    ProcessId = (IntPtr)processId,
                    ImageNameLength = 0,
                    ImageNameMaximumLength = (ushort)nameBytes,
                    ImageNameBuffer = block + headerSize,
                };
                Marshal.StructureToPtr(request, block, false);

                var status = NtQuerySystemInformation(SystemProcessIdInformation, block, headerSize, out _);
                var reply = Marshal.PtrToStructure<SystemProcessIdInformationData>(block);
                if (status == StatusInfoLengthMismatch)
                {
                    nameBytes = NextNameBytes(nameBytes, reply.ImageNameMaximumLength);
                    continue;
                }

                if (status != 0 || reply.ImageNameLength == 0 || reply.ImageNameBuffer == IntPtr.Zero)
                {
                    return null;
                }

                return Marshal.PtrToStringUni(reply.ImageNameBuffer, reply.ImageNameLength / sizeof(char));
            }
            finally
            {
                Marshal.FreeHGlobal(block);
            }
        }

        return null;
    }

    /// <summary>
    /// Picks the next buffer size after a length-mismatch reply. The kernel echoes the size it
    /// needs in MaximumLength; that is used when it is an actual increase, otherwise the buffer
    /// doubles so the loop always makes progress and reaches <see cref="MaxNameBytes"/>.
    /// </summary>
    /// <param name="currentBytes">Size of the buffer that was too small.</param>
    /// <param name="kernelHint">MaximumLength as written back by the kernel.</param>
    internal static int NextNameBytes(int currentBytes, ushort kernelHint) =>
        kernelHint > currentBytes ? kernelHint : currentBytes * 2;

    private static string? SafeQuery(Func<string, string?> queryDosDevice, string drive)
    {
        try
        {
            return queryDosDevice(drive);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    private static string? QueryDosDevice(string drive)
    {
        var buffer = new char[DeviceNameChars];
        var written = QueryDosDeviceW(drive, buffer, (uint)buffer.Length);
        if (written == 0)
        {
            return null;
        }

        // The answer is a MULTI_SZ; the first entry is the current mapping.
        var end = Array.IndexOf(buffer, '\0');
        return end <= 0 ? null : new string(buffer, 0, end);
    }
}
