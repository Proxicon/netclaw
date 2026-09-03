// -----------------------------------------------------------------------
// <copyright file="LlmFacingToolName.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Netclaw.Tools;

/// <summary>
/// The form of a tool name that is safe to surface to an LLM provider's
/// tool-use API. Anthropic enforces <c>^[a-zA-Z0-9_-]{1,128}$</c> on tool
/// names; OpenAI and others are at least as permissive. This type exists
/// to keep that constraint at a type boundary instead of relying on
/// every emit site to remember which form is required.
/// </summary>
/// <remarks>
/// Inside Netclaw, tool identity is always the canonical
/// <see cref="ToolName"/> (e.g. <c>notion/notion-create-pages</c> for
/// MCP, <c>shell_execute</c> for first-party). The LLM-facing alias preserves
/// the established MCP slash replacement and encodes other disallowed names.
/// It is surfaced only at the two LLM boundaries:
/// when emitting tool definitions, and when echoing tool result messages
/// back. Internal code (audit logs, approvals, prompts, CLI) should
/// keep working in canonical <see cref="ToolName"/> values.
/// </remarks>
public readonly record struct LlmFacingToolName
{
    private static readonly Regex AnthropicSafeName =
        new("^[a-zA-Z0-9_-]{1,128}$", RegexOptions.Compiled);
    private const string EncodedPrefix = "nc_";
    private const string HashedPrefix = "nc_hash_";

    private LlmFacingToolName(string value) => Value = value;

    /// <summary>The LLM-facing string. Guaranteed to satisfy the
    /// Anthropic tool-name regex.</summary>
    public string Value { get; }

    /// <summary>
    /// Produce a deterministic provider-safe alias. Already-safe canonical
    /// names keep their established wire value. Canonical MCP names preserve
    /// their existing slash replacement. Other names use reversible Base64Url
    /// UTF-8 encoding when it fits the provider limit. An oversized name uses
    /// a deterministic hash and remains correlated by the registry.
    /// </summary>
    public static LlmFacingToolName FromCanonical(string canonical)
    {
        ArgumentException.ThrowIfNullOrEmpty(canonical);

        if (IsProviderSafe(canonical))
            return new LlmFacingToolName(canonical);

        var slashSanitized = canonical.Replace("/", "__", StringComparison.Ordinal);
        if (IsProviderSafe(slashSanitized))
            return new LlmFacingToolName(slashSanitized);

        var encoded = EncodedPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(canonical))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        if (IsProviderSafe(encoded))
            return new LlmFacingToolName(encoded);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var compact = HashedPrefix + hash;
        return new LlmFacingToolName(compact);
    }

    /// <summary>
    /// Returns a safe name for every provider-visible tool call, including
    /// malformed historical records that cannot be dispatched any more.
    /// </summary>
    public static string ForProvider(string? canonical) =>
        string.IsNullOrWhiteSpace(canonical) ? "nc_empty" : FromCanonical(canonical).Value;

    public static bool IsProviderSafe(string? name) =>
        name is not null && AnthropicSafeName.IsMatch(name);

    public override string ToString() => Value;

    /// <summary>
    /// Pure-string reversal of the LLM-facing alias to a canonical
    /// candidate, used by surfaces that don't have a
    /// <c>ToolRegistry</c> handy (config loader, doctor checks, CLI
    /// against the on-disk approval store). Returns the canonical
    /// candidate if <paramref name="name"/> matches the
    /// <c>{server}__{tool}</c> shape; returns <c>null</c> if the input
    /// already looks canonical (contains <c>/</c>) or doesn't contain a
    /// <c>__</c> separator. First-party tool names use single
    /// underscores by convention, so a name with <c>__</c> in it is
    /// reliably an MCP alias.
    /// </summary>
    /// <remarks>
    /// Heuristic, not authoritative: a tool literally named
    /// <c>foo__bar</c> (no MCP server prefix) would be misread as
    /// <c>foo/bar</c>. The convention is enforced by code review on
    /// new first-party tools; if a future tool legitimately needs
    /// <c>__</c> in its name, this method needs to grow a registry-
    /// aware overload.
    /// </remarks>
    public static string? TryReverseSanitizedToCanonical(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        if (name.StartsWith(EncodedPrefix, StringComparison.Ordinal)
            && TryDecodeBase64Url(name[EncodedPrefix.Length..], out var decoded)
            && decoded is not null
            && !IsProviderSafe(decoded)
            && string.Equals(FromCanonical(decoded).Value, name, StringComparison.Ordinal))
        {
            return decoded;
        }

        if (name.Contains('/', StringComparison.Ordinal))
            return null;
        var idx = name.IndexOf("__", StringComparison.Ordinal);
        if (idx <= 0 || idx + 2 >= name.Length)
            return null;
        return string.Concat(name.AsSpan(0, idx), "/", name.AsSpan(idx + 2));
    }

    private static bool TryDecodeBase64Url(string encoded, out string? value)
    {
        value = null;
        if (string.IsNullOrEmpty(encoded))
            return false;

        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
            value = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
