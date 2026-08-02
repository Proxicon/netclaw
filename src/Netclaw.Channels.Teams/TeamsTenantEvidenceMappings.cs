// -----------------------------------------------------------------------
// <copyright file="TeamsTenantEvidenceMappings.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels.Teams;

/// <summary>
/// Pure mappings recorded by the opt-in tenant transport spike. These helpers
/// are deliberately not connected to production channel routing; PR 4 owns
/// the policy and durable activity-to-root index that will consume them.
/// </summary>
public static class TeamsTenantEvidenceMappings
{
    private const string ThreadMessageIdSeparator = ";messageid=";

    public static bool TryGetCanonicalChannelRootActivityId(string? conversationId, out string rootActivityId)
    {
        rootActivityId = string.Empty;
        if (string.IsNullOrWhiteSpace(conversationId))
            return false;

        var separatorIndex = conversationId.LastIndexOf(ThreadMessageIdSeparator, StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex != conversationId.IndexOf(ThreadMessageIdSeparator, StringComparison.Ordinal))
            return false;

        var candidate = conversationId[(separatorIndex + ThreadMessageIdSeparator.Length)..];
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Contains(';', StringComparison.Ordinal)
            || !TeamsSessionIdentifierCodec.IsValidActivityIdentifier(candidate))
            return false;

        rootActivityId = candidate;
        return true;
    }

    /// <summary>
    /// Removes only mention spans whose qualified identity matches both the
    /// activity recipient and the configured bot. Display names are not used.
    /// </summary>
    public static string RemoveQualifiedBotMentions(
        string text,
        IEnumerable<TeamsMentionEvidence> entities,
        string recipientId,
        string configuredBotId)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(entities);

        var qualifiedBotId = $"28:{configuredBotId}";
        var result = text;
        foreach (var entity in entities)
        {
            if (!string.Equals(entity.Type, "mention", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(entity.Text)
                || !string.Equals(entity.MentionedId, recipientId, StringComparison.Ordinal)
                || !string.Equals(entity.MentionedId, qualifiedBotId, StringComparison.Ordinal))
            {
                continue;
            }

            var spanIndex = result.IndexOf(entity.Text, StringComparison.Ordinal);
            if (spanIndex >= 0)
                result = result.Remove(spanIndex, entity.Text.Length);
        }

        return result;
    }

    public static bool IsUnsupportedGraphBackedAttachmentShell(string? contentType, string? name, string? contentUrl)
        => string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase)
           && string.IsNullOrWhiteSpace(name)
           && string.IsNullOrWhiteSpace(contentUrl);
}

public sealed record TeamsMentionEvidence(string Type, string MentionedId, string Text);
