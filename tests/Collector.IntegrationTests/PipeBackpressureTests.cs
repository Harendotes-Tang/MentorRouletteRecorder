using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// What happens when the pipe runs out of instances, and what one connection may ask the
/// server to keep doing for it.
///
/// <c>PipeServer</c> promises that a single misbehaving client cannot take the Collector down.
/// A client holding every pipe instance idle makes each attempt to create the next instance
/// fail; those failures must not count towards the accept loop's give-up threshold, because a
/// full pipe is backpressure, not a fault (review finding H1).
///
/// The same problem one level up: one connection may hold one live-event subscription, not an
/// unbounded number of pumps each with its own channel and deep clone of every event published
/// (review finding M2).
/// </summary>
public sealed class PipeBackpressureTests
{
    [Fact]
    public async Task AFullPipeIsBackpressureRatherThanAFault()
    {
        await using var fixture = ServerFixture.Start();

        // Fill every instance and leave the clients idle: no request, no disconnect.
        var clients = new List<PipeClient>();
        try
        {
            for (var i = 0; i < PipeServer.MaxConcurrentConnections; i++)
            {
                clients.Add(await fixture.ConnectAsync());
            }

            // Long enough to run well past MaxConsecutiveAcceptFailures at the retry cadence,
            // which is where a loop that counted these failures would give up and rethrow.
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(fixture.Serving.IsFaulted, DescribeFault(fixture));
            Assert.False(fixture.Serving.IsCompleted);

            // One client leaves; the server must pick the freed instance up and serve again.
            await clients[0].DisposeAsync();
            clients.RemoveAt(0);

            var response = await fixture.CallAsync("GetVersion");
            Assert.True(response.Ok);
            Assert.False(fixture.Serving.IsCompleted);
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task OneConnectionMayHoldOneLiveSubscription()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        var first = await client.SendAsync("SubscribeLiveEvents", new JsonObject());
        Assert.True(first.Ok);

        var second = await client.SendAsync("SubscribeLiveEvents", new JsonObject());
        Assert.False(second.Ok);
        Assert.Equal(ErrorCodes.BadRequest, second.ErrorCode);

        // The connection is still usable: a refused subscription is one message failing, not
        // the connection failing.
        var version = await client.SendAsync("GetVersion");
        Assert.True(version.Ok);
    }

    private static string DescribeFault(ServerFixture fixture) =>
        fixture.Serving.Exception is null
            ? "the accept loop faulted"
            : "the accept loop faulted: " + fixture.Serving.Exception;
}
