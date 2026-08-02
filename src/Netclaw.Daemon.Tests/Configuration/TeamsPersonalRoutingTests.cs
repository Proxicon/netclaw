// -----------------------------------------------------------------------
// <copyright file="TeamsPersonalRoutingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Channels.Teams;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Tests.Configuration;

[Collection("TeamsTelemetry")]
public sealed class TeamsPersonalRoutingTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.WithInMemoryJournal().WithInMemorySnapshotStore().WithNetclawSerialization();
    }

    [Fact]
    public async Task Allowed_personal_activity_dispatches_once_with_final_personal_trust_context()
    {
        var pipeline = CreatePipeline(TestActor);
        var dependencies = CreateDependencies(pipeline);
        var sessionId = CreateSessionId("tenant-a", "conversation-a");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies));

        var result = await RouteAsync(actor, CreateActivity("activity-a", "tenant-a", "conversation-a"));

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, result.Disposition);
        var dispatched = ReceiveDispatchedMessage();
        Assert.Equal(sessionId, dispatched.SessionId);
        Assert.NotNull(dispatched.Source);
        Assert.Equal(TrustAudience.Personal, dispatched.Source!.Audience);
        Assert.Equal(TrustBoundary.Personal, dispatched.Source.Boundary);
        Assert.Equal(PrincipalClassification.TrustedInternal, dispatched.Source.Principal);
        Assert.Equal("activity-a", dispatched.Source.MessageId);

        var duplicate = await RouteAsync(actor, CreateActivity("activity-a", "tenant-a", "conversation-a"));

        Assert.Equal(TeamsBindingRouteDisposition.Duplicate, duplicate.Disposition);
    }

    [Fact]
    public async Task Disabled_direct_messages_and_an_empty_user_allow_list_deny_without_a_pipeline_turn()
    {
        var disabledPipeline = CreatePipeline(TestActor);
        var disabled = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            CreateSessionId("tenant-a", "conversation-disabled"),
            CreateDependencies(disabledPipeline, allowDirectMessages: false)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Denied,
            (await RouteAsync(disabled, CreateActivity("activity-disabled", "tenant-a", "conversation-disabled"))).Disposition);

        var empty = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            CreateSessionId("tenant-a", "conversation-empty"),
            CreateDependencies(disabledPipeline, allowedUserIds: [])));

        Assert.Equal(
            TeamsBindingRouteDisposition.Denied,
            (await RouteAsync(empty, CreateActivity("activity-empty", "tenant-a", "conversation-empty"))).Disposition);

        var wrongUser = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            CreateSessionId("tenant-a", "conversation-wrong-user"),
            CreateDependencies(disabledPipeline, allowedUserIds: ["User-A"])));

        Assert.Equal(
            TeamsBindingRouteDisposition.Denied,
            (await RouteAsync(wrongUser, CreateActivity("activity-wrong-user", "tenant-a", "conversation-wrong-user"))).Disposition);

        var wrongTenant = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            CreateSessionId("tenant-b", "conversation-wrong-tenant"),
            CreateDependencies(disabledPipeline)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Denied,
            (await RouteAsync(wrongTenant, CreateActivity("activity-wrong-tenant", "tenant-b", "conversation-wrong-tenant"))).Disposition);

        var oversizedActivity = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            CreateSessionId("tenant-a", "conversation-oversized-activity"),
            CreateDependencies(disabledPipeline)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Denied,
            (await RouteAsync(
                oversizedActivity,
                CreateActivity(
                    new string('a', TeamsSessionIdentifierCodec.MaxRawIdentifierBytes + 1),
                    "tenant-a",
                    "conversation-oversized-activity"))).Disposition);
    }

    [Fact]
    public async Task A_durable_activity_reservation_suppresses_a_duplicate_after_binding_restart()
    {
        var pipeline = CreatePipeline(TestActor);
        var dependencies = CreateDependencies(pipeline);
        var sessionId = CreateSessionId("tenant-a", "conversation-restart");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-restart-first");
        var activity = CreateActivity("activity-restart-活動", "tenant-a", "conversation-restart");

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteAsync(actor, activity)).Disposition);
        ReceiveDispatchedMessage();
        Watch(actor);
        actor.Tell(PoisonPill.Instance);
        ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-restart-second");

        Assert.Equal(TeamsBindingRouteDisposition.Duplicate, (await RouteAsync(recovered, activity)).Disposition);
    }

    [Fact]
    public async Task A_passivated_binding_rehydrates_its_durable_activity_reservation()
    {
        var pipeline = CreatePipeline(TestActor);
        var dependencies = CreateDependencies(pipeline);
        var sessionId = CreateSessionId("tenant-a", "conversation-passivation");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-passivation-first");
        var activity = CreateActivity("activity-passivation", "tenant-a", "conversation-passivation");

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteAsync(actor, activity)).Disposition);
        ReceiveDispatchedMessage();
        Watch(actor);
        actor.Tell(ReceiveTimeout.Instance);
        ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-passivation-second");

        Assert.Equal(TeamsBindingRouteDisposition.Duplicate, (await RouteAsync(recovered, activity)).Disposition);
    }

    [Fact]
    public async Task Processed_activity_retention_evicts_the_oldest_identifier_deterministically()
    {
        var sink = Sys.ActorOf(DiscardActor.Create(), "teams-discard-session-manager");
        var pipeline = CreatePipeline(sink);
        var dependencies = CreateDependencies(pipeline);
        var sessionId = CreateSessionId("tenant-a", "conversation-retention");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies));

        for (var index = 0; index <= TeamsSessionBindingActor.ProcessedActivityCapacity; index++)
        {
            var result = await RouteAsync(actor, CreateActivity($"activity-{index}", "tenant-a", "conversation-retention"));
            Assert.Equal(TeamsBindingRouteDisposition.Accepted, result.Disposition);
        }

        var retained = await RouteAsync(actor, CreateActivity("activity-1", "tenant-a", "conversation-retention"));
        var evicted = await RouteAsync(actor, CreateActivity("activity-0", "tenant-a", "conversation-retention"));

        Assert.Equal(TeamsBindingRouteDisposition.Duplicate, retained.Disposition);
        Assert.Equal(TeamsBindingRouteDisposition.Accepted, evicted.Disposition);

        Watch(actor);
        actor.Tell(PoisonPill.Instance);
        ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies));

        Assert.Equal(
            TeamsBindingRouteDisposition.Duplicate,
            (await RouteAsync(recovered, CreateActivity("activity-2", "tenant-a", "conversation-retention"))).Disposition);
        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(recovered, CreateActivity("activity-1", "tenant-a", "conversation-retention"))).Disposition);
    }

    [Fact]
    public async Task The_same_activity_identifier_in_another_tenant_uses_an_independent_binding()
    {
        var pipeline = CreatePipeline(TestActor);
        var tenantA = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            CreateSessionId("tenant-a", "conversation-a"),
            CreateDependencies(pipeline, tenantId: "tenant-a")));
        var tenantB = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            CreateSessionId("tenant-b", "conversation-a"),
            CreateDependencies(pipeline, tenantId: "tenant-b")));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(tenantA, CreateActivity("activity-shared", "tenant-a", "conversation-a"))).Disposition);
        ReceiveDispatchedMessage();
        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(tenantB, CreateActivity("activity-shared", "tenant-b", "conversation-a"))).Disposition);
        ReceiveDispatchedMessage();
    }

    [Fact]
    public async Task A_real_conversation_sink_routes_personal_input_once_and_keeps_channel_input_deferred()
    {
        var pipeline = CreatePipeline(TestActor);
        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            AllowDirectMessages = true,
            AllowedUserIds = ["user-a"]
        };
        var services = new ServiceCollection();
        services.AddSingleton<ISessionPipeline>(pipeline);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        using var provider = services.BuildServiceProvider();
        var sink = new TeamsActorConversationIngressSink(Sys, options, provider);
        var personal = CreateActivity("activity-sink", "tenant-a", "conversation-sink");
        var ingress = Sys.ActorOf(Props.Create(() => new TeamsIngressActor(sink, TimeProvider.System)));

        Assert.Equal(
            TeamsIngressRouteDisposition.Routed,
            (await ingress.Ask<TeamsIngressRouteResult>(
                new TeamsIngressReceived(personal, TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken)).Disposition);
        ReceiveDispatchedMessage();
        Assert.Equal(
            TeamsIngressRouteDisposition.Duplicate,
            (await ingress.Ask<TeamsIngressRouteResult>(
                new TeamsIngressReceived(personal, TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken)).Disposition);

        var channel = new TeamsInboundActivity(
            new TeamsIngressTrustContext(
                TrustAudience.Public,
                PrincipalClassification.UntrustedExternal,
                TrustBoundary.Public,
                new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
                "user-a",
                "tenant-a",
                "conversation-channel",
                TeamsConversationScope.Channel,
                "activity-channel",
                TimeProvider.System.GetUtcNow()),
            "hello");

        Assert.Equal(TeamsIngressSinkResult.Denied, await sink.RouteAsync(channel, TestContext.Current.CancellationToken));
        var blockedSession = CreateSessionId("tenant-a", "conversation-channel");
        await Assert.ThrowsAsync<ActorNotFoundException>(() =>
            Sys.ActorSelection($"/user/{TeamsActorNames.Conversation(blockedSession)}")
                .ResolveOne(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_real_conversation_sink_routes_channel_roots_and_replies_and_indexes_mutations()
    {
        var pipeline = CreatePipeline(TestActor);
        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            MentionOnly = true,
            AllowedTeamIds = ["team-a"],
            AllowedChannelIds = ["channel-a"]
        };
        var services = new ServiceCollection();
        services.AddSingleton<ISessionPipeline>(pipeline);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        using var provider = services.BuildServiceProvider();
        var sink = new TeamsActorConversationIngressSink(Sys, options, provider);
        var root = CreateChannelActivity("root-a", "conversation-a;messageid=root-a");

        Assert.Equal(TeamsIngressSinkResult.Accepted, await sink.RouteAsync(root, TestContext.Current.CancellationToken));
        var first = ReceiveDispatchedMessage();
        Assert.True(TeamsSessionIdentifierCodec.TryCreateChannel("tenant-a", root.Trust.ConversationId, "root-a", out var expected, out _));
        Assert.Equal(expected, first.SessionId);

        var reply = CreateChannelActivity("reply-a", "conversation-a;messageid=root-a");
        Assert.Equal(TeamsIngressSinkResult.Accepted, await sink.RouteAsync(reply, TestContext.Current.CancellationToken));

        var update = CreateChannelActivity("root-a", "conversation-a;messageid=root-a", TeamsIngressActivityKind.MessageUpdate);
        var unknownDelete = CreateChannelActivity("unknown", "conversation-a;messageid=root-a", TeamsIngressActivityKind.MessageDelete);
        Assert.Equal(TeamsIngressSinkResult.Accepted, await sink.RouteAsync(update, TestContext.Current.CancellationToken));
        Assert.Equal(TeamsIngressSinkResult.Ignored, await sink.RouteAsync(unknownDelete, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_personal_ingress_resolves_one_conversation_and_binding()
    {
        var pipeline = CreatePipeline(TestActor);
        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            AllowDirectMessages = true,
            AllowedUserIds = ["user-a"]
        };
        var services = new ServiceCollection();
        services.AddSingleton<ISessionPipeline>(pipeline);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        using var provider = services.BuildServiceProvider();
        var sink = new TeamsActorConversationIngressSink(Sys, options, provider);
        var activity = CreateActivity("activity-concurrent", "tenant-a", "conversation-concurrent");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => sink.RouteAsync(activity, TestContext.Current.CancellationToken).AsTask()));

        Assert.Equal(1, results.Count(result => result == TeamsIngressSinkResult.Accepted));
        Assert.Equal(7, results.Count(result => result == TeamsIngressSinkResult.Duplicate));
        ReceiveDispatchedMessage();

        var sessionId = CreateSessionId("tenant-a", "conversation-concurrent");
        var conversation = await Sys.ActorSelection($"/user/{TeamsActorNames.Conversation(sessionId)}")
            .ResolveOne(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var binding = await Sys.ActorSelection($"/user/{TeamsActorNames.Conversation(sessionId)}/{TeamsActorNames.Binding(sessionId)}")
            .ResolveOne(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.NotEqual(ActorRefs.Nobody, conversation);
        Assert.NotEqual(ActorRefs.Nobody, binding);
    }

    [Fact]
    public async Task A_pipeline_failure_releases_the_activity_reservation_for_a_retry()
    {
        var fallback = CreatePipeline(TestActor);
        var pipeline = new FailThenDelegatePipeline(fallback);
        var dependencies = CreateDependencies(pipeline);
        var sessionId = CreateSessionId("tenant-a", "conversation-failure");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies));
        var activity = CreateActivity("activity-failure", "tenant-a", "conversation-failure");

        Assert.Equal(TeamsBindingRouteDisposition.Failed, (await RouteAsync(actor, activity)).Disposition);
        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteAsync(actor, activity)).Disposition);
        ReceiveDispatchedMessage();
    }

    [Fact]
    public async Task A_pipeline_cancellation_releases_the_activity_reservation_for_a_retry()
    {
        var fallback = CreatePipeline(TestActor);
        using var cancellation = new CancellationTokenSource();
        var pipeline = new CancelThenDelegatePipeline(fallback, cancellation);
        var dependencies = CreateDependencies(pipeline);
        var sessionId = CreateSessionId("tenant-a", "conversation-cancelled");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies));
        var activity = CreateActivity("activity-cancelled", "tenant-a", "conversation-cancelled");

        var cancelled = await actor.Ask<TeamsBindingRouteResult>(
            new TeamsBindingIngress(activity, cancellation.Token),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsBindingRouteDisposition.Cancelled, cancelled.Disposition);
        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteAsync(actor, activity)).Disposition);
        ReceiveDispatchedMessage();
    }

    private ISessionPipeline CreatePipeline(IActorRef sessionManager)
    {
        var registry = ActorRegistry.For(Sys);
        registry.Register<SessionManagerActorKey>(sessionManager);
        return new SessionPipeline(
            Sys,
            new RequiredActor<SessionManagerActorKey>(registry),
            new NetclawPaths(Path.Combine(Path.GetTempPath(), $"teams-routing-{Guid.NewGuid():N}")));
    }

    private static TeamsConversationDependencies CreateDependencies(
        ISessionPipeline pipeline,
        bool allowDirectMessages = true,
        string[]? allowedUserIds = null,
        string tenantId = "tenant-a") => new(
        new TeamsChannelOptions
        {
            TenantId = tenantId,
            AllowDirectMessages = allowDirectMessages,
            AllowedUserIds = allowedUserIds ?? ["user-a"]
        },
        pipeline,
        TimeProvider.System);

    private static SessionId CreateSessionId(string tenantId, string conversationId)
    {
        Assert.True(TeamsSessionIdentifierCodec.TryCreatePersonal(tenantId, conversationId, out var sessionId, out _));
        return sessionId;
    }

    private static TeamsInboundActivity CreateActivity(string activityId, string tenantId, string conversationId) => new(
        new TeamsIngressTrustContext(
            TrustAudience.Public,
            PrincipalClassification.UntrustedExternal,
            TrustBoundary.Public,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            "user-a",
            tenantId,
            conversationId,
            TeamsConversationScope.Personal,
            activityId,
            TimeProvider.System.GetUtcNow()),
        "hello");

    private static TeamsInboundActivity CreateChannelActivity(
        string activityId,
        string conversationId,
        TeamsIngressActivityKind kind = TeamsIngressActivityKind.Message) => new(
        new TeamsIngressTrustContext(
            TrustAudience.Public,
            PrincipalClassification.UntrustedExternal,
            TrustBoundary.Public,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            "user-a",
            "tenant-a",
            conversationId,
            TeamsConversationScope.Channel,
            activityId,
            TimeProvider.System.GetUtcNow()),
        kind == TeamsIngressActivityKind.Message ? "hello" : string.Empty,
        new TeamsReplyMetadata(null, "root-a"),
        isMentioned: true,
        kind: kind,
        teamId: "team-a",
        channelId: "channel-a");

    private static Task<TeamsBindingRouteResult> RouteAsync(
        IActorRef actor,
        TeamsInboundActivity activity) => actor.Ask<TeamsBindingRouteResult>(
        new TeamsBindingIngress(activity, TestContext.Current.CancellationToken),
        TestContext.Current.CancellationToken);

    private SendUserMessage ReceiveDispatchedMessage()
    {
        ExpectMsg<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        ExpectMsg<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        return ExpectMsg<SendUserMessage>(cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class DiscardActor : ReceiveActor
    {
        public static Props Create() => Props.Create(() => new DiscardActor());
    }

    private sealed class FailThenDelegatePipeline(ISessionPipeline fallback) : ISessionPipeline
    {
        private bool _shouldFail = true;

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            Akka.Streams.IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
        {
            if (_shouldFail)
            {
                _shouldFail = false;
                return Task.FromException<MaterializedSession>(new InvalidOperationException("synthetic pipeline failure"));
            }

            return fallback.CreateAsync(sessionId, options, materializer, cancellationToken);
        }

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            fallback.SendFeedbackAsync(feedback, ct);

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            fallback.SendFeedbackAndWaitAsync(feedback, ct);
    }

    private sealed class CancelThenDelegatePipeline(
        ISessionPipeline fallback,
        CancellationTokenSource cancellation) : ISessionPipeline
    {
        private bool _shouldCancel = true;

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            Akka.Streams.IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
        {
            if (_shouldCancel)
            {
                _shouldCancel = false;
                cancellation.Cancel();
                return Task.FromCanceled<MaterializedSession>(cancellation.Token);
            }

            return fallback.CreateAsync(sessionId, options, materializer, cancellationToken);
        }

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            fallback.SendFeedbackAsync(feedback, ct);

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            fallback.SendFeedbackAndWaitAsync(feedback, ct);
    }
}
