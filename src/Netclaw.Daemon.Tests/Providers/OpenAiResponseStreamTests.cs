// -----------------------------------------------------------------------
// <copyright file="OpenAiResponseStreamTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Providers.OpenAi;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class OpenAiResponseStreamTests
{
    [Theory]
    [InlineData(AuthMethod.ApiKey)]
    [InlineData(AuthMethod.OAuthDevice)]
    public async Task Completed_response_finishes_without_transport_eof(AuthMethod auth)
    {
        using var stream = new HeldTailStream(OpenAiResponseSse.Call());
        using var handler = new FakeHttpMessageHandler(_ => OpenAiResponseSse.Response(stream));
        using var client = OpenAiResponseSse.Client(handler, auth);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var response = await client.GetStreamingResponseAsync([new(ChatRole.User, "Synthetic read-only request")],
                cancellationToken: cancel.Token).ToChatResponseAsync(cancel.Token)
                .WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);

            var call = Assert.Single(Assert.Single(response.Messages).Contents.OfType<FunctionCallContent>());
            Assert.Equal("call-synthetic", call.CallId);
            Assert.Equal("Inspect synthetic data", call.Arguments!["_rationale"]!.ToString());
            Assert.True(stream.WasDisposed);
        }
        finally
        {
            cancel.Cancel();
        }
    }

    [Theory]
    [InlineData("response.failed")]
    [InlineData("response.incomplete")]
    [InlineData("")]
    public async Task Unsuccessful_or_absent_terminal_event_cannot_return_a_tool_call(string terminal)
    {
        using var handler = new FakeHttpMessageHandler(_ => OpenAiResponseSse.Response(
            new MemoryStream(Encoding.UTF8.GetBytes(OpenAiResponseSse.Call(terminal)))));
        using var client = OpenAiResponseSse.Client(handler, AuthMethod.OAuthDevice);

        await Assert.ThrowsAsync<ProviderException>(async () =>
            await client.GetStreamingResponseAsync([new(ChatRole.User, "Synthetic read-only request")],
                cancellationToken: TestContext.Current.CancellationToken)
                .ToChatResponseAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("response.failed", true)]
    [InlineData("response.incomplete", true)]
    [InlineData("", true)]
    [InlineData("", false)]
    public async Task Terminal_failure_is_not_retried_and_a_later_request_keeps_prior_results(string terminal, bool includeCall)
    {
        var requests = new List<string>();
        using var handler = new FakeHttpMessageHandler(request =>
        {
            requests.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var payload = requests.Count == 1
                ? includeCall ? OpenAiResponseSse.Call(terminal) : OpenAiResponseSse.Call("").Split("\n\n")[0] + "\n\n"
                : OpenAiResponseSse.Call();
            return OpenAiResponseSse.Response(new MemoryStream(Encoding.UTF8.GetBytes(payload)));
        });
        using var client = new RetryingChatClient(OpenAiResponseSse.Client(handler, AuthMethod.OAuthDevice),
            new RetryPolicy { MaxRetries = 3, BaseDelay = TimeSpan.Zero }, NullLogger.Instance);
        ChatMessage[] messages =
        [
            new(ChatRole.User, "Original turn"),
            new(ChatRole.Assistant, [new FunctionCallContent("prior-call", "prior_action", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent("prior-call", "Prior action completed once")])
        ];
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ProviderException>(async () =>
            await client.GetStreamingResponseAsync(messages, cancellationToken: ct).ToChatResponseAsync(ct));
        Assert.Single(requests);

        var response = await client.GetStreamingResponseAsync(messages, cancellationToken: ct).ToChatResponseAsync(ct);
        Assert.Single(Assert.Single(response.Messages).Contents.OfType<FunctionCallContent>());
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0], requests[1]);
        Assert.Contains("Prior action completed once", requests[1]);
        Assert.Contains("prior-call", requests[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"Name\":")]
    public async Task Completed_malformed_calls_retain_the_rationale_guard_and_do_not_poison_the_next_call(string arguments)
    {
        var count = 0;
        using var handler = new FakeHttpMessageHandler(_ => OpenAiResponseSse.Response(new MemoryStream(Encoding.UTF8.GetBytes(
            ++count == 1 ? OpenAiResponseSse.Call(arguments: arguments) : OpenAiResponseSse.Call()))));
        using var client = OpenAiResponseSse.Client(handler, AuthMethod.OAuthDevice);
        var ct = TestContext.Current.CancellationToken;

        var bad = await client.GetStreamingResponseAsync([new(ChatRole.User, "Synthetic request")], cancellationToken: ct).ToChatResponseAsync(ct);
        var badCall = Assert.Single(Assert.Single(bad.Messages).Contents.OfType<FunctionCallContent>());
        Assert.NotNull(ToolCallMetaExtractor.ValidateRequiredRationale(badCall.Arguments, static key => key));

        var good = await client.GetStreamingResponseAsync([new(ChatRole.User, "Synthetic request")], cancellationToken: ct).ToChatResponseAsync(ct);
        var goodCall = Assert.Single(Assert.Single(good.Messages).Contents.OfType<FunctionCallContent>());
        Assert.Null(ToolCallMetaExtractor.ValidateRequiredRationale(goodCall.Arguments, static key => key));
        Assert.Equal("synthetic", goodCall.Arguments!["Name"]!.ToString());
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Slow_fragmented_arguments_keep_their_values_and_diagnostics_exclude_payloads()
    {
        var capture = new CapturingLoggerProvider();
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(capture));
        var time = new FakeTimeProvider();
        using var stream = new ChannelSseStream();
        var events = OpenAiResponseSse.Call().Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        stream.WriteEvents(events[..3]);
        using var handler = new FakeHttpMessageHandler(_ => OpenAiResponseSse.Response(stream));
        using var client = PipelineChatClientFactory.Compose(
            OpenAiResponseSse.Client(handler, AuthMethod.OAuthDevice, logs, time), new RetryPolicy(), logs, time);
        var ct = TestContext.Current.CancellationToken;
        var responseTask = client.GetStreamingResponseAsync([new(ChatRole.User, "Private prompt sentinel")],
            new SessionScopedChatOptions { SessionId = "synthetic-session" }, ct).ToChatResponseAsync(ct);
        await stream.WaitForReadAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.False(responseTask.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(643));
        stream.WriteEvents(events[3..]);
        stream.Complete();
        var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5), ct);

        var call = Assert.Single(Assert.Single(response.Messages).Contents.OfType<FunctionCallContent>());
        Assert.Equal("Inspect synthetic data", call.Arguments!["_rationale"]!.ToString());
        var summary = Assert.Single(capture.Records, entry => entry.Message.StartsWith("response_stream_end"));
        Assert.Contains(summary.Values, value => value.Key == "ElapsedMs" && (double)value.Value! == 643_000);
        Assert.Contains(summary.Values, value => value.Key == "ArgumentDeltaChars" && (int)value.Value! > 0);
        Assert.Contains(summary.Scopes.OfType<IEnumerable<KeyValuePair<string, object>>>().SelectMany(scope => scope),
            value => value.Key == "SessionId" && value.Value.ToString() == "synthetic-session");
        var allLogs = string.Join("\n", capture.Entries.Select(entry => entry.Message));
        Assert.DoesNotContain("Private prompt sentinel", allLogs);
        Assert.DoesNotContain("Inspect synthetic data", allLogs);
        Assert.DoesNotContain("synthetic-token", allLogs);
        Assert.DoesNotContain("call-synthetic", allLogs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Error_events_and_inconsistent_terminal_status_fail_without_payload_disclosure(bool errorEvent)
    {
        var capture = new CapturingLoggerProvider();
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(capture));
        var payload = errorEvent
            ? OpenAiResponseSse.Call("") + "event: error\ndata: {\"type\":\"error\",\"code\":\"server_error\",\"message\":\"Private provider error sentinel\"}\n\n"
            : OpenAiResponseSse.Call().Replace("\"status\":\"completed\"", "\"status\":\"in_progress\"", StringComparison.Ordinal);
        using var handler = new FakeHttpMessageHandler(_ => OpenAiResponseSse.Response(new MemoryStream(Encoding.UTF8.GetBytes(payload))));
        using var client = OpenAiResponseSse.Client(handler, AuthMethod.OAuthDevice, logs, TimeProvider.System);
        var ct = TestContext.Current.CancellationToken;

        var failure = await Assert.ThrowsAsync<ProviderException>(async () =>
            await client.GetStreamingResponseAsync([new(ChatRole.User, "Private prompt sentinel")],
                cancellationToken: ct).ToChatResponseAsync(ct));

        Assert.DoesNotContain("Private provider error sentinel", failure.ToString());
        Assert.DoesNotContain("Private provider error sentinel", string.Join("\n", capture.Entries.Select(entry => entry.Message)));
        Assert.DoesNotContain("Private prompt sentinel", string.Join("\n", capture.Entries.Select(entry => entry.Message)));
    }

    [Fact]
    public async Task Cancellation_stops_a_partial_stream_without_retry_or_success()
    {
        using var stream = new ChannelSseStream();
        stream.WriteEvents(OpenAiResponseSse.Call().Split("\n\n", StringSplitOptions.RemoveEmptyEntries)[..3]);
        var count = 0;
        using var handler = new FakeHttpMessageHandler(_ => { count++; return OpenAiResponseSse.Response(stream); });
        using var client = new RetryingChatClient(OpenAiResponseSse.Client(handler, AuthMethod.OAuthDevice),
            new RetryPolicy(), NullLogger.Instance);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var response = client.GetStreamingResponseAsync([new(ChatRole.User, "Synthetic request")],
            cancellationToken: cancel.Token).ToChatResponseAsync(cancel.Token);
        await stream.WaitForReadAsync(cancel.Token).WaitAsync(TimeSpan.FromSeconds(5), cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await response);
        Assert.Equal(1, count);
        Assert.True(stream.WasDisposed);
    }

    private sealed class HeldTailStream(string payload) : MemoryStream(Encoding.UTF8.GetBytes(payload))
    {
        private readonly TaskCompletionSource _eof = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasDisposed { get; private set; }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = base.Read(buffer.Span);
            if (read != 0)
                return read;

            // A terminal event must release the response without another transport read.
            await _eof.Task.WaitAsync(cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
