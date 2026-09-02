// -----------------------------------------------------------------------
// <copyright file="TeamsGraphDirectoryClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Netclaw.Channels.Teams.Graph;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class TeamsGraphDirectoryClientTests
{
    [Fact]
    public async Task Team_search_keeps_a_short_query_cache_and_seeds_the_long_lived_team_record()
    {
        using var handler = new TeamsDirectoryHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var graphClient = new GraphServiceClient(
            httpClient,
            new AnonymousAuthenticationProvider(),
            "https://graph.test/v1.0");
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 });
        using var directory = new TeamsGraphDirectoryClient(graphClient, "tenant-a", cache, TimeProvider.System);

        var first = await directory.SearchTeamsAsync("Op", 25, TestContext.Current.CancellationToken);
        var second = await directory.SearchTeamsAsync("Op", 25, TestContext.Current.CancellationToken);
        var cachedRecord = await directory.GetTeamAsync("team-1", TestContext.Current.CancellationToken);
        var differentBound = await directory.SearchTeamsAsync("Op", 26, TestContext.Current.CancellationToken);

        Assert.True(first.IsAvailable);
        Assert.True(second.IsAvailable);
        Assert.True(cachedRecord.IsAvailable);
        Assert.True(differentBound.IsAvailable);
        Assert.Equal(2, handler.TeamListRequests);
        Assert.Equal(0, handler.TeamRecordRequests);
    }

    [Fact]
    public async Task Channel_list_omits_the_unsupported_top_query_option()
    {
        using var handler = new TeamsDirectoryHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var graphClient = new GraphServiceClient(
            httpClient,
            new AnonymousAuthenticationProvider(),
            "https://graph.test/v1.0");
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 });
        using var directory = new TeamsGraphDirectoryClient(graphClient, "tenant-a", cache, TimeProvider.System);

        var result = await directory.GetChannelsAsync("team-1", 25, TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.Single(result.Value!);
        Assert.NotNull(handler.ChannelListRequestUri);
        Assert.DoesNotContain("top=", handler.ChannelListRequestUri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "teams_directory_authentication_failed")]
    [InlineData(HttpStatusCode.Forbidden, "teams_directory_permission_denied")]
    public async Task Directory_request_classifies_authentication_and_permission_failures(HttpStatusCode statusCode, string reasonCode)
    {
        using var httpClient = new HttpClient(new StatusCodeHandler(statusCode));
        using var graphClient = new GraphServiceClient(
            httpClient,
            new AnonymousAuthenticationProvider(),
            "https://graph.test/v1.0");
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 });
        using var directory = new TeamsGraphDirectoryClient(graphClient, "tenant-a", cache, TimeProvider.System);

        var result = await directory.GetTeamAsync("team-1", TestContext.Current.CancellationToken);

        Assert.False(result.IsAvailable);
        Assert.Equal(reasonCode, result.ReasonCode);
    }

    private sealed class StatusCodeHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private sealed class TeamsDirectoryHttpHandler : HttpMessageHandler
    {
        public int TeamListRequests { get; private set; }

        public int TeamRecordRequests { get; private set; }

        public Uri? ChannelListRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/teams", StringComparison.Ordinal))
            {
                TeamListRequests++;
                return Task.FromResult(Json("""{ "value": [{ "id": "team-1", "displayName": "Operations" }] }"""));
            }

            if (path.EndsWith("/teams/team-1", StringComparison.Ordinal))
            {
                TeamRecordRequests++;
                return Task.FromResult(Json("""{ "id": "team-1", "displayName": "Operations" }"""));
            }

            if (path.EndsWith("/teams/team-1/channels", StringComparison.Ordinal))
            {
                ChannelListRequestUri = request.RequestUri;
                return Task.FromResult(Json("""{ "value": [{ "id": "channel-1", "displayName": "General" }] }"""));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string content)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
                }
            };
    }
}
