// -----------------------------------------------------------------------
// <copyright file="TeamsTenantEvidenceFixtureTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Channels.Teams;
using Xunit;

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

    private static JsonNode Load(string name)
        => JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, name)))!;

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Teams", "TenantEvidence");
}
