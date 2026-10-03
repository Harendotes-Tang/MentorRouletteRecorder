using System.Net;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.UnitTests;

public sealed class NpcapPacketTests
{
    [Fact]
    public void AdapterMatchesBothIdentityAndAddressWithoutFallback()
    {
        const string id = "{4B40B236-8888-4444-9999-ABCDEABCDEAB}";
        const string name = @"\Device\NPF_{4B40B236-8888-4444-9999-ABCDEABCDEAB}";
        var address = IPAddress.Parse("192.0.2.10");
        Assert.True(NpcapPacketReader.Matches(name, new[] { address }, id, address));
        Assert.True(NpcapPacketReader.Matches(name, new[] { address }, name, address));
        Assert.False(NpcapPacketReader.Matches(name, new[] { IPAddress.Loopback }, id, address));
        Assert.False(NpcapPacketReader.Matches(name, new[] { address }, "{AAAAAAAA-8888-4444-9999-ABCDEABCDEAB}", address));
        Assert.False(NpcapPacketReader.Matches("other", new[] { address }, id, address));
    }

    [Theory]
    [InlineData(0)] // Bad IPv4 IHL
    [InlineData(1)] // Truncated IPv4 body
    [InlineData(2)] // First fragment, more fragments follow
    [InlineData(3)] // Unassociable later fragment
    [InlineData(4)] // Invalid TCP data offset
    [InlineData(5)] // Reserved IP fragment flag
    public void MalformedOrFragmentedSelectedTrafficIsCountedAndDroppedWithoutDecoding(int corruption)
    {
        // Never decoded, and never the end of the capture either: the pcap filter admits every
        // program's TCP on this address, so one fragmented frame of somebody else's must not
        // be able to stop the recording (audit 2026-10-03, OA-1).
        var packet = Corrupt(FirstPacketTests.Packet(false, 100, 2), corruption);
        var created = 0;
        var buffer = new FirstPacketBuffer(FirstPacketTests.Local, 42, _ => { created++; return new FirstPacketTests.Sink(); });
        buffer.Offer(packet, 101);
        buffer.Pump(new[] { FirstPacketTests.Owned() });
        Assert.Null(buffer.Failure);
        Assert.Equal(0, created);
        Assert.Equal((0, 0, 0), buffer.Usage);
        Assert.Equal(1, buffer.Counters.RawPackets);
        Assert.Equal(1, buffer.Counters.DroppedNoStream);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AnUnsafeFrameOnTheGamesOwnStreamLeavesAHoleTheDecoderIsNeverFedAcross(int corruption)
    {
        var sink = new FirstPacketTests.Sink();
        var buffer = new FirstPacketBuffer(FirstPacketTests.Local, 42, _ => sink);
        buffer.Offer(FirstPacketTests.Packet(false, 100, 2), 101);
        buffer.Pump(new[] { FirstPacketTests.Owned() });
        Assert.Single(sink.Packets);

        buffer.Offer(Corrupt(FirstPacketTests.Packet(false, 101, 24, FirstPacketTests.Bundle(1)), corruption), 101);
        buffer.Offer(FirstPacketTests.Packet(false, 177, 24, FirstPacketTests.Bundle(2)), 101);
        buffer.Pump(new[] { FirstPacketTests.Owned() });

        Assert.Null(buffer.Failure);
        Assert.Single(sink.Packets);
        Assert.Equal(1, buffer.Usage.Packets); // Waits behind the hole for the gap tolerance.
    }

    private static byte[] Corrupt(byte[] packet, int corruption)
    {
        switch (corruption)
        {
            case 0: packet[0] = 0x41; break;
            case 1: packet[3] = (byte)(packet.Length + 10); break;
            case 2: packet[6] = 0x20; break;
            case 3: packet[7] = 1; break;
            case 4: packet[32] = 0x40; break;
            case 5: packet[6] = 0x80; break;
        }
        return packet;
    }

    [Theory]
    [InlineData(6)]
    [InlineData(113)]
    [InlineData(999)]
    public void UnsupportedLinkDoesNotGuessAnIpv4Offset(int link)
    {
        Assert.False(FirstPacketFrame.TryRead(FirstPacketTests.Packet(false, 100, 2), link,
            FirstPacketTests.Local, out _, out _));
    }

    [Fact]
    public void EthernetVlanAndPaddingUseDeclaredIpv4Length()
    {
        var raw = FirstPacketTests.Packet(false, 100, 2);
        var ethernet = new byte[18 + raw.Length + 12];
        ethernet[12] = 0x81; ethernet[13] = 0;
        ethernet[16] = 8; ethernet[17] = 0;
        raw.CopyTo(ethernet, 18);
        Assert.True(FirstPacketFrame.TryRead(ethernet, 1, FirstPacketTests.Local, out var frame, out _));
        Assert.Equal(18, frame.Offset);
        Assert.Equal(raw.Length, frame.Length);
    }

    /// <summary>
    /// The device list must be taken again on every open.
    ///
    /// SharpPcap's <c>LibPcapLiveDeviceList.Instance</c> is a lazily created static singleton
    /// that enumerates once per process. A card created afterwards -- the TAP adapter a 加速器
    /// installs, a VPN interface, a card that changed address over DHCP -- stays invisible to
    /// it, so the controller can recommend a card the reader is never able to open for the
    /// whole life of the process (review finding H-1).
    /// </summary>
    [Fact]
    public void EveryOpenEnumeratesTheDeviceListAgainRatherThanReusingASnapshot()
    {
        var listings = 0;
        using var reader = new NpcapPacketReader(
            Options(), () => { listings++; return Array.Empty<SharpPcap.LibPcap.LibPcapLiveDevice>(); });

        Assert.Throws<CollectorException>(reader.Open);
        Assert.Equal(1, listings);

        Assert.Throws<CollectorException>(reader.Open);
        Assert.Equal(2, listings);
    }

    /// <summary>
    /// A card that is no longer in the list is a stale selection, not a missing driver. The
    /// distinction decides what the user is told and how long the software waits before trying
    /// again: <c>ERR_NPCAP_MISSING</c> advises reinstalling a driver that is working and
    /// carries a thirty-second back-off (review finding H-1).
    /// </summary>
    [Fact]
    public void AVanishedAdapterIsABadRequestAboutTheAdapter_NotAMissingNpcap()
    {
        using var reader = new NpcapPacketReader(
            Options(), () => Array.Empty<SharpPcap.LibPcap.LibPcapLiveDevice>());

        var error = Assert.Throws<CollectorException>(reader.Open);

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal("adapter_id", error.Field);
        Assert.True(error.Retryable);
        Assert.Equal("wifi", error.Details!["adapter_id"]);
        Assert.Contains("网卡列表", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Npcap", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The translation layer must preserve that distinction: mapping anything that mentions
    /// Npcap onto <c>ERR_NPCAP_MISSING</c> restores the wrong advice.
    /// </summary>
    [Fact]
    public void TranslateKeepsTheVanishedAdapterRefusalIntact()
    {
        var direct = MachinaCaptureSource.Translate(NpcapPacketReader.AdapterListChanged("wifi"), "wifi");
        Assert.Equal(ErrorCodes.BadRequest, direct.Code);
        Assert.Equal("adapter_id", direct.Field);

        // Even wrapped by something that does not carry the code, the message still decides.
        var wrapped = MachinaCaptureSource.Translate(
            new InvalidOperationException(NpcapPacketReader.AdapterListChangedMessage), "wifi");
        Assert.Equal(ErrorCodes.BadRequest, wrapped.Code);
        Assert.Equal("adapter_id", wrapped.Field);

        // A genuine driver failure is still reported as one.
        var driver = MachinaCaptureSource.Translate(new IOException("pcap open failed"), "wifi");
        Assert.Equal(ErrorCodes.NpcapMissing, driver.Code);
    }

    /// <summary>
    /// Audit 2026-10-03, OA-7. A start that failed for a reason that has nothing to do with the
    /// driver -- an unsupported link type, a reader that did not come up, the Oodle setup --
    /// must not be reported as a missing Npcap: the Desktop turns that code into "install
    /// Npcap", which sends the player after a driver that is working.
    /// </summary>
    [Theory]
    [InlineData("所选网卡的数据链路类型不受支持。")]
    [InlineData("准备阶段网卡读取未能安全启动。")]
    [InlineData("synthetic Oodle initialization failure")]
    public void AStartFailureThatIsNotAboutNpcapIsNotReportedAsAMissingNpcap(string message)
    {
        var error = MachinaCaptureSource.Translate(new IOException(message), "wifi");

        Assert.Equal(ErrorCodes.Internal, error.Code);
        Assert.DoesNotContain("Npcap", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CaptureStartOptions Options() => new(
        "adapter-refresh", 42, IPAddress.Parse("192.0.2.10"), "wifi", OodleMode.LibraryTcp, null, null);
}
