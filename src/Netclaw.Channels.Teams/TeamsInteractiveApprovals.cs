// -----------------------------------------------------------------------
// <copyright file="TeamsInteractiveApprovals.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels.Teams;

/// <summary>
/// Builds the SDK-free Adaptive Card contract for Teams tool approvals.
/// Descriptive request fields never become callback authorization data.
/// </summary>
public static class TeamsApprovalCardRenderer
{
    internal const int MaxOptionCount = 16;
    internal const int MaxOptionLabelLength = 128;
    internal const int MaxToolNameChars = 128;
    internal const int MaxRequestDisplayChars = 2_048;
    internal const int MaxSummaryChars = 512;

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

        ValidateOptions(request.Options);
        var card = new TeamsApprovalCard(
            request.ToolName.IsMcp ? "MCP tool approval required" : "Tool approval required",
            BuildPendingBody(request),
            request.Options
                .Select(option => new TeamsApprovalCardAction(
                    option.Label,
                    option.Key.Value,
                    correlationId,
                    nonce,
                    GetActionStyle(option.Key.Value)))
                .ToArray(),
            TeamsApprovalCardTone.Warning);
        EnsureBounded(card);
        return card;
    }

    public static IReadOnlyList<string> GetOfferedOptionKeys(ToolInteractionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateOptions(request.Options);
        return request.Options.Select(static option => option.Key.Value).ToArray();
    }

    public static TeamsApprovalCard CreateTerminal(string message)
        => CreateTerminal(string.Empty, string.Empty, message);

    public static TeamsApprovalCard CreateTerminal(
        string toolName,
        string requestDisplayText,
        string message,
        bool isMcpTool = false)
    {
        var (title, tone) = message switch
        {
            "Denied." => ("Approval denied", TeamsApprovalCardTone.Attention),
            "This approval has expired." => ("Approval expired", TeamsApprovalCardTone.Warning),
            "This approval was already processed." => ("Approval already resolved", TeamsApprovalCardTone.Warning),
            "This approval is no longer available." => ("Approval unavailable", TeamsApprovalCardTone.Warning),
            _ when message.StartsWith("Approved:", StringComparison.Ordinal) => ("Approval granted", TeamsApprovalCardTone.Good),
            _ => ("Approval resolved", TeamsApprovalCardTone.Default)
        };
        var requestLabel = isMcpTool ? "Invocation" : "Action";
        var body = string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(requestDisplayText)
            ? Truncate(message, MaxSummaryChars)
            : $"Tool: {Truncate(toolName, MaxToolNameChars)}\n{requestLabel}: {Truncate(requestDisplayText, MaxRequestDisplayChars)}\n\n{Truncate(message, MaxSummaryChars)}";
        var card = new TeamsApprovalCard(title, body, [], tone);
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

    private static string BuildPendingBody(ToolInteractionRequest request)
    {
        var requestLabel = request.ToolName.IsMcp ? "Invocation" : "Request";
        var lines = new List<string>
        {
            $"Tool: {ApprovalDisplayTextFormatter.Truncate(request.ToolName.Value, MaxToolNameChars)}",
            $"{requestLabel}: {ApprovalDisplayTextFormatter.Truncate(request.DisplayText, MaxRequestDisplayChars)}"
        };

        var candidates = request.CandidateVerbs.Count > 0 ? request.CandidateVerbs : request.Patterns;
        if (candidates.Count > 0)
            lines.Add($"Candidates: {ApprovalDisplayTextFormatter.Truncate(string.Join(", ", candidates), MaxSummaryChars)}");
        if (!string.IsNullOrWhiteSpace(request.Cwd))
            lines.Add($"Working directory: {ApprovalDisplayTextFormatter.Truncate(request.Cwd, MaxSummaryChars)}");
        if (request.IsMessy)
            lines.Add("Reusable approval is unavailable for this request.");
        if (request.HasAdoptedContext)
        {
            lines.Add("Adopted context: present.");
            if (request.AdoptedSpeakerIds.Count > 0)
            {
                lines.Add(
                    $"Speakers: {ApprovalDisplayTextFormatter.Truncate(string.Join(", ", request.AdoptedSpeakerIds), MaxSummaryChars)}");
            }
        }

        return string.Join("\n", lines);
    }

    private static void ValidateOptions(IReadOnlyList<ToolInteractionOption> options)
    {
        if (options.Count is 0 or > MaxOptionCount)
            throw new ArgumentException("The approval option count is invalid.", nameof(options));

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options)
        {
            if (!TeamsApprovalAction.IsSupportedAction(option.Key.Value)
                || !keys.Add(option.Key.Value)
                || string.IsNullOrWhiteSpace(option.Label)
                || option.Label.Length > MaxOptionLabelLength
                || option.Label.Any(char.IsControl))
            {
                throw new ArgumentException("The approval options are invalid.", nameof(options));
            }
        }
    }

    private static TeamsApprovalActionStyle GetActionStyle(string optionKey)
    {
        if (ApprovalOptionKeys.IsDangerStyled(optionKey))
            return TeamsApprovalActionStyle.Destructive;

        return optionKey == ApprovalOptionKeys.ApproveOnce
            ? TeamsApprovalActionStyle.Positive
            : TeamsApprovalActionStyle.Default;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
