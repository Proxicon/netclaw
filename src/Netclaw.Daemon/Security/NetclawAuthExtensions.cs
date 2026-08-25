// -----------------------------------------------------------------------
// <copyright file="NetclawAuthExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;

namespace Netclaw.Daemon.Security;

/// <summary>
/// Registers the Netclaw multi-scheme auth pipeline. The selector routes the
/// mapped Teams endpoint to AzureAd, a bearer token to DeviceBearer, and all
/// other requests to Loopback (local operator).
/// </summary>
internal static class NetclawAuthExtensions
{
    internal static IServiceCollection AddNetclawAuthSchemes(this IServiceCollection services, DaemonConfig daemonConfig)
    {
        services.TryAddSingleton(daemonConfig);
        services
            .AddAuthentication("AuthSelector")
            .AddPolicyScheme("AuthSelector", "Bearer or Loopback selector", options =>
            {
                options.ForwardDefaultSelector = ctx =>
                    ctx.GetEndpoint() is not null
                    && ctx.Request.Path.Equals(TeamsActivityEndpointExtensions.ActivityPath, StringComparison.OrdinalIgnoreCase)
                        ? TeamsActivityEndpointExtensions.AuthenticationScheme
                        : ctx.Request.Headers.ContainsKey("Authorization") &&
                    ctx.Request.Headers.Authorization.ToString()
                        .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? DeviceTokenAuthenticationHandler.SchemeName
                        : LoopbackAuthenticationHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, LoopbackAuthenticationHandler>(
                LoopbackAuthenticationHandler.SchemeName, _ => { })
            .AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>(
                DeviceTokenAuthenticationHandler.SchemeName, _ => { });

        return services;
    }
}
