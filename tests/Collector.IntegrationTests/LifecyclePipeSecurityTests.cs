using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;

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
