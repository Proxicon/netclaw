// -----------------------------------------------------------------------
// <copyright file="TeamsSdkReplyClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Netclaw.Channels.Teams;
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class TeamsSdkReplyClientTests
{
    [Fact]
    public async Task Reply_client_maps_create_and_update_success_without_exposing_sdk_data()
    {
        var operations = new FakeOperations("created", "updated");
        var client = new TeamsSdkReplyClient(operations);

        var created = await client.DeliverAsync(CreateMessage(), TestContext.Current.CancellationToken);
        var updated = await client.DeliverAsync(CreateMessage(updateActivityId: "processing"), TestContext.Current.CancellationToken);

        Assert.Equal(TeamsDeliveryStatus.Delivered, created.Status);
        Assert.Equal("created", created.ActivityId);
        Assert.Equal(TeamsDeliveryStatus.Updated, updated.Status);
        Assert.Equal("updated", updated.ActivityId);
        Assert.Equal(2, operations.Requests.Count);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("unauthorized")]
    public async Task Reply_client_maps_transport_failures_to_safe_unavailable_results(string failure)
    {
        var client = new TeamsSdkReplyClient(new FakeOperations(failure));

        var result = await client.DeliverAsync(CreateMessage(), TestContext.Current.CancellationToken);

        Assert.Equal(TeamsDeliveryStatus.Unavailable, result.Status);
        Assert.DoesNotContain("secret", result.ReasonCode, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", result.ReasonCode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reply_client_maps_message_size_failures_without_the_response_body()
    {
        var client = new TeamsSdkReplyClient(new FakeOperations("too-large"));

        var result = await client.DeliverAsync(CreateMessage(), TestContext.Current.CancellationToken);

        Assert.Equal(TeamsDeliveryStatus.RejectedTooLarge, result.Status);
        Assert.Equal("output_too_large", result.ReasonCode);
    }

    [Fact]
    public async Task Reply_client_maps_a_missing_destination_to_a_permanent_safe_result()
    {
        var result = await new TeamsSdkReplyClient(new FakeOperations("missing-destination"))
            .DeliverAsync(CreateMessage(), TestContext.Current.CancellationToken);

        Assert.Equal(TeamsDeliveryStatus.InvalidDestination, result.Status);
        Assert.Equal("sdk_destination_invalid", result.ReasonCode);
    }

    [Fact]
    public async Task Reply_client_maps_cancellation_and_sanitizes_unknown_exceptions()
    {
        var cancelled = await new TeamsSdkReplyClient(new FakeOperations("cancelled"))
            .DeliverAsync(CreateMessage(), TestContext.Current.CancellationToken);
        var failure = await new TeamsSdkReplyClient(new FakeOperations("secret"))
            .DeliverAsync(CreateMessage(), TestContext.Current.CancellationToken);

        Assert.Equal(TeamsDeliveryStatus.Cancelled, cancelled.Status);
        Assert.Equal("cancelled", cancelled.ReasonCode);
        Assert.Equal(TeamsDeliveryStatus.Failed, failure.Status);
        Assert.Equal("sdk_delivery_failed", failure.ReasonCode);
        Assert.DoesNotContain("secret", failure.ReasonCode, StringComparison.OrdinalIgnoreCase);
    }

    private static TeamsOutboundMessage CreateMessage(string? updateActivityId = null) => new(
        new TeamsOutboundDestination(
            "tenant",
            "conversation",
            TeamsConversationScope.Personal,
            "https://service.invalid/",
            userId: "user"),
        "reply",
        "idempotency",
        "correlation",
        updateActivityId: updateActivityId);

    private sealed class FakeOperations(params string[] outcomes) : ITeamsSdkReplyOperations
    {
        private readonly Queue<string> _outcomes = new(outcomes);

        public List<TeamsOutboundMessage> Requests { get; } = [];

        public Task<string?> DeliverAsync(TeamsOutboundMessage message, CancellationToken cancellationToken)
        {
            Requests.Add(message);
            var outcome = _outcomes.Dequeue();
            return outcome switch
            {
                "unavailable" => Task.FromException<string?>(new HttpRequestException("transport unavailable")),
                "unauthorized" => Task.FromException<string?>(new UnauthorizedAccessException("credential failure")),
                "too-large" => Task.FromException<string?>(new HttpRequestException("MessageSizeTooBig", null, HttpStatusCode.RequestEntityTooLarge)),
                "missing-destination" => Task.FromException<string?>(new HttpRequestException("gone", null, HttpStatusCode.Gone)),
                "cancelled" => Task.FromException<string?>(new OperationCanceledException()),
                "secret" => Task.FromException<string?>(new InvalidOperationException("secret tenant service URL body")),
                _ => Task.FromResult<string?>(outcome)
            };
        }
    }
}
