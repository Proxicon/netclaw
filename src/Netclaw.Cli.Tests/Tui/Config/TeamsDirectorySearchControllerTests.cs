// -----------------------------------------------------------------------
// <copyright file="TeamsDirectorySearchControllerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using Netclaw.Channels.Teams;
using Netclaw.Cli.Tui.Config;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class TeamsDirectorySearchControllerTests
{
    [Fact]
    public async Task Search_waits_for_the_debounce_before_it_calls_the_directory()
    {
        var time = new FakeTimeProvider();
        var directory = new RecordingDirectory();
        using var controller = new TeamsDirectorySearchController(directory, time);

        var search = controller.SearchTeamsAsync("Operations", TestContext.Current.CancellationToken).AsTask();

        Assert.Equal(0, directory.TeamSearchCount);
        time.Advance(TimeSpan.FromMilliseconds(300));
        var response = await search;

        Assert.True(response.IsCurrent);
        Assert.True(response.Result.IsAvailable);
        Assert.Equal(1, directory.TeamSearchCount);
        Assert.Equal("team-1", Assert.Single(response.Result.Value!).Id);
    }

    [Fact]
    public async Task A_superseded_search_cannot_publish_stale_results()
    {
        var time = new FakeTimeProvider();
        var directory = new RecordingDirectory();
        using var controller = new TeamsDirectorySearchController(directory, time);

        var first = controller.SearchTeamsAsync("Old query", TestContext.Current.CancellationToken).AsTask();
        var second = controller.SearchTeamsAsync("Current query", TestContext.Current.CancellationToken).AsTask();
        time.Advance(TimeSpan.FromMilliseconds(300));

        var firstResponse = await first;
        var secondResponse = await second;

        Assert.False(firstResponse.IsCurrent);
        Assert.True(secondResponse.IsCurrent);
        Assert.Equal(1, directory.TeamSearchCount);
    }

    private sealed class RecordingDirectory : ITeamsDirectory
    {
        public int TeamSearchCount { get; private set; }

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryTeam>>> SearchTeamsAsync(
            string query,
            int maximumResults,
            CancellationToken cancellationToken = default)
        {
            TeamSearchCount++;
            return ValueTask.FromResult(
                TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryTeam>>.Available(
                    [new TeamsDirectoryTeam("team-1", "Operations", null)]));
        }

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryTeam>> GetTeamAsync(
            string teamId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryTeam>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryChannel>>> GetChannelsAsync(
            string teamId,
            int maximumResults,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryChannel>>.Available([]));

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryChannel>> GetChannelAsync(
            string teamId,
            string channelId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryChannel>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryUser>>> SearchUsersAsync(
            string query,
            int maximumResults,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryUser>>.Available([]));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryGroup>>> SearchGroupsAsync(
            string query,
            int maximumResults,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<IReadOnlyList<TeamsDirectoryGroup>>.Available([]));

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryGroup>> GetGroupAsync(
            string groupId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryGroup>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<TeamsDirectoryUser>> GetUserAsync(
            string userId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryUser>.Unavailable("not_used"));

        public ValueTask<TeamsDirectoryOperationResult<IReadOnlySet<string>>> CheckUserGroupMembershipAsync(
            string userId,
            IReadOnlyCollection<string> groupIds,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(
                TeamsDirectoryOperationResult<IReadOnlySet<string>>.Available(new HashSet<string>(StringComparer.Ordinal)));
    }
}
