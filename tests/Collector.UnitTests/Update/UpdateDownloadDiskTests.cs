using MentorRecorder.Collector.Update;
using Failure = MentorRecorder.Collector.Update.UpdateDownloadFailure;
using State = MentorRecorder.Collector.Update.UpdateDownloadState;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// 下载并安装: the <c>updates</c> folder - refused when it is not a plain folder of ours, a final name that cannot be
/// replaced, held so it cannot be swapped while in use - links left under our names, what READY names, and the
/// leftovers removed at startup and when a download starts, ours only.
/// </summary>
public sealed class UpdateDownloadDiskTests : UpdateDownloadTestBase
{
    // ----------------------------------------------------------------------------------- disk

    [Fact]
    public async Task AnUpdatesFolderThatIsAFileIsADiskFailureAndNothingIsSent()
    {
        await File.WriteAllTextAsync(Updates, "not a folder");
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport);

        service.Start();

        AssertFailed(await SettleAsync(service), Failure.DiskFailed);
        Assert.Empty(transport.Requests);
        Assert.Equal("not a folder", await File.ReadAllTextAsync(Updates));
    }

    [Fact]
    public async Task AnUpdatesFolderThatIsAJunctionIsRefusedAndNothingBehindItIsTouched()
    {
        var elsewhere = Path.Combine(Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var bait = new[]
        {
            Path.Combine(elsewhere, "MentorRecorder-1.0.0-setup.exe.part"),
            Path.Combine(elsewhere, "MentorRecorder-0.9.0-setup.exe"),
        };
        foreach (var path in bait)
        {
            await File.WriteAllTextAsync(path, "not ours");
        }

        await Junction.CreateAsync(Updates, elsewhere);
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport);

        Assert.Equal(0, service.RemoveLeftovers());
        service.Start();

        AssertFailed(await SettleAsync(service), Failure.DiskFailed);
        Assert.Empty(transport.Requests);
        Assert.All(bait, path => Assert.Equal("not ours", File.ReadAllText(path)));
        Assert.Equal(2, Directory.EnumerateFiles(elsewhere).Count());
    }

    [Fact]
    public async Task AFinalNameThatCannotBeReplacedIsADiskFailureAndThePartIsRemoved()
    {
        Directory.CreateDirectory(InstallerPath());
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport);

        service.Start();

        AssertFailed(await SettleAsync(service), Failure.DiskFailed);
        Assert.False(File.Exists(PartPath()));
        Assert.True(Directory.Exists(InstallerPath()));
    }

    // ------------------------------------------------------- the folder held while it is used

    /// <summary>
    /// <c>updates</c> is checked once, then used for seconds - the checksum request alone may take fifteen. While a
    /// download uses it, it is held open, so another process cannot move it aside or remove it and put a link to some
    /// other folder in its place: everything the download writes stays in the folder that was checked.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdatesCannotBeSwappedForALinkWhileADownloadUsesIt(bool moveAside)
    {
        var elsewhere = Path.Combine(Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var swapped = (bool?)null;
        var transport = new Transport().Publish(
            Published, Installer, onChecksum: () => Task.FromResult(swapped = TrySwapUpdates(elsewhere, moveAside)));
        var service = Service(transport);

        service.Start();
        var ready = await SettleAsync(service);

        Assert.False(swapped, "updates must stay in place while the download uses it");
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        Assert.Equal(State.Ready, ready.State);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(InstallerPath()));
    }

    /// <summary>
    /// A cancel that lands after the rename removes the installer again. If <c>updates</c> was swapped for a link by
    /// then, that removal must not reach through it: the file of the same name behind the link is not ours.
    /// </summary>
    [Fact]
    public async Task AnInstallerRemovedAfterACancelIsNeverRemovedThroughALink()
    {
        var elsewhere = Path.Combine(Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var bait = Path.Combine(elsewhere, Path.GetFileName(InstallerPath()));
        await File.WriteAllTextAsync(bait, "not ours");
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport);
        var swapped = false;
        service.BeforeOutcome = () =>
        {
            service.Cancel();
            swapped = TrySwapUpdates(elsewhere);
        };

        service.Start();
        await SettleAsync(service);

        Assert.True(swapped, "the download has let go of updates by now, so the swap is possible");
        Assert.Equal("not ours", await File.ReadAllTextAsync(bait));
    }

    /// <summary>A READY installer given up because the check was switched off is likewise never removed through a link.</summary>
    [Fact]
    public async Task AReadyInstallerGivenUpIsNeverRemovedThroughALink()
    {
        var elsewhere = Path.Combine(Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var bait = Path.Combine(elsewhere, Path.GetFileName(InstallerPath()));
        await File.WriteAllTextAsync(bait, "not ours");
        var service = Service(new Transport().Publish(Published, Installer));
        service.Start();
        Assert.Equal(State.Ready, (await SettleAsync(service)).State);
        Assert.True(TrySwapUpdates(elsewhere));

        LastCheck.ApplySetting(false);
        AssertFailed(service.Start(), Failure.Disabled);

        Assert.Equal("not ours", await File.ReadAllTextAsync(bait));
    }

    // ------------------------------------------------------------------ links at our names

    /// <summary>
    /// A hard link needs no privilege, so another process can leave one under the <c>.part</c> name or the final name,
    /// pointing at a file of its choosing. Each name is removed as an entry and created afresh: the file behind the
    /// link is never written to.
    /// </summary>
    [Fact]
    public async Task AHardLinkAtThePartOrFinalNameIsReplacedNeverWrittenThrough()
    {
        var outsidePart = Path.Combine(Root, "outside-part.bin");
        var outsideFinal = Path.Combine(Root, "outside-final.bin");
        await File.WriteAllTextAsync(outsidePart, "precious");
        await File.WriteAllTextAsync(outsideFinal, "precious");
        var transport = new Transport().Publish(Published, Installer, onChecksum: async () =>
        {
            await HardLink.CreateAsync(PartPath(), outsidePart);
            await HardLink.CreateAsync(InstallerPath(), outsideFinal);
        });
        var service = Service(transport);

        service.Start();
        var ready = await SettleAsync(service);

        Assert.Equal(State.Ready, ready.State);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(InstallerPath()));
        Assert.Equal("precious", await File.ReadAllTextAsync(outsidePart));
        Assert.Equal("precious", await File.ReadAllTextAsync(outsideFinal));
    }

    /// <summary>A junction under either name cannot be replaced by a file; the download fails and writes nothing behind it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AJunctionAtThePartOrFinalNameIsNeverWrittenThrough(bool atPart)
    {
        var elsewhere = Path.Combine(Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var transport = new Transport().Publish(
            Published, Installer, onChecksum: () => Junction.CreateAsync(atPart ? PartPath() : InstallerPath(), elsewhere));
        var service = Service(transport);

        service.Start();

        AssertFailed(await SettleAsync(service), Failure.DiskFailed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        Assert.False(File.Exists(atPart ? InstallerPath() : PartPath()));
    }

    // ---------------------------------------------------------------- what READY names

    /// <summary>
    /// Between the moment the <c>.part</c> is closed and its rename, another process could change it. READY must
    /// describe the file now at its path, not the bytes that went into the <c>.part</c>: a file changed in that
    /// instant is not reported READY and is not kept.
    /// </summary>
    [Fact]
    public async Task APartChangedBeforeItsRenameIsNeitherReportedReadyNorKept()
    {
        var service = Service(new Transport().Publish(Published, Installer));
        var tampered = (byte[])Installer.Clone();
        tampered[1234] ^= 0xFF;
        LastFiles.BeforePromote = () => File.WriteAllBytes(PartPath(), tampered);

        service.Start();
        var settled = await SettleAsync(service);

        AssertFailed(settled, Failure.ChecksumMismatch);
        AssertNothingKept();
    }

    /// <summary>
    /// Another process that keeps the <c>.part</c> open for writing across the rename could change the kept file after
    /// it was checked. The kept file is checked while held open for reading alone, which such a writer prevents: no
    /// READY, and nothing kept once the writer lets go.
    /// </summary>
    [Fact]
    public async Task AFileSomeoneElseCanStillWriteIsNotReportedReady()
    {
        var service = Service(new Transport().Publish(Published, Installer));
        FileStream? writer = null;
        LastFiles.BeforePromote = () => writer = new FileStream(
            PartPath(), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            service.Start();
            var settled = await SettleAsync(service);

            Assert.NotNull(writer);
            AssertFailed(settled, Failure.DiskFailed);
        }
        finally
        {
            writer?.Dispose();
        }

        AssertNothingKept();
    }

    /// <summary>The checksum READY reports is that of the file at the path it reports.</summary>
    [Fact]
    public async Task ReadyNamesTheChecksumOfTheFileAtItsPath()
    {
        var service = Service(new Transport().Publish(Published, Installer));

        service.Start();
        var ready = await SettleAsync(service);

        Assert.Equal(State.Ready, ready.State);
        Assert.Equal(Sha(await File.ReadAllBytesAsync(ready.FilePath!)), ready.Sha256);
    }

    // --------------------------------------------------------------------------------- leftovers

    private static readonly string[] Ours =
    {
        "MentorRecorder-1.0.0-setup.exe.part",
        "MentorRecorder-2.0.0-setup.exe.part",
        "mentorrecorder-1.9.0-SETUP.EXE",
        "MentorRecorder-0.9.0-setup.exe",
    };

    private static readonly string[] NotOurs =
    {
        "notes.txt",
        "MentorRecorder-2.0.0-setup.exe.bak",
        "MentorRecorder-2.0.0-beta.1-setup.exe",
        "MentorRecorder-2.0.0-setup.exe.part.old",
        "Other-1.0.0-setup.exe",
        "MentorRecorder-2.0-setup.exe",
    };

    private async Task SeedLeftoversAsync()
    {
        Directory.CreateDirectory(Updates);
        foreach (var name in Ours.Concat(NotOurs))
        {
            await File.WriteAllTextAsync(Path.Combine(Updates, name), name);
        }

        Directory.CreateDirectory(Path.Combine(Updates, "MentorRecorder-3.0.0-setup.exe"));
    }

    private string[] Remaining() =>
        Directory.EnumerateFileSystemEntries(Updates).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    [Fact]
    public async Task LeftoversAreRemovedAtStartupAndNothingElse()
    {
        await SeedLeftoversAsync();
        await File.WriteAllTextAsync(InstallerPath(), "the newer version, still offered");
        var service = Service(new Transport());

        var removed = service.RemoveLeftovers();

        Assert.Equal(Ours.Length, removed);
        Assert.Equal(
            NotOurs.Append("MentorRecorder-3.0.0-setup.exe").Append("MentorRecorder-2.0.0-setup.exe")
                .Order(StringComparer.Ordinal),
            Remaining());
    }

    [Fact]
    public async Task AtStartupAnInstallerNoLongerOfferedIsALeftoverToo()
    {
        await SeedLeftoversAsync();
        await File.WriteAllTextAsync(InstallerPath(), "already installed");
        var service = Service(new Transport(), local: Published);

        Assert.Equal(Ours.Length + 1, service.RemoveLeftovers());
        Assert.False(File.Exists(InstallerPath()));
    }

    [Fact]
    public void AtStartupAMissingUpdatesFolderIsNotCreated()
    {
        var service = Service(new Transport());

        Assert.Equal(0, service.RemoveLeftovers());
        Assert.False(Directory.Exists(Updates));
    }

    /// <summary>
    /// The clean-up lists <c>updates</c>, then removes what it found by name. It holds the folder for the whole time,
    /// so the folder cannot be swapped for a link in between and the removals cannot reach files of the same names
    /// somewhere else.
    /// </summary>
    [Fact]
    public async Task LeftoversCannotBeRemovedThroughAFolderSwappedMidway()
    {
        await SeedLeftoversAsync();
        var elsewhere = Path.Combine(Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        foreach (var name in Ours)
        {
            await File.WriteAllTextAsync(Path.Combine(elsewhere, name), "not ours");
        }

        var files = new UpdateDownloadFiles(DataDirectory);
        var swapped = (bool?)null;
        files.BeforeRemoving = () => swapped = TrySwapUpdates(elsewhere);

        var removed = files.RemoveLeftovers(Published);

        Assert.False(swapped, "updates must stay in place while it is being cleaned");
        Assert.Equal(Ours.Length, removed);
        Assert.All(Ours, name => Assert.Equal("not ours", File.ReadAllText(Path.Combine(elsewhere, name))));
    }

    [Fact]
    public async Task LeftoversAreRemovedWhenADownloadStarts()
    {
        await SeedLeftoversAsync();
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport);

        service.Start();
        Assert.Equal(State.Ready, (await SettleAsync(service)).State);

        Assert.Equal(
            NotOurs.Append("MentorRecorder-3.0.0-setup.exe").Append("MentorRecorder-2.0.0-setup.exe")
                .Order(StringComparer.Ordinal),
            Remaining());
    }
}
