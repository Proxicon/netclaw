# Microsoft Teams live validation evidence

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

## Tool-approval denial live pass and presentation follow-up

A fresh Personal-scope tool approval reached the Teams client. Selecting
**Deny** returned the decision to the app, produced the terminal denied outcome,
and did not run the requested operation. This is a live pass for the protected
approval callback, one-time decision handling, and denial behaviour.

The initial Teams presentation still left the source approval card actionable
and added a separate minimal terminal card. The required presentation behaviour
is that the source card is replaced with a terminal card that has no actions and
shows the tool, action or invocation, and decision using the supported Adaptive
Card styling. The next implementation PR returns that terminal card directly
from the `Action.Execute` invoke response; it requires a fresh live click test
after deployment to confirm that Teams replaces the original card.

## Remaining work

The next live Teams check is the terminal approval-card replacement described
above, followed by the tool-approval tenant matrix. The established-thread
continuation capability is live validated. The optional remaining negative live
check is a different approved human attempting an unmentioned continuation;
the equivalent offline policy coverage already passes.
