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

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.OodleRuntime", Guid.NewGuid().ToString("N"));

    public OodleSignatureRuntimeTests() => Directory.CreateDirectory(_root);

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
        var executable = Path.Combine(_root, "ffxiv_dx11.exe");
        File.WriteAllBytes(executable, image);
        WriteProfileForExecutable(executable);
        var options = new CaptureStartOptions(
            "test", 1, null, null, OodleMode.FfxivTcp, null, executable, Region.Cn, Build,
            AllowCandidateOodleSignature: true);
        using var runtime = OodleSignatureRuntime.TryCreate(options, _root);
        Assert.NotNull(runtime);

        var originalImplementation = ReadImplementation();
        ClearMachinaNative();
        try
        {
            var error = Assert.Throws<CollectorException>(() => runtime!.Install());

            Assert.Equal(ErrorCodes.Internal, error.Code);
            Assert.Contains("抓包未开始", error.Message, StringComparison.Ordinal);
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
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "UnInitialize")
            {
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
        var executable = Path.Combine(_root, "ffxiv_dx11.exe");
        File.WriteAllBytes(executable, image);
        // The donor is bound to another executable (hash of other bytes) on an older build,
        // and each of its patterns occurs exactly once in this image.
        WriteDonorProfile(image, "2026.08.05.0000.0000", OodleSignatureProfile.VerifiedStatus);
        var options = new CaptureStartOptions(
            "test", 1, null, null, OodleMode.FfxivTcp, null, executable, Region.Cn, "2026.09.01.0000.0000");

        using var runtime = OodleSignatureRuntime.TryCreate(options, _root);

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
        var executable = Path.Combine(_root, "ffxiv_dx11.exe");
        File.WriteAllBytes(executable, image);
        var options = new CaptureStartOptions(
            "test", 1, null, null, OodleMode.FfxivTcp, null, executable, Region.Cn, "2026.09.01.0000.0000");

        WriteDonorProfile(image, "2026.08.05.0000.0000", OodleSignatureProfile.CandidateStatus);
        Assert.Null(OodleSignatureRuntime.TryCreate(options, _root));

        foreach (var file in Directory.EnumerateFiles(Path.Combine(_root, OodleSignatureProfile.DirectoryName)))
        {
            File.Delete(file);
        }

        WriteDonorProfile(image, "2026.08.05.0000.0000", OodleSignatureProfile.VerifiedStatus, fit: false);
        Assert.Null(OodleSignatureRuntime.TryCreate(options, _root));
    }

    /// <summary>
    /// Writes a VERIFIED/CANDIDATE profile bound to *another* executable whose patterns are
    /// unique byte runs taken from <paramref name="image"/> (or runs that do not occur in it).
    /// </summary>
    private void WriteDonorProfile(byte[] image, string build, string status, bool fit = true)
    {
        var directory = Path.Combine(_root, OodleSignatureProfile.DirectoryName);
        Directory.CreateDirectory(directory);
        var patterns = new Dictionary<string, object>();
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
                        var hits = OodleSignaturePattern.FindAll(pattern, image, limit: 2);
                        var target = hits.Count == 1
                            ? OodleSignaturePattern.ResolveRelativeTarget(image, hits[0], pattern.Length)
                            : null;
                        if (hits.Count == 1 && target is > 0 && target < image.Length)
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
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private OodleSignatureProfile LoadProfileForBytes(byte[] executableBytes)
    {
        var executable = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(executable, executableBytes);
        var path = WriteProfileForExecutable(executable);
        return OodleSignatureProfile.TryLoad(path, out var reason)
               ?? throw new InvalidOperationException(reason);
    }

    private string WriteProfileForExecutable(string executable)
    {
        var directory = Path.Combine(_root, OodleSignatureProfile.DirectoryName);
        Directory.CreateDirectory(directory);
        var bytes = File.ReadAllBytes(executable);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var document = new Dictionary<string, object?>
        {
            ["schema_version"] = OodleSignatureProfile.SupportedSchemaVersion,
            ["region"] = "CN",
            ["game_build"] = Build,
            ["exe_sha256"] = hash,
            ["exe_size"] = bytes.LongLength,
            ["generated_at_utc"] = "2026-09-05T00:00:00Z",
            ["source"] = OodleSignatureProfile.FinderSource,
            ["status"] = OodleSignatureProfile.CandidateStatus,
            ["signatures"] = Enum.GetValues<SignatureType>().ToDictionary(
                type => type.ToString(),
                type => (object)(type is SignatureType.OodleMalloc or SignatureType.OodleFree
                    ? "48 8b ff 15"
                    : "48 8b 00 e8")),
            ["resolved_rvas"] = Enum.GetValues<SignatureType>().ToDictionary(
                type => type.ToString(),
                _ => (object)"00000010"),
            ["profile_sha256"] = new string('0', 64),
        };
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        document["profile_sha256"] = ProfileLoader.ComputeProfileHash(json);
        json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
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
