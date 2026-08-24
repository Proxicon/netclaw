// -----------------------------------------------------------------------
// <copyright file="TeamsSdkReplyClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Teams;
using Netclaw.Daemon.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

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

    [Fact]
    public void Approval_card_payload_preserves_the_canonical_button_styles()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("teams~tenant~personal~conversation/conversation"),
            Kind = "approval",
            CallId = new ToolCallId("call-approval"),
            ToolName = new ToolName("shell_execute"),
            DisplayText = "rmdir /home/sto/.netclaw/workspaces/testapproval",
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhereKey, ApprovalOptionKeys.ApproveEverywhereLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };

        var approvalCard = TeamsApprovalCardRenderer.CreatePending(request, "correlation_123", "nonce_123");
        var payload = TeamsAdaptiveCardPayloadBuilder.Create(approvalCard);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var actions = document.RootElement.GetProperty("actions").EnumerateArray().ToArray();

        Assert.Equal(request.Options.Select(option => option.Label), actions.Select(action => action.GetProperty("title").GetString()));
        Assert.Equal(
            ["positive", "default", "default", "destructive", "destructive"],
            actions.Select(action => action.GetProperty("style").GetString()));
        Assert.All(actions, action => Assert.Equal("Action.Execute", action.GetProperty("type").GetString()));
    }

    [Fact]
    public void Terminal_denial_card_payload_has_no_actions_and_uses_attention_tone()
    {
        var card = TeamsApprovalCardRenderer.CreateTerminal(
            "shell_execute",
            "rmdir netclaw-approval-card-never-created",
            "Denied.");
        var payload = TeamsAdaptiveCardPayloadBuilder.Create(card);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));

        var body = document.RootElement.GetProperty("body");
        Assert.Equal("Approval denied", body[0].GetProperty("text").GetString());
        Assert.Equal("attention", body[0].GetProperty("color").GetString());
        Assert.Contains("Tool: shell_execute", body[1].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains("Action: rmdir netclaw-approval-card-never-created", body[1].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Empty(document.RootElement.GetProperty("actions").EnumerateArray());
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
