// -----------------------------------------------------------------------
// <copyright file="TeamsIngressActorHost.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Channels.Teams;
using Netclaw.Channels.Telemetry;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Deliberately terminates at the PR 2 actor boundary. This makes accepted
/// ingress observable without creating a hidden path to a session or LLM.
/// </summary>
internal sealed class DeferredTeamsConversationIngressSink : ITeamsConversationIngressSink
{
    public ValueTask RouteAsync(TeamsInboundActivity activity, CancellationToken cancellationToken)
    {
        ChannelTelemetry.For(ChannelType.Teams).RecordEventDropped("conversation_routing_not_implemented");
        return ValueTask.CompletedTask;
    }
}

internal sealed class TeamsIngressActorHost(ActorSystem actorSystem) : IHostedService
{
    private IActorRef? _actor;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _actor = actorSystem.ActorOf(
            DependencyResolver.For(actorSystem).Props<TeamsIngressActor>(),
            "teams-ingress");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _actor?.Tell(PoisonPill.Instance);
        _actor = null;
        return Task.CompletedTask;
    }

    public void Submit(TeamsInboundActivity activity, CancellationToken cancellationToken)
    {
        var actor = _actor ?? throw new InvalidOperationException("Teams ingress actor is not started.");
        actor.Tell(new TeamsIngressReceived(activity, cancellationToken));
    }
}
