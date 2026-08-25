// -----------------------------------------------------------------------
// <copyright file="TeamsSdkReplyClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Core.Schema;
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
        return await ExecuteAsync(async cancellationToken =>
        {
            var activityId = await operations.DeliverAsync(message, cancellationToken);
            return string.IsNullOrWhiteSpace(activityId)
                ? new TeamsDeliveryResult(TeamsDeliveryStatus.Failed, ReasonCode: "empty_sdk_result")
                : new TeamsDeliveryResult(
                    string.IsNullOrWhiteSpace(message.UpdateActivityId)
                        ? TeamsDeliveryStatus.Delivered
                        : TeamsDeliveryStatus.Updated,
                    activityId);
        }, cancellationToken);
    }

    public Task<TeamsDeliveryResult> SendTypingAsync(
        TeamsOutboundDestination destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return ExecuteAsync(async cancellationToken =>
        {
            await operations.SendTypingAsync(destination, cancellationToken);
            return new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered);
        }, cancellationToken);
    }

    private static async Task<TeamsDeliveryResult> ExecuteAsync(
        Func<CancellationToken, Task<TeamsDeliveryResult>> operation,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new TeamsDeliveryResult(TeamsDeliveryStatus.Cancelled, ReasonCode: "cancelled");

        try
        {
            return await operation(cancellationToken);
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

    Task SendTypingAsync(TeamsOutboundDestination destination, CancellationToken cancellationToken);
}

internal sealed class TeamsSdkReplyOperations(
    TeamsBotApplication app) : ITeamsSdkReplyOperations
{
    public async Task<string?> DeliverAsync(TeamsOutboundMessage message, CancellationToken cancellationToken)
    {
        var serviceUrl = new Uri(message.Destination.ServiceUrl, UriKind.Absolute);
        var activity = new MessageActivityInput()
            .WithText(message.ApprovalCard is null ? message.Text : string.Empty);
        if (message.ApprovalCard is { } approvalCard)
        {
            var payload = TeamsAdaptiveCardPayloadBuilder.Create(approvalCard);
            activity.AddAdaptiveCardAttachment(payload);
        }

        // The application owns authenticated client credentials. It survives
        // the original HTTP request and sends to the validated destination
        // recovered by the binding actor.
        if (!string.IsNullOrWhiteSpace(message.UpdateActivityId))
        {
            var updated = await app.Api
                .ForServiceUrl(serviceUrl)
                .Conversations
                .UpdateActivityAsync(
                    message.Destination.ConversationId,
                    message.UpdateActivityId,
                    activity,
                    cancellationToken: cancellationToken);
            return updated.Id;
        }

        var proactiveSent = string.IsNullOrWhiteSpace(message.ReplyToActivityId)
            ? await app.SendAsync(
                message.Destination.ConversationId,
                activity,
                serviceUrl,
                cancellationToken: cancellationToken)
            : await app.ReplyAsync(
                message.Destination.ConversationId,
                message.ReplyToActivityId,
                activity,
                serviceUrl,
                cancellationToken: cancellationToken);
        return proactiveSent?.Id;
    }

    public async Task SendTypingAsync(TeamsOutboundDestination destination, CancellationToken cancellationToken)
    {
        var activity = new CoreActivityInput("typing")
        {
            ReplyToId = destination.Scope == TeamsConversationScope.Channel
                ? destination.RootActivityId
                : null
        };

        await app.SendActivityAsync(
            destination.ConversationId,
            activity,
            new Uri(destination.ServiceUrl, UriKind.Absolute),
            cancellationToken: cancellationToken);
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

        var body = new List<object?> { title };
        if (approvalCard.Fields.Count > 0)
        {
            body.AddRange(approvalCard.Fields.Select(CreateField));
            if (!string.IsNullOrWhiteSpace(approvalCard.Summary))
                body.Add(CreateSummary(approvalCard.Summary));
        }
        else if (!string.IsNullOrWhiteSpace(approvalCard.Body))
        {
            body.Add(CreateSummary(approvalCard.Body));
        }

        return new Dictionary<string, object?>
        {
            ["$schema"] = TeamsApprovalCard.Schema,
            ["type"] = "AdaptiveCard",
            ["version"] = TeamsApprovalCard.Version,
            ["body"] = body,
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

    private static Dictionary<string, object?> CreateField(TeamsApprovalCardField field) => new()
    {
        ["type"] = "ColumnSet",
        ["spacing"] = "Small",
        ["columns"] = new object?[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "Column",
                ["width"] = "auto",
                ["items"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "TextBlock",
                        ["text"] = field.Label + ":",
                        ["weight"] = "Bolder",
                        ["wrap"] = true
                    }
                }
            },
            new Dictionary<string, object?>
            {
                ["type"] = "Column",
                ["width"] = "stretch",
                ["items"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "TextBlock",
                        ["text"] = field.Value,
                        ["fontType"] = "Monospace",
                        ["color"] = "Light",
                        ["wrap"] = true
                    }
                }
            }
        }
    };

    private static Dictionary<string, object?> CreateSummary(string summary) => new()
    {
        ["type"] = "TextBlock",
        ["text"] = summary,
        ["wrap"] = true,
        ["spacing"] = "Medium"
    };

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
