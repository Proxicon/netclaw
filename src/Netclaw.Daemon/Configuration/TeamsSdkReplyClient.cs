// -----------------------------------------------------------------------
// <copyright file="TeamsSdkReplyClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
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

        var body = new List<object?> { CreateHeader(approvalCard) };
        if (!string.IsNullOrWhiteSpace(approvalCard.Banner))
            body.Add(CreateBanner(approvalCard.Banner, approvalCard.Tone));

        if (approvalCard.Fields.Count > 0)
        {
            body.Add(CreateTable(approvalCard.Fields));
        }

        if (!string.IsNullOrWhiteSpace(approvalCard.Summary)
            && !string.Equals(approvalCard.Summary, approvalCard.Banner, StringComparison.Ordinal))
        {
            body.Add(CreateContextSummary(approvalCard.Summary));
        }

        if (!string.IsNullOrWhiteSpace(approvalCard.Footer))
            body.Add(CreateFooter(approvalCard.Footer, approvalCard.Tone));

        var payload = new Dictionary<string, object?>
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
        if (!string.IsNullOrWhiteSpace(approvalCard.Speak))
            payload["speak"] = approvalCard.Speak;

        if (JsonSerializer.SerializeToUtf8Bytes(payload).Length > TeamsApprovalCard.MaxSerializedBytes)
            throw new InvalidOperationException("The Teams approval card exceeds the outbound payload limit.");

        return payload;
    }

    private static Dictionary<string, object?> CreateHeader(TeamsApprovalCard card) => new()
    {
        ["type"] = "ColumnSet",
        ["columns"] = new object?[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "Column",
                ["width"] = "auto",
                ["verticalContentAlignment"] = "Center",
                ["items"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "Icon",
                        ["name"] = card.IconName,
                        ["size"] = "Large",
                        ["color"] = ToWireTone(card.Tone),
                        ["style"] = "Regular"
                    }
                }
            },
            new Dictionary<string, object?>
            {
                ["type"] = "Column",
                ["width"] = "stretch",
                ["verticalContentAlignment"] = "Center",
                ["items"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "TextBlock",
                        ["text"] = card.Title,
                        ["size"] = "Large",
                        ["weight"] = "Bolder",
                        ["wrap"] = true
                    },
                    new Dictionary<string, object?>
                    {
                        ["type"] = "TextBlock",
                        ["text"] = "NETCLAW SECURITY CONTROL",
                        ["isSubtle"] = true,
                        ["spacing"] = "None",
                        ["wrap"] = true
                    }
                }
            }
        }
    };

    private static Dictionary<string, object?> CreateBanner(string text, TeamsApprovalCardTone tone) => new()
    {
        ["type"] = "Container",
        ["style"] = ToWireTone(tone),
        ["bleed"] = true,
        ["spacing"] = "Medium",
        ["items"] = new object?[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "TextBlock",
                ["text"] = text,
                ["color"] = ToWireTone(tone),
                ["weight"] = "Bolder",
                ["wrap"] = true
            }
        }
    };

    private static Dictionary<string, object?> CreateTable(IReadOnlyList<TeamsApprovalCardField> fields) => new()
    {
        ["type"] = "Table",
        ["firstRowAsHeaders"] = false,
        ["showGridLines"] = true,
        ["gridStyle"] = "Default",
        ["columns"] = new object?[]
        {
            new Dictionary<string, object?> { ["width"] = 1 },
            new Dictionary<string, object?> { ["width"] = 2 }
        },
        ["rows"] = fields.Select(CreateRow).ToArray()
    };

    private static Dictionary<string, object?> CreateRow(TeamsApprovalCardField field) => new()
    {
        ["type"] = "TableRow",
        ["cells"] = new object?[]
        {
            CreateCell(field.Label, subtle: true),
            CreateCell(field.Value, subtle: false)
        }
    };

    private static Dictionary<string, object?> CreateCell(string text, bool subtle) => new()
    {
        ["type"] = "TableCell",
        ["items"] = new object?[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "TextBlock",
                ["text"] = text,
                ["isSubtle"] = subtle,
                ["weight"] = subtle ? "Default" : "Bolder",
                ["wrap"] = true
            }
        }
    };

    private static Dictionary<string, object?> CreateContextSummary(string summary) => new()
    {
        ["type"] = "Container",
        ["style"] = "Warning",
        ["spacing"] = "Medium",
        ["items"] = new object?[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "TextBlock",
                ["text"] = summary,
                ["color"] = "Warning",
                ["wrap"] = true
            }
        }
    };

    private static Dictionary<string, object?> CreateFooter(string text, TeamsApprovalCardTone tone) => new()
    {
        ["type"] = "Container",
        ["style"] = ToWireTone(tone),
        ["bleed"] = true,
        ["spacing"] = "Medium",
        ["items"] = new object?[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "TextBlock",
                ["text"] = text,
                ["horizontalAlignment"] = "Center",
                ["weight"] = "Bolder",
                ["color"] = ToWireTone(tone),
                ["wrap"] = true
            }
        }
    };

    private static string ToWireStyle(TeamsApprovalActionStyle style) => style switch
    {
        TeamsApprovalActionStyle.Default => "default",
        TeamsApprovalActionStyle.Positive => "positive",
        TeamsApprovalActionStyle.Destructive => "destructive",
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unsupported Teams approval action style.")
    };

    private static string ToWireTone(TeamsApprovalCardTone tone) => tone switch
    {
        TeamsApprovalCardTone.Default => "Default",
        TeamsApprovalCardTone.Accent => "Accent",
        TeamsApprovalCardTone.Good => "Good",
        TeamsApprovalCardTone.Warning => "Warning",
        TeamsApprovalCardTone.Attention => "Attention",
        _ => throw new ArgumentOutOfRangeException(nameof(tone), tone, "Unsupported Teams approval card tone.")
    };
}
