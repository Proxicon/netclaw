// -----------------------------------------------------------------------
// <copyright file="TeamsSdkAttachmentDownloaderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Immutable;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Core.Schema;
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Channels.Teams;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class TeamsSdkAttachmentDownloaderTests
{
    [Theory]
    [InlineData("http://smba.trafficmanager.net/attachments/image")]
    [InlineData("https://user@smba.trafficmanager.net/attachments/image")]
    [InlineData("https://files.example.test/attachments/image")]
    [InlineData("https://smba.trafficmanager.net.example.test/attachments/image")]
    [InlineData("https://attacker.trafficmanager.net/attachments/image")]
    public async Task Capture_rejects_untrusted_attachment_urls(string url)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var handler = new RecordingHandler(_ => Success("content"));
        var downloader = new TeamsSdkAttachmentDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        var attachment = CreateAttachment();

        downloader.Capture(CreateSdkMessage(url), activity);

        using var temp = new DisposableTempDir();
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Capture_allows_the_documented_smba_attachment_host()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var handler = new RecordingHandler(_ => Success("content"));
        var downloader = new TeamsSdkAttachmentDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        var attachment = CreateAttachment();

        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);

        using var temp = new DisposableTempDir();
        var result = await downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result.BytesWritten);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Download_does_not_follow_a_redirect()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://files.example.test/redirected") }
        });
        var downloader = new TeamsSdkAttachmentDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        var attachment = CreateAttachment();
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);

        using var temp = new DisposableTempDir();
        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Captures_expire_after_the_short_lifetime()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var handler = new RecordingHandler(_ => Success("content"));
        var downloader = new TeamsSdkAttachmentDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        var attachment = CreateAttachment();
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);
        clock.Advance(TimeSpan.FromMinutes(6));

        using var temp = new DisposableTempDir();
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Captured_attachment_url_is_single_use()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var handler = new RecordingHandler(_ => Success("content"));
        var downloader = new TeamsSdkAttachmentDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        var attachment = CreateAttachment();
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);

        using var temp = new DisposableTempDir();
        await downloader.DownloadAsync(activity, attachment, temp.Path, 1_024, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public void Capture_keeps_no_more_than_1024_activities()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var downloader = new TeamsSdkAttachmentDownloader(
            new TestHttpClientFactory(new RecordingHandler(_ => Success("content"))),
            clock);

        for (var index = 0; index < 1_025; index++)
        {
            var activity = CreateActivity($"activity-{index}");
            downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);
        }

        var capturesField = typeof(TeamsSdkAttachmentDownloader).GetField("_captures", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var captures = capturesField!.GetValue(downloader)!;
        var count = Assert.IsType<int>(captures.GetType().GetProperty("Count")!.GetValue(captures));
        Assert.Equal(1_024, count);
    }

    [Fact]
    public async Task Capture_rejects_an_overlong_url()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var handler = new RecordingHandler(_ => Success("content"));
        var downloader = new TeamsSdkAttachmentDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        var attachment = CreateAttachment();
        var url = "https://smba.trafficmanager.net/" + new string('a', 4_097);
        downloader.Capture(CreateSdkMessage(url), activity);

        using var temp = new DisposableTempDir();
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Download_removes_the_partial_file_when_the_stream_exceeds_the_limit()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var handler = new RecordingHandler(_ => Success(new string('x', 1_025)));
        var downloader = new TeamsSdkAttachmentDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        var attachment = CreateAttachment();
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);

        using var temp = new DisposableTempDir();
        await Assert.ThrowsAsync<AttachmentTooLargeException>(() => downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(temp.Path));
    }

    [Fact]
    public async Task Download_removes_the_partial_file_when_cancelled()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T10:00:00Z"));
        var stream = new PartialThenCancellationStream();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        });
        var downloader = new TeamsSdkAttachmentDownloader(new TestHttpClientFactory(handler), clock);
        var activity = CreateActivity();
        var attachment = CreateAttachment();
        downloader.Capture(CreateSdkMessage("https://smba.trafficmanager.net/amer/v3/attachments/image"), activity);

        using var temp = new DisposableTempDir();
        using var cancellation = new CancellationTokenSource();
        var download = downloader.DownloadAsync(
            activity,
            attachment,
            temp.Path,
            maximumBytes: 1_024,
            cancellation.Token);
        await stream.WaitingForCancellation.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Empty(Directory.EnumerateFiles(temp.Path));
    }

    [Fact]
    public void Attachment_url_does_not_enter_actor_or_persistence_contracts()
    {
        const string rawUrl = "https://smba.trafficmanager.net/amer/v3/attachments/sensitive";
        var attachment = CreateAttachment();
        var activity = CreateActivity(attachments: [attachment]);
        var ingress = new TeamsBindingIngress(activity, CancellationToken.None);
        var conversationIngress = new TeamsConversationIngress(activity, CancellationToken.None);

        Assert.DoesNotContain("Url", string.Join(',', typeof(TeamsAttachmentMetadata).GetProperties().Select(property => property.Name)), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rawUrl, JsonSerializer.Serialize(activity), StringComparison.Ordinal);
        Assert.Same(activity, ingress.Activity);
        Assert.Same(activity, conversationIngress.Activity);
    }

    private static TeamsInboundActivity CreateActivity(
        string activityId = "activity-a",
        ImmutableArray<TeamsAttachmentMetadata> attachments = default) => new(
        new TeamsIngressTrustContext(
            TrustAudience.Personal,
            PrincipalClassification.TrustedInternal,
            TrustBoundary.Personal,
            new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            "user-a",
            "tenant-a",
            "conversation-a",
            TeamsConversationScope.Personal,
            activityId,
            DateTimeOffset.Parse("2026-09-02T10:00:00Z")),
        "attachment input",
        attachments: attachments.IsDefault ? [CreateAttachment()] : attachments);

    private static TeamsAttachmentMetadata CreateAttachment() => new("image.png", "image/png", 7)
    {
        Kind = TeamsInboundAttachmentKind.InlineImage,
        SourceIndex = 0
    };

    private static MessageActivity CreateSdkMessage(string url)
    {
        var message = MessageActivity.FromActivity(CoreActivity.FromJsonString("{\"type\":\"message\"}"));
        message.Attachments =
        [
            new TeamsAttachment
            {
                ContentType = new AttachmentContentType("image/png"),
                ContentUrl = new Uri(url),
                Name = "image.png"
            }
        ];
        return message;
    }

    private static HttpResponseMessage Success(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/octet-stream")
    };

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("teams-attachments", name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class PartialThenCancellationStream : Stream
    {
        private bool _returnedInitialBytes;

        public TaskCompletionSource WaitingForCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_returnedInitialBytes)
            {
                _returnedInitialBytes = true;
                buffer.Span[..4].Fill(1);
                return ValueTask.FromResult(4);
            }

            WaitingForCancellation.TrySetResult();
            var pendingRead = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => pendingRead.TrySetCanceled(cancellationToken));
            return new ValueTask<int>(pendingRead.Task);
        }
    }
}
