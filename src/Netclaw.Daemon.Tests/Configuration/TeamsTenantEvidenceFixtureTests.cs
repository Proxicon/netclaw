// -----------------------------------------------------------------------
// <copyright file="TeamsTenantEvidenceFixtureTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Teams.Api;
using Microsoft.Teams.Api.Activities;
using Microsoft.Teams.Api.Entities;
using Netclaw.Channels.Teams;
using Netclaw.Daemon.Configuration;
using Xunit;
using TeamsAccount = Microsoft.Teams.Api.Account;
using TeamsAttachment = Microsoft.Teams.Api.Attachment;
using TeamsChannel = Microsoft.Teams.Api.Channel;
using TeamsChannelData = Microsoft.Teams.Api.ChannelData;
using TeamsConversation = Microsoft.Teams.Api.Conversation;
using TeamsConversationType = Microsoft.Teams.Api.ConversationType;
using TeamsTeam = Microsoft.Teams.Api.Team;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// Offline guards for the documented Phase 0.2 transport observations. They
/// intentionally exercise only fixtures and pure evidence mappings, not PR 4.
/// </summary>
public sealed class TeamsTenantEvidenceFixtureTests
{
    [Fact]
    public void Channel_thread_fixtures_derive_canonical_roots_without_reply_to_id()
    {
        var root = Load("channel-root-message.json");
        var reply = Load("channel-reply-message.json");
        var secondRoot = Load("channel-second-root-message.json");

        Assert.True(TeamsTenantEvidenceMappings.TryGetCanonicalChannelRootActivityId(ConversationId(root), out var rootId));
        Assert.True(TeamsTenantEvidenceMappings.TryGetCanonicalChannelRootActivityId(ConversationId(reply), out var replyRootId));
        Assert.True(TeamsTenantEvidenceMappings.TryGetCanonicalChannelRootActivityId(ConversationId(secondRoot), out var secondRootId));
        Assert.Equal(root["id"]!.GetValue<string>(), rootId);
        Assert.Equal(rootId, replyRootId);
        Assert.NotEqual(rootId, secondRootId);
        Assert.Null(reply["replyToId"]);
    }

    [Theory]
    [InlineData("CONVERSATION_ROOT_TEST_001")]
    [InlineData(";messageid=ACTIVITY_ROOT_TEST_001")]
    [InlineData("CONVERSATION_ROOT_TEST_001;messageid=")]
    [InlineData("CONVERSATION_ROOT_TEST_001;messageid=ACTIVITY_ROOT_TEST_001;extra=value")]
    [InlineData("CONVERSATION_ROOT_TEST_001;messageid=A;messageid=B")]
    public void Missing_or_malformed_message_id_suffix_fails_closed(string conversationId)
    {
        Assert.False(TeamsTenantEvidenceMappings.TryGetCanonicalChannelRootActivityId(conversationId, out _));
    }

    [Fact]
    public void Qualified_bot_mentions_are_removed_by_entity_span_while_user_mentions_remain()
    {
        var single = Load("single-bot-mention.json");
        var mixed = Load("bot-plus-user-mention.json");
        var duplicate = Load("double-bot-mention.json");

        Assert.Equal(" harmless probe", StripBotMentions(single));
        Assert.Equal(" @synthetic-user harmless probe", StripBotMentions(mixed));
        Assert.Equal("  harmless probe", StripBotMentions(duplicate));
    }

    [Fact]
    public void Update_and_delete_fixtures_retain_activity_root_index_identity()
    {
        var update = Load("message-update.json");
        var delete = Load("message-delete.json");

        Assert.Equal("messageUpdate", update["type"]!.GetValue<string>());
        Assert.Equal("messageDelete", delete["type"]!.GetValue<string>());
        Assert.Equal(update["id"]!.GetValue<string>(), delete["id"]!.GetValue<string>());
        Assert.Equal(ConversationId(update), ConversationId(delete));
        Assert.Null(update["replyToId"]);
        Assert.Null(delete["replyToId"]);
        Assert.Null(delete["text"]);
    }

    [Fact]
    public void Tenant_upload_shell_is_classified_as_graph_backed_before_routing()
    {
        var attachment = Load("attachment-shell.json")["attachments"]![0]!;

        var result = TeamsTenantEvidenceMappings.ClassifyAttachment(new TeamsAttachmentEvidence(
            attachment["contentType"]!.GetValue<string>(),
            HasName: false,
            ContentUrl: null,
            HasContentUrl: false,
            ContentKind: TeamsAttachmentContentKind.EmptyText));

        Assert.Equal(TeamsAttachmentClassification.GraphBackedUnsupported, result.Classification);
        Assert.Equal("graph_backed_attachment_unsupported", result.ReasonCode);
    }

    [Fact]
    public void Channel_root_formatted_wrapper_with_a_charset_parameter_is_inline_rendering_metadata()
    {
        var attachment = Load("channel-root-formatted-wrapper.json")["attachments"]![0]!;

        var result = TeamsTenantEvidenceMappings.ClassifyAttachment(new TeamsAttachmentEvidence(
            attachment["contentType"]!.GetValue<string>(),
            HasName: false,
            ContentUrl: null,
            HasContentUrl: false,
            ContentKind: TeamsAttachmentContentKind.NonEmptyText));

        Assert.Equal(TeamsAttachmentClassification.InlineTextRendering, result.Classification);
        Assert.Null(result.ReasonCode);
    }

    [Theory]
    [InlineData("personal-message.json", TeamsConversationScope.Personal, false)]
    [InlineData("channel-root-message.json", TeamsConversationScope.Channel, false)]
    [InlineData("channel-reply-message.json", TeamsConversationScope.Channel, false)]
    [InlineData("channel-second-root-message.json", TeamsConversationScope.Channel, false)]
    [InlineData("channel-root-formatted-wrapper.json", TeamsConversationScope.Channel, true)]
    public void Complete_message_fixtures_translate_with_the_expected_scope_root_and_mention(
        string fixtureName,
        TeamsConversationScope expectedScope,
        bool expectedBotMention)
    {
        var fixture = Load(fixtureName);
        var translator = CreateTranslator();

        var result = translator.Translate(CreateSdkMessage(fixture), "TENANT_TEST_001");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.Equal(expectedScope, result.Activity!.Trust.Scope);
        Assert.Equal(expectedBotMention, result.Activity.IsMentioned);
        Assert.Empty(result.Activity.Attachments);
        if (expectedScope == TeamsConversationScope.Channel)
        {
            Assert.True(TeamsTenantEvidenceMappings.TryGetCanonicalChannelRootActivityId(ConversationId(fixture), out var rootActivityId));
            Assert.Equal(rootActivityId, result.Activity.Reply!.RootActivityId);
        }
    }

    [Theory]
    [InlineData("single-bot-mention.json", " harmless probe")]
    [InlineData("bot-plus-user-mention.json", " @synthetic-user harmless probe")]
    [InlineData("double-bot-mention.json", "  harmless probe")]
    public void Mention_fixtures_recognize_only_the_configured_bot_and_preserve_canonical_text(
        string fixtureName,
        string expectedText)
    {
        var result = CreateTranslator().Translate(CreateSdkMessage(Load(fixtureName), TeamsConversationScope.Channel), "TENANT_TEST_001");

        Assert.Equal(TeamsTranslationDisposition.Accepted, result.Disposition);
        Assert.True(result.Activity!.IsMentioned);
        Assert.Equal(expectedText, result.Activity.Text);
    }

    [Fact]
    public void Tenant_evidence_directory_requires_an_explicit_fixture_matrix_entry()
    {
        var expected = new[]
        {
            "adaptive-card-action-execute.json",
            "attachment-shell.json",
            "bot-message-update-address.json",
            "bot-plus-user-mention.json",
            "channel-reply-message.json",
            "channel-root-formatted-wrapper.json",
            "channel-root-message.json",
            "channel-second-root-message.json",
            "double-bot-mention.json",
            "message-delete.json",
            "message-update.json",
            "personal-message.json",
            "proactive-destination.json",
            "single-bot-mention.json"
        };

        var actual = Directory.EnumerateFiles(FixtureDirectory, "*.json")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Action_execute_and_outbound_address_fixtures_preserve_only_required_non_secret_fields()
    {
        var invoke = Load("adaptive-card-action-execute.json");
        var proactive = Load("proactive-destination.json");
        var update = Load("bot-message-update-address.json");

        Assert.Equal("Action.Execute", invoke["value"]!["action"]!["type"]!.GetValue<string>());
        Assert.Equal("tenant-evidence-confirm", invoke["value"]!["action"]!["verb"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(invoke["replyToId"]!.GetValue<string>()));
        Assert.Equal("channel", proactive["destination"]!["scope"]!.GetValue<string>());
        Assert.False(proactive["service"]!["persistsAccessToken"]!.GetValue<bool>());
        Assert.Equal("ACTIVITY_BOT_CREATED_TEST_001", update["createdActivityId"]!.GetValue<string>());
        Assert.Equal(["CreateAsync", "UpdateAsync"], update["sdkMethods"]!.AsArray().Select(value => value!.GetValue<string>()));
    }

    [Fact]
    public void All_fixtures_are_explicitly_sanitized_and_contain_no_sensitive_field_names()
    {
        foreach (var path in Directory.EnumerateFiles(FixtureDirectory, "*.json"))
        {
            var text = File.ReadAllText(path);
            Assert.Contains("sanitized tenant-backed evidence", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ClientSecret", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Authorization", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer", text, StringComparison.Ordinal);
        }
    }

    private static string StripBotMentions(JsonNode fixture)
    {
        var recipientId = fixture["recipient"]!["id"]!.GetValue<string>();
        var entities = fixture["entities"]!.AsArray().Select(entity => new TeamsMentionEvidence(
            entity!["type"]!.GetValue<string>(),
            entity["mentioned"]!["id"]!.GetValue<string>(),
            entity["text"]!.GetValue<string>()));
        return TeamsTenantEvidenceMappings.RemoveQualifiedBotMentions(
            fixture["text"]!.GetValue<string>(),
            entities,
            recipientId,
            "BOT_TEST_001");
    }

    private static string ConversationId(JsonNode fixture) => fixture["conversation"]!["id"]!.GetValue<string>();

    private static TeamsSdkActivityTranslator CreateTranslator() => new(new TeamsChannelOptions
    {
        TenantId = "TENANT_TEST_001",
        BotId = "BOT_TEST_001"
    }, TimeProvider.System);

    private static MessageActivity CreateSdkMessage(JsonNode fixture, TeamsConversationScope? fallbackScope = null)
    {
        var conversation = fixture["conversation"];
        var scope = conversation?["conversationType"]?.GetValue<string>() switch
        {
            "channel" => TeamsConversationScope.Channel,
            "personal" => TeamsConversationScope.Personal,
            _ => fallbackScope ?? TeamsConversationScope.Personal
        };
        var activityId = fixture["id"]?.GetValue<string>() ?? "ACTIVITY_DEFAULT_TEST_001";
        var conversationId = conversation?["id"]?.GetValue<string>()
            ?? (scope == TeamsConversationScope.Channel
                ? $"CONVERSATION_DEFAULT_TEST_001;messageid={activityId}"
                : "CONVERSATION_DEFAULT_TEST_001");
        var activity = new MessageActivity(fixture["text"]?.GetValue<string>() ?? "harmless synthetic text")
        {
            Id = activityId,
            From = new TeamsAccount { Id = fixture["from"]?["id"]?.GetValue<string>() ?? "USER_TEST_001" },
            Recipient = new TeamsAccount { Id = fixture["recipient"]?["id"]?.GetValue<string>() ?? "28:BOT_TEST_001" },
            ServiceUrl = "https://service.invalid/",
            Conversation = new TeamsConversation
            {
                Id = conversationId,
                TenantId = conversation?["tenantId"]?.GetValue<string>() ?? "TENANT_TEST_001",
                Type = scope == TeamsConversationScope.Channel ? TeamsConversationType.Channel : TeamsConversationType.Personal
            }
        };

        if (scope == TeamsConversationScope.Channel)
        {
            activity.ChannelData = new TeamsChannelData
            {
                Team = new TeamsTeam { Id = fixture["channelData"]?["team"]?["id"]?.GetValue<string>() ?? "TEAM_TEST_001" },
                Channel = new TeamsChannel { Id = fixture["channelData"]?["channel"]?["id"]?.GetValue<string>() ?? "CHANNEL_TEST_001" }
            };
        }

        if (fixture["entities"] is JsonArray entities)
        {
            activity.Entities = entities
                .Select(entity => new MentionEntity
                {
                    Type = entity!["type"]!.GetValue<string>(),
                    Text = entity["text"]!.GetValue<string>(),
                    Mentioned = new TeamsAccount { Id = entity["mentioned"]!["id"]!.GetValue<string>() }
                })
                .Cast<IEntity>()
                .ToArray();
        }

        if (fixture["attachments"] is JsonArray attachments)
        {
            activity.Attachments = attachments.Select(attachment =>
            {
                object? content = attachment!["content"] is { } node
                    ? JsonSerializer.Deserialize<JsonElement>(node.ToJsonString()).Clone()
                    : null;
                return new TeamsAttachment(attachment["contentType"]!.GetValue<string>(), content)
                {
                    Name = attachment["name"]?.GetValue<string>(),
                    ContentUrl = attachment["contentUrl"]?.GetValue<string>()
                };
            }).ToArray();
        }

        return activity;
    }

    private static JsonNode Load(string name)
        => JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, name)))!;

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Teams", "TenantEvidence");
}
