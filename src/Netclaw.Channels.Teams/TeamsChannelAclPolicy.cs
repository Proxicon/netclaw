// -----------------------------------------------------------------------
// <copyright file="TeamsChannelAclPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Configuration;

namespace Netclaw.Channels.Teams;

public enum TeamsChannelPolicyDisposition
{
    Allowed,
    Ignored,
    Denied
}

public sealed record TeamsChannelPolicyDecision(
    TeamsChannelPolicyDisposition Disposition,
    string ReasonCode,
    ChannelAclDecision? Acl = null);

/// <summary>
/// Applies the final default-deny policy for a Teams channel activity.
/// </summary>
public static class TeamsChannelAclPolicy
{
    public static TeamsChannelPolicyDecision Evaluate(TeamsInboundActivity activity, TeamsChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(options);

        if (activity.Trust.Scope != TeamsConversationScope.Channel)
            return Deny("unsupported_scope");
        if (string.IsNullOrWhiteSpace(options.TenantId)
            || !string.Equals(activity.Trust.TenantId, options.TenantId, StringComparison.Ordinal))
            return Deny("configured_tenant_mismatch");
        if (string.IsNullOrWhiteSpace(activity.TeamId))
            return Deny("missing_team_id");
        if (string.IsNullOrWhiteSpace(activity.ChannelId))
            return Deny("missing_channel_id");
        if (options.AllowedTeamIds.Length == 0 || !options.AllowedTeamIds.Contains(activity.TeamId, StringComparer.Ordinal))
            return Deny(AclDenyReasons.ChannelNotAllowed);
        if (options.AllowedChannelIds.Length == 0 || !options.AllowedChannelIds.Contains(activity.ChannelId, StringComparer.Ordinal))
            return Deny(AclDenyReasons.ChannelNotAllowed);
        if (string.IsNullOrWhiteSpace(activity.Trust.SenderId))
            return Deny(AclDenyReasons.MissingUserId);
        if (options.AllowedUserIds.Length > 0 && !options.AllowedUserIds.Contains(activity.Trust.SenderId, StringComparer.Ordinal))
            return Deny(AclDenyReasons.UserNotAllowed);
        if (!TeamsSessionIdentifierCodec.IsValidActivityIdentifier(activity.Trust.ActivityId)
            || activity.Reply?.RootActivityId is not { } rootActivityId
            || !TeamsSessionIdentifierCodec.IsValidActivityIdentifier(rootActivityId))
            return Deny("invalid_channel_root_identity");

        if (activity.Kind == TeamsIngressActivityKind.Message && options.MentionOnly && !activity.IsMentioned)
            return new TeamsChannelPolicyDecision(TeamsChannelPolicyDisposition.Ignored, "channel_unmentioned");

        if (!TryResolveAudience(
                options.ChannelAudiences,
                options.ChannelAudienceOverrides,
                activity.TeamId,
                activity.ChannelId,
                out var audience))
            return Deny("invalid_channel_audience");
        var isExplicitUser = options.AllowedUserIds.Contains(activity.Trust.SenderId, StringComparer.Ordinal);
        return new TeamsChannelPolicyDecision(
            TeamsChannelPolicyDisposition.Allowed,
            "channel_acl_allowed",
            ChannelAclDecision.Allow(
                audience,
                isExplicitUser ? PrincipalClassification.TrustedInternal : PrincipalClassification.UntrustedExternal,
                new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
                {
                    SourceKind = new SourceKind("teams"),
                    SourceScope = new SourceScope("teams-channel")
                }));
    }

    private static TeamsChannelPolicyDecision Deny(string reason) =>
        new(TeamsChannelPolicyDisposition.Denied, reason);

    private static bool TryResolveAudience(
        IReadOnlyDictionary<string, string> audiences,
        IReadOnlyList<TeamsChannelAudienceOverride> structuredOverrides,
        string teamId,
        string channelId,
        out TrustAudience audience)
    {
        var key = $"{teamId}/{channelId}";
        if (!TryFindStructuredOverride(
                structuredOverrides,
                teamId,
                channelId,
                exactChannel: true,
                out var found,
                out var value))
        {
            audience = default;
            return false;
        }
        if (found || audiences.TryGetValue(key, out value))
            return SecurityPolicyDefaults.TryParseAudience(value, out audience);

        if (!TryFindStructuredOverride(
                structuredOverrides,
                teamId,
                channelId,
                exactChannel: false,
                out found,
                out value))
        {
            audience = default;
            return false;
        }
        if (found || audiences.TryGetValue(teamId, out value))
            return SecurityPolicyDefaults.TryParseAudience(value, out audience);

        audience = TrustAudience.Public;
        return true;
    }

    private static bool TryFindStructuredOverride(
        IReadOnlyList<TeamsChannelAudienceOverride> overrides,
        string teamId,
        string channelId,
        bool exactChannel,
        out bool found,
        out string value)
    {
        found = false;
        value = string.Empty;
        foreach (var audienceOverride in overrides)
        {
            if (!string.Equals(audienceOverride.TeamId, teamId, StringComparison.Ordinal))
                continue;

            var channelMatches = exactChannel
                ? string.Equals(audienceOverride.ChannelId, channelId, StringComparison.Ordinal)
                : string.IsNullOrWhiteSpace(audienceOverride.ChannelId);
            if (!channelMatches)
                continue;

            if (found)
                return false;

            found = true;
            value = audienceOverride.Audience;
        }

        return true;
    }
}
