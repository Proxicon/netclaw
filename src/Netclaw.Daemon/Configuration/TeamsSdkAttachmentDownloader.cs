// -----------------------------------------------------------------------
// <copyright file="TeamsSdkAttachmentDownloader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Schema;
using Netclaw.Channels;
using Netclaw.Channels.Teams;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Keeps short-lived SDK attachment URLs at the authenticated HTTP boundary.
/// Actors receive only a downloader interface and sanitized attachment metadata.
/// </summary>
internal sealed class TeamsSdkAttachmentDownloader(
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider) : ITeamsAttachmentDownloader
{
    private const int MaximumCaptures = 1_024;
    private const int MaximumUrlLength = 4_096;
    private static readonly TimeSpan CaptureLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<CaptureKey, CapturedActivity> _captures = new();

    internal void Capture(MessageActivity source, TeamsInboundActivity activity)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(activity);
        if (activity.Attachments.Length == 0 || source.Attachments is null)
            return;

        RemoveExpiredCaptures();
        var captured = new ConcurrentDictionary<int, CapturedAttachment>();
        foreach (var attachment in activity.Attachments)
        {
            if (attachment.Kind == TeamsInboundAttachmentKind.Unknown
                || attachment.SourceIndex < 0
                || attachment.SourceIndex >= source.Attachments.Count)
            {
                continue;
            }

            var sdkAttachment = source.Attachments[attachment.SourceIndex];
            if (sdkAttachment is not null
                && TryGetDownloadUrl(sdkAttachment, attachment.Kind, out var downloadUrl))
            {
                captured.TryAdd(attachment.SourceIndex, new CapturedAttachment(downloadUrl));
            }
        }

        if (captured.IsEmpty)
            return;

        while (_captures.Count >= MaximumCaptures)
        {
            var oldest = _captures.FirstOrDefault();
            if (oldest.Key == default || !_captures.TryRemove(oldest.Key, out _))
                break;
        }

        _captures[CaptureKey.Create(activity)] = new CapturedActivity(
            timeProvider.GetUtcNow() + CaptureLifetime,
            captured);
    }

    public Task<AttachmentDownloadResult> DownloadAsync(
        TeamsInboundActivity activity,
        TeamsAttachmentMetadata attachment,
        string stagingDirectory,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(attachment);
        if (!_captures.TryGetValue(CaptureKey.Create(activity), out var captured)
            || captured.ExpiresAt <= timeProvider.GetUtcNow()
            || !captured.Attachments.TryRemove(attachment.SourceIndex, out var source))
        {
            throw new InvalidDataException("The authenticated Teams attachment download is unavailable.");
        }

        if (captured.Attachments.IsEmpty)
            _captures.TryRemove(CaptureKey.Create(activity), out _);

        return StreamingAttachmentDownloader.DownloadToFileAsync(
            httpClientFactory.CreateClient("teams-attachments"),
            source.DownloadUrl,
            configureRequest: null,
            targetDirectory: stagingDirectory,
            maxBytes: maximumBytes,
            cancellationToken: cancellationToken);
    }

    private void RemoveExpiredCaptures()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var entry in _captures)
        {
            if (entry.Value.ExpiresAt <= now)
                _captures.TryRemove(entry.Key, out _);
        }
    }

    private static bool TryGetDownloadUrl(
        TeamsAttachment attachment,
        TeamsInboundAttachmentKind kind,
        out string downloadUrl)
    {
        downloadUrl = string.Empty;
        var candidate = kind == TeamsInboundAttachmentKind.PersonalFile
            ? TryGetPersonalDownloadUrl(attachment.Content) ?? attachment.ContentUrl?.ToString()
            : attachment.ContentUrl?.ToString();
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Length > MaximumUrlLength
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !IsTrustedAttachmentHost(uri.Host))
        {
            return false;
        }

        downloadUrl = uri.AbsoluteUri;
        return true;
    }

    private static bool IsTrustedAttachmentHost(string host)
        => host.EndsWith(".teams.microsoft.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".botframework.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".trafficmanager.net", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".sharepoint.us", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".onedrive.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("onedrive.live.com", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetPersonalDownloadUrl(object? content)
    {
        if (content is JsonElement { ValueKind: JsonValueKind.Object } json
            && json.TryGetProperty("downloadUrl", out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private readonly record struct CaptureKey(
        string TenantId,
        string ConversationId,
        string ActivityId,
        TeamsConversationScope Scope)
    {
        public static CaptureKey Create(TeamsInboundActivity activity) => new(
            activity.Trust.TenantId,
            activity.Trust.ConversationId,
            activity.Trust.ActivityId,
            activity.Trust.Scope);
    }

    private sealed record CapturedActivity(
        DateTimeOffset ExpiresAt,
        ConcurrentDictionary<int, CapturedAttachment> Attachments);

    private sealed record CapturedAttachment(string DownloadUrl);
}
