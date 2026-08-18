## Delivery rule

Each numbered PR is a hard stop. At its boundary, report exact files changed,
exact commands and focused/broader test results, deviations, remaining gates,
and known risks. Do not begin the next PR automatically.

## PR 0 — OpenSpec amendments and compatibility records only

**Objective:** separate proven offline compatibility from tenant evidence and
make release-blocking security/ownership rules normative. **Areas:** this change
folder only. **Focused tests:** strict OpenSpec validation and documentation
lint/format commands required by contributor guidance. **Acceptance:** all
amendments validate; no implementation code changes. **Tenant prerequisite:**
none. **Rollback:** discard this uncommitted OpenSpec change.

- [x] 0.1 Complete the offline Microsoft Teams SDK compatibility spike: restore and
  publish Microsoft.Teams.Plugins.AspNetCore 2.0.9 on net10.0 for linux-x64;
  verify AddTeams/UseTeams hosting APIs, activity contracts, reply/update
  client APIs, and Adaptive Card action types; record the exact results and
  unauthenticated-mode guard in design.md.
- [ ] 0.2 Complete the opt-in tenant-backed transport spike: validate authenticated
  inbound activity, personal reply, channel root/reply identifiers, Adaptive
  Card action round-trip, proactive send, update semantics, and supported
  attachment authorization using a locally configured test application and
  public HTTPS endpoint. Record sanitized fixtures and all deviations in
  design.md. Evidence recorded 2026-08-02: authenticated personal ingress and
  durable duplicate suppression; channel root/reply `;messageid=` mapping;
  structured mention identity and span removal; update/delete identity;
  Graph-free attachment rejection; SDK send, card action plus terminal response,
  channel-thread proactive reply, and SDK create/update. Unsupported with
  evidence: current file attachment shell requires Graph-backed retrieval.
  Still unproven: personal SDK reply, personal proactive delivery, and a
  structured bot-plus-user mention entity; therefore this task remains open.
  Sanitized fixtures:
  `src/Netclaw.Daemon.Tests/Fixtures/Teams/TenantEvidence/`.
  PR 4 prerequisite subset is complete: canonical channel root/reply identity,
  qualified bot mention identity/span shapes, update/delete identity, and
  Graph-free attachment rejection. PR 5 still requires personal reply evidence;
  PR 8 still requires personal proactive evidence. Structured non-bot mention
  entities remain unproven and are not used to authorize bot mention removal.
- [x] 0.3 Amend proposal, design, tasks, and capability specs for ownership,
  strict endpoint registration, ACL, secret protection, approval recovery,
  attachment/output gates, and classified proactive delivery.

## PR 1 — Project skeleton and transport-independent foundation

**Objective:** create the disabled Teams capability without ingress or transport
activation. **Areas:** `src/Netclaw.Channels.Teams`, package props, solution,
`ChannelType`, daemon registration/diagnostics, configuration/schema, actor and
channel tests. **Focused tests:** options/schema/secret-nondisclosure,
descriptor-disabled, exhaustive enum switch, identifier/contract tests.
**Acceptance:** disabled configuration has no Teams side effects; codec is
canonical, bounded, tenant-isolated, and no SDK type crosses the contract.
**Tenant prerequisite:** completed offline spike only. **Rollback:** remove the
isolated project/config support or leave `Teams.Enabled=false`.

- [x] 1.1 Add `Netclaw.Channels.Teams`, central Teams SDK 2.0.9 package pinning,
  solution/project references, `ChannelType.Teams`, exhaustive switch updates,
  and disabled descriptor/diagnostic integration.
- [x] 1.2 Add credential-mode-aware `TeamsChannelOptions`, secret-only
  ClientSecret binding, JSON schema, and tests proving configuration/doctor/
  diagnostics never disclose ClientSecret.
- [x] 1.3 Add Teams identifier codec and immutable ingress/outbound contracts;
  test canonical unpadded base64url, exact two-segment round trips, scope/value/
  length rejection, root separation, tenant isolation, and restart stability.

## PR 2 — Strict endpoint registration, translation, and ingress routing

**Objective:** admit only complete configured Teams SDK ingress and translate
validated activities without durable duplicate ownership at ingress. **Areas:**
daemon endpoint/DI, `Transport`, translator, ingress actor, TestServer tests.
**Focused tests:** disabled/missing TenantId/missing ClientId/missing ClientSecret
route absence; complete-shaped config route registered once; conflicting route
fails; body/rate limit; malformed fixtures; in-memory ingress dedupe. **Acceptance:**
Netclaw cannot enter the SDK unauthenticated mode and malformed input creates no
conversation/binding actor. **Tenant prerequisite:** offline only; no claim of
live token validation. **Rollback:** disable Teams removes endpoint after restart.

- [x] 2.1 Implement strict credential-mode-aware endpoint registration and
  contained configuration health failure before `AddTeams`/`UseTeams`.
- [x] 2.2 Implement SDK translator and sanitized offline fixtures with complete
  immutable trust context and no synthetic tenant/activity identities.
- [x] 2.3 Implement `TeamsIngressActor` as a bounded in-memory fast-path router
  only; no durable duplicate or activity-root persistence.

## PR 3 — Personal conversation and binding route

**Objective:** route allowed personal messages through durable bindings.
**Areas:** conversation/binding actors and pipeline integration; outbound
delivery remains deferred.
**Focused tests:** deterministic personal session, processed-ID persistence
before dispatch, restart/passivation, and no outbound delivery.
**Acceptance:** personal sessions are stable and default-deny. **Tenant
prerequisite:** personal reply observation remains tenant smoke-gated. **Rollback:**
disable Teams; endpoint may remain separately testable but performs no dispatch.

- [x] 3.1 Implement personal conversation and binding actors with binding-owned
  durable processed-ID dedupe and pipeline dispatch without outbound delivery.

## PR 4 — Channel routing, mention ACL, and activity-root index

**Objective:** add standard channel traffic safely. **Areas:** conversation actor,
root index, ACL/audience/mention policies, channel tests. **Focused tests:** all
team/channel/user/tenant/mention matrix cases, root-index retention, unknown
edit/delete, no-actor denied/ignored paths. **Acceptance:** only mentioned,
fully allowed traffic reaches the model; conversation actor exclusively owns the
durable edit/delete activity-root mapping. **Tenant prerequisite:** canonical
root/reply derivation must be confirmed by 0.2 before final behavior ships.
**Rollback:** retain personal-only support or disable Teams.

- [x] 4.1 Implement fail-closed Teams ACL/audience/mention policy and
  distinguish rejected from ignored outcomes.
- [x] 4.2 Implement conversation-owned bounded persisted activity-root/session
  index and edit/delete routing without LLM dispatch for unknown mappings.

## PR 5 — Safe reply rendering and bounded output

**Objective:** provide correlated, ordered non-streaming output. **Areas:**
renderer, chunker, reply client, output tests. **Focused tests:** Unicode,
card-overhead, exact/one-byte-over boundary, ordering, cancellation, update
failure, bounded final-reply retry. **Acceptance:** no fragment spam, lost or
duplicated text, or unbounded duplicate final messages. **Tenant prerequisite:**
payload ceiling and update semantics remain 0.2 gates; use no undocumented
constant. **Rollback:** disable processing updates and retain basic replies only
when the verified transport behavior permits it.

- [x] 5.1 Implement constrained Markdown rendering, serialized-byte chunking,
  processing-message updates, and transport-owned reply/update operations.

## PR 6 — Persisted Adaptive Card approvals

**Objective:** deliver replay-safe tool approvals across restarts. **Areas:**
approval protobuf/persistence, binding actor, card codec/builder, tests.
**Focused tests:** serialization compatibility, valid/replay/forged/expired/
wrong-context/completed/passivated/restarted flows and consume/update interruption.
**Acceptance:** binding serializes consume-before-forward and card update failure
cannot repeat a tool decision. **Tenant prerequisite:** card round-trip and
terminal update behavior must be observed in 0.2 before production release.
**Rollback:** disable Teams approval capability; do not silently use text fallback.

- [x] 6.1 Extend pending approval persistence with versioned nonce/expiry and
  stable protobuf compatibility behavior.
- [x] 6.2 Implement card action decoding, binding rehydration, consume-once
  validation, terminal result rendering, and presentation-failure telemetry.

## PR 7 — Tenant-spike-approved attachment ingress

**Objective:** stage only attachment shapes demonstrated by tenant evidence.
**Areas:** Teams attachment translator/validation and shared attachment pipeline.
**Focused tests:** MIME/size/trust/redirect/timeout/duplicate/cancellation/cleanup
plus Graph-backed rejection. **Acceptance:** no documentation-only download
assumption, no Graph fallback, no Teams-specific size knob. **Tenant prerequisite:**
0.2 sanitized attachment fixture and authorization proof. **Rollback:** disable
Teams file ingress independently.

- [x] 7.1 Implement only spike-approved attachment shapes through shared staging;
  reject Adaptive Cards, content links, and SharePoint/OneDrive as
  `graph_backed_attachment_unsupported` before model dispatch when required.

## PR 8 — Persisted destinations and proactive delivery

**Objective:** deliver reminders with honest durable state. **Areas:** destination
registry/resolver, reminder integration, proactive client, delivery persistence.
**Focused tests:** known/unknown targets, state transitions, retry classification,
permanent invalidation, recovery of `DeliveryUnknown`. **Acceptance:** never
claims external exactly-once delivery; `DeliveryUnknown` policy is explicitly
chosen and exposed to operators. **Tenant prerequisite:** proactive address
construction and send behavior from 0.2. **Rollback:** disable Teams reminder
targets while preserving evidence.

- [x] 8.1 Implement tenant-bound persisted destinations and current-session/
  explicit target resolution without Graph discovery.
- [x] 8.2 Implement `Pending`/`Sending`/`Sent`/`FailedRetryable`/
  `FailedPermanent`/`DeliveryUnknown` state transitions and choose/document the
  explicit `DeliveryUnknown` recovery policy.
- [x] 8.3 Complete the CI-gated live channel proactive matrix. Personal
  capture, delivery, and restart recovery passed. PR #16 added the structured
  exact Team audience override required for canonical identifiers containing
  configuration delimiters and merged after all required CI checks passed. A
  fresh owner-only live run then proved personal and channel-root replies,
  same-root reminder creation through generic `set_reminder` plus
  `current_session`, proactive delivery, restart recovery, no resend after a
  second restart, and isolation of a second reminder to its distinct root.
  Tenant, ACL, mention, trust, Public fallback, and destination policy remained
  unchanged. The final real-upload smoke failed closed-policy validation: the
  activity produced normal processing and a model reply rather than an
  attachment rejection. On 2026-08-13, one owner-only diagnostic capture found
  a nonempty JSON-string `text/html` attachment with a non-Graph embedded
  reference and channel data. No identifiers, URLs, file data, or message data
  were retained. The daemon and tunnel stopped immediately. A sanitized
  regression fixture and a narrow pre-routing correction now reject that shape.
  Keep 8.3 open and stop packaging or release progression until the merged
  correction passes one real-upload smoke with no Processing or model reply.

## PR 9 — App package and operations

**Objective:** provide a deployable, secure operator path. **Areas:**
`deploy/teams`, runbooks, daemon CLI/help/diagnostics, system operations skill.
**Focused tests:** manifest validation and documentation review. **Acceptance:**
no Graph/group/meeting/tab capability and no secret disclosure. **Tenant
prerequisite:** final manifest/runtime details that depend on 0.2 observations.
**Rollback:** withdraw package; `Teams.Enabled=false` removes ingress on restart.

- [x] 9.1 Add manifest/assets/runbook covering registration, secret rotation,
  tunnel, sideloading, production publication, health, and rollback.

## PR 10 — Hardening and tenant smoke

**Objective:** production readiness. **Areas:** rate limits, bounded ingress,
telemetry/health, offline integration suite, opt-in tenant smoke, operations
skill/OpenSpec updates. **Focused tests:** full offline matrix, opt-in live smoke,
slopwatch, headers, diff check, strict OpenSpec validation. **Acceptance:** all
tenant gates are evidenced or the feature stays disabled/non-released. **Tenant
prerequisite:** complete configured test app and public HTTPS endpoint. **Rollback:**
`Teams.Enabled=false`; binary rollback only where serialization tests prove it.

- [ ] 10.1 Add rate limiting, bounded ingress, telemetry, contained health, and
  offline daemon/TestServer integration coverage.
- [ ] 10.2 Add secret-gated tenant smoke fixtures and document invocation without
  making normal CI tenant-dependent.
- [ ] 10.3 Run all applicable quality gates and update the operations system skill
  and final OpenSpec artifacts.
