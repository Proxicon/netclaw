// -----------------------------------------------------------------------
// <copyright file="TeamsIngressContracts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;

namespace Netclaw.Channels.Teams;

/// <summary>
/// Immutable, SDK-free trust context that the future Teams translator supplies
/// only after its authenticated HTTP boundary has validated required identity.
/// </summary>
public sealed record TeamsIngressTrustContext
{
    public TeamsIngressTrustContext(
        TrustAudience audience,
        PrincipalClassification principal,
        TrustBoundary boundary,
        SourceProvenance provenance,
        string senderId,
        string tenantId,
        string conversationId,
        TeamsConversationScope scope,
        string activityId,
        DateTimeOffset receivedAtUtc)
    {
        Audience = ValidateEnum(audience, nameof(audience));
        Principal = ValidateEnum(principal, nameof(principal));
        Boundary = string.IsNullOrWhiteSpace(boundary.Value)
            ? throw new ArgumentException("Trust boundary is required.", nameof(boundary))
            : boundary;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        SenderId = RequireValue(senderId, nameof(senderId));
        TenantId = RequireValue(tenantId, nameof(tenantId));
        ConversationId = RequireValue(conversationId, nameof(conversationId));
        Scope = ValidateScope(scope);
        ActivityId = RequireValue(activityId, nameof(activityId));
        ReceivedAtUtc = receivedAtUtc == default
            ? throw new ArgumentException("Received timestamp is required.", nameof(receivedAtUtc))
            : receivedAtUtc;
    }

    public TrustAudience Audience { get; }

    public PrincipalClassification Principal { get; }

    public TrustBoundary Boundary { get; }

    public SourceProvenance Provenance { get; }

    public string SenderId { get; }

    public string TenantId { get; }

    public string ConversationId { get; }

    public TeamsConversationScope Scope { get; }

    public string ActivityId { get; }

    public DateTimeOffset ReceivedAtUtc { get; }

    private static T ValidateEnum<T>(T value, string parameterName)
        where T : struct, Enum
        => Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, value, "Unsupported value.");

    private static TeamsConversationScope ValidateScope(TeamsConversationScope scope)
        => scope is TeamsConversationScope.Personal or TeamsConversationScope.Channel
            ? scope
            : throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported Teams conversation scope.");

    private static string RequireValue(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A nonblank value is required.", parameterName)
            : value;
}

/// <summary>
/// Sanitized metadata only. Download URLs, authorization data, and SDK objects
/// deliberately remain outside this contract until the attachment spike proves
/// a supported authenticated retrieval shape.
/// </summary>
public sealed record TeamsAttachmentMetadata
{
    public TeamsAttachmentMetadata(string name, string? contentType, long? declaredSizeBytes)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Attachment name is required.", nameof(name))
            : name;
        ContentType = contentType;
        DeclaredSizeBytes = declaredSizeBytes is < 0
            ? throw new ArgumentOutOfRangeException(nameof(declaredSizeBytes), "Attachment size cannot be negative.")
            : declaredSizeBytes;
    }

    public string Name { get; }

    public string? ContentType { get; }

    public long? DeclaredSizeBytes { get; }
}

public sealed record TeamsReplyMetadata(
    string? ReplyToActivityId,
    string? RootActivityId);

public sealed record TeamsInboundActivity
{
    public TeamsInboundActivity(
        TeamsIngressTrustContext trust,
        string text,
        TeamsReplyMetadata? reply = null,
        bool isMentioned = false,
        ImmutableArray<TeamsAttachmentMetadata> attachments = default)
    {
        Trust = trust ?? throw new ArgumentNullException(nameof(trust));
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Reply = reply;
        IsMentioned = isMentioned;
        Attachments = attachments.IsDefault ? [] : attachments;
    }

    public TeamsIngressTrustContext Trust { get; }

    public string Text { get; }

    public TeamsReplyMetadata? Reply { get; }

    public bool IsMentioned { get; }

    public ImmutableArray<TeamsAttachmentMetadata> Attachments { get; }
}

/// <summary>
/// SDK-free destination captured only after a Teams activity passes the future
/// authentication and ACL gates. It deliberately stores no credentials or
/// serialized SDK conversation reference.
/// </summary>
public sealed record TeamsOutboundDestination
{
    public TeamsOutboundDestination(
        string tenantId,
        string conversationId,
        TeamsConversationScope scope,
        string? teamId = null,
        string? channelId = null,
        string? userId = null)
    {
        TenantId = RequireValue(tenantId, nameof(tenantId));
        ConversationId = RequireValue(conversationId, nameof(conversationId));
        Scope = scope is TeamsConversationScope.Personal or TeamsConversationScope.Channel
            ? scope
            : throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported Teams conversation scope.");
        TeamId = teamId;
        ChannelId = channelId;
        UserId = userId;
    }

    public string TenantId { get; }

    public string ConversationId { get; }

    public TeamsConversationScope Scope { get; }

    public string? TeamId { get; }

    public string? ChannelId { get; }

    public string? UserId { get; }

    private static string RequireValue(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A nonblank value is required.", parameterName)
            : value;
}

public sealed record TeamsOutboundMessage
{
    public TeamsOutboundMessage(
        TeamsOutboundDestination destination,
        string text,
        string idempotencyKey,
        string correlationId,
        string? replyToActivityId = null,
        string? updateActivityId = null,
        ImmutableArray<TeamsAttachmentMetadata> attachments = default)
    {
        Destination = destination ?? throw new ArgumentNullException(nameof(destination));
        Text = string.IsNullOrWhiteSpace(text) ? throw new ArgumentException("Text is required.", nameof(text)) : text;
        ReplyToActivityId = replyToActivityId;
        UpdateActivityId = updateActivityId;
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey)) : idempotencyKey;
        CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? throw new ArgumentException("Correlation ID is required.", nameof(correlationId)) : correlationId;
        Attachments = attachments.IsDefault ? [] : attachments;
    }

    public TeamsOutboundDestination Destination { get; }

    public string Text { get; }

    public string? ReplyToActivityId { get; }

    public string? UpdateActivityId { get; }

    public string IdempotencyKey { get; }

    public string CorrelationId { get; }

    public ImmutableArray<TeamsAttachmentMetadata> Attachments { get; }
}
