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
for both paths. The next fresh Threads smoke must confirm that the completed
reply still appears and that the failed-reply counter does not increase.

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

Result: **THREADS ROOT LIVE PASS**.

## Remaining work

The next planned Teams capability is PR 10 tool-approval parity and its tenant
matrix. The mention-only, continuation, and root-isolation behaviour matrix
also remains unproven for the Threads layout.
