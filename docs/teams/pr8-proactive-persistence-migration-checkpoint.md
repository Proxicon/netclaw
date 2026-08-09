<!--
  Copyright (C) 2026 Petabridge, LLC
-->
# PR 8 proactive-persistence migration checkpoint

## Purpose and status

This is a checkpoint for the PR 8 proactive-reminder architecture correction.
The automated architecture gate and merged-dev CI passed. Live validation
established the personal proactive path, but exposed a channel-root ingress
defect; release progression is stopped pending a fix.

- Branch: `feature/teams-channel-pr8-proactive-reminders`
- Pre-checkpoint head: `4baf137c`
- Safety reference: `backup/teams-pr8-before-quality-review`

## Why the rework was required

Teams persistence had leaked into generic Actors. A direct move would make
existing journals and snapshots unreadable. The prior design also had a
permanent-invalidation crash gap, no destination generation, one mutable
delivery correlation, no known-destination resolver, non-authoritative
diagnostics, and no request-context disposal proof.

## Implemented architecture

New Teams records, snapshots, protobuf schema, and serializer are Teams-owned.
The generic serializer reads legacy Teams manifests into a decode-only envelope;
the Teams recovery actors convert that envelope and write only v2 snapshots.
Destination generation is retained per delivery. A permanent delivery failure
records its terminal state and matching-generation invalidation in one v2 event.
Session output carries immutable per-delivery correlation. The daemon no longer
caches an SDK request context. The binding actor resolves its own current or
explicit canonical destination. It fails closed for unavailable and
cross-session destinations. Actor-owned diagnostics expose safe bounded state.

## Responsibility map

- Generic compatibility: `Netclaw.Actors` legacy envelope, legacy protobuf
  definitions, and decode-only manifests.
- Teams active state: `Netclaw.Channels.Teams` persistence contracts, serializer,
  recovery conversion, binding state, channel index, and resolver.
- Daemon boundary: Teams SDK send uses the app-level sender only.
- Tests: Actors serialization and Teams routing/persistence coverage.

## Legacy migration table

| Legacy manifest/type | Legacy payload shape | Decode owner | Teams v2 write path | Conversion and trigger | Unsupported/lossy behavior |
| --- | --- | --- | --- | --- | --- |
| `tapc-v1`, `tacd-v1`, `taco-v1` | approval pending, card locator, terminal decision | generic `LegacyChannelPersistenceEnvelope`; binding actor | approval v2 records and `teams-binding-snapshot-v2` | `ApplyLegacyPersistence`; recovery completion saves a v2 snapshot | invalid approval values fail recovery |
| `tpdc-v1`, `tpdi-v1`, `tpdr-v1` | destination capture, invalidation, delivery state | generic envelope; binding actor | destination/delivery v2 records and binding snapshot | `ApplyLegacyDestination` and `ApplyProactiveDeliveryRecorded`; recovery completion saves v2 | no destination values are invented; invalid or mismatched destinations fail recovery |
| `dads-v1` with Teams fields | activity fingerprints, approvals, destination, deliveries | generic envelope; binding actor | `teams-binding-snapshot-v2` | `ApplyLegacySnapshot`; recovery completion saves v2 | a payload without Teams fields remains the generic activity snapshot; invalid Teams fields fail recovery |
| `dtcam-v1`, `dtcais-v1` | channel activity map or index entries | generic envelope; channel conversation actor | channel map/snapshot v2 | `ApplyLegacyPersistence`; recovery completion saves v2 | invalid fingerprints or canonical channel sessions fail recovery |

## Migration and restart proof

The deterministic migration matrix is complete in
`TeamsPersonalRoutingTests`. It builds historical protobuf payloads with the
real legacy manifests, decodes them through `NetclawProtobufSerializer`, and
seeds the persistence test journal or snapshot store at explicit sequence
numbers. No fixture constructs a pre-converted v2 record.

- Journal-only legacy state recovers approval, destination, delivery, and
  routing state; the generated binding snapshot is `TeamsBindingSnapshot` v1;
  a restart suppresses the already routed activity.
- Legacy snapshot only, snapshot plus later legacy events, and legacy plus
  later v2 events all recover in order, compact to a Teams-owned v2 snapshot,
  and restart from that snapshot.
- Legacy channel index records compact to `TeamsChannelActivityIndexSnapshot`
  and survive restart.
- Forced migration snapshot failures leave binding migration health `Failed`.
  Repeated restarts retry from retained legacy journal records without state
  multiplication. A later successful save retains even a sequence-1 snapshot;
  compaction now deletes older snapshots only when one exists.
- Empty or insufficient legacy destination/snapshot payloads remain
  decode-only envelopes and cannot be emitted by the generic serializer. New
  Teams events and snapshots resolve only to serializer 151 v2 manifests.

## Current local validation

- Full Actors regression: 2,869 passed with no skips.
- Actors reminder, proactive, and serialization regression: 272 passed.
- Full Daemon regression: 1,123 passed with no skips.
- Teams-only Daemon regression: 172 passed.
- All required Actors, Teams, Daemon, and Daemon.Tests builds passed with zero
  warnings and zero errors.
- `dotnet slopwatch analyze`: 0 issues.
- `git diff --check`, copyright-header verification, and strict OpenSpec
  validation passed.

## Dedicated live validation evidence (2026-08-09)

The merged dev build was run with a fresh owner-only state directory, the
existing authorized app identity, least-privilege reminder-only tool access,
and the existing development tunnel. Local and public readiness passed; an
unauthenticated messages request was rejected rather than routed.

- Personal destination capture and the normal personal reply passed.
- A normal one-time personal reminder delivered exactly once after its inbound
  request had completed.
- A second personal reminder survived a clean daemon restart with the same
  isolated state, delivered once, and was not re-queued after a further restart.
- Safe telemetry showed no pending or failed reminders after the confirmed
  personal deliveries and contained no destination values or message payloads.
- The operator-facing workflow does not expose the binding's internal
  proactive-diagnostics query; the bounded health telemetry was used instead.
- Known-destination selection, duplicate/retry injection, permanent
  invalidation, and missing/ambiguous target cases were not run live because
  there is no safe normal operator workflow to induce them. Their automated
  proof remains the authority for this checkpoint.

### Channel-root blocker

A new bot-mentioned channel root reached the live daemon, but safe telemetry
recorded one received event, zero routed events, and zero replies. Translation
rejected the activity as `unsupported_attachment_shape` before channel ACL
evaluation, canonical-root capture, or reply generation. No outbound Teams
operation was attempted. This blocks channel-root capture and proactive
delivery validation and is a release-stopping product defect, not a tunnel or
personal-proactive failure.

The daemon and tunnel were stopped after the failure. The original local state
and configuration were not changed; the isolated evidence state remains
owner-only for diagnosis.

## Proof Pass 4 full-regression and architecture-audit evidence

The branch tip was checked against current `proxicon/dev`. The current dev tip
is an ancestor of this branch. The audit required no rebase.

Generic Actors retains only channel-neutral dispatch, reminder contracts, and a
decode-only legacy envelope. Its serializer cannot emit historical Teams
manifests. Teams owns all active persistence contracts, protobuf definitions,
serializer mappings, recovery conversion, destinations, delivery state, and
binding diagnostics.

The SDK package and SDK types occur only in the daemon Teams transport boundary
and its tests. The Teams project contains no scheduler or timer engine. It
receives generic reminder delivery messages through the registered gateway.
The full Actors regression includes other supported channel behavior.

The audit verified these durable rules:

- A reminder persists `Pending` before it persists `Sending`.
- A captured generation remains bound to its delivery key.
- A changed destination blocks an old-generation completion from delivery.
- A permanent result persists its terminal state and invalidation in one record.
- Recovery changes an uncertain in-flight send to `DeliveryUnknown`.
- Capacity rejects a new delivery key without deleting terminal evidence.
- Diagnostics derive from the binding owner and exclude destination values,
  request state, content, credentials, headers, tokens, and provider exceptions.

## Proof Pass 2 state-machine evidence

Proof Pass 2 establishes the remaining offline state-machine properties without
claiming live Teams validation. Generation one is the first accepted capture;
an identical capture is idempotent and a changed validated destination advances
the generation with checked overflow. Each reservation retains that generation.
Late output for an older generation is recorded only against its original key,
does not post to the refreshed destination, and cannot invalidate the newer
generation.

The `TeamsProactiveDeliveryRecorded` terminal event atomically contains both
`FailedPermanent` and matching-generation invalidation. Recovery before that
event leaves no partial permanent state; recovery after it preserves the
terminal record and removes only the matching destination. Immutable delivery
keys correlate concurrent outputs independently; late terminal completions are
ignored. The binding retains 1,024 delivery records, rejects a new key at
capacity, preserves `Sent` idempotency evidence, and rejects oversized or
future-generation snapshots. Sequence-64 snapshot compaction retains the
destination generation, terminal delivery idempotency record, and durable
duplicate state. An invalidated snapshot retains its last generation so the
next authenticated capture advances rather than reusing terminal history.

## Proof Pass 3 request-independence and diagnostics evidence

The production TestServer route exercises `UseTeams`, SDK request parsing,
translation, `TeamsIngressActorHost`, and durable binding capture. Its test
authorization seam changes only policy validation; it does not bypass the SDK
handler or actor route. After the response and request-scoped sentinel are
disposed, a generic reminder forwarded through the registered Teams gateway
delivers through the app-level reply-client seam using the persisted personal
or canonical channel-root destination. A cancelled HTTP request creates no
destination; later generic delivery fails closed without an outbound call.

`GetTeamsBindingProactiveDiagnostics` is binding-actor-owned and derives only
safe bounded state: feature/migration health, destination and invalidation
presence, pending and terminal retention, retryable/permanent/unknown state,
missing-target state, and capacity pressure. Its single-owner binding model has
no ambiguous current-target state. The DTO never contains destination values,
request values, content, credentials, tokens, headers, or provider exceptions.

## Known incomplete gates

- Channel-root ingress must accept the supported bot-mentioned root activity
  without weakening attachment or ACL policy, then the blocked live channel and
  negative validation matrix must be rerun.

## Resume order

1. Read this checkpoint and inspect the branch diff.
2. Do not redesign or repeat completed migration work.
3. Wait for the required CI workflows after the branch push.
4. Run dedicated live proactive validation only after CI passes.

## Prohibited shortcuts

- Do not relocate serializers without legacy decoding.
- Do not write legacy manifests or add Teams persistence to generic Actors.
- Do not alter persisted test state to force recovery or add a test-only reminder
  endpoint.
- Do not perform live validation before the architecture gate passes.
