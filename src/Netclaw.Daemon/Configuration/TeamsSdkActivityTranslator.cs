// -----------------------------------------------------------------------
// <copyright file="TeamsSdkActivityTranslator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Microsoft.Teams.Api;
using Microsoft.Teams.Api.Activities;
using Microsoft.Teams.Api.Entities;
using Netclaw.Actors.Channels;
using Netclaw.Channels.Teams;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Keeps Microsoft SDK activity objects at the HTTP hosting edge. No Teams
/// SDK type is allowed to cross into the channel contracts or actor boundary.
/// </summary>
internal sealed class TeamsSdkActivityTranslator(TeamsChannelOptions options, TimeProvider timeProvider)
{
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
            MessageActivity message => TranslateMessage(message, authenticatedTenantId),
            _ => TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, TeamsIngressActivityKind.Unknown, "unsupported_activity_type")
        };
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

        // Attachment retrieval is deliberately deferred to PR 7. Do not let
        // an attachment-bearing channel post reach the model as its caption.
        if (scope == TeamsConversationScope.Channel && activity.Attachments is { Count: > 0 })
        {
            return TeamsTranslationResult.Rejected(
                TeamsTranslationDisposition.RejectedMalformed,
                TeamsIngressActivityKind.Message,
                "graph_backed_attachment_unsupported");
        }

        string? rootActivityId = null;
        if (scope == TeamsConversationScope.Channel
            && !TeamsTenantEvidenceMappings.TryGetCanonicalChannelRootActivityId(activity.Conversation.Id, out rootActivityId))
        {
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedMalformed, TeamsIngressActivityKind.Message, "invalid_channel_root_identity");
        }

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
            mentions: mentions));
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
}
