using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.StateMachine;

namespace MentorRecorder.Collector.Protocol.Profiles;

/// <summary>
/// Where the identity of the running client comes from, for <see cref="ProfileSelector.Current"/>.
///
/// Live capture does not use it: it selects with the region and build of the game-process
/// detection (<c>GameProcessDetection</c>), whose build is read from the launcher's
/// <c>ffxivgame.ver</c> text file next to the client and whose region follows the install path
/// (docs/protocol-profile-format.md section 8). Nothing here opens a process. The default
/// implementation knows nothing, which is the safe answer.
/// </summary>
public interface IGameBuildSource
{
    /// <summary>Region the client belongs to, or <see cref="Region.Unknown"/>.</summary>
    Region Region { get; }

    /// <summary>Build discriminator of the client, or null when it could not be determined.</summary>
    string? GameBuild { get; }
}

/// <summary>A build source that never identifies anything, so selection always fails closed.</summary>
public sealed class UnknownGameBuildSource : IGameBuildSource
{
    /// <summary>The shared instance.</summary>
    public static UnknownGameBuildSource Instance { get; } = new();

    private UnknownGameBuildSource()
    {
    }

    /// <inheritdoc />
    public Region Region => Region.Unknown;

    /// <inheritdoc />
    public string? GameBuild => null;
}

/// <summary>
/// Why a selection came out as it did, as a token: <see cref="ProfileSelection.Reason"/> is English for
/// logs and diagnostics, and the pipeline words each token for the player in a sentence of its own
/// (audit 2026-11-03, D2-1).
/// </summary>
public enum ProfileSelectionReason
{
    /// <summary>A profile was selected and may record.</summary>
    Selected,

    /// <summary>The client build is not known.</summary>
    BuildUnknown,

    /// <summary>The client region is not known, and no profile serves an unknown region.</summary>
    RegionUnknown,

    /// <summary>Two or more profiles claim the region and build.</summary>
    Ambiguous,

    /// <summary>The region has profiles that record, none of them for this build.</summary>
    NoMatch,

    /// <summary>No profile records anything for the region yet: only placeholders, or nothing at all.</summary>
    RegionUnsupported,

    /// <summary>A profile matched, but its status or binding does not permit recording.</summary>
    NotUsable,

    /// <summary>Selection itself failed, or the caller did not say why.</summary>
    Failed,
}

/// <summary>What one selection attempt produced.</summary>
/// <param name="Status">Effective status of the selection.</param>
/// <param name="Binding">Binding for the state machine; fail-closed unless the profile is usable.</param>
/// <param name="Profile">Selected profile, or null.</param>
/// <param name="Region">Region that was asked for.</param>
/// <param name="GameBuild">Build that was asked for.</param>
/// <param name="Reason">Short, non-sensitive explanation of the outcome, in English, for logs and diagnostics.</param>
/// <param name="Origin">
/// Where the selected profile's file came from, or null when nothing was selected or the
/// origin is not known (an explicitly named file bypasses the catalogue entirely). Callers
/// use this to render "本机校准" without ever showing a path or an opcode.
/// </param>
/// <param name="ReasonCode">The same outcome as a token the player's sentence is chosen by.</param>
public sealed record ProfileSelection(
    ProfileCompatibilityStatus Status,
    ProfileBinding Binding,
    ProtocolProfile? Profile,
    Region Region,
    string? GameBuild,
    string Reason,
    ProfileOrigin? Origin = null,
    ProfileSelectionReason ReasonCode = ProfileSelectionReason.Failed)
{
    /// <summary>True when the parser and the state machine may act at all.</summary>
    public bool IsUsable => Profile is not null && Binding.IsUsable;
}

/// <summary>
/// Chooses the profile for a region and a client build.
///
/// Three rules decide everything: a build that matches no profile is UNSUPPORTED rather than
/// "close enough"; two profiles claiming the same build refuse each other; and a SYNTHETIC
/// profile is invisible unless the caller explicitly asked for synthetic profiles, which
/// only the replay tool and the tests ever do.
/// </summary>
public sealed class ProfileSelector : IProfileStatusProvider
{
    /// <summary>
    /// Reason of a refusal that means exactly "the build is unknown to this directory": the
    /// only refusal self-calibration may act on (an ambiguous directory or a load failure
    /// never qualifies).
    /// </summary>
    public const string NoProfileMatchesReason = "no profile matches this region and build";

    private readonly ProfileCatalog _catalog;
    private readonly IGameBuildSource _buildSource;
    private readonly bool _allowSynthetic;
    private ProfileSelection _current;

    /// <summary>Creates a selector over one catalogue.</summary>
    /// <param name="catalog">Profiles to choose from.</param>
    /// <param name="buildSource">Identity of the running client; unknown by default.</param>
    /// <param name="allowSynthetic">
    /// When true, SYNTHETIC profiles take part in selection. Live capture never sets this.
    /// </param>
    public ProfileSelector(
        ProfileCatalog catalog,
        IGameBuildSource? buildSource = null,
        bool allowSynthetic = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _buildSource = buildSource ?? UnknownGameBuildSource.Instance;
        _allowSynthetic = allowSynthetic;
        _current = Select(_buildSource.Region, _buildSource.GameBuild);
    }

    /// <summary>The selection made for the current build source.</summary>
    public ProfileSelection Current => _current;

    /// <summary>Re-runs selection against the build source; used when capture (re)starts.</summary>
    public ProfileSelection Refresh() => _current = Select(_buildSource.Region, _buildSource.GameBuild);

    /// <summary>Selects a profile for an explicit region and build.</summary>
    /// <param name="region">Region of the client.</param>
    /// <param name="gameBuild">Build discriminator, or null when unknown.</param>
    public ProfileSelection Select(Region region, string? gameBuild)
    {
        if (string.IsNullOrWhiteSpace(gameBuild))
        {
            return Refused(region, gameBuild, "the client build could not be determined", ProfileSelectionReason.BuildUnknown);
        }

        if (_catalog.IsAmbiguous(region, gameBuild))
        {
            return new ProfileSelection(
                ProfileCompatibilityStatus.Ambiguous,
                ProfileBinding.FailClosed,
                null,
                region,
                gameBuild,
                "two or more profiles claim this region and build",
                ReasonCode: ProfileSelectionReason.Ambiguous);
        }

        var candidates = _catalog.UsableFor(region)
            .Where(profile => string.Equals(profile.GameBuild, gameBuild, StringComparison.OrdinalIgnoreCase))
            .Where(profile => profile.Status != ProfileCompatibilityStatus.Candidate)
            .Where(profile => _allowSynthetic || profile.Status != ProfileCompatibilityStatus.Synthetic)
            .ToArray();

        if (candidates.Length == 0)
        {
            // Said apart for the player: a region the software does not know, a region nothing records
            // for yet (only placeholders), and a build of a region that does record (D2-1).
            var why = region == Region.Unknown
                ? ProfileSelectionReason.RegionUnknown
                : _catalog.UsableFor(region).Any(profile => profile.Status == ProfileCompatibilityStatus.Verified)
                    ? ProfileSelectionReason.NoMatch
                    : ProfileSelectionReason.RegionUnsupported;
            return Refused(region, gameBuild, NoProfileMatchesReason, why);
        }

        if (candidates.Length > 1)
        {
            return new ProfileSelection(
                ProfileCompatibilityStatus.Ambiguous,
                ProfileBinding.FailClosed,
                null,
                region,
                gameBuild,
                "two or more profiles claim this region and build",
                ReasonCode: ProfileSelectionReason.Ambiguous);
        }

        var selected = candidates[0];
        var binding = selected.ToBinding();
        var reason = binding.IsUsable
            ? "profile selected"
            : $"profile status {selected.Status} does not permit recording";
        var origin = _catalog.OriginOf(selected);
        return new ProfileSelection(selected.Status, binding, selected, region, gameBuild, reason, origin,
            binding.IsUsable ? ProfileSelectionReason.Selected : ProfileSelectionReason.NotUsable);
    }

    /// <summary>仅返回显式开启且身份精确匹配的唯一候选；不改变正式选择或状态快照。</summary>
    /// <param name="region">客户端区服，必须是 CN、GLOBAL 或 TC。</param>
    /// <param name="gameBuild">已确认客户端版本；空值不选择。</param>
    /// <param name="enabled">用户是否显式开启候选验证。</param>
    /// <returns>唯一、无歧义的候选档案；关闭、缺失或歧义时返回 null。</returns>
    public ProtocolProfile? SelectCandidate(Region region, string? gameBuild, bool enabled)
    {
        if (!enabled || region is not (Region.Cn or Region.Global or Region.Tc) ||
            string.IsNullOrWhiteSpace(gameBuild) || _catalog.IsAmbiguous(region, gameBuild, candidate: true))
        {
            return null;
        }

        var candidates = _catalog.UsableFor(region)
            .Where(profile => profile.Status == ProfileCompatibilityStatus.Candidate &&
                string.Equals(profile.GameBuild, gameBuild, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    /// <summary>Selects an explicitly named profile file, bypassing build matching.</summary>
    /// <param name="path">Profile file to load.</param>
    /// <param name="allowSynthetic">Whether a SYNTHETIC profile may be returned.</param>
    public static ProfileSelection SelectExplicit(string path, bool allowSynthetic)
    {
        var report = ProfileLoader.Validate(path);
        if (report.Profile is not { } profile)
        {
            var reason = report.Errors.Count > 0 ? report.Errors[0].Message : "profile refused";
            return new ProfileSelection(
                ProfileCompatibilityStatus.Unsupported, ProfileBinding.FailClosed, null,
                Region.Unknown, null, reason, ReasonCode: ProfileSelectionReason.Failed);
        }

        if (!allowSynthetic && profile.Status == ProfileCompatibilityStatus.Synthetic)
        {
            return new ProfileSelection(
                ProfileCompatibilityStatus.Unsupported, ProfileBinding.FailClosed, null,
                profile.Region, profile.GameBuild,
                "a SYNTHETIC profile may not be used without allow_synthetic",
                ReasonCode: ProfileSelectionReason.NotUsable);
        }

        var binding = profile.ToBinding();
        return new ProfileSelection(
            profile.Status,
            binding,
            profile,
            profile.Region,
            profile.GameBuild,
            binding.IsUsable ? "profile selected" : $"profile status {profile.Status} does not permit recording",
            ReasonCode: binding.IsUsable ? ProfileSelectionReason.Selected : ProfileSelectionReason.NotUsable);
    }

    /// <inheritdoc />
    public ProfileStatusSnapshot GetProfileStatus()
    {
        var selection = _current;
        return new ProfileStatusSnapshot(
            selection.Profile?.ProfileId,
            EnumWire<Region>.Format(selection.Region),
            selection.GameBuild,
            EnumWire<ProfileCompatibilityStatus>.Format(selection.Status),
            selection.Profile?.Messages.Count ?? 0,
            selection.Profile?.FixturesVerified ?? false,
            selection.IsUsable ? null : selection.Reason,
            selection.Origin);
    }

    private static ProfileSelection Refused(
        Region region, string? gameBuild, string reason, ProfileSelectionReason why) =>
        new(ProfileCompatibilityStatus.Unsupported, ProfileBinding.FailClosed, null, region, gameBuild, reason,
            ReasonCode: why);
}
