// -----------------------------------------------------------------------
// <copyright file="TeamsChannelOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels;
using Netclaw.Configuration;
using System.Text.Json.Serialization;

namespace Netclaw.Channels.Teams;

public enum TeamsAuthenticationMode
{
    ClientSecret
}

/// <summary>
/// Configuration for the disabled-by-default Microsoft Teams integration.
/// ClientSecret is loaded only from the existing secrets overlay or NETCLAW_
/// environment variables and is never included in the normal configuration schema.
/// </summary>
public sealed class TeamsChannelOptions : IRemoteChatChannelOptions
{
    public bool Enabled { get; init; }

    public string? TenantId { get; init; }

    public string? ClientId { get; init; }

    public TeamsAuthenticationMode AuthenticationMode { get; init; } = TeamsAuthenticationMode.ClientSecret;

    [JsonIgnore]
    public SensitiveString? ClientSecret { get; init; }

    public bool AllowDirectMessages { get; init; }

    public bool MentionOnly { get; init; } = true;

    public string[] AllowedTeamIds { get; init; } = [];

    public string[] AllowedChannelIds { get; init; } = [];

    public string[] AllowedUserIds { get; init; } = [];

    /// <summary>
    /// Per-channel audience overrides. Keys use canonical team/channel or team
    /// identities; values are personal, team, or public.
    /// </summary>
    public Dictionary<string, string> ChannelAudiences { get; init; } = new(StringComparer.Ordinal);
}
