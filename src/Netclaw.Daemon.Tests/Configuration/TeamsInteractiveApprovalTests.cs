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
    public void Pending_card_has_only_opaque_action_data_and_safe_display_text()
    {
        const string sensitiveArguments = "Authorization: synthetic-token --raw-arguments";
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("teams~dGVuYW50~personal~Y29udmVyc2F0aW9u/conversation"),
            Kind = "approval",
            CallId = new ToolCallId("call-1"),
            ToolName = new ToolName("execute_shell"),
            DisplayText = sensitiveArguments,
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, "Once"),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, "Deny")
            ]
        };

        var card = TeamsApprovalCardRenderer.CreatePending(request, "correlation_123", "nonce_123");
        var serialized = JsonSerializer.Serialize(card);

        Assert.Equal("Approval required", card.Title);
        Assert.Equal(["approve", "deny"], card.Actions.Select(action => action.Action));
        Assert.All(card.Actions, action =>
        {
            Assert.Equal("correlation_123", action.CorrelationId);
            Assert.Equal("nonce_123", action.Nonce);
        });
        Assert.DoesNotContain(sensitiveArguments, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", serialized, StringComparison.Ordinal);
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
    [InlineData("approve_once", false)]
    [InlineData("Approve", false)]
    [InlineData("", false)]
    public void Approval_action_accepts_only_the_teams_card_actions(string action, bool expected)
    {
        Assert.Equal(expected, TeamsApprovalAction.IsSupportedAction(action));
    }
}
