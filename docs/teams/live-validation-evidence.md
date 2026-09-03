# Microsoft Teams live validation evidence

## Inline image stabilization follow-up (2026-09-03)

Status: **OFFLINE IMPLEMENTATION COMPLETE; LIVE SMOKE PENDING**.

The owner tenant logs identified two consecutive attachment boundaries.

- Teams sent two attachment entries for one inline image.
- One entry was a PNG with a content URL.
- The other entry was HTML image-rendering metadata.
- Before PR #56, the translator accepted the PNG entry and rejected the full
  activity for the companion entry. No download, scan, model call, or reply
  followed.
- PR #56 fixed that translation failure. The same live message then reached the
  shared attachment pipeline and failed with `mime=image/*`, `category=Other`,
  and `reason=category-not-allowed`.
- The transport wildcard was a bounded Teams image candidate. It was not a
  concrete verified MIME. `MimeTypeCatalog` correctly classified it as `Other`
  before the scanner could identify the PNG bytes.

The old synthetic tests used a named concrete image MIME or a single rendering
entry. They did not combine the live two-entry shape with an unnamed
`image/*` candidate and the production magic-byte scanner.

The translator still evaluates each entry independently. It accepts a bounded
content-URL `image/*` candidate, ignores only the strict HTML rendering
companion, and keeps unknown entries non-executable. The shared ingress request
now carries a provisional inline-image intent. That intent permits the Image
audience gate before download. It does not make a wildcard MIME verified.

For an extensionless provisional image only, the production scanner detects the
magic bytes. It accepts only a concrete supported Image MIME that passes the
scanner and the audience policy again. A PDF, archive, executable, unknown
byte stream, or policy-blocked image still rejects. The pipeline writes a safe
generated extension such as `attachment-1.png` when Teams did not provide a
usable name. Text and independently accepted attachments still make a model
turn; attachment-only input with no accepted attachment does not.

The daemon boundary still excludes raw attachment URLs from actor state. The
trusted-host allow list, redirect block, byte limits, staging, scanner,
verified MIME, model-image gate, and durable activity idempotency remain in
force.

The change does not add Microsoft Graph permissions.
It does not need `Files.Read.All`, `Sites.Read.All`, or `Chat.Read.All`.
The device-token handler can also be probed before Teams authentication
finishes. It now returns no device-auth result on `/api/messages`, so the
dedicated Azure AD Teams policy owns the Bot Framework bearer token. Device
token validation on its own endpoints is unchanged.

## Personal provider history compatibility

The Personal Teams route was healthy. The provider 400 came from a persisted
assistant `FunctionCallContent` name sent in provider history. A legacy name
with a dot, slash, colon, space, or other invalid punctuation can violate the
provider name pattern at a stable history index. This was not an image, Teams
transport, Graph, or memory-recall failure. A separate memory
`TaskCanceledException` remains degraded recall handling and is not joined to
this diagnosis.

The provider boundary now maps every current and historical function-call name
to the provider pattern. Existing safe names and established MCP slash aliases
stay stable. Other names use a deterministic reversible alias when it fits;
oversized names use a deterministic registry-correlated alias. The registry
rejects a collision before it can reach a provider. Function result call IDs
stay unchanged, and no persisted session is deleted or rewritten.

Focused regression coverage proves these cases:

- A mentioned channel message with text, PNG, and a rendering companion makes one model turn.
- The live unnamed `image/*` candidate is verified as a PNG and receives a safe `.png` stored name.
- An established thread with text and an image makes one model turn.
- An image-only accepted continuation makes one model turn.
- Disabled attachments never reach the model.
- A known rendering companion is ignored.
- A hostile structured companion remains rejected.
- Text remains available when a safe attachment is rejected.
- No raw attachment URL enters the actor or persistence contract.
- Historical invalid tool-call names and a following ordinary Personal message reach the provider successfully.

After deployment, the owner should run this smoke sequence:

1. Send a normal `@Netclaw` text message.
2. Send `@Netclaw` with a PNG.
3. Send PNG and text in an established thread.
4. Send an image-only established-thread continuation.
5. Disable attachments and repeat one image message.

For each accepted image, confirm one model turn and a reply. For the disabled
case, confirm no attachment reaches the model. Record only counters, scopes,
and pass or fail. Do not record message text, URLs, identifiers, tokens,
headers, or attachment bytes.

## Channel-binding and approval parity (2026-08-25)

Status: **OFFLINE IMPLEMENTATION COMPLETE; NOT LIVE VALIDATED**.

This change adds shared prompt-injection classification before Teams session
dispatch and routes ordinary output through the common channel output lifecycle.
It also uses the shared approval-response algorithm after Teams validates its
transport-specific callback binding. Teams retains its native Adaptive Card,
opaque nonce, destination, typing, and proactive-delivery responsibilities.

Expiry now reissues a transport card with a fresh nonce while leaving the core
approval pending. It does not manufacture a core Deny. Offline tests cover a
stale old card being rejected, the replacement card resolving once, duplicate
replacement actions being idempotent, detector failure failing closed, and the
feedback-failure forwarding/recovery path preserving one session decision.
They also deterministically cover initial and expiry-replacement card delivery
failure, restart recovery, bounded repeated failures, and successful unbound
card delivery. No raw nonce is persisted; each recovery reissues a fresh opaque
binding while the session remains authoritative and pending.

No Graph history source was configured or added. There is therefore no Teams
history backfill claim in this change. No tenant, package, route, deployment,
or live configuration was changed. Repeat the controlled Personal, Posts, and
Threads approval smoke matrix after deployment, including one expired-card
replacement action; record only sanitized structural outcomes.

Before the first deployment, take and verify a Teams persistence-store backup
or snapshot. The change can write `teams-approval-reissued-v2` and
`teams-approval-forwarding-v2`; the previous binary cannot read those
manifests. Binary rollback is safe only before either manifest is written.
Afterward, apply a forward fix or restore the verified pre-deployment snapshot
before starting the previous binary. Production transport failure injection is
not required because automated tests cover it.

## Phase 1.1 runtime-modernization handover (2026-08-24)

Status: **OFFLINE IMPLEMENTATION COMPLETE; NOT LIVE VALIDATED**.

The Phase 1.1 branch updates the runtime from Teams SDK .NET 2.0.9's plugin
host to the stable 2.1.0 native ASP.NET Core host. It keeps the one public
`/api/messages` endpoint and the existing tenant, ACL, body-limit, rate-limit,
actor, persistence, approval, and outbound contracts. The SDK's native typed
handlers now terminate at `TeamsSdkActivityTranslator`; session and approval
authority remain Netclaw-owned.

The existing `Teams` settings and secret-backed `Teams.ClientSecret` remain the
deployment configuration. SDK 2.1 uses its native compatibility mapping when
`AzureAd:ClientId` is absent, producing its in-memory `AzureAd`
client-credential configuration from `Teams.ClientId`, `Teams.TenantId`, and
`Teams.ClientSecret`. Netclaw rejects an enabled Teams configuration with a
root `AzureAd:ClientId`, so the mapping cannot silently choose conflicting
credentials. No Azure Bot, Entra application, Teams package, tenant, URL,
Traefik route, or customer credential change is required by this library
migration.

Offline coverage proves the SDK-bound inbound `AzureAd` JWT configuration and
outbound Microsoft identity configuration from the existing `Teams` settings.
The named `teams-sdk` policy uses `AzureAd` only for `/api/messages`; the
daemon's generic default policy and `AuthSelector` behavior remain unchanged.
Native SDK telemetry source subscription is deferred, so Phase 1.1 adds no
global ASP.NET Core instrumentation. Existing channel telemetry remains the
supported observability boundary.

The branch base `93df44f924d0f2321b4a7c9ba29d865478ed61a6` is the immediate
rollback point. Phase 1.1 makes no Teams persistence-format or schema change;
an operator can roll back by deploying the prior known-good image/commit.

After operator deployment, run one controlled interaction at a time: Personal
message, genuine-mentioned Posts root, genuine-mentioned Threads root,
same-human established-root continuation, native typing, one Deny and one Once
approval in each permitted scope, and a duplicate approval callback. Record
only counters and structural outcomes. Stop at the first failed boundary and
do not test persistent approval grants without explicit approval.

## 2026-08-23 standard channel roots

The deployed development source was `f5d4f5ef5393656a85ba668c11d43dbc53346523`.
Komodo deployed the linked `0.0.10` development image. The container health
check and the daemon readiness endpoint passed.

The protected configuration check passed without printing identifiers or
secrets. It confirmed these facts:

- Teams is enabled.
- Mention-only mode is enabled.
- Direct messages are disabled.
- The one approved team, one approved human user, and two approved standard
  channel identities remain in the allow lists.
- Both approved channels have one exact `Team` audience override.
- The live source uses the Entra object ID for the human ACL, with the Teams
  transport ID only as its fallback.

The Posts root has prior live pass evidence. On 2026-08-23, the operator sent
one genuine mention root in the separate standard Threads layout. The message
received two replies under the same root. The first was the processing reply.
The second was the completed model reply. This proves the standard Threads
root path from Teams ingress through model completion and reply delivery.

The fresh pre-message baseline was `2026-08-23T16:19:23Z`. The sanitized
post-message counters showed one additional session and completed turn, 9,770
input tokens, 39 output tokens, two received Teams activities, two routed
activities, and two posted replies. The safe counters also recorded one failed
reply attempt and two dropped activities. The visible root reply still passed.

The failed reply attempt has a confirmed, local cause. The current SDK
transport cannot update an existing activity. The binding actor attempted to
replace the processing reply, recorded that expected failure, and then posted
the completed reply as its normal fallback. The source now disables this
unsupported update attempt by default. A later transport can opt in only when
it implements activity updates. The focused actor and transport tests passed
for both paths.

The two dropped activities have no persisted reason code in the runtime
snapshot. They did not prevent the visible completed reply. Keep this as a
separate observability follow-up; do not infer an ACL or routing failure from
the aggregate counter.

The sanitized log scanner found one channel binding creation. It found no
attachment-policy rejection or exception type. It also found two bearer-value-
like log entries. No log value, message content, identifier, URL, token, or
secret was read, stored, or printed. A follow-up redaction-only scan of the
same uninterrupted container found zero bearer-value-like entries. No
credential exposure is confirmed and no credential rotation is required from
the available evidence.

### Processing-update follow-up

The deployed source is `753fcce3`. Komodo built and deployed development image
`0.0.13` from that source. The container is healthy, the readiness endpoint
returns HTTP 200, and the restart count is zero.

One fresh genuine mention root was then sent in the standard Threads-layout
channel. The Teams client showed both the processing reply and the completed
reply beneath that same root, and the reply composer remained scoped to the
thread.

The pre-message counters were zero for received, routed, dropped, posted,
rejected, and failed Teams activity. The sanitized post-message counters were:

- one received Teams activity;
- two routed boundaries (ingress and binding);
- one completed turn;
- two posted replies;
- zero rejected replies; and
- zero failed replies.

The new `channel_activity_mapping_stored`, `proactive_destination_captured`,
and rendering-wrapper counters each increased once. This confirms that the
unsupported update path is no longer used and that the completed response is
delivered normally.

The aggregate dropped counter increased once, but the runtime snapshot does
not retain a reason code. The redaction-only log classifier found no known
Teams drop-reason marker in the smoke window. The successful callback, route,
completion, and two successful deliveries show that this counter did not block
the tested root. Treat reason-level dropped-event telemetry as an observability
follow-up rather than an ACL, routing, or delivery failure.

Result: **THREADS ROOT LIVE PASS**.

## Requested established-thread continuation policy

The current mention-only policy deliberately ignores every unmentioned channel
message. On 2026-08-23, the operator tested two unmentioned continuations in
previously successful standard Threads roots. Neither received a reply. The
sanitized application counters did not change: no event reached Netclaw, no
route or turn ran, and no outbound reply was attempted. This is consistent with
the current upstream mention-only filter.

The requested product behaviour is different: after an approved human starts
a specific standard channel root with a genuine bot mention, later messages
from that same approved human in that same root should continue the existing
session without another mention. This is a new capability, not a regression in
the current mention-only implementation.

The implementation must not disable mention-only globally. It must retain the
tenant, team, channel, audience, and approved-human checks, and must admit an
unmentioned message only when its canonical root maps to an established bot
thread for that same human. Unmentioned new roots, replies in an unestablished
root, and replies from a different human must stay ignored. Add sanitized
fixtures and offline coverage before changing the ingress filter, then repeat
the controlled live continuation smoke.

### RSC established-thread continuation live pass

PR #32 was merged to `dev` as `4f3fb55a`, and Komodo built and deployed image
`0.0.15` from that commit. The source requests the Teams RSC permission
`ChannelMessage.Read.Group` but remains fail closed: it persists a SHA-256
fingerprint of the approved human with a root established by a genuine bot
mention. An unmentioned reply is admitted only when that fingerprint, the
canonical root, and every normal ACL check match. Roots created before this
change lack that fingerprint and remain ineligible.

The first package attempt declared a different Entra application ID than the
active Teams `ClientId`/`BotId`. It therefore did not activate the intended RSC
delivery path. Package version `1.0.3` corrected the package `AppId`, bot ID,
and `webApplicationInfo.id` to the active Entra application (client) ID. The
package was then upgraded or reinstalled in the exact test Team and the owner
accepted its RSC request. No application, tenant, team, channel, user, URL, or
secret identifier is recorded here.

A new genuine-mentioned Threads root and one unmentioned reply from the same
approved human both received normal processing and completed replies in that
same root. The reply composer remained thread scoped. The safe process totals
at the end of the positive smoke were `received=8`, `routed=6`, `replied=6`,
`rejected=0`, and `failed=0`; three activity-root mappings and two proactive
destinations had been captured. These totals include other non-content activity
boundaries and are not interpreted as message bodies or identifiers.

One new unmentioned Threads root was then sent. Teams delivered it: the safe
`received` counter increased from 8 to 9 and its rendering-wrapper diagnostic
increased from 4 to 5. The `routed`, `replied`, root-mapping, and proactive
destination counts did not change, and no reply was visible. This proves the
RSC transport delivery is active while Netclaw rejects an unmentioned new root
before session or model dispatch.

Result: **THREADS ESTABLISHED CONTINUATION LIVE PASS** and **RSC NEW-ROOT
FAIL-CLOSED LIVE PASS**.

## Personal tool-approval terminal-card live pass

A fresh Personal-scope tool approval reached the Teams client. Selecting
**Deny** returned the decision to the app, produced the terminal denied outcome,
and did not run the requested operation. This is a live pass for the protected
approval callback, one-time decision handling, and denial behaviour.

A subsequent fresh **Deny** test confirmed the source card is replaced in
place by an attention-styled **Approval denied** card. It includes the safe
tool and action display, has no actions, and produces one text outcome stating
that nothing was created.

A fresh **Once** test confirmed the complementary success presentation: the
source card is replaced in place by a good-styled **Approval granted** card
with no actions. The intentionally nonexistent `rmdir` target then failed as
expected, proving the approval decision proceeded to the command boundary
without creating or deleting anything.

Result: **PERSONAL APPROVAL CARD DENY AND ONCE LIVE PASS**.

## Team tool-approval card live pass

Posts and Threads both passed the controlled Team approval matrix. A fresh
**Deny** test replaced the source card in place and produced one text outcome.
The command did not run. A fresh **Once** test replaced the source card in
place and reached the harmless nonexistent target. The target did not exist,
so the command failed without any creation or deletion.

Result: **TEAM APPROVAL CARD DENY AND ONCE LIVE PASS**.

## Native typing-indicator follow-up

The earlier persistent `Processing...` post is an application-created message,
not the Teams native typing signal. The next transport change sends a transient
Teams `typing` activity when Netclaw begins processing. It must remain
best-effort: a typing-delivery failure must not suppress the final response or
any approval card.

After deployment, live validation must confirm that a normal interaction in
both approved Teams layouts shows the native typing indicator without a
persisted `Processing...` message.

## Remaining work

The current source improves approval-card presentation. After its deployment,
repeat the card matrix in both approved Team channels:

- Posts: one fresh **Deny** and one fresh **Once** using an agreed harmless,
  nonexistent target.
- Threads: one fresh **Deny** and one fresh **Once** in a genuine mentioned
  root, checking that the card and terminal result stay in that root.

For every case, verify the pending-card button styles, terminal card
replacement (no active buttons), one final text outcome, and no creation or
deletion. Do not relax the Team audience tool policy merely to force the test;
if it blocks before an approval prompt, record that as the policy result.

The established-thread continuation capability is live validated. The optional
remaining negative live check is a different approved human attempting an
unmentioned continuation; the equivalent offline policy coverage already
passes.
