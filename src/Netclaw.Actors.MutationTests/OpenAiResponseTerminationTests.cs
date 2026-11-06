// -----------------------------------------------------------------------
// <copyright file="OpenAiResponseTerminationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Daemon.Tests.Providers;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class OpenAiResponseTerminationTests
{
    [Fact]
    public async Task A_completed_event_releases_the_response_before_eof()
    {
        using var stream = new ChannelSseStream();
        stream.WriteEvents([OpenAiResponseSse.Call()]);
        using var handler = new StreamHandler(stream);
        using var client = OpenAiResponseSse.Client(handler, AuthMethod.OAuthDevice);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var response = await client.GetStreamingResponseAsync([new(ChatRole.User, "Synthetic request")],
            cancellationToken: cancel.Token).ToChatResponseAsync(cancel.Token);

        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        Assert.Single(Assert.Single(response.Messages).Contents.OfType<FunctionCallContent>());
        Assert.True(stream.WasDisposed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("response.failed")]
    [InlineData("response.incomplete")]
    public async Task An_unsuccessful_stream_cannot_return_its_tool_call(string terminal)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(OpenAiResponseSse.Call(terminal)));
        using var handler = new StreamHandler(stream);
        using var client = OpenAiResponseSse.Client(handler, AuthMethod.OAuthDevice);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<ProviderException>(async () =>
            await client.GetStreamingResponseAsync([new(ChatRole.User, "Synthetic request")],
                cancellationToken: cancel.Token).ToChatResponseAsync(cancel.Token));
    }

    private sealed class StreamHandler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OpenAiResponseSse.Response(stream));
        }
    }
}
