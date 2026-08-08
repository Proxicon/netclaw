// -----------------------------------------------------------------------
// <copyright file="DurableActivityDispatchState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Serialization;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Reserves one activity fingerprint before a channel binding admits it to a
/// session pipeline. The reservation is the durable duplicate authority.
/// </summary>
public sealed record DurableActivityDispatchReserved(
    string ActivityFingerprint,
    string? EvictedActivityFingerprint) : INetclawSerializableMessage;

/// <summary>
/// Releases a reservation when the local session pipeline did not admit it.
/// </summary>
public sealed record DurableActivityDispatchReleased(
    string ActivityFingerprint) : INetclawSerializableMessage;

/// <summary>
/// Stores the ordered, bounded activity fingerprint state for a channel
/// binding. The snapshot permits journal compaction without losing retention.
/// </summary>
public sealed record DurableActivityDispatchSnapshot(
    IReadOnlyList<string> ActivityFingerprints) : INetclawSerializableMessage
{
    /// <summary>
    /// Teams bindings retain this approval state when snapshot compaction
    /// removes the corresponding journal events. Other channel bindings use
    /// the empty default.
    /// </summary>
    public IReadOnlyList<TeamsApprovalSnapshotEntry> TeamsApprovals { get; init; } = Array.Empty<TeamsApprovalSnapshotEntry>();

    /// <summary>
    /// The current Teams proactive destination. It contains only bounded routing
    /// data and is null until an allowed Teams activity captures one.
    /// </summary>
    public TeamsProactiveDestinationSnapshotEntry? TeamsDestination { get; init; }

    /// <summary>
    /// Bounded per-reminder delivery state retained by a Teams binding. Entries
    /// are keyed by a generic reminder delivery key, never reminder content.
    /// </summary>
    public IReadOnlyList<TeamsProactiveDeliverySnapshotEntry> TeamsProactiveDeliveries { get; init; } = Array.Empty<TeamsProactiveDeliverySnapshotEntry>();
}

/// <summary>
/// Captures a validated Teams destination after an allowed inbound activity.
/// SDK objects, tokens, message bodies, and headers are deliberately absent.
/// </summary>
public sealed record TeamsProactiveDestinationCaptured : INetclawSerializableMessage
{
    public string TenantId { get; init; } = string.Empty;

    public string ConversationId { get; init; } = string.Empty;

    public int Scope { get; init; }

    public string ServiceUrl { get; init; } = string.Empty;

    public string? RootActivityId { get; init; }

    public string? TeamId { get; init; }

    public string? ChannelId { get; init; }

    public string? UserId { get; init; }
}

/// <summary>
/// Removes the current Teams destination after a permanent outbound failure.
/// The event applies only to its owning session-binding actor.
/// </summary>
public sealed record TeamsProactiveDestinationInvalidated : INetclawSerializableMessage;

/// <summary>
/// Durable state transition for a single Teams reminder delivery attempt.
/// </summary>
public sealed record TeamsProactiveDeliveryRecorded : INetclawSerializableMessage
{
    public string DeliveryKey { get; init; } = string.Empty;

    public int State { get; init; }

    public string? EvictedDeliveryKey { get; init; }
}

/// <summary>
/// Snapshot representation of the non-secret Teams destination.
/// </summary>
public sealed record TeamsProactiveDestinationSnapshotEntry
{
    public string TenantId { get; init; } = string.Empty;

    public string ConversationId { get; init; } = string.Empty;

    public int Scope { get; init; }

    public string ServiceUrl { get; init; } = string.Empty;

    public string? RootActivityId { get; init; }

    public string? TeamId { get; init; }

    public string? ChannelId { get; init; }

    public string? UserId { get; init; }
}

/// <summary>
/// Snapshot representation of one bounded reminder delivery state transition.
/// </summary>
public sealed record TeamsProactiveDeliverySnapshotEntry
{
    public string DeliveryKey { get; init; } = string.Empty;

    public int State { get; init; }
}

/// <summary>
/// Stores the Teams channel conversation owner's reversible routing target for
/// a fixed activity fingerprint. Session IDs are canonical encoded values; raw
/// Teams identifiers are never persisted by this index.
/// </summary>
public sealed record DurableTeamsChannelActivityMapped(
    string ActivityFingerprint,
    string SessionId,
    string? EvictedActivityFingerprint) : INetclawSerializableMessage;

/// <summary>
/// Snapshot of the bounded Teams channel activity routing index.
/// </summary>
public sealed record DurableTeamsChannelActivityIndexSnapshot(
    IReadOnlyList<DurableTeamsChannelActivityMapped> Entries) : INetclawSerializableMessage;
