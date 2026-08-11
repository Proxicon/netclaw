// -----------------------------------------------------------------------
// <copyright file="TeamsChannelConversationActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Akka.Persistence;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using Netclaw.Channels.Telemetry;
using static Netclaw.Actors.Sessions.SessionProtocol;
using LegacyProto = Netclaw.Actors.Serialization.Proto;

namespace Netclaw.Channels.Teams;

/// <summary>
/// The sole durable owner of a channel conversation's activity routing index.
/// It stores SHA-256 fingerprints with canonical encoded session IDs so edits
/// and deletes can find their root without raw Teams identifiers or a model
/// turn.
/// </summary>
public sealed class TeamsConversationActor : ReceivePersistentActor
{
    internal const int ActivityIndexCapacity = 1_024;
    private const long SnapshotInterval = 64;
    private static readonly TimeSpan BindingRouteTimeout = TimeSpan.FromSeconds(10);

    private readonly SessionId _conversationId;
    private readonly TeamsConversationDependencies _dependencies;
    private readonly ILoggingAdapter _log;
    private readonly Dictionary<string, SessionId> _activitySessions = new(StringComparer.Ordinal);
    private readonly Queue<string> _activityOrder = new();
    private bool _requiresMigrationSnapshot;

    public TeamsConversationActor(SessionId conversationId, TeamsConversationDependencies dependencies)
    {
        _conversationId = conversationId;
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        _log = Context.GetLogger().WithContext("Adapter", "teams");

        Recover<TeamsChannelActivityMapped>(ApplyMapped);
        Recover<LegacyChannelPersistenceEnvelope>(ApplyLegacyPersistence);
        Recover<RecoveryCompleted>(_ =>
        {
            if (_requiresMigrationSnapshot)
                Self.Tell(new SaveTeamsChannelMigrationSnapshot());
        });
        Recover<SnapshotOffer>(offer =>
        {
            if (offer.Snapshot is TeamsChannelActivityIndexSnapshot snapshot)
                ApplySnapshot(snapshot);
            else if (offer.Snapshot is LegacyChannelPersistenceEnvelope legacy)
                ApplyLegacyPersistence(legacy);
        });

        Command<TeamsConversationIngress>(HandleIngress);
        CommandAsync<TeamsConversationApprovalAction>(HandleApprovalActionAsync);
        Command<TeamsConversationReminder>(HandleReminder);
        CommandAsync<RouteChannelBinding>(RouteBindingAsync);
        Command<SaveSnapshotSuccess>(saved =>
        {
            DeleteMessages(saved.Metadata.SequenceNr);
            if (saved.Metadata.SequenceNr > 1)
                DeleteSnapshots(new SnapshotSelectionCriteria(saved.Metadata.SequenceNr - 1));
        });
        Command<SaveSnapshotFailure>(_ =>
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("channel_activity_index_snapshot_failed"));
        Command<SaveTeamsChannelMigrationSnapshot>(_ => SaveMigrationSnapshot());
    }

    public override string PersistenceId =>
        "teams-channel-conversation-" + Uri.EscapeDataString(_conversationId.Value);

    public static Props CreateProps(SessionId conversationId, TeamsConversationDependencies dependencies) =>
        Props.Create(() => new TeamsConversationActor(conversationId, dependencies));

    private void HandleIngress(TeamsConversationIngress ingress)
    {
        var replyTo = Sender;
        if (ingress.CancellationToken.IsCancellationRequested)
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Cancelled));
            return;
        }

        if (!IsExpectedConversation(ingress.Activity))
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Unavailable));
            return;
        }

        var policy = TeamsChannelAclPolicy.Evaluate(ingress.Activity, _dependencies.Options);
        if (policy.Disposition == TeamsChannelPolicyDisposition.Ignored)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("channel_unmentioned");
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Ignored));
            return;
        }

        if (policy.Disposition != TeamsChannelPolicyDisposition.Allowed)
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped(policy.ReasonCode);
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Denied));
            return;
        }

        if (ingress.Activity.Kind is TeamsIngressActivityKind.MessageUpdate or TeamsIngressActivityKind.MessageDelete)
        {
            HandleMutation(ingress.Activity, replyTo);
            return;
        }

        if (string.IsNullOrWhiteSpace(ingress.Activity.Text))
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("channel_empty_prompt");
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Ignored));
            return;
        }

        var rootId = ingress.Activity.Reply!.RootActivityId!;
        if (!TeamsSessionIdentifierCodec.TryCreateChannel(
                ingress.Activity.Trust.TenantId,
                ingress.Activity.Trust.ConversationId,
                rootId,
                out var sessionId,
                out _))
        {
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Denied));
            return;
        }

        var fingerprint = ActivityFingerprint.Create(ingress.Activity.Trust.ActivityId);
        if (_activitySessions.TryGetValue(fingerprint, out var indexedSessionId))
        {
            if (indexedSessionId != sessionId)
            {
                replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Denied));
                return;
            }

            Self.Tell(new RouteChannelBinding(ingress.Activity, sessionId, replyTo, ingress.CancellationToken));
            return;
        }

        var evicted = _activityOrder.Count == ActivityIndexCapacity ? _activityOrder.Peek() : null;
        Persist(
            new TeamsChannelActivityMapped(fingerprint, sessionId.Value, evicted),
            mapped =>
            {
                ApplyMapped(mapped);
                SaveSnapshotWhenDue();
                ChannelTelemetry.For(ChannelType.Teams).RecordExtra("channel_activity_mapping_stored");
                Self.Tell(new RouteChannelBinding(ingress.Activity, sessionId, replyTo, ingress.CancellationToken));
            });
    }

    private void HandleMutation(TeamsInboundActivity activity, IActorRef replyTo)
    {
        var fingerprint = ActivityFingerprint.Create(activity.Trust.ActivityId);
        if (!_activitySessions.TryGetValue(fingerprint, out _))
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("unknown_activity_mapping");
            replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Ignored));
            return;
        }

        // PR 4 deliberately records only the durable lookup. It does not change
        // model history or enqueue a turn for an update or delete.
        ChannelTelemetry.For(ChannelType.Teams).RecordExtra("channel_activity_mapping_resolved");
        replyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Accepted));
    }

    private async Task RouteBindingAsync(RouteChannelBinding route)
    {
        var binding = Context.Child(TeamsActorNames.Binding(route.SessionId));
        if (binding.IsNobody())
        {
            binding = Context.ActorOf(
                TeamsSessionBindingActor.CreateProps(route.SessionId, _dependencies),
                TeamsActorNames.Binding(route.SessionId));
            _log.Info("channel_binding_created");
        }

        try
        {
            var result = await binding.Ask<TeamsBindingRouteResult>(
                new TeamsBindingIngress(route.Activity, route.CancellationToken),
                BindingRouteTimeout,
                route.CancellationToken);
            route.ReplyTo.Tell(result);
        }
        catch (OperationCanceledException) when (route.CancellationToken.IsCancellationRequested)
        {
            route.ReplyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Cancelled));
        }
        catch (AskTimeoutException)
        {
            route.ReplyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Unavailable));
        }
        catch (Exception)
        {
            route.ReplyTo.Tell(new TeamsBindingRouteResult(TeamsBindingRouteDisposition.Failed));
        }
    }

    private async Task HandleApprovalActionAsync(TeamsConversationApprovalAction action)
    {
        var replyTo = Sender;
        if (action.CancellationToken.IsCancellationRequested
            || !IsExpectedApprovalConversation(action.Action)
            || action.Action.RootActivityId is not { } rootActivityId
            || !TeamsSessionIdentifierCodec.TryCreateChannel(
                action.Action.Trust.TenantId,
                action.Action.Trust.ConversationId,
                rootActivityId,
                out var sessionId,
                out _))
        {
            replyTo.Tell(new TeamsApprovalActionResult(action.CancellationToken.IsCancellationRequested
                ? TeamsApprovalActionDisposition.Cancelled
                : TeamsApprovalActionDisposition.Rejected));
            return;
        }

        var binding = Context.Child(TeamsActorNames.Binding(sessionId));
        if (binding.IsNobody())
        {
            binding = Context.ActorOf(
                TeamsSessionBindingActor.CreateProps(sessionId, _dependencies),
                TeamsActorNames.Binding(sessionId));
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
        if (!TeamsSessionIdentifierCodec.TryParse(reminder.Reminder.SessionId, out var identifier, out _)
            || identifier.Scope != TeamsConversationScope.Channel
            || !TeamsSessionIdentifierCodec.TryCreatePersonal(
                identifier.TenantId,
                identifier.ConversationId,
                out var expectedConversation,
                out _)
            || expectedConversation != _conversationId)
        {
            Sender.Tell(CommandNack.For(reminder.Reminder.SessionId, "Teams session does not match the channel conversation."));
            return;
        }

        var binding = Context.Child(TeamsActorNames.Binding(reminder.Reminder.SessionId));
        if (binding.IsNobody())
        {
            binding = Context.ActorOf(
                TeamsSessionBindingActor.CreateProps(reminder.Reminder.SessionId, _dependencies),
                TeamsActorNames.Binding(reminder.Reminder.SessionId));
        }

        binding.Forward(new TeamsBindingReminder(reminder.Reminder));
    }

    private bool IsExpectedConversation(TeamsInboundActivity activity)
    {
        if (activity.Trust.Scope != TeamsConversationScope.Channel)
            return false;

        return TeamsSessionIdentifierCodec.TryCreatePersonal(
                   activity.Trust.TenantId,
                   activity.Trust.ConversationId,
                   out var expected,
                   out _)
               && expected == _conversationId;
    }

    private bool IsExpectedApprovalConversation(TeamsApprovalAction action)
    {
        if (action.Trust.Scope != TeamsConversationScope.Channel)
            return false;

        return TeamsSessionIdentifierCodec.TryCreatePersonal(
                   action.Trust.TenantId,
                   action.Trust.ConversationId,
                   out var expected,
                   out _)
               && expected == _conversationId;
    }

    private void ApplyMapped(TeamsChannelActivityMapped mapped)
    {
        EnsureFingerprint(mapped.ActivityFingerprint);
        if (!TeamsSessionIdentifierCodec.TryParse(new SessionId(mapped.SessionId), out var parsed, out _)
            || parsed.Scope != TeamsConversationScope.Channel)
        {
            throw new InvalidOperationException("The Teams channel activity index contains an invalid session.");
        }

        if (mapped.EvictedActivityFingerprint is { } evicted)
        {
            EnsureFingerprint(evicted);
            if (_activityOrder.Count == 0
                || !string.Equals(_activityOrder.Peek(), evicted, StringComparison.Ordinal)
                || !_activitySessions.Remove(evicted))
            {
                throw new InvalidOperationException("The Teams channel activity index has invalid retention ordering.");
            }

            _activityOrder.Dequeue();
        }
        else if (_activityOrder.Count >= ActivityIndexCapacity)
            throw new InvalidOperationException("The Teams channel activity index exceeds its retention limit.");

        if (!_activitySessions.TryAdd(mapped.ActivityFingerprint, new SessionId(mapped.SessionId)))
            throw new InvalidOperationException("The Teams channel activity index contains a duplicate entry.");
        _activityOrder.Enqueue(mapped.ActivityFingerprint);
    }

    private void ApplySnapshot(TeamsChannelActivityIndexSnapshot snapshot)
    {
        if (snapshot.Entries.Count > ActivityIndexCapacity)
            throw new InvalidOperationException("The Teams channel activity index snapshot exceeds its retention limit.");

        _activitySessions.Clear();
        _activityOrder.Clear();
        foreach (var entry in snapshot.Entries)
            ApplyMapped(entry with { EvictedActivityFingerprint = null });
    }

    private void ApplyLegacyPersistence(LegacyChannelPersistenceEnvelope legacy)
    {
        _requiresMigrationSnapshot = true;
        switch (legacy.Manifest)
        {
            case "dtcam-v1":
            {
                var value = LegacyProto.DurableTeamsChannelActivityMappedProto.Parser.ParseFrom(legacy.Payload);
                ApplyMapped(new TeamsChannelActivityMapped(
                    value.ActivityFingerprint,
                    value.SessionId,
                    value.HasEvictedActivityFingerprint ? value.EvictedActivityFingerprint : null));
                break;
            }
            case "dtcais-v1":
            {
                var value = LegacyProto.DurableTeamsChannelActivityIndexSnapshotProto.Parser.ParseFrom(legacy.Payload);
                ApplySnapshot(new TeamsChannelActivityIndexSnapshot(value.Entries.Select(entry => new TeamsChannelActivityMapped(
                    entry.ActivityFingerprint,
                    entry.SessionId,
                    entry.HasEvictedActivityFingerprint ? entry.EvictedActivityFingerprint : null)).ToArray()));
                break;
            }
            default:
                throw new InvalidOperationException("The legacy Team persistence manifest is not valid for a channel conversation actor.");
        }
    }

    private void SaveMigrationSnapshot()
    {
        if (IsRecovering || !_requiresMigrationSnapshot)
            return;

        SaveSnapshot(new TeamsChannelActivityIndexSnapshot(_activityOrder.Select(fingerprint => new TeamsChannelActivityMapped(
            fingerprint,
            _activitySessions[fingerprint].Value,
            null)).ToArray()));
        _requiresMigrationSnapshot = false;
    }

    private void SaveSnapshotWhenDue()
    {
        if (!IsRecovering && LastSequenceNr > 0 && LastSequenceNr % SnapshotInterval == 0)
        {
            var entries = _activityOrder.Select(fingerprint => new TeamsChannelActivityMapped(
                fingerprint,
                _activitySessions[fingerprint].Value,
                null)).ToArray();
            SaveSnapshot(new TeamsChannelActivityIndexSnapshot(entries));
        }
    }

    private static void EnsureFingerprint(string fingerprint)
    {
        if (fingerprint.Length != 64 || fingerprint.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidOperationException("The Teams channel activity index contains an invalid fingerprint.");
    }

    private sealed record RouteChannelBinding(
        TeamsInboundActivity Activity,
        SessionId SessionId,
        IActorRef ReplyTo,
        CancellationToken CancellationToken) : INoSerializationVerificationNeeded;

    private sealed record SaveTeamsChannelMigrationSnapshot : INoSerializationVerificationNeeded;
}
