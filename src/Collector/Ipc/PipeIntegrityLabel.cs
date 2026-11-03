using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace MentorRecorder.Collector.Ipc;

/// <summary>
/// The mandatory integrity label an elevated Collector puts on its pipe (audit 2026-11-03, G-1).
///
/// The pipe's DACL grants the current user alone. That keeps every other account out, but not the user's own
/// ordinary processes, and while the Collector runs at the user's normal (Medium) integrity it need not: any such
/// process could end the Collector or open the database itself. Run elevated - which three capture texts ask for when
/// Npcap is restricted to administrators - the Collector writes and reads files with administrator rights on request
/// (exports, backups, import previews), and Windows treats an object with no label as Medium, so a Medium process of
/// the same user could open the pipe and borrow those rights. A High label with no-write-up and no-read-up closes
/// that: only a process at High integrity or above, which could end the Collector anyway, may open it. Below High
/// nothing changes.
///
/// <see cref="PipeSecurity"/> keeps only the entries it knows and silently drops a label, so the label goes on the
/// handle through SetSecurityInfo. The caller creates the instance shut - its DACL denies the user - and
/// <see cref="Apply"/> puts the label and the real DACL on in one call, so no client can open the instance in between.
/// Windows keeps one security descriptor per pipe, set with its first instance: a later instance's own descriptor is
/// ignored, and it is refused the owner rights setting a label takes. A later instance is therefore served only when
/// <see cref="Holds"/> confirms the pipe still carries the label.
/// </summary>
internal static class PipeIntegrityLabel
{
    /// <summary>RID of the High mandatory level, <c>S-1-16-12288</c>.</summary>
    internal const int HighRid = 0x3000;

    private const AceType MandatoryLabelAceType = (AceType)0x11;
    private const int NoWriteUp = 0x1;
    private const int NoReadUp = 0x2;
    private const int TokenIntegrityLevel = 25;
    private const int KernelObject = 6;
    private const uint DaclSecurityInformation = 0x4;
    private const uint LabelSecurityInformation = 0x10;

    /// <summary>
    /// The level to label the pipe at for a process running at <paramref name="processRid"/>: High at High and above,
    /// none below, and none when the level could not be read - which leaves the pipe exactly as before.
    /// </summary>
    /// <param name="processRid">The RID of the process's mandatory level, or null when it is not known.</param>
    internal static int? For(int? processRid) => processRid >= HighRid ? HighRid : null;

    /// <summary>The level to label this process's pipe at.</summary>
    internal static int? ForThisProcess() => For(ProcessRid());

    /// <summary>
    /// Puts the label at <paramref name="rid"/> and the DACL of <paramref name="security"/> on the pipe instance in one
    /// call. Throws when Windows refuses: an instance that cannot be labelled must not be served.
    /// </summary>
    /// <param name="pipe">The server handle, opened with WRITE_DAC and WRITE_OWNER.</param>
    /// <param name="security">The grant the instance is to carry.</param>
    /// <param name="rid">The mandatory level, at or below this process's own.</param>
    internal static void Apply(SafePipeHandle pipe, PipeSecurity security, int rid)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var dacl = BinaryOf(descriptor.DiscretionaryAcl
            ?? throw new InvalidOperationException("the pipe's grant has no access list"));
        var error = SetSecurityInfo(
            pipe, KernelObject, DaclSecurityInformation | LabelSecurityInformation, IntPtr.Zero, IntPtr.Zero, dacl, BinaryOf(Label(rid)));
        if (error != 0)
        {
            throw new IOException("the pipe's integrity label could not be set", new Win32Exception((int)error));
        }
    }

    /// <summary>
    /// True when the pipe <paramref name="pipe"/> is an instance of carries a mandatory label at <paramref name="rid"/> or
    /// above with no write up and no read up. Throws when the pipe's security cannot be read.
    /// </summary>
    /// <param name="pipe">A server handle of the pipe.</param>
    /// <param name="rid">The lowest level accepted.</param>
    internal static bool Holds(SafePipeHandle pipe, int rid)
    {
        var error = GetSecurityInfo(pipe, KernelObject, LabelSecurityInformation,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor);
        if (error != 0)
        {
            throw new IOException("the pipe's integrity label could not be read", new Win32Exception((int)error));
        }

        try
        {
            var bytes = new byte[GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            foreach (var ace in new RawSecurityDescriptor(bytes, 0).SystemAcl ?? new RawAcl(GenericAcl.AclRevision, 0))
            {
                if (ace is CustomAce { AceType: MandatoryLabelAceType } label && label.GetOpaque() is { Length: > sizeof(int) } body)
                {
                    var policy = BinaryPrimitives.ReadInt32LittleEndian(body);
                    var level = new SecurityIdentifier(body, sizeof(int)).Value;
                    return (policy & (NoWriteUp | NoReadUp)) == (NoWriteUp | NoReadUp) &&
                           int.TryParse(level[(level.LastIndexOf('-') + 1)..], System.Globalization.NumberStyles.None,
                               System.Globalization.CultureInfo.InvariantCulture, out var found) && found >= rid;
                }
            }

            return false;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>A system access list holding one mandatory label at <paramref name="rid"/>: no write up, no read up.</summary>
    /// <param name="rid">The mandatory level.</param>
    internal static RawAcl Label(int rid)
    {
        var level = new SecurityIdentifier("S-1-16-" + rid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var body = new byte[sizeof(int) + level.BinaryLength];
        BinaryPrimitives.WriteInt32LittleEndian(body, NoWriteUp | NoReadUp);
        level.GetBinaryForm(body, sizeof(int));
        var acl = new RawAcl(GenericAcl.AclRevision, 1);
        acl.InsertAce(0, new CustomAce(MandatoryLabelAceType, AceFlags.None, body));
        return acl;
    }

    private static byte[] BinaryOf(GenericAcl acl)
    {
        var bytes = new byte[acl.BinaryLength];
        acl.GetBinaryForm(bytes, 0);
        return bytes;
    }

    /// <summary>The RID of this process's mandatory level; null when it cannot be read.</summary>
    private static int? ProcessRid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var token = identity.AccessToken;
        _ = GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);
        if (needed <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
            {
                return null;
            }

            // TOKEN_MANDATORY_LABEL starts with the SID pointer of its SID_AND_ATTRIBUTES; the level is the SID's last part.
            var level = new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
            return int.TryParse(level[(level.LastIndexOf('-') + 1)..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var rid)
                ? rid
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token, int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint SetSecurityInfo(
        SafePipeHandle handle, int objectType, uint securityInfo, IntPtr owner, IntPtr group, byte[] dacl, byte[] sacl);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetSecurityInfo(
        SafePipeHandle handle, int objectType, uint securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int GetSecurityDescriptorLength(IntPtr descriptor);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
