// -----------------------------------------------------------------------
// <copyright file="TeamsInteractiveApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Teams;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class TeamsInteractiveApprovalTests
{
    [Fact]
    public void Pending_card_preserves_the_session_option_order_labels_and_safe_context()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("teams~dGVuYW50~personal~Y29udmVyc2F0aW9u/conversation"),
            Kind = "approval",
            CallId = new ToolCallId("call-1"),
            ToolName = new ToolName("execute_shell"),
            DisplayText = "git push origin main",
            CandidateVerbs = ["git push", "git status"],
            Cwd = "/work/netclaw",
            HasAdoptedContext = true,
            AdoptedSpeakerIds = ["user-a", "user-b"],
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhereKey, ApprovalOptionKeys.ApproveEverywhereLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };

        var card = TeamsApprovalCardRenderer.CreatePending(request, "correlation_123", "nonce_123");

        Assert.Equal("Tool approval required", card.Title);
        Assert.Equal(request.Options.Select(option => option.Key.Value), card.Actions.Select(action => action.Action));
        Assert.Equal(request.Options.Select(option => option.Label), card.Actions.Select(action => action.Title));
        Assert.Equal(
            [
                TeamsApprovalActionStyle.Positive,
                TeamsApprovalActionStyle.Default,
                TeamsApprovalActionStyle.Default,
                TeamsApprovalActionStyle.Destructive,
                TeamsApprovalActionStyle.Destructive
            ],
            card.Actions.Select(action => action.Style));
        Assert.Contains("Tool: execute_shell", card.Body, StringComparison.Ordinal);
        Assert.Contains("Request: git push origin main", card.Body, StringComparison.Ordinal);
        Assert.Contains("Candidates: git push, git status", card.Body, StringComparison.Ordinal);
        Assert.Contains("Working directory: /work/netclaw", card.Body, StringComparison.Ordinal);
        Assert.Contains("Adopted context: present.", card.Body, StringComparison.Ordinal);
        Assert.Contains("Speakers: user-a, user-b", card.Body, StringComparison.Ordinal);
        Assert.All(card.Actions, action =>
        {
            Assert.Equal("correlation_123", action.CorrelationId);
            Assert.Equal("nonce_123", action.Nonce);
            var serializedAction = JsonSerializer.Serialize(action);
            Assert.DoesNotContain(request.CallId.Value, serializedAction, StringComparison.Ordinal);
            Assert.DoesNotContain(request.DisplayText, serializedAction, StringComparison.Ordinal);
            Assert.DoesNotContain(request.ToolName.Value, serializedAction, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Pending_card_uses_the_supplied_mcp_scope_label()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("teams~dGVuYW50~personal~Y29udmVyc2F0aW9u/conversation"),
            Kind = "approval",
            CallId = new ToolCallId("call-mcp"),
            ToolName = new ToolName("filesystem/read_file"),
            DisplayText = "Read README.md",
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhereKey, ApprovalOptionKeys.ApproveMcpToolLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };

        var card = TeamsApprovalCardRenderer.CreatePending(request, "correlation_123", "nonce_123");

        Assert.Equal("MCP tool approval required", card.Title);
        Assert.Equal(
            [ApprovalOptionKeys.ApproveOnceLabel, ApprovalOptionKeys.ApproveMcpToolLabel, ApprovalOptionKeys.DenyLabel],
            card.Actions.Select(action => action.Title));
        Assert.Equal(
            [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveEverywhere, ApprovalOptionKeys.Deny],
            TeamsApprovalCardRenderer.GetOfferedOptionKeys(request));
        Assert.Equal(
            [
                TeamsApprovalActionStyle.Positive,
                TeamsApprovalActionStyle.Destructive,
                TeamsApprovalActionStyle.Destructive
            ],
            card.Actions.Select(action => action.Style));
    }

    [Fact]
    public void Pending_card_rejects_duplicate_or_invalid_option_keys()
    {
        var duplicate = CreateRequest(
            new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
            new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, "Once again"));
        var invalid = CreateRequest(new ToolInteractionOption(new ApprovalOptionKey("approve now"), "Approve"));

        Assert.Throws<ArgumentException>(() => TeamsApprovalCardRenderer.CreatePending(duplicate, "correlation_123", "nonce_123"));
        Assert.Throws<ArgumentException>(() => TeamsApprovalCardRenderer.CreatePending(invalid, "correlation_123", "nonce_123"));
    }

    [Fact]
    public void Terminal_denial_card_keeps_the_tool_context_and_removes_actions()
    {
        var card = TeamsApprovalCardRenderer.CreateTerminal(
            "shell_execute",
            "rmdir netclaw-approval-card-never-created",
            "Denied.");

        Assert.Equal("Approval denied", card.Title);
        Assert.Equal(TeamsApprovalCardTone.Attention, card.Tone);
        Assert.Empty(card.Actions);
        Assert.Contains("Tool: shell_execute", card.Body, StringComparison.Ordinal);
        Assert.Contains("Action: rmdir netclaw-approval-card-never-created", card.Body, StringComparison.Ordinal);
        Assert.EndsWith("Denied.", card.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Nonce_hash_requires_an_exact_bounded_value()
    {
        const string nonce = "opaque_nonce_123";
        var hash = TeamsApprovalCardRenderer.HashNonce(nonce);

        Assert.True(TeamsApprovalCardRenderer.NonceMatches(hash, nonce));
        Assert.False(TeamsApprovalCardRenderer.NonceMatches(hash, "opaque_nonce_12"));
        Assert.False(TeamsApprovalCardRenderer.NonceMatches(hash, nonce + "!"));
        Assert.False(TeamsApprovalCardRenderer.NonceMatches(hash, new string('x', TeamsApprovalAction.MaxNonceLength + 1)));
        Assert.False(TeamsApprovalCardRenderer.NonceMatches("not-a-hash", nonce));
    }

    [Theory]
    [InlineData("approve", true)]
    [InlineData("deny", true)]
    [InlineData("approve_once", true)]
    [InlineData("approve_session", true)]
    [InlineData("approve_always", true)]
    [InlineData("approve_everywhere", true)]
    [InlineData("Approve", false)]
    [InlineData("approve now", false)]
    [InlineData("", false)]
    public void Approval_action_accepts_only_a_bounded_canonical_key_shape(string action, bool expected)
    {
        Assert.Equal(expected, TeamsApprovalAction.IsSupportedAction(action));
    }

    private static ToolInteractionRequest CreateRequest(params ToolInteractionOption[] options) => new()
    {
        SessionId = new SessionId("teams~dGVuYW50~personal~Y29udmVyc2F0aW9u/conversation"),
        Kind = "approval",
        CallId = new ToolCallId("call-invalid"),
        ToolName = new ToolName("execute_shell"),
        DisplayText = "git status",
        Options = options
    };
}
