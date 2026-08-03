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
using Netclaw.Channels.Telemetry;
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
        services.AddSingleton<ITeamsReplyClient, TestTeamsReplyClient>();
        services.AddSingleton<TeamsOutputRenderer>();
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
        services.AddSingleton<ITeamsReplyClient, TestTeamsReplyClient>();
        services.AddSingleton<TeamsOutputRenderer>();
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
    public async Task Channel_activity_mapping_rehydrates_after_conversation_actor_restart()
    {
        var pipeline = CreatePipeline(TestActor);
        var dependencies = new TeamsConversationDependencies(
            new TeamsChannelOptions
            {
                TenantId = "tenant-a",
                MentionOnly = true,
                AllowedTeamIds = ["team-a"],
                AllowedChannelIds = ["channel-a"]
            },
            pipeline,
            new TestTeamsReplyClient(),
            new TeamsOutputRenderer(),
            TimeProvider.System);
        const string conversationId = "conversation-recovery;messageid=root-a";
        var parentId = CreateSessionId("tenant-a", conversationId);
        var root = CreateChannelActivity("root-a", conversationId);
        var first = Sys.ActorOf(TeamsConversationActor.CreateProps(parentId, dependencies), "teams-channel-recovery-first");

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteConversationAsync(first, root)).Disposition);
        ReceiveDispatchedMessage();

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = Sys.ActorOf(TeamsConversationActor.CreateProps(parentId, dependencies), "teams-channel-recovery-second");
        var update = CreateChannelActivity("root-a", conversationId, TeamsIngressActivityKind.MessageUpdate);

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteConversationAsync(recovered, update)).Disposition);
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
        services.AddSingleton<ITeamsReplyClient, TestTeamsReplyClient>();
        services.AddSingleton<TeamsOutputRenderer>();
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

    [Fact]
    public async Task Personal_output_posts_one_reply_to_the_verified_personal_destination()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-output");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("activity-output", "tenant-a", "conversation-output"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        var telemetryBefore = ChannelTelemetry.For(ChannelType.Teams).GetSnapshot();
        subscriber.Tell(new TextOutput("final reply") { SessionId = sessionId });

        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var message = Assert.Single(replyClient.Messages);
        Assert.Equal(TeamsConversationScope.Personal, message.Destination.Scope);
        Assert.Equal("tenant-a", message.Destination.TenantId);
        Assert.Equal("conversation-output", message.Destination.ConversationId);
        Assert.Equal("user-a", message.Destination.UserId);
        Assert.Equal("final reply", message.Text);
        Assert.Equal(telemetryBefore.RepliesPosted + 1, ChannelTelemetry.For(ChannelType.Teams).GetSnapshot().RepliesPosted);
    }

    [Fact]
    public async Task Channel_output_for_a_root_and_reply_uses_the_same_canonical_thread_destination()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            MentionOnly = true,
            AllowedTeamIds = ["team-a"],
            AllowedChannelIds = ["channel-a"]
        };
        var dependencies = new TeamsConversationDependencies(
            options,
            pipeline,
            replyClient,
            new TeamsOutputRenderer(),
            TimeProvider.System);
        const string conversationId = "conversation-output;messageid=root-a";
        var parent = Sys.ActorOf(TeamsConversationActor.CreateProps(CreateSessionId("tenant-a", conversationId), dependencies));

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteConversationAsync(parent, CreateChannelActivity("root-a", conversationId))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        subscriber.Tell(new TextOutput("root result") { SessionId = CreateChannelSessionId(conversationId) });
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteConversationAsync(parent, CreateChannelActivity("reply-a", conversationId))).Disposition);
        var replySubscriber = ReceiveNextOutputSubscriber();
        replySubscriber.Tell(new TextOutput("reply result") { SessionId = CreateChannelSessionId(conversationId) });
        await AwaitAssertAsync(() => Assert.Equal(2, replyClient.Messages.Count), cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(replyClient.Messages, message =>
        {
            Assert.Equal(TeamsConversationScope.Channel, message.Destination.Scope);
            Assert.Equal("root-a", message.Destination.RootActivityId);
            Assert.Equal("root-a", message.ReplyToActivityId);
            Assert.Equal("team-a", message.Destination.TeamId);
            Assert.Equal("channel-a", message.Destination.ChannelId);
        });

        const string secondConversationId = "conversation-output;messageid=root-b";
        var secondParent = Sys.ActorOf(TeamsConversationActor.CreateProps(CreateSessionId("tenant-a", secondConversationId), dependencies));
        Assert.Equal(TeamsBindingRouteDisposition.Accepted, (await RouteConversationAsync(secondParent, CreateChannelActivity("root-b", secondConversationId, rootActivityId: "root-b"))).Disposition);
        var secondSubscriber = ReceiveOutputSubscriber();
        secondSubscriber.Tell(new TextOutput("second root result") { SessionId = CreateChannelSessionId(secondConversationId, "root-b") });
        await AwaitAssertAsync(() => Assert.Equal(3, replyClient.Messages.Count), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("root-b", replyClient.Messages[2].Destination.RootActivityId);
        Assert.Equal("root-b", replyClient.Messages[2].ReplyToActivityId);
    }

    [Fact]
    public async Task Empty_output_does_not_call_the_reply_client_and_update_failure_gets_one_final_fallback()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient(
            new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered, "processing"),
            new TeamsDeliveryResult(TeamsDeliveryStatus.Failed),
            new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered, "final"));
        var sessionId = CreateSessionId("tenant-a", "conversation-processing");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("activity-processing", "tenant-a", "conversation-processing"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        var telemetryBefore = ChannelTelemetry.For(ChannelType.Teams).GetSnapshot();
        subscriber.Tell(new TextOutput("  \r\n") { SessionId = sessionId });
        await AwaitAssertAsync(() => Assert.Empty(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);

        subscriber.Tell(new ProcessingStateOutput(true) { SessionId = sessionId });
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        subscriber.Tell(new TextOutput("final reply") { SessionId = sessionId });
        await AwaitAssertAsync(() => Assert.Equal(3, replyClient.Messages.Count), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(replyClient.Messages[0].UpdateActivityId);
        Assert.Equal("processing", replyClient.Messages[1].UpdateActivityId);
        Assert.Null(replyClient.Messages[2].UpdateActivityId);
        Assert.Equal("final reply", replyClient.Messages[2].Text);
        var telemetryAfter = ChannelTelemetry.For(ChannelType.Teams).GetSnapshot();
        Assert.Equal(telemetryBefore.RepliesPosted + 2, telemetryAfter.RepliesPosted);
        Assert.Equal(telemetryBefore.RepliesFailed + 1, telemetryAfter.RepliesFailed);
    }

    [Fact]
    public async Task Processing_delivery_never_retains_an_unbounded_activity_id()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient(
            new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered, new string('p', TeamsSessionIdentifierCodec.MaxRawIdentifierBytes + 1)),
            new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered, "final"));
        var sessionId = CreateSessionId("tenant-a", "conversation-processing-bound");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("activity-processing-bound", "tenant-a", "conversation-processing-bound"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        subscriber.Tell(new ProcessingStateOutput(true) { SessionId = sessionId });
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        subscriber.Tell(new TextOutput("final reply") { SessionId = sessionId });
        await AwaitAssertAsync(() => Assert.Equal(2, replyClient.Messages.Count), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(replyClient.Messages[1].UpdateActivityId);
    }

    [Fact]
    public async Task Recovered_binding_reports_an_unavailable_destination_without_a_delivery_attempt()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-output-recovered");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient)));
        var telemetryBefore = ChannelTelemetry.For(ChannelType.Teams).GetSnapshot();

        actor.Tell(new TeamsSessionBindingActor.BindingOutput(
            new TextOutput("late output") { SessionId = sessionId }));

        await AwaitAssertAsync(
            () => Assert.Equal(telemetryBefore.RepliesFailed + 1, ChannelTelemetry.For(ChannelType.Teams).GetSnapshot().RepliesFailed),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(replyClient.Messages);
    }

    [Fact]
    public async Task Final_delivery_failure_makes_no_retry_attempt()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient(new TeamsDeliveryResult(TeamsDeliveryStatus.Unavailable));
        var sessionId = CreateSessionId("tenant-a", "conversation-output-failed");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("activity-output-failed", "tenant-a", "conversation-output-failed"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        subscriber.Tell(new TextOutput("final reply") { SessionId = sessionId });

        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Binding_restart_does_not_leave_an_output_subscription_that_can_deliver_twice()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-output-restart");
        var dependencies = CreateDependencies(pipeline, replyClient: replyClient);
        var first = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-output-first");

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity("activity-output-first", "tenant-a", "conversation-output-restart"))).Disposition);
        var oldSubscriber = ReceiveOutputSubscriber();
        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);

        oldSubscriber.Tell(new TextOutput("stale output") { SessionId = sessionId });
        var recovered = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-output-second");
        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(recovered, CreateActivity("activity-output-second", "tenant-a", "conversation-output-restart"))).Disposition);
        var currentSubscriber = ReceiveOutputSubscriber();
        currentSubscriber.Tell(new TextOutput("current output") { SessionId = sessionId });

        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("current output", Assert.Single(replyClient.Messages).Text);
    }

    [Fact]
    public async Task A_pending_approval_survives_a_binding_restart_and_continues_once()
    {
        var replyClient = new RecordingTeamsReplyClient();
        var pipeline = new ApprovalRecordingPipeline(CreatePipeline(TestActor));
        var sessionId = CreateSessionId("tenant-a", "conversation-approval-restart");
        var dependencies = CreateDependencies(pipeline, replyClient: replyClient);
        var first = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-approval-first");

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity("activity-approval", "tenant-a", "conversation-approval-restart"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        subscriber.Tell(new ToolInteractionRequest
        {
            SessionId = sessionId,
            Kind = "approval",
            CallId = new ToolCallId("call-approval"),
            ToolName = new ToolName("safe_tool"),
            DisplayText = "Approve safe tool use.",
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, "Approve"),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, "Deny")
            ],
            RequesterSenderId = new SenderId("user-a"),
            RequesterPrincipal = PrincipalClassification.TrustedInternal
        });
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var card = replyClient.Messages[0].ApprovalCard;
        Assert.NotNull(card);
        var approve = Assert.Single(card!.Actions, action => action.Action == "approve");

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-approval-second");
        var action = CreateApprovalAction(
            "tenant-a",
            "conversation-approval-restart",
            approve.CorrelationId,
            approve.Nonce,
            "synthetic-activity");
        var accepted = await recovered.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(action, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Accepted, accepted.Disposition);
        await AwaitAssertAsync(() => Assert.Single(pipeline.Feedback), cancellationToken: TestContext.Current.CancellationToken);
        var feedback = Assert.IsType<ToolInteractionResponse>(pipeline.Feedback[0]);
        Assert.Equal(ApprovalOptionKeys.ApproveOnceKey, feedback.SelectedKey);

        var duplicate = await recovered.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(action, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        Assert.Equal(TeamsApprovalActionDisposition.AlreadyProcessed, duplicate.Disposition);
        Assert.Single(pipeline.Feedback);
    }

    [Fact]
    public async Task Pending_approval_rebinds_its_card_locator_and_forwards_a_denial_without_a_duplicate_terminal_card()
    {
        var replyClient = new RecordingTeamsReplyClient(
            new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered));
        var pipeline = new ApprovalRecordingPipeline(CreatePipeline(TestActor));
        var sessionId = CreateSessionId("tenant-a", "conversation-approval-rebind");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("activity-approval-rebind", "tenant-a", "conversation-approval-rebind"))).Disposition);
        var subscriber = ReceiveOutputSubscriber();
        subscriber.Tell(new ToolInteractionRequest
        {
            SessionId = sessionId,
            Kind = "approval",
            CallId = new ToolCallId("call-approval-rebind"),
            ToolName = new ToolName("safe_tool"),
            DisplayText = "Approve safe tool use.",
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, "Approve"),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, "Deny")
            ],
            RequesterSenderId = new SenderId("user-a"),
            RequesterPrincipal = PrincipalClassification.TrustedInternal
        });
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var card = Assert.Single(replyClient.Messages).ApprovalCard;
        Assert.NotNull(card);
        var deny = Assert.Single(card!.Actions, action => action.Action == "deny");
        var action = CreateApprovalAction(
            "tenant-a",
            "conversation-approval-rebind",
            deny.CorrelationId,
            deny.Nonce,
            "card-activity-rebind",
            "deny");

        var accepted = await actor.Ask<TeamsApprovalActionResult>(
            new TeamsBindingApprovalAction(action, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(TeamsApprovalActionDisposition.Accepted, accepted.Disposition);
        await AwaitAssertAsync(() => Assert.Single(pipeline.Feedback), cancellationToken: TestContext.Current.CancellationToken);
        var feedback = Assert.IsType<ToolInteractionResponse>(pipeline.Feedback[0]);
        Assert.Equal(ApprovalOptionKeys.DenyKey, feedback.SelectedKey);
        Assert.Single(replyClient.Messages);
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
        string tenantId = "tenant-a",
        ITeamsReplyClient? replyClient = null) => new(
        new TeamsChannelOptions
        {
            TenantId = tenantId,
            AllowDirectMessages = allowDirectMessages,
            AllowedUserIds = allowedUserIds ?? ["user-a"]
        },
        pipeline,
        replyClient ?? new TestTeamsReplyClient(),
        new TeamsOutputRenderer(),
        TimeProvider.System);

    private sealed class TestTeamsReplyClient : ITeamsReplyClient
    {
        public Task<TeamsDeliveryResult> DeliverAsync(TeamsOutboundMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered, "synthetic-activity"));
    }

    private sealed class RecordingTeamsReplyClient(params TeamsDeliveryResult[] results) : ITeamsReplyClient
    {
        private readonly Queue<TeamsDeliveryResult> _results = new(results);

        public List<TeamsOutboundMessage> Messages { get; } = [];

        public Task<TeamsDeliveryResult> DeliverAsync(TeamsOutboundMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(_results.Count == 0
                ? new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered, "synthetic-activity")
                : _results.Dequeue());
        }
    }

    private static SessionId CreateSessionId(string tenantId, string conversationId)
    {
        Assert.True(TeamsSessionIdentifierCodec.TryCreatePersonal(tenantId, conversationId, out var sessionId, out _));
        return sessionId;
    }

    private static SessionId CreateChannelSessionId(string conversationId, string rootActivityId = "root-a")
    {
        Assert.True(TeamsSessionIdentifierCodec.TryCreateChannel("tenant-a", conversationId, rootActivityId, out var sessionId, out _));
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
        "hello",
        new TeamsReplyMetadata(null, null, "https://service.invalid/"));

    private static TeamsInboundActivity CreateChannelActivity(
        string activityId,
        string conversationId,
        TeamsIngressActivityKind kind = TeamsIngressActivityKind.Message,
        string rootActivityId = "root-a") => new(
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
        new TeamsReplyMetadata(null, rootActivityId, "https://service.invalid/"),
        isMentioned: true,
        kind: kind,
        teamId: "team-a",
        channelId: "channel-a");

    private static TeamsApprovalAction CreateApprovalAction(
        string tenantId,
        string conversationId,
        string correlationId,
        string nonce,
        string promptActivityId,
        string action = "approve") => new(
        new TeamsIngressTrustContext(
            TrustAudience.Public,
            PrincipalClassification.UntrustedExternal,
            TrustBoundary.Public,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            "user-a",
            tenantId,
            conversationId,
            TeamsConversationScope.Personal,
            "invoke-approval",
            TimeProvider.System.GetUtcNow()),
        correlationId,
        nonce,
        action,
        null,
        null,
        null,
        promptActivityId,
        "https://service.invalid/");

    private static Task<TeamsBindingRouteResult> RouteAsync(
        IActorRef actor,
        TeamsInboundActivity activity) => actor.Ask<TeamsBindingRouteResult>(
        new TeamsBindingIngress(activity, TestContext.Current.CancellationToken),
        TestContext.Current.CancellationToken);

    private static Task<TeamsBindingRouteResult> RouteConversationAsync(
        IActorRef actor,
        TeamsInboundActivity activity) => actor.Ask<TeamsBindingRouteResult>(
        new TeamsConversationIngress(activity, TestContext.Current.CancellationToken),
        TestContext.Current.CancellationToken);

    private SendUserMessage ReceiveDispatchedMessage()
    {
        ExpectMsg<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        ExpectMsg<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        return ExpectMsg<SendUserMessage>(cancellationToken: TestContext.Current.CancellationToken);
    }

    private IActorRef ReceiveOutputSubscriber()
    {
        ExpectMsg<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        return ReceiveNextOutputSubscriber();
    }

    private IActorRef ReceiveNextOutputSubscriber()
    {
        var subscription = ExpectMsg<JoinSession>(cancellationToken: TestContext.Current.CancellationToken);
        ExpectMsg<SendUserMessage>(cancellationToken: TestContext.Current.CancellationToken);
        return subscription.Subscriber;
    }

    private sealed class DiscardActor : ReceiveActor
    {
        public static Props Create() => Props.Create(() => new DiscardActor());
    }

    private sealed class ApprovalRecordingPipeline(ISessionPipeline fallback) : ISessionPipeline
    {
        public List<IWithSessionId> Feedback { get; } = [];

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            Akka.Streams.IMaterializer? materializer = null,
            CancellationToken cancellationToken = default) =>
            fallback.CreateAsync(sessionId, options, materializer, cancellationToken);

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default)
        {
            Feedback.Add(feedback);
            return Task.CompletedTask;
        }

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default)
        {
            Feedback.Add(feedback);
            return Task.FromResult<ISessionResponse>(CommandAck.For(feedback.SessionId));
        }
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
