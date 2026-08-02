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
using Netclaw.Channels;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using static Netclaw.Actors.Sessions.SessionProtocol;

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

        if (activity.Kind == TeamsIngressActivityKind.Message)
        {
            var policy = TeamsChannelAclPolicy.Evaluate(activity, _options);
            if (policy.Disposition == TeamsChannelPolicyDisposition.Ignored)
                return TeamsIngressSinkResult.Ignored;
            if (policy.Disposition != TeamsChannelPolicyDisposition.Allowed)
                return TeamsIngressSinkResult.Denied;
        }

        return await RouteChannelAsync(activity, conversationId, cancellationToken);
    }

    private async ValueTask<TeamsIngressSinkResult> RoutePersonalAsync(
        TeamsInboundActivity activity,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var conversation = GetOrCreatePersonalConversation(sessionId, new TeamsConversationDependencies(
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

    private async ValueTask<TeamsIngressSinkResult> RouteChannelAsync(
        TeamsInboundActivity activity,
        SessionId conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = GetOrCreateChannelConversation(conversationId, new TeamsConversationDependencies(
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
}

/// <summary>
/// Owns the personal Teams session pipeline and durable activity duplicate
/// history. A durable reservation occurs before local queue admission.
/// </summary>
public sealed class TeamsSessionBindingActor : ReceivePersistentActor
{
    internal const int ProcessedActivityCapacity = 1_024;

    private const long SnapshotInterval = 64;

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
        Recover<SnapshotOffer>(offer =>
        {
            if (offer.Snapshot is DurableActivityDispatchSnapshot snapshot)
                ApplySnapshot(snapshot);
        });

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
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("personal_acl_denied");
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

        return await _pipelineHandle.InitializeWithChannelAsync(
            Context,
            _sessionId,
            new SessionPipelineOptions
            {
                ChannelType = ChannelType.Teams,
                Filter = OutputFilter.None
            },
            DiscardDeferredOutput,
            (generation, cause) => Self.Tell(new OutputStreamTerminated(generation, cause)),
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

    private static void DiscardDeferredOutput(SessionOutput _)
        => ChannelTelemetry.For(ChannelType.Teams).RecordExtra("personal_output_deferred");

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

}

internal static class ActivityFingerprint
{
    public static string Create(string activityId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(activityId)));
}
