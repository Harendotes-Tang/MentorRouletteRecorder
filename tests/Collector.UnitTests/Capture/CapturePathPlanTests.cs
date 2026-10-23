using System.Net;
using System.Reflection;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CapturePathPlanTests
{
    [Fact]
    public void AutomaticCandidatesRetainVirtualAddressesAndCanonicalLoopbackWithoutBrandRules()
    {
        var selected = Adapter("physical", "192.0.2.1") with { PreferredRoute = true };
        var virtualPath = Adapter("opaque-interface", "10.0.0.1", "10.0.0.2") with { CarriesGameTraffic = true };
        var loopback = Adapter(CapturePathPlan.LoopbackDeviceId) with { IsLoopback = true };

        var plan = CapturePathPlan.Create(new[] { selected, virtualPath, loopback }, selected, true);

        Assert.Equal(new[] { "physical", "opaque-interface", CapturePathPlan.LoopbackDeviceId },
            plan.Bindings.Select(binding => binding.AdapterId));
        Assert.Equal(new[] { IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.2") },
            plan.Bindings[1].LocalAddresses);
        Assert.Equal(new[] { IPAddress.Loopback }, plan.Bindings[2].LocalAddresses);
        Assert.True(plan.Bindings[2].IsLoopback);
        Assert.False(plan.CandidateLimitReached);
        Assert.False(selected.CarriesGameTraffic); // Planning never invents measured game ownership.
    }

    [Fact]
    public void ManualSelectionNeverAddsTheOtherOrLoopbackInterface()
    {
        var selected = Adapter("manual", "192.0.2.1");
        var plan = CapturePathPlan.Create(new[] { selected,
            Adapter("other", "10.0.0.1") with { CarriesGameTraffic = true },
            Adapter(CapturePathPlan.LoopbackDeviceId) }, selected, false);

        Assert.False(plan.Automatic);
        Assert.Equal("manual", Assert.Single(plan.Bindings).AdapterId);
    }

    [Fact]
    public void CandidatesAreBoundedAndTheSelectedAddressSurvivesAddressTruncation()
    {
        var primaryAddress = IPAddress.Parse("192.0.2.99");
        var selected = Adapter("primary", Enumerable.Range(1, 30).Select(i => $"192.0.2.{i}").ToArray())
            with { BindAddress = primaryAddress };
        var adapters = Enumerable.Range(1, 30).Select(i => Adapter($"candidate-{i}", $"10.0.0.{i}"))
            .Append(selected).ToArray();

        var plan = CapturePathPlan.Create(adapters, selected, true);

        Assert.Equal(CapturePathPlan.MaxInterfaces, plan.Bindings.Count);
        Assert.Equal("primary", plan.Bindings[0].AdapterId);
        Assert.Equal(CapturePathPlan.MaxAddressesPerInterface, plan.Bindings[0].LocalAddresses.Count);
        Assert.Contains(primaryAddress, plan.Bindings[0].LocalAddresses);
        Assert.True(plan.CandidateLimitReached);
    }

    [Fact]
    public void GuidAndNpcapDeviceIdentityOpenOnlyOneDevice()
    {
        const string id = "{4B40B236-8888-4444-9999-ABCDEABCDEAB}";
        var selected = Adapter(id, "192.0.2.1");
        var alias = selected with { Id = @"\Device\NPF_" + id };
        var plan = CapturePathPlan.Create(new[] { alias, selected }, selected, true);
        Assert.Single(plan.Bindings);
    }

    [Fact]
    public void DownIpv6OnlyAndArbitrarilyNamedLoopbackAreNotEligible()
    {
        var selected = Adapter("selected", "192.0.2.1");
        var plan = CapturePathPlan.Create(new[] { selected,
            Adapter("down", "10.0.0.1") with { IsUp = false },
            Adapter("ipv6", "2001:db8::1"),
            Adapter("Npcap Loopback Adapter", "127.0.0.1") with { IsLoopback = true } }, selected, true);
        Assert.Single(plan.Bindings);
    }

    [Fact]
    public void EnumeratorPreservesAllLocalAddressesPrivatelyAndRecognizesLoopbackCarriers()
    {
        var enumerator = new AdapterEnumerator(new Adapters(new[] {
            new AdapterInfo(CapturePathPlan.LoopbackDeviceId, "Any name", "Any description", true, true,
                new[] { IPAddress.Loopback }),
            new AdapterInfo("multi", "Any other name", "Any description", true, false,
                new[] { IPAddress.Parse("192.0.2.1"), IPAddress.Parse("192.0.2.2") }) }),
            new Tcp(new[] { IPAddress.Parse("127.0.0.2") }));

        var views = enumerator.List(42);
        Assert.True(views[0].CarriesGameTraffic);
        Assert.True(views[0].Recommended);
        Assert.Equal(2, views[1].LocalAddresses.Count);
        Assert.Null(typeof(CaptureAdapterView).GetProperty(nameof(CaptureAdapterView.LocalAddresses),
            BindingFlags.Instance | BindingFlags.Public));
        Assert.NotNull(typeof(CaptureAdapterView).GetProperty(nameof(CaptureAdapterView.LocalAddresses),
            BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void LoopbackMatchingUsesOnlyCanonicalDeviceIdentityWhileOrdinaryMatchingKeepsEveryAddress()
    {
        var loopback = new CaptureInterfaceBinding(CapturePathPlan.LoopbackDeviceId, Array.Empty<IPAddress>(), true);
        Assert.True(NpcapPacketReader.Matches(CapturePathPlan.LoopbackDeviceId, Array.Empty<IPAddress>(), loopback));
        Assert.False(NpcapPacketReader.Matches("Npcap Loopback Adapter", new[] { IPAddress.Loopback }, loopback));
        Assert.Equal("ip and tcp", NpcapPacketReader.BuildFilter(loopback));

        var ordinary = new CaptureInterfaceBinding("exact", new[] {
            IPAddress.Parse("192.0.2.1"), IPAddress.Parse("192.0.2.2") }, false);
        Assert.True(NpcapPacketReader.Matches("exact", ordinary.LocalAddresses, ordinary));
        Assert.False(NpcapPacketReader.Matches("exact", ordinary.LocalAddresses.Take(1), ordinary));
        Assert.False(NpcapPacketReader.Matches("other", ordinary.LocalAddresses, ordinary));
        Assert.Equal("ip and tcp and (host 192.0.2.1 or host 192.0.2.2)", NpcapPacketReader.BuildFilter(ordinary));
        Assert.Throws<InvalidOperationException>(() => NpcapPacketReader.BuildFilter(loopback with { AdapterId = "fake" }));
    }

    private static CaptureAdapterView Adapter(string id, params string[] addresses)
    {
        var raw = addresses.Select(IPAddress.Parse).ToArray();
        return new CaptureAdapterView(id, "same display name", "same driver description", true, false,
            raw.Select(AdapterEnumerator.Mask).ToArray(), false, false)
        { BindAddress = raw.FirstOrDefault(), LocalAddresses = raw };
    }

    private sealed class Adapters(IReadOnlyList<AdapterInfo> adapters) : IAdapterProvider
    {
        public IReadOnlyList<AdapterInfo> List() => adapters;
    }

    private sealed class Tcp(IReadOnlyList<IPAddress> addresses) : IProcessTcpTable
    {
        public IReadOnlyList<IPAddress> LocalAddresses(int processId) => addresses;
    }
}
