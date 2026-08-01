// -----------------------------------------------------------------------
// <copyright file="DurableActivityDispatchState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Serialization;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Reserves one transport activity before a channel binding admits it to a
/// session pipeline. The reservation is the durable duplicate authority.
/// </summary>
public sealed record DurableActivityDispatchReserved(
    string ActivityId,
    string? EvictedActivityId) : INetclawSerializableMessage;

/// <summary>
/// Releases a reservation when the local session pipeline did not admit it.
/// </summary>
public sealed record DurableActivityDispatchReleased(
    string ActivityId) : INetclawSerializableMessage;
