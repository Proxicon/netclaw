// -----------------------------------------------------------------------
// <copyright file="TeamsInteractiveApprovals.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Netclaw.Actors.Protocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels.Teams;

/// <summary>
/// Builds the SDK-free Adaptive Card contract for Teams tool approvals.
/// Descriptive request fields never become callback authorization data.
/// </summary>
public static class TeamsApprovalCardRenderer
{
    public static TeamsApprovalCard CreatePending(
        ToolInteractionRequest request,
        string correlationId,
        string nonce)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TeamsApprovalAction.IsBoundedOpaqueValue(correlationId, TeamsApprovalAction.MaxCorrelationLength))
            throw new ArgumentException("The approval correlation must be bounded and opaque.", nameof(correlationId));
        if (!TeamsApprovalAction.IsBoundedOpaqueValue(nonce, TeamsApprovalAction.MaxNonceLength))
            throw new ArgumentException("The approval nonce must be bounded and opaque.", nameof(nonce));

        var toolName = Truncate(request.ToolName.Value, 128);
        var card = new TeamsApprovalCard(
            "Approval required",
            $"Netclaw needs approval to use {toolName}.",
            [
                new TeamsApprovalCardAction("Approve", "approve", correlationId, nonce),
                new TeamsApprovalCardAction("Deny", "deny", correlationId, nonce)
            ]);
        EnsureBounded(card);
        return card;
    }

    public static TeamsApprovalCard CreateTerminal(string message)
    {
        var card = new TeamsApprovalCard("Approval", Truncate(message, 512), []);
        EnsureBounded(card);
        return card;
    }

    public static string CreateNonce()
        => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string CreateCorrelationId()
        => Base64Url(RandomNumberGenerator.GetBytes(24));

    public static string HashNonce(string nonce)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)));
    }

    public static bool NonceMatches(string expectedHash, string nonce)
    {
        if (string.IsNullOrWhiteSpace(expectedHash)
            || !TeamsApprovalAction.IsBoundedOpaqueValue(nonce, TeamsApprovalAction.MaxNonceLength))
        {
            return false;
        }

        var candidate = SHA256.HashData(Encoding.UTF8.GetBytes(nonce));
        try
        {
            var expected = Convert.FromHexString(expectedHash);
            return expected.Length == candidate.Length
                && CryptographicOperations.FixedTimeEquals(expected, candidate);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void EnsureBounded(TeamsApprovalCard card)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(card).Length > TeamsApprovalCard.MaxSerializedBytes)
            throw new InvalidOperationException("The Teams approval card exceeds the outbound payload limit.");
    }

    private static string Truncate(string value, int maximumLength)
        => value.Length <= maximumLength ? value : value[..maximumLength];

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
