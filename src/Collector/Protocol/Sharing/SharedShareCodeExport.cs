using MentorRecorder.Collector.Contracts.Texts;
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

    /// <summary>
    /// The profile in force came from another player and this machine proved it, but the code it was written from is not
    /// kept here to hand on (plan §20.5): never downloaded - pasted by the player - or no longer intact. Until 2026-10-15
    /// this meant "shared calibrations are not passed on" and was given for every shared profile.
    /// </summary>
    Shared,

    /// <summary>
    /// The profile in force came from another player and is still being checked here: it has not yet recorded a complete
    /// duty with every audited criterion passed (plan §18.4), so this machine has nothing to vouch for (plan §20.5).
    /// </summary>
    SharedNotProven,

    /// <summary>The local profile cannot be used, read back unchanged, or rebuilt into a code.</summary>
    NotShareable,
}

/// <summary>
/// What the player filing a code says it rests on; prefilled into the issue form's <c>basis</c> text field (plan §20.4) as
/// <see cref="SharedCalibrationIssueLink.BasisOption"/>.
/// </summary>
public enum SharedShareBasis
{
    /// <summary>Calibrated on this machine, the timeline checked entry by entry. Wire token <c>CALIBRATED</c>.</summary>
    Calibrated,

    /// <summary>Another player's code that this machine verified and recorded with. Wire token <c>VERIFIED</c>.</summary>
    Verified,
}

/// <summary>A share code ready for 分享给其他玩家 or 这份校准好用，告诉其他玩家.</summary>
/// <param name="Code">The code text.</param>
/// <param name="CodeSha256">Its identity.</param>
/// <param name="IssueUrl">
/// The public repository's issue form, prefilled with the title, the basis and, when it fits, the code.
/// </param>
/// <param name="CodeInUrl">False when the code was left out of <paramref name="IssueUrl"/> to stay under the length the form accepts.</param>
/// <param name="Basis">What the code rests on: this machine's own calibration, or another player's code proven here.</param>
public sealed record SharedShareCode(
    string Code, string CodeSha256, string IssueUrl, bool CodeInUrl, SharedShareBasis Basis = SharedShareBasis.Calibrated);

/// <summary>
/// What the shared calibration session vouches for when the profile in force came from another player (plan §20.5): that
/// profile document is the one it bound or adopted, and it is proven. Taken under the pipeline's gate; nothing in it is
/// read from disk.
/// </summary>
/// <param name="ProfileSha256">Canonical hash of the proven profile document.</param>
/// <param name="CodeSha256">The code it was written from, when the session knows it; null for a settled profile adopted from disk.</param>
public sealed record SharedShareProof(string ProfileSha256, string? CodeSha256);

/// <summary>The answer to <c>GetCalibrationShareCode</c>: a code, or why there is none.</summary>
/// <param name="ShareCode">The code, when there is one.</param>
/// <param name="Refusal">Why there is none, otherwise.</param>
/// <param name="Text">What to tell the player when there is none, a catalogue text.</param>
public sealed record SharedShareCodeResult(SharedShareCode? ShareCode, SharedShareCodeRefusal? Refusal, UserText? Text)
{
    /// <summary>What to tell the player when there is none, in Simplified Chinese.</summary>
    public string? Message => Text?.Chinese;

    internal static SharedShareCodeResult Refused(SharedShareCodeRefusal refusal) => new(null, refusal, refusal switch
    {
        SharedShareCodeRefusal.NoProfile => CollectorTexts.SharingShareCodeNoProfile(),
        SharedShareCodeRefusal.NotLocal => CollectorTexts.SharingShareCodeNotLocal(),
        SharedShareCodeRefusal.Shared => CollectorTexts.SharingShareCodeSharedMissing(),
        SharedShareCodeRefusal.SharedNotProven => CollectorTexts.SharingShareCodeSharedNotProven(),
        _ => CollectorTexts.SharingShareCodeLocalUnusable(),
    });
}

/// <summary>
/// The share code of the calibration in force, in one of two ways.
///
/// A local calibration's code comes from the profile file this machine wrote, read back from disk and required to still
/// be the profile in force, never from a draft in memory; <see cref="SharedProfileBuilder.ToShareCode"/> then refuses
/// anything that is not exactly a calibrated shape of the template. Its basis is <see cref="SharedShareBasis.Calibrated"/>.
///
/// Another player's calibration is handed back only once this machine has proven it (plan §18.4, §20.5) - a complete duty
/// recorded and every audited criterion passed - so the repository can count one more account that verified the very same
/// code. The text is the one the store downloaded, not one rebuilt from the profile: it must be byte for byte the code the
/// repository already holds, and must still decode to the identity the session bound. Its basis is
/// <see cref="SharedShareBasis.Verified"/>. A code that is only being checked, or that the store does not hold, is not handed on.
/// </summary>
public static class SharedShareCodeExport
{
    /// <summary>
    /// As <see cref="From(ProfileSelection, Func{Region, CalibrationTemplate}, SharedShareProof, ISharedCalibrationStore)"/>
    /// with nothing vouched for and nothing stored: a shared profile is refused as not proven.
    /// </summary>
    /// <param name="selection">The formal selection in force.</param>
    /// <param name="selectTemplate">The shipped template for a region.</param>
    public static SharedShareCodeResult From(ProfileSelection selection, Func<Region, CalibrationTemplate?> selectTemplate) =>
        From(selection, selectTemplate, null, SharedCalibrationStore.Inert);

    /// <summary>Takes the code, or says why there is none. Reads files and the template; call it off any lock.</summary>
    /// <param name="selection">The formal selection in force.</param>
    /// <param name="selectTemplate">The shipped template for a region.</param>
    /// <param name="proof">What the shared session vouches for, taken with <paramref name="selection"/> under one gate; null when nothing.</param>
    /// <param name="store">Where the downloaded codes are kept.</param>
    public static SharedShareCodeResult From(
        ProfileSelection selection, Func<Region, CalibrationTemplate?> selectTemplate, SharedShareProof? proof, ISharedCalibrationStore store)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(selectTemplate);
        ArgumentNullException.ThrowIfNull(store);
        if (selection.Profile is not { } inForce)
        {
            return SharedShareCodeResult.Refused(SharedShareCodeRefusal.NoProfile);
        }

        return selection.Origin switch
        {
            ProfileOrigin.Local => FromLocal(selection, inForce, selectTemplate),
            ProfileOrigin.Shared => FromShared(selection, inForce, selectTemplate, proof, store),
            _ => SharedShareCodeResult.Refused(SharedShareCodeRefusal.NotLocal),
        };
    }

    private static SharedShareCodeResult FromLocal(
        ProfileSelection selection, ProtocolProfile inForce, Func<Region, CalibrationTemplate?> selectTemplate)
    {
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

        var (url, inUrl) = SharedCalibrationIssueLink.For(written.Region, written.GameBuild, code, SharedShareBasis.Calibrated);
        return new SharedShareCodeResult(new SharedShareCode(code, sha, url, inUrl, SharedShareBasis.Calibrated), null, null);
    }

    private static SharedShareCodeResult FromShared(
        ProfileSelection selection, ProtocolProfile inForce, Func<Region, CalibrationTemplate?> selectTemplate,
        SharedShareProof? proof, ISharedCalibrationStore store)
    {
        // Only the very document the session proved: one swapped in since, or still being checked, vouches for nothing.
        if (!selection.IsUsable || proof is null ||
            !string.Equals(proof.ProfileSha256, inForce.ProfileSha256, StringComparison.Ordinal))
        {
            return SharedShareCodeResult.Refused(SharedShareCodeRefusal.SharedNotProven);
        }

        // The code the profile in force was built from: as recorded when it was bound, else read off the profile through
        // the template - and, when the session names the code it bound, the very same one (review 2026-10-15): a mismatch
        // means the document and the code the session vouches for are not one thing, and neither is handed on.
        var sha = RecordedCode(store, inForce) ?? RecoverCodeSha256(inForce, selectTemplate);
        if (!SharedCalibrationIndex.IsSha256(sha) ||
            (proof.CodeSha256 is { } named && !string.Equals(named, sha, StringComparison.Ordinal)) ||
            StoredText(store, inForce, sha) is not { } text)
        {
            return SharedShareCodeResult.Refused(SharedShareCodeRefusal.Shared);
        }

        var (url, inUrl) = SharedCalibrationIssueLink.For(inForce.Region, inForce.GameBuild, text, SharedShareBasis.Verified);
        return new SharedShareCodeResult(new SharedShareCode(text, sha, url, inUrl, SharedShareBasis.Verified), null, null);
    }

    /// <summary>
    /// The code the store recorded the profile document was bound from (review 2026-10-15, L2): what the profile was
    /// written from even after a software update brought another template, under which reading it back names another code.
    /// </summary>
    private static string? RecordedCode(ISharedCalibrationStore store, ProtocolProfile profile)
    {
        try
        {
            return store.BoundCode(profile.Region, profile.GameBuild, profile.ProfileSha256);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// The code identity of the shared profile in force, read off the profile in memory through the template, as the
    /// session reads it when nothing was recorded. Null when there is no template or the profile is not exactly a
    /// calibrated shape of it.
    /// </summary>
    private static string? RecoverCodeSha256(ProtocolProfile profile, Func<Region, CalibrationTemplate?> selectTemplate)
    {
        try
        {
            return selectTemplate(profile.Region) is { } template && SharedProfileBuilder.RecoverPayload(profile, template) is { } payload
                ? ShareCode.Sha256(payload)
                : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// The stored text of the code, only while it decodes to that very identity and to the profile's region and build:
    /// the store is a directory on disk, so what it hands back is checked again before it goes to the repository.
    /// </summary>
    private static string? StoredText(ISharedCalibrationStore store, ProtocolProfile profile, string sha)
    {
        string? text;
        try
        {
            text = store.CodeText(profile.Region, profile.GameBuild, sha);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }

        return text is not null &&
               ShareCode.Decode(text) is { Payload: { } payload, CodeSha256: { } decoded } &&
               string.Equals(decoded, sha, StringComparison.Ordinal) &&
               payload.Region == profile.Region &&
               string.Equals(payload.GameBuild, profile.GameBuild, StringComparison.Ordinal)
            ? text
            : null;
    }
}
