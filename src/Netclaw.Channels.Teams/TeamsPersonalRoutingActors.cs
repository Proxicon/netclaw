// -----------------------------------------------------------------------
// <copyright file="TeamsPersonalRoutingActors.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
using System.Security.Cryptography;
using System.Text;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Channels;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels.Teams;

/// <summary>
/// Dependencies shared by the Teams personal conversation and binding actors.
/// </summary>
public sealed record TeamsConversationDependencies(
    TeamsChannelOptions Options,
    ISessionPipeline Pipeline,
    ITeamsReplyClient ReplyClient,
    TeamsOutputRenderer OutputRenderer,
    TimeProvider TimeProvider);

public sealed record TeamsConversationIngress(
    TeamsInboundActivity Activity,
    CancellationToken CancellationToken) : INoSerializationVerificationNeeded;

public sealed record TeamsBindingIngress(
    TeamsInboundActivity Activity,
    CancellationToken CancellationToken) : INoSerializationVerificationNeeded;

public sealed record TeamsBindingApprovalAction(
    TeamsApprovalAction Action,
    CancellationToken CancellationToken) : INoSerializationVerificationNeeded;

public sealed record TeamsConversationApprovalAction(
    TeamsApprovalAction Action,
    CancellationToken CancellationToken) : INoSerializationVerificationNeeded;

public enum TeamsBindingRouteDisposition
{
    Accepted,
    Duplicate,
    Ignored,
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

    public static string ChannelConversation(SessionId sessionId) =>
        "teams-channel-conversation-" + Uri.EscapeDataString(sessionId.Value);

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

        if (!TeamsSessionIdentifierCodec.TryCreatePersonal(
                activity.Trust.TenantId,
                activity.Trust.ConversationId,
                out var conversationId,
                out _))
        {
            return TeamsIngressSinkResult.Denied;
        }

        if (activity.Trust.Scope == TeamsConversationScope.Personal)
        {
            if (!TeamsPersonalAclPolicy.Evaluate(activity, _options).IsAllowed)
                return TeamsIngressSinkResult.Denied;

            return await RoutePersonalAsync(activity, conversationId, cancellationToken);
        }

        if (activity.Trust.Scope != TeamsConversationScope.Channel)
            return TeamsIngressSinkResult.Unavailable;

        var policy = TeamsChannelAclPolicy.Evaluate(activity, _options);
        if (policy.Disposition == TeamsChannelPolicyDisposition.Ignored)
            return TeamsIngressSinkResult.Ignored;
        if (policy.Disposition != TeamsChannelPolicyDisposition.Allowed)
            return TeamsIngressSinkResult.Denied;

        return await RouteChannelAsync(activity, conversationId, cancellationToken);
    }

    public async ValueTask<TeamsApprovalActionResult> RouteApprovalAsync(
        TeamsApprovalAction action,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Cancelled);

        if (!TryCreateApprovalActivity(action, out var activity)
            || !TeamsSessionIdentifierCodec.TryCreatePersonal(
                action.Trust.TenantId,
                action.Trust.ConversationId,
                out var conversationId,
                out _))
        {
            return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Rejected);
        }

        if (action.Trust.Scope == TeamsConversationScope.Personal)
        {
            if (!TeamsPersonalAclPolicy.Evaluate(activity, _options).IsAllowed)
                return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Rejected);

            return await RoutePersonalApprovalAsync(action, conversationId, cancellationToken);
        }

        if (action.Trust.Scope != TeamsConversationScope.Channel
            || TeamsChannelAclPolicy.Evaluate(activity, _options).Disposition != TeamsChannelPolicyDisposition.Allowed)
        {
            return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Rejected);
        }

        return await RouteChannelApprovalAsync(action, conversationId, cancellationToken);
    }

    private async ValueTask<TeamsApprovalActionResult> RoutePersonalApprovalAsync(
        TeamsApprovalAction action,
        SessionId conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = GetOrCreatePersonalConversation(conversationId, CreateDependencies());
        return await AskApprovalAsync(conversation, action, cancellationToken);
    }

    private async ValueTask<TeamsApprovalActionResult> RouteChannelApprovalAsync(
        TeamsApprovalAction action,
        SessionId conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = GetOrCreateChannelConversation(conversationId, CreateDependencies());
        return await AskApprovalAsync(conversation, action, cancellationToken);
    }

    private TeamsConversationDependencies CreateDependencies() => new(
        _options,
        _serviceProvider.GetRequiredService<ISessionPipeline>(),
        _serviceProvider.GetRequiredService<ITeamsReplyClient>(),
        _serviceProvider.GetRequiredService<TeamsOutputRenderer>(),
        _serviceProvider.GetRequiredService<TimeProvider>());

    private static async ValueTask<TeamsApprovalActionResult> AskApprovalAsync(
        IActorRef conversation,
        TeamsApprovalAction action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await conversation.Ask<TeamsApprovalActionResult>(
                new TeamsConversationApprovalAction(action, cancellationToken),
                RouteTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Cancelled);
        }
        catch (AskTimeoutException)
        {
            return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Unavailable);
        }
        catch (Exception)
        {
            return new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Failed);
        }
    }

    private static bool TryCreateApprovalActivity(TeamsApprovalAction action, out TeamsInboundActivity activity)
    {
        activity = null!;
        if (!TeamsApprovalAction.IsBoundedOpaqueValue(action.CorrelationId, TeamsApprovalAction.MaxCorrelationLength)
            || !TeamsApprovalAction.IsBoundedOpaqueValue(action.Nonce, TeamsApprovalAction.MaxNonceLength)
            || !TeamsApprovalAction.IsSupportedAction(action.Action)
            || !TeamsOutboundDestination.IsValidServiceUrl(action.ServiceUrl))
        {
            return false;
        }

        try
        {
            activity = new TeamsInboundActivity(
                action.Trust,
                string.Empty,
                new TeamsReplyMetadata(null, action.RootActivityId, action.ServiceUrl),
                isMentioned: true,
                kind: TeamsIngressActivityKind.AdaptiveCardAction,
                teamId: action.TeamId,
                channelId: action.ChannelId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async ValueTask<TeamsIngressSinkResult> RoutePersonalAsync(
        TeamsInboundActivity activity,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var conversation = GetOrCreatePersonalConversation(sessionId, new TeamsConversationDependencies(
            _options,
            _serviceProvider.GetRequiredService<ISessionPipeline>(),
            _serviceProvider.GetRequiredService<ITeamsReplyClient>(),
            _serviceProvider.GetRequiredService<TeamsOutputRenderer>(),
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

    private async ValueTask<TeamsIngressSinkResult> RouteChannelAsync(
        TeamsInboundActivity activity,
        SessionId conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = GetOrCreateChannelConversation(conversationId, new TeamsConversationDependencies(
            _options,
            _serviceProvider.GetRequiredService<ISessionPipeline>(),
            _serviceProvider.GetRequiredService<ITeamsReplyClient>(),
            _serviceProvider.GetRequiredService<TeamsOutputRenderer>(),
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
                TeamsBindingRouteDisposition.Ignored => TeamsIngressSinkResult.Ignored,
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

    private IActorRef GetOrCreatePersonalConversation(
        SessionId sessionId,
        TeamsConversationDependencies dependencies)
    {
        lock (_creationLock)
        {
            if (_conversations.TryGetValue(sessionId.Value, out var existing))
                return existing;

            var actor = _actorSystem.ActorOf(
                TeamsPersonalConversationActor.CreateProps(sessionId, dependencies),
                TeamsActorNames.Conversation(sessionId));
            _conversations.Add(sessionId.Value, actor);
            return actor;
        }
    }

    private IActorRef GetOrCreateChannelConversation(
        SessionId conversationId,
        TeamsConversationDependencies dependencies)
    {
        lock (_creationLock)
        {
            var key = "channel:" + conversationId.Value;
            if (_conversations.TryGetValue(key, out var existing))
                return existing;

            var actor = _actorSystem.ActorOf(
                TeamsConversationActor.CreateProps(conversationId, dependencies),
                TeamsActorNames.ChannelConversation(conversationId));
            _conversations.Add(key, actor);
            return actor;
        }
    }
}

/// <summary>
/// Owns deterministic binding-child lookup for one canonical Teams personal
/// conversation. It owns no pipeline work and no durable duplicate state.
/// </summary>
public sealed class TeamsPersonalConversationActor : ReceiveActor
{
    private static readonly TimeSpan BindingRouteTimeout = TimeSpan.FromSeconds(10);

    private readonly SessionId _sessionId;
    private readonly TeamsConversationDependencies _dependencies;
    private readonly ILoggingAdapter _log;

    public TeamsPersonalConversationActor(SessionId sessionId, TeamsConversationDependencies dependencies)
    {
        _sessionId = sessionId;
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        _log = Context.GetLogger().WithContext("Adapter", "teams");

        ReceiveAsync<TeamsConversationIngress>(HandleIngressAsync);
        ReceiveAsync<TeamsConversationApprovalAction>(HandleApprovalAsync);
    }

    public static Props CreateProps(SessionId sessionId, TeamsConversationDependencies dependencies) =>
        Props.Create(() => new TeamsPersonalConversationActor(sessionId, dependencies));

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

    private async Task HandleApprovalAsync(TeamsConversationApprovalAction action)
    {
        var replyTo = Sender;
        if (action.CancellationToken.IsCancellationRequested
            || action.Action.Trust.Scope != TeamsConversationScope.Personal
            || !TeamsSessionIdentifierCodec.TryCreatePersonal(
                action.Action.Trust.TenantId,
                action.Action.Trust.ConversationId,
                out var expectedSessionId,
                out _)
            || expectedSessionId != _sessionId)
        {
            replyTo.Tell(new TeamsApprovalActionResult(action.CancellationToken.IsCancellationRequested
                ? TeamsApprovalActionDisposition.Cancelled
                : TeamsApprovalActionDisposition.Rejected));
            return;
        }

        var binding = Context.Child(TeamsActorNames.Binding(_sessionId));
        if (binding.IsNobody())
        {
            binding = Context.ActorOf(
                TeamsSessionBindingActor.CreateProps(_sessionId, _dependencies),
                TeamsActorNames.Binding(_sessionId));
        }

        try
        {
            replyTo.Tell(await binding.Ask<TeamsApprovalActionResult>(
                new TeamsBindingApprovalAction(action.Action, action.CancellationToken),
                BindingRouteTimeout,
                action.CancellationToken));
        }
        catch (OperationCanceledException) when (action.CancellationToken.IsCancellationRequested)
        {
            replyTo.Tell(new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Cancelled));
        }
        catch (AskTimeoutException)
        {
            replyTo.Tell(new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Unavailable));
        }
        catch (Exception)
        {
            replyTo.Tell(new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Failed));
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

    internal const int ApprovalCapacity = 128;

    private const long SnapshotInterval = 64;

    private readonly SessionId _sessionId;
    private readonly TeamsConversationDependencies _dependencies;
    private readonly SessionPipelineHandle _pipelineHandle;
    private readonly ILoggingAdapter _log;
    private readonly bool _isChannelBinding;
    private readonly HashSet<string> _processedActivityIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _processedActivityOrder = new();
    private readonly Dictionary<string, TeamsPendingApproval> _pendingApprovals = new(StringComparer.Ordinal);
    private TeamsOutboundDestination? _destination;
    private string? _processingActivityId;

    public TeamsSessionBindingActor(SessionId sessionId, TeamsConversationDependencies dependencies)
    {
        _sessionId = sessionId;
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        _log = Context.GetLogger().WithContext("Adapter", "teams");
        _isChannelBinding = TeamsSessionIdentifierCodec.TryParse(_sessionId, out var identifier, out _)
                            && identifier.Scope == TeamsConversationScope.Channel;
        _pipelineHandle = new SessionPipelineHandle(
            _dependencies.Pipeline,
            _log,
            _isChannelBinding ? "teams-channel" : "teams-personal");

        Recover<DurableActivityDispatchReserved>(ApplyReserved);
        Recover<DurableActivityDispatchReleased>(ApplyReleased);
        Recover<TeamsApprovalPendingCreated>(ApplyApprovalPendingCreated);
        Recover<TeamsApprovalCardDelivered>(ApplyApprovalCardDelivered);
        Recover<TeamsApprovalConsumed>(ApplyApprovalConsumed);
        Recover<SnapshotOffer>(offer =>
        {
            if (offer.Snapshot is DurableActivityDispatchSnapshot snapshot)
                ApplySnapshot(snapshot);
        });

        Context.SetReceiveTimeout(TimeSpan.FromHours(1));
        Command<ReceiveTimeout>(_ => Context.Stop(Self));
        Command<TeamsBindingIngress>(HandleIngress);
        CommandAsync<TeamsBindingApprovalAction>(HandleApprovalActionAsync);
        CommandAsync<ForwardTeamsApprovalDecision>(ForwardApprovalDecisionAsync);
        CommandAsync<DeliverTeamsApprovalTerminal>(DeliverApprovalTerminalAsync);
        CommandAsync<DenyTeamsApprovalRequest>(DenyApprovalRequestAsync);
        CommandAsync<DispatchReservedActivity>(DispatchReservedActivityAsync);
        CommandAsync<BindingOutput>(HandleOutputAsync);
        CommandAsync<DeliverTeamsApprovalCard>(DeliverApprovalCardAsync);
        Command<OutputStreamTerminated>(terminated =>
        {
            if (terminated.Generation == _pipelineHandle.Generation)
                Self.Tell(new ReinitializePipeline());
        });
        CommandAsync<ReinitializePipeline>(async _ =>
        {
            await _pipelineHandle.ReinitializeAsync(
                _isChannelBinding ? "Teams channel pipeline output terminated" : "Teams personal pipeline output terminated",
                () => ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped(
                    _isChannelBinding ? "channel_pipeline_reinitialize_failed" : "personal_pipeline_reinitialize_failed"));
        });
        Command<SaveSnapshotSuccess>(saved =>
        {
            DeleteMessages(saved.Metadata.SequenceNr);
            DeleteSnapshots(new SnapshotSelectionCriteria(saved.Metadata.SequenceNr - 1));
        });
        Command<SaveSnapshotFailure>(_ =>
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("personal_processed_state_snapshot_failed"));
    }

    public override string PersistenceId =>
        (TeamsSessionIdentifierCodec.TryParse(_sessionId, out var identifier, out _)
         && identifier.Scope == TeamsConversationScope.Channel
            ? "teams-channel-binding-"
            : "teams-personal-binding-") + Uri.EscapeDataString(_sessionId.Value);

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

        if (!TryGetExpectedSessionId(ingress.Activity, out var expectedSessionId)
            || expectedSessionId != _sessionId)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Unavailable));
            return;
        }

        var acl = EvaluateAcl(ingress.Activity);
        if (acl is null)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped(
                _isChannelBinding ? "channel_acl_denied" : "personal_acl_denied");
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Denied));
            return;
        }

        var activityFingerprint = ActivityFingerprint.Create(ingress.Activity.Trust.ActivityId);
        if (_processedActivityIds.Contains(activityFingerprint))
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("durable_activity_duplicate");
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Duplicate));
            return;
        }

        var evictedActivityFingerprint = _processedActivityOrder.Count == ProcessedActivityCapacity
            ? _processedActivityOrder.Peek()
            : null;
        Persist(
            new DurableActivityDispatchReserved(activityFingerprint, evictedActivityFingerprint),
            reserved =>
            {
                ApplyReserved(reserved);
                SaveSnapshotWhenDue();
                Self.Tell(new DispatchReservedActivity(
                    ingress.Activity,
                    activityFingerprint,
                    replyTo,
                    ingress.CancellationToken));
            });
    }

    private async Task DispatchReservedActivityAsync(DispatchReservedActivity dispatch)
    {
        try
        {
            _destination = CreateDestination(dispatch.Activity);
            var writer = await EnsurePipelineAsync(dispatch.CancellationToken);
            await writer.WriteAsync(BuildChannelInput(dispatch.Activity), dispatch.CancellationToken);
            ChannelTelemetry.For(ChannelType.Teams).RecordMessageEnqueued();
            ChannelTelemetry.For(ChannelType.Teams).RecordEventRouted(
                _isChannelBinding ? "channel_binding" : "personal_binding");
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
            new DurableActivityDispatchReleased(dispatch.ActivityFingerprint),
            released =>
            {
                ApplyReleased(released);
                SaveSnapshotWhenDue();
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

        var self = Self;
        return await _pipelineHandle.InitializeWithChannelAsync(
            Context,
            _sessionId,
            new SessionPipelineOptions
            {
                ChannelType = ChannelType.Teams,
                Filter = OutputFilter.Text | OutputFilter.ProcessingState
            },
            output => self.Tell(new BindingOutput(output)),
            (generation, cause) => self.Tell(new OutputStreamTerminated(generation, cause)),
            cancellationToken);
    }

    private ChannelInput BuildChannelInput(TeamsInboundActivity activity)
    {
        var acl = EvaluateAcl(activity) ?? throw new InvalidOperationException("The Teams activity is not authorized for dispatch.");
        var sourceScope = activity.Trust.Scope == TeamsConversationScope.Personal
            ? "teams-personal"
            : "teams-channel";
        var boundary = activity.Trust.Scope == TeamsConversationScope.Personal
            ? TrustBoundary.Personal
            : TrustBoundary.Public;
        return new ChannelInput
        {
        SenderId = new SenderId(activity.Trust.SenderId),
        ChannelId = activity.Trust.ConversationId,
        MessageId = activity.Trust.ActivityId,
        Audience = acl.Audience,
        Boundary = boundary,
        Principal = acl.Principal,
        Provenance = acl.Provenance with { SourceScope = new SourceScope(sourceScope) },
        Contents = [new TextContent(activity.Text)],
        ReceivedAt = activity.Trust.ReceivedAtUtc,
        ExecutableText = activity.Text
        };
    }

    private bool TryGetExpectedSessionId(TeamsInboundActivity activity, out SessionId sessionId)
    {
        if (activity.Trust.Scope == TeamsConversationScope.Personal)
        {
            return TeamsSessionIdentifierCodec.TryCreatePersonal(
                activity.Trust.TenantId,
                activity.Trust.ConversationId,
                out sessionId,
                out _);
        }

        if (activity.Trust.Scope == TeamsConversationScope.Channel
            && activity.Reply?.RootActivityId is { } rootActivityId)
        {
            return TeamsSessionIdentifierCodec.TryCreateChannel(
                activity.Trust.TenantId,
                activity.Trust.ConversationId,
                rootActivityId,
                out sessionId,
                out _);
        }

        sessionId = default;
        return false;
    }

    private ChannelAclDecision? EvaluateAcl(TeamsInboundActivity activity)
    {
        if (activity.Trust.Scope == TeamsConversationScope.Personal)
        {
            var personal = TeamsPersonalAclPolicy.Evaluate(activity, _dependencies.Options);
            return personal.IsAllowed ? personal : null;
        }

        var channel = TeamsChannelAclPolicy.Evaluate(activity, _dependencies.Options);
        return channel.Disposition == TeamsChannelPolicyDisposition.Allowed ? channel.Acl : null;
    }


    private void ApplyReserved(DurableActivityDispatchReserved reserved)
    {
        EnsureValidFingerprint(reserved.ActivityFingerprint);
        if (reserved.EvictedActivityFingerprint is { } evicted)
        {
            EnsureValidFingerprint(evicted);
            if (_processedActivityOrder.Count == 0
                || !string.Equals(_processedActivityOrder.Peek(), evicted, StringComparison.Ordinal)
                || !_processedActivityIds.Remove(evicted))
            {
                throw new InvalidOperationException("The Teams processed activity state has invalid retention ordering.");
            }

            _processedActivityOrder.Dequeue();
        }
        else if (_processedActivityOrder.Count >= ProcessedActivityCapacity)
            throw new InvalidOperationException("The Teams processed activity state exceeds its retention limit.");

        if (!_processedActivityIds.Add(reserved.ActivityFingerprint))
            throw new InvalidOperationException("The Teams processed activity state contains a duplicate reservation.");

        _processedActivityOrder.Enqueue(reserved.ActivityFingerprint);
    }

    private void ApplyReleased(DurableActivityDispatchReleased released)
    {
        EnsureValidFingerprint(released.ActivityFingerprint);
        if (!_processedActivityIds.Remove(released.ActivityFingerprint))
            return;

        var retained = _processedActivityOrder.Where(id => id != released.ActivityFingerprint).ToArray();
        _processedActivityOrder.Clear();
        foreach (var fingerprint in retained)
            _processedActivityOrder.Enqueue(fingerprint);
    }

    private void ApplySnapshot(DurableActivityDispatchSnapshot snapshot)
    {
        if (snapshot.ActivityFingerprints.Count > ProcessedActivityCapacity)
            throw new InvalidOperationException("The Teams processed activity snapshot exceeds its retention limit.");

        _processedActivityIds.Clear();
        _processedActivityOrder.Clear();
        foreach (var fingerprint in snapshot.ActivityFingerprints)
        {
            EnsureValidFingerprint(fingerprint);
            if (!_processedActivityIds.Add(fingerprint))
                throw new InvalidOperationException("The Teams processed activity snapshot contains a duplicate entry.");

            _processedActivityOrder.Enqueue(fingerprint);
        }
    }

    private void SaveSnapshotWhenDue()
    {
        if (!IsRecovering && LastSequenceNr > 0 && LastSequenceNr % SnapshotInterval == 0)
            SaveSnapshot(new DurableActivityDispatchSnapshot(_processedActivityOrder.ToArray()));
    }

    private async Task HandleOutputAsync(BindingOutput received)
    {
        if (_destination is null)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("output_destination_unavailable");
            RecordDeliveryOutcome(new TeamsDeliveryResult(TeamsDeliveryStatus.Unavailable, ReasonCode: "output_destination_unavailable"), 0);
            return;
        }

        if (received.Output is ProcessingStateOutput { IsProcessing: true })
        {
            if (_processingActivityId is null)
            {
                var processing = await DeliverAsync(CreateMessage(_destination, "Processing..."));
                if (processing.IsSuccess)
                    _processingActivityId = IsBoundedActivityId(processing.ActivityId)
                        ? processing.ActivityId
                        : null;
            }
            return;
        }

        if (received.Output is ToolInteractionRequest approval)
        {
            HandleApprovalRequest(approval);
            return;
        }

        if (received.Output is not TextOutput text)
            return;

        var rendered = _dependencies.OutputRenderer.Render(
            text.Text,
            _destination.Scope == TeamsConversationScope.Channel ? _destination.RootActivityId : null);
        if (rendered.Chunks.Count == 0)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordExtra(
                rendered.IsRejectedTooLarge ? "output_rejected_too_large" : "output_ignored_empty");
            return;
        }

        for (var index = 0; index < rendered.Chunks.Count; index++)
        {
            var processingActivityId = index == 0 ? _processingActivityId : null;
            var result = await DeliverAsync(
                CreateMessage(_destination, rendered.Chunks[index], processingActivityId));

            // The update is a presentation optimization. A failed update gets
            // one normal reply attempt for this final output and no retry loop.
            if (processingActivityId is not null && !result.IsSuccess)
            {
                _processingActivityId = null;
                result = await DeliverAsync(CreateMessage(_destination, rendered.Chunks[index]));
            }

            if (!result.IsSuccess)
            {
                ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped(
                    result.Status == TeamsDeliveryStatus.RejectedTooLarge
                        ? "output_rejected_too_large"
                        : "output_delivery_failed");
                return;
            }

            _processingActivityId = null;
        }
    }

    private async Task<TeamsDeliveryResult> DeliverAsync(TeamsOutboundMessage message)
    {
        var startedAt = _dependencies.TimeProvider.GetTimestamp();
        TeamsDeliveryResult result;
        try
        {
            result = await _dependencies.ReplyClient.DeliverAsync(message, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            result = new TeamsDeliveryResult(TeamsDeliveryStatus.Cancelled, ReasonCode: "cancelled");
        }
        catch (Exception)
        {
            result = new TeamsDeliveryResult(TeamsDeliveryStatus.Failed, ReasonCode: "reply_client_failed");
        }

        RecordDeliveryOutcome(result, _dependencies.TimeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
        return result;
    }

    private static void RecordDeliveryOutcome(TeamsDeliveryResult result, double durationMs)
    {
        var telemetry = ChannelTelemetry.For(ChannelType.Teams);
        if (result.IsSuccess)
        {
            telemetry.RecordReplyPosted(durationMs);
            return;
        }

        if (result.Status == TeamsDeliveryStatus.RejectedTooLarge)
        {
            telemetry.RecordReplyRejected(result.ReasonCode);
            return;
        }

        telemetry.RecordReplyFailed(durationMs);
    }

    private static bool IsBoundedActivityId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Encoding.UTF8.GetByteCount(value) <= TeamsSessionIdentifierCodec.MaxRawIdentifierBytes;

    private TeamsOutboundDestination CreateDestination(TeamsInboundActivity activity)
    {
        var serviceUrl = activity.Reply?.ServiceUrl;
        if (string.IsNullOrWhiteSpace(serviceUrl))
            throw new InvalidOperationException("The Teams activity lacks an outbound service URL.");

        return new TeamsOutboundDestination(
            activity.Trust.TenantId,
            activity.Trust.ConversationId,
            activity.Trust.Scope,
            serviceUrl,
            activity.Trust.Scope == TeamsConversationScope.Channel ? activity.Reply?.RootActivityId : null,
            activity.TeamId,
            activity.ChannelId,
            activity.Trust.Scope == TeamsConversationScope.Personal ? activity.Trust.SenderId : null);
    }

    private TeamsOutboundMessage CreateMessage(
        TeamsOutboundDestination destination,
        string text,
        string? updateActivityId = null,
        TeamsApprovalCard? approvalCard = null) => new(
        destination,
        text,
        ActivityFingerprint.Create(_sessionId.Value + text),
        ActivityFingerprint.Create(_sessionId.Value),
        destination.Scope == TeamsConversationScope.Channel ? destination.RootActivityId : null,
        updateActivityId,
        approvalCard: approvalCard);

    private void HandleApprovalRequest(ToolInteractionRequest request)
    {
        if (_destination is null || !string.Equals(request.Kind, "approval", StringComparison.Ordinal))
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("approval_card_destination_unavailable");
            return;
        }

        if (_pendingApprovals.Count >= ApprovalCapacity)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("approval_state_capacity_exceeded");
            Self.Tell(new DenyTeamsApprovalRequest(request));
            return;
        }

        var correlationId = TeamsApprovalCardRenderer.CreateCorrelationId();
        var nonce = TeamsApprovalCardRenderer.CreateNonce();
        var expiresAt = _dependencies.TimeProvider.GetUtcNow().AddMinutes(15);
        Persist(new TeamsApprovalPendingCreated
        {
            CallId = request.CallId.Value,
            CorrelationId = correlationId,
            NonceHash = TeamsApprovalCardRenderer.HashNonce(nonce),
            RequesterSenderId = request.RequesterSenderId?.Value,
            RequesterPrincipal = request.RequesterPrincipal,
            ExpiresAtUnixMilliseconds = expiresAt.ToUnixTimeMilliseconds()
        }, created =>
        {
            ApplyApprovalPendingCreated(created);
            Self.Tell(new DeliverTeamsApprovalCard(request, correlationId, nonce));
        });
    }

    private async Task DeliverApprovalCardAsync(DeliverTeamsApprovalCard delivery)
    {
        if (_destination is null || !_pendingApprovals.TryGetValue(delivery.CorrelationId, out var pending))
            return;

        TeamsApprovalCard card;
        try
        {
            card = TeamsApprovalCardRenderer.CreatePending(delivery.Request, delivery.CorrelationId, delivery.Nonce);
        }
        catch (ArgumentException)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("approval_card_invalid_contract");
            return;
        }

        var result = await DeliverAsync(CreateMessage(_destination, "Approval required.", approvalCard: card));
        if (!result.IsSuccess || !IsBoundedActivityId(result.ActivityId))
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("approval_card_delivery_failed");
            return;
        }

        Persist(new TeamsApprovalCardDelivered
        {
            CorrelationId = pending.CorrelationId,
            PromptId = result.ActivityId!
        }, ApplyApprovalCardDelivered);
    }

    private async Task DenyApprovalRequestAsync(DenyTeamsApprovalRequest request)
    {
        try
        {
            await _dependencies.Pipeline.SendFeedbackAsync(new ToolInteractionResponse
            {
                SessionId = _sessionId,
                CallId = request.Request.CallId,
                SelectedKey = ApprovalOptionKeys.DenyKey,
                SenderId = request.Request.RequesterSenderId ?? new SenderId(string.Empty)
            });
        }
        catch (Exception)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("approval_capacity_deny_failed");
        }
    }

    private async Task HandleApprovalActionAsync(TeamsBindingApprovalAction received)
    {
        if (received.CancellationToken.IsCancellationRequested)
        {
            Sender.Tell(new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Cancelled));
            return;
        }

        var action = received.Action;
        if (!TryGetExpectedSessionId(action, out var expectedSessionId)
            || expectedSessionId != _sessionId
            || !TryCreateDestination(action, out var destination)
            || !TeamsApprovalAction.IsSupportedAction(action.Action)
            || !_pendingApprovals.TryGetValue(action.CorrelationId, out var pending)
            || pending.PromptId is null
            || !string.Equals(pending.PromptId, action.PromptActivityId, StringComparison.Ordinal)
            || !TeamsApprovalCardRenderer.NonceMatches(pending.NonceHash, action.Nonce))
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("approval_action_rejected");
            Sender.Tell(new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Rejected));
            return;
        }

        _destination = destination;
        if (!ApprovalButtonValueCodec.CanApprove(
                pending.RequesterPrincipal,
                pending.RequesterSenderId,
                action.Trust.SenderId))
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("approval_action_wrong_user");
            Sender.Tell(new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Rejected));
            return;
        }

        if (pending.Decision is not null)
        {
            await DeliverTerminalCardAsync(destination, pending, "This approval was already processed.");
            ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("approval_action_duplicate");
            Sender.Tell(new TeamsApprovalActionResult(TeamsApprovalActionDisposition.AlreadyProcessed));
            return;
        }

        if (_dependencies.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds() >= pending.ExpiresAtUnixMilliseconds)
        {
            Persist(new TeamsApprovalConsumed
            {
                CorrelationId = pending.CorrelationId,
                Decision = "expired",
                ConsumedAtUnixMilliseconds = _dependencies.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            }, consumed =>
            {
                ApplyApprovalConsumed(consumed);
                Self.Tell(new DeliverTeamsApprovalTerminal(destination, pending.CorrelationId, "This approval has expired."));
            });
            ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("approval_action_expired");
            Sender.Tell(new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Expired));
            return;
        }

        var replyTo = Sender;
        Persist(new TeamsApprovalConsumed
        {
            CorrelationId = pending.CorrelationId,
            Decision = action.Action,
            ConsumedAtUnixMilliseconds = _dependencies.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        }, consumed =>
        {
            ApplyApprovalConsumed(consumed);
            Self.Tell(new ForwardTeamsApprovalDecision(
                destination,
                pending.CorrelationId,
                action.Action,
                action.Trust.SenderId));
            ChannelTelemetry.For(ChannelType.Teams).RecordExtra("approval_action_accepted");
            replyTo.Tell(new TeamsApprovalActionResult(TeamsApprovalActionDisposition.Accepted));
        });
    }

    private async Task ForwardApprovalDecisionAsync(ForwardTeamsApprovalDecision decision)
    {
        if (!_pendingApprovals.TryGetValue(decision.CorrelationId, out var pending))
            return;

        try
        {
            var feedback = await _dependencies.Pipeline.SendFeedbackAndWaitAsync(new ToolInteractionResponse
            {
                SessionId = _sessionId,
                CallId = new ToolCallId(pending.CallId),
                SelectedKey = decision.Action == "approve"
                    ? ApprovalOptionKeys.ApproveOnceKey
                    : ApprovalOptionKeys.DenyKey,
                SenderId = new SenderId(decision.SenderId)
            });

            var text = feedback is CommandAck
                ? decision.Action == "approve" ? "Approved." : "Denied."
                : "This approval is no longer available.";
            await DeliverTerminalCardAsync(decision.Destination, pending, text);
        }
        catch (Exception)
        {
            // The terminal decision already exists in the journal. Never retry
            // feedback from presentation code because that could repeat a tool decision.
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("approval_feedback_failed");
            await DeliverTerminalCardAsync(decision.Destination, pending, "This approval is no longer available.");
        }
    }

    private async Task DeliverTerminalCardAsync(
        TeamsOutboundDestination destination,
        TeamsPendingApproval pending,
        string text)
    {
        if (pending.PromptId is null)
            return;

        var card = TeamsApprovalCardRenderer.CreateTerminal(text);
        var update = await DeliverAsync(CreateMessage(destination, text, pending.PromptId, card));
        if (update.IsSuccess)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordExtra("approval_terminal_update_succeeded");
            return;
        }

        // One fallback provides a safe visible result. The decision remains
        // terminal when both presentation attempts fail.
        await DeliverAsync(CreateMessage(destination, text, approvalCard: card));
        ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("approval_terminal_update_failed");
    }

    private async Task DeliverApprovalTerminalAsync(DeliverTeamsApprovalTerminal terminal)
    {
        if (_pendingApprovals.TryGetValue(terminal.CorrelationId, out var pending))
            await DeliverTerminalCardAsync(terminal.Destination, pending, terminal.Text);
    }

    private bool TryGetExpectedSessionId(TeamsApprovalAction action, out SessionId sessionId)
    {
        if (action.Trust.Scope == TeamsConversationScope.Personal)
        {
            return TeamsSessionIdentifierCodec.TryCreatePersonal(
                action.Trust.TenantId,
                action.Trust.ConversationId,
                out sessionId,
                out _);
        }

        if (action.Trust.Scope == TeamsConversationScope.Channel
            && action.RootActivityId is { } rootActivityId)
        {
            return TeamsSessionIdentifierCodec.TryCreateChannel(
                action.Trust.TenantId,
                action.Trust.ConversationId,
                rootActivityId,
                out sessionId,
                out _);
        }

        sessionId = default;
        return false;
    }

    private static bool TryCreateDestination(TeamsApprovalAction action, out TeamsOutboundDestination destination)
    {
        try
        {
            destination = new TeamsOutboundDestination(
                action.Trust.TenantId,
                action.Trust.ConversationId,
                action.Trust.Scope,
                action.ServiceUrl,
                action.RootActivityId,
                action.TeamId,
                action.ChannelId,
                action.Trust.Scope == TeamsConversationScope.Personal ? action.Trust.SenderId : null);
            return true;
        }
        catch (ArgumentException)
        {
            destination = null!;
            return false;
        }
    }

    private void ApplyApprovalPendingCreated(TeamsApprovalPendingCreated created)
    {
        if (!TeamsApprovalAction.IsBoundedOpaqueValue(created.CorrelationId, TeamsApprovalAction.MaxCorrelationLength)
            || created.NonceHash.Length != 64
            || created.ExpiresAtUnixMilliseconds <= 0
            || string.IsNullOrWhiteSpace(created.CallId))
        {
            throw new InvalidOperationException("The Teams approval state is invalid.");
        }

        if (_pendingApprovals.Count >= ApprovalCapacity && !_pendingApprovals.ContainsKey(created.CorrelationId))
            throw new InvalidOperationException("The Teams approval state exceeds its retention limit.");

        _pendingApprovals[created.CorrelationId] = new TeamsPendingApproval(
            created.CallId,
            created.CorrelationId,
            created.NonceHash,
            created.RequesterSenderId,
            created.RequesterPrincipal,
            created.ExpiresAtUnixMilliseconds);
    }

    private void ApplyApprovalCardDelivered(TeamsApprovalCardDelivered delivered)
    {
        if (!_pendingApprovals.TryGetValue(delivered.CorrelationId, out var pending)
            || !IsBoundedActivityId(delivered.PromptId))
        {
            throw new InvalidOperationException("The Teams approval card locator is invalid.");
        }

        _pendingApprovals[delivered.CorrelationId] = pending with { PromptId = delivered.PromptId };
    }

    private void ApplyApprovalConsumed(TeamsApprovalConsumed consumed)
    {
        if (!_pendingApprovals.TryGetValue(consumed.CorrelationId, out var pending)
            || (consumed.Decision is not "approve" and not "deny" and not "expired"))
        {
            throw new InvalidOperationException("The Teams approval terminal state is invalid.");
        }

        _pendingApprovals[consumed.CorrelationId] = pending with { Decision = consumed.Decision };
    }

    private static void EnsureValidFingerprint(string fingerprint)
    {
        if (fingerprint.Length != 64 || fingerprint.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) || !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidOperationException("The Teams processed activity state contains an invalid fingerprint.");
        }
    }

    private sealed record DispatchReservedActivity(
        TeamsInboundActivity Activity,
        string ActivityFingerprint,
        IActorRef ReplyTo,
        CancellationToken CancellationToken) : INoSerializationVerificationNeeded;

    private sealed record OutputStreamTerminated(
        int Generation,
        Exception? Cause) : INoSerializationVerificationNeeded;

    private sealed record ReinitializePipeline : INoSerializationVerificationNeeded;

    private sealed record DeliverTeamsApprovalCard(
        ToolInteractionRequest Request,
        string CorrelationId,
        string Nonce) : INoSerializationVerificationNeeded;

    private sealed record DenyTeamsApprovalRequest(ToolInteractionRequest Request) : INoSerializationVerificationNeeded;

    private sealed record ForwardTeamsApprovalDecision(
        TeamsOutboundDestination Destination,
        string CorrelationId,
        string Action,
        string SenderId) : INoSerializationVerificationNeeded;

    private sealed record DeliverTeamsApprovalTerminal(
        TeamsOutboundDestination Destination,
        string CorrelationId,
        string Text) : INoSerializationVerificationNeeded;

    private sealed record TeamsPendingApproval(
        string CallId,
        string CorrelationId,
        string NonceHash,
        string? RequesterSenderId,
        PrincipalClassification? RequesterPrincipal,
        long ExpiresAtUnixMilliseconds,
        string? PromptId = null,
        string? Decision = null);

    internal sealed record BindingOutput(SessionOutput Output) : INoSerializationVerificationNeeded;

}

internal static class ActivityFingerprint
{
    public static string Create(string activityId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(activityId)));
}
