<!--
  Copyright (C) 2026 Petabridge, LLC
-->
# PR 8 proactive-persistence migration checkpoint

## Purpose and status

This is a checkpoint for the PR 8 proactive-reminder architecture correction.
Implementation is incomplete but coherent. Live validation has not started.

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
Session output carries reminder correlation, and the daemon no longer caches an
SDK request context. The Teams resolver foundation fails closed.

## Responsibility map

- Generic compatibility: `Netclaw.Actors` legacy envelope, legacy protobuf
  definitions, and decode-only manifests.
- Teams active state: `Netclaw.Channels.Teams` persistence contracts, serializer,
  recovery conversion, binding state, channel index, and resolver.
- Daemon boundary: Teams SDK send uses the app-level sender only.
- Tests: Actors serialization and Teams routing/persistence coverage.

## Legacy migration table

| Legacy manifests | Decode owner | v2 write path | Migration behavior | Removal condition |
| --- | --- | --- | --- | --- |
| `tapc-v1`, `tacd-v1`, `taco-v1` | generic envelope, Teams binding actor | Teams approval v2 manifests | next binding snapshot is v2 | legacy persistence retention window ends |
| `tpdc-v1`, `tpdi-v1`, `tpdr-v1` | generic envelope, Teams binding actor | Teams destination/delivery v2 manifests | next binding snapshot is v2 | legacy persistence retention window ends |
| `dads-v1` with Teams fields | generic envelope, Teams binding actor | `teams-binding-snapshot-v2` | converted during recovery | legacy persistence retention window ends |
| `dtcam-v1`, `dtcais-v1` | generic envelope, Teams channel actor | Teams channel map/snapshot v2 manifests | next channel snapshot is v2 | legacy persistence retention window ends |

## Proven checkpoint validation

- Focused Teams routing/persistence tests: 27 passed.
- Focused Actors serialization tests: 42 passed.
- Fresh checkpoint builds passed with zero warnings and errors: Actors, Teams,
  Daemon, and Daemon.Tests. Rerun the documented full gate before merge.
- `dotnet slopwatch analyze`: 0 issues.
- `git diff --check`: passed.

## Known incomplete gates

- Actor-integrated known-destination resolution and zero/one/multiple behavior.
- Authoritative migration and proactive diagnostics.
- End-to-end request-context disposal proof.
- Legacy journal/snapshot migration, failure/retry, crash, concurrency, and
  snapshot-compaction matrices.
- OpenSpec completion, full channel/reminder regressions, and architecture audit.
- Push CI and dedicated personal/channel/negative live validation.

## Resume order

1. Read this checkpoint and inspect the branch diff.
2. Do not redesign or repeat completed migration work.
3. Complete resolver integration and tests.
4. Complete authoritative diagnostics and tests.
5. Complete request-context disposal proof.
6. Complete migration, crash, concurrency, and snapshot matrices.
7. Complete OpenSpec.
8. Run full regressions and architecture audit.
9. Push corrections and wait for CI.
10. Run dedicated live proactive validation only after the architecture gate passes.

## Prohibited shortcuts

- Do not relocate serializers without legacy decoding.
- Do not write legacy manifests or add Teams persistence to generic Actors.
- Do not alter persisted test state to force recovery or add a test-only reminder
  endpoint.
- Do not perform live validation before the architecture gate passes.
