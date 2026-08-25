# Microsoft Teams channel integration

This runbook describes how to package, configure, deploy, and operate the
Netclaw Microsoft Teams channel.

## Security boundary

The Teams integration is disabled by default. It accepts only explicit tenant,
team, channel, and user identities.

The app package grants only these bot scopes:

- `personal`
- `team`

The package requests one team-scoped RSC permission:

- `ChannelMessage.Read.Group`

This permission is required for Teams to deliver an unmentioned channel reply
to the bot. The team owner consents to it during app installation or upgrade.
It delivers standard channel messages from that installed team to the bot
endpoint. Netclaw retains `MentionOnly=true` as its model-dispatch policy: it
admits an unmentioned message only when its canonical root was established by a
genuine bot mention from the same approved human. New roots, unknown roots, and
other senders are ignored before a session or model turn.

The package does not request `ChatMessage.Read.Chat`, message-write, group
chat, meeting, tab, calling, video, or file capabilities. It supports personal
chats and standard team channels. It does not enable private or shared channels.

Netclaw rejects every Teams file attachment before model dispatch. Send the
required content as message text.

## Prerequisites

Prepare these items before you start:

- A Microsoft Entra tenant with permission to register an application.
- Permission to create an Azure Bot resource.
- A public HTTPS endpoint for the Netclaw daemon.
- Public HTTPS privacy and terms pages for your organization.
- Canonical tenant, team, channel, and user IDs from an authenticated source.

Do not copy canonical IDs from unauthenticated messages or log text. Use an
authenticated tenant directory or an approved tenant administration process.

Configure a supported non-local exposure mode before registration. A reverse
proxy needs a non-loopback daemon address and explicit `Daemon.TrustedProxies`.

## Register the app and bot

1. Create a single-tenant Microsoft Entra application.
2. Record its application ID and tenant ID.
3. Create one client secret with the shortest practical expiry.
4. Remove the default Microsoft Graph `User.Read` permission if it exists.
5. Create a single-tenant Azure Bot that uses the Entra application.
6. Enter the application ID and tenant ID for the bot identity.
7. Enable the Microsoft Teams channel on the Azure Bot resource.
8. Set the bot messaging endpoint to `https://<public-host>/api/messages`.

Azure Bot supports one messaging endpoint. Use the same endpoint for personal
and team messages.

Use the Entra application **(client) ID** for the Azure Bot, package `AppId`,
`ClientId`, and `BotId`; all four values must represent the same application.
Do not use the Entra object ID or the Teams `28:` bot identifier. Increment the
package version for every manifest update, then upgrade or reinstall that
package in the exact target Team so its owner can grant the requested RSC
permission. Copy the client secret value, not the secret ID.

Do not add permissions beyond the package's required
`ChannelMessage.Read.Group` RSC entry. Do not enable calling or meeting
features.

## Build the Teams package

Run the package script from the Netclaw repository root:

```powershell
$BuildPackage = @{
    AppId = '00000000-0000-0000-0000-000000000000'
    DeveloperName = 'Example Operator'
    PrivacyUrl = 'https://example.com/privacy'
    TermsOfUseUrl = 'https://example.com/terms'
    OutputPath = './artifacts/netclaw-teams.zip'
    Version = '1.0.0'
    Verbose = $true
}

./deploy/teams/build-package.ps1 @BuildPackage
```

The script requires absolute HTTPS policy URLs. It creates a ZIP file with
`manifest.json`, `color.png`, and `outline.png` at the package root.

Do not commit the generated package. Review its manifest before each upload.
The developer name has a 32-character limit. Increase the version for each
package update.

## Configure Netclaw

Put non-secret settings in `~/.netclaw/config/netclaw.json`:

```json
{
  "Teams": {
    "Enabled": true,
    "TenantId": "<tenant-id>",
    "ClientId": "<entra-application-id>",
    "BotId": "<entra-application-id>",
    "AuthenticationMode": "ClientSecret",
    "AllowDirectMessages": false,
    "MentionOnly": true,
    "AllowedTeamIds": ["<canonical-team-id>"],
    "AllowedChannelIds": ["<canonical-channel-id>"],
    "AllowedUserIds": ["<canonical-user-id>"],
    "ChannelAudienceOverrides": [
      {
        "TeamId": "<canonical-team-id>",
        "ChannelId": "<canonical-channel-id>",
        "Audience": "team"
      }
    ]
  }
}
```

`BotId` is the bare Microsoft application ID. Do not include the Teams `28:`
prefix.

Set the secret through the Netclaw secret store:

```text
netclaw secrets set Teams.ClientSecret <client-secret>
```

Do not put `ClientSecret` in `netclaw.json`. Do not paste the secret into logs,
issues, chat messages, or source control.

An empty team or channel allow-list rejects channel traffic. An empty user
allow-list accepts any sender in an allowed channel.

Personal chats require `AllowDirectMessages: true` and an exact
`AllowedUserIds` match. Production configurations must list approved user IDs.

`MentionOnly` defaults to `true`. Keep it enabled. With the package RSC
permission, it still ignores every unmentioned new or unknown root and permits
only the same approved human's continuation of a root they established with a
genuine bot mention.

Use `ChannelAudienceOverrides` for canonical IDs that contain configuration
delimiters. An exact team and channel entry takes precedence over a team entry.

## Phase 1.1 runtime modernization

The Teams transport uses the stable Microsoft Teams SDK for .NET 2.1 native
ASP.NET Core host. The public contract remains one authenticated
`POST /api/messages` endpoint with the existing body ceiling and rate limit.
Netclaw translates native typed SDK activities at that edge before actor and
session routing; Microsoft SDK types do not enter Netclaw channel contracts.

Existing `Teams` configuration and secret ownership remain unchanged. SDK 2.1
uses its native compatibility mapping when `AzureAd:ClientId` is absent: it
maps `Teams.ClientId`, `Teams.TenantId`, and secret-backed
`Teams.ClientSecret` into its in-memory `AzureAd` client-credential model.
Netclaw rejects enabled Teams configuration that also sets `AzureAd:ClientId`.
Do not add an `AzureAd` configuration block or duplicate the client secret.

The migration preserves personal, Posts, Threads, ACL, mention, attachment,
proactive delivery, typing, and Adaptive Card approval semantics. The named
`teams-sdk` policy selects `AzureAd` only for `/api/messages`; generic daemon
endpoints retain the upstream `AuthSelector` behavior. Native SDK telemetry
source subscription is deferred. Existing `ChannelTelemetry` remains in use,
and Phase 1.1 adds no global ASP.NET Core instrumentation or a second telemetry
pipeline.

Before deploying a Phase 1.1 build, run the controlled Personal, Posts,
Threads, approval, typing, and duplicate-approval smoke matrix in the live
handover record. Do not treat this documentation update as live validation.

## Validate a development tunnel

Use a development tunnel only for a bounded test period.

1. Start the daemon with an isolated owner-only Netclaw home.
2. Start the approved HTTPS development tunnel.
3. Set the Azure Bot messaging endpoint to the tunnel `/api/messages` URL.
4. Run `netclaw doctor`.
5. Run `netclaw status`.
6. Stop the daemon and tunnel after the test.
7. Restore the production endpoint before you leave the test environment.

Never publish a tunnel URL in a log, document, commit, or test fixture.

## Sideload the package

1. Open **Apps** in Microsoft Teams.
2. Open **Manage your apps**.
3. Select **Upload an app**.
4. Select **Upload a custom app**.
5. Upload the generated ZIP file.
6. Upgrade or reinstall the app in the approved team.
7. Have the team owner approve the `ChannelMessage.Read.Group` request.

Your tenant policy can disable custom app upload. Ask a Teams administrator to
approve or upload the package when required.

## Publish for production

1. Complete the privacy, legal, security, and ownership review.
2. Build a package with the production app ID and policy URLs.
3. Confirm that the manifest requests only `personal` and `team` bot scopes and
   the single `ChannelMessage.Read.Group` RSC permission.
4. Submit the package through the Teams admin center.
5. Ask a Teams administrator to approve and publish the app.
6. Install the app only in approved teams and accounts.

Do not use a development tunnel as the production endpoint. Use a stable HTTPS
host with normal certificate, monitoring, and recovery controls.

## Tool approvals

Teams sends a native Adaptive Card for a tool approval. The card preserves the
order and labels that the session supplies.

The pending card has a lock icon. The terminal card has an outcome icon.
The card shows bold labels and muted monospace values. Teams selects the local
monospace font. Netclaw does not load an external image for these icons.

| Card state | Icon | Card tone |
| --- | --- | --- |
| Pending | 🔒 | warning |
| Approved | ✅ | good |
| Denied | ⛔ | attention |

| Decision | Card style | Effect |
| --- | --- | --- |
| Once | positive | Allows the current call only. |
| This chat | default | Allows the scoped action for this session. |
| Always here | default | Saves a directory-scoped grant. |
| Always anywhere | destructive | Saves a global grant. |
| Deny | destructive | Refuses the current call. |

The Teams client can wrap the action row on a narrow display. This does not
change the action order or semantics. Each button sends an authenticated
`Action.Execute` callback. Netclaw validates the sender, tenant, conversation,
nonce, expiry, and persisted offered key before it accepts a decision.

Teams approval cards do not accept letter replies. Use a card button. This
keeps the signed card callback as the only decision path.

## Health checks

Run these checks after each deployment or secret rotation:

```text
netclaw doctor
netclaw status
curl http://127.0.0.1:5199/api/health/ready
```

Teams always reports degraded with a configured-but-unvalidated message in this
release. The readiness endpoint proves only daemon liveness.

Confirm that an approved message gets a reply under its original root. A
request without valid Bot Framework authentication cannot reach model dispatch.

After a package upgrade that adds RSC, first send one genuine-mentioned root.
Then send one unmentioned reply from the same approved human in that root. It
must continue the same session and reply in the same root. Confirm separately
that an unmentioned new root and an unmentioned reply from another human do not
create a session or model turn.

Run `netclaw status` after the smoke. Confirm that the Teams `recv`, `routed`,
and `replied` counters increase.

Check structured daemon logs for the stage and outcome. Do not enable payload
logging or retain tenant identifiers as evidence.

Record each live result with only counters and structural facts. Do not record
message bodies, activity payloads, identifiers, URLs, headers, tokens, or
secrets. See [live validation evidence](../teams/live-validation-evidence.md)
for the current standard Posts and Threads root results and open follow-ups.

## Rotate the client secret

1. Create a new Entra client secret.
2. Store it with `netclaw secrets set Teams.ClientSecret <new-secret>`.
3. Restart the Netclaw daemon.
4. Run the health checks.
5. Run one approved message smoke when your change policy requires it.
6. Revoke the old secret after the new secret works.

If validation fails, restore the old secret before you revoke it. Never print
either secret during diagnosis.

## Troubleshooting

- A disconnected connector usually indicates invalid credentials or an
  unreachable Bot Framework service.
- An unmentioned new or unknown channel root is ignored when `MentionOnly` is
  `true`. An unmentioned continuation is admitted only for the approved human
  who established that root with a genuine bot mention.
- A channel identity outside either allow-list is rejected before dispatch.
- A user outside `AllowedUserIds` is rejected before dispatch.
- An unmapped channel uses the `public` audience and cannot use restricted
  tools.
- A Graph-backed attachment is rejected without a download fallback.
- A client secret in `netclaw.json` fails the supported configuration model.

## Rollback

1. Set `Teams.Enabled` to `false` in `netclaw.json`.
2. Restart the Netclaw daemon.
3. Confirm that the Teams connector is absent or disabled in `netclaw status`.
4. Withdraw or block the app in the Teams admin center.
5. Revoke the client secret when the rollback is permanent.

This rollback stops new Teams ingress after restart. It does not delete durable
session, approval, destination, or delivery evidence.

## Microsoft references

- [Register an Entra application](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app)
- [Configure Azure Bot authentication](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/how-to/authentication/add-authentication)
- [Enable RSC channel-message delivery](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/how-to/conversations/channel-messages-for-bots-and-agents)
- [Upload a custom Teams app](https://learn.microsoft.com/en-us/microsoftteams/platform/concepts/deploy-and-publish/apps-upload)
- [Publish a Teams app](https://learn.microsoft.com/en-us/microsoftteams/platform/concepts/deploy-and-publish/apps-publish-overview)
