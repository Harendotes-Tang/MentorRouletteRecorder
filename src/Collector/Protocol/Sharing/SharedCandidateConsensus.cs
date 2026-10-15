using System.Globalization;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>One code as the consensus weighs it.</summary>
/// <param name="Sha">Identity of the code (<c>code_sha256</c>).</param>
/// <param name="Payload">What it declares.</param>
/// <param name="Submitters">Distinct accounts the last index this machine read gives it; 0 for a code no index listed.</param>
/// <param name="LocalPrior">
/// The shape prior this machine computed itself (<see cref="SharedLocalShapePrior"/>): true when the match has the shape of
/// the calibration this machine verified for the previous build, false when that calibration names the match the same way
/// with another shape, null when there is nothing to compare with. Never the index's <c>shape_prior</c>, which one account
/// can plant by publishing a code for an invented earlier build (security review 2026-10-15).
/// </param>
public sealed record SharedConsensusCandidate(string Sha, ShareCodePayload Payload, int Submitters, bool? LocalPrior = null);

/// <summary>What the other codes being verified say about one code.</summary>
/// <param name="Disputed">
/// Messages on which another code declares a different value, in <see cref="SharedCandidateConsensus.Fields"/> order; empty
/// when none.
/// </param>
/// <param name="Preferred">
/// True when, on every disputed message, the code carries the value most submitters stand behind - strictly more than any
/// other value, or, on the match only, tied at the top with this machine's own shape prior on its side alone. True when
/// nothing is disputed.
/// </param>
public sealed record SharedConsensus(IReadOnlyList<string> Disputed, bool Preferred)
{
    /// <summary>A code nothing contradicts: alone, or agreeing with every other code on everything both declare.</summary>
    public static SharedConsensus Undisputed { get; } = new(Array.Empty<string>(), true);
}

/// <summary>
/// Field-level agreement among the codes being verified for one region, build and template (plan §20.5).
///
/// Two codes of one build were seen in October 2026 to agree on the zone marker, the territory and the job and to differ
/// only on the match; the repository could only mark both as conflicting, and that mark sent every player of the build back
/// to verifying both in full - no live record of the first duty, the very thing §18 removed. Comparing field by field keeps
/// what the codes agree on out of the dispute, and lets the submitters of each value decide the rest.
///
/// The zone marker, the territory and the job are compared across match sources: whatever names the match, the client sends
/// each of them on one opcode. The match itself - its source with the learned half of the pop - is compared only among codes
/// that name it the same way, because codes of different sources declare different messages and cannot disagree about one.
/// A message a code does not declare neither votes nor disputes: a code without the job is no evidence against a job.
///
/// A value's votes are the submitters of every code carrying it, so two codes that agree on the pop and differ elsewhere
/// both stand behind that pop. A tie is left undecided, with one exception: on the match, a code whose shape the
/// calibration this machine verified for the previous build had (<see cref="SharedConsensusCandidate.LocalPrior"/>) is
/// preferred over tied rivals that lack it. Only this machine's own evidence breaks the tie: a value from the repository
/// would let whoever writes to it choose the code that skips verifying its match. The prior is a prior: it never
/// outweighs a vote, and it says nothing about the other messages. A code no index listed has no submitters, so it
/// loses every dispute with a published one and cannot by itself unsettle a published code's preference.
///
/// Pure and static: what is decided here only gates and orders candidates (<see cref="SharedCalibrationSession"/>); nothing
/// is rejected for being outvoted - local traffic still decides that.
/// </summary>
public static class SharedCandidateConsensus
{
    private static readonly Field[] FieldsInOrder =
    {
        new(CalibratedShape.ZoneName, payload => Number(payload.ZoneOpcode), BySource: false),
        new(CalibratedShape.TerritoryName, payload => Number(payload.TerritoryOpcode), BySource: false),
        new(CalibratedShape.JobName, payload => Number(payload.JobOpcode), BySource: false),
        new(CalibratedShape.PopName, Match, BySource: true),
    };

    /// <summary>The messages compared, in the order <see cref="SharedConsensus.Disputed"/> lists them.</summary>
    public static IReadOnlyList<string> Fields { get; } = FieldsInOrder.Select(field => field.Name).ToArray();

    /// <summary>Judges every code against the others. A code listed twice counts once, as first listed.</summary>
    /// <param name="candidates">The codes still being verified, rejected ones left out; at most a handful.</param>
    public static IReadOnlyDictionary<string, SharedConsensus> Judge(IEnumerable<SharedConsensusCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var codes = candidates.OfType<SharedConsensusCandidate>().DistinctBy(code => code.Sha, StringComparer.Ordinal).ToArray();
        var verdicts = new Dictionary<string, SharedConsensus>(StringComparer.Ordinal);
        foreach (var code in codes)
        {
            var disputed = new List<string>();
            var preferred = true;
            foreach (var field in FieldsInOrder)
            {
                if (PreferredOn(field, code, codes) is { } wins)
                {
                    disputed.Add(field.Name);
                    preferred &= wins;
                }
            }

            verdicts[code.Sha] = disputed.Count == 0 ? SharedConsensus.Undisputed : new SharedConsensus(disputed, preferred);
        }

        return verdicts;
    }

    /// <summary>
    /// Null when <paramref name="code"/> is not disputed on <paramref name="field"/>; otherwise whether its value wins there.
    /// </summary>
    private static bool? PreferredOn(Field field, SharedConsensusCandidate code, IReadOnlyList<SharedConsensusCandidate> codes)
    {
        if (field.Value(code.Payload) is not { } own)
        {
            return null;
        }

        // Every value declared among the codes this one is compared with, with the votes behind it and whether any code
        // carrying it has the local prior. Votes are added up as long: the counts come from the network, and eight codes at
        // the largest count the index allows would overflow an int - checked arithmetic, so a throw on the packet thread.
        var tally = codes
            .Where(other => !field.BySource || other.Payload.MatchSource == code.Payload.MatchSource)
            .Select(other => (Value: field.Value(other.Payload), Code: other))
            .Where(item => item.Value is not null)
            .GroupBy(item => item.Value!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (
                    Votes: group.Sum(item => (long)Math.Max(0, item.Code.Submitters)),
                    Prior: group.Any(item => item.Code.LocalPrior == true)),
                StringComparer.Ordinal);
        if (tally.Count < 2)
        {
            return null;
        }

        var votes = tally[own].Votes;
        var rivals = tally.Where(pair => !string.Equals(pair.Key, own, StringComparison.Ordinal)).Select(pair => pair.Value).ToArray();
        if (rivals.Any(rival => rival.Votes > votes))
        {
            return false;
        }

        var tied = rivals.Where(rival => rival.Votes == votes).ToArray();
        return tied.Length == 0 || (field.BySource && code.LocalPrior == true && tied.All(rival => !rival.Prior));
    }

    private static string? Number(ushort? opcode) => opcode?.ToString(CultureInfo.InvariantCulture);

    /// <summary>The match as one comparable value: how it is named, and every learned part of the pop.</summary>
    private static string Match(ShareCodePayload payload)
    {
        var pop = payload.Pop;
        var selectors = pop.SelectorValues is { } values
            ? string.Join(",", values.Select(value => value.ToString(CultureInfo.InvariantCulture)))
            : "-";
        return string.Join(
            "|",
            EnumWire<CalibrationMatchSource>.Format(payload.MatchSource),
            pop.Opcode.ToString(CultureInfo.InvariantCulture),
            pop.Length?.ToString(CultureInfo.InvariantCulture) ?? "-",
            pop.RouletteOffset?.ToString(CultureInfo.InvariantCulture) ?? "-",
            selectors);
    }

    /// <summary>One message compared.</summary>
    /// <param name="Name">Its semantic name.</param>
    /// <param name="Value">The value a payload declares for it, or null when it declares none.</param>
    /// <param name="BySource">True for the match: compared only among codes of the same match source; the prior breaks its ties.</param>
    private sealed record Field(string Name, Func<ShareCodePayload, string?> Value, bool BySource);
}
