// -----------------------------------------------------------------------
// <copyright file="TeamsPersonalRoutingActors.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;

namespace Netclaw.Channels.Teams;

/// <summary>
/// Dependencies shared by the Teams personal conversation and binding actors.
/// </summary>
public sealed record TeamsConversationDependencies(
    TeamsChannelOptions Options,
    ISessionPipeline Pipeline,
    TimeProvider TimeProvider);

public sealed record TeamsConversationIngress(
    TeamsInboundActivity Activity,
    CancellationToken CancellationToken) : INoSerializationVerificationNeeded;

public sealed record TeamsBindingIngress(
    TeamsInboundActivity Activity,
    CancellationToken CancellationToken) : INoSerializationVerificationNeeded;

public enum TeamsBindingRouteDisposition
{
    Accepted,
    Duplicate,
    Denied,
    Unavailable,
    Failed,
    Cancelled
}

public sealed record TeamsBindingRouteResult(TeamsBindingRouteDisposition Disposition);

/// <summary>
/// Builds actor names from a canonical Teams session ID. The codec constructs
/// the session ID before this type applies the repository actor-name escape.
/// </summary>
public static class TeamsActorNames
{
    public static string Conversation(SessionId sessionId) =>
        "teams-conversation-" + Uri.EscapeDataString(sessionId.Value);

    public static string Binding(SessionId sessionId) =>
        "teams-binding-" + Uri.EscapeDataString(sessionId.Value);
}

/// <summary>
/// Resolves a personal conversation actor from a validated Teams activity.
/// This sink has no durable duplicate state and does not dispatch a session.
/// </summary>
public sealed class TeamsActorConversationIngressSink : ITeamsConversationIngressSink
{
    private static readonly TimeSpan RouteTimeout = TimeSpan.FromSeconds(10);

    private readonly ActorSystem _actorSystem;
    private readonly TeamsChannelOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly object _creationLock = new();
    private readonly Dictionary<string, IActorRef> _conversations = new(StringComparer.Ordinal);

    public TeamsActorConversationIngressSink(
        ActorSystem actorSystem,
        TeamsChannelOptions options,
        IServiceProvider serviceProvider)
    {
        _actorSystem = actorSystem ?? throw new ArgumentNullException(nameof(actorSystem));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public async ValueTask<TeamsIngressSinkResult> RouteAsync(
        TeamsInboundActivity activity,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return TeamsIngressSinkResult.Cancelled;

        if (activity.Trust.Scope != TeamsConversationScope.Personal)
            return TeamsIngressSinkResult.Unavailable;

        if (!TeamsSessionIdentifierCodec.TryCreatePersonal(
                activity.Trust.TenantId,
                activity.Trust.ConversationId,
                out var sessionId,
                out _))
        {
            return TeamsIngressSinkResult.Denied;
        }

        if (!TeamsPersonalAclPolicy.Evaluate(activity, _options).IsAllowed)
            return TeamsIngressSinkResult.Denied;

        var conversation = GetOrCreateConversation(sessionId, new TeamsConversationDependencies(
            _options,
            _serviceProvider.GetRequiredService<ISessionPipeline>(),
            _serviceProvider.GetRequiredService<TimeProvider>()));
        try
        {
            var result = await conversation.Ask<TeamsBindingRouteResult>(
                new TeamsConversationIngress(activity, cancellationToken),
                RouteTimeout,
                cancellationToken);
            return result.Disposition switch
            {
                TeamsBindingRouteDisposition.Accepted => TeamsIngressSinkResult.Accepted,
                TeamsBindingRouteDisposition.Duplicate => TeamsIngressSinkResult.Duplicate,
                TeamsBindingRouteDisposition.Denied => TeamsIngressSinkResult.Denied,
                TeamsBindingRouteDisposition.Unavailable => TeamsIngressSinkResult.Unavailable,
                TeamsBindingRouteDisposition.Cancelled => TeamsIngressSinkResult.Cancelled,
                TeamsBindingRouteDisposition.Failed => TeamsIngressSinkResult.Failed,
                _ => throw new ArgumentOutOfRangeException()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TeamsIngressSinkResult.Cancelled;
        }
        catch (AskTimeoutException)
        {
            return TeamsIngressSinkResult.Unavailable;
        }
        catch (Exception)
        {
            return TeamsIngressSinkResult.Failed;
        }
    }

    private IActorRef GetOrCreateConversation(
        SessionId sessionId,
        TeamsConversationDependencies dependencies)
    {
        lock (_creationLock)
        {
            if (_conversations.TryGetValue(sessionId.Value, out var existing))
                return existing;

            var actor = _actorSystem.ActorOf(
                TeamsConversationActor.CreateProps(sessionId, dependencies),
                TeamsActorNames.Conversation(sessionId));
            _conversations.Add(sessionId.Value, actor);
            return actor;
        }
    }
}

/// <summary>
/// Owns deterministic binding-child lookup for one canonical Teams personal
/// conversation. It owns no pipeline work and no durable duplicate state.
/// </summary>
public sealed class TeamsConversationActor : ReceiveActor
{
    private static readonly TimeSpan BindingRouteTimeout = TimeSpan.FromSeconds(10);

    private readonly SessionId _sessionId;
    private readonly TeamsConversationDependencies _dependencies;
    private readonly ILoggingAdapter _log;

    public TeamsConversationActor(SessionId sessionId, TeamsConversationDependencies dependencies)
    {
        _sessionId = sessionId;
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        _log = Context.GetLogger().WithContext("Adapter", "teams");

        ReceiveAsync<TeamsConversationIngress>(HandleIngressAsync);
    }

    public static Props CreateProps(SessionId sessionId, TeamsConversationDependencies dependencies) =>
        Props.Create(() => new TeamsConversationActor(sessionId, dependencies));

    private async Task HandleIngressAsync(TeamsConversationIngress ingress)
    {
        var replyTo = Sender;
        if (ingress.CancellationToken.IsCancellationRequested)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Cancelled));
            return;
        }

        if (ingress.Activity.Trust.Scope != TeamsConversationScope.Personal
            || !TeamsSessionIdentifierCodec.TryCreatePersonal(
                ingress.Activity.Trust.TenantId,
                ingress.Activity.Trust.ConversationId,
                out var expectedSessionId,
                out _)
            || expectedSessionId != _sessionId)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Unavailable));
            return;
        }

        if (!TeamsPersonalAclPolicy.Evaluate(ingress.Activity, _dependencies.Options).IsAllowed)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("personal_acl_denied");
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Denied));
            return;
        }

        var binding = Context.Child(TeamsActorNames.Binding(_sessionId));
        if (binding.IsNobody())
        {
            binding = Context.ActorOf(
                TeamsSessionBindingActor.CreateProps(_sessionId, _dependencies),
                TeamsActorNames.Binding(_sessionId));
            _log.Info("personal_binding_created");
        }

        try
        {
            var result = await binding.Ask<TeamsBindingRouteResult>(
                new TeamsBindingIngress(ingress.Activity, ingress.CancellationToken),
                BindingRouteTimeout,
                ingress.CancellationToken);
            replyTo.Tell(result);
        }
        catch (OperationCanceledException) when (ingress.CancellationToken.IsCancellationRequested)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Cancelled));
        }
        catch (AskTimeoutException)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Unavailable));
        }
        catch (Exception)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Failed));
        }
    }
}

/// <summary>
/// Owns the personal Teams session pipeline and durable activity duplicate
/// history. A durable reservation occurs before local queue admission.
/// </summary>
public sealed class TeamsSessionBindingActor : ReceivePersistentActor
{
    internal const int ProcessedActivityCapacity = 1_024;

    private readonly SessionId _sessionId;
    private readonly TeamsConversationDependencies _dependencies;
    private readonly SessionPipelineHandle _pipelineHandle;
    private readonly ILoggingAdapter _log;
    private readonly HashSet<string> _processedActivityIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _processedActivityOrder = new();

    public TeamsSessionBindingActor(SessionId sessionId, TeamsConversationDependencies dependencies)
    {
        _sessionId = sessionId;
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        _log = Context.GetLogger().WithContext("Adapter", "teams");
        _pipelineHandle = new SessionPipelineHandle(_dependencies.Pipeline, _log, "teams-personal");

        Recover<DurableActivityDispatchReserved>(ApplyReserved);
        Recover<DurableActivityDispatchReleased>(ApplyReleased);

        Context.SetReceiveTimeout(TimeSpan.FromHours(1));
        Command<ReceiveTimeout>(_ => Context.Stop(Self));
        Command<TeamsBindingIngress>(HandleIngress);
        CommandAsync<DispatchReservedActivity>(DispatchReservedActivityAsync);
        Command<OutputStreamTerminated>(terminated =>
        {
            if (terminated.Generation == _pipelineHandle.Generation)
                Self.Tell(new ReinitializePipeline());
        });
        CommandAsync<ReinitializePipeline>(async _ =>
        {
            await _pipelineHandle.ReinitializeAsync(
                "Teams personal pipeline output terminated",
                () => ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("personal_pipeline_reinitialize_failed"));
        });
    }

    public override string PersistenceId =>
        "teams-personal-binding-" + Uri.EscapeDataString(_sessionId.Value);

    public static Props CreateProps(SessionId sessionId, TeamsConversationDependencies dependencies) =>
        Props.Create(() => new TeamsSessionBindingActor(sessionId, dependencies));

    protected override void PostStop()
    {
        _pipelineHandle.Dispose();
        base.PostStop();
    }

    private void HandleIngress(TeamsBindingIngress ingress)
    {
        var replyTo = Sender;
        if (ingress.CancellationToken.IsCancellationRequested)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Cancelled));
            return;
        }

        if (ingress.Activity.Trust.Scope != TeamsConversationScope.Personal
            || !TeamsSessionIdentifierCodec.TryCreatePersonal(
                ingress.Activity.Trust.TenantId,
                ingress.Activity.Trust.ConversationId,
                out var expectedSessionId,
                out _)
            || expectedSessionId != _sessionId)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Unavailable));
            return;
        }

        var acl = TeamsPersonalAclPolicy.Evaluate(ingress.Activity, _dependencies.Options);
        if (!acl.IsAllowed)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("personal_acl_denied");
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Denied));
            return;
        }

        var activityId = ingress.Activity.Trust.ActivityId;
        if (_processedActivityIds.Contains(activityId))
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("durable_activity_duplicate");
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Duplicate));
            return;
        }

        var evictedActivityId = _processedActivityOrder.Count == ProcessedActivityCapacity
            ? _processedActivityOrder.Peek()
            : null;
        Persist(
            new DurableActivityDispatchReserved(activityId, evictedActivityId),
            reserved =>
            {
                ApplyReserved(reserved);
                Self.Tell(new DispatchReservedActivity(ingress.Activity, replyTo, ingress.CancellationToken));
            });
    }

    private async Task DispatchReservedActivityAsync(DispatchReservedActivity dispatch)
    {
        try
        {
            var writer = await EnsurePipelineAsync(dispatch.CancellationToken);
            await writer.WriteAsync(BuildChannelInput(dispatch.Activity), dispatch.CancellationToken);
            ChannelTelemetry.For(ChannelType.Teams).RecordMessageEnqueued();
            ChannelTelemetry.For(ChannelType.Teams).RecordEventRouted("personal_binding");
            dispatch.ReplyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Accepted));
        }
        catch (OperationCanceledException) when (dispatch.CancellationToken.IsCancellationRequested)
        {
            ReleaseReservation(dispatch, TeamsBindingRouteDisposition.Cancelled);
        }
        catch (ChannelClosedException)
        {
            ReleaseReservation(dispatch, TeamsBindingRouteDisposition.Failed);
        }
        catch (Exception)
        {
            ReleaseReservation(dispatch, TeamsBindingRouteDisposition.Failed);
        }
    }

    private void ReleaseReservation(
        DispatchReservedActivity dispatch,
        TeamsBindingRouteDisposition disposition)
    {
        Persist(
            new DurableActivityDispatchReleased(dispatch.Activity.Trust.ActivityId),
            released =>
            {
                ApplyReleased(released);
                ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped(
                    disposition == TeamsBindingRouteDisposition.Cancelled
                        ? "pipeline_dispatch_cancelled"
                        : "pipeline_dispatch_failed");
                dispatch.ReplyTo.Tell(new TeamsBindingRouteResult(disposition));
            });
    }

    private async Task<ChannelWriter<ChannelInput>> EnsurePipelineAsync(CancellationToken cancellationToken)
    {
        if (_pipelineHandle.InputQueue is { } writer)
            return writer;

        return await _pipelineHandle.InitializeWithChannelAsync(
            Context,
            _sessionId,
            new SessionPipelineOptions
            {
                ChannelType = ChannelType.Teams,
                Filter = OutputFilter.None
            },
            _ => { },
            (generation, cause) => Self.Tell(new OutputStreamTerminated(generation, cause)),
            cancellationToken);
    }

    private static ChannelInput BuildChannelInput(TeamsInboundActivity activity) => new()
    {
        SenderId = new SenderId(activity.Trust.SenderId),
        ChannelId = activity.Trust.ConversationId,
        MessageId = activity.Trust.ActivityId,
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        Principal = PrincipalClassification.TrustedInternal,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community)
        {
            SourceKind = new SourceKind("teams"),
            SourceScope = new SourceScope("teams-personal")
        },
        Contents = [new TextContent(activity.Text)],
        ReceivedAt = activity.Trust.ReceivedAtUtc,
        ExecutableText = activity.Text
    };

    private void ApplyReserved(DurableActivityDispatchReserved reserved)
    {
        if (reserved.EvictedActivityId is { } evicted)
        {
            if (_processedActivityOrder.Count > 0
                && string.Equals(_processedActivityOrder.Peek(), evicted, StringComparison.Ordinal))
            {
                _processedActivityOrder.Dequeue();
            }

            _processedActivityIds.Remove(evicted);
        }

        if (_processedActivityIds.Add(reserved.ActivityId))
            _processedActivityOrder.Enqueue(reserved.ActivityId);
    }

    private void ApplyReleased(DurableActivityDispatchReleased released)
    {
        if (!_processedActivityIds.Remove(released.ActivityId))
            return;

        var retained = _processedActivityOrder.Where(id => id != released.ActivityId).ToArray();
        _processedActivityOrder.Clear();
        foreach (var activityId in retained)
            _processedActivityOrder.Enqueue(activityId);
    }

    private sealed record DispatchReservedActivity(
        TeamsInboundActivity Activity,
        IActorRef ReplyTo,
        CancellationToken CancellationToken) : INoSerializationVerificationNeeded;

    private sealed record OutputStreamTerminated(
        int Generation,
        Exception? Cause) : INoSerializationVerificationNeeded;

    private sealed record ReinitializePipeline : INoSerializationVerificationNeeded;

}
