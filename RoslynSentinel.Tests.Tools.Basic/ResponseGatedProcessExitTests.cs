// ResponseGatedProcessExit -> StopServer exits only after its own JSON-RPC response has been written.
// The exit action is injected, so no test here ever calls Environment.Exit.

using System.Text.Json.Nodes;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("ResponseGatedProcessExit")] // sentinel:auto-category
public class ResponseGatedProcessExitTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NoExitObservation = TimeSpan.FromMilliseconds(400);

    private static JsonRpcResponse Response(RequestId id) => new() { Id = id, Result = new JsonObject() };

    private static JsonRpcError Error(RequestId id) => new() { Id = id, Error = new JsonRpcErrorDetail { Code = -32000, Message = "x" } };

    private static ResponseGatedProcessExit Create(out TaskCompletionSource exited, out List<string> events, TimeSpan? fallback = null)
    {
        var exitedSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<string>();
        exited = exitedSource;
        events = log;
        return new ResponseGatedProcessExit(
            exit: () =>
            {
                lock (log)
                {
                    log.Add("exit");
                }

                exitedSource.TrySetResult();
            },
            grace: TimeSpan.FromMilliseconds(20),
            fallback: fallback ?? TimeSpan.FromMinutes(5));
    }

    private static Func<Task> Send(List<string> events) => async () =>
    {
        await Task.Delay(30);
        lock (events)
        {
            events.Add("sent");
        }
    };

    [Test]
    public async Task ResponseWithMatchingId_SendsFirstThenExits()
    {
        var gate = Create(out var exited, out var events);
        gate.ExpectResponseTo(new RequestId("stop-1"));

        await gate.ObserveOutgoingAsync(Response(new RequestId("stop-1")), Send(events));
        await exited.Task.WaitAsync(Wait);

        Assert.That(events, Is.EqualTo(new[] { "sent", "exit" }), "the exit must start only after the send completed");
    }

    [Test]
    public async Task ErrorWithMatchingId_AlsoTriggersExit()
    {
        var gate = Create(out var exited, out var events);
        gate.ExpectResponseTo(new RequestId(42));

        await gate.ObserveOutgoingAsync(Error(new RequestId(42)), Send(events));
        await exited.Task.WaitAsync(Wait);

        Assert.That(events, Is.EqualTo(new[] { "sent", "exit" }));
    }

    [Test]
    public async Task ResponseWithOtherId_DoesNotExit_AndStillSends()
    {
        var gate = Create(out var exited, out var events);
        gate.ExpectResponseTo(new RequestId("stop-1"));

        await gate.ObserveOutgoingAsync(Response(new RequestId("other")), Send(events));
        await Task.Delay(NoExitObservation);

        Assert.That(exited.Task.IsCompleted, Is.False);
        Assert.That(events, Is.EqualTo(new[] { "sent" }));
        Assert.That(gate.IsPending, Is.True, "the expected response has not been seen yet");
    }

    [Test]
    public async Task NotificationAndRequest_AreNeverTheAnswer()
    {
        var gate = Create(out var exited, out var events);
        gate.ExpectResponseTo(new RequestId("stop-1"));

        await gate.ObserveOutgoingAsync(new JsonRpcNotification { Method = "notifications/message" }, Send(events));
        await gate.ObserveOutgoingAsync(new JsonRpcRequest { Id = new RequestId("stop-1"), Method = "ping" }, Send(events));
        await Task.Delay(NoExitObservation);

        Assert.That(exited.Task.IsCompleted, Is.False);
        Assert.That(events, Is.EqualTo(new[] { "sent", "sent" }));
    }

    [Test]
    public async Task NothingRegistered_PassesThroughAndNeverExits()
    {
        var gate = Create(out var exited, out var events);

        await gate.ObserveOutgoingAsync(Response(new RequestId("stop-1")), Send(events));
        await Task.Delay(NoExitObservation);

        Assert.That(gate.IsPending, Is.False);
        Assert.That(exited.Task.IsCompleted, Is.False);
        Assert.That(events, Is.EqualTo(new[] { "sent" }));
    }

    [Test]
    public async Task NoResponseSeen_FallbackTimerExits()
    {
        var gate = Create(out var exited, out var events, fallback: TimeSpan.FromMilliseconds(80));

        gate.ExpectResponseTo(new RequestId("stop-1"));
        await exited.Task.WaitAsync(Wait);

        Assert.That(events, Is.EqualTo(new[] { "exit" }));
    }

    [Test]
    public async Task SendThrows_StillExits_AndRethrows()
    {
        var gate = Create(out var exited, out _);
        gate.ExpectResponseTo(new RequestId("stop-1"));

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.ObserveOutgoingAsync(Response(new RequestId("stop-1")), () => throw new InvalidOperationException("transport closed")));
        await exited.Task.WaitAsync(Wait);

        Assert.That(exited.Task.IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public async Task ResponseThenFallback_ExitsExactlyOnce()
    {
        var gate = Create(out var exited, out var events, fallback: TimeSpan.FromMilliseconds(150));
        gate.ExpectResponseTo(new RequestId("stop-1"));

        await gate.ObserveOutgoingAsync(Response(new RequestId("stop-1")), Send(events));
        await exited.Task.WaitAsync(Wait);
        await Task.Delay(400); // past the fallback deadline

        Assert.That(events.Count(e => e == "exit"), Is.EqualTo(1));
    }
}
