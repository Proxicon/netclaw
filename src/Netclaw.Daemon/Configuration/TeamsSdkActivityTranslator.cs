// -----------------------------------------------------------------------
// <copyright file="TeamsSdkActivityTranslator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Teams.Api;
using Microsoft.Teams.Api.Activities;
using Microsoft.Teams.Api.Activities.Invokes;
using Microsoft.Teams.Api.Entities;
using Netclaw.Actors.Channels;
using Netclaw.Channels.Teams;
using Netclaw.Configuration;
using MessageActivity = Microsoft.Teams.Api.Activities.MessageActivity;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Keeps Microsoft SDK activity objects at the HTTP hosting edge. No Teams
/// SDK type is allowed to cross into the channel contracts or actor boundary.
/// </summary>
internal sealed class TeamsSdkActivityTranslator(TeamsChannelOptions options, TimeProvider timeProvider)
{
    private const int MaxAttachmentContentTypeLength = 255;
    private const int MaxAttachmentContentUrlLength = 2_048;

    public TeamsTranslationResult Translate(IActivity activity, string? authenticatedTenantId)
    {
        ArgumentNullException.ThrowIfNull(activity);

        return activity switch
        {
            MessageUpdateActivity update => TranslateMutation(update, authenticatedTenantId, TeamsIngressActivityKind.MessageUpdate),
            MessageDeleteActivity delete => TranslateMutation(delete, authenticatedTenantId, TeamsIngressActivityKind.MessageDelete),
            ConversationUpdateActivity => TeamsTranslationResult.Ignored(
                TeamsIngressActivityKind.ConversationUpdate,
                "conversation_update_recording_not_implemented"),
            AdaptiveCards.ActionActivity action => TranslateApprovalAction(action, authenticatedTenantId),
            MessageActivity message => TranslateMessage(message, authenticatedTenantId),
            _ => TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, TeamsIngressActivityKind.Unknown, "unsupported_activity_type")
        };
    }

    private TeamsTranslationResult TranslateApprovalAction(
        AdaptiveCards.ActionActivity activity,
        string? authenticatedTenantId)
    {
        if (!TryValidateCommon(activity, authenticatedTenantId, TeamsIngressActivityKind.AdaptiveCardAction, out var scope, out var failure))
            return failure!;
        var invokeAction = activity.Value?.Action;
        if (invokeAction is null || !invokeAction.Type.IsExecute)
        {
            return TeamsTranslationResult.Rejected(
                TeamsTranslationDisposition.RejectedMalformed,
                TeamsIngressActivityKind.AdaptiveCardAction,
                "unsupported_card_action_type");
        }

        var data = invokeAction.Data;
        if (!TryGetOpaqueValue(data, "correlation", TeamsApprovalAction.MaxCorrelationLength, out var correlation)
            || !TryGetOpaqueValue(data, "nonce", TeamsApprovalAction.MaxNonceLength, out var nonce)
            || !TryGetOpaqueValue(data, "action", 16, out var action)
            || !TeamsApprovalAction.IsSupportedAction(action)
            || !TeamsApprovalAction.IsBoundedOpaqueValue(correlation, TeamsApprovalAction.MaxCorrelationLength)
            || !TeamsApprovalAction.IsBoundedOpaqueValue(nonce, TeamsApprovalAction.MaxNonceLength))
        {
            return TeamsTranslationResult.Rejected(
                TeamsTranslationDisposition.RejectedMalformed,
                TeamsIngressActivityKind.AdaptiveCardAction,
                "invalid_approval_action_data");
        }

        string? rootActivityId = null;
        if (scope == TeamsConversationScope.Channel
            && !TeamsTenantEvidenceMappings.TryGetCanonicalChannelRootActivityId(activity.Conversation!.Id, out rootActivityId))
        {
            return TeamsTranslationResult.Rejected(
                TeamsTranslationDisposition.RejectedMalformed,
                TeamsIngressActivityKind.AdaptiveCardAction,
                "invalid_channel_root_identity");
        }

        var serviceUrl = activity.ServiceUrl;
        if (string.IsNullOrWhiteSpace(activity.ReplyToId)
            || !TeamsSessionIdentifierCodec.IsValidActivityIdentifier(activity.ReplyToId)
            || !TeamsOutboundDestination.IsValidServiceUrl(serviceUrl))
        {
            return TeamsTranslationResult.Rejected(
                TeamsTranslationDisposition.RejectedMalformed,
                TeamsIngressActivityKind.AdaptiveCardAction,
                "invalid_approval_action_context");
        }

        return TeamsTranslationResult.Accepted(new TeamsApprovalAction(
            CreateTrust(activity, authenticatedTenantId!, scope),
            correlation,
            nonce,
            action,
            rootActivityId,
            activity.ChannelData?.Team?.Id,
            activity.ChannelData?.Channel?.Id,
            activity.ReplyToId,
            serviceUrl!));
    }

    private TeamsTranslationResult TranslateMessage(MessageActivity activity, string? authenticatedTenantId)
    {
        if (string.IsNullOrWhiteSpace(authenticatedTenantId))
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedPendingTenantEvidence, TeamsIngressActivityKind.Message, "missing_authenticated_tenant_id");

        if (string.IsNullOrWhiteSpace(options.TenantId))
        {
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedPendingTenantEvidence, TeamsIngressActivityKind.Message, "missing_configured_tenant_id");
        }

        if (!string.Equals(authenticatedTenantId, options.TenantId, StringComparison.Ordinal))
        {
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedPendingTenantEvidence, TeamsIngressActivityKind.Message, "configured_tenant_mismatch");
        }

        if (activity.Conversation is null || string.IsNullOrWhiteSpace(activity.Conversation.Id))
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, TeamsIngressActivityKind.Message, "missing_conversation_id");

        if (string.IsNullOrWhiteSpace(activity.Id))
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, TeamsIngressActivityKind.Message, "missing_activity_id");

        if (string.IsNullOrWhiteSpace(activity.Text))
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, TeamsIngressActivityKind.Message, "missing_message_text");

        if (string.IsNullOrWhiteSpace(activity.From?.Id))
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, TeamsIngressActivityKind.Message, "missing_sender_id");

        if (!string.IsNullOrWhiteSpace(activity.Conversation.TenantId)
            && !string.Equals(activity.Conversation.TenantId, authenticatedTenantId, StringComparison.Ordinal))
        {
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, TeamsIngressActivityKind.Message, "tenant_mismatch");
        }

        var scope = activity.Conversation.Type switch
        {
            { IsPersonal: true } => TeamsConversationScope.Personal,
            { IsChannel: true } => TeamsConversationScope.Channel,
            _ => (TeamsConversationScope?)null
        };

        if (scope is null)
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedUnsupportedScope, TeamsIngressActivityKind.Message, "unsupported_conversation_scope");

        string? rootActivityId = null;
        if (scope == TeamsConversationScope.Channel
            && !TeamsTenantEvidenceMappings.TryGetCanonicalChannelRootActivityId(activity.Conversation.Id, out rootActivityId))
        {
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, TeamsIngressActivityKind.Message, "invalid_channel_root_identity");
        }

        var attachmentFailure = RejectUnsupportedAttachments(activity.Attachments);
        if (attachmentFailure is not null)
            return attachmentFailure;

        var trust = CreateTrust(activity, authenticatedTenantId, scope.Value);
        var mentions = TranslateMentions(activity.Entities);
        var text = scope == TeamsConversationScope.Channel
            ? TeamsTenantEvidenceMappings.RemoveQualifiedBotMentions(
                activity.Text,
                mentions.Select(mention => new TeamsMentionEvidence(mention.Type, mention.MentionedId, mention.Text)),
                activity.Recipient?.Id ?? string.Empty,
                options.BotId ?? string.Empty)
            : activity.Text;
        var mentioned = scope == TeamsConversationScope.Channel
            && mentions.Any(mention => IsQualifiedBotMention(mention, activity.Recipient?.Id, options.BotId));

        return TeamsTranslationResult.Accepted(new TeamsInboundActivity(
            trust,
            text,
            new TeamsReplyMetadata(activity.ReplyToId, rootActivityId, activity.ServiceUrl),
            mentioned,
            kind: TeamsIngressActivityKind.Message,
            teamId: activity.ChannelData?.Team?.Id,
            channelId: activity.ChannelData?.Channel?.Id,
            mentions: mentions),
            activity.Attachments is { Count: > 0 }
                ? "teams_text_rendering_wrapper_ignored"
                : "plain_text_accepted");
    }

    private TeamsTranslationResult TranslateMutation(
        IActivity activity,
        string? authenticatedTenantId,
        TeamsIngressActivityKind kind)
    {
        if (!TryValidateCommon(activity, authenticatedTenantId, kind, out var scope, out var failure))
            return failure!;
        if (scope != TeamsConversationScope.Channel)
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedUnsupportedScope, kind, "unsupported_conversation_scope");
        if (!TeamsTenantEvidenceMappings.TryGetCanonicalChannelRootActivityId(activity.Conversation!.Id, out var rootActivityId))
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, kind, "invalid_channel_root_identity");

        return TeamsTranslationResult.Accepted(new TeamsInboundActivity(
            CreateTrust(activity, authenticatedTenantId!, scope),
            string.Empty,
            new TeamsReplyMetadata(activity.ReplyToId, rootActivityId, activity.ServiceUrl),
            kind: kind,
            teamId: activity.ChannelData?.Team?.Id,
            channelId: activity.ChannelData?.Channel?.Id));
    }

    private TeamsIngressTrustContext CreateTrust(IActivity activity, string authenticatedTenantId, TeamsConversationScope scope) => new(
            TrustAudience.Public,
            PrincipalClassification.UntrustedExternal,
            TrustBoundary.Public,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            activity.From.Id,
            authenticatedTenantId,
            activity.Conversation.Id,
            scope,
            activity.Id,
            timeProvider.GetUtcNow(),
            activity.Timestamp is { } timestamp ? new DateTimeOffset(timestamp.ToUniversalTime()) : null);

    private bool TryValidateCommon(
        IActivity activity,
        string? authenticatedTenantId,
        TeamsIngressActivityKind kind,
        out TeamsConversationScope scope,
        out TeamsTranslationResult? failure)
    {
        scope = default;
        failure = null;
        if (string.IsNullOrWhiteSpace(authenticatedTenantId) || string.IsNullOrWhiteSpace(options.TenantId)
            || !string.Equals(authenticatedTenantId, options.TenantId, StringComparison.Ordinal))
        {
            failure = TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedPendingTenantEvidence, kind, "configured_tenant_mismatch");
            return false;
        }
        if (activity.Conversation is null || string.IsNullOrWhiteSpace(activity.Conversation.Id))
        {
            failure = TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, kind, "missing_conversation_id");
            return false;
        }
        if (string.IsNullOrWhiteSpace(activity.Id))
        {
            failure = TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, kind, "missing_activity_id");
            return false;
        }
        if (string.IsNullOrWhiteSpace(activity.From?.Id))
        {
            failure = TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, kind, "missing_sender_id");
            return false;
        }
        if (!string.IsNullOrWhiteSpace(activity.Conversation.TenantId)
            && !string.Equals(activity.Conversation.TenantId, authenticatedTenantId, StringComparison.Ordinal))
        {
            failure = TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, kind, "tenant_mismatch");
            return false;
        }
        if (activity.Conversation.Type is { IsChannel: true })
        {
            scope = TeamsConversationScope.Channel;
            return true;
        }
        if (activity.Conversation.Type is { IsPersonal: true })
        {
            scope = TeamsConversationScope.Personal;
            return true;
        }

        failure = TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedUnsupportedScope, kind, "unsupported_conversation_scope");
        return false;
    }

    private static TeamsTranslationResult? RejectUnsupportedAttachments(IList<Attachment>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
            return null;

        var reasonCode = "unsupported_attachment_shape";
        var hasUnsupportedAttachment = false;
        var hasMalformedAttachment = false;
        foreach (var attachment in attachments)
        {
            if (attachment is null)
            {
                hasUnsupportedAttachment = true;
                hasMalformedAttachment = true;
                continue;
            }

            var (contentKind, hasEmbeddedContentReference, hasEmbeddedGraphBackedContentReference) = GetContentFacts(attachment.Content);
            var evidence = new TeamsAttachmentEvidence(
                GetBoundedAttachmentMetadata(attachment.ContentType?.Value, MaxAttachmentContentTypeLength),
                attachment.Name is not null,
                GetBoundedAttachmentMetadata(attachment.ContentUrl, MaxAttachmentContentUrlLength),
                attachment.ContentUrl is not null,
                hasEmbeddedContentReference,
                hasEmbeddedGraphBackedContentReference,
                contentKind,
                attachment.ThumbnailUrl is not null);

            var classification = TeamsTenantEvidenceMappings.ClassifyAttachment(evidence);
            if (classification.Classification == TeamsAttachmentClassification.InlineTextRendering)
                continue;

            hasUnsupportedAttachment = true;
            if (classification.Classification == TeamsAttachmentClassification.GraphBackedUnsupported)
                reasonCode = classification.ReasonCode!;
        }

        if (!hasUnsupportedAttachment)
            return null;

        if (reasonCode == "unsupported_attachment_shape" && hasMalformedAttachment)
            reasonCode = "attachment_malformed_rejected";

        return TeamsTranslationResult.Rejected(
            TeamsTranslationDisposition.RejectedMalformed,
            TeamsIngressActivityKind.Message,
            reasonCode);
    }

    private static string? GetBoundedAttachmentMetadata(string? value, int maximumLength)
        => value is { Length: > 0 } && value.Length <= maximumLength ? value : null;

    private static (TeamsAttachmentContentKind ContentKind, bool HasReference, bool HasGraphBackedReference) GetContentFacts(object? content)
    {
        if (content is null)
            return (TeamsAttachmentContentKind.Missing, false, false);

        // The Teams SDK declares attachment Content as object. System.Text.Json
        // therefore materializes the normal text/html rendering wrapper as a
        // JSON string element on live inbound activities, not a CLR string.
        // Only that scalar representation is equivalent to text; objects and
        // arrays remain structured attachment evidence and fail closed.
        var text = content switch
        {
            string value => value,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => null
        };
        if (text is null)
            return (TeamsAttachmentContentKind.Structured, false, false);
        if (string.IsNullOrWhiteSpace(text))
            return (TeamsAttachmentContentKind.EmptyText, false, false);

        var hasReference = text.Contains("http://", StringComparison.OrdinalIgnoreCase)
                           || text.Contains("https://", StringComparison.OrdinalIgnoreCase);
        if (!hasReference)
            return (TeamsAttachmentContentKind.NonEmptyText, false, false);

        var hasGraphBackedReference = text.Contains("graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
                                      || text.Contains(".sharepoint.com", StringComparison.OrdinalIgnoreCase)
                                      || text.Contains(".sharepoint.us", StringComparison.OrdinalIgnoreCase)
                                      || text.Contains(".onedrive.com", StringComparison.OrdinalIgnoreCase)
                                      || text.Contains("onedrive.live.com", StringComparison.OrdinalIgnoreCase);
        return (TeamsAttachmentContentKind.NonEmptyText, true, hasGraphBackedReference);
    }

    private static ImmutableArray<TeamsMention> TranslateMentions(IList<IEntity>? entities)
        => entities?.OfType<MentionEntity>()
            .Where(entity => entity.Mentioned is not null && !string.IsNullOrWhiteSpace(entity.Text))
            .Select(entity => new TeamsMention(entity.Type, entity.Mentioned!.Id ?? string.Empty, entity.Text!))
            .ToImmutableArray() ?? [];

    private static bool IsQualifiedBotMention(TeamsMention mention, string? recipientId, string? configuredBotId)
        => string.Equals(mention.Type, "mention", StringComparison.Ordinal)
           && mention.Text.StartsWith("<at>", StringComparison.Ordinal)
           && mention.Text.EndsWith("</at>", StringComparison.Ordinal)
           && mention.Text.Length > "<at></at>".Length
           && !string.IsNullOrWhiteSpace(configuredBotId)
           && !string.IsNullOrWhiteSpace(recipientId)
           && string.Equals(mention.MentionedId, recipientId, StringComparison.Ordinal)
           && string.Equals(mention.MentionedId, $"28:{configuredBotId}", StringComparison.Ordinal);

    private static bool TryGetOpaqueValue(
        IDictionary<string, object>? data,
        string key,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (data is null || !data.TryGetValue(key, out var candidate))
            return false;

        value = candidate switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? string.Empty,
            _ => string.Empty
        };
        return value.Length <= maximumLength;
    }
}
