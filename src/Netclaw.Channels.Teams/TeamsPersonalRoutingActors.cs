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
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Channels;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Reminders.ReminderProtocol;
using static Netclaw.Actors.Sessions.SessionProtocol;
using LegacyProto = Netclaw.Actors.Serialization.Proto;

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

public sealed record TeamsConversationReminder(
    DeliverTrustedSessionTurn Reminder,
    string? KnownDestinationKey = null) : INoSerializationVerificationNeeded;

public sealed record TeamsBindingReminder(
    DeliverTrustedSessionTurn Reminder,
    string? KnownDestinationKey = null) : INoSerializationVerificationNeeded;

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

public enum TeamsMigrationHealthState
{
    NotRequired,
    Pending,
    Completed,
    Failed
}

public enum TeamsProactiveHealthState
{
    Disabled,
    Unavailable,
    Available,
    CapacityPressure
}

/// <summary>
/// Provides bounded state from one binding actor. It contains only counts and
/// classification values. It never includes Teams routing or message data.
/// </summary>
public sealed record TeamsBindingProactiveDiagnostics(
    TeamsProactiveHealthState Health,
    TeamsMigrationHealthState Migration,
    int PersonalDestinationCount,
    int ChannelDestinationCount,
    int PendingDeliveryCount,
    int RetryableFailureCount,
    int PermanentFailureCount,
    int UnknownDeliveryCount,
    int RetainedDeliveryCount,
    bool HasCapacityPressure,
    string? ReasonCode = null) : INoSerializationVerificationNeeded;

public sealed record GetTeamsBindingProactiveDiagnostics : INoSerializationVerificationNeeded
{
    public static readonly GetTeamsBindingProactiveDiagnostics Instance = new();
}

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

    public bool TryGetReminderConversation(SessionId sessionId, out IActorRef conversation)
    {
        conversation = ActorRefs.Nobody;
        if (!TeamsSessionIdentifierCodec.TryParse(sessionId, out var identifier, out _))
            return false;

        var dependencies = CreateDependencies();
        if (identifier.Scope == TeamsConversationScope.Personal)
        {
            conversation = GetOrCreatePersonalConversation(sessionId, dependencies);
            return true;
        }

        if (identifier.Scope != TeamsConversationScope.Channel
            || !TeamsSessionIdentifierCodec.TryCreatePersonal(
                identifier.TenantId,
                identifier.ConversationId,
                out var conversationId,
                out _))
        {
            return false;
        }

        conversation = GetOrCreateChannelConversation(conversationId, dependencies);
        return true;
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
        Receive<TeamsConversationReminder>(HandleReminder);
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

    private void HandleReminder(TeamsConversationReminder reminder)
    {
        if (reminder.Reminder.SessionId != _sessionId)
        {
            Sender.Tell(CommandNack.For(reminder.Reminder.SessionId, "Teams session does not match the personal conversation."));
            return;
        }

        var binding = Context.Child(TeamsActorNames.Binding(_sessionId));
        if (binding.IsNobody())
        {
            binding = Context.ActorOf(
                TeamsSessionBindingActor.CreateProps(_sessionId, _dependencies),
                TeamsActorNames.Binding(_sessionId));
        }

        binding.Forward(new TeamsBindingReminder(reminder.Reminder, reminder.KnownDestinationKey));
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

    internal const int ProactiveDeliveryCapacity = 1_024;

    private const long SnapshotInterval = 64;

    private readonly SessionId _sessionId;
    private readonly TeamsConversationDependencies _dependencies;
    private readonly SessionPipelineHandle _pipelineHandle;
    private readonly ILoggingAdapter _log;
    private readonly bool _isChannelBinding;
    private readonly HashSet<string> _processedActivityIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _processedActivityOrder = new();
    private readonly Dictionary<string, TeamsPendingApproval> _pendingApprovals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TeamsProactiveDeliveryState> _proactiveDeliveries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _proactiveDeliveryGenerations = new(StringComparer.Ordinal);
    private readonly Queue<string> _proactiveDeliveryOrder = new();
    private readonly Dictionary<string, IActorRef> _reminderDeliveryObservers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reminderTextDelivered = new(StringComparer.Ordinal);
    private TeamsOutboundDestination? _destination;
    private long _destinationGeneration;
    private bool _requiresMigrationSnapshot;
    private bool _migrationSnapshotSaveInFlight;
    private bool _migrationSnapshotFailed;
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
        Recover<TeamsProactiveDestinationCaptured>(ApplyDestinationCaptured);
        Recover<TeamsProactiveDeliveryRecorded>(ApplyProactiveDeliveryRecorded);
        Recover<LegacyChannelPersistenceEnvelope>(ApplyLegacyPersistence);
        Recover<RecoveryCompleted>(_ =>
        {
            Self.Tell(new MarkRecoveredProactiveDeliveriesUnknown());
            if (_requiresMigrationSnapshot)
                Self.Tell(new SaveTeamsMigrationSnapshot());
        });
        Recover<SnapshotOffer>(offer =>
        {
            if (offer.Snapshot is TeamsBindingSnapshot snapshot)
                ApplySnapshot(snapshot);
            else if (offer.Snapshot is DurableActivityDispatchSnapshot legacySnapshot)
                ApplySnapshot(new TeamsBindingSnapshot(legacySnapshot.ActivityFingerprints));
            else if (offer.Snapshot is LegacyChannelPersistenceEnvelope legacyEnvelope)
                ApplyLegacyPersistence(legacyEnvelope);
        });

        Context.SetReceiveTimeout(TimeSpan.FromHours(1));
        Command<ReceiveTimeout>(_ => Context.Stop(Self));
        Command<TeamsBindingIngress>(HandleIngress);
        CommandAsync<TeamsBindingReminder>(HandleReminderAsync);
        Command<GetTeamsBindingProactiveDiagnostics>(_ => Sender.Tell(CreateProactiveDiagnostics()));
        CommandAsync<TeamsBindingApprovalAction>(HandleApprovalActionAsync);
        CommandAsync<ForwardTeamsApprovalDecision>(ForwardApprovalDecisionAsync);
        CommandAsync<DeliverTeamsApprovalTerminal>(DeliverApprovalTerminalAsync);
        CommandAsync<DenyTeamsApprovalRequest>(DenyApprovalRequestAsync);
        CommandAsync<DispatchReservedActivity>(DispatchReservedActivityAsync);
        Command<BeginTeamsReminderDispatch>(BeginReminderDispatch);
        CommandAsync<DispatchTeamsReminder>(DispatchReminderAsync);
        Command<MarkRecoveredProactiveDeliveriesUnknown>(_ => MarkRecoveredDeliveriesUnknown());
        Command<SaveTeamsMigrationSnapshot>(_ => SaveMigrationSnapshot());
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
            if (_migrationSnapshotSaveInFlight)
            {
                _migrationSnapshotSaveInFlight = false;
                _migrationSnapshotFailed = false;
                _requiresMigrationSnapshot = false;
            }

            DeleteMessages(saved.Metadata.SequenceNr);
            if (saved.Metadata.SequenceNr > 1)
                DeleteSnapshots(new SnapshotSelectionCriteria(saved.Metadata.SequenceNr - 1));
        });
        Command<SaveSnapshotFailure>(_ =>
        {
            if (_migrationSnapshotSaveInFlight)
            {
                _migrationSnapshotSaveInFlight = false;
                _migrationSnapshotFailed = true;
                ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("teams_migration_snapshot_failed");
                return;
            }

            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("personal_processed_state_snapshot_failed");
        });
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

        TeamsOutboundDestination destination;
        try
        {
            destination = CreateDestination(ingress.Activity);
        }
        catch (ArgumentException)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Denied));
            return;
        }
        catch (InvalidOperationException)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Denied));
            return;
        }

        if (!Equals(_destination, destination))
        {
            var generation = _destinationGeneration == 0 ? 1 : checked(_destinationGeneration + 1);
            Persist(ToCapturedDestination(destination, generation), captured =>
            {
                ApplyDestinationCaptured(captured);
                RecordDestinationTelemetry("proactive_destination_captured");
                ReserveIngress(ingress, activityFingerprint, replyTo);
            });
            return;
        }

        ReserveIngress(ingress, activityFingerprint, replyTo);
    }

    private void ReserveIngress(
        TeamsBindingIngress ingress,
        string activityFingerprint,
        IActorRef replyTo)
    {
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

    private async Task HandleReminderAsync(TeamsBindingReminder received)
    {
        var reminder = received.Reminder;
        var replyTo = Sender;
        if (reminder.SessionId != _sessionId
            || reminder.Source.ReminderId is not { } reminderId
            || string.IsNullOrWhiteSpace(reminderId.Value))
        {
            replyTo.Tell(CommandNack.For(reminder.SessionId, "Teams reminder delivery is invalid."));
            return;
        }

        var destinationResolution = ResolveReminderDestination(received.KnownDestinationKey);
        if (destinationResolution.Disposition != TeamsDestinationResolutionDisposition.Resolved)
        {
            RecordDestinationTelemetry(destinationResolution.ReasonCode ?? "proactive_destination_missing");
            replyTo.Tell(CommandNack.For(_sessionId, "Teams proactive destination is unavailable."));
            return;
        }

        var deliveryKey = reminderId.Value;
        if (_proactiveDeliveries.TryGetValue(deliveryKey, out var state))
        {
            if (state == TeamsProactiveDeliveryState.Sent)
            {
                replyTo.Tell(CommandAck.For(_sessionId));
                if (reminder.Source.DeliveryObserver is { } observer)
                    observer.Tell(new ReminderDeliveryResult(reminderId, ChannelType.Teams, true,
                        ObservedAtMs: _dependencies.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
                return;
            }

            if (state is TeamsProactiveDeliveryState.Sending or TeamsProactiveDeliveryState.DeliveryUnknown or TeamsProactiveDeliveryState.FailedPermanent)
            {
                replyTo.Tell(CommandNack.For(_sessionId, "Teams reminder delivery requires operator review."));
                return;
            }

            // Pending is safe to resume because no send attempt was recorded.
            // FailedRetryable is retried only when the generic reminder system
            // redelivers this same stable delivery key.
            Self.Tell(new BeginTeamsReminderDispatch(reminder, replyTo, deliveryKey));
            return;
        }

        if (_proactiveDeliveryOrder.Count >= ProactiveDeliveryCapacity)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("proactive_delivery_capacity_reached");
            replyTo.Tell(CommandNack.For(_sessionId, "Teams proactive delivery capacity is reached."));
            return;
        }

        Persist(new TeamsProactiveDeliveryRecorded
        {
            DeliveryKey = deliveryKey,
            State = (int)TeamsProactiveDeliveryState.Pending,
            DestinationGeneration = _destinationGeneration
        }, recorded =>
        {
            ApplyProactiveDeliveryRecorded(recorded);
            Self.Tell(new BeginTeamsReminderDispatch(reminder, replyTo, deliveryKey));
        });
    }

    private void BeginReminderDispatch(BeginTeamsReminderDispatch dispatch)
    {
        if (!_proactiveDeliveries.TryGetValue(dispatch.DeliveryKey, out var state)
            || state is not (TeamsProactiveDeliveryState.Pending or TeamsProactiveDeliveryState.FailedRetryable))
        {
            return;
        }

        Persist(new TeamsProactiveDeliveryRecorded
        {
            DeliveryKey = dispatch.DeliveryKey,
            State = (int)TeamsProactiveDeliveryState.Sending,
            DestinationGeneration = _proactiveDeliveryGenerations.GetValueOrDefault(dispatch.DeliveryKey, _destinationGeneration)
        }, recorded =>
        {
            ApplyProactiveDeliveryRecorded(recorded);
            Self.Tell(new DispatchTeamsReminder(dispatch.Reminder, dispatch.ReplyTo, dispatch.DeliveryKey));
        });
    }

    private async Task DispatchReminderAsync(DispatchTeamsReminder dispatch)
    {
        try
        {
            var writer = await EnsurePipelineAsync(CancellationToken.None);
            var source = dispatch.Reminder.Source with { AckTarget = dispatch.ReplyTo };
            if (source.DeliveryObserver is { } observer)
                _reminderDeliveryObservers[dispatch.DeliveryKey] = observer;

            await writer.WriteAsync(new ChannelInput
            {
                SenderId = source.SenderId,
                ChannelId = _destination!.ConversationId,
                MessageId = source.MessageId,
                Audience = source.Audience,
                Boundary = source.Boundary,
                Principal = source.Principal,
                Provenance = source.Provenance,
                Contents = [new TextContent(dispatch.Reminder.Content)],
                ReceivedAt = _dependencies.TimeProvider.GetUtcNow(),
                ExecutableText = dispatch.Reminder.Content,
                ReminderId = source.ReminderId,
                AckTarget = source.AckTarget
            }, CancellationToken.None);
            ChannelTelemetry.For(ChannelType.Teams).RecordExtra("proactive_delivery_attempted");
        }
        catch (ChannelClosedException)
        {
            CompleteReminderFailure(dispatch.DeliveryKey, "Teams pipeline is unavailable.");
            dispatch.ReplyTo.Tell(CommandNack.For(_sessionId, "Teams pipeline is unavailable."));
        }
        catch (Exception)
        {
            CompleteReminderFailure(dispatch.DeliveryKey, "Teams pipeline dispatch failed.");
            dispatch.ReplyTo.Tell(CommandNack.For(_sessionId, "Teams pipeline dispatch failed."));
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

    private void ApplyDestinationCaptured(TeamsProactiveDestinationCaptured captured)
    {
        TeamsOutboundDestination destination;
        try
        {
            destination = new TeamsOutboundDestination(
                captured.TenantId,
                captured.ConversationId,
                (TeamsConversationScope)captured.Scope,
                captured.ServiceUrl,
                captured.RootActivityId,
                captured.TeamId,
                captured.ChannelId,
                captured.UserId);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("The Teams proactive destination state is invalid.", exception);
        }

        if (!MatchesBinding(destination))
            throw new InvalidOperationException("The Teams proactive destination does not match its binding session.");

        if (captured.Generation < 1)
            throw new InvalidOperationException("The Teams proactive destination generation is invalid.");
        if (_destination is not null && captured.Generation <= _destinationGeneration)
            throw new InvalidOperationException("The Teams proactive destination generation must advance.");

        _destination = destination;
        _destinationGeneration = captured.Generation;
    }

    private void ApplyProactiveDeliveryRecorded(TeamsProactiveDeliveryRecorded recorded)
    {
        if (!IsBoundedDeliveryKey(recorded.DeliveryKey)
            || !Enum.IsDefined((TeamsProactiveDeliveryState)recorded.State)
            || recorded.DestinationGeneration < 1)
        {
            throw new InvalidOperationException("The Teams proactive delivery state is invalid.");
        }
        if (_destinationGeneration > 0 && recorded.DestinationGeneration > _destinationGeneration)
            throw new InvalidOperationException("The Teams proactive delivery references an unknown destination generation.");

        if (recorded.EvictedDeliveryKey is { } evicted)
        {
            if (!IsBoundedDeliveryKey(evicted)
                || _proactiveDeliveryOrder.Count == 0
                || !string.Equals(_proactiveDeliveryOrder.Peek(), evicted, StringComparison.Ordinal)
                || !_proactiveDeliveries.Remove(evicted)
                || !_proactiveDeliveryGenerations.Remove(evicted))
            {
                throw new InvalidOperationException("The Teams proactive delivery retention state is invalid.");
            }

            _proactiveDeliveryOrder.Dequeue();
        }

        if (_proactiveDeliveries.ContainsKey(recorded.DeliveryKey))
        {
            _proactiveDeliveries[recorded.DeliveryKey] = (TeamsProactiveDeliveryState)recorded.State;
            _proactiveDeliveryGenerations[recorded.DeliveryKey] = recorded.DestinationGeneration;
            ApplyDestinationInvalidation(recorded);
            return;
        }

        if (_proactiveDeliveryOrder.Count >= ProactiveDeliveryCapacity)
            throw new InvalidOperationException("The Teams proactive delivery state exceeds its retention limit.");

        _proactiveDeliveries.Add(recorded.DeliveryKey, (TeamsProactiveDeliveryState)recorded.State);
        _proactiveDeliveryGenerations.Add(recorded.DeliveryKey, recorded.DestinationGeneration);
        _proactiveDeliveryOrder.Enqueue(recorded.DeliveryKey);
        ApplyDestinationInvalidation(recorded);
    }

    private bool MatchesBinding(TeamsOutboundDestination destination)
    {
        if (destination.Scope == TeamsConversationScope.Personal)
        {
            return TeamsSessionIdentifierCodec.TryCreatePersonal(
                       destination.TenantId,
                       destination.ConversationId,
                       out var sessionId,
                       out _)
                   && sessionId == _sessionId;
        }

        return TeamsSessionIdentifierCodec.TryCreateChannel(
                   destination.TenantId,
                   destination.ConversationId,
                   destination.RootActivityId!,
                   out var channelSessionId,
                   out _)
               && channelSessionId == _sessionId;
    }

    private static bool IsBoundedDeliveryKey(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Encoding.UTF8.GetByteCount(value) <= TeamsSessionIdentifierCodec.MaxRawIdentifierBytes;

    private TeamsDestinationResolution ResolveReminderDestination(string? knownDestinationKey)
    {
        if (!TeamsSessionIdentifierCodec.TryParse(_sessionId, out var identifier, out _))
            return new TeamsDestinationResolution(TeamsDestinationResolutionDisposition.Rejected, ReasonCode: "invalid_binding_session");

        var candidates = _destination is null
            ? Array.Empty<TeamsProactiveDestinationCandidate>()
            : [new TeamsProactiveDestinationCandidate(
                _sessionId,
                identifier.Scope,
                _destinationGeneration,
                IsDestinationActive(_destination))];

        return TeamsProactiveDestinationResolver.Resolve(
            _sessionId,
            identifier.Scope,
            knownDestinationKey,
            candidates);
    }

    private bool IsDestinationActive(TeamsOutboundDestination destination) =>
        _destinationGeneration > 0 && MatchesBinding(destination);

    private static TeamsProactiveDestinationCaptured ToCapturedDestination(TeamsOutboundDestination destination, long generation) => new()
    {
        TenantId = destination.TenantId,
        ConversationId = destination.ConversationId,
        Scope = (int)destination.Scope,
        ServiceUrl = destination.ServiceUrl,
        RootActivityId = destination.RootActivityId,
        TeamId = destination.TeamId,
        ChannelId = destination.ChannelId,
        UserId = destination.UserId,
        Generation = generation
    };

    private void ApplyDestinationInvalidation(TeamsProactiveDeliveryRecorded recorded)
    {
        if (recorded.InvalidatesDestination && _destinationGeneration == recorded.DestinationGeneration)
        {
            _destination = null;
            RecordDestinationTelemetry("proactive_destination_invalidated");
        }
    }

    private void ApplyLegacyPersistence(LegacyChannelPersistenceEnvelope legacy)
    {
        _requiresMigrationSnapshot = true;
        switch (legacy.Manifest)
        {
            case "tapc-v1":
            {
                var value = LegacyProto.TeamsApprovalPendingCreatedProto.Parser.ParseFrom(legacy.Payload);
                ApplyApprovalPendingCreated(new TeamsApprovalPendingCreated
                {
                    CallId = value.CallId,
                    CorrelationId = value.CorrelationId,
                    NonceHash = value.NonceHash,
                    RequesterSenderId = value.HasRequesterSenderId ? value.RequesterSenderId : null,
                    RequesterPrincipal = value.HasRequesterPrincipal ? (PrincipalClassification)value.RequesterPrincipal : null,
                    ExpiresAtUnixMilliseconds = value.ExpiresAtUnixMilliseconds
                });
                break;
            }
            case "tacd-v1":
            {
                var value = LegacyProto.TeamsApprovalCardDeliveredProto.Parser.ParseFrom(legacy.Payload);
                ApplyApprovalCardDelivered(new TeamsApprovalCardDelivered { CorrelationId = value.CorrelationId, PromptId = value.PromptId });
                break;
            }
            case "taco-v1":
            {
                var value = LegacyProto.TeamsApprovalConsumedProto.Parser.ParseFrom(legacy.Payload);
                ApplyApprovalConsumed(new TeamsApprovalConsumed
                {
                    CorrelationId = value.CorrelationId, Decision = value.Decision,
                    ConsumedAtUnixMilliseconds = value.ConsumedAtUnixMilliseconds
                });
                break;
            }
            case "tpdc-v1":
                ApplyLegacyDestination(LegacyProto.TeamsProactiveDestinationCapturedProto.Parser.ParseFrom(legacy.Payload));
                break;
            case "tpdi-v1":
                _destination = null;
                break;
            case "tpdr-v1":
            {
                var value = LegacyProto.TeamsProactiveDeliveryRecordedProto.Parser.ParseFrom(legacy.Payload);
                ApplyProactiveDeliveryRecorded(new TeamsProactiveDeliveryRecorded
                {
                    DeliveryKey = value.DeliveryKey,
                    State = value.State,
                    EvictedDeliveryKey = value.HasEvictedDeliveryKey ? value.EvictedDeliveryKey : null,
                    DestinationGeneration = Math.Max(1, _destinationGeneration)
                });
                break;
            }
            case "dads-v1":
                ApplyLegacySnapshot(LegacyProto.DurableActivityDispatchSnapshotProto.Parser.ParseFrom(legacy.Payload));
                break;
            default:
                throw new InvalidOperationException("The legacy Team persistence manifest is not valid for a binding actor.");
        }
    }

    private void ApplyLegacyDestination(LegacyProto.TeamsProactiveDestinationCapturedProto value) =>
        ApplyDestinationCaptured(new TeamsProactiveDestinationCaptured
        {
            TenantId = value.TenantId,
            ConversationId = value.ConversationId,
            Scope = value.Scope,
            ServiceUrl = value.ServiceUrl,
            RootActivityId = value.HasRootActivityId ? value.RootActivityId : null,
            TeamId = value.HasTeamId ? value.TeamId : null,
            ChannelId = value.HasChannelId ? value.ChannelId : null,
            UserId = value.HasUserId ? value.UserId : null,
            Generation = _destination is null ? 1 : checked(_destinationGeneration + 1)
        });

    private void ApplyLegacySnapshot(LegacyProto.DurableActivityDispatchSnapshotProto value)
    {
        ApplySnapshot(new TeamsBindingSnapshot(value.ActivityFingerprints.ToArray())
        {
            Approvals = value.TeamsApprovals.Select(approval => new TeamsApprovalSnapshotEntry
            {
                CallId = approval.CallId,
                CorrelationId = approval.CorrelationId,
                NonceHash = approval.NonceHash,
                RequesterSenderId = approval.HasRequesterSenderId ? approval.RequesterSenderId : null,
                RequesterPrincipal = approval.HasRequesterPrincipal ? (PrincipalClassification)approval.RequesterPrincipal : null,
                ExpiresAtUnixMilliseconds = approval.ExpiresAtUnixMilliseconds,
                PromptId = approval.HasPromptId ? approval.PromptId : null,
                Decision = approval.HasDecision ? approval.Decision : null
            }).ToArray(),
            Destination = value.TeamsDestination is null ? null : new TeamsProactiveDestinationCaptured
            {
                TenantId = value.TeamsDestination.TenantId,
                ConversationId = value.TeamsDestination.ConversationId,
                Scope = value.TeamsDestination.Scope,
                ServiceUrl = value.TeamsDestination.ServiceUrl,
                RootActivityId = value.TeamsDestination.HasRootActivityId ? value.TeamsDestination.RootActivityId : null,
                TeamId = value.TeamsDestination.HasTeamId ? value.TeamsDestination.TeamId : null,
                ChannelId = value.TeamsDestination.HasChannelId ? value.TeamsDestination.ChannelId : null,
                UserId = value.TeamsDestination.HasUserId ? value.TeamsDestination.UserId : null,
                Generation = 1
            },
            ProactiveDeliveries = value.TeamsProactiveDeliveries.Select(delivery => new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = delivery.DeliveryKey,
                State = delivery.State,
                DestinationGeneration = 1
            }).ToArray()
        });
    }

    private static void RecordDestinationTelemetry(string code) =>
        ChannelTelemetry.For(ChannelType.Teams).RecordExtra(code);

    private void MarkRecoveredDeliveriesUnknown()
    {
        var interrupted = _proactiveDeliveryOrder
            .Where(key => _proactiveDeliveries[key] == TeamsProactiveDeliveryState.Sending)
            .Select(key => new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = key,
                State = (int)TeamsProactiveDeliveryState.DeliveryUnknown,
                DestinationGeneration = _proactiveDeliveryGenerations[key]
            })
            .ToArray();
        if (interrupted.Length > 0)
            PersistAll(interrupted, ApplyProactiveDeliveryRecorded);
    }

    private void CompleteReminderFailure(string deliveryKey, string safeReason)
    {
        Persist(new TeamsProactiveDeliveryRecorded
        {
            DeliveryKey = deliveryKey,
            State = (int)TeamsProactiveDeliveryState.FailedRetryable,
            DestinationGeneration = _proactiveDeliveryGenerations.GetValueOrDefault(deliveryKey, _destinationGeneration)
        }, recorded =>
        {
            ApplyProactiveDeliveryRecorded(recorded);
            if (_reminderDeliveryObservers.Remove(deliveryKey, out var observer))
            {
                observer.Tell(new ReminderDeliveryResult(
                    new ReminderId(deliveryKey),
                    ChannelType.Teams,
                    false,
                    safeReason,
                    _dependencies.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
            }
        });
    }

    private void ApplySnapshot(TeamsBindingSnapshot snapshot)
    {
        if (snapshot.MigrationVersion != TeamsBindingSnapshot.CurrentMigrationVersion)
            throw new InvalidOperationException("The Teams binding snapshot version is not supported.");
        if (snapshot.ActivityFingerprints.Count > ProcessedActivityCapacity)
            throw new InvalidOperationException("The Teams processed activity snapshot exceeds its retention limit.");
        if (snapshot.Approvals.Count > ApprovalCapacity)
            throw new InvalidOperationException("The Teams approval snapshot exceeds its retention limit.");
        if (snapshot.ProactiveDeliveries.Count > ProactiveDeliveryCapacity)
            throw new InvalidOperationException("The Teams proactive delivery snapshot exceeds its retention limit.");

        _processedActivityIds.Clear();
        _processedActivityOrder.Clear();
        _pendingApprovals.Clear();
        _proactiveDeliveries.Clear();
        _proactiveDeliveryGenerations.Clear();
        _proactiveDeliveryOrder.Clear();
        if (snapshot.LastDestinationGeneration < 0)
            throw new InvalidOperationException("The Teams proactive destination snapshot generation is invalid.");
        _destination = null;
        _destinationGeneration = snapshot.LastDestinationGeneration;
        foreach (var fingerprint in snapshot.ActivityFingerprints)
        {
            EnsureValidFingerprint(fingerprint);
            if (!_processedActivityIds.Add(fingerprint))
                throw new InvalidOperationException("The Teams processed activity snapshot contains a duplicate entry.");

            _processedActivityOrder.Enqueue(fingerprint);
        }

        foreach (var approval in snapshot.Approvals)
        {
            ApplyApprovalPendingCreated(new TeamsApprovalPendingCreated
            {
                CallId = approval.CallId,
                CorrelationId = approval.CorrelationId,
                NonceHash = approval.NonceHash,
                RequesterSenderId = approval.RequesterSenderId,
                RequesterPrincipal = approval.RequesterPrincipal,
                ExpiresAtUnixMilliseconds = approval.ExpiresAtUnixMilliseconds
            });
            if (approval.PromptId is not null)
            {
                ApplyApprovalCardDelivered(new TeamsApprovalCardDelivered
                {
                    CorrelationId = approval.CorrelationId,
                    PromptId = approval.PromptId
                });
            }
            if (approval.Decision is not null)
            {
                ApplyApprovalConsumed(new TeamsApprovalConsumed
                {
                    CorrelationId = approval.CorrelationId,
                    Decision = approval.Decision
                });
            }
        }

        if (snapshot.Destination is not null)
        {
            if (snapshot.LastDestinationGeneration > 0
                && snapshot.LastDestinationGeneration != snapshot.Destination.Generation)
            {
                throw new InvalidOperationException("The Teams proactive destination snapshot generation is inconsistent.");
            }
            ApplyDestinationCaptured(new TeamsProactiveDestinationCaptured
            {
                TenantId = snapshot.Destination.TenantId,
                ConversationId = snapshot.Destination.ConversationId,
                Scope = snapshot.Destination.Scope,
                ServiceUrl = snapshot.Destination.ServiceUrl,
                RootActivityId = snapshot.Destination.RootActivityId,
                TeamId = snapshot.Destination.TeamId,
                ChannelId = snapshot.Destination.ChannelId,
                UserId = snapshot.Destination.UserId,
                Generation = snapshot.Destination.Generation
            });
        }

        foreach (var delivery in snapshot.ProactiveDeliveries)
        {
            ApplyProactiveDeliveryRecorded(new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = delivery.DeliveryKey,
                State = delivery.State,
                DestinationGeneration = delivery.DestinationGeneration,
                InvalidatesDestination = delivery.InvalidatesDestination
            });
        }
    }

    private void SaveSnapshotWhenDue()
    {
        if (!IsRecovering && LastSequenceNr > 0 && LastSequenceNr % SnapshotInterval == 0)
            SaveSnapshot(CreateSnapshot());
    }

    private void SaveMigrationSnapshot()
    {
        if (IsRecovering || !_requiresMigrationSnapshot || _migrationSnapshotSaveInFlight)
            return;

        _migrationSnapshotSaveInFlight = true;
        SaveSnapshot(CreateSnapshot());
    }

    private TeamsBindingProactiveDiagnostics CreateProactiveDiagnostics()
    {
        var migration = _requiresMigrationSnapshot
            ? _migrationSnapshotFailed
                ? TeamsMigrationHealthState.Failed
                : TeamsMigrationHealthState.Pending
            : _destination is null && _destinationGeneration == 0
                ? TeamsMigrationHealthState.NotRequired
                : TeamsMigrationHealthState.Completed;
        var pending = _proactiveDeliveries.Values.Count(state => state is TeamsProactiveDeliveryState.Pending or TeamsProactiveDeliveryState.Sending);
        var retryable = _proactiveDeliveries.Values.Count(state => state == TeamsProactiveDeliveryState.FailedRetryable);
        var permanent = _proactiveDeliveries.Values.Count(state => state == TeamsProactiveDeliveryState.FailedPermanent);
        var unknown = _proactiveDeliveries.Values.Count(state => state == TeamsProactiveDeliveryState.DeliveryUnknown);
        var hasCapacityPressure = _proactiveDeliveryOrder.Count >= ProactiveDeliveryCapacity;

        if (!_dependencies.Options.Enabled)
        {
            return new TeamsBindingProactiveDiagnostics(
                TeamsProactiveHealthState.Disabled,
                migration,
                0,
                0,
                pending,
                retryable,
                permanent,
                unknown,
                _proactiveDeliveryOrder.Count,
                hasCapacityPressure,
                "teams_disabled");
        }

        if (hasCapacityPressure)
        {
            return new TeamsBindingProactiveDiagnostics(
                TeamsProactiveHealthState.CapacityPressure,
                migration,
                _destination?.Scope == TeamsConversationScope.Personal ? 1 : 0,
                _destination?.Scope == TeamsConversationScope.Channel ? 1 : 0,
                pending,
                retryable,
                permanent,
                unknown,
                _proactiveDeliveryOrder.Count,
                true,
                "proactive_delivery_capacity_reached");
        }

        if (_destination is null)
        {
            return new TeamsBindingProactiveDiagnostics(
                TeamsProactiveHealthState.Unavailable,
                migration,
                0,
                0,
                pending,
                retryable,
                permanent,
                unknown,
                _proactiveDeliveryOrder.Count,
                false,
                "proactive_destination_missing");
        }

        return new TeamsBindingProactiveDiagnostics(
            TeamsProactiveHealthState.Available,
            migration,
            _destination.Scope == TeamsConversationScope.Personal ? 1 : 0,
            _destination.Scope == TeamsConversationScope.Channel ? 1 : 0,
            pending,
            retryable,
            permanent,
            unknown,
            _proactiveDeliveryOrder.Count,
            false);
    }

    private TeamsBindingSnapshot CreateSnapshot() => new(_processedActivityOrder.ToArray())
    {
        Approvals = _pendingApprovals.Values.Select(pending => new TeamsApprovalSnapshotEntry
        {
            CallId = pending.CallId,
            CorrelationId = pending.CorrelationId,
            NonceHash = pending.NonceHash,
            RequesterSenderId = pending.RequesterSenderId,
            RequesterPrincipal = pending.RequesterPrincipal,
            ExpiresAtUnixMilliseconds = pending.ExpiresAtUnixMilliseconds,
            PromptId = pending.PromptId,
            Decision = pending.Decision
        }).ToArray(),
        Destination = _destination is null ? null : ToCapturedDestination(_destination, _destinationGeneration),
        LastDestinationGeneration = _destinationGeneration,
        ProactiveDeliveries = _proactiveDeliveryOrder.Select(key => new TeamsProactiveDeliveryRecorded
        {
            DeliveryKey = key,
            State = (int)_proactiveDeliveries[key],
            DestinationGeneration = _proactiveDeliveryGenerations[key]
        }).ToArray()
    };

    private async Task HandleOutputAsync(BindingOutput received)
    {
        if (received.Output is TurnCompleted completed)
        {
            HandleReminderTurnCompleted(completed);
            return;
        }

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

        var reminderDeliveryKey = received.Output.SourceReminderId?.Value;
        if (reminderDeliveryKey is not null)
        {
            if (!_proactiveDeliveries.TryGetValue(reminderDeliveryKey, out var reminderState)
                || reminderState != TeamsProactiveDeliveryState.Sending)
            {
                ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("proactive_delivery_completion_ignored");
                return;
            }

            if (_proactiveDeliveryGenerations[reminderDeliveryKey] != _destinationGeneration)
            {
                CompleteReminderFailure(
                    reminderDeliveryKey,
                    "The Teams proactive destination changed before delivery completed.");
                return;
            }
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
                if (reminderDeliveryKey is not null)
                    CompleteReminderDelivery(reminderDeliveryKey, result);
                return;
            }

            _processingActivityId = null;
        }

        if (reminderDeliveryKey is not null)
        {
            _reminderTextDelivered.Add(reminderDeliveryKey);
            CompleteReminderDelivery(reminderDeliveryKey, new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered));
        }
    }

    private void HandleReminderTurnCompleted(TurnCompleted completed)
    {
        if (completed.SourceReminderId is not { } reminderId
            || string.IsNullOrWhiteSpace(reminderId.Value))
            return;

        var deliveryKey = reminderId.Value;
        if (!_proactiveDeliveries.TryGetValue(deliveryKey, out var state))
            return;

        if (state != TeamsProactiveDeliveryState.Sending)
        {
            _reminderTextDelivered.Remove(deliveryKey);
            return;
        }

        if (!_reminderTextDelivered.Remove(deliveryKey))
        {
            CompleteReminderFailure(deliveryKey, "Teams reminder completed without a delivered message.");
            return;
        }

    }

    private void CompleteReminderDelivery(string deliveryKey, TeamsDeliveryResult result)
    {
        var state = result.IsSuccess
            ? TeamsProactiveDeliveryState.Sent
            : result.Status == TeamsDeliveryStatus.InvalidDestination
                ? TeamsProactiveDeliveryState.FailedPermanent
                : TeamsProactiveDeliveryState.FailedRetryable;
        Persist(new TeamsProactiveDeliveryRecorded
        {
            DeliveryKey = deliveryKey,
            State = (int)state,
            DestinationGeneration = _proactiveDeliveryGenerations.GetValueOrDefault(deliveryKey, _destinationGeneration),
            InvalidatesDestination = state == TeamsProactiveDeliveryState.FailedPermanent
        }, recorded =>
        {
            ApplyProactiveDeliveryRecorded(recorded);
            SaveSnapshotWhenDue();

            if (_reminderDeliveryObservers.Remove(deliveryKey, out var observer))
            {
                observer.Tell(new ReminderDeliveryResult(
                    new ReminderId(deliveryKey),
                    ChannelType.Teams,
                    result.IsSuccess,
                    result.IsSuccess ? null : "Teams proactive delivery failed.",
                    _dependencies.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
            }

            ChannelTelemetry.For(ChannelType.Teams).RecordExtra(result.IsSuccess
                ? "proactive_delivery_delivered"
                : state == TeamsProactiveDeliveryState.FailedPermanent
                    ? "proactive_delivery_failed_permanent"
                    : "proactive_delivery_failed_retryable");
        });
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
            || created.NonceHash.Any(static character => !char.IsAsciiHexDigit(character))
            || created.ExpiresAtUnixMilliseconds <= 0
            || string.IsNullOrWhiteSpace(created.CallId))
        {
            throw new InvalidOperationException("The Teams approval state is invalid.");
        }

        if (_pendingApprovals.Count >= ApprovalCapacity && !_pendingApprovals.ContainsKey(created.CorrelationId))
            throw new InvalidOperationException("The Teams approval state exceeds its retention limit.");

        if (!_pendingApprovals.TryAdd(created.CorrelationId, new TeamsPendingApproval(
            created.CallId,
            created.CorrelationId,
            created.NonceHash,
            created.RequesterSenderId,
            created.RequesterPrincipal,
            created.ExpiresAtUnixMilliseconds)))
        {
            throw new InvalidOperationException("The Teams approval state contains a duplicate correlation.");
        }
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

    private sealed record BeginTeamsReminderDispatch(
        DeliverTrustedSessionTurn Reminder,
        IActorRef ReplyTo,
        string DeliveryKey) : INoSerializationVerificationNeeded;

    private sealed record DispatchTeamsReminder(
        DeliverTrustedSessionTurn Reminder,
        IActorRef ReplyTo,
        string DeliveryKey) : INoSerializationVerificationNeeded;

    private sealed record MarkRecoveredProactiveDeliveriesUnknown : INoSerializationVerificationNeeded;

    private sealed record SaveTeamsMigrationSnapshot : INoSerializationVerificationNeeded;

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
