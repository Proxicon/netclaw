// -----------------------------------------------------------------------
// <copyright file="TeamsProvisionalInlineImageIngress.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Event;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;

namespace Netclaw.Channels.Teams;

/// <summary>
/// Normalizes the Teams <c>image/*</c> inline transport shape before it enters
/// the shared attachment projection path. This transport-only shape never
/// changes generic MIME classification.
/// </summary>
internal static class TeamsProvisionalInlineImageIngress
{
    private const int HeaderReadSize = 64;

    public static bool IsProvisionalInlineImage(TeamsAttachmentMetadata attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        if (attachment.Kind != TeamsInboundAttachmentKind.InlineImage
            || string.IsNullOrWhiteSpace(attachment.ContentType))
        {
            return false;
        }

        var mediaType = attachment.ContentType.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, "image/*", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<AttachmentIngestOutcome> IngestAsync(
        TeamsInboundActivity activity,
        TeamsAttachmentMetadata attachment,
        TrustAudience audience,
        ChannelAttachmentPolicy policy,
        bool inlineImages,
        string inboxDirectory,
        string stagingDirectory,
        TimeSpan operationTimeout,
        IContentScanner scanner,
        ILoggingAdapter log,
        ITeamsAttachmentDownloader downloader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(downloader);

        var declaredMime = new DeclaredMimeType(attachment.ContentType);
        if (!policy.Allows(AttachmentCategory.Image))
        {
            log.Warning(
                "attachment_rejected name={Name} mime={Mime} audience={Audience} category={Category} reason=category-not-allowed",
                attachment.Name, declaredMime.Value, audience, AttachmentCategory.Image);
            return NotAllowed(attachment.Name, AttachmentCategory.Image, audience);
        }

        if (attachment.DeclaredSizeBytes is { } declaredSize && declaredSize > policy.MaxFileBytes)
        {
            log.Warning(
                "attachment_rejected name={Name} mime={Mime} audience={Audience} size={Size} limit={Limit} reason=too-large",
                attachment.Name, declaredMime.Value, audience, declaredSize, policy.MaxFileBytes);
            return Reject($"`{attachment.Name}` ({AttachmentIngressFormatting.FormatBytes(declaredSize)}) exceeds the {AttachmentIngressFormatting.FormatBytes(policy.MaxFileBytes)} per-file limit.");
        }

        AttachmentDownloadResult download;
        try
        {
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadCts.CancelAfter(operationTimeout);
            download = await downloader.DownloadAsync(
                activity,
                attachment,
                stagingDirectory,
                policy.MaxFileBytes,
                downloadCts.Token).ConfigureAwait(false);
        }
        catch (AttachmentTooLargeException exception)
        {
            log.Warning(
                "attachment_rejected name={Name} mime={Mime} audience={Audience} size={Size} limit={Limit} reason=too-large-during-download",
                attachment.Name, declaredMime.Value, audience, exception.BytesReceived, exception.MaxBytes);
            return Reject($"`{attachment.Name}` ({AttachmentIngressFormatting.FormatBytes(exception.BytesReceived)}) exceeds the {AttachmentIngressFormatting.FormatBytes(exception.MaxBytes)} per-file limit.");
        }
        catch (OperationCanceledException exception)
        {
            log.Warning(exception,
                "attachment_rejected name={Name} mime={Mime} reason=download-timeout",
                attachment.Name, declaredMime.Value);
            return Reject($"Timed out downloading `{attachment.Name}`. Please try again.");
        }
        catch (Exception exception)
        {
            log.Warning(exception,
                "attachment_rejected name={Name} mime={Mime} reason=download-failed",
                attachment.Name, declaredMime.Value);
            return Reject($"Couldn't download `{attachment.Name}` — please try again later.");
        }

        if (download.BytesWritten == 0)
        {
            log.Warning(
                "attachment_rejected name={Name} mime={Mime} reason=empty-download",
                attachment.Name, declaredMime.Value);
            TryDeleteTemp(log, download.FilePath);
            return Reject($"`{attachment.Name}` downloaded as zero bytes.");
        }

        var detectedMime = await DetectMimeAsync(download.FilePath, cancellationToken).ConfigureAwait(false);
        if (detectedMime is null)
        {
            log.Warning(
                "attachment_rejected name={Name} mime={Mime} reason=provisional-image-signature-unrecognized",
                attachment.Name, declaredMime.Value);
            TryDeleteTemp(log, download.FilePath);
            return Reject($"Content scanner rejected `{attachment.Name}`: a verified image signature is required.");
        }

        var verifiedName = CreateVerifiedName(attachment, detectedMime.Value);
        ContentVerificationResult verification;
        try
        {
            verification = await ContentVerification.ResolveAsync(
                scanner,
                download.FilePath,
                verifiedName,
                new DeclaredMimeType(detectedMime.Value.Value),
                policy,
                operationTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteTemp(log, download.FilePath);
            throw;
        }

        if (verification is not ContentVerificationResult.Verified verified
            || verified.Category != AttachmentCategory.Image
            || !string.Equals(verified.MimeType.Value, detectedMime.Value.Value, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteTemp(log, download.FilePath);
            return RejectVerification(verification, attachment.Name, declaredMime.Value, audience, log);
        }

        string inboxPath;
        try
        {
            inboxPath = InboxWriter.SanitizeReserveAndMove(inboxDirectory, verifiedName, download.FilePath);
        }
        catch (InboxWriter.CollisionExhaustedException exception)
        {
            log.Warning(exception,
                "attachment_rejected name={Name} reason=collision-exhausted",
                verifiedName);
            TryDeleteTemp(log, download.FilePath);
            return Reject($"Too many attachments named `{verifiedName}` in this session — please rename and try again.");
        }
        catch (Exception exception)
        {
            log.Error(exception,
                "attachment_rejected name={Name} reason=inbox-write-failed",
                verifiedName);
            TryDeleteTemp(log, download.FilePath);
            return Reject($"Couldn't save `{verifiedName}` — please try again later.");
        }

        var projection = await AttachmentIngressFormatting.BuildAcceptedProjectionAsync(
            inboxPath,
            verifiedName,
            verified.MimeType.Value,
            verified.Category,
            inlineImages,
            download.BytesWritten,
            cancellationToken).ConfigureAwait(false);

        log.Info(
            "attachment_accepted name={Name} declaredMime={DeclaredMime} verifiedMime={VerifiedMime} size={Size} category={Category} inlined={Inlined}",
            verifiedName, declaredMime.Value, verified.MimeType.Value, download.BytesWritten, verified.Category, projection.Inlined);

        return new AttachmentIngestOutcome.Accepted(projection.Line, projection.InlineContent);
    }

    private static async Task<MimeType?> DetectMimeAsync(string filePath, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderReadSize];
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            HeaderReadSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        var detected = MagicByteValidator.DetectMimeType(header.AsSpan(0, read));
        if (detected is null)
            return null;

        return new MimeType(detected);
    }

    private static string CreateVerifiedName(TeamsAttachmentMetadata attachment, MimeType mimeType)
    {
        var safeName = FilenameSanitizer.Sanitize(attachment.Name);
        var stem = Path.GetFileNameWithoutExtension(safeName);
        if (string.IsNullOrWhiteSpace(stem))
            stem = $"attachment-{attachment.SourceIndex + 1}";

        return FilenameSanitizer.Sanitize(stem + MimeTypeCatalog.ExtensionFor(mimeType));
    }

    private static AttachmentIngestOutcome.Rejected RejectVerification(
        ContentVerificationResult verification,
        string name,
        string declaredMime,
        TrustAudience audience,
        ILoggingAdapter log)
    {
        switch (verification)
        {
            case ContentVerificationResult.ScanThrew scanThrew:
                log.Warning(scanThrew.Exception,
                    "attachment_rejected name={Name} mime={Mime} reason=scan-exception",
                    name, declaredMime);
                return Reject($"Couldn't scan `{name}` — please try again later.");

            case ContentVerificationResult.ScanBlocked scanBlocked:
                log.Warning(
                    "attachment_rejected name={Name} mime={Mime} reason=scan-blocked error={ScanError} message={ScanMessage}",
                    name, declaredMime, scanBlocked.Error?.ToString(), scanBlocked.Message ?? scanBlocked.Error?.ToString());
                return scanBlocked.Error == ContentScanError.ScanFailure
                    ? Reject($"Couldn't scan `{name}` — please try again later.")
                    : Reject($"Content scanner rejected `{name}`: {scanBlocked.Message ?? scanBlocked.Error?.ToString() ?? "unknown error"}.");

            case ContentVerificationResult.MissingVerifiedMime:
                log.Warning(
                    "attachment_rejected name={Name} declaredMime={DeclaredMime} reason=missing-verified-mime",
                    name, declaredMime);
                return Reject($"Content scanner did not verify `{name}`. Please try again later.");

            case ContentVerificationResult.CategoryNotAllowed notAllowed:
                log.Warning(
                    "attachment_rejected name={Name} declaredMime={DeclaredMime} verifiedMime={VerifiedMime} audience={Audience} category={Category} reason=verified-category-not-allowed",
                    name, declaredMime, notAllowed.MimeType.Value, audience, notAllowed.Category);
                return NotAllowed(name, notAllowed.Category, audience);

            case ContentVerificationResult.Verified verified:
                log.Warning(
                    "attachment_rejected name={Name} declaredMime={DeclaredMime} verifiedMime={VerifiedMime} reason=provisional-image-verification-mismatch",
                    name, declaredMime, verified.MimeType.Value);
                return Reject($"Content scanner rejected `{name}`: a verified image signature is required.");

            default:
                throw new InvalidOperationException($"Unhandled content verification result: {verification.GetType().Name}");
        }
    }

    private static AttachmentIngestOutcome.Rejected NotAllowed(
        string name,
        AttachmentCategory category,
        TrustAudience audience) =>
        new($"`{name}` ({category}) isn't allowed in {audience} channels. " +
            "Please DM me if you want to share this class of file.");

    private static AttachmentIngestOutcome.Rejected Reject(string reason) => new(reason);

    private static void TryDeleteTemp(ILoggingAdapter log, string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (Exception exception)
        {
            log.Error(exception, "Failed to clean up staged attachment file {Path}", tempPath);
        }
    }
}
