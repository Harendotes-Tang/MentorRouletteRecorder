using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Machina.FFXIV.Memory;
using Machina.FFXIV.Oodle;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MachinaOodleRuntimeCollection
{
    public const string Name = "Machina Oodle runtime";
}

/// <summary>
/// Exercises the unmanaged-image and Machina-static-state failure boundaries without reading
/// a real game executable. These tests do not run in parallel because OodleFactory owns one
/// process-wide native instance.
/// </summary>
[Collection(MachinaOodleRuntimeCollection.Name)]
public sealed class OodleSignatureRuntimeTests : IDisposable
{
    private const string Build = "2026.08.05.0000.0000";
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    private static readonly FieldInfo NativeField =
        typeof(OodleFactory).GetField("_oodleNative", PrivateStatic)
        ?? throw new InvalidOperationException("Machina OodleFactory._oodleNative is unavailable.");

    private static readonly FieldInfo ImplementationField =
        typeof(OodleFactory).GetField("_oodleImplementation", PrivateStatic)
        ?? throw new InvalidOperationException("Machina OodleFactory._oodleImplementation is unavailable.");

    private static readonly object FactoryLock =
        typeof(OodleFactory).GetField("_lock", PrivateStatic)?.GetValue(null)
        ?? throw new InvalidOperationException("Machina OodleFactory._lock is unavailable.");

    private readonly TempDirectory _root = new("OodleRuntime");

    [Theory]
    [InlineData("missing_dos_header")]
    [InlineData("invalid_pe_offset")]
    [InlineData("missing_pe_signature")]
    [InlineData("pe32_instead_of_pe32_plus")]
    [InlineData("zero_image_size")]
    public void ScannerRefusesMalformedPeImages(string malformedCase)
    {
        var profile = LoadProfileForBytes([0x00]);
        var scanner = new OodleSignatureScan(profile);
        var image = BuildMalformedPe(malformedCase);
        var memory = Marshal.AllocHGlobal(image.Length);
        try
        {
            Marshal.Copy(image, 0, memory, image.Length);

            var error = Assert.Throws<InvalidDataException>(() => scanner.Read(memory));

            Assert.Contains("mapped Oodle image", error.Message, StringComparison.Ordinal);
            Assert.Empty(scanner.LastResults);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [Fact]
    public void FailedInstallClearsMachinaNative_WhenValidPeHasNoProfileSignatures()
    {
        var nativeFixture = Path.Combine(AppContext.BaseDirectory, "e_sqlite3.dll");
        Assert.True(File.Exists(nativeFixture), "The x64 test output must contain e_sqlite3.dll.");
        var image = File.ReadAllBytes(nativeFixture);
        var executable = _root.Combine("ffxiv_dx11.exe");
        File.WriteAllBytes(executable, image);
        WriteProfileForExecutable(executable);
        var options = new CaptureStartOptions(
            "test", 1, null, null, OodleMode.FfxivTcp, null, executable, Region.Cn, Build,
            AllowCandidateOodleSignature: true);
        using var logger = new MentorRecorder.Collector.Diagnostics.RotatingFileLogger(
            _root.Combine("logs"), new TestClock(DateTimeOffset.UnixEpoch.AddYears(56)));
        using var runtime = OodleSignatureRuntime.TryCreate(options, _root.Path, logger);
        Assert.NotNull(runtime);

        var originalImplementation = ReadImplementation();
        ClearMachinaNative();
        try
        {
            var error = Assert.Throws<CollectorException>(() => runtime!.Install());

            Assert.Equal(ErrorCodes.Internal, error.Code);
            // Audit 2026-10-20, A2-4: any failure here - a full temp folder, an antivirus scan, a
            // profile that no longer fits - reads the same neutral sentence; the tool a maintainer
            // regenerates the profile with is named in the local log only.
            Assert.Equal(MentorRecorder.Collector.Contracts.Texts.CollectorTexts.CaptureOodleProfileLoadFailed(), error.Text);
            Assert.DoesNotContain("tools/", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("签名档案", error.Message, StringComparison.Ordinal);
            Assert.Contains(File.ReadAllLines(logger.CurrentPath), line =>
                line.Contains("oodle_signature_override_failed", StringComparison.Ordinal) &&
                line.Contains("oodle-signature-finder", StringComparison.Ordinal));
            Assert.Null(ReadMachinaNative());
        }
        finally
        {
            runtime!.Dispose();
            ClearMachinaNative();
            WriteImplementation(originalImplementation);
        }
    }

    [Fact]
    public void NativeUninitializeFailureIsVisibleAndKeepsRuntimeForRetry()
    {
        var profile = LoadProfileForBytes([0]);
        var runtime = (OodleSignatureRuntime)Activator.CreateInstance(typeof(OodleSignatureRuntime),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object?[] { "synthetic", OodleImplementation.FfxivTcp, profile, MentorRecorder.Collector.Diagnostics.RotatingFileLogger.Disabled, null }, null)!;
        var native = DispatchProxy.Create<IOodleNative, FailingNative>();
        var proxy = (FailingNative)native;
        var args = new object?[] { null, null };
        Assert.True((bool)typeof(OodleSignatureRuntime).GetMethod("TryGetRuntimeContract", PrivateStatic)!.Invoke(null, args)!);
        void Set(string name, object value) => typeof(OodleSignatureRuntime).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, value);
        Set("_contract", args[0]!);
        Set("_installedNative", native);
        Set("_installed", true);
        lock (FactoryLock) NativeField.SetValue(null, native);
        try
        {
            Assert.ThrowsAny<Exception>(runtime.Dispose);
            Assert.Same(native, ReadMachinaNative());
            proxy.Fail = false;
            runtime.Dispose();
            Assert.Null(ReadMachinaNative());
            Assert.Equal(2, proxy.Attempts);
        }
        finally { proxy.Fail = false; runtime.Dispose(); ClearMachinaNative(); }
    }

    public class FailingNative : DispatchProxy
    {
        public bool Fail = true;
        public int Attempts;
        public string? MarkerPath;
        public bool MarkerPresentAtFirstAttempt;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "UnInitialize")
            {
                if (Attempts == 0 && MarkerPath is not null) MarkerPresentAtFirstAttempt = File.Exists(MarkerPath);
                Attempts++;
                if (Fail) throw new IOException("synthetic uninitialize fault");
            }
            return targetMethod.ReturnType == typeof(bool) ? false : null;
        }
    }

    [Fact]
    public void FailedInstallRetainsNativeWhoseRollbackCannotUninitialize()
    {
        var runtime = (OodleSignatureRuntime)Activator.CreateInstance(typeof(OodleSignatureRuntime),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object?[] { "synthetic", OodleImplementation.FfxivTcp, LoadProfileForBytes([0]), MentorRecorder.Collector.Diagnostics.RotatingFileLogger.Disabled, null }, null)!;
        var native = DispatchProxy.Create<IOodleNative, FailingNative>();
        var proxy = (FailingNative)native;
        var originalImplementation = ReadImplementation();
        lock (FactoryLock) NativeField.SetValue(null, native);
        WriteImplementation(OodleImplementation.FfxivTcp);
        try
        {
            Assert.Throws<CollectorException>(runtime.Install);
            Assert.ThrowsAny<Exception>(runtime.Dispose);
            Assert.Same(native, ReadMachinaNative());
            proxy.Fail = false;
            runtime.Dispose();
            Assert.Null(ReadMachinaNative());
        }
        finally { proxy.Fail = false; runtime.Dispose(); ClearMachinaNative(); WriteImplementation(originalImplementation); }
    }

    [Fact]
    public void ANewBuildWithoutItsOwnProfileBorrowsTheNewestVerifiedPatterns()
    {
        var nativeFixture = Path.Combine(AppContext.BaseDirectory, "e_sqlite3.dll");
        var image = File.ReadAllBytes(nativeFixture);
        var executable = _root.Combine("ffxiv_dx11.exe");
        File.WriteAllBytes(executable, image);
        // The donor is bound to another executable (hash of other bytes) on an older build,
        // and each of its patterns occurs exactly once in this image.
        WriteDonorProfile(image, "2026.08.05.0000.0000", OodleSignatureProfile.VerifiedStatus);
        var options = new CaptureStartOptions(
            "test", 1, null, null, OodleMode.FfxivTcp, null, executable, Region.Cn, "2026.09.01.0000.0000");

        using var runtime = OodleSignatureRuntime.TryCreate(options, _root.Path);

        Assert.NotNull(runtime);
        Assert.True(runtime!.IsPatternFallback);
        Assert.Equal(OodleSignatureRuntime.PatternFallbackSource, runtime.SourceToken);
        Assert.Equal("2026.08.05.0000.0000", runtime.Profile.GameBuild);
    }

    [Fact]
    public void ACandidateDonorOrAPatternThatDoesNotFitFallsBackToBuiltin()
    {
        var nativeFixture = Path.Combine(AppContext.BaseDirectory, "e_sqlite3.dll");
        var image = File.ReadAllBytes(nativeFixture);
        var executable = _root.Combine("ffxiv_dx11.exe");
        File.WriteAllBytes(executable, image);
        var options = new CaptureStartOptions(
            "test", 1, null, null, OodleMode.FfxivTcp, null, executable, Region.Cn, "2026.09.01.0000.0000");

        WriteDonorProfile(image, "2026.08.05.0000.0000", OodleSignatureProfile.CandidateStatus);
        Assert.Null(OodleSignatureRuntime.TryCreate(options, _root.Path));

        foreach (var file in Directory.EnumerateFiles(_root.Combine(OodleSignatureProfile.DirectoryName)))
        {
            File.Delete(file);
        }

        WriteDonorProfile(image, "2026.08.05.0000.0000", OodleSignatureProfile.VerifiedStatus, fit: false);
        Assert.Null(OodleSignatureRuntime.TryCreate(options, _root.Path));
    }

    /// <summary>
    /// 2026-11-20. A region with no profile of its own has no donor either. A round the player
    /// started (the only place a CANDIDATE may be used) searches the executable with every
    /// pattern this installation knows, another region's included; ordinary recording does not.
    /// </summary>
    [Fact]
    public void ARegionWithoutAnyProfileIsSearchedOnlyWhereACandidateIsAllowed()
    {
        var nativeFixture = Path.Combine(AppContext.BaseDirectory, "e_sqlite3.dll");
        var image = File.ReadAllBytes(nativeFixture);
        var executable = _root.Combine("ffxiv_dx11.exe");
        File.WriteAllBytes(executable, image);
        WriteDonorProfile(image, "2026.08.05.0000.0000", OodleSignatureProfile.VerifiedStatus);
        var recording = new CaptureStartOptions(
            "test", 1, null, null, OodleMode.FfxivTcp, null, executable, Region.Global, "2026.11.03.0000.0000");

        Assert.Null(OodleSignatureRuntime.TryCreate(recording, _root.Path));

        using var runtime = OodleSignatureRuntime.TryCreate(
            recording with { AllowCandidateOodleSignature = true }, _root.Path);

        Assert.NotNull(runtime);
        Assert.True(runtime!.IsPatternFallback);
        Assert.Equal("discovered.global.2026.11.03.0000.0000", runtime.Profile.Id);
        Assert.Equal(OodleSignatureProfile.CandidateStatus, runtime.Profile.Status);
        Assert.Equal(Enum.GetValues<SignatureType>().Length, runtime.Profile.Signatures.Count);
    }

    /// <summary>
    /// The excerpts of the executable's code are the last resort (owner, 2026-11-20): a search
    /// that settled all twelve writes none, one that could not writes them once, with the whole
    /// hash and the section table a maintainer needs beside them.
    /// </summary>
    [Fact]
    public void CodeExcerptsAreLoggedOnlyWhenTheSearchCouldNotSettleEverySignature()
    {
        var image = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "e_sqlite3.dll"));
        var executable = _root.Combine("ffxiv_dx11.exe");
        File.WriteAllBytes(executable, image);
        var options = new CaptureStartOptions(
            "test", 1, null, null, OodleMode.FfxivTcp, null, executable, Region.Global, "2026.11.03.0000.0000",
            AllowCandidateOodleSignature: true);
        using var logger = new MentorRecorder.Collector.Diagnostics.RotatingFileLogger(
            _root.Combine("logs"), new TestClock(DateTimeOffset.UnixEpoch.AddYears(56)));

        WriteDonorProfile(image, "2026.08.05.0000.0000", OodleSignatureProfile.VerifiedStatus);
        using (var found = OodleSignatureRuntime.TryCreate(options, _root.Path, logger))
        {
            Assert.NotNull(found);
        }

        var lines = File.ReadAllLines(logger.CurrentPath);
        Assert.Contains(lines, line => line.Contains("oodle_signature_discovery", StringComparison.Ordinal) &&
            line.Contains("\"complete\":true", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("oodle_signature_context", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("oodle_signature_sections", StringComparison.Ordinal));

        // No pattern of this installation fits this file: nothing to use, and the last resort.
        // The file has one place two bytes off the built-in TCP_Train pattern, so there is at
        // least one near miss to show (audit 2026-11-20, A2-4: the check used to pass with none).
        foreach (var file in Directory.EnumerateFiles(_root.Combine(OodleSignatureProfile.DirectoryName)))
        {
            File.Delete(file);
        }

        var synthetic = _root.Combine("synthetic", "ffxiv_dx11.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(synthetic)!);
        File.WriteAllBytes(synthetic, OodleSignatureDiscoveryTests.ImageWith((0x400, "41 83 fc 02 75 0a 48 8b 0f e8")));
        Assert.Null(OodleSignatureRuntime.TryCreate(options with { GameExecutablePath = synthetic }, _root.Path, logger));

        lines = File.ReadAllLines(logger.CurrentPath)[lines.Length..];
        var discovery = Assert.Single(lines, line => line.Contains("oodle_signature_discovery", StringComparison.Ordinal));
        Assert.Contains("\"complete\":false", discovery, StringComparison.Ordinal);
        Assert.Contains("OodleNetwork1TCP_Train", discovery, StringComparison.Ordinal);
        // The whole hash survives the logger's redaction of long hexadecimal runs.
        Assert.DoesNotContain("[hex]", discovery, StringComparison.Ordinal);
        Assert.Single(lines, line => line.Contains("oodle_signature_sections", StringComparison.Ordinal));
        var context = lines.Where(line => line.Contains("oodle_signature_context", StringComparison.Ordinal)).ToArray();
        Assert.Contains(context, line =>
            line.Contains("\"signature\":\"OodleNetwork1TCP_Train\"", StringComparison.Ordinal) &&
            line.Contains("\"kind\":\"candidate\"", StringComparison.Ordinal) &&
            line.Contains("\"file_offset\":\"000003E0\"", StringComparison.Ordinal));
        Assert.All(context, line =>
        {
            Assert.Contains("\"reason\":\"incomplete\"", line, StringComparison.Ordinal);
            Assert.DoesNotContain("[hex]", line, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Audit 2026-11-20, A1-2 / A2-4. A round that used a discovered set and read no IPC segment,
    /// but failed to decompress something, writes the excerpts when it stops - however many
    /// control segments (keep-alives) it framed. A round that read IPC writes none.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADiscoveredSetThatReadNoIpcLogsTheCodeAroundItsCallSitesWhenTheRoundStops(bool ipcRead)
    {
        using var logger = new MentorRecorder.Collector.Diagnostics.RotatingFileLogger(
            _root.Combine("logs"), new TestClock(DateTimeOffset.UnixEpoch.AddYears(56)));
        using var runtime = DiscoveredRuntime(logger);
        var source = new MachinaCaptureSource(_ => new MachinaCleanupTests.NativeMonitor());
        source.Start(new CaptureStartOptions("synthetic", 42, null, null, OodleMode.LibraryTcp, null, null),
            new SilentObserver());
        typeof(MachinaCaptureSource).GetField("_oodleSignatureRuntime", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(source, runtime);
        var onMessage = typeof(MachinaCaptureSource).GetMethod("OnMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var onTrace = typeof(MachinaCaptureSource).GetMethod("OnMonitorTrace", BindingFlags.Instance | BindingFlags.NonPublic)!;
        onMessage.Invoke(source, new object?[] { null, 0L, Segment(segmentType: 7, length: 16), MessageDirection.Inbound });
        if (ipcRead)
        {
            onMessage.Invoke(source, new object?[] { null, 0L, Segment(FfxivFraming.SegmentTypeIpc, 32), MessageDirection.Inbound });
        }

        onTrace.Invoke(source, new object[] { MachinaCaptureSource.DecodeFailureTraceMarkers[0] });
        var before = File.ReadAllLines(logger.CurrentPath).Length;

        source.Stop();

        var lines = File.ReadAllLines(logger.CurrentPath)[before..];
        var excerpts = lines.Where(line => line.Contains("oodle_signature_sections", StringComparison.Ordinal)).ToArray();
        if (ipcRead)
        {
            Assert.Empty(excerpts);
        }
        else
        {
            Assert.Contains("\"reason\":\"decode_failed\"", Assert.Single(excerpts), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Audit 2026-11-20, A2-1. While a discovered set is in force a marker naming the executable's
    /// hash lies in the data directory, and a clean stop removes it. One left behind means that
    /// set's round died: the next round logs the code around its call sites with reason
    /// <c>crashed</c>, uses the built-in table and says so, and keeps the marker.
    /// </summary>
    [Fact]
    public void ADiscoveredSetWhoseRoundDidNotStopCleanlyIsNeverUsedAgain()
    {
        var data = _root.Combine("data");
        using var logger = new MentorRecorder.Collector.Diagnostics.RotatingFileLogger(
            _root.Combine("logs"), new TestClock(DateTimeOffset.UnixEpoch.AddYears(56)));
        var options = DiscoveryOptions();
        var hash = GameExecutableHash.Compute(options.GameExecutablePath)!;
        var marker = OodleSignatureRuntime.CrashSentinelPath(data, hash)!;
        Assert.DoesNotContain(Path.GetFileName(options.GameExecutablePath!), marker, StringComparison.OrdinalIgnoreCase);
        var arm = typeof(OodleSignatureRuntime).GetMethod("ArmCrashSentinel", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // Written when the set is handed to Machina, removed by a clean stop of the source.
        using (var runtime = OodleSignatureRuntime.TryCreate(options, _root.Path, logger, dataDirectory: data))
        {
            Assert.True(runtime!.IsDiscovered);
            Assert.False(File.Exists(marker));
            arm.Invoke(runtime, null);
            Assert.Equal(hash, File.ReadAllText(marker).Trim());
            var source = new MachinaCaptureSource(_ => new MachinaCleanupTests.NativeMonitor());
            source.Start(new CaptureStartOptions("synthetic", 42, null, null, OodleMode.LibraryTcp, null, null),
                new SilentObserver());
            typeof(MachinaCaptureSource).GetField("_oodleSignatureRuntime", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(source, runtime);
            source.Stop();
            Assert.False(File.Exists(marker));
        }

        // A round that never stopped leaves it behind.
        using (var runtime = OodleSignatureRuntime.TryCreate(options, _root.Path, logger, dataDirectory: data))
        {
            arm.Invoke(runtime!, null);
        }

        var before = File.ReadAllLines(logger.CurrentPath).Length;
        Assert.Null(OodleSignatureRuntime.TryCreate(options, _root.Path, logger, dataDirectory: data));

        var lines = File.ReadAllLines(logger.CurrentPath)[before..];
        Assert.DoesNotContain(lines, line => line.Contains("oodle_signature_discovery", StringComparison.Ordinal));
        Assert.Contains("\"reason\":\"crashed\"",
            Assert.Single(lines, line => line.Contains("oodle_signature_sections", StringComparison.Ordinal)),
            StringComparison.Ordinal);
        Assert.Contains(lines, line => line.Contains("oodle_signature_source", StringComparison.Ordinal) &&
            line.Contains("\"source\":\"builtin\"", StringComparison.Ordinal) &&
            line.Contains("not searched again", StringComparison.Ordinal));
        Assert.True(File.Exists(marker));
        Assert.Null(OodleSignatureRuntime.TryCreate(options, _root.Path, logger, dataDirectory: data));
        Assert.True(File.Exists(marker));
    }

    /// <summary>
    /// Audit 2026-11-20, A2-1, through <see cref="OodleSignatureRuntime.Install"/> itself: the marker is
    /// on disk by the time Machina's native slot is first touched, and an install that is refused
    /// removes it again. Machina's slot holds a probe whose release fails, so the install stops there
    /// and never initialises the discovered set (a successful install needs a real client; not tested).
    /// </summary>
    [Fact]
    public void InstallingADiscoveredSetWritesItsMarkerFirstAndARefusedInstallRemovesIt()
    {
        var data = _root.Combine("data");
        var options = DiscoveryOptions();
        var marker = OodleSignatureRuntime.CrashSentinelPath(data, GameExecutableHash.Compute(options.GameExecutablePath)!)!;
        using var runtime = OodleSignatureRuntime.TryCreate(options, _root.Path, dataDirectory: data);
        Assert.True(runtime!.IsDiscovered);
        var native = DispatchProxy.Create<IOodleNative, FailingNative>();
        var probe = (FailingNative)native;
        probe.MarkerPath = marker;
        var originalImplementation = ReadImplementation();
        lock (FactoryLock) NativeField.SetValue(null, native);
        WriteImplementation(OodleImplementation.FfxivTcp);
        try
        {
            Assert.Throws<CollectorException>(runtime.Install);

            Assert.True(probe.Attempts > 0, "the install must have reached Machina's native slot");
            Assert.True(probe.MarkerPresentAtFirstAttempt, "the marker must be written before Machina gets the set");
            Assert.False(File.Exists(marker), "a refused install leaves no marker behind");
        }
        finally { probe.Fail = false; runtime.Dispose(); ClearMachinaNative(); WriteImplementation(originalImplementation); }
    }

    /// <summary>
    /// Audit 2026-11-20, A2-1. The live source keeps the marker in the Collector's data directory, the
    /// one beside its temp-copy manifest: a marker left there by a round that died makes the next
    /// round fall back to the built-in table without searching again.
    /// </summary>
    [Fact]
    public void TheCaptureSourceLooksForTheMarkerBesideItsTempManifest()
    {
        var data = _root.Combine("data");
        var options = DiscoveryOptions();
        var hash = GameExecutableHash.Compute(options.GameExecutablePath)!;
        var marker = OodleSignatureRuntime.CrashSentinelPath(data, hash)!;
        Directory.CreateDirectory(data);
        File.WriteAllText(marker, hash + Environment.NewLine);
        using var logger = new MentorRecorder.Collector.Diagnostics.RotatingFileLogger(
            _root.Combine("logs"), new TestClock(DateTimeOffset.UnixEpoch.AddYears(56)));
        // Should the marker be missed, the discovered set's install stops at this probe instead of
        // initialising anything, and the assertions below fail.
        var native = DispatchProxy.Create<IOodleNative, FailingNative>();
        var probe = (FailingNative)native;
        var originalImplementation = ReadImplementation();
        lock (FactoryLock) NativeField.SetValue(null, native);
        WriteImplementation(OodleImplementation.FfxivTcp);
        var source = new MachinaCaptureSource(
            _ => new MachinaCleanupTests.NativeMonitor(), connectionCounter: (_, _) => 0, logger: logger,
            oodleTempManifestPath: Path.Combine(data, "oodle-temp-copies.json"));
        try
        {
            source.Start(options, new SilentObserver());

            Assert.Equal(OodleSignatureUse.Builtin, source.SignatureUse);
            Assert.Contains(File.ReadAllLines(logger.CurrentPath), line =>
                line.Contains("oodle_signature_source", StringComparison.Ordinal) &&
                line.Contains("not searched again", StringComparison.Ordinal));
            source.Stop();
            Assert.True(File.Exists(marker));
        }
        finally
        {
            source.Dispose();
            probe.Fail = false;
            ClearMachinaNative();
            WriteImplementation(originalImplementation);
        }
    }

    /// <summary>A Global client with only a CN donor: the search finds all twelve in e_sqlite3.dll.</summary>
    private CaptureStartOptions DiscoveryOptions()
    {
        var image = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "e_sqlite3.dll"));
        var executable = _root.Combine("ffxiv_dx11.exe");
        File.WriteAllBytes(executable, image);
        WriteDonorProfile(image, "2026.08.05.0000.0000", OodleSignatureProfile.VerifiedStatus);
        return new CaptureStartOptions(
            "test", 1, null, null, OodleMode.FfxivTcp, null, executable, Region.Global, "2026.11.03.0000.0000",
            AllowCandidateOodleSignature: true);
    }

    private OodleSignatureRuntime DiscoveredRuntime(MentorRecorder.Collector.Diagnostics.RotatingFileLogger logger)
    {
        var runtime = OodleSignatureRuntime.TryCreate(DiscoveryOptions(), _root.Path, logger);
        Assert.NotNull(runtime);
        Assert.True(runtime!.IsDiscovered);
        return runtime;
    }

    /// <summary>A framed segment of <paramref name="length"/> bytes and the given type.</summary>
    private static byte[] Segment(ushort segmentType, int length)
    {
        var bytes = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12, 2), segmentType);
        return bytes;
    }

    private sealed class SilentObserver : ICaptureSourceObserver
    {
        public void OnMessage(DecodedMessage message) { }
        public void OnDecodeError() { }
        public void OnFault(string reason, Exception? error) { }
    }

    /// <summary>
    /// Writes a VERIFIED/CANDIDATE profile bound to *another* executable whose patterns are
    /// unique byte runs taken from <paramref name="image"/> (or runs that do not occur in it).
    /// </summary>
    private void WriteDonorProfile(byte[] image, string build, string status, bool fit = true)
    {
        var directory = _root.Combine(OodleSignatureProfile.DirectoryName);
        Directory.CreateDirectory(directory);
        var patterns = new Dictionary<string, object>();
        var sections = OodleSignatureRuntime.PeSections(image);
        var offset = 0x1000;
        foreach (var type in Enum.GetValues<SignatureType>())
        {
            // A pattern must end at a resolvable call operand: `ff 15` for the allocator
            // pair, `e8` for everything else.
            var indirect = type is SignatureType.OodleMalloc or SignatureType.OodleFree;
            string text;
            if (fit)
            {
                while (true)
                {
                    var ends = indirect
                        ? image[offset + 8] == 0xff && image[offset + 9] == 0x15
                        : image[offset + 8] == 0xe8;
                    if (ends)
                    {
                        var run = image.AsSpan(offset, indirect ? 10 : 9).ToArray();
                        text = string.Join(' ', run.Select(b => b.ToString("x2")));
                        OodleSignaturePattern.TryParse(text, out var pattern, out _);
                        // A call site both the donor check and the search accept (audit
                        // 2026-11-20, A2-1: the search takes only calls inside code).
                        var hits = OodleSignaturePattern.FindAll(pattern, image, limit: 2);
                        var target = hits.Count == 1
                            ? OodleSignaturePattern.ResolveRelativeTarget(image, hits[0], pattern.Length)
                            : null;
                        if (hits.Count == 1 && target is > 0 && target < image.Length &&
                            OodleSignatureDiscovery.IsCallSite(image, sections, hits[0], pattern))
                        {
                            offset += 64;
                            break;
                        }
                    }

                    offset += 1;
                }
            }
            else
            {
                text = indirect ? "de ad be ef de ad be ef ff 15" : "de ad be ef de ad be ef e8";
            }

            patterns[type.ToString()] = text;
        }

        var otherHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("other executable " + build)))
            .ToLowerInvariant();
        var document = new Dictionary<string, object?>
        {
            ["schema_version"] = OodleSignatureProfile.SupportedSchemaVersion,
            ["region"] = "CN",
            ["game_build"] = build,
            ["exe_sha256"] = otherHash,
            ["exe_size"] = image.LongLength + 1,
            ["generated_at_utc"] = "2026-09-05T00:00:00Z",
            ["source"] = OodleSignatureProfile.FinderSource,
            ["status"] = status,
            ["signatures"] = patterns,
            ["resolved_rvas"] = Enum.GetValues<SignatureType>().ToDictionary(
                type => type.ToString(), _ => (object)"00000010"),
            ["profile_sha256"] = new string('0', 64),
        };
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        document["profile_sha256"] = ProfileLoader.ComputeProfileHash(json);
        json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, "cn." + build + ".json"), json, new UTF8Encoding(false));
    }

    public void Dispose()
    {
        ClearMachinaNative();
        _root.Dispose();
    }

    private OodleSignatureProfile LoadProfileForBytes(byte[] executableBytes)
    {
        var executable = _root.Combine(Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(executable, executableBytes);
        var path = WriteProfileForExecutable(executable);
        return OodleSignatureProfile.TryLoad(path, out var reason)
               ?? throw new InvalidOperationException(reason);
    }

    private string WriteProfileForExecutable(string executable)
    {
        var directory = _root.Combine(OodleSignatureProfile.DirectoryName);
        Directory.CreateDirectory(directory);
        var bytes = File.ReadAllBytes(executable);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return OodleSignatureTestFiles.Write(
            directory, Guid.NewGuid().ToString("N"), Build, hash, bytes.LongLength);
    }

    private static byte[] BuildMalformedPe(string malformedCase)
    {
        var image = new byte[512];
        if (malformedCase == "missing_dos_header")
        {
            return image;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0, 2), 0x5a4d);
        if (malformedCase == "invalid_pe_offset")
        {
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3c, 4), 0x20);
            return image;
        }

        const int peOffset = 0x80;
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3c, 4), peOffset);
        if (malformedCase == "missing_pe_signature")
        {
            return image;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(peOffset, 4), 0x00004550);
        const int optionalHeader = peOffset + 4 + 20;
        if (malformedCase == "pe32_instead_of_pe32_plus")
        {
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalHeader, 2), 0x010b);
            return image;
        }

        if (malformedCase != "zero_image_size")
        {
            throw new ArgumentOutOfRangeException(nameof(malformedCase), malformedCase, null);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalHeader, 2), 0x020b);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(optionalHeader + 56, 4), 0);
        return image;
    }

    private static IOodleNative? ReadMachinaNative()
    {
        lock (FactoryLock)
        {
            return NativeField.GetValue(null) as IOodleNative;
        }
    }

    private static OodleImplementation ReadImplementation()
    {
        lock (FactoryLock)
        {
            return (OodleImplementation)(ImplementationField.GetValue(null)
                ?? throw new InvalidOperationException("Machina Oodle implementation is unavailable."));
        }
    }

    private static void WriteImplementation(OodleImplementation implementation)
    {
        lock (FactoryLock)
        {
            ImplementationField.SetValue(null, implementation);
        }
    }

    private static void ClearMachinaNative()
    {
        lock (FactoryLock)
        {
            var native = NativeField.GetValue(null) as IOodleNative;
            NativeField.SetValue(null, null);
            try
            {
                native?.UnInitialize();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // The static field is already clear. A partially initialized third-party
                // object must not prevent the test fixture from restoring isolation.
            }
        }
    }
}
