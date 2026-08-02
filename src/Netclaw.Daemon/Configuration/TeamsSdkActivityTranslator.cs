// -----------------------------------------------------------------------
// <copyright file="TeamsSdkActivityTranslator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Teams.Api;
using Microsoft.Teams.Api.Activities;
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
            MessageUpdateActivity => TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedPendingTenantEvidence,
                TeamsIngressActivityKind.MessageUpdate,
                "activity_update_pending_persisted_mapping"),
            MessageDeleteActivity => TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedPendingTenantEvidence,
                TeamsIngressActivityKind.MessageDelete,
                "activity_delete_pending_persisted_mapping"),
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

        if (scope == TeamsConversationScope.Channel)
            return TeamsTranslationResult.Rejected(TeamsTranslationDisposition.RejectedPendingTenantEvidence, TeamsIngressActivityKind.Message, "channel_root_mapping_pending_tenant_evidence");

        var trust = new TeamsIngressTrustContext(
            TrustAudience.Public,
            PrincipalClassification.UntrustedExternal,
            TrustBoundary.Public,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            activity.From.Id,
            authenticatedTenantId,
            activity.Conversation.Id,
            scope.Value,
            activity.Id,
            timeProvider.GetUtcNow(),
            activity.Timestamp is { } timestamp ? new DateTimeOffset(timestamp.ToUniversalTime()) : null);

        // Mention extraction is a PR 4 concern; false cannot grant a channel pass.
        return TeamsTranslationResult.Accepted(new TeamsInboundActivity(trust, activity.Text, null, false));
    }
}
