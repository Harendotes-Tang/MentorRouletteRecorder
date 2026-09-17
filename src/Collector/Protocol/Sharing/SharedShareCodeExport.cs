using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>Why the profile in force gives no share code; <c>details.reason</c> of <c>ERR_SHARE_CODE_UNAVAILABLE</c>.</summary>
public enum SharedShareCodeRefusal
{
    /// <summary>No profile is in force.</summary>
    NoProfile,

    /// <summary>The profile in force shipped with the software.</summary>
    NotLocal,

    /// <summary>The profile in force came from another player; shared calibrations are not passed on (plan §5).</summary>
    Shared,

    /// <summary>The local profile cannot be used, read back unchanged, or rebuilt into a code.</summary>
    NotShareable,
}

/// <summary>A share code ready for 分享给其他玩家.</summary>
/// <param name="Code">The code text.</param>
/// <param name="CodeSha256">Its identity.</param>
/// <param name="IssueUrl">The public repository's issue form, prefilled with the title and, when it fits, the code.</param>
/// <param name="CodeInUrl">False when the code was left out of <paramref name="IssueUrl"/> to stay under the length the form accepts.</param>
public sealed record SharedShareCode(string Code, string CodeSha256, string IssueUrl, bool CodeInUrl);

/// <summary>The answer to <c>GetCalibrationShareCode</c>: a code, or why there is none.</summary>
/// <param name="ShareCode">The code, when there is one.</param>
/// <param name="Refusal">Why there is none, otherwise.</param>
/// <param name="Message">What to tell the player when there is none, in Chinese.</param>
public sealed record SharedShareCodeResult(SharedShareCode? ShareCode, SharedShareCodeRefusal? Refusal, string? Message)
{
    internal static SharedShareCodeResult Refused(SharedShareCodeRefusal refusal) => new(null, refusal, refusal switch
    {
        SharedShareCodeRefusal.NoProfile => "当前没有正在使用的协议档案，没有可以分享的校准。",
        SharedShareCodeRefusal.NotLocal => "当前使用的是随包档案，不是本机校准出来的，不需要分享。",
        SharedShareCodeRefusal.Shared => "当前使用的是其他玩家分享的校准，不能再次分享。",
        _ => "本机校准的档案暂时不能使用、读不出来或已被改动，无法生成校准码。",
    });
}

/// <summary>
/// The share code of the local calibration in force. The code comes from
/// the profile file this machine wrote, read back from disk and required to still be the profile in force, never
/// from a draft in memory; <see cref="SharedProfileBuilder.ToShareCode"/> then refuses anything that is not
/// exactly a calibrated shape of the template.
/// </summary>
public static class SharedShareCodeExport
{
    /// <summary>Takes the code, or says why there is none. Reads the file and the template; call it off any lock.</summary>
    /// <param name="selection">The formal selection in force.</param>
    /// <param name="selectTemplate">The shipped template for a region.</param>
    public static SharedShareCodeResult From(ProfileSelection selection, Func<Region, CalibrationTemplate?> selectTemplate)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(selectTemplate);
        if (selection.Profile is not { } inForce)
        {
            return SharedShareCodeResult.Refused(SharedShareCodeRefusal.NoProfile);
        }

        if (selection.Origin != ProfileOrigin.Local)
        {
            return SharedShareCodeResult.Refused(
                selection.Origin == ProfileOrigin.Shared ? SharedShareCodeRefusal.Shared : SharedShareCodeRefusal.NotLocal);
        }

        // A local profile whose binding is refused - one that infers the match without its territory message -
        // records nothing on this machine and is not passed on to anyone else's either.
        if (!selection.IsUsable)
        {
            return SharedShareCodeResult.Refused(SharedShareCodeRefusal.NotShareable);
        }

        ProtocolProfile written;
        CalibrationTemplate? template;
        try
        {
            written = ProfileLoader.Load(inForce.SourcePath);
            template = selectTemplate(inForce.Region);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return SharedShareCodeResult.Refused(SharedShareCodeRefusal.NotShareable);
        }

        if (template is null || !string.Equals(written.ProfileSha256, inForce.ProfileSha256, StringComparison.Ordinal) ||
            SharedProfileBuilder.ToShareCode(written, template) is not { Code: { } code, CodeSha256: { } sha })
        {
            return SharedShareCodeResult.Refused(SharedShareCodeRefusal.NotShareable);
        }

        var (url, inUrl) = SharedCalibrationIssueLink.For(written.Region, written.GameBuild, code);
        return new SharedShareCodeResult(new SharedShareCode(code, sha, url, inUrl), null, null);
    }
}
