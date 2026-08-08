// -----------------------------------------------------------------------
// <copyright file="TeamsSdkReplyClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Microsoft.Teams.Api;
using Microsoft.Teams.Api.Activities;
using Microsoft.Teams.Apps;
using Netclaw.Channels.Teams;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Executes the Teams SDK calls at the daemon transport edge. The SDK context
/// contains the authenticated application client and never enters an actor.
/// </summary>
internal sealed class TeamsSdkReplyClient(ITeamsSdkReplyOperations operations) : ITeamsReplyClient
{
    public async Task<TeamsDeliveryResult> DeliverAsync(
        TeamsOutboundMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (cancellationToken.IsCancellationRequested)
            return new TeamsDeliveryResult(TeamsDeliveryStatus.Cancelled, ReasonCode: "cancelled");

        try
        {
            var activityId = await operations.DeliverAsync(message, cancellationToken);
            return string.IsNullOrWhiteSpace(activityId)
                ? new TeamsDeliveryResult(TeamsDeliveryStatus.Failed, ReasonCode: "empty_sdk_result")
                : new TeamsDeliveryResult(
                    string.IsNullOrWhiteSpace(message.UpdateActivityId)
                        ? TeamsDeliveryStatus.Delivered
                        : TeamsDeliveryStatus.Updated,
                    activityId);
        }
        catch (OperationCanceledException)
        {
            return new TeamsDeliveryResult(TeamsDeliveryStatus.Cancelled, ReasonCode: "cancelled");
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.RequestEntityTooLarge
                                                    || exception.Message.Contains("MessageSizeTooBig", StringComparison.Ordinal))
        {
            return new TeamsDeliveryResult(TeamsDeliveryStatus.RejectedTooLarge, ReasonCode: "output_too_large");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return new TeamsDeliveryResult(TeamsDeliveryStatus.InvalidDestination, ReasonCode: "sdk_destination_invalid");
        }
        catch (HttpRequestException)
        {
            return new TeamsDeliveryResult(TeamsDeliveryStatus.Unavailable, ReasonCode: "sdk_unavailable");
        }
        catch (UnauthorizedAccessException)
        {
            return new TeamsDeliveryResult(TeamsDeliveryStatus.Unavailable, ReasonCode: "sdk_unauthorized");
        }
        catch (Exception exception) when (exception.Message.Contains("MessageSizeTooBig", StringComparison.Ordinal))
        {
            return new TeamsDeliveryResult(TeamsDeliveryStatus.RejectedTooLarge, ReasonCode: "output_too_large");
        }
        catch (Exception)
        {
            // SDK messages can contain remote response data. Expose only a safe code.
            return new TeamsDeliveryResult(TeamsDeliveryStatus.Failed, ReasonCode: "sdk_delivery_failed");
        }
    }
}

/// <summary>
/// Keeps the SDK call surface narrow so the result mapper has direct tests
/// without a live Teams request context.
/// </summary>
internal interface ITeamsSdkReplyOperations
{
    Task<string?> DeliverAsync(TeamsOutboundMessage message, CancellationToken cancellationToken);
}

internal sealed class TeamsSdkReplyOperations(
    Microsoft.Teams.Apps.App app,
    TeamsSdkConversationContextStore contexts) : ITeamsSdkReplyOperations
{
    public async Task<string?> DeliverAsync(TeamsOutboundMessage message, CancellationToken cancellationToken)
    {
        var activity = new MessageActivity(message.Text)
        {
            ReplyToId = message.ReplyToActivityId
        };
        if (message.ApprovalCard is { } approvalCard)
        {
            var payload = new Dictionary<string, object?>
            {
                ["$schema"] = TeamsApprovalCard.Schema,
                ["type"] = "AdaptiveCard",
                ["version"] = TeamsApprovalCard.Version,
                ["body"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "TextBlock", ["text"] = approvalCard.Title,
                        ["weight"] = "Bolder", ["wrap"] = true
                    },
                    new Dictionary<string, object?>
                    {
                        ["type"] = "TextBlock", ["text"] = approvalCard.Body, ["wrap"] = true
                    }
                },
                ["actions"] = approvalCard.Actions.Select(cardAction => (object?)new Dictionary<string, object?>
                {
                    ["type"] = "Action.Execute",
                    ["title"] = cardAction.Title,
                    ["verb"] = "netclaw-approval",
                    ["data"] = new Dictionary<string, object?>
                    {
                        ["correlation"] = cardAction.CorrelationId,
                        ["nonce"] = cardAction.Nonce,
                        ["action"] = cardAction.Action
                    }
                }).ToArray()
            };
            var card = Microsoft.Teams.Cards.AdaptiveCard.Deserialize(JsonSerializer.Serialize(payload))
                ?? throw new InvalidOperationException("The Teams approval card did not deserialize.");
            activity.Attachments = [new Attachment(card)];
        }
        if (contexts.TryGet(message.Destination, out var context))
        {
            if (!string.IsNullOrWhiteSpace(message.UpdateActivityId))
            {
                var update = await context.Api.Conversations.Activities.UpdateAsync(
                    message.Destination.ConversationId,
                    message.UpdateActivityId,
                    activity,
                    cancellationToken);
                return update?.Id;
            }

            var sent = await context.Send(activity, cancellationToken);
            return sent.Id;
        }

        if (!string.IsNullOrWhiteSpace(message.UpdateActivityId))
            throw new HttpRequestException("The Teams update context is unavailable.");

        // The app owns authenticated client credentials. Unlike an inbound SDK
        // context, it survives the original HTTP request and can send to the
        // validated destination recovered by the binding actor.
        var proactiveSent = await app.Send(
            message.Destination.ConversationId,
            activity,
            serviceUrl: message.Destination.ServiceUrl,
            cancellationToken: cancellationToken);
        return proactiveSent.Id;
    }
}

/// <summary>
/// Holds the live SDK context at the daemon edge for the active conversation.
/// It is bounded by active keys and never serializes, logs, or exposes the SDK
/// object to actor state.
/// </summary>
internal sealed class TeamsSdkConversationContextStore
{
    private readonly Dictionary<TeamsSdkContextKey, IContext<IActivity>> _contexts = [];
    private readonly object _gate = new();

    public void Capture(TeamsInboundActivity activity, IContext<IActivity> context)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryCreateDestination(activity, out var destination)
            || !MatchesActivity(activity, context.Activity))
            return;

        lock (_gate)
        {
            var key = TeamsSdkContextKey.Create(destination);
            if (_contexts.Count >= 1_024 && !_contexts.ContainsKey(key))
                _contexts.Remove(_contexts.Keys.First());
            _contexts[key] = context;
        }
    }

    public void Capture(TeamsApprovalAction action, IContext<IActivity> context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryCreateDestination(action, out var destination)
            || !MatchesAction(action, context.Activity))
        {
            return;
        }

        lock (_gate)
        {
            var key = TeamsSdkContextKey.Create(destination);
            if (_contexts.Count >= 1_024 && !_contexts.ContainsKey(key))
                _contexts.Remove(_contexts.Keys.First());
            _contexts[key] = context;
        }
    }

    public bool TryGet(TeamsOutboundDestination destination, out IContext<IActivity> context)
    {
        lock (_gate)
            return _contexts.TryGetValue(TeamsSdkContextKey.Create(destination), out context!);
    }

    private static bool TryCreateDestination(TeamsInboundActivity activity, out TeamsOutboundDestination destination)
    {
        destination = null!;
        try
        {
            destination = new TeamsOutboundDestination(
                activity.Trust.TenantId,
                activity.Trust.ConversationId,
                activity.Trust.Scope,
                activity.Reply?.ServiceUrl ?? string.Empty,
                activity.Trust.Scope == TeamsConversationScope.Channel ? activity.Reply?.RootActivityId : null,
                activity.TeamId,
                activity.ChannelId,
                activity.Trust.Scope == TeamsConversationScope.Personal ? activity.Trust.SenderId : null);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryCreateDestination(TeamsApprovalAction action, out TeamsOutboundDestination destination)
    {
        destination = null!;
        try
        {
            destination = new TeamsOutboundDestination(
                action.Trust.TenantId,
                action.Trust.ConversationId,
                action.Trust.Scope,
                action.ServiceUrl,
                action.RootActivityId,
                action.TeamId,
                action.ChannelId,
                action.Trust.Scope == TeamsConversationScope.Personal ? action.Trust.SenderId : null);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool MatchesActivity(TeamsInboundActivity activity, IActivity sdkActivity) =>
        string.Equals(activity.Trust.ActivityId, sdkActivity.Id, StringComparison.Ordinal)
        && string.Equals(activity.Trust.ConversationId, sdkActivity.Conversation?.Id, StringComparison.Ordinal)
        && string.Equals(activity.Reply?.ServiceUrl, sdkActivity.ServiceUrl, StringComparison.Ordinal);

    private static bool MatchesAction(TeamsApprovalAction action, IActivity sdkActivity) =>
        string.Equals(action.Trust.ActivityId, sdkActivity.Id, StringComparison.Ordinal)
        && string.Equals(action.Trust.ConversationId, sdkActivity.Conversation?.Id, StringComparison.Ordinal)
        && string.Equals(action.ServiceUrl, sdkActivity.ServiceUrl, StringComparison.Ordinal);

    private sealed record TeamsSdkContextKey(
        string TenantId,
        string ConversationId,
        TeamsConversationScope Scope,
        string? RootActivityId,
        string ServiceUrl)
    {
        public static TeamsSdkContextKey Create(TeamsOutboundDestination destination) => new(
            destination.TenantId,
            destination.ConversationId,
            destination.Scope,
            destination.RootActivityId,
            destination.ServiceUrl);
    }
}
