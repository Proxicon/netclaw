// -----------------------------------------------------------------------
// <copyright file="TeamsPersonalRoutingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence;
using Akka.Persistence.Hosting;
using Akka.Serialization;
using Google.Protobuf;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Teams.Api.Activities;
using Microsoft.Teams.Api.Entities;
using Microsoft.Teams.Plugins.AspNetCore.Extensions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Channels.Teams;
using Netclaw.Channels.Teams.Serialization;
using Netclaw.Channels.Telemetry;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;
using static Netclaw.Actors.Sessions.SessionProtocol;
using LegacyProto = Netclaw.Actors.Serialization.Proto;
using TeamsAccount = Microsoft.Teams.Api.Account;
using TeamsChannel = Microsoft.Teams.Api.Channel;
using TeamsChannelData = Microsoft.Teams.Api.ChannelData;
using TeamsConversation = Microsoft.Teams.Api.Conversation;
using TeamsConversationType = Microsoft.Teams.Api.ConversationType;
using TeamsTeam = Microsoft.Teams.Api.Team;

namespace Netclaw.Daemon.Tests.Configuration;

[Collection("TeamsTelemetry")]
public sealed class TeamsPersonalRoutingTests(ITestOutputHelper output) : PersistenceTestKit(output: output)
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        base.ConfigureAkka(builder, provider);
        builder.WithNetclawSerialization().WithTeamsPersistenceSerialization();
    }

    [Fact]
    public void Teams_persistence_records_use_the_teams_owned_v2_serializer()
    {
        var value = new TeamsProactiveDeliveryRecorded
        {
            DeliveryKey = "reminder-v2:1",
            State = (int)TeamsProactiveDeliveryState.Sent,
            DestinationGeneration = 1
        };

        var serializer = Sys.Serialization.FindSerializerFor(value);
        var manifest = Assert.IsAssignableFrom<SerializerWithStringManifest>(serializer).Manifest(value);
        var restored = Assert.IsType<TeamsProactiveDeliveryRecorded>(
            Sys.Serialization.Deserialize(serializer.ToBinary(value), serializer.Identifier, manifest));

        Assert.Equal(151, serializer.Identifier);
        Assert.Equal("teams-delivery-recorded-v2", manifest);
        Assert.Equal(value, restored);
    }

    [Fact]
    public void Binding_snapshot_roundtrips_the_last_invalidated_destination_generation()
    {
        var value = new TeamsBindingSnapshot([])
        {
            LastDestinationGeneration = 7,
            ProactiveDeliveries = [new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "snapshot-roundtrip:1",
                State = (int)TeamsProactiveDeliveryState.FailedPermanent,
                DestinationGeneration = 7,
                InvalidatesDestination = true
            }]
        };

        var serializer = Sys.Serialization.FindSerializerFor(value);
        var manifest = Assert.IsAssignableFrom<SerializerWithStringManifest>(serializer).Manifest(value);
        var restored = Assert.IsType<TeamsBindingSnapshot>(
            Sys.Serialization.Deserialize(serializer.ToBinary(value), serializer.Identifier, manifest));

        Assert.Equal(7, restored.LastDestinationGeneration);
        Assert.Equal(value.ProactiveDeliveries, restored.ProactiveDeliveries);
    }

    [Fact]
    public async Task Http_personal_capture_survives_request_scope_disposal_and_uses_the_app_level_reply_client()
    {
        var requestLifetime = new RequestLifetimeProbe();
        var replyClient = new RequestIndependentReplyClient(requestLifetime);
        await using var app = await BuildRequestIndependenceHostAsync(requestLifetime, replyClient);
        var sessionId = CreateSessionId("tenant-a", "request-personal");
        using var inboundCancellation = new CancellationTokenSource();
        using var request = CreateTeamsActivityRequest(CreateSdkPersonalMessage("request-personal", "request-personal-activity"));
        using var response = await app.GetTestClient().SendAsync(request, inboundCancellation.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ReceiveOutputSubscriber();
        response.Dispose();
        inboundCancellation.Cancel();
        await AwaitAssertAsync(() => Assert.True(requestLifetime.AllDisposed), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(ActorRegistry.For(Sys).TryGet<TeamsGatewayActorKey>(out var gateway));
        gateway.Tell(CreateReminder(sessionId, "request-personal:1"));
        var subscriber = ReceiveNextOutputSubscriber();
        subscriber.Tell(new TextOutput("reply after request disposal")
        {
            SessionId = sessionId,
            SourceReminderId = new ReminderId("request-personal:1")
        });

        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var delivery = Assert.Single(replyClient.Messages);
        Assert.Equal(TeamsConversationScope.Personal, delivery.Destination.Scope);
        Assert.Equal("request-personal", delivery.Destination.ConversationId);
        Assert.Equal("user-a", delivery.Destination.UserId);
        Assert.Equal("https://request-service.invalid/", delivery.Destination.ServiceUrl);
        Assert.Equal("reply after request disposal", delivery.Text);
        Assert.True(requestLifetime.AllDisposed);
    }

    [Fact]
    public async Task Http_channel_root_capture_survives_request_scope_disposal_without_a_top_level_fallback()
    {
        var requestLifetime = new RequestLifetimeProbe();
        var replyClient = new RequestIndependentReplyClient(requestLifetime);
        await using var app = await BuildRequestIndependenceHostAsync(requestLifetime, replyClient, includeChannel: true);
        const string conversationId = "request-channel;messageid=request-root";
        Assert.True(TeamsSessionIdentifierCodec.TryCreateChannel(
            "tenant-a", conversationId, "request-root", out var sessionId, out _));
        using var request = CreateTeamsActivityRequest(CreateSdkChannelRootMessage(conversationId, "request-root"));
        using var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ReceiveOutputSubscriber();
        response.Dispose();
        await AwaitAssertAsync(() => Assert.True(requestLifetime.AllDisposed), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(ActorRegistry.For(Sys).TryGet<TeamsGatewayActorKey>(out var gateway));
        gateway.Tell(CreateReminder(sessionId, "request-channel:1"));
        var subscriber = ReceiveNextOutputSubscriber();
        subscriber.Tell(new TextOutput("channel reply after request disposal")
        {
            SessionId = sessionId,
            SourceReminderId = new ReminderId("request-channel:1")
        });

        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        var delivery = Assert.Single(replyClient.Messages);
        Assert.Equal(TeamsConversationScope.Channel, delivery.Destination.Scope);
        Assert.Equal(conversationId, delivery.Destination.ConversationId);
        Assert.Equal("request-root", delivery.Destination.RootActivityId);
        Assert.Equal("team-a", delivery.Destination.TeamId);
        Assert.Equal("channel-a", delivery.Destination.ChannelId);
        Assert.Equal("request-root", delivery.ReplyToActivityId);
        Assert.True(requestLifetime.AllDisposed);
    }

    [Fact]
    public async Task Cancelled_http_request_captures_no_destination_and_later_generic_delivery_fails_closed()
    {
        var requestLifetime = new RequestLifetimeProbe();
        var replyClient = new RequestIndependentReplyClient(requestLifetime);
        await using var app = await BuildRequestIndependenceHostAsync(requestLifetime, replyClient);
        var sessionId = CreateSessionId("tenant-a", "request-cancelled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var request = CreateTeamsActivityRequest(CreateSdkPersonalMessage("request-cancelled", "request-cancelled-activity"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            app.GetTestClient().SendAsync(request, cancellation.Token));

        Assert.True(ActorRegistry.For(Sys).TryGet<TeamsGatewayActorKey>(out var gateway));
        var dispatcher = CreateTestProbe();
        gateway.Tell(CreateReminder(sessionId, "request-cancelled:1"), dispatcher.Ref);
        var nack = await dispatcher.ExpectMsgAsync<CommandNack>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("destination", nack.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(replyClient.Messages);
    }

    [Fact]
    public void Proactive_destination_resolution_is_explicit_and_fail_closed()
    {
        var session = CreateSessionId("tenant-a", "conversation-a");
        var other = CreateSessionId("tenant-a", "conversation-b");
        var current = new TeamsProactiveDestinationCandidate(session, TeamsConversationScope.Personal, 1, true);
        var invalid = new TeamsProactiveDestinationCandidate(other, TeamsConversationScope.Personal, 1, false);

        Assert.Equal(TeamsDestinationResolutionDisposition.Resolved,
            TeamsProactiveDestinationResolver.Resolve(session, TeamsConversationScope.Personal, null, [current]).Disposition);
        Assert.Equal(TeamsDestinationResolutionDisposition.Unavailable,
            TeamsProactiveDestinationResolver.Resolve(session, TeamsConversationScope.Personal, null, [invalid]).Disposition);
        Assert.Equal(TeamsDestinationResolutionDisposition.Rejected,
            TeamsProactiveDestinationResolver.Resolve(session, TeamsConversationScope.Personal, other.Value, [current, new(other, TeamsConversationScope.Personal, 1, true)]).Disposition);
        Assert.Equal(TeamsDestinationResolutionDisposition.Unavailable,
            TeamsProactiveDestinationResolver.Resolve(session, TeamsConversationScope.Channel, null, [current]).Disposition);
        Assert.Equal(TeamsDestinationResolutionDisposition.Ambiguous,
            TeamsProactiveDestinationResolver.Resolve(session, TeamsConversationScope.Personal, null, [current, current]).Disposition);
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
    public async Task Legacy_journal_only_recovers_to_a_teams_v2_snapshot_and_survives_restart()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-legacy-journal");
        var persistenceId = BindingPersistenceId(sessionId);
        var fingerprint = ActivityFingerprint.Create("legacy-journal-activity");
        await SeedJournalAsync(persistenceId,
            new DurableActivityDispatchReserved(fingerprint, null),
            DecodeLegacy("tapc-v1", CreateLegacyApproval("legacy-journal-correlation")),
            DecodeLegacy("tpdc-v1", CreateLegacyDestination("conversation-legacy-journal")),
            DecodeLegacy("tpdr-v1", new LegacyProto.TeamsProactiveDeliveryRecordedProto
            {
                DeliveryKey = "legacy-journal:1",
                State = (int)TeamsProactiveDeliveryState.Sent
            }));

        var dependencies = CreateDependencies(CreatePipeline(TestActor));
        var actor = CreateBindingActor(sessionId, dependencies, "legacy-journal-first");
        var snapshot = await WaitForBindingSnapshotAsync(persistenceId);

        Assert.Equal(TeamsBindingSnapshot.CurrentMigrationVersion, snapshot.MigrationVersion);
        Assert.Contains(fingerprint, snapshot.ActivityFingerprints);
        Assert.Contains(snapshot.Approvals, approval => approval.CorrelationId == "legacy-journal-correlation");
        Assert.Equal("conversation-legacy-journal", snapshot.Destination?.ConversationId);
        Assert.Contains(snapshot.ProactiveDeliveries, delivery => delivery.DeliveryKey == "legacy-journal:1");
        Assert.Equal(TeamsMigrationHealthState.Completed, (await GetDiagnosticsAsync(actor)).Migration);

        Watch(actor);
        actor.Tell(PoisonPill.Instance);
        ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = CreateBindingActor(sessionId, dependencies, "legacy-journal-recovered");
        Assert.Equal(
            TeamsBindingRouteDisposition.Duplicate,
            (await RouteAsync(recovered, CreateActivity(
                "legacy-journal-activity",
                "tenant-a",
                "conversation-legacy-journal"))).Disposition);
    }

    [Fact]
    public async Task Legacy_snapshot_only_recovers_all_binding_state_to_a_teams_v2_snapshot()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-legacy-snapshot");
        var persistenceId = BindingPersistenceId(sessionId);
        var fingerprint = ActivityFingerprint.Create("legacy-snapshot-activity");
        await SeedSnapshotAsync(persistenceId, 7, DecodeLegacy("dads-v1", CreateLegacyBindingSnapshot(
            fingerprint,
            "conversation-legacy-snapshot",
            "legacy-snapshot-correlation",
            "legacy-snapshot:1")));

        var dependencies = CreateDependencies(CreatePipeline(TestActor));
        var first = CreateBindingActor(sessionId, dependencies, "legacy-snapshot-only");
        var snapshot = await WaitForBindingSnapshotAsync(persistenceId);

        Assert.Equal(TeamsBindingSnapshot.CurrentMigrationVersion, snapshot.MigrationVersion);
        Assert.Equal([fingerprint], snapshot.ActivityFingerprints);
        Assert.Single(snapshot.Approvals);
        Assert.Equal("legacy-snapshot-correlation", snapshot.Approvals[0].CorrelationId);
        Assert.Equal("conversation-legacy-snapshot", snapshot.Destination?.ConversationId);
        Assert.Single(snapshot.ProactiveDeliveries);
        Assert.Equal("legacy-snapshot:1", snapshot.ProactiveDeliveries[0].DeliveryKey);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = CreateBindingActor(sessionId, dependencies, "legacy-snapshot-only-recovered");
        Assert.Equal(TeamsMigrationHealthState.Completed, (await GetDiagnosticsAsync(recovered)).Migration);
        Assert.Equal([fingerprint], (await WaitForBindingSnapshotAsync(persistenceId)).ActivityFingerprints);
    }

    [Fact]
    public async Task Legacy_snapshot_and_events_recover_in_sequence_before_the_v2_migration_snapshot()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-legacy-sequence");
        var persistenceId = BindingPersistenceId(sessionId);
        var fingerprint = ActivityFingerprint.Create("legacy-sequence-activity");
        await SeedSnapshotAsync(persistenceId, 1, DecodeLegacy("dads-v1", CreateLegacyBindingSnapshot(
            fingerprint,
            "conversation-legacy-sequence",
            "legacy-sequence-correlation",
            "legacy-sequence:1")));
        await SeedJournalAsync(persistenceId, 2,
            DecodeLegacy("tpdc-v1", CreateLegacyDestination("conversation-legacy-sequence", "https://service-refreshed.invalid/")),
            DecodeLegacy("tacd-v1", new LegacyProto.TeamsApprovalCardDeliveredProto
            {
                CorrelationId = "legacy-sequence-correlation",
                PromptId = "legacy-sequence-prompt"
            }));

        var dependencies = CreateDependencies(CreatePipeline(TestActor));
        var first = CreateBindingActor(sessionId, dependencies, "legacy-snapshot-events");
        var snapshot = await WaitForBindingSnapshotAsync(persistenceId);

        Assert.Equal("https://service-refreshed.invalid/", snapshot.Destination?.ServiceUrl);
        Assert.Equal(2, snapshot.Destination?.Generation);
        Assert.Equal("legacy-sequence-prompt", snapshot.Approvals.Single().PromptId);
        Assert.Contains(snapshot.ActivityFingerprints, value => value == fingerprint);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);
        CreateBindingActor(sessionId, dependencies, "legacy-snapshot-events-recovered");
        var recovered = await WaitForBindingSnapshotAsync(persistenceId);
        Assert.Equal(2, recovered.Destination?.Generation);
        Assert.Equal("legacy-sequence-prompt", recovered.Approvals.Single().PromptId);
    }

    [Fact]
    public async Task Legacy_and_v2_records_can_coexist_before_compaction_to_a_v2_snapshot()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-legacy-v2");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedJournalAsync(persistenceId,
            DecodeLegacy("tpdc-v1", CreateLegacyDestination("conversation-legacy-v2")),
            new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "legacy-v2:1",
                State = (int)TeamsProactiveDeliveryState.FailedRetryable,
                DestinationGeneration = 1
            });

        var dependencies = CreateDependencies(CreatePipeline(TestActor));
        var first = CreateBindingActor(sessionId, dependencies, "legacy-v2-mixed");
        var snapshot = await WaitForBindingSnapshotAsync(persistenceId);

        Assert.IsType<TeamsBindingSnapshot>(snapshot);
        Assert.Contains(snapshot.ProactiveDeliveries, delivery =>
            delivery.DeliveryKey == "legacy-v2:1"
            && delivery.State == (int)TeamsProactiveDeliveryState.FailedRetryable
            && delivery.DestinationGeneration == 1);
        Assert.Equal(151, Sys.Serialization.FindSerializerFor(snapshot).Identifier);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);
        CreateBindingActor(sessionId, dependencies, "legacy-v2-mixed-recovered");
        Assert.Contains((await WaitForBindingSnapshotAsync(persistenceId)).ProactiveDeliveries, delivery =>
            delivery.DeliveryKey == "legacy-v2:1" && delivery.DestinationGeneration == 1);
    }

    [Fact]
    public async Task Legacy_channel_index_recovers_to_a_teams_v2_snapshot()
    {
        const string conversationId = "conversation-legacy-channel;messageid=root-a";
        var sessionId = CreateChannelSessionId(conversationId);
        var persistenceId = "teams-channel-conversation-" + Uri.EscapeDataString(sessionId.Value);
        var fingerprint = ActivityFingerprint.Create("legacy-channel-root");
        await SeedJournalAsync(persistenceId, DecodeLegacy("dtcam-v1", new LegacyProto.DurableTeamsChannelActivityMappedProto
        {
            ActivityFingerprint = fingerprint,
            SessionId = sessionId.Value
        }));

        var options = new TeamsChannelOptions
        {
            TenantId = "tenant-a",
            MentionOnly = true,
            AllowedTeamIds = ["team-a"],
            AllowedChannelIds = ["channel-a"]
        };
        var dependencies = new TeamsConversationDependencies(
            options,
            CreatePipeline(TestActor),
            new TestTeamsReplyClient(),
            new TeamsOutputRenderer(),
            TimeProvider.System);
        var first = Sys.ActorOf(TeamsConversationActor.CreateProps(sessionId, dependencies), "legacy-channel-index");
        var snapshot = await WaitForSnapshotAsync<TeamsChannelActivityIndexSnapshot>(persistenceId);

        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal(fingerprint, entry.ActivityFingerprint);
        Assert.Equal(sessionId.Value, entry.SessionId);
        Assert.Equal(151, Sys.Serialization.FindSerializerFor(snapshot).Identifier);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);
        Sys.ActorOf(TeamsConversationActor.CreateProps(sessionId, dependencies), "legacy-channel-index-recovered");
        Assert.Equal(fingerprint, Assert.Single((await WaitForSnapshotAsync<TeamsChannelActivityIndexSnapshot>(persistenceId)).Entries).ActivityFingerprint);
    }

    [Fact]
    public async Task Failed_migration_snapshot_is_retained_and_a_restart_retries_successfully()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-migration-retry");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedJournalAsync(persistenceId, DecodeLegacy("tpdc-v1", CreateLegacyDestination("conversation-migration-retry")));
        await Snapshots.OnSave.Fail();

        var dependencies = CreateDependencies(CreatePipeline(TestActor));
        var failed = CreateBindingActor(sessionId, dependencies, "migration-retry-failed");
        Assert.Equal(TeamsMigrationHealthState.Failed,
            (await WaitForMigrationStateAsync(failed, TeamsMigrationHealthState.Failed)).Migration);

        await Snapshots.OnSave.Pass();
        Watch(failed);
        failed.Tell(PoisonPill.Instance);
        ExpectTerminated(failed, cancellationToken: TestContext.Current.CancellationToken);

        var retried = CreateBindingActor(sessionId, dependencies, "migration-retry-success");
        var snapshot = await WaitForBindingSnapshotAsync(persistenceId);
        Assert.Equal("conversation-migration-retry", snapshot.Destination?.ConversationId);
        Assert.Equal(TeamsMigrationHealthState.Completed,
            (await WaitForMigrationStateAsync(retried, TeamsMigrationHealthState.Completed)).Migration);
    }

    [Fact]
    public async Task Repeated_restart_failures_do_not_clear_required_legacy_migration()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-migration-restarts");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedJournalAsync(persistenceId, DecodeLegacy("tpdc-v1", CreateLegacyDestination("conversation-migration-restarts")));
        await Snapshots.OnSave.Fail();

        var dependencies = CreateDependencies(CreatePipeline(TestActor));
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var actor = CreateBindingActor(sessionId, dependencies, $"migration-restart-{attempt}");
            Assert.Equal(TeamsMigrationHealthState.Failed,
                (await WaitForMigrationStateAsync(actor, TeamsMigrationHealthState.Failed)).Migration);
            Watch(actor);
            actor.Tell(PoisonPill.Instance);
            ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);
        }

        await Snapshots.OnSave.Pass();
        CreateBindingActor(sessionId, dependencies, "migration-restart-success");
        Assert.IsType<TeamsBindingSnapshot>(await WaitForBindingSnapshotAsync(persistenceId));
    }

    [Fact]
    public void Malformed_or_insufficient_legacy_payloads_are_decode_only_and_never_writeable()
    {
        var serializer = Assert.IsType<NetclawProtobufSerializer>(
            Sys.Serialization.FindSerializerFor(new SessionId("serializer-proof")));
        var malformedDestination = serializer.FromBinary(
            new LegacyProto.TeamsProactiveDestinationCapturedProto().ToByteArray(),
            "tpdc-v1");
        var insufficientSnapshot = serializer.FromBinary(
            new LegacyProto.DurableActivityDispatchSnapshotProto
            {
                TeamsDestination = new LegacyProto.TeamsProactiveDestinationSnapshotEntryProto()
            }.ToByteArray(),
            "dads-v1");

        Assert.IsType<LegacyChannelPersistenceEnvelope>(malformedDestination);
        Assert.IsType<LegacyChannelPersistenceEnvelope>(insufficientSnapshot);
        Assert.Throws<ArgumentException>(() => serializer.Manifest(malformedDestination));
        Assert.Throws<ArgumentException>(() => serializer.Manifest(insufficientSnapshot));
    }

    [Fact]
    public async Task Insufficient_legacy_destination_fails_closed_during_binding_recovery()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-malformed-legacy");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedJournalAsync(persistenceId, DecodeLegacy("tpdc-v1", new LegacyProto.TeamsProactiveDestinationCapturedProto()));

        var observer = CreateTestProbe();
        var dependencies = CreateDependencies(CreatePipeline(TestActor));
        Sys.ActorOf(
            Props.Create(() => new StopOnRecoveryFailureParent(
                TeamsSessionBindingActor.CreateProps(sessionId, dependencies),
                observer.Ref)),
            "malformed-legacy-parent");
        var binding = await observer.ExpectMsgAsync<IActorRef>(cancellationToken: TestContext.Current.CancellationToken);
        Watch(binding);
        ExpectTerminated(binding, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await LoadLatestSnapshotAsync(persistenceId));
    }

    [Fact]
    public void New_teams_persistence_writes_use_only_the_teams_owned_v2_serializer()
    {
        var values = new ITeamsPersistenceMessage[]
        {
            new TeamsApprovalPendingCreated
            {
                CallId = "call", CorrelationId = "correlation", NonceHash = new string('a', 64),
                ExpiresAtUnixMilliseconds = 4_102_444_800_000
            },
            new TeamsApprovalCardDelivered { CorrelationId = "correlation", PromptId = "prompt" },
            new TeamsApprovalConsumed { CorrelationId = "correlation", Decision = "approve", ConsumedAtUnixMilliseconds = 1 },
            new TeamsProactiveDestinationCaptured
            {
                TenantId = "tenant-a", ConversationId = "conversation-v2", Scope = (int)TeamsConversationScope.Personal,
                ServiceUrl = "https://service.invalid/", UserId = "user-a", Generation = 1
            },
            new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "delivery:1", State = (int)TeamsProactiveDeliveryState.Sent, DestinationGeneration = 1
            },
            new TeamsBindingSnapshot([]),
            new TeamsChannelActivityMapped(ActivityFingerprint.Create("v2-channel"), "channel-session", null),
            new TeamsChannelActivityIndexSnapshot([])
        };

        foreach (var value in values)
        {
            var serializer = Assert.IsAssignableFrom<SerializerWithStringManifest>(Sys.Serialization.FindSerializerFor(value));
            Assert.Equal(151, serializer.Identifier);
            Assert.DoesNotContain("-v1", serializer.Manifest(value), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Destination_generations_start_at_one_ignore_identical_refreshes_and_bind_new_deliveries()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-generation");
        var persistenceId = BindingPersistenceId(sessionId);
        var dependencies = CreateDependencies(CreatePipeline(TestActor), enabled: true);
        var actor = CreateBindingActor(sessionId, dependencies, "generation-policy");

        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("generation-first", "tenant-a", "conversation-generation", "https://generation-first.invalid/"))).Disposition);
        ReceiveOutputSubscriber();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "generation:1")));
        ReceiveNextOutputSubscriber();

        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("generation-identical", "tenant-a", "conversation-generation", "https://generation-first.invalid/"))).Disposition);
        ReceiveNextOutputSubscriber();
        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("generation-refresh", "tenant-a", "conversation-generation", "https://generation-second.invalid/"))).Disposition);
        ReceiveNextOutputSubscriber();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "generation:2")));
        ReceiveNextOutputSubscriber();

        var events = await ReadJournalAsync(persistenceId);
        var captures = events.OfType<TeamsProactiveDestinationCaptured>().ToArray();
        Assert.Equal(2, captures.Length);
        Assert.Equal([1, 2], captures.Select(capture => capture.Generation));
        var reservations = events.OfType<TeamsProactiveDeliveryRecorded>()
            .Where(recorded => recorded.State == (int)TeamsProactiveDeliveryState.Pending)
            .ToDictionary(recorded => recorded.DeliveryKey, StringComparer.Ordinal);
        Assert.Equal(1, reservations["generation:1"].DestinationGeneration);
        Assert.Equal(2, reservations["generation:2"].DestinationGeneration);

        Watch(actor);
        actor.Tell(PoisonPill.Instance);
        ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = CreateBindingActor(sessionId, dependencies, "generation-policy-recovered");
        Assert.Equal(TeamsProactiveHealthState.Available, (await GetDiagnosticsAsync(recovered)).Health);
    }

    [Fact]
    public async Task Generation_overflow_fails_closed_without_wraparound()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-generation-overflow");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedSnapshotAsync(persistenceId, 1, CreateBindingSnapshot(
            "conversation-generation-overflow",
            "https://generation-max.invalid/",
            long.MaxValue));

        var observer = CreateTestProbe();
        var dependencies = CreateDependencies(CreatePipeline(TestActor));
        Sys.ActorOf(Props.Create(() => new StopOnRecoveryFailureParent(
            TeamsSessionBindingActor.CreateProps(sessionId, dependencies), observer.Ref)), "generation-overflow-parent");
        var binding = await observer.ExpectMsgAsync<IActorRef>(cancellationToken: TestContext.Current.CancellationToken);
        Watch(binding);
        binding.Tell(new TeamsBindingIngress(
            CreateActivity("generation-overflow-refresh", "tenant-a", "conversation-generation-overflow", "https://generation-overflow.invalid/"),
            TestContext.Current.CancellationToken));
        ExpectTerminated(binding, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Stale_completion_updates_only_its_old_delivery_and_never_posts_or_invalidates_the_refreshed_generation()
    {
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-stale-matrix");
        var persistenceId = BindingPersistenceId(sessionId);
        var dependencies = CreateDependencies(CreatePipeline(TestActor), replyClient: replyClient, enabled: true);
        var actor = CreateBindingActor(sessionId, dependencies, "stale-matrix");

        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("stale-first", "tenant-a", "conversation-stale-matrix", "https://stale-first.invalid/"))).Disposition);
        ReceiveOutputSubscriber();
        var observer = CreateTestProbe();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "stale-matrix:1", observer.Ref)));
        ReceiveNextOutputSubscriber();
        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("stale-refresh", "tenant-a", "conversation-stale-matrix", "https://stale-second.invalid/"))).Disposition);
        ReceiveNextOutputSubscriber();

        actor.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("late success")
        {
            SessionId = sessionId,
            SourceReminderId = new ReminderId("stale-matrix:1")
        }));
        Assert.False((await observer.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken)).Delivered);
        Assert.Empty(replyClient.Messages);

        var events = await ReadJournalAsync(persistenceId);
        var stale = events.OfType<TeamsProactiveDeliveryRecorded>().Last(recorded => recorded.DeliveryKey == "stale-matrix:1");
        Assert.Equal((int)TeamsProactiveDeliveryState.FailedRetryable, stale.State);
        Assert.Equal(1, stale.DestinationGeneration);
        Assert.False(stale.InvalidatesDestination);
        Assert.Equal(2, events.OfType<TeamsProactiveDestinationCaptured>().Last().Generation);

        Watch(actor);
        actor.Tell(PoisonPill.Instance);
        ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = CreateBindingActor(sessionId, dependencies, "stale-matrix-recovered");
        var diagnostics = await GetDiagnosticsAsync(recovered);
        Assert.Equal(TeamsProactiveHealthState.Available, diagnostics.Health);
        Assert.Equal(1, diagnostics.RetryableFailureCount);
        Assert.Equal(0, diagnostics.PermanentFailureCount);
    }

    [Fact]
    public async Task Atomic_permanent_failure_invalidates_only_the_matching_generation_and_survives_restart()
    {
        var replyClient = new RecordingTeamsReplyClient(new TeamsDeliveryResult(TeamsDeliveryStatus.InvalidDestination));
        var sessionId = CreateSessionId("tenant-a", "conversation-atomic-permanent");
        var persistenceId = BindingPersistenceId(sessionId);
        var dependencies = CreateDependencies(CreatePipeline(TestActor), replyClient: replyClient, enabled: true);
        var actor = CreateBindingActor(sessionId, dependencies, "atomic-permanent");

        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("atomic-first", "tenant-a", "conversation-atomic-permanent"))).Disposition);
        ReceiveOutputSubscriber();
        var observer = CreateTestProbe();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "atomic-permanent:1", observer.Ref)));
        ReceiveNextOutputSubscriber();
        actor.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("permanent failure")
        {
            SessionId = sessionId,
            SourceReminderId = new ReminderId("atomic-permanent:1")
        }));
        Assert.False((await observer.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken)).Delivered);

        var events = await ReadJournalAsync(persistenceId);
        var terminal = events.OfType<TeamsProactiveDeliveryRecorded>().Last(recorded => recorded.DeliveryKey == "atomic-permanent:1");
        Assert.Equal((int)TeamsProactiveDeliveryState.FailedPermanent, terminal.State);
        Assert.Equal(1, terminal.DestinationGeneration);
        Assert.True(terminal.InvalidatesDestination);
        Assert.Single(events.OfType<TeamsProactiveDeliveryRecorded>(), recorded =>
            recorded.DeliveryKey == "atomic-permanent:1" && recorded.InvalidatesDestination);

        Watch(actor);
        actor.Tell(PoisonPill.Instance);
        ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = CreateBindingActor(sessionId, dependencies, "atomic-permanent-recovered");
        var diagnostics = await GetDiagnosticsAsync(recovered);
        Assert.Equal(TeamsProactiveHealthState.Unavailable, diagnostics.Health);
        Assert.Equal(1, diagnostics.PermanentFailureCount);
        Assert.Equal(1, diagnostics.TerminalDeliveryCount);
        Assert.Equal(1, diagnostics.InvalidatedDestinationCount);
        Assert.Equal(1, diagnostics.MissingTargetCount);

        recovered.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("duplicate completion")
        {
            SessionId = sessionId,
            SourceReminderId = new ReminderId("atomic-permanent:1")
        }));
        var afterDuplicate = await ReadJournalAsync(persistenceId);
        Assert.Single(afterDuplicate.OfType<TeamsProactiveDeliveryRecorded>(), recorded =>
            recorded.DeliveryKey == "atomic-permanent:1" && recorded.InvalidatesDestination);
    }

    [Fact]
    public async Task Concurrent_reminder_outputs_complete_by_delivery_key_and_late_completions_are_ignored()
    {
        var replyClient = new RecordingTeamsReplyClient(
            new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered),
            new TeamsDeliveryResult(TeamsDeliveryStatus.Cancelled));
        var sessionId = CreateSessionId("tenant-a", "conversation-concurrent-reminders");
        var persistenceId = BindingPersistenceId(sessionId);
        var actor = CreateBindingActor(sessionId, CreateDependencies(CreatePipeline(TestActor), replyClient: replyClient), "concurrent-reminders");

        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity("concurrent-first", "tenant-a", "conversation-concurrent-reminders"))).Disposition);
        ReceiveOutputSubscriber();
        var observerA = CreateTestProbe();
        var observerB = CreateTestProbe();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "concurrent:a", observerA.Ref)));
        ReceiveNextOutputSubscriber();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "concurrent:b", observerB.Ref)));
        ReceiveNextOutputSubscriber();

        actor.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("B")
        {
            SessionId = sessionId, SourceReminderId = new ReminderId("concurrent:b")
        }));
        actor.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("A")
        {
            SessionId = sessionId, SourceReminderId = new ReminderId("concurrent:a")
        }));
        Assert.True((await observerB.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken)).Delivered);
        Assert.False((await observerA.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken)).Delivered);
        Assert.Equal(2, replyClient.Messages.Count);

        actor.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("late A")
        {
            SessionId = sessionId, SourceReminderId = new ReminderId("concurrent:a")
        }));
        await AwaitAssertAsync(() => Assert.Equal(2, replyClient.Messages.Count), cancellationToken: TestContext.Current.CancellationToken);
        var terminal = (await ReadJournalAsync(persistenceId)).OfType<TeamsProactiveDeliveryRecorded>()
            .Where(recorded => recorded.State is (int)TeamsProactiveDeliveryState.Sent or (int)TeamsProactiveDeliveryState.FailedRetryable)
            .GroupBy(recorded => recorded.DeliveryKey)
            .ToDictionary(group => group.Key, group => group.Last().State, StringComparer.Ordinal);
        Assert.Equal((int)TeamsProactiveDeliveryState.Sent, terminal["concurrent:b"]);
        Assert.Equal((int)TeamsProactiveDeliveryState.FailedRetryable, terminal["concurrent:a"]);
    }

    [Fact]
    public async Task Delivery_capacity_preserves_terminal_idempotency_and_invalid_snapshots_fail_closed()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-delivery-capacity");
        var persistenceId = BindingPersistenceId(sessionId);
        var retained = Enumerable.Range(0, TeamsSessionBindingActor.ProactiveDeliveryCapacity)
            .Select(index => new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = $"retained:{index}",
                State = (int)TeamsProactiveDeliveryState.Sent,
                DestinationGeneration = 1
            }).ToArray();
        await SeedSnapshotAsync(persistenceId, 1, CreateBindingSnapshot(
            "conversation-delivery-capacity",
            "https://capacity.invalid/",
            1,
            retained));

        var dependencies = CreateDependencies(CreatePipeline(TestActor), enabled: true);
        var actor = CreateBindingActor(sessionId, dependencies, "delivery-capacity");
        Assert.True((await GetDiagnosticsAsync(actor)).HasCapacityPressure);
        var existingObserver = CreateTestProbe();
        var existingDispatcher = CreateTestProbe();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "retained:0", existingObserver.Ref)), existingDispatcher.Ref);
        await existingDispatcher.ExpectMsgAsync<CommandAck>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True((await existingObserver.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken)).Delivered);
        var fullDispatcher = CreateTestProbe();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "new-at-capacity")), fullDispatcher.Ref);
        var nack = await fullDispatcher.ExpectMsgAsync<CommandNack>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("capacity", nack.Reason, StringComparison.OrdinalIgnoreCase);

        var invalidPersistenceId = BindingPersistenceId(CreateSessionId("tenant-a", "conversation-delivery-overcapacity"));
        await SeedSnapshotAsync(invalidPersistenceId, 1, CreateBindingSnapshot(
            "conversation-delivery-overcapacity",
            "https://overcapacity.invalid/",
            1,
            retained.Append(new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "overflow", State = (int)TeamsProactiveDeliveryState.Sent, DestinationGeneration = 1
            }).ToArray()));
        var stopObserver = CreateTestProbe();
        var invalidSession = CreateSessionId("tenant-a", "conversation-delivery-overcapacity");
        Sys.ActorOf(Props.Create(() => new StopOnRecoveryFailureParent(
            TeamsSessionBindingActor.CreateProps(invalidSession, dependencies),
            stopObserver.Ref)), "delivery-overcapacity-parent");
        var invalidBinding = await stopObserver.ExpectMsgAsync<IActorRef>(cancellationToken: TestContext.Current.CancellationToken);
        Watch(invalidBinding);
        ExpectTerminated(invalidBinding, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Interrupted_dispatch_has_no_partial_permanent_invalidation_after_restart()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-before-atomic");
        var dependencies = CreateDependencies(CreatePipeline(TestActor), enabled: true);
        var first = CreateBindingActor(sessionId, dependencies, "before-atomic-first");

        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity("before-atomic-activity", "tenant-a", "conversation-before-atomic"))).Disposition);
        ReceiveOutputSubscriber();
        first.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "before-atomic:1")));
        ReceiveNextOutputSubscriber();

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = CreateBindingActor(sessionId, dependencies, "before-atomic-recovered");
        TeamsBindingProactiveDiagnostics? diagnostics = null;
        await AwaitAssertAsync(() =>
        {
            diagnostics = GetDiagnosticsAsync(recovered).GetAwaiter().GetResult();
            Assert.Equal(1, diagnostics.UnknownDeliveryCount);
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(diagnostics);
        Assert.Equal(TeamsProactiveHealthState.Available, diagnostics.Health);
        Assert.Equal(0, diagnostics.PermanentFailureCount);
        Assert.Equal(1, diagnostics.UnknownDeliveryCount);
    }

    [Fact]
    public async Task Journal_only_stale_permanent_failure_is_terminal_without_invalidating_newer_destination()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-journal-stale-permanent");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedJournalAsync(persistenceId,
            CreateDestination("conversation-journal-stale-permanent", "https://journal-first.invalid/", 1),
            new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "journal-stale:1",
                State = (int)TeamsProactiveDeliveryState.Sending,
                DestinationGeneration = 1
            },
            CreateDestination("conversation-journal-stale-permanent", "https://journal-second.invalid/", 2),
            new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "journal-stale:1",
                State = (int)TeamsProactiveDeliveryState.FailedPermanent,
                DestinationGeneration = 1,
                InvalidatesDestination = true
            });

        var dependencies = CreateDependencies(CreatePipeline(TestActor), enabled: true);
        var first = CreateBindingActor(sessionId, dependencies, "journal-stale-permanent-first");
        var diagnostics = await GetDiagnosticsAsync(first);
        Assert.Equal(TeamsProactiveHealthState.Available, diagnostics.Health);
        Assert.Equal(1, diagnostics.PermanentFailureCount);
        Assert.Equal(1, diagnostics.TerminalDeliveryCount);
        Assert.Equal(0, diagnostics.InvalidatedDestinationCount);
        Assert.Equal(0, diagnostics.MissingTargetCount);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = CreateBindingActor(sessionId, dependencies, "journal-stale-permanent-recovered");
        diagnostics = await GetDiagnosticsAsync(recovered);
        Assert.Equal(TeamsProactiveHealthState.Available, diagnostics.Health);
        Assert.Equal(1, diagnostics.PermanentFailureCount);
    }

    [Fact]
    public async Task Snapshot_recovery_preserves_atomic_permanent_invalidation_without_reapplying_it()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-snapshot-permanent");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedSnapshotAsync(persistenceId, 4, new TeamsBindingSnapshot([])
        {
            LastDestinationGeneration = 1,
            ProactiveDeliveries = [new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "snapshot-permanent:1",
                State = (int)TeamsProactiveDeliveryState.FailedPermanent,
                DestinationGeneration = 1,
                InvalidatesDestination = true
            }]
        });

        var dependencies = CreateDependencies(CreatePipeline(TestActor), enabled: true);
        var first = CreateBindingActor(sessionId, dependencies, "snapshot-permanent-first");
        var diagnostics = await GetDiagnosticsAsync(first);
        Assert.Equal(TeamsProactiveHealthState.Unavailable, diagnostics.Health);
        Assert.Equal(1, diagnostics.PermanentFailureCount);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = CreateBindingActor(sessionId, dependencies, "snapshot-permanent-recovered");
        diagnostics = await GetDiagnosticsAsync(recovered);
        Assert.Equal(TeamsProactiveHealthState.Unavailable, diagnostics.Health);
        Assert.Equal(1, diagnostics.PermanentFailureCount);
        Assert.Empty(await ReadJournalAsync(persistenceId));
    }

    [Fact]
    public async Task Invalidated_snapshot_advances_before_a_new_destination_is_reserved()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-snapshot-recapture");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedSnapshotAsync(persistenceId, 4, new TeamsBindingSnapshot([])
        {
            LastDestinationGeneration = 1,
            ProactiveDeliveries = [new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "snapshot-recapture:1",
                State = (int)TeamsProactiveDeliveryState.FailedPermanent,
                DestinationGeneration = 1,
                InvalidatesDestination = true
            }]
        });

        var actor = CreateBindingActor(
            sessionId,
            CreateDependencies(CreatePipeline(TestActor), enabled: true),
            "snapshot-recapture");
        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity(
                "snapshot-recapture-activity",
                "tenant-a",
                "conversation-snapshot-recapture",
                "https://recaptured.invalid/"))).Disposition);
        ReceiveOutputSubscriber();

        var capture = Assert.Single((await ReadJournalAsync(persistenceId)).OfType<TeamsProactiveDestinationCaptured>());
        Assert.Equal(2, capture.Generation);
    }

    [Fact]
    public async Task Compaction_snapshot_retains_generation_and_terminal_idempotency_state()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-compaction-state");
        var persistenceId = BindingPersistenceId(sessionId);
        var replyClient = new RecordingTeamsReplyClient();
        var dependencies = CreateDependencies(
            CreatePipeline(Sys.ActorOf(DiscardActor.Create())),
            enabled: true,
            replyClient: replyClient);
        var first = CreateBindingActor(sessionId, dependencies, "compaction-state-first");

        Assert.Equal(TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity(
                "compaction-0",
                "tenant-a",
                "conversation-compaction-state"))).Disposition);
        first.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "compaction-delivery:1")));
        await AwaitAssertAsync(() => Assert.Contains(
                ReadJournalAsync(persistenceId).GetAwaiter().GetResult().OfType<TeamsProactiveDeliveryRecorded>(),
                recorded => recorded.DeliveryKey == "compaction-delivery:1"
                    && recorded.State == (int)TeamsProactiveDeliveryState.Sending),
            cancellationToken: TestContext.Current.CancellationToken);
        first.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("compaction delivery")
        {
            SessionId = sessionId,
            SourceReminderId = new ReminderId("compaction-delivery:1")
        }));
        await AwaitAssertAsync(() => Assert.Contains(
                ReadJournalAsync(persistenceId).GetAwaiter().GetResult().OfType<TeamsProactiveDeliveryRecorded>(),
                recorded => recorded.DeliveryKey == "compaction-delivery:1"
                    && recorded.State == (int)TeamsProactiveDeliveryState.Sent),
            cancellationToken: TestContext.Current.CancellationToken);

        for (var index = 1; index < 60; index++)
        {
            Assert.Equal(TeamsBindingRouteDisposition.Accepted,
                (await RouteAsync(first, CreateActivity(
                    $"compaction-{index}",
                    "tenant-a",
                    "conversation-compaction-state"))).Disposition);
        }

        var snapshot = await WaitForBindingSnapshotAsync(persistenceId);
        Assert.Equal(TeamsBindingSnapshot.CurrentMigrationVersion, snapshot.MigrationVersion);
        Assert.Equal(60, snapshot.ActivityFingerprints.Count);
        Assert.Equal(1, snapshot.Destination?.Generation);
        Assert.Equal("conversation-compaction-state", snapshot.Destination?.ConversationId);
        Assert.Contains(snapshot.ProactiveDeliveries, recorded =>
            recorded.DeliveryKey == "compaction-delivery:1"
                && recorded.State == (int)TeamsProactiveDeliveryState.Sent);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);
        var recovered = CreateBindingActor(sessionId, dependencies, "compaction-state-recovered");
        Assert.Equal(TeamsBindingRouteDisposition.Duplicate,
            (await RouteAsync(recovered, CreateActivity(
                "compaction-59",
                "tenant-a",
                "conversation-compaction-state"))).Disposition);
        Assert.Equal(TeamsProactiveHealthState.Available, (await GetDiagnosticsAsync(recovered)).Health);
        var dispatcher = CreateTestProbe();
        var observer = CreateTestProbe();
        recovered.Tell(new TeamsBindingReminder(CreateReminder(sessionId, "compaction-delivery:1", observer.Ref)), dispatcher.Ref);
        await dispatcher.ExpectMsgAsync<CommandAck>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True((await observer.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken)).Delivered);
        Assert.Single(replyClient.Messages);
    }

    [Fact]
    public async Task Snapshot_with_a_future_delivery_generation_fails_closed()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-future-delivery-generation");
        var persistenceId = BindingPersistenceId(sessionId);
        await SeedSnapshotAsync(persistenceId, 1, CreateBindingSnapshot(
            "conversation-future-delivery-generation",
            "https://future-generation.invalid/",
            1,
            [new TeamsProactiveDeliveryRecorded
            {
                DeliveryKey = "future-generation:1",
                State = (int)TeamsProactiveDeliveryState.Sent,
                DestinationGeneration = 2
            }]));

        var observer = CreateTestProbe();
        Sys.ActorOf(Props.Create(() => new StopOnRecoveryFailureParent(
            TeamsSessionBindingActor.CreateProps(sessionId, CreateDependencies(CreatePipeline(TestActor))), observer.Ref)),
            "future-generation-parent");
        var binding = await observer.ExpectMsgAsync<IActorRef>(cancellationToken: TestContext.Current.CancellationToken);
        Watch(binding);
        ExpectTerminated(binding, cancellationToken: TestContext.Current.CancellationToken);
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
    public async Task Allowed_personal_destination_survives_binding_restart_for_later_output()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-proactive-restart");
        var dependencies = CreateDependencies(pipeline, replyClient: replyClient);
        var first = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-proactive-first");

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity(
                "activity-proactive",
                "tenant-a",
                "conversation-proactive-restart"))).Disposition);
        ReceiveDispatchedMessage();
        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-proactive-second");
        recovered.Tell(new TeamsSessionBindingActor.BindingOutput(
            new TextOutput("later reminder output") { SessionId = sessionId }));

        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("conversation-proactive-restart", Assert.Single(replyClient.Messages).Destination.ConversationId);
        Assert.Equal(TeamsConversationScope.Personal, Assert.Single(replyClient.Messages).Destination.Scope);
    }

    [Fact]
    public async Task Rejected_personal_activity_cannot_create_a_destination()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-poisoning");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient, allowedUserIds: ["different-user"])));

        Assert.Equal(
            TeamsBindingRouteDisposition.Denied,
            (await RouteAsync(actor, CreateActivity("activity-poisoning", "tenant-a", "conversation-poisoning"))).Disposition);

        var telemetryBefore = ChannelTelemetry.For(ChannelType.Teams).GetSnapshot();
        actor.Tell(new TeamsSessionBindingActor.BindingOutput(
            new TextOutput("must not deliver") { SessionId = sessionId }));
        await AwaitAssertAsync(
            () => Assert.Equal(telemetryBefore.RepliesFailed + 1, ChannelTelemetry.For(ChannelType.Teams).GetSnapshot().RepliesFailed),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(replyClient.Messages);
    }

    [Fact]
    public async Task Reminder_without_a_persisted_destination_is_rejected_without_a_delivery_attempt()
    {
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-reminder-missing");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(CreatePipeline(TestActor), replyClient: replyClient)));
        var dispatcher = CreateTestProbe();

        actor.Tell(
            new TeamsBindingReminder(CreateReminder(sessionId, "reminder-missing:1")),
            dispatcher.Ref);

        var nack = await dispatcher.ExpectMsgAsync<CommandNack>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("destination", nack.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(replyClient.Messages);
    }

    [Fact]
    public async Task Proactive_diagnostics_are_actor_owned_and_do_not_disclose_destination_values()
    {
        var sessionId = CreateSessionId("tenant-a", "conversation-proactive-diagnostics");
        var dependencies = CreateDependencies(CreatePipeline(TestActor), enabled: true);
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies));

        var missing = await actor.Ask<TeamsBindingProactiveDiagnostics>(
            GetTeamsBindingProactiveDiagnostics.Instance,
            TestContext.Current.CancellationToken);
        Assert.Equal(TeamsProactiveHealthState.Unavailable, missing.Health);
        Assert.Equal("proactive_destination_missing", missing.ReasonCode);
        Assert.Equal(0, missing.PersonalDestinationCount);
        Assert.Equal(0, missing.ChannelDestinationCount);
        Assert.Equal(1, missing.MissingTargetCount);
        Assert.Equal(0, missing.InvalidatedDestinationCount);
        Assert.Equal(0, missing.TerminalDeliveryCount);
        Assert.Equal(0, missing.AmbiguousTargetCount);
        Assert.False(missing.HasInvalidRecoveredState);

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity(
                "activity-proactive-diagnostics",
                "tenant-a",
                "conversation-proactive-diagnostics"))).Disposition);
        ReceiveOutputSubscriber();

        var available = await actor.Ask<TeamsBindingProactiveDiagnostics>(
            GetTeamsBindingProactiveDiagnostics.Instance,
            TestContext.Current.CancellationToken);
        Assert.Equal(TeamsProactiveHealthState.Available, available.Health);
        Assert.Equal(1, available.PersonalDestinationCount);
        Assert.Equal(0, available.ChannelDestinationCount);
        Assert.Equal(0, available.MissingTargetCount);
        Assert.Equal(0, available.InvalidatedDestinationCount);
        Assert.Equal(0, available.TerminalDeliveryCount);
        Assert.Equal(0, available.AmbiguousTargetCount);
        Assert.False(available.HasInvalidRecoveredState);
        Assert.Null(available.ReasonCode);
        Assert.DoesNotContain("tenant-a", available.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("conversation-proactive-diagnostics", available.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reminder_known_destination_key_must_match_the_binding_session()
    {
        var pipeline = CreatePipeline(TestActor);
        var sessionId = CreateSessionId("tenant-a", "conversation-reminder-known");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity(
                "activity-reminder-known",
                "tenant-a",
                "conversation-reminder-known"))).Disposition);
        ReceiveOutputSubscriber();

        var dispatcher = CreateTestProbe();
        var otherSession = CreateSessionId("tenant-a", "conversation-reminder-other");
        actor.Tell(
            new TeamsBindingReminder(
                CreateReminder(sessionId, "reminder-known-rejected:1"),
                otherSession.Value),
            dispatcher.Ref);

        var nack = await dispatcher.ExpectMsgAsync<CommandNack>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("destination", nack.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reminder_known_destination_key_routes_only_the_captured_binding()
    {
        var pipeline = CreatePipeline(TestActor);
        var sessionId = CreateSessionId("tenant-a", "conversation-reminder-explicit");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity(
                "activity-reminder-explicit",
                "tenant-a",
                "conversation-reminder-explicit"))).Disposition);
        ReceiveOutputSubscriber();

        actor.Tell(new TeamsBindingReminder(
            CreateReminder(sessionId, "reminder-known-accepted:1"),
            sessionId.Value));

        ReceiveNextOutputSubscriber();
    }

    [Fact]
    public async Task Reminder_output_after_a_destination_refresh_does_not_post_to_the_refreshed_destination()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-reminder-stale");
        var actor = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(
            sessionId,
            CreateDependencies(pipeline, replyClient: replyClient)));

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity(
                "activity-reminder-stale-first",
                "tenant-a",
                "conversation-reminder-stale",
                "https://service-first.invalid/"))).Disposition);
        ReceiveOutputSubscriber();

        const string deliveryKey = "reminder-stale:1";
        var observer = CreateTestProbe();
        actor.Tell(new TeamsBindingReminder(CreateReminder(sessionId, deliveryKey, observer.Ref)));
        ReceiveNextOutputSubscriber();

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(actor, CreateActivity(
                "activity-reminder-stale-refresh",
                "tenant-a",
                "conversation-reminder-stale",
                "https://service-refresh.invalid/"))).Disposition);
        ReceiveNextOutputSubscriber();

        actor.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("stale reminder output")
        {
            SessionId = sessionId,
            SourceReminderId = new ReminderId(deliveryKey)
        }));

        var result = await observer.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Delivered);
        Assert.Empty(replyClient.Messages);
    }

    [Fact]
    public async Task Delivered_reminder_is_not_resent_after_binding_restart()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-reminder-idempotent");
        var dependencies = CreateDependencies(pipeline, replyClient: replyClient);
        var first = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-reminder-idempotent-first");
        var observer = CreateTestProbe();

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity(
                "activity-reminder-idempotent",
                "tenant-a",
                "conversation-reminder-idempotent"))).Disposition);
        ReceiveOutputSubscriber();

        const string deliveryKey = "reminder-idempotent:1";
        first.Tell(new TeamsBindingReminder(CreateReminder(sessionId, deliveryKey, observer.Ref)));
        ReceiveNextOutputSubscriber();
        first.Tell(new TeamsSessionBindingActor.BindingOutput(new TextOutput("reminder reply")
        {
            SessionId = sessionId,
            SourceReminderId = new ReminderId(deliveryKey)
        }));

        var delivered = await observer.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(delivered.Delivered);
        await AwaitAssertAsync(() => Assert.Single(replyClient.Messages), cancellationToken: TestContext.Current.CancellationToken);

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-reminder-idempotent-recovered");
        var retryObserver = CreateTestProbe();
        var dispatcher = CreateTestProbe();
        recovered.Tell(new TeamsBindingReminder(CreateReminder(sessionId, deliveryKey, retryObserver.Ref)), dispatcher.Ref);

        await dispatcher.ExpectMsgAsync<CommandAck>(cancellationToken: TestContext.Current.CancellationToken);
        var repeatedDelivery = await retryObserver.ExpectMsgAsync<ReminderDeliveryResult>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(repeatedDelivery.Delivered);
        Assert.Single(replyClient.Messages);
    }

    [Fact]
    public async Task Interrupted_sending_reminder_recovers_as_delivery_unknown_and_is_not_resent()
    {
        var pipeline = CreatePipeline(TestActor);
        var replyClient = new RecordingTeamsReplyClient();
        var sessionId = CreateSessionId("tenant-a", "conversation-reminder-interrupted");
        var dependencies = CreateDependencies(pipeline, replyClient: replyClient);
        var first = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-reminder-interrupted-first");

        Assert.Equal(
            TeamsBindingRouteDisposition.Accepted,
            (await RouteAsync(first, CreateActivity(
                "activity-reminder-interrupted",
                "tenant-a",
                "conversation-reminder-interrupted"))).Disposition);
        ReceiveOutputSubscriber();

        const string deliveryKey = "reminder-interrupted:1";
        first.Tell(new TeamsBindingReminder(CreateReminder(sessionId, deliveryKey)));
        ReceiveNextOutputSubscriber();

        Watch(first);
        first.Tell(PoisonPill.Instance);
        ExpectTerminated(first, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), "teams-reminder-interrupted-recovered");
        var dispatcher = CreateTestProbe();
        recovered.Tell(new TeamsBindingReminder(CreateReminder(sessionId, deliveryKey)), dispatcher.Ref);

        var nack = await dispatcher.ExpectMsgAsync<CommandNack>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("operator review", nack.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(replyClient.Messages);
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

    private IActorRef CreateBindingActor(
        SessionId sessionId,
        TeamsConversationDependencies dependencies,
        string name) => Sys.ActorOf(TeamsSessionBindingActor.CreateProps(sessionId, dependencies), name);

    private static string BindingPersistenceId(SessionId sessionId) =>
        "teams-personal-binding-" + Uri.EscapeDataString(sessionId.Value);

    private static TeamsBindingSnapshot CreateBindingSnapshot(
        string conversationId,
        string serviceUrl,
        long generation,
        IReadOnlyList<TeamsProactiveDeliveryRecorded>? deliveries = null) => new([])
    {
        Destination = CreateDestination(conversationId, serviceUrl, generation),
        LastDestinationGeneration = generation,
        ProactiveDeliveries = deliveries ?? Array.Empty<TeamsProactiveDeliveryRecorded>()
    };

    private static TeamsProactiveDestinationCaptured CreateDestination(
        string conversationId,
        string serviceUrl,
        long generation) => new()
    {
        TenantId = "tenant-a",
        ConversationId = conversationId,
        Scope = (int)TeamsConversationScope.Personal,
        ServiceUrl = serviceUrl,
        UserId = "user-a",
        Generation = generation
    };

    private async Task SeedJournalAsync(string persistenceId, params object[] payloads) =>
        await SeedJournalAsync(persistenceId, 1, payloads);

    private async Task SeedJournalAsync(string persistenceId, long firstSequenceNumber, params object[] payloads)
    {
        var writer = CreateTestProbe();
        var records = payloads.Select((payload, index) => (IPersistentRepresentation)new Persistent(
            payload,
            firstSequenceNumber + index,
            persistenceId)).ToImmutableArray();
        JournalActorRef.Tell(new WriteMessages([new AtomicWrite(records)], writer.Ref, 0), writer.Ref);
        await writer.ExpectMsgAsync<WriteMessagesSuccessful>(cancellationToken: TestContext.Current.CancellationToken);
        foreach (var _ in payloads)
            await writer.ExpectMsgAsync<WriteMessageSuccess>(cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task SeedSnapshotAsync(string persistenceId, long sequenceNumber, object snapshot)
    {
        var writer = CreateTestProbe();
        SnapshotsActorRef.Tell(new SaveSnapshot(
            new SnapshotMetadata(persistenceId, sequenceNumber, DateTime.UtcNow),
            snapshot), writer.Ref);
        await writer.ExpectMsgAsync<SaveSnapshotSuccess>(cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<object>> ReadJournalAsync(string persistenceId)
    {
        var reader = CreateTestProbe();
        JournalActorRef.Tell(new ReplayMessages(
            1,
            long.MaxValue,
            long.MaxValue,
            persistenceId,
            reader.Ref), reader.Ref);
        var messages = new List<object>();
        while (true)
        {
            var message = await reader.ExpectMsgAsync<object>(cancellationToken: TestContext.Current.CancellationToken);
            if (message is RecoverySuccess)
                return messages;
            messages.Add(Assert.IsType<ReplayedMessage>(message).Persistent.Payload);
        }
    }

    private async Task<TSnapshot> WaitForSnapshotAsync<TSnapshot>(string persistenceId)
        where TSnapshot : class
    {
        TSnapshot? snapshot = null;
        await AwaitAssertAsync(() =>
        {
            snapshot = LoadLatestSnapshotAsync(persistenceId).GetAwaiter().GetResult() as TSnapshot;
            Assert.NotNull(snapshot);
        }, cancellationToken: TestContext.Current.CancellationToken);
        return snapshot!;
    }

    private Task<TeamsBindingSnapshot> WaitForBindingSnapshotAsync(string persistenceId) =>
        WaitForSnapshotAsync<TeamsBindingSnapshot>(persistenceId);

    private async Task<object?> LoadLatestSnapshotAsync(string persistenceId)
    {
        var reader = CreateTestProbe();
        SnapshotsActorRef.Tell(new LoadSnapshot(
            persistenceId,
            SnapshotSelectionCriteria.Latest,
            long.MaxValue), reader.Ref);
        var loaded = await reader.ExpectMsgAsync<LoadSnapshotResult>(cancellationToken: TestContext.Current.CancellationToken);
        return loaded.Snapshot?.Snapshot;
    }

    private async Task<TeamsBindingProactiveDiagnostics> GetDiagnosticsAsync(IActorRef actor) =>
        await actor.Ask<TeamsBindingProactiveDiagnostics>(
            GetTeamsBindingProactiveDiagnostics.Instance,
            TestContext.Current.CancellationToken);

    private async Task<TeamsBindingProactiveDiagnostics> WaitForMigrationStateAsync(
        IActorRef actor,
        TeamsMigrationHealthState expected)
    {
        TeamsBindingProactiveDiagnostics? diagnostics = null;
        await AwaitAssertAsync(() =>
        {
            diagnostics = GetDiagnosticsAsync(actor).GetAwaiter().GetResult();
            Assert.Equal(expected, diagnostics.Migration);
        }, cancellationToken: TestContext.Current.CancellationToken);
        return diagnostics!;
    }

    private object DecodeLegacy<TMessage>(string manifest, TMessage message)
        where TMessage : IMessage
    {
        var serializer = Assert.IsType<NetclawProtobufSerializer>(
            Sys.Serialization.FindSerializerFor(new SessionId("legacy-fixture")));
        var decoded = serializer.FromBinary(message.ToByteArray(), manifest);
        Assert.IsType<LegacyChannelPersistenceEnvelope>(decoded);
        return decoded;
    }

    private static LegacyProto.TeamsApprovalPendingCreatedProto CreateLegacyApproval(string correlationId) => new()
    {
        CallId = "legacy-call",
        CorrelationId = correlationId,
        NonceHash = new string('a', 64),
        RequesterSenderId = "user-a",
        ExpiresAtUnixMilliseconds = 4_102_444_800_000
    };

    private static LegacyProto.TeamsProactiveDestinationCapturedProto CreateLegacyDestination(
        string conversationId,
        string serviceUrl = "https://service.invalid/") => new()
    {
        TenantId = "tenant-a",
        ConversationId = conversationId,
        Scope = (int)TeamsConversationScope.Personal,
        ServiceUrl = serviceUrl,
        UserId = "user-a"
    };

    private static LegacyProto.DurableActivityDispatchSnapshotProto CreateLegacyBindingSnapshot(
        string fingerprint,
        string conversationId,
        string correlationId,
        string deliveryKey)
    {
        var snapshot = new LegacyProto.DurableActivityDispatchSnapshotProto
        {
            TeamsDestination = new LegacyProto.TeamsProactiveDestinationSnapshotEntryProto
            {
                TenantId = "tenant-a",
                ConversationId = conversationId,
                Scope = (int)TeamsConversationScope.Personal,
                ServiceUrl = "https://service.invalid/",
                UserId = "user-a"
            }
        };
        snapshot.ActivityFingerprints.Add(fingerprint);
        snapshot.TeamsApprovals.Add(new LegacyProto.TeamsApprovalSnapshotEntryProto
        {
            CallId = "legacy-call",
            CorrelationId = correlationId,
            NonceHash = new string('a', 64),
            RequesterSenderId = "user-a",
            ExpiresAtUnixMilliseconds = 4_102_444_800_000
        });
        snapshot.TeamsProactiveDeliveries.Add(new LegacyProto.TeamsProactiveDeliverySnapshotEntryProto
        {
            DeliveryKey = deliveryKey,
            State = (int)TeamsProactiveDeliveryState.Sent
        });
        return snapshot;
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

    private async Task<WebApplication> BuildRequestIndependenceHostAsync(
        RequestLifetimeProbe requestLifetime,
        RequestIndependentReplyClient replyClient,
        bool includeChannel = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Teams:Enabled"] = "true",
            ["Teams:TenantId"] = "tenant-a",
            ["Teams:ClientId"] = "test-client",
            ["Teams:ClientSecret"] = "test-secret",
            ["Teams:AllowDirectMessages"] = "true",
            ["Teams:AllowedUserIds:0"] = "user-a",
            ["Teams:MentionOnly"] = includeChannel ? "true" : "false",
            ["Teams:BotId"] = "bot",
            ["Teams:AllowedTeamIds:0"] = "team-a",
            ["Teams:AllowedChannelIds:0"] = "channel-a"
        });
        builder.Services.AddChannelIntegrations(builder.Configuration);
        builder.Services.AddSingleton<ActorSystem>(Sys);
        builder.Services.AddSingleton<ISessionPipeline>(CreatePipeline(TestActor));
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton(requestLifetime);
        builder.Services.AddScoped<RequestScopeSentinel>();
        builder.AddTeamsIngress();
        builder.Services.RemoveAll<ITeamsReplyClient>();
        builder.Services.AddSingleton<ITeamsReplyClient>(replyClient);
        builder.Services.PostConfigure<AuthorizationOptions>(options =>
            options.AddPolicy(
                HostApplicationBuilderExtensions.TeamsTokenAuthConstants.AuthorizationPolicy,
                policy => policy.RequireAssertion(_ => true)));

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            _ = context.RequestServices.GetRequiredService<RequestScopeSentinel>();
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseTeamsIngress();
        app.MapTeamsActivityEndpoint();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static HttpRequestMessage CreateTeamsActivityRequest(MessageActivity activity)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, TeamsActivityEndpointExtensions.ActivityPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(activity), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            "Bearer eyJhbGciOiJub25lIn0.eyJ0aWQiOiJ0ZW5hbnQtYSJ9.");
        return request;
    }

    private static MessageActivity CreateSdkPersonalMessage(string conversationId, string activityId) => new("request input")
    {
        Id = activityId,
        From = new TeamsAccount { Id = "user-a" },
        Conversation = new TeamsConversation
        {
            Id = conversationId,
            TenantId = "tenant-a",
            Type = TeamsConversationType.Personal
        },
        ServiceUrl = "https://request-service.invalid/"
    };

    private static MessageActivity CreateSdkChannelRootMessage(string conversationId, string rootActivityId) => new("<at>bot</at> request input")
    {
        Id = rootActivityId,
        From = new TeamsAccount { Id = "user-a" },
        Recipient = new TeamsAccount { Id = "28:bot" },
        Conversation = new TeamsConversation
        {
            Id = conversationId,
            TenantId = "tenant-a",
            Type = TeamsConversationType.Channel
        },
        ChannelData = new TeamsChannelData
        {
            Team = new TeamsTeam { Id = "team-a" },
            Channel = new TeamsChannel { Id = "channel-a" }
        },
        Entities = [new MentionEntity
        {
            Type = "mention",
            Mentioned = new TeamsAccount { Id = "28:bot" },
            Text = "<at>bot</at>"
        }],
        ServiceUrl = "https://request-service.invalid/"
    };

    private static TeamsConversationDependencies CreateDependencies(
        ISessionPipeline pipeline,
        bool allowDirectMessages = true,
        string[]? allowedUserIds = null,
        string tenantId = "tenant-a",
        bool enabled = false,
        ITeamsReplyClient? replyClient = null) => new(
        new TeamsChannelOptions
        {
            Enabled = enabled,
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

    private sealed class RequestLifetimeProbe
    {
        private int _created;
        private int _disposed;

        public bool AllDisposed => Volatile.Read(ref _created) > 0
            && Volatile.Read(ref _created) == Volatile.Read(ref _disposed);

        public void Created() => Interlocked.Increment(ref _created);

        public void Disposed() => Interlocked.Increment(ref _disposed);
    }

    private sealed class RequestScopeSentinel : IDisposable
    {
        private readonly RequestLifetimeProbe _probe;

        public RequestScopeSentinel(RequestLifetimeProbe probe)
        {
            _probe = probe;
            _probe.Created();
        }

        public void Dispose() => _probe.Disposed();
    }

    private sealed class RequestIndependentReplyClient(RequestLifetimeProbe requestLifetime) : ITeamsReplyClient
    {
        public List<TeamsOutboundMessage> Messages { get; } = [];

        public Task<TeamsDeliveryResult> DeliverAsync(TeamsOutboundMessage message, CancellationToken cancellationToken = default)
        {
            if (!requestLifetime.AllDisposed)
                throw new ObjectDisposedException("request_scope", "The reply client must not run during the inbound request.");

            Messages.Add(message);
            return Task.FromResult(new TeamsDeliveryResult(TeamsDeliveryStatus.Delivered, "request-independent-activity"));
        }
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

    private static TeamsInboundActivity CreateActivity(
        string activityId,
        string tenantId,
        string conversationId,
        string serviceUrl = "https://service.invalid/") => new(
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
        new TeamsReplyMetadata(null, null, serviceUrl));

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
        string promptActivityId) => new(
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
        "approve",
        null,
        null,
        null,
        promptActivityId,
        "https://service.invalid/");

    private static DeliverTrustedSessionTurn CreateReminder(
        SessionId sessionId,
        string deliveryKey,
        IActorRef? observer = null) => new(
        sessionId,
        "Run the scheduled reminder.",
        new MessageSource
        {
            ChannelType = ChannelType.Teams,
            SenderId = new SenderId("reminder-system"),
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            Principal = PrincipalClassification.VerifiedAutomation,
            Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
            {
                SourceKind = new SourceKind("reminder")
            },
            ReceivedAt = TimeProvider.System.GetUtcNow(),
            ReminderId = new ReminderId(deliveryKey),
            DeliveryObserver = observer
        });

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

    private sealed class StopOnRecoveryFailureParent : ReceiveActor
    {
        private readonly Props _childProps;
        private readonly IActorRef _observer;

        public StopOnRecoveryFailureParent(Props childProps, IActorRef observer)
        {
            _childProps = childProps;
            _observer = observer;
        }

        protected override void PreStart()
        {
            _observer.Tell(Context.ActorOf(_childProps, "binding"));
            base.PreStart();
        }

        protected override SupervisorStrategy SupervisorStrategy() =>
            new OneForOneStrategy(_ => Directive.Stop, loggingEnabled: false);
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
