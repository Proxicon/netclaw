// -----------------------------------------------------------------------
// <copyright file="PendingApprovalPromptState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Serialization;
using Netclaw.Configuration;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Journaled by a channel binding actor after it successfully posts an approval
/// prompt and captures the transport-specific locator needed to redraw that
/// prompt after a later cold spawn.
/// </summary>
public sealed record PendingApprovalPromptTracked : INetclawSerializableMessage
{
    /// <summary>
    /// Hard cap on persisted display text. Sized to match the most permissive
    /// renderer cap (Mattermost 12000) so the journal never carries bytes no
    /// channel could ever render. Per-channel render-time truncation (Slack
    /// 2500, Discord 1700, Mattermost 12000) still applies on top.
    /// </summary>
    public const int MaxPersistedDisplayTextChars = 12_000;

    public string CallId { get; init; } = string.Empty;

    public string? RequesterSenderId { get; init; }

    public PrincipalClassification? RequesterPrincipal { get; init; }

    public IReadOnlyList<string> OptionKeys { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Opaque transport-specific prompt locator: Slack message ts, Discord
    /// message id, or Mattermost post id.
    /// </summary>
    public string PromptId { get; init; } = string.Empty;

    /// <summary>
    /// Tool name from the original <c>ToolInteractionRequest</c>. Null on journal
    /// entries written before this field was added — the cold-spawn redraw then
    /// falls back to the generic resolution banner.
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// Display text from the original <c>ToolInteractionRequest</c>, truncated
    /// to <see cref="MaxPersistedDisplayTextChars"/> before persistence. Null on
    /// journal entries written before this field was added.
    /// </summary>
    public string? DisplayText { get; init; }
}

/// <summary>
/// Journaled by a channel binding actor when a previously tracked approval
/// prompt is no longer pending locally.
/// </summary>
public sealed record PendingApprovalPromptCleared : INetclawSerializableMessage
{
    public string CallId { get; init; } = string.Empty;
}

/// <summary>
/// Journaled by the Teams binding before it sends an approval card. The nonce
/// hash permits restart-safe validation without retaining the bearer value.
/// </summary>
public sealed record TeamsApprovalPendingCreated : INetclawSerializableMessage
{
    public string CallId { get; init; } = string.Empty;

    public string CorrelationId { get; init; } = string.Empty;

    public string NonceHash { get; init; } = string.Empty;

    public string? RequesterSenderId { get; init; }

    public PrincipalClassification? RequesterPrincipal { get; init; }

    public long ExpiresAtUnixMilliseconds { get; init; }
}

/// <summary>
/// Journaled after Teams creates the card. The actor uses this locator for a
/// terminal update. It never trusts an invoke-supplied message identifier.
/// </summary>
public sealed record TeamsApprovalCardDelivered : INetclawSerializableMessage
{
    public string CorrelationId { get; init; } = string.Empty;

    public string PromptId { get; init; } = string.Empty;
}

/// <summary>
/// Journaled before the binding forwards an approval decision to the existing
/// session workflow. A later card update cannot reopen this terminal state.
/// </summary>
public sealed record TeamsApprovalConsumed : INetclawSerializableMessage
{
    public string CorrelationId { get; init; } = string.Empty;

    public string Decision { get; init; } = string.Empty;

    public long ConsumedAtUnixMilliseconds { get; init; }
}

/// <summary>
/// Stores the complete bounded Teams approval state in a binding snapshot.
/// The state has no raw nonce, card payload, service URL, or tool arguments.
/// </summary>
public sealed record TeamsApprovalSnapshotEntry
{
    public string CallId { get; init; } = string.Empty;

    public string CorrelationId { get; init; } = string.Empty;

    public string NonceHash { get; init; } = string.Empty;

    public string? RequesterSenderId { get; init; }

    public PrincipalClassification? RequesterPrincipal { get; init; }

    public long ExpiresAtUnixMilliseconds { get; init; }

    public string? PromptId { get; init; }

    public string? Decision { get; init; }
}
