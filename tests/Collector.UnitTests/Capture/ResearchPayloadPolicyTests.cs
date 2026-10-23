using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

public sealed class ResearchPayloadPolicyTests
{
    [Fact]
    public void RequestedWhitelistRequiresKnownBoundedNonObfuscatedHypotheses()
    {
        var profile = CandidateObserverTests.Profile();
        Assert.Equal(new[] { "0xf101" }, ResearchPayloadPolicy.ValidateRequested(new[] { "0xF101" }, new[] { profile }));
        Assert.Throws<CollectorException>(() => ResearchPayloadPolicy.ValidateRequested(new[] { "0xfffe" }, new[] { profile }));
        var longProfile = profile with { Hypotheses = new[] { profile.Hypotheses[0] with { ExpectedLength = 513 } } };
        Assert.Throws<CollectorException>(() => ResearchPayloadPolicy.ValidateRequested(new[] { "0xf101" }, new[] { longProfile }));
        var unbounded = profile with { Hypotheses = new[] { profile.Hypotheses[0] with { ExpectedLength = null, MinLength = 1, MaxLength = null } } };
        Assert.Throws<CollectorException>(() => ResearchPayloadPolicy.ValidateRequested(new[] { "0xf101" }, new[] { unbounded }));
    }

    [Theory]
    [InlineData("PlayerSpawn")]
    [InlineData("NpcSpawn")]
    [InlineData("NpcSpawn2")]
    [InlineData("ActionEffect01")]
    [InlineData("ActionEffect02")]
    [InlineData("ActionEffect04")]
    [InlineData("ActionEffect08")]
    [InlineData("ActionEffect16")]
    [InlineData("ActionEffect24")]
    [InlineData("ActionEffect32")]
    [InlineData("StatusEffectList")]
    [InlineData("StatusEffectList3")]
    [InlineData("Examine")]
    [InlineData("UpdateGearset")]
    [InlineData("UpdateParty")]
    [InlineData("ActorControl")]
    [InlineData("ActorCast")]
    [InlineData("UnknownEffect01")]
    [InlineData("UnknownEffect16")]
    [InlineData("ACTOR_CONTROL")]
    public void EveryLocallyDocumentedObfuscatedNameIsRefused(string name)
    {
        var profile = CandidateObserverTests.Profile();
        profile = profile with { Hypotheses = new[] { profile.Hypotheses[0] with { Name = name } } };
        Assert.Throws<CollectorException>(() => ResearchPayloadPolicy.ValidateRequested(new[] { "0xf101" }, new[] { profile }));
    }

    [Theory]
    [InlineData("f101")]
    [InlineData("0xf10")]
    [InlineData("0x10000")]
    [InlineData("0xzzzz")]
    [InlineData(" 0xf101")]
    public void OpcodeSyntaxIsNotCoerced(string token) =>
        Assert.Throws<CollectorException>(() => ResearchPayloadPolicy.Normalize(new[] { token }));

    [Fact]
    public void DuplicateAndOversizeListsAreRefused()
    {
        Assert.Throws<CollectorException>(() => ResearchPayloadPolicy.Normalize(new[] { "0xf101", "0xF101" }));
        Assert.Throws<CollectorException>(() => ResearchPayloadPolicy.Normalize(Enumerable.Range(0, 33).Select(i => $"0x{i:x4}").ToArray()));
    }

    [Fact]
    public void ObserverDefaultsToNoPayloadAndClearingWhitelistIsImmediate()
    {
        var rows = new List<CandidateObservation>();
        // A plain (non zone_load) hypothesis: zone members are only written as part of a burst.
        var plain = CandidateObserverTests.Profile();
        plain = plain with { Hypotheses = new[] { plain.Hypotheses[0] with { Group = "finder" } } };
        var observer = new CandidateObserver(plain, "session", rows.Add);
        observer.Accept(CandidateObserverTests.Message());
        Assert.Null(Assert.Single(rows).PayloadHex);
        observer.SetResearchOpcodes(new HashSet<ushort> { 0xf101 });
        observer.Accept(CandidateObserverTests.Message(t: 1));
        Assert.Equal(new string('0', 16), rows[1].PayloadHex);
        observer.SetResearchOpcodes(new HashSet<ushort>());
        observer.Accept(CandidateObserverTests.Message(t: 2));
        Assert.Null(rows[2].PayloadHex);
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, true)]
    [InlineData(456, true)]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public void ObserverEnforcesTheActualPayloadCeiling(int length, bool rawAllowed)
    {
        var profile = CandidateObserverTests.Profile();
        profile = profile with { Hypotheses = new[] { profile.Hypotheses[0] with { ExpectedLength = length, Group = "finder" } } };
        var rows = new List<CandidateObservation>();
        var observer = new CandidateObserver(profile, "session", rows.Add, new HashSet<ushort> { 0xf101 });
        var payload = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
        observer.Accept(CandidateObserverTests.Message() with { Payload = payload });
        var observation = Assert.Single(rows);
        Assert.Equal(length, observation.Length);
        Assert.Equal(rawAllowed ? Convert.ToHexString(payload).ToLowerInvariant() : null, observation.PayloadHex);
    }

    [Theory]
    [InlineData(512, false, true)]
    [InlineData(513, false, false)]
    [InlineData(512, true, true)]
    [InlineData(513, true, false)]
    public void WhitelistChecksBothExactAndRangeUpperBounds(int upperBound, bool range, bool allowed)
    {
        var profile = CandidateObserverTests.Profile();
        var hypothesis = profile.Hypotheses[0] with
        {
            ExpectedLength = range ? null : upperBound,
            MinLength = range ? 1 : null,
            MaxLength = range ? upperBound : null,
        };
        profile = profile with { Hypotheses = new[] { hypothesis } };
        Assert.Equal(allowed, ResearchPayloadPolicy.IsEligible(profile, hypothesis));
        if (allowed)
            Assert.Equal(new[] { "0xf101" }, ResearchPayloadPolicy.ValidateRequested(new[] { "0xf101" }, new[] { profile }));
        else
            Assert.Throws<CollectorException>(() => ResearchPayloadPolicy.ValidateRequested(new[] { "0xf101" }, new[] { profile }));
    }
}
