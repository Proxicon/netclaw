// -----------------------------------------------------------------------
// <copyright file="TeamsActivityEndpointExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Teams.Api.Activities;
using Microsoft.Teams.Api.Auth;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Activities;
using Microsoft.Teams.Plugins.AspNetCore;
using Microsoft.Teams.Plugins.AspNetCore.Extensions;
using Netclaw.Actors.Channels;
using Netclaw.Channels.Teams;
using Netclaw.Channels.Telemetry;

namespace Netclaw.Daemon.Configuration;

internal static class TeamsActivityEndpointExtensions
{
    internal const string ActivityPath = "/api/messages";
    internal const string RateLimitPolicy = "teams-activity";
    internal const int MaxActivityBodyBytes = 64 * 1024;

    public static void AddTeamsIngress(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(nameof(ChannelType.Teams)).Get<TeamsChannelOptions>()
            ?? new TeamsChannelOptions();
        var registration = TeamsIngressRegistration.Evaluate(options);
        if (!registration.CanActivateSdk)
            return;

        // AddTeams sets the default authentication scheme. This runs before
        // Netclaw's auth registration so the daemon retains its PolicyScheme.
        var appBuilder = App.Builder()
            .AddCredentials(new ClientCredentials(
                options.ClientId!,
                options.ClientSecret!.Value,
                options.TenantId!));
        builder.AddTeams(appBuilder, routing: false);
        builder.Services.AddSingleton<TeamsSdkActivityTranslator>();
        builder.Services.AddSingleton<ITeamsConversationIngressSink, DeferredTeamsConversationIngressSink>();
        builder.Services.AddSingleton<TeamsIngressActorHost>();
        builder.Services.AddSingleton<IHostedService>(serviceProvider =>
            serviceProvider.GetRequiredService<TeamsIngressActorHost>());
        builder.Services.AddRateLimiter(rateLimitOptions =>
        {
            // Matches the signed Mattermost action budget while keeping a
            // defensive per-source cap independent of Teams platform quotas.
            rateLimitOptions.AddPolicy(RateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                    }));
        });
    }

    public static void UseTeamsIngress(this WebApplication app)
    {
        var registration = app.Services.GetRequiredService<TeamsIngressRegistration>();
        if (!registration.CanActivateSdk)
            return;

        var teamsApp = app.UseTeams(routing: false);
        var translator = app.Services.GetRequiredService<TeamsSdkActivityTranslator>();
        var ingress = app.Services.GetRequiredService<TeamsIngressActorHost>();
        teamsApp.OnActivity(async (context, cancellationToken) =>
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventReceived("activity");
            var result = translator.Translate(
                context.Activity,
                ResolveTenantId(context.Activity, context.TenantId));

            if (result.Disposition == TeamsTranslationDisposition.Accepted)
            {
                var routeResult = await ingress.SubmitAsync(result.Activity!, cancellationToken);
                if (routeResult.Disposition != TeamsIngressRouteDisposition.Routed)
                    ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped($"ingress_{routeResult.Disposition.ToString().ToLowerInvariant()}");
            }
            else
            {
                ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered(result.ReasonCode);
            }

            await Task.CompletedTask;
        });
    }

    /// <summary>
    /// Resolves the tenant asserted by the Teams SDK when it supplies one, with
    /// the platform conversation tenant as the authenticated-activity fallback.
    /// Bot Framework service JWTs do not carry a tenant claim for all valid
    /// Teams deliveries. This runs only after <see cref="AspNetCorePlugin"/>
    /// has authenticated the request; the translator then requires the resolved
    /// tenant to match the operator-configured tenant and rejects a conflicting
    /// conversation tenant.
    /// </summary>
    internal static string? ResolveTenantId(IActivity activity, string? sdkTenantId)
    {
        ArgumentNullException.ThrowIfNull(activity);

        return string.IsNullOrWhiteSpace(sdkTenantId)
            ? activity.Conversation?.TenantId
            : sdkTenantId;
    }

    public static void MapTeamsActivityEndpoint(this WebApplication app)
    {
        var registration = app.Services.GetRequiredService<TeamsIngressRegistration>();
        if (!registration.CanActivateSdk)
            return;

        if (((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Any(endpoint => string.Equals(endpoint.RoutePattern.RawText, ActivityPath, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"The Teams activity route '{ActivityPath}' is already registered.");
        }

        app.MapPost(ActivityPath, async (HttpContext context, AspNetCorePlugin plugin, CancellationToken cancellationToken) =>
                await plugin.Do(context, cancellationToken))
            .AddEndpointFilter(async (filterContext, next) =>
            {
                var request = filterContext.HttpContext.Request;
                if (request.ContentLength is > MaxActivityBodyBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                if (request.ContentLength == 0)
                    return Results.BadRequest();

                return await next(filterContext);
            })
            .RequireAuthorization(HostApplicationBuilderExtensions.TeamsTokenAuthConstants.AuthorizationPolicy)
            .RequireRateLimiting(RateLimitPolicy);
    }
}
