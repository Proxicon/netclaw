// -----------------------------------------------------------------------
// <copyright file="ProvisionalInlineImageIngressTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Covers the narrow extensionless image path used by Teams inline images.
/// The transport wildcard is only a candidate; bytes still decide the MIME.
/// </summary>
public sealed class ProvisionalInlineImageIngressTests : IDisposable
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] PdfBytes = "%PDF-1.7"u8.ToArray();
    private readonly DisposableTempDir _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task Extensionless_image_transport_candidate_uses_verified_png_name_and_data()
    {
        var outcome = await IngestAsync(PngBytes);

        var accepted = Assert.IsType<AttachmentIngestOutcome.Accepted>(outcome);
        Assert.Contains("attachment-1.png", accepted.Line, StringComparison.Ordinal);
        var image = Assert.IsType<DataContent>(accepted.Inline);
        Assert.Equal("image/png", image.MediaType);
        Assert.True(File.Exists(Path.Combine(_root.Path, "inbox", "attachment-1.png")));
    }

    [Fact]
    public async Task Extensionless_image_transport_candidate_rejects_non_image_bytes()
    {
        var outcome = await IngestAsync(PdfBytes);

        Assert.IsType<AttachmentIngestOutcome.Rejected>(outcome);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root.Path, "inbox")));
    }

    private async Task<AttachmentIngestOutcome> IngestAsync(byte[] bytes)
    {
        var inbox = Path.Combine(_root.Path, "inbox");
        var staging = Path.Combine(_root.Path, "staging");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(staging);

        return await AttachmentIngressPipeline.IngestAsync(
            new AttachmentIngressRequest(
                "attachment-1",
                "image/*",
                bytes.Length,
                AttachmentIngressIntent.ProvisionalInlineImage,
                RequiresVerifiedFileName: true),
            TrustAudience.Public,
            new ChannelAttachmentPolicy
            {
                AllowedCategories = [AttachmentCategory.Image],
                MaxFileBytes = ChannelAttachmentPolicy.DefaultMaxFileBytes,
                MaxFilesPerMessage = 1
            },
            inlineImages: true,
            inbox,
            staging,
            TimeSpan.FromSeconds(5),
            new MagicByteContentScanner(new ContentPolicy()),
            NoLogger.Instance,
            async (directory, _, cancellationToken) =>
            {
                var path = Path.Combine(directory, "download.tmp");
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                return new AttachmentDownloadResult(path, bytes.Length);
            },
            TestContext.Current.CancellationToken);
    }
}
