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
using Microsoft.Extensions.Logging;
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
        builder.Services.AddSingleton<ITeamsSdkReplyOperations, TeamsSdkReplyOperations>();
        builder.Services.AddSingleton<ITeamsReplyClient, TeamsSdkReplyClient>();
        builder.Services.AddSingleton<TeamsOutputRenderer>();
        builder.Services.AddSingleton<ITeamsConversationIngressSink, TeamsActorConversationIngressSink>();
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
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TeamsActivityEndpointExtensions));
        teamsApp.OnActivity(async (context, cancellationToken) =>
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventReceived("activity");
            var result = translator.Translate(
                context.Activity,
                ResolveTenantId(context.Activity, context.TenantId));

            if (result.Disposition == TeamsTranslationDisposition.Accepted)
            {
                RecordTranslationTelemetry(result);
                if (result.ApprovalAction is { } approvalAction)
                {
                    var approvalResult = await ingress.SubmitApprovalAsync(approvalAction, cancellationToken);
                    ChannelTelemetry.For(ChannelType.Teams).RecordExtra(
                        $"approval_action_{approvalResult.Disposition.ToString().ToLowerInvariant()}");
                }
                else
                {
                    var routeResult = await ingress.SubmitAsync(result.Activity!, cancellationToken);
                    if (routeResult.Disposition != TeamsIngressRouteDisposition.Routed)
                        ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped($"ingress_{routeResult.Disposition.ToString().ToLowerInvariant()}");
                }
            }
            else
            {
                ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered(result.ReasonCode);
                RecordRejectedAttachmentDiagnostic(logger, translator, context.Activity, ResolveTenantId(context.Activity, context.TenantId), result);
                RecordTranslationTelemetry(result);
            }

            await Task.CompletedTask;
        });
    }

    internal static void RecordTranslationTelemetry(TeamsTranslationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var telemetryCode = result.ReasonCode switch
        {
            "plain_text_accepted" => "plain_text_accepted",
            "teams_text_rendering_wrapper_ignored" => "teams_text_rendering_wrapper_ignored",
            "graph_backed_attachment_unsupported" => "attachment_graph_backed_rejected",
            "unsupported_attachment_shape" => "attachment_shape_rejected",
            "attachment_malformed_rejected" => "attachment_malformed_rejected",
            _ => null
        };
        if (telemetryCode is not null)
            ChannelTelemetry.For(ChannelType.Teams).RecordExtra(telemetryCode);
    }

    private static void RecordRejectedAttachmentDiagnostic(
        ILogger logger,
        TeamsSdkActivityTranslator translator,
        IActivity activity,
        string? authenticatedTenantId,
        TeamsTranslationResult result)
    {
        var diagnostic = translator.DescribeRejectedAttachment(activity, authenticatedTenantId, result);
        if (diagnostic is null)
            return;

        logger.LogWarning(
            "Teams attachment diagnostic: scope={Scope}; tenant_match={TenantMatch}; team_match={TeamMatch}; channel_match={ChannelMatch}; sender_match={SenderMatch}; mentioned={Mentioned}; root_activity_valid={RootActivityValid}; audience_valid={AudienceValid}; policy_reason={PolicyReason}; attachment_count={AttachmentCount}; attachment_content_type={AttachmentContentType}; attachment_content_kind={AttachmentContentKind}; attachment_content_exists={AttachmentContentExists}; attachment_content_url_exists={AttachmentContentUrlExists}; attachment_reference_exists={AttachmentReferenceExists}; attachment_graph_reference_exists={AttachmentGraphReferenceExists}; attachment_name_exists={AttachmentNameExists}; attachment_thumbnail_exists={AttachmentThumbnailExists}; channel_data_exists={ChannelDataExists}; attachment_html_rendering_markup_exists={AttachmentHtmlRenderingMarkupExists}; attachment_html_envelope_kind={AttachmentHtmlEnvelopeKind}; attachment_html_anchor_exists={AttachmentHtmlAnchorExists}; attachment_html_href_exists={AttachmentHtmlHrefExists}; attachment_html_closing_envelope_exists={AttachmentHtmlClosingEnvelopeExists}; mention_count={MentionCount}; reply_to_id_exists={ReplyToIdExists}",
            diagnostic.Scope,
            diagnostic.TenantMatch,
            diagnostic.TeamMatch,
            diagnostic.ChannelMatch,
            diagnostic.SenderMatch,
            diagnostic.Mentioned,
            diagnostic.RootActivityValid,
            diagnostic.AudienceValid,
            diagnostic.PolicyReason,
            diagnostic.AttachmentCount,
            diagnostic.AttachmentContentType,
            diagnostic.AttachmentContentKind,
            diagnostic.AttachmentContentExists,
            diagnostic.AttachmentContentUrlExists,
            diagnostic.AttachmentReferenceExists,
            diagnostic.AttachmentGraphReferenceExists,
            diagnostic.AttachmentNameExists,
            diagnostic.AttachmentThumbnailExists,
            diagnostic.ChannelDataExists,
            diagnostic.AttachmentHtmlRenderingMarkupExists,
            diagnostic.AttachmentHtmlEnvelopeKind,
            diagnostic.AttachmentHtmlAnchorExists,
            diagnostic.AttachmentHtmlHrefExists,
            diagnostic.AttachmentHtmlClosingEnvelopeExists,
            diagnostic.MentionCount,
            diagnostic.ReplyToIdExists);
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
