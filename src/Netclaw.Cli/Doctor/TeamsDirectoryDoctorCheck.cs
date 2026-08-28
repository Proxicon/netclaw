// -----------------------------------------------------------------------
// <copyright file="TeamsDirectoryDoctorCheck.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Safe, offline validation for the Teams directory and ACL configuration. It
/// intentionally does not acquire Graph tokens: doctor must not expose secrets
/// or create live-tenant side effects merely to inspect configuration.
/// </summary>
public sealed class TeamsDirectoryDoctorCheck(NetclawPaths paths) : IDoctorCheck
{
    public Task<DoctorCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var (root, readError) = DoctorJsonConfigReader.TryReadConfig(paths);
        if (readError is not null)
            return Task.FromResult(DoctorCheckResult.Pass("Teams directory", "Skipped (base config is missing or invalid)."));

        if (root!["Teams"] is not JsonObject teams || !DoctorJsonConfigReader.ReadBool(teams, "Enabled"))
            return Task.FromResult(DoctorCheckResult.Pass("Teams directory", "Microsoft Teams connector disabled or not configured."));

        if (Missing(teams, "TenantId") || Missing(teams, "ClientId") || Missing(teams, "BotId"))
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "Teams directory",
                "Teams is enabled but its tenant, application, or bot ID is missing.",
                "Set Teams:TenantId, Teams:ClientId, and Teams:BotId; keep Teams:ClientSecret in the secrets overlay."));
        }

        var teamIds = DoctorJsonConfigReader.ReadStringArray(teams, "AllowedTeamIds");
        var channelIds = DoctorJsonConfigReader.ReadStringArray(teams, "AllowedChannelIds");
        if (teamIds.Count == 0 && channelIds.Count == 0)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "Teams directory",
                "Teams is enabled with no team or channel allow-list; channel traffic will be denied.",
                "Use Microsoft Teams > Channels to add canonical Team/channel IDs."));
        }

        var userIds = DoctorJsonConfigReader.ReadStringArray(teams, "AllowedUserIds");
        var groupIds = DoctorJsonConfigReader.ReadStringArray(teams, "AllowedGroupIds");
        if (DoctorJsonConfigReader.ReadBool(teams, "AllowDirectMessages") && userIds.Count == 0 && groupIds.Count == 0)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "Teams directory",
                "Teams DMs are enabled with no global user or group allow-list; DMs fail closed.",
                "Add Teams:AllowedUserIds or Teams:AllowedGroupIds before relying on DMs."));
        }

        if (groupIds.Count > 0)
        {
            return Task.FromResult(DoctorCheckResult.Pass(
                "Teams directory",
                "Teams group authorization is configured. Verify admin consent for Team.ReadBasic.All, Channel.ReadBasic.All, User.Read.All, and GroupMember.Read.All."));
        }

        return Task.FromResult(DoctorCheckResult.Pass(
            "Teams directory",
            "Teams has explicit canonical channel scope. Directory discovery remains optional for manual-ID configuration."));
    }

    private static bool Missing(JsonObject source, string property)
        => source[property] is not JsonValue value
           || !value.TryGetValue<string>(out var text)
           || string.IsNullOrWhiteSpace(text);
}
