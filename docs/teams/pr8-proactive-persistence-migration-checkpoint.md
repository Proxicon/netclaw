<!--
  Copyright (C) 2026 Petabridge, LLC
-->
# PR 8 proactive-persistence migration checkpoint

## Purpose and status

This is a checkpoint for the PR 8 proactive-reminder architecture correction.
The merged-dev architecture gate and personal live matrix passed. Channel-root
live validation exposed a translation drift and remains pending the focused
correction's CI and live rerun.

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

## Live channel-root translation drift and correction

The initial dedicated live validation established personal destination capture,
one-time proactive delivery, and restart recovery. A bot-mentioned channel root
authenticated and reached the daemon but was rejected as
`unsupported_attachment_shape` before routing or reply generation.

The saved tenant fixtures covered channel roots and HTML rendering wrappers
independently, but not their combined current SDK representation. A new
sanitized fixture captures the structural distinction: a channel-root activity
can carry a scalar nonempty HTML rendering wrapper with a standard UTF-8
charset parameter. It is transport rendering metadata, not a model-visible
attachment, because it has no name, content URL, or embedded reference; the
canonical activity text remains the only model-visible text.

The classifier now accepts only `text/html` with no parameters or with UTF-8
charset parameters, in addition to the pre-existing scalar/no-name/no-URL/
no-reference requirements. Unknown parameters, structured content, names,
URLs, embedded references, file-download-info, Graph, SharePoint, OneDrive,
empty upload shells, and mixed unsupported attachments remain fail-closed.

The test project now links directly to the OpenSpec fixture directory and
requires an explicit matrix entry for every JSON fixture. Focused tenant-
evidence and Teams foundation coverage passed after the correction. The live
channel root, proactive delivery, second-root isolation, and attachment smoke
remain pending until the focused correction passes CI and is merged.

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

- The focused channel-root translation correction must pass CI and merge to
  `dev`.
- The blocked channel-root, channel proactive, second-root, negative, and
  attachment tenant matrix must pass without weakening the attachment or ACL
  policy.

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
