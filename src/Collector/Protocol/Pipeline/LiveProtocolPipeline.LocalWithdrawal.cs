using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Pipeline;

// The pipeline's side of local withdrawal: a profile this machine calibrated for itself, whose
// declared match message the live traffic then disproves, takes itself out of use.
//
// A shared profile is watched for as long as it records and can be withdrawn (SharedCandidateVerifier,
// SharedCalibrationSession.Supersede). A local one had nothing: once confirmed it was simply believed,
// for as long as the build lasted. The real machine's 1.2.0 bug is what that cost - a VERIFIED local
// profile whose CONTENT_FINDER_POP was the retainer bell's list, announcing a match every time the
// player opened the bell and cancelling it at the next opening, so the evening's real matches were
// never recorded and nothing ever re-examined the profile. This closes that: the one shape of local
// profile that reads every message of its opcode as a match - a learned announcement, with no selector
// to tell one message of that opcode from another - is watched while it records, and withdrawn the
// way a contradicted shared profile is.
public sealed partial class LiveProtocolPipeline
{
    /// <summary>The system revision's reason on every record a disproved local profile made.</summary>
    private const string ContradictedLocalReason =
        "这条记录由本机校准档案生成，该档案随后被本机流量证伪" +
        "（同一秒内出现多个不同的轮盘编号，说明所认报文并非匹配通知）而撤下，记录标记待复核。";

    /// <summary>How the player is told, in the statistics notice, before the count.</summary>
    private const string ContradictedLocalNotice = "本机校准档案已撤下，";

    /// <summary>Watches the bound profile's pop, or null when the profile in force is not one of the watched kind.</summary>
    private PopContradictionWatch? _popWatch;

    /// <summary>The opcode <see cref="_popWatch"/> is watching, so a contradiction can name it.</summary>
    private ushort _watchedPopOpcode;

    /// <summary>
    /// Local profiles this process withdrew. The file is put away by renaming it, and a file held
    /// open cannot be renamed while the directory still lists it; without this the reloaded
    /// catalogue would hand back the very profile the traffic has just disproved.
    /// </summary>
    private readonly HashSet<string> _withdrawnLocalProfiles = new(StringComparer.Ordinal);

    /// <summary>
    /// The sink the parser hands its events to: the state machine itself, or the state machine
    /// behind a watch on the declared match message.
    ///
    /// Watched only when all three hold. The profile is one this machine calibrated
    /// (<see cref="ProfileOrigin.Local"/>): a shipped profile is not this machine's guess, and a
    /// shared one already has its own verifier and withdrawal. Its pop travels server to client,
    /// so it claims to be an announcement rather than the player's own queue request standing in
    /// for one. And it declares no selector field, which means it reads *every* message of that
    /// opcode as a match - the only shape that a list of rows sharing the opcode can impersonate.
    /// </summary>
    /// <param name="profile">Profile about to be bound.</param>
    /// <param name="processor">The state machine's sink.</param>
    private ISemanticEventSink WatchPops(ProtocolProfile profile, SemanticEventProcessor processor)
    {
        ForgetPopWatch();
        if (_selection.Origin != ProfileOrigin.Local ||
            profile.Message("CONTENT_FINDER_POP") is not { Direction: PacketDirection.ServerToClient } pop ||
            pop.Fields.Any(field => field.Role == ProfileFieldRole.Selector))
        {
            return processor;
        }

        _popWatch = new PopContradictionWatch();
        _watchedPopOpcode = pop.Opcode;
        return new PopWatchSink(processor, _popWatch);
    }

    /// <summary>
    /// Drops the watch along with the parser it fed. Called wherever the bound profile goes away
    /// - a new capture session, either withdrawal - so a watch can never outlive the sink that
    /// held it and be read against a profile it was not armed over.
    /// </summary>
    private void ForgetPopWatch()
    {
        _popWatch = null;
        _watchedPopOpcode = 0;
    }

    /// <summary>
    /// The traffic disproved the local profile in force: stop recording with it, mark what it
    /// recorded, put its file away and start calibrating this build again.
    ///
    /// Called from <see cref="Accept"/> with the gate held, on the capture thread, after the
    /// parser has finished with the message - never from inside the parser, so the state machine
    /// is not torn down underneath the event that is being applied to it. Nothing here throws:
    /// every step that can fail is one the withdrawal goes ahead without.
    ///
    /// The steps run in this order for a reason. The file is retired and the catalogue re-read
    /// before the rejection is recorded, because re-arming calibration for a build forgets what
    /// was rejected for the previous one; rejecting first would throw the rejection away.
    /// </summary>
    private void WithdrawContradictedLocalProfile()
    {
        if (_popWatch is not { Contradicted: true } || _boundProfileId is not { } profileId)
        {
            return;
        }

        var opcode = _watchedPopOpcode;
        var region = _selection.Region;
        var gameBuild = _selection.GameBuild ?? _game.GameBuild;
        // Dropped before anything else: a re-bind below arms a fresh watch, and this one has
        // said all it has to say.
        ForgetPopWatch();
        _withdrawnLocalProfiles.Add(profileId);

        UnbindProfile(profileId);

        // Every record under the id, not only this binding's: unlike a shared profile - whose id
        // is shared by every code published for the build - a local profile id names exactly one
        // file, written by this machine, so everything recorded under it came from the shape the
        // traffic has just disproved.
        FlagRecords(profileId, null, ContradictedLocalReason, ContradictedLocalNotice);

        if (!string.IsNullOrWhiteSpace(gameBuild))
        {
            try
            {
                // Renamed, never deleted: it is the record of what the software believed while it
                // was recording, and the player may well ask.
                _calibrationServices.RetireLocalProfile(region, gameBuild);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // A file that cannot be moved is still listed; ReloadedSelect refuses it by id.
                // The withdrawal itself must still finish.
            }
        }

        if (ReloadedSelect() is { } select)
        {
            ReselectAfterProfileChange(select);
        }

        _calibration.RejectPopOpcode(opcode);
        NotifyCalibrationChanged();
    }

    /// <summary>
    /// The selector over the catalogue as it now stands on disk, or null when it cannot be read.
    /// A profile this process withdrew is answered as "no profile matches", the one reason
    /// calibration is allowed to act on. A new confirmation installs a selector of its own, so a
    /// profile calibrated afresh for the same build is not caught by this.
    /// </summary>
    private Func<GameProcessDetection, ProfileSelection>? ReloadedSelect()
    {
        try
        {
            var select = _calibrationServices.ReloadSelect();
            return game =>
            {
                var selection = select(game);
                return selection.Profile is { } profile && _withdrawnLocalProfiles.Contains(profile.ProfileId)
                    ? new ProfileSelection(
                        ProfileCompatibilityStatus.Unsupported, ProfileBinding.FailClosed, null,
                        selection.Region, selection.GameBuild, ProfileSelector.NoProfileMatchesReason)
                    : selection;
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sits between the parser and the state machine while a learned local announcement records,
    /// and feeds every match the profile claims to the watch. Once the watch has fired the pop is
    /// dropped rather than forwarded: the message that proves the profile wrong must not also be
    /// allowed to start or close a run with it. The pops before it unavoidably reached the machine
    /// already - that is what the records marked for review are for.
    /// </summary>
    private sealed class PopWatchSink : ISemanticEventSink
    {
        private readonly ISemanticEventSink _inner;
        private readonly PopContradictionWatch _watch;

        /// <summary>Wraps the state machine's sink.</summary>
        /// <param name="inner">Sink the events go to when the profile still stands.</param>
        /// <param name="watch">Watch over the declared match message.</param>
        public PopWatchSink(ISemanticEventSink inner, PopContradictionWatch watch)
        {
            _inner = inner;
            _watch = watch;
        }

        /// <inheritdoc />
        public void Accept(SemanticEvent semanticEvent)
        {
            if (semanticEvent is ContentFinderPop pop && _watch.Observe(pop.Mono, pop.RouletteId))
            {
                return;
            }

            _inner.Accept(semanticEvent);
        }
    }
}
