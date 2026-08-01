// -----------------------------------------------------------------------
// <copyright file="TeamsIngressActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Channels.Telemetry;

namespace Netclaw.Channels.Teams;

/// <summary>
/// The PR 2 boundary for canonical Teams conversation-child routing. PR 3
/// replaces the deferred implementation with the durable conversation actor;
/// this interface deliberately has no session-pipeline dependency.
/// </summary>
public interface ITeamsConversationIngressSink
{
    ValueTask RouteAsync(TeamsInboundActivity activity, CancellationToken cancellationToken);
}

public sealed record TeamsIngressReceived(TeamsInboundActivity Activity, CancellationToken CancellationToken);

public enum TeamsIngressRouteDisposition
{
    Routed,
    Duplicate,
    Cancelled
}

public sealed record TeamsIngressRouteResult(TeamsIngressRouteDisposition Disposition);

/// <summary>
/// Process-local routing and duplicate fast path only. Durable deduplication
/// belongs to the future session-binding actor, and activity/root lookup
/// belongs to the future conversation actor.
/// </summary>
public sealed class TeamsIngressActor : ReceiveActor
{
    internal const int DuplicateCapacity = 1_024;
    internal static readonly TimeSpan DuplicateRetention = TimeSpan.FromMinutes(5);

    private readonly ITeamsConversationIngressSink _conversationSink;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<DuplicateKey, DateTimeOffset> _recent = new();
    private readonly Queue<DuplicateKey> _order = new();

    public TeamsIngressActor(ITeamsConversationIngressSink conversationSink, TimeProvider timeProvider)
    {
        _conversationSink = conversationSink ?? throw new ArgumentNullException(nameof(conversationSink));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        ReceiveAsync<TeamsIngressReceived>(HandleAsync);
    }

    private async Task HandleAsync(TeamsIngressReceived received)
    {
        if (received.CancellationToken.IsCancellationRequested)
        {
            Sender.Tell(new TeamsIngressRouteResult(TeamsIngressRouteDisposition.Cancelled));
            return;
        }

        var now = _timeProvider.GetUtcNow();
        EvictExpired(now);
        var key = new DuplicateKey(
            received.Activity.Trust.TenantId,
            received.Activity.Trust.ConversationId,
            received.Activity.Trust.ActivityId);

        if (_recent.ContainsKey(key))
        {
            ChannelTelemetry.For(ChannelType.Teams).RecordEventFiltered("fast_path_duplicate");
            Sender.Tell(new TeamsIngressRouteResult(TeamsIngressRouteDisposition.Duplicate));
            return;
        }

        _recent.Add(key, now);
        _order.Enqueue(key);
        EvictExcess();

        await _conversationSink.RouteAsync(received.Activity, received.CancellationToken);
        ChannelTelemetry.For(ChannelType.Teams).RecordEventRouted("conversation_boundary");
        Sender.Tell(new TeamsIngressRouteResult(TeamsIngressRouteDisposition.Routed));
    }

    private void EvictExpired(DateTimeOffset now)
    {
        while (_order.TryPeek(out var key)
               && _recent.TryGetValue(key, out var seenAt)
               && now - seenAt >= DuplicateRetention)
        {
            _order.Dequeue();
            _recent.Remove(key);
        }
    }

    private void EvictExcess()
    {
        while (_recent.Count > DuplicateCapacity && _order.TryDequeue(out var key))
            _recent.Remove(key);
    }

    private sealed record DuplicateKey(string TenantId, string ConversationId, string ActivityId);
}
