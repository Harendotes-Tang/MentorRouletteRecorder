using System.Net;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The three detectors that decide whether capture may be attempted: Npcap, the game process
/// and the adapter. Each must report accurately on a machine where none of them is present.
/// </summary>
public sealed class CaptureDetectionTests
{
    [Fact]
    public void Npcap_IsReady_WhenInstalledCompatibleAndUnrestricted()
    {
        var detection = new NpcapDetector(FakeNpcapEnvironment.Healthy()).Detect();

        Assert.Equal(NpcapStatus.Ready, detection.Status);
        Assert.True(detection.Usable);
        Assert.True(detection.Installed);
        Assert.Equal("1.79", detection.Version);
        Assert.True(detection.WinPcapCompatible);
    }

    [Fact]
    public void Npcap_IsNotInstalled_WhenNothingIsPresent()
    {
        var detection = new NpcapDetector(new FakeNpcapEnvironment()).Detect();

        Assert.Equal(NpcapStatus.NotInstalled, detection.Status);
        Assert.False(detection.Installed);
        Assert.False(detection.Usable);
        Assert.Null(detection.Version);

        // The guidance is the whole answer in this case: it must name the install option and
        // must never suggest that this software downloads anything.
        Assert.Contains("WinPcap API-compatible Mode", detection.Guidance, StringComparison.Ordinal);
        Assert.Contains("不会替您下载", detection.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void Npcap_IsLoadFailed_WhenRegisteredButLibrariesAreGone()
    {
        var environment = new FakeNpcapEnvironment().WithKey(NpcapDetector.RegistryKey);

        var detection = new NpcapDetector(environment).Detect();

        // Registered but broken must stay distinct from never installed: the two lead to
        // different guidance ("install it" versus "your installation is damaged").
        Assert.Equal(NpcapStatus.LoadFailed, detection.Status);
        Assert.True(detection.Installed);
        Assert.False(detection.Usable);
    }

    [Fact]
    public void Npcap_IsNotWinPcapCompatible_WhenTheModeWasNotInstalled()
    {
        var environment = new FakeNpcapEnvironment { Version = "1.80" };
        environment
            .WithFile(@"C:\Windows\System32\Npcap\wpcap.dll")
            .WithFile(@"C:\Windows\System32\Npcap\Packet.dll")
            .WithValue(NpcapDetector.RegistryKey, NpcapDetector.WinPcapCompatibleValue, 0);

        var detection = new NpcapDetector(environment).Detect();

        Assert.Equal(NpcapStatus.NotWinPcapCompatible, detection.Status);
        Assert.False(detection.Usable);
        Assert.False(detection.WinPcapCompatible);
    }

    [Fact]
    public void Npcap_FallsBackToTheCompatibilityShim_WhenTheRegistryFlagIsAbsent()
    {
        var environment = new FakeNpcapEnvironment { Version = "1.80" };
        environment
            .WithKey(NpcapDetector.RegistryKey)
            .WithFile(@"C:\Windows\System32\Npcap\wpcap.dll")
            .WithFile(@"C:\Windows\System32\Npcap\Packet.dll")
            .WithFile(@"C:\Windows\System32\wpcap.dll");

        Assert.Equal(NpcapStatus.Ready, new NpcapDetector(environment).Detect().Status);
    }

    [Fact]
    public void Npcap_IsAdminOnly_WhenRestrictedAndWeAreNotElevated()
    {
        var environment = FakeNpcapEnvironment.Healthy();
        environment.WithValue(NpcapDetector.RegistryKey, NpcapDetector.AdminOnlyValue, 1);
        environment.IsElevated = false;

        var detection = new NpcapDetector(environment).Detect();

        Assert.Equal(NpcapStatus.AdminOnly, detection.Status);
        Assert.False(detection.Usable);
        Assert.True(detection.AdminOnly);
        Assert.Contains("管理员", detection.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void Npcap_IsReady_WhenRestrictedButWeAreElevated()
    {
        var environment = FakeNpcapEnvironment.Healthy();
        environment.WithValue(NpcapDetector.RegistryKey, NpcapDetector.AdminOnlyValue, 1);
        environment.IsElevated = true;

        var detection = new NpcapDetector(environment).Detect();

        Assert.Equal(NpcapStatus.Ready, detection.Status);
        Assert.True(detection.AdminOnly);
    }

    [Fact]
    public void Game_IsNotRunning_WhenNoProcessMatches()
    {
        var detection = new GameProcessLocator(new FakeGameProcessProvider(), new FakeGameFileReader()).Locate();

        Assert.False(detection.Running);
        Assert.Null(detection.ProcessId);
        Assert.Equal(Region.Unknown, detection.Region);
        Assert.Empty(detection.Warnings);
    }

    [Fact]
    public void Game_ReadsBuildAndRegion_FromTheInstallPath()
    {
        const string exe = @"D:\SdoA\FFXIV\game\ffxiv_dx11.exe";
        var processes = new FakeGameProcessProvider().Add(
            GameProcessLocator.Dx11ProcessName, 4321, DateTimeOffset.UnixEpoch, exe);
        var files = new FakeGameFileReader().With(@"D:\SdoA\FFXIV\game\ffxivgame.ver", "2024.06.18.0000.0000\n");

        var detection = new GameProcessLocator(processes, files).Locate();

        Assert.True(detection.Running);
        Assert.Equal(4321, detection.ProcessId);
        Assert.Equal(Region.Cn, detection.Region);
        Assert.Equal("2024.06.18.0000.0000", detection.GameBuild);
        Assert.Equal(1, detection.InstanceCount);
        Assert.Empty(detection.Warnings);
    }

    [Fact]
    public void Game_RecognisesTheGlobalInstallPath()
    {
        const string exe = @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe";
        var processes = new FakeGameProcessProvider().Add(GameProcessLocator.Dx11ProcessName, 7, null, exe);

        Assert.Equal(Region.Global, new GameProcessLocator(processes, new FakeGameFileReader()).Locate().Region);
    }

    [Fact]
    public void Game_PicksTheOldestInstanceAndWarns_WhenSeveralAreRunning()
    {
        var older = new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);
        var processes = new FakeGameProcessProvider()
            .Add(GameProcessLocator.Dx11ProcessName, 200, older.AddMinutes(30), @"D:\g\game\ffxiv_dx11.exe")
            .Add(GameProcessLocator.Dx11ProcessName, 100, older, @"D:\g\game\ffxiv_dx11.exe");

        var detection = new GameProcessLocator(processes, new FakeGameFileReader()).Locate();

        // Two clients is a legitimate situation, not an error: the choice must be
        // deterministic and the ambiguity reported so the user can override it.
        Assert.Equal(100, detection.ProcessId);
        Assert.Equal(2, detection.InstanceCount);
        Assert.Contains(detection.Warnings, warning => warning.Contains("2 个", StringComparison.Ordinal));
    }

    [Fact]
    public void Game_StaysUnknownAndWarns_WhenThePathIsAccessDenied()
    {
        var processes = new FakeGameProcessProvider().Add(
            GameProcessLocator.Dx11ProcessName, 55, DateTimeOffset.UnixEpoch, path: null, accessDenied: true);

        var detection = new GameProcessLocator(processes, new FakeGameFileReader()).Locate();

        Assert.True(detection.Running);
        Assert.Equal(55, detection.ProcessId);
        Assert.Equal(Region.Unknown, detection.Region);
        Assert.Null(detection.GameBuild);
        Assert.Contains(detection.Warnings, warning => warning.Contains("权限", StringComparison.Ordinal));
    }

    [Fact]
    public void Game_AlsoFindsTheLegacyClient()
    {
        var processes = new FakeGameProcessProvider().Add(GameProcessLocator.LegacyProcessName, 9);

        Assert.True(new GameProcessLocator(processes, new FakeGameFileReader()).Locate().Running);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2024.06.18 <script>")]
    [InlineData("这不是版本号")]
    public void Game_RefusesABuildStringThatIsNotAPlainToken(string content)
    {
        const string exe = @"D:\g\game\ffxiv_dx11.exe";
        var files = new FakeGameFileReader().With(@"D:\g\game\ffxivgame.ver", content);

        // A build string reaches a database column and the wire, so anything that is not a
        // short, plainly formed token must read as unreadable rather than be passed through.
        Assert.Null(new GameProcessLocator(new FakeGameProcessProvider(), files).ReadBuild(exe));
    }

    [Fact]
    public void Game_RefusesABuildStringThatIsTooLong()
    {
        const string exe = @"D:\g\game\ffxiv_dx11.exe";
        var files = new FakeGameFileReader().With(
            @"D:\g\game\ffxivgame.ver", new string('1', GameProcessLocator.MaxBuildLength + 1));

        Assert.Null(new GameProcessLocator(new FakeGameProcessProvider(), files).ReadBuild(exe));
    }

    [Theory]
    [InlineData("192.168.1.34", "192.168.1.x")]
    [InlineData("10.0.0.1", "10.0.0.x")]
    [InlineData("127.0.0.1", "127.0.0.x")]
    public void Adapter_MasksTheHostPartOfAnAddress(string address, string expected) =>
        Assert.Equal(expected, AdapterEnumerator.Mask(IPAddress.Parse(address)));

    [Fact]
    public void Adapter_MasksEverything_ForAnAddressItCannotShorten()
    {
        Assert.Equal("x.x.x.x", AdapterEnumerator.Mask(null));
        Assert.Equal("x.x.x.x", AdapterEnumerator.Mask(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void Adapter_NeverRendersAnUnmaskedAddress()
    {
        var enumerator = new AdapterEnumerator(
            new FakeAdapterProvider().Add("wifi", "Wi-Fi", addresses: "192.168.31.77"),
            new FakeProcessTcpTable());

        var adapters = enumerator.List(gameProcessId: null);

        Assert.Equal(new[] { "192.168.31.x" }, adapters[0].MaskedIPv4);
        Assert.DoesNotContain(
            adapters[0].MaskedIPv4, masked => masked.Contains("77", StringComparison.Ordinal));
    }

    [Fact]
    public void Adapter_RecommendsTheOneCarryingTheGamesConnections()
    {
        var enumerator = new AdapterEnumerator(
            new FakeAdapterProvider()
                .Add("loopback", "Loopback", isLoopback: true, addresses: "127.0.0.1")
                .Add("wired", "Ethernet", addresses: "10.1.2.3")
                .Add("wifi", "Wi-Fi", addresses: "192.168.31.77"),
            new FakeProcessTcpTable().With(4321, "192.168.31.77"));

        var adapters = enumerator.List(4321);

        var recommended = Assert.Single(adapters, adapter => adapter.Recommended);
        Assert.Equal("wifi", recommended.Id);
        Assert.True(recommended.CarriesGameTraffic);
        Assert.Equal(IPAddress.Parse("192.168.31.77"), recommended.BindAddress);
    }

    [Fact]
    public void Adapter_RecommendsNothing_WhenTheGamesTrafficCannotBeLocated()
    {
        var enumerator = new AdapterEnumerator(
            new FakeAdapterProvider()
                .Add("wired", "Ethernet", addresses: "10.1.2.3")
                .Add("wifi", "Wi-Fi", addresses: "192.168.31.77"),
            new FakeProcessTcpTable());

        // Guessing would bind a capture that observes nothing while reporting itself healthy,
        // so no adapter may be recommended and the user must choose.
        Assert.DoesNotContain(enumerator.List(4321), adapter => adapter.Recommended);
    }

    [Fact]
    public void Adapter_PrefersTheAdapterTheUserChoseBefore()
    {
        // The game's traffic is not locatable, so nothing weighs against the remembered
        // choice and it stands.
        var enumerator = new AdapterEnumerator(
            new FakeAdapterProvider()
                .Add("wired", "Ethernet", addresses: "10.1.2.3")
                .Add("wifi", "Wi-Fi", addresses: "192.168.31.77"),
            new FakeProcessTcpTable());

        var adapters = enumerator.List(4321, preferredAdapterId: "wired");

        Assert.Equal("wired", Assert.Single(adapters, adapter => adapter.Recommended).Id);
        Assert.DoesNotContain(adapters, adapter => adapter.PreferenceStale);
    }

    [Fact]
    public void Adapter_YieldsTheRememberedChoiceToTheOneCarryingTheGame()
    {
        // A 加速器 or a VPN moves the game onto another card. The adapter carrying the game
        // must win over the remembered one, otherwise capture binds to an address the game no
        // longer uses: the pcap filter drops every packet while the capture reports RUNNING.
        var enumerator = new AdapterEnumerator(
            new FakeAdapterProvider()
                .Add("wired", "Ethernet", addresses: "10.1.2.3")
                .Add("wifi", "Wi-Fi", addresses: "192.168.31.77"),
            new FakeProcessTcpTable().With(4321, "192.168.31.77"));

        var adapters = enumerator.List(4321, preferredAdapterId: "wired");

        Assert.Equal("wifi", Assert.Single(adapters, adapter => adapter.Recommended).Id);
        Assert.Equal("wired", Assert.Single(adapters, adapter => adapter.PreferenceStale).Id);
    }

    [Fact]
    public void Adapter_FindIsCaseInsensitiveAndRejectsBlanks()
    {
        var adapters = new AdapterEnumerator(
            new FakeAdapterProvider().Add("{ABC-123}", "Wi-Fi"), new FakeProcessTcpTable()).List(null);

        Assert.NotNull(AdapterEnumerator.Find(adapters, "{abc-123}"));
        Assert.Null(AdapterEnumerator.Find(adapters, "  "));
        Assert.Null(AdapterEnumerator.Find(adapters, null));
    }
}
