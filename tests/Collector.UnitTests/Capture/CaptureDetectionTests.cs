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

    /// <summary>
    /// Audit 2026-10-03 OB-8. <c>wpcap.dll</c> is Npcap's build of libpcap and carries libpcap's
    /// version (1.10.6 on a machine with Npcap 1.88 installed); <c>Packet.dll</c> carries Npcap's
    /// own. The version reported as Npcap's is Npcap's.
    /// </summary>
    [Fact]
    public void Npcap_ReportsItsOwnVersionNotTheLibpcapOneInsideIt()
    {
        var environment = FakeNpcapEnvironment.Healthy()
            .WithFileVersion(@"C:\Windows\System32\Npcap\Packet.dll", "1.88")
            .WithFileVersion(@"C:\Windows\System32\Npcap\wpcap.dll", "1.10.6");

        Assert.Equal("1.88", new NpcapDetector(environment).Detect().Version);
    }

    /// <summary>
    /// When only the libpcap library answers, its number is still worth showing, but labelled as
    /// what it is rather than passed off as Npcap's.
    /// </summary>
    [Fact]
    public void Npcap_LabelsTheLibpcapVersionWhenItIsAllThatCanBeRead()
    {
        var environment = FakeNpcapEnvironment.Healthy()
            .WithFileVersion(@"C:\Windows\System32\Npcap\Packet.dll", null)
            .WithFileVersion(@"C:\Windows\System32\Npcap\wpcap.dll", "1.10.6");

        Assert.Equal("libpcap 1.10.6", new NpcapDetector(environment).Detect().Version);
    }

    [Fact]
    public void Npcap_IsNotInstalled_WhenNothingIsPresent()
    {
        var detection = new NpcapDetector(new FakeNpcapEnvironment()).Detect();

        Assert.Equal(NpcapStatus.NotInstalled, detection.Status);
        Assert.False(detection.Installed);
        Assert.False(detection.Usable);
        Assert.Null(detection.Version);

        // The guidance is the whole answer in this case: it must name the install option, say
        // that the running program downloads nothing, and not deny what the installer does -- it
        // fetches the official Npcap installer when Npcap is missing (audit 2026-10-03, CS-8).
        Assert.Contains("WinPcap API-compatible Mode", detection.Guidance.Chinese, StringComparison.Ordinal);
        Assert.Contains("运行时不会替您下载", detection.Guidance.Chinese, StringComparison.Ordinal);
        Assert.Contains("安装程序", detection.Guidance.Chinese, StringComparison.Ordinal);
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
        Assert.Contains("管理员", detection.Guidance.Chinese, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(@"C:\Program Files\USERJOY GAMES\FINAL FANTASY XIV TC\game\ffxiv_dx11.exe")]
    [InlineData(@"D:\Games\FINAL FANTASY XIV TC\game\ffxiv_dx11.exe")]
    [InlineData(@"E:\userjoy games\ffxiv\game\ffxiv_dx11.exe")]
    public void Game_RecognisesTheTraditionalChineseLauncherFolder(string exe)
    {
        // The launcher's default folder as players reported it; either half of it is enough.
        var processes = new FakeGameProcessProvider().Add(GameProcessLocator.Dx11ProcessName, 7, null, exe);
        var version = Path.Combine(Path.GetDirectoryName(exe)!, GameProcessLocator.VersionFileName);
        var files = new FakeGameFileReader().With(version, "2026.09.14.0000.0000");

        var detection = new GameProcessLocator(processes, files).Locate();

        Assert.Equal(Region.Tc, detection.Region);
        Assert.Equal("2026.09.14.0000.0000", detection.GameBuild);
        Assert.Empty(detection.Warnings);
    }

    /// <summary>
    /// Audit 2026-11-03, A2-1. A manual region that was not chosen for this client never relabels
    /// a client whose install path names another region: a TC tester's override must not make the
    /// CN client unrecordable as TC, and a CN override must not arm CN calibration on a TC client.
    /// The client fails closed and says why.
    /// </summary>
    [Theory]
    [InlineData(Region.Cn, @"C:\Program Files\USERJOY GAMES\FINAL FANTASY XIV TC\game\ffxiv_dx11.exe")]
    [InlineData(Region.Tc, @"D:\最终幻想XIV\game\ffxiv_dx11.exe")]
    [InlineData(Region.Global, @"D:\SdoA\FFXIV\game\ffxiv_dx11.exe")]
    public void AManualRegionNotChosenForThisClientNeverRelabelsAPathThatNamesAnotherRegion(Region chosen, string exe)
    {
        var processes = new FakeGameProcessProvider().Add(GameProcessLocator.Dx11ProcessName, 7, null, exe);

        var detection = new GameProcessLocator(processes, new FakeGameFileReader())
            .WithRegionOverride(() => chosen).Locate();

        Assert.Equal(Region.Unknown, detection.Region);
        Assert.Contains(detection.Warnings, warning =>
            warning.Key == Contracts.Texts.CollectorTexts.CaptureGameRegionOverrideMismatch().Key);
    }

    /// <summary>
    /// Audit 2026-11-03, A2-1. A region chosen while a client runs is bound to that installation:
    /// it keeps fixing a client whose path is misleading (a TC client in a folder named in
    /// Simplified Chinese), still applies to any client whose path names no region, and fails
    /// closed for a different client whose path names another region. Only a fingerprint of the
    /// path is stored, never the path (docs/privacy-boundary.md).
    /// </summary>
    [Fact]
    public void AManualRegionStaysBoundToTheInstallationItWasChosenFor()
    {
        const string misleading = @"D:\最终幻想14繁中服\game\ffxiv_dx11.exe";
        const string cn = @"D:\SdoA\FFXIV\game\ffxiv_dx11.exe";
        const string unmarked = @"D:\Games\FF14\game\ffxiv_dx11.exe";
        using var db = new TestDatabase();
        var settings = new Storage.Repositories.SettingsRepository(db.Database, db.Clock);
        settings.EnsureDefaults();
        var processes = new FakeGameProcessProvider().Add(GameProcessLocator.Dx11ProcessName, 7, null, misleading);
        var locator = new GameProcessLocator(processes, new FakeGameFileReader())
            .WithRegionOverride(() => CaptureSettingsStore.ReadRegionOverride(settings));

        CaptureSettingsStore.Apply(settings, new CaptureSettingsUpdate { RegionOverride = Region.Tc, RegionOverrideSpecified = true });
        var chosenFor = locator.Locate();
        Assert.Equal(Region.Tc, chosenFor.Region);
        Assert.DoesNotContain(chosenFor.Warnings, warning =>
            warning.Key == Contracts.Texts.CollectorTexts.CaptureGameRegionOverrideMismatch().Key);
        // The path still names CN, so the relabelling is said out loud (re-review 2026-11-03, RV1-5).
        Assert.Contains(chosenFor.Warnings, warning =>
            warning.Key == Contracts.Texts.CollectorTexts.CaptureGameRegionOverrideApplied().Key);
        var stored = settings.GetSetting(CaptureSettingsStore.RegionOverrideInstallSetting);
        Assert.NotNull(stored);
        Assert.DoesNotContain("繁中服", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("ffxiv_dx11", stored, StringComparison.OrdinalIgnoreCase);

        processes.Clear();
        processes.Add(GameProcessLocator.Dx11ProcessName, 8, null, cn);
        var other = locator.Locate();
        Assert.Equal(Region.Unknown, other.Region);
        Assert.Contains(other.Warnings, warning =>
            warning.Key == Contracts.Texts.CollectorTexts.CaptureGameRegionOverrideMismatch().Key);

        processes.Clear();
        processes.Add(GameProcessLocator.Dx11ProcessName, 9, null, unmarked);
        var unmarkedClient = locator.Locate();
        Assert.Equal(Region.Tc, unmarkedClient.Region);
        Assert.DoesNotContain(unmarkedClient.Warnings, warning =>
            warning.Key == Contracts.Texts.CollectorTexts.CaptureGameRegionOverrideApplied().Key);

        processes.Clear();
        processes.Add(GameProcessLocator.Dx11ProcessName, 10, null, misleading.ToUpperInvariant());
        Assert.Equal(Region.Tc, locator.Locate().Region);

        CaptureSettingsStore.Apply(settings, new CaptureSettingsUpdate { RegionOverride = null, RegionOverrideSpecified = true });
        Assert.Equal(Region.Cn, locator.Locate().Region);
        Assert.Equal("null", settings.GetSetting(CaptureSettingsStore.RegionOverrideInstallSetting));
    }

    [Fact]
    public void AnUnprovenTraditionalClientPathNeedsExplicitRegionSelection()
    {
        const string exe = @"D:\Games\TC\game\ffxiv_dx11.exe";
        var processes = new FakeGameProcessProvider().Add(GameProcessLocator.Dx11ProcessName, 7, null, exe);
        var files = new FakeGameFileReader().With(@"D:\Games\TC\game\ffxivgame.ver", "2026.10.01.0000.0000");
        var locator = new GameProcessLocator(processes, files);

        Assert.Equal(Region.Unknown, locator.Locate().Region);
        var selected = locator.WithRegionOverride(() => Region.Tc).Locate();
        Assert.Equal(Region.Tc, selected.Region);
        Assert.Equal("2026.10.01.0000.0000", selected.GameBuild);
        Assert.Equal("TC", EnumWire<Region>.Format(selected.Region));
    }

    [Theory]
    [InlineData(@"D:\最终幻想\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe")]
    [InlineData(@"E:\ff14cn\Square Enix\FINAL FANTASY XIV - A Realm Reborn\game\ffxiv_dx11.exe")]
    [InlineData(@"D:\最终幻想\USERJOY GAMES\FINAL FANTASY XIV TC\game\ffxiv_dx11.exe")]
    [InlineData(@"F:\Square Enix\FINAL FANTASY XIV TC\game\ffxiv_dx11.exe")]
    public void Game_LeavesTheRegionUnknownAndSaysWhy_WhenThePathCarriesMarkersOfBothRegions(string exe)
    {
        var processes = new FakeGameProcessProvider().Add(GameProcessLocator.Dx11ProcessName, 7, null, exe);

        var detection = new GameProcessLocator(processes, new FakeGameFileReader()).Locate();

        // Which marker wins would be a guess; the region stays unknown and the user is told why.
        Assert.Equal(Region.Unknown, detection.Region);
        Assert.Contains(detection.Warnings, warning => warning.Chinese.Contains("同时含有", StringComparison.Ordinal));
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
        Assert.Contains(detection.Warnings, warning => warning.Chinese.Contains("2 个", StringComparison.Ordinal));
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
        Assert.Contains(detection.Warnings, warning => warning.Chinese.Contains("权限", StringComparison.Ordinal));
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

    [Theory]
    [InlineData(false, false, "10.1.2.3")]
    [InlineData(true, false, "::1")]
    [InlineData(true, true, "127.0.0.1")]
    public void Adapter_SkipsUnusableRememberedAdapterBeforeTheFirstConnection(
        bool isUp, bool isLoopback, string address)
    {
        var enumerator = new AdapterEnumerator(
            new FakeAdapterProvider()
                .Add("old", "Old adapter", isUp: isUp, isLoopback: isLoopback, addresses: address)
                .Add("current", "Current route", hasDefaultRoute: true, addresses: "192.168.31.77"),
            new FakeProcessTcpTable().With(4321));

        var adapters = enumerator.List(4321, preferredAdapterId: "old");

        Assert.Equal("current", Assert.Single(adapters, adapter => adapter.Recommended).Id);
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
