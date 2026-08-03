// -----------------------------------------------------------------------
// <copyright file="TeamsTenantEvidenceMappings.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels.Teams;

/// <summary>
/// Pure mappings recorded by the opt-in tenant transport spike. The daemon
/// uses the attachment classifier before it routes an activity to an actor.
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
            || candidate.Contains("messageid=", StringComparison.Ordinal)
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
                || !IsWellFormedMentionSpan(entity.Text)
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

    private static bool IsWellFormedMentionSpan(string text) =>
        text.StartsWith("<at>", StringComparison.Ordinal)
        && text.EndsWith("</at>", StringComparison.Ordinal)
        && text.Length > "<at></at>".Length;

    public static bool IsUnsupportedGraphBackedAttachmentShell(string? contentType, string? name, string? contentUrl)
        => string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase)
           && string.IsNullOrWhiteSpace(name)
           && string.IsNullOrWhiteSpace(contentUrl);

    /// <summary>
    /// Classifies only bounded, transport-neutral attachment facts. The current
    /// tenant evidence approves no downloadable attachment shape, so every
    /// nonempty attachment fails closed before actor routing.
    /// </summary>
    public static TeamsAttachmentClassificationResult ClassifyAttachment(TeamsAttachmentEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (evidence.HasContent && evidence.HasContentUrl)
            return TeamsAttachmentClassificationResult.UnsupportedAttachmentShape();

        if (IsUnsupportedGraphBackedAttachmentShell(
                evidence.ContentType,
                evidence.HasName ? "attachment" : null,
                evidence.HasContentUrl ? evidence.ContentUrl : null)
            || IsGraphBackedContentReference(evidence.ContentUrl)
            || IsKnownGraphBackedContentType(evidence.ContentType))
        {
            return TeamsAttachmentClassificationResult.GraphBackedUnsupported();
        }

        return TeamsAttachmentClassificationResult.UnsupportedAttachmentShape();
    }

    private static bool IsGraphBackedContentReference(string? contentUrl)
    {
        if (string.IsNullOrWhiteSpace(contentUrl))
            return false;

        if (!Uri.TryCreate(contentUrl, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".sharepoint.us", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".onedrive.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("onedrive.live.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownGraphBackedContentType(string? contentType)
        => string.Equals(contentType, "application/vnd.microsoft.teams.file.download.info", StringComparison.OrdinalIgnoreCase)
           || string.Equals(contentType, "application/vnd.microsoft.teams.file.download.info+json", StringComparison.OrdinalIgnoreCase);
}

public enum TeamsAttachmentClassification
{
    GraphBackedUnsupported,
    UnsupportedAttachmentShape
}

public sealed record TeamsAttachmentClassificationResult(TeamsAttachmentClassification Classification, string? ReasonCode = null)
{
    public static TeamsAttachmentClassificationResult GraphBackedUnsupported()
        => new(TeamsAttachmentClassification.GraphBackedUnsupported, "graph_backed_attachment_unsupported");

    public static TeamsAttachmentClassificationResult UnsupportedAttachmentShape()
        => new(TeamsAttachmentClassification.UnsupportedAttachmentShape, "unsupported_attachment_shape");
}

/// <summary>
/// Attachment facts copied by the daemon from the SDK object. This descriptor
/// never enters actor state, telemetry, persistence, or a model request.
/// </summary>
public sealed record TeamsAttachmentEvidence(
    string? ContentType,
    bool HasName,
    string? ContentUrl,
    bool HasContent,
    bool HasContentUrl);

public sealed record TeamsMentionEvidence(string Type, string MentionedId, string Text);
