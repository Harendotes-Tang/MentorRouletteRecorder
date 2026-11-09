using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using Microsoft.Win32.SafeHandles;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// The access control list on the Named Pipe, read back from a real pipe handle.
///
/// docs/privacy-boundary.md section 8 says the pipe's ACL is limited to the current user, and
/// section 9 invites the user to verify it. The pipe is created by the shipping code path, the
/// private <c>CreateStream</c> the accept loop calls, reached by reflection so the test cannot
/// assert against a second pipe of its own making.
///
/// The forbidden well-known identities are the ones that matter on Windows: Everyone and
/// Authenticated Users would expose the pipe to every logged-in account, NETWORK beyond the
/// machine, and ANONYMOUS to an unauthenticated caller.
/// </summary>
public sealed class LifecyclePipeSecurityTests : IDisposable
{
    private static readonly WellKnownSidType[] ForbiddenIdentities =
    {
        WellKnownSidType.WorldSid,
        WellKnownSidType.AuthenticatedUserSid,
        WellKnownSidType.NetworkSid,
        WellKnownSidType.AnonymousSid,
        WellKnownSidType.BuiltinUsersSid,
        WellKnownSidType.InteractiveSid,
    };

    private readonly string _directory;
    private readonly CollectorHost _host;

    public LifecyclePipeSecurityTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.PipeAcl", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _host = CollectorHost.Open(Path.Combine(_directory, "acl.db"), SystemClock.Instance);
    }

    [Fact]
    public async Task ThePipeGrantsExactlyOneIdentityAndItIsTheCurrentUser()
    {
        await using var pipe = CreateShippingPipe();
        var security = pipe.Stream.GetAccessControl();

        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();

        var rule = Assert.Single(rules);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);

        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(identity.User, rule.IdentityReference);
        Assert.Equal(PipeAccessRights.FullControl, rule.PipeAccessRights & PipeAccessRights.FullControl);
    }

    [Fact]
    public async Task ThePipeGrantsNothingToEveryoneOrToAnyOtherWellKnownGroup()
    {
        await using var pipe = CreateShippingPipe();
        var rules = pipe.Stream
            .GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();

        foreach (var wellKnown in ForbiddenIdentities)
        {
            var sid = new SecurityIdentifier(wellKnown, null);
            Assert.DoesNotContain(
                rules,
                rule => rule.IdentityReference is SecurityIdentifier granted && granted.Equals(sid));
        }
    }

    /// <summary>
    /// Audit 2026-11-03, G-1: an elevated Collector labels its pipe High, so the user's ordinary (Medium) processes cannot
    /// open it and borrow administrator rights for exports, backups and import previews. One that is not elevated is left
    /// exactly as before: no label at all. Run elevated, the same test checks the label itself.
    /// </summary>
    [Fact]
    public async Task ThePipeCarriesAHighIntegrityLabelOnlyWhenTheCollectorRunsElevated()
    {
        await using var pipe = CreateShippingPipe();
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        Assert.Equal(elevated ? "S:(ML;;NWNR;;;HI)" : string.Empty, LabelOf(pipe.Stream));
    }

    /// <summary>
    /// Audit 2026-11-03, G-1: the label is decided by the integrity level the Collector runs at - High for High and above,
    /// nothing below High, where a label could add nothing the user's own processes could not undo.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(0x1000, null)]
    [InlineData(0x2000, null)]
    [InlineData(0x2100, null)]
    [InlineData(0x3000, 0x3000)]
    [InlineData(0x4000, 0x3000)]
    public void ThePipeIsLabelledHighExactlyWhenTheProcessRunsAtHighIntegrityOrAbove(int? process, int? label)
    {
        var decide = LabelType().GetMethod("For", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.True(decide is not null, "PipeIntegrityLabel.For has been renamed; update this test rather than deleting it.");

        Assert.Equal(label, (int?)decide!.Invoke(null, new object?[] { process }));
    }

    /// <summary>
    /// Audit 2026-11-03, G-1: the path an elevated Collector takes, run end to end at the level a test process has. The
    /// instance is created shut and gets its label and its one grant together; afterwards it still grants the current user
    /// alone and admits a client at the label's level.
    /// </summary>
    [Fact]
    public async Task ALabelledPipeKeepsItsOneGrantAndAdmitsAClientAtTheLabelsLevel()
    {
        var create = typeof(PipeServer).GetMethod("CreatePipeInstance", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(create is not null, "PipeServer.CreatePipeInstance has been renamed; update this test rather than deleting it.");
        var name = "MentorRecorder.acltest." + Guid.NewGuid().ToString("N") + ".v1";
        using var identity = WindowsIdentity.GetCurrent();

        await using var stream = (NamedPipeServerStream)create!.Invoke(null, new object?[] { name, identity.User, 0x2000 })!;

        Assert.Equal("S:(ML;;NWNR;;;ME)", LabelOf(stream));
        var rule = Assert.Single(stream.GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>());
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(identity.User, rule.IdentityReference);
        Assert.Equal(PipeAccessRights.FullControl, rule.PipeAccessRights & PipeAccessRights.FullControl);

        var accepted = stream.WaitForConnectionAsync();
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5_000);
        await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(client.IsConnected);

        // The next instance, created the same way while the first is connected - what the accept loop does.
        await using var next = (NamedPipeServerStream)create.Invoke(null, new object?[] { name, identity.User, 0x2000 })!;
        Assert.Equal("S:(ML;;NWNR;;;ME)", LabelOf(next));
        var acceptedNext = next.WaitForConnectionAsync();
        await using var second = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await second.ConnectAsync(5_000);
        await acceptedNext.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(second.IsConnected);
    }

    /// <summary>
    /// Audit 2026-11-03, G-1: a later instance shares the security of the pipe's first one, so a labelled Collector does
    /// not serve an instance of a pipe that already stands without the label.
    /// </summary>
    [Fact]
    public async Task ALabelledInstanceIsNotServedOnAPipeThatStandsWithoutTheLabel()
    {
        var create = typeof(PipeServer).GetMethod("CreatePipeInstance", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(create is not null, "PipeServer.CreatePipeInstance has been renamed; update this test rather than deleting it.");
        var name = "MentorRecorder.acltest." + Guid.NewGuid().ToString("N") + ".v1";
        using var identity = WindowsIdentity.GetCurrent();
        await using var plain = (NamedPipeServerStream)create!.Invoke(null, new object?[] { name, identity.User, null })!;
        Assert.Equal(string.Empty, LabelOf(plain));

        var refused = Assert.Throws<TargetInvocationException>(() => create.Invoke(null, new object?[] { name, identity.User, 0x2000 }));
        Assert.IsType<IOException>(refused.InnerException);
    }

    [Fact]
    public async Task ThePreparedInstanceIsSecuredAndConnectableBeforeTheAcceptLoopStarts()
    {
        await using var server = new PipeServer(new MessageDispatcher(_host),
            "MentorRecorder.prepared." + Guid.NewGuid().ToString("N") + ".v1");
        var prepared = PrepareShippingPipe(server);
        var preparedHandle = prepared.SafePipeHandle;
        var rule = Assert.Single(prepared.GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>());
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(identity.User, rule.IdentityReference);
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        Assert.Equal(elevated ? "S:(ML;;NWNR;;;HI)" : string.Empty, LabelOf(prepared));

        // The startup announcement may now be written: even a client that opens the pipe
        // before RunAsync reaches WaitForConnection sees its complete security descriptor.
        await using var client = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5_000);
        Assert.True(client.IsConnected);
        using var stopping = new CancellationTokenSource();
        var serving = server.RunAsync(stopping.Token);
        stopping.Cancel();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(preparedHandle.IsClosed);
    }

    [Fact]
    public async Task StoppingBeforeTheAcceptLoopReleasesThePreparedInstance()
    {
        await using var server = new PipeServer(new MessageDispatcher(_host),
            "MentorRecorder.prepared-stop." + Guid.NewGuid().ToString("N") + ".v1");
        var prepared = PrepareShippingPipe(server);
        var preparedHandle = prepared.SafePipeHandle;
        await server.StopAsync();
        Assert.True(preparedHandle.IsClosed);
        await using var client = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await Assert.ThrowsAsync<TimeoutException>(() => client.ConnectAsync(100));
    }

    [Fact]
    public async Task DisposingBeforeTheAcceptLoopReleasesThePreparedInstance()
    {
        var server = new PipeServer(new MessageDispatcher(_host),
            "MentorRecorder.prepared-dispose." + Guid.NewGuid().ToString("N") + ".v1");
        var prepared = PrepareShippingPipe(server);
        var preparedHandle = prepared.SafePipeHandle;
        // This is also the ownership path when the process's startup console writer throws.
        await server.DisposeAsync();
        Assert.True(preparedHandle.IsClosed);
    }

    private static NamedPipeServerStream PrepareShippingPipe(PipeServer server)
    {
        var prepare = typeof(PipeServer).GetMethod("PrepareFirstInstance", BindingFlags.Instance | BindingFlags.NonPublic);
        var field = typeof(PipeServer).GetField("_preparedStream", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(prepare);
        Assert.NotNull(field);
        prepare.Invoke(server, Array.Empty<object>());
        return Assert.IsType<NamedPipeServerStream>(field.GetValue(server));
    }

    [Fact]
    public void ThePipeNameCarriesADigestOfTheUserSidRatherThanTheSidItself()
    {
        var sid = PipeNaming.CurrentUserSid();
        var name = PipeNaming.CurrentUserPipeName();

        // A raw SID in a globally visible object name would identify the account to anyone
        // who can enumerate the pipe namespace, which is everyone.
        Assert.DoesNotContain(sid, name, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(PipeNaming.Prefix, name, StringComparison.Ordinal);
        Assert.EndsWith(PipeNaming.Suffix, name, StringComparison.Ordinal);
        Assert.Equal(PipeNaming.ForSid(sid), name);

        // Same user, same name; a different user, a different name.
        Assert.NotEqual(name, PipeNaming.ForSid("S-1-5-21-0-0-0-1234"));
    }

    /// <summary>
    /// The pipe the shipping accept loop would create, built by the shipping code.
    ///
    /// <c>PipeServer.CreateStream</c> is private, and reflecting into it is the only way to hold
    /// the resulting handle, which is the only way to read the ACL Windows actually applied. A
    /// renamed member fails the assertion below rather than silently asserting nothing.
    /// </summary>
    private ShippingPipe CreateShippingPipe()
    {
        var server = new PipeServer(
            new MessageDispatcher(_host),
            "MentorRecorder.acltest." + Guid.NewGuid().ToString("N") + ".v1");

        try
        {
            var create = typeof(PipeServer).GetMethod(
                "CreateStream", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(
                create is not null,
                "PipeServer.CreateStream has been renamed; this test must be updated rather " +
                "than deleted, because it is the only check on the pipe's ACL.");

            var stream = (NamedPipeServerStream)create!.Invoke(server, Array.Empty<object>())!;
            return new ShippingPipe(server, stream);
        }
        catch
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private static Type LabelType() =>
        typeof(PipeServer).Assembly.GetType("MentorRecorder.Collector.Ipc.PipeIntegrityLabel")
        ?? throw new InvalidOperationException("PipeIntegrityLabel has been renamed; update this test rather than deleting it.");

    /// <summary>
    /// The mandatory label Windows keeps on the pipe, as SDDL (<c>S:(ML;;NWNR;;;HI)</c>), or the empty string when it has
    /// none. Read off the handle itself: <see cref="PipeSecurity"/> drops label entries, so it cannot answer this.
    /// </summary>
    private static string LabelOf(NamedPipeServerStream stream)
    {
        const int kernelObject = 6;
        const uint labelInformation = 0x10;
        var error = GetSecurityInfo(stream.SafePipeHandle, kernelObject, labelInformation,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor);
        Assert.Equal(0u, error);
        try
        {
            Assert.True(ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, 1, labelInformation, out var text, out _),
                "the label could not be converted to SDDL: " + Marshal.GetLastWin32Error());
            try
            {
                return Marshal.PtrToStringUni(text) ?? string.Empty;
            }
            finally
            {
                LocalFree(text);
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetSecurityInfo(
        SafePipeHandle handle, int objectType, uint securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(
        IntPtr descriptor, int revision, uint securityInfo, out IntPtr text, out int length);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>
    /// The server and the pipe it created, so both are released. The server owns a
    /// cancellation source even when its accept loop was never started.
    /// </summary>
    private sealed class ShippingPipe : IAsyncDisposable
    {
        private readonly PipeServer _server;

        public ShippingPipe(PipeServer server, NamedPipeServerStream stream)
        {
            _server = server;
            Stream = stream;
        }

        /// <summary>The pipe the shipping accept loop would have created.</summary>
        public NamedPipeServerStream Stream { get; }

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync().ConfigureAwait(false);
            await _server.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _host.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Windows can hold a WAL handle briefly; the temp cleaner will get it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}
