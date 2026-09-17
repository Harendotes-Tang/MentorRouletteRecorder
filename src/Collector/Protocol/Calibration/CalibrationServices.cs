using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Reference;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// The three things calibration needs from outside the pipeline, so tests can point every one
/// of them at a temp directory: where templates come from, how the formal selector is rebuilt
/// after a local profile was written, and how the profile is written.
/// </summary>
/// <param name="SelectTemplate">Template for a region; shipped profiles only in production.</param>
/// <param name="ReloadSelect">Builds a fresh formal selector over the shipped and local roots.</param>
/// <param name="Write">Writes a ready draft as a local profile and returns where it landed.</param>
public sealed record CalibrationServices(
    Func<Region, CalibrationTemplate?> SelectTemplate,
    Func<Func<GameProcessDetection, ProfileSelection>> ReloadSelect,
    Func<CalibrationDraft, CalibrationTemplate, string, DateTimeOffset, LocalProfileWriteResult> Write)
{
    /// <summary>
    /// Reads the evidence an earlier run of the Collector left for this region, build and
    /// template, or null when there is none.
    ///
    /// Inert by default, like the two below. Persistence touches a directory outside the
    /// caller's control, so it is something a caller opts into - <see cref="Default"/> does,
    /// and a test that builds its own services does not unless it points them somewhere of
    /// its own.
    /// </summary>
    public Func<Region, string, string, CalibrationSnapshot?> LoadEvidence { get; init; } =
        (_, _, _) => null;

    /// <summary>Writes the evidence so the next run can carry on from it. Never throws.</summary>
    public Func<Region, string, string, CalibrationSnapshot, bool> SaveEvidence { get; init; } =
        (_, _, _, _) => false;

    /// <summary>
    /// Removes the evidence for a build. Called when the player discards it and when a profile
    /// has been written, so neither decision is undone by the next restart. Never throws.
    /// </summary>
    public Action<Region, string> DeleteEvidence { get; init; } = (_, _) => { };

    /// <summary>Why the evidence for a build was or was not adopted: OK, NO_FILE, OTHER_TEMPLATE...</summary>
    public Func<Region, string, string, string> ExplainEvidence { get; init; } = (_, _, _) => "NO_STORE";

    /// <summary>
    /// Remembers a roulette name the player corrected. Inert by default like the evidence
    /// seams, so a test that builds its own services never writes to the real data directory.
    /// </summary>
    public Action<Region, int, string, DateTimeOffset> RecordRouletteName { get; init; } =
        (_, _, _, _) => { };

    /// <summary>The roulette names to render a timeline with, corrections included.</summary>
    public Func<RouletteCatalog> LoadRoulettes { get; init; } = () => RouletteCatalog.Default;

    /// <summary>
    /// Downloads the shared calibrations published for a region and client build.
    ///
    /// Inert by default, like the evidence seams: it answers DISABLED at once and sends nothing, so
    /// services a test builds for itself can never reach the network. <see cref="WithSharedCalibrationIn"/>
    /// wires the real client, which additionally honours <c>MR_DISABLE_SHARED_FETCH</c> on every call.
    /// </summary>
    public Func<Region, string, CancellationToken, Task<SharedCalibrationFetchResult>> FetchSharedCalibration { get; init; } =
        (_, _, _) => Task.FromResult(SharedCalibrationFetchResult.Disabled);

    /// <summary>
    /// Where downloaded codes, fetch bookkeeping and rejection records are kept. Inert by default: it
    /// remembers nothing and never touches the disk.
    /// </summary>
    public ISharedCalibrationStore SharedCalibrations { get; init; } = SharedCalibrationStore.Inert;

    /// <summary>
    /// True once a caller wired <see cref="FetchSharedCalibration"/> and <see cref="SharedCalibrations"/>.
    /// Until then the pipeline never schedules a download or reads a stored code at all, so services a
    /// test builds for itself add no background work. Importing a pasted code does not depend on it.
    /// </summary>
    public bool SharedFetchWired { get; init; }

    /// <summary>
    /// Writes a shared profile that passed local verification and returns its path. Inert by default: it
    /// refuses, so a candidate can pass but never bind until a caller points it somewhere
    /// (<see cref="WithSharedProfilesIn"/>).
    /// </summary>
    public Func<SharedProfileBuildResult, string> WriteSharedProfile { get; init; } =
        _ => throw new InvalidOperationException("no shared profile directory is configured");

    /// <summary>Removes the shared profile of a region and build when it was withdrawn. Inert by default. Never throws.</summary>
    public Action<Region, string> DeleteSharedProfile { get; init; } = (_, _) => { };

    /// <summary>The shared-calibration seams pointed at a fetch and a store of the caller's choosing.</summary>
    /// <param name="fetch">Fetches codes for a region and build.</param>
    /// <param name="store">Keeps what was fetched.</param>
    public CalibrationServices WithSharedCalibration(
        Func<Region, string, CancellationToken, Task<SharedCalibrationFetchResult>> fetch, ISharedCalibrationStore store)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(store);
        return this with { FetchSharedCalibration = fetch, SharedCalibrations = store, SharedFetchWired = true };
    }

    /// <summary>
    /// The real shared-calibration wiring: the download client and a store under <paramref name="root"/>
    /// (production: <see cref="SharedCalibrationStore.RootPath"/>). Constructing it sends and writes
    /// nothing. Not part of <see cref="Default"/>: only the shipping pipeline
    /// (<c>LiveProtocolPipeline.CreateDefault</c>) opts in, so a pipeline a test builds never downloads.
    /// </summary>
    /// <param name="root">Directory for the store.</param>
    public CalibrationServices WithSharedCalibrationIn(string root) =>
        WithSharedCalibration(SharedCalibrationClient.CreateDefault().FetchAsync, new SharedCalibrationStore(root));

    /// <summary>The shared-profile seams pointed at a directory; production uses <see cref="ProfileCatalog.SharedRootPath"/>.</summary>
    /// <param name="root">Directory holding shared profiles by region.</param>
    public CalibrationServices WithSharedProfilesIn(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return this with
        {
            WriteSharedProfile = built => SharedProfileFiles.Write(built, root),
            DeleteSharedProfile = (region, build) => SharedProfileFiles.Delete(root, region, build),
        };
    }

    /// <summary>The three evidence seams pointed at a directory. Production points them at the data root.</summary>
    /// <param name="root">Directory holding the evidence files.</param>
    public CalibrationServices WithEvidenceIn(string root) => this with
    {
        LoadEvidence = (region, build, templateSha) =>
            CalibrationEvidenceStore.Load(root, region, build, templateSha),
        SaveEvidence = (region, build, templateSha, snapshot) =>
            CalibrationEvidenceStore.Save(root, region, build, templateSha, snapshot),
        DeleteEvidence = (region, build) => CalibrationEvidenceStore.Delete(root, region, build),
        ExplainEvidence = (region, build, templateSha) =>
            CalibrationEvidenceStore.Explain(root, region, build, templateSha),
    };

    /// <summary>The roulette-name seams pointed at one file.</summary>
    /// <param name="path">Override file holding the player's corrections.</param>
    public CalibrationServices WithRouletteNamesIn(string path) => this with
    {
        RecordRouletteName = (region, id, name, now) =>
            RouletteNameOverrides.Record(path, region, id, name, now),
        LoadRoulettes = () => RouletteCatalog.WithLocalNames(path),
    };

    /// <summary>
    /// Production wiring: shipped templates, the catalogue merged over the shipped, local and shared roots,
    /// managed data directory. The shared-calibration download and profile write stay inert here; the
    /// shipping pipeline opts into them (<c>LiveProtocolPipeline.CreateDefault</c>).
    /// </summary>
    public static CalibrationServices Default => new CalibrationServices(
        region => CalibrationTemplate.Select(ProfileCatalog.LoadDefault(), region),
        () =>
        {
            var selector = new ProfileSelector(ProfileCatalog.LoadMerged(
                ProfileCatalog.FindDefaultRoot(), ProfileCatalog.FindLocalRoot(), ProfileCatalog.FindSharedRoot()));
            return game => selector.Select(game.Region, game.GameBuild);
        },
        (draft, template, build, now) => LocalProfileWriter.Write(draft, template, build, now, ProfileCatalog.LocalRootPath))
        .WithEvidenceIn(CalibrationEvidenceStore.RootPath)
        .WithRouletteNamesIn(RouletteNameOverrides.DefaultPath);
}

/// <summary>What a confirmed calibration produced.</summary>
/// <param name="ProfileId">Profile id of the local profile.</param>
/// <param name="ProfilePath">Where it was written.</param>
/// <param name="BoundInSession">True when the running capture session started recording with it.</param>
public sealed record CalibrationConfirmation(string ProfileId, string ProfilePath, bool BoundInSession);
