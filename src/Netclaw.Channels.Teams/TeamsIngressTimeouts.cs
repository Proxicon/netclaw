// -----------------------------------------------------------------------
// <copyright file="TeamsIngressTimeouts.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels.Teams;

internal static class TeamsIngressTimeouts
{
    internal static readonly TimeSpan AttachmentOperation = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan InlineImageDownload = TimeSpan.FromSeconds(60);

    // Each file has separate download and scan deadlines. The binding processes files in sequence.
    internal static TimeSpan BindingRoute(TeamsInboundActivity activity) =>
        TimeSpan.FromSeconds(10) + (InlineImageDownload + AttachmentOperation) * activity.Attachments.Length;

    internal static TimeSpan ConversationRoute(TeamsInboundActivity activity) =>
        BindingRoute(activity) + TimeSpan.FromSeconds(5);

    internal static TimeSpan IngressRoute(TeamsInboundActivity activity) =>
        ConversationRoute(activity) + TimeSpan.FromSeconds(5);
}

// This exception crosses only the local downloader call. It contains no SDK exception or resource identifier.
internal sealed class TeamsAttachmentDownloadException(
    string hostClass,
    bool authenticated,
    string stage,
    bool cancelled,
    bool httpError) : Exception("The Teams attachment download failed.")
{
    internal string HostClass { get; } = hostClass;
    internal bool Authenticated { get; } = authenticated;
    internal string Stage { get; } = stage;
    internal bool Cancelled { get; } = cancelled;
    internal bool HttpError { get; } = httpError;
}
