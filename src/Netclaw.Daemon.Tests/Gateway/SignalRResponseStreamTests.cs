// -----------------------------------------------------------------------
// <copyright file="SignalRResponseStreamTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Threading.Channels;
using Akka;
using Akka.Actor;
using Akka.Persistence;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Tests.Providers;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Tests.Gateway;

public sealed partial class SignalRAdmissionTests
{
    [Fact]
    public async Task A_completed_empty_call_is_rejected_then_a_valid_call_finishes_the_original_admitted_turn()
    {
        var ct = TestContext.Current.CancellationToken;
        var requests = new List<string>();
        using var handler = new FakeHttpMessageHandler(request =>
        {
            requests.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var payload = requests.Count switch
            {
                1 => OpenAiResponseSse.Call(arguments: "").Replace("call-synthetic", "call-bad", StringComparison.Ordinal),
                2 => OpenAiResponseSse.Call(arguments: """{"Name":"load_tool","_rationale":"Inspect synthetic data"}""")
                    .Replace("call-synthetic", "call-good", StringComparison.Ordinal),
                3 => OpenAiResponseSse.Text(),
                _ => throw new InvalidOperationException("The model repeated a synthetic request.")
            };
            return OpenAiResponseSse.Response(new MemoryStream(Encoding.UTF8.GetBytes(payload)));
        });
        using var provider = OpenAiResponseSse.Client(handler, AuthMethod.OAuthDevice);
        _model.StreamingOverride = provider;
        await using var client = await ConnectAsync(useSocket: true);
        var outputs = Channel.CreateUnbounded<SessionOutputDto>();
        using var subscription = client.On<SessionOutputDto>("ReceiveOutput", output => outputs.Writer.TryWrite(output));
        var ensured = await client.InvokeAsync<SessionEnsureResultDto>("EnsureSession", null, "tui", ct);
        var writes = CreateTestProbe();
        await Persistence.Instance.Apply(Sys).JournalFor("test-journal").Ask<Done>(
            new WriteGate(writes, Task.CompletedTask, false), TimeSpan.FromSeconds(10), ct);

        await client.InvokeAsync("SendMessage", ensured.SessionId, "One original synthetic request", ct);
        var admitted = await writes.FishForMessageAsync<InputAdmitted>(input => input.UserMessage.Content == "One original synthetic request",
            TimeSpan.FromSeconds(10), cancellationToken: ct);
        var batch = await writes.FishForMessageAsync<ToolBatchStarted>(value => value.ConsumedInputIds.Contains(admitted.InputId),
            TimeSpan.FromSeconds(10), cancellationToken: ct);
        Assert.Equal(admitted.InputId, Assert.Single(batch.ConsumedInputIds));
        var received = new List<SessionOutputDto>();
        do
        {
            received.Add(await outputs.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        } while (received[^1].Type != SessionOutputTypes.TurnCompleted);

        var results = received.Where(output => output.Type == SessionOutputTypes.ToolResult).ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal("call-bad", results[0].CallId);
        Assert.Contains("_rationale", results[0].Result);
        Assert.Equal("invalid_rationale", results[0].ToolFailureCode);
        Assert.Equal("call-good", results[1].CallId);
        Assert.Equal("load_tool", results[1].Result);
        Assert.Null(results[1].ToolFailureCode);
        Assert.Equal("completed", received[^1].TurnOutcome);
        Assert.Equal(new TurnNumber(1), received[^1].TurnNumber);
        Assert.All(received, output => Assert.Equal(ensured.SessionId, output.SessionId));
        Assert.Equal(3, requests.Count);
        Assert.Contains("call-bad", requests[1]);
        Assert.Contains("call-bad", requests[2]);
        Assert.Contains("call-good", requests[2]);
        Assert.Contains("_rationale", requests[1]);
        var recorded = await writes.FishForMessageAsync<TurnRecorded>(turn => turn.AssistantReply.Content == "Synthetic final answer",
            TimeSpan.FromSeconds(10), cancellationToken: ct);
        Assert.Equal(admitted.UserMessage.Content, recorded.UserMessage.Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Responses_failure_or_watchdog_reaches_SignalR_without_tool_dispatch_or_input_replay(bool timeout)
    {
        var ct = TestContext.Current.CancellationToken;
        using var held = new ChannelSseStream();
        held.WriteEvents(OpenAiResponseSse.Call().Split("\n\n", StringSplitOptions.RemoveEmptyEntries)[..3]);
        var requests = 0;
        using var handler = new FakeHttpMessageHandler(_ => ++requests == 1
            ? OpenAiResponseSse.Response(timeout ? held : new MemoryStream(Encoding.UTF8.GetBytes(OpenAiResponseSse.Call("response.incomplete"))))
            : OpenAiResponseSse.Response(new MemoryStream(Encoding.UTF8.GetBytes(OpenAiResponseSse.Text()))));
        using var provider = new RetryingChatClient(OpenAiResponseSse.Client(handler, AuthMethod.OAuthDevice),
            new RetryPolicy(), NullLogger.Instance);
        _model.StreamingOverride = provider;
        await using var client = await ConnectAsync(useSocket: true);
        var outputs = Channel.CreateUnbounded<SessionOutputDto>();
        using var subscription = client.On<SessionOutputDto>("ReceiveOutput", output => outputs.Writer.TryWrite(output));
        var ensured = await client.InvokeAsync<SessionEnsureResultDto>("EnsureSession", null, "tui", ct);
        await client.InvokeAsync("SendMessage", ensured.SessionId, "Original synthetic request", ct);
        if (timeout)
        {
            await held.WaitForReadAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            ((Akka.TestKit.TestScheduler)Sys.Scheduler).Advance(TimeSpan.FromSeconds(1200));
        }
        var first = await ReadTurnAsync();
        Assert.Contains(first, output => output.Type == SessionOutputTypes.Error
            && output.ErrorCategory == (timeout ? "Timeout" : "ProviderFailure"));
        Assert.DoesNotContain(first, output => output.Type == SessionOutputTypes.ToolCall || output.Type == SessionOutputTypes.ToolResult);
        Assert.Equal("failed", first[^1].TurnOutcome);
        Assert.Equal(new TurnNumber(1), first[^1].TurnNumber);
        Assert.Equal(1, requests);

        await client.InvokeAsync("SendMessage", ensured.SessionId, "New synthetic request", ct);
        var second = await ReadTurnAsync();
        Assert.Contains(second, output => output.Type == SessionOutputTypes.Text && output.Text == "Synthetic final answer");
        Assert.Equal("completed", second[^1].TurnOutcome);
        Assert.Equal(new TurnNumber(2), second[^1].TurnNumber);
        Assert.Equal(2, requests);
        Assert.All(first.Concat(second), output => Assert.Equal(ensured.SessionId, output.SessionId));

        async Task<List<SessionOutputDto>> ReadTurnAsync()
        {
            var result = new List<SessionOutputDto>();
            while (true)
            {
                var output = await outputs.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
                result.Add(output);
                if (output.Type == SessionOutputTypes.TurnCompleted) return result;
            }
        }
    }

}
