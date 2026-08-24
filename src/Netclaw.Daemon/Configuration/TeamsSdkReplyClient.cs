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
    Microsoft.Teams.Apps.App app) : ITeamsSdkReplyOperations
{
    public async Task<string?> DeliverAsync(TeamsOutboundMessage message, CancellationToken cancellationToken)
    {
        var activity = new MessageActivity(message.ApprovalCard is null ? message.Text : string.Empty)
        {
            ReplyToId = message.ReplyToActivityId
        };
        if (message.ApprovalCard is { } approvalCard)
        {
            var payload = TeamsAdaptiveCardPayloadBuilder.Create(approvalCard);
            var card = Microsoft.Teams.Cards.AdaptiveCard.Deserialize(JsonSerializer.Serialize(payload))
                ?? throw new InvalidOperationException("The Teams approval card did not deserialize.");
            activity.Attachments = [new Attachment(card)];
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

internal static class TeamsAdaptiveCardPayloadBuilder
{
    public static Dictionary<string, object?> Create(TeamsApprovalCard approvalCard)
    {
        ArgumentNullException.ThrowIfNull(approvalCard);

        var title = new Dictionary<string, object?>
        {
            ["type"] = "TextBlock", ["text"] = approvalCard.Title,
            ["size"] = "Medium", ["weight"] = "Bolder", ["wrap"] = true
        };
        if (approvalCard.Tone != TeamsApprovalCardTone.Default)
            title["color"] = ToWireColor(approvalCard.Tone);

        return new Dictionary<string, object?>
        {
            ["$schema"] = TeamsApprovalCard.Schema,
            ["type"] = "AdaptiveCard",
            ["version"] = TeamsApprovalCard.Version,
            ["body"] = new object?[]
            {
                title,
                new Dictionary<string, object?>
                {
                    ["type"] = "TextBlock", ["text"] = approvalCard.Body, ["wrap"] = true,
                    ["spacing"] = "Medium"
                }
            },
            ["actions"] = approvalCard.Actions.Select(cardAction => (object?)new Dictionary<string, object?>
            {
                ["type"] = "Action.Execute",
                ["title"] = cardAction.Title,
                ["style"] = ToWireStyle(cardAction.Style),
                ["verb"] = "netclaw-approval",
                ["data"] = new Dictionary<string, object?>
                {
                    ["correlation"] = cardAction.CorrelationId,
                    ["nonce"] = cardAction.Nonce,
                    ["action"] = cardAction.Action
                }
            }).ToArray()
        };
    }

    private static string ToWireStyle(TeamsApprovalActionStyle style) => style switch
    {
        TeamsApprovalActionStyle.Default => "default",
        TeamsApprovalActionStyle.Positive => "positive",
        TeamsApprovalActionStyle.Destructive => "destructive",
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unsupported Teams approval action style.")
    };

    private static string ToWireColor(TeamsApprovalCardTone tone) => tone switch
    {
        TeamsApprovalCardTone.Good => "good",
        TeamsApprovalCardTone.Warning => "warning",
        TeamsApprovalCardTone.Attention => "attention",
        _ => throw new ArgumentOutOfRangeException(nameof(tone), tone, "Unsupported Teams approval card tone.")
    };
}
