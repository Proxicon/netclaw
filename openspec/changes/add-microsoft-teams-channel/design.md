## Context

See `proposal.md` for motivation and the change specs for behavior. Netclaw
already has remote-chat descriptors, actor-owned session bindings, durable
session state, channel output renderers, reminder target resolvers, and shared
attachment staging. Discord's gateway hierarchy is reusable at the actor seam,
but Teams sends authenticated HTTPS activities and must not be represented as a
persistent socket transport.

## Goals / Non-Goals

**Goals:**
- Keep Microsoft SDK types at the HTTP transport boundary.
- Reuse Netclaw's session, trust-context, persistence, output, attachment, and
  reminder contracts.
- Make Teams configuration disabled-by-default and fail closed.
- Make activity deduplication, approval correlation, and destination data
  durable across actor passivation and process restart.

**Non-Goals:**
- Group or meeting chat, Graph-backed file retrieval, Graph destination
  discovery, user-delegated actions, SSO, tabs, message extensions, or
  multi-tenant SaaS distribution.
- Cloud registration, tenant approval, or Teams-app provisioning at daemon
  startup.

## Decisions

### Use the Microsoft Teams SDK at the transport edge

Use the current C# Teams SDK package set and hosting route after a disposable
compatibility spike proves net10.0, Linux container, authenticated activity,
card action, update, and proactive-send behavior. The Teams SDK is selected
over Microsoft 365 Agents SDK because this change needs Teams-native mention and
channel-thread behavior. The spike is a hard implementation gate: no fallback
to Bot Framework v4, TeamsFx, or unauthenticated raw HTTP is allowed.

#### Phase 0 SDK compatibility record (2026-08-01)

The disposable net10.0 spike restored and published for `linux-x64` using
NuGet.org and `Microsoft.Teams.Plugins.AspNetCore` **2.0.9**. Its transitive
Teams packages are `Microsoft.Teams.Apps`, `Microsoft.Teams.Api`,
`Microsoft.Teams.Common`, `Microsoft.Teams.Cards`,
`Microsoft.Teams.Extensions.Hosting`, `Microsoft.Teams.Extensions.Configuration`,
and `Microsoft.Teams.Extensions.Logging`, all version 2.0.9. The hosting API
compiled as:

```csharp
using Microsoft.Teams.Plugins.AspNetCore.Extensions;

builder.AddTeams();
app.UseTeams();
```

The SDK exposes `IContext<IActivity>.Send`, `.Reply`, and `.Quote` for inbound
replies, while `ActivityClient.CreateAsync`, `.ReplyAsync`, `.UpdateAsync`, and
`.DeleteAsync` provide destination-addressed replies and updates. Adaptive Card
actions are represented by `AdaptiveCards.ActionActivity`; generic activity,
message update, message delete, and conversation update activity types are also
present. This confirms the transport API shape needed by the planned adapter.

The spike also established a non-negotiable guard: when no `Teams:ClientId` is
configured, SDK 2.0.9 logs that it will accept unauthenticated `/api/messages`
requests. Netclaw's enabled integration registration must therefore reject
missing tenant, client ID, or secret before `AddTeams`/`UseTeams` runs; an
unauthenticated SDK mode is prohibited.

The offline compatibility portion passed. Tenant-authenticated inbound activity,
actual channel root/reply identifiers, card round-trip, proactive delivery,
message update semantics, and attachment download authorization remain
tenant-backed smoke gates. They cannot be claimed from a local process without
an Entra app registration, Teams installation, and public HTTPS tunnel.

Foundation work permitted from the completed offline evidence is limited to the
`Netclaw.Channels.Teams` project, central package pinning, solution/project
references, `ChannelType.Teams` and exhaustive switch updates, disabled
descriptor/diagnostics, Teams options/schema, identifier codec, Netclaw-owned
immutable contracts, strict endpoint-registration guard, and offline
translator fixtures/tests. The following remain tenant-backed gates: canonical
channel root/reply derivation, proactive destination/address construction,
Adaptive Card round-trip and terminal update behavior, message update semantics,
authenticated attachment download shapes, serialized payload ceiling, and final
manifest/runtime assumptions based on observed Teams behavior.

### Translate at ingress and route through a non-transport actor parent

The daemon's authenticated `/api/messages` handler invokes
`TeamsSdkActivityTranslator`, which emits immutable Netclaw-owned ingress
records. `TeamsIngressActor` is a process-local router with only a bounded
in-memory duplicate fast-path and canonical conversation-child lookup; it owns
no durable activity state or socket lifecycle. `TeamsConversationActor` is one
actor per canonical Teams conversation. It owns deterministic binding-child
resolution and the bounded, oldest-first persisted activity-ID-to-root/session
mapping. This ownership is required because an edit or delete arrives with an
activity ID and must locate a root/session before a binding actor can be
selected. `TeamsSessionBindingActor` is one actor per personal conversation or
channel root thread. It persists processed activity IDs before pipeline
dispatch, pending approval state/nonce/expiry, output/card locators, the
approved outbound destination, and session-specific reminder idempotency state;
it alone drives `ISessionPipeline` and the Teams reply contracts.

Alternative: call the pipeline directly from the HTTP handler. Rejected because
it bypasses actor serialization, session ownership, passivation behavior, and
the existing channel contract.

#### PR 2 ingress correction record (2026-08-01)

The authenticated tenant presented by the SDK is an explicit Netclaw boundary:
it must be nonblank and ordinal-equal to configured `Teams:TenantId`; an
optional activity conversation tenant must equal that authenticated tenant.
Mismatches are rejected with safe reason codes before the ingress actor.

PR 2's duplicate cache records an activity only after the next actor boundary
reports acceptance. Failures, cancellations, and unavailable/deferred outcomes
remain retryable; the cache is process-local and bounded, not durable.
Until PR 3 supplies the conversation/binding owner, the deferred boundary
reports unavailable and the connector remains degraded/not-ready. It does not
claim successful routing or session dispatch.

`ReceivedAtUtc` is daemon receipt time from `TimeProvider`. A separate optional
SDK-free `PlatformTimestampUtc` preserves the platform event time without
letting it redefine local receipt ordering. The translator's public/untrusted
trust context is provisional transport authentication only; PR 3/4 must derive
a final ACL/audience context before any pipeline dispatch.

#### PR 3 personal-binding durability record (2026-08-01)

PR 3 enables only personal activities. `TeamsIngressActor` delegates to the
real conversation sink only after transport validation. The sink and
`TeamsConversationActor` reject every non-personal activity before actor
creation, and final personal ACL evaluation is default-deny: direct messages
must be enabled, the configured tenant must match ordinally, and the sender
must exactly match a non-empty `AllowedUserIds` entry. The binding derives the
pipeline `ChannelInput` as `Personal` / `Personal` /
`TrustedInternal` with verified Teams provenance; the provisional PR 2 public
transport classification never reaches the session.

`TeamsSessionBindingActor` is the sole durable processed-activity owner. Its
retention is 1,024 IDs per canonical personal session and evicts the oldest ID
first. It persists a reservation before queue admission. A live pipeline write
failure or caller cancellation persists a release, so that activity can be
retried. After a process crash that occurs after the reservation commits but
before the pipeline accepts the input, recovery suppresses the retry; this is a
documented at-most-once local-admission trade-off, not an exactly-once external
or model-execution claim. The persisted reservation and release records use
new Netclaw protobuf types and do not contain Microsoft SDK types. They store
only fixed activity fingerprints. Ordered snapshots compact the journal after
snapshot success and retain at most 1,024 fingerprints. Older binaries do not
recognize the new durable manifests. They must not run against PR 3 Teams
binding state. Disabling `Teams.Enabled` is safe operational rollback. It does
not establish binary rollback compatibility for stored PR 3 binding state.

PR 3 uses `TeamsSessionIdentifierCodec` as the sole personal session builder;
actor names URI-escape the resulting canonical ID. Bindings may passivate and
recover their durable state. The cached conversation parent remains live so its
name remains a reliable owner for binding-child recreation. Pipeline options
use `OutputFilter.None` and no default delivery target: no Teams reply,
renderer, destination, card, attachment, or proactive operation is introduced
in this PR.

### Preserve the two-segment session grammar

Existing reminder routing parses session IDs as `{channelPart}/{threadPart}` in
`src/Netclaw.Channels/ChannelGatewayActor.cs`; that implementation has no
hard actor-name or persistence-key length constraint today.
Teams stores its tenant/scope/conversation tuple in a slash-free first segment:
`teams~base64url(tenant)~scope~base64url(conversation)`. The second segment is
`conversation` for personal sessions or the base64url root activity ID for
channel sessions. The Teams identifier codec is the sole builder/parser and
rejects blank, malformed, oversized, or ambiguous values.

PR 1 applies a 1 KiB UTF-8 limit to each opaque raw tenant, conversation, and
root activity component (and the corresponding 1,366-character unpadded
base64url limit before decode). `ChannelGatewayActor` has no existing hard
actor-name or persistence-key limit, so these are bounded-resource guards, not
an undocumented Teams platform limit; values reject rather than truncate or
hash.

Alternative: use `teams/{tenant}/{scope}/...`. Rejected because current generic
gateway and reminder code would parse only `teams` as the conversation key.

### Use one authoritative durable owner per datum

`TeamsIngressActor` has no authoritative persistence. `TeamsConversationActor`
durably owns only the bounded activity-to-root/session mapping used before a
binding can be chosen; unknown edits/deletes are acknowledged, logged safely,
and never create an LLM turn. `TeamsSessionBindingActor` owns processed
activity-ID deduplication, approval correlation, output/card locators, approved
destination, and delivery state. Retention is bounded and oldest-first at the
owning actor. Existing pending-approval persistence is extended with stable
protobuf field numbering and backward-readable behavior; removed field numbers
are never reused. Serialization compatibility tests must state whether older
binaries ignore, retain, or reject new fields before binary rollback is claimed.

### Enforce authorization before routing

The endpoint relies on Teams SDK authentication, then translation records the
validated tenant and activity metadata. Actor policy checks enabled state,
tenant, supported scope, team/channel, user, mention, payload, and audience in
that order. Personal direct messages require both explicit enablement and an
explicit user allow-list entry; no empty-list implicit allow is permitted.

### Keep outbound and proactive APIs transport-owned

`ITeamsReplyClient` and `ITeamsProactiveClient` expose Netclaw-owned message
and destination records. SDK clients implement them in `Transport/`. A single
processing message may be updated; final-update failure sends exactly one
correlated final reply. The output chunker measures serialized payload bytes,
not character count.

Proactive delivery only targets an address captured from a prior authenticated
allowed activity. It persists no access token and performs no Graph discovery.
The durable delivery states are `Pending`, `Sending`, `Sent`,
`FailedRetryable`, `FailedPermanent`, and `DeliveryUnknown`. The default
recovery policy for `DeliveryUnknown` is an explicit pre-implementation decision
gate: it must be retry-with-operator-visibility or operator-review-only, never
an implicit retry or false success claim.

### Keep credentials secret in every surface

`ClientSecret` may participate in effective runtime binding for ClientSecret
mode but must never appear in normal configuration serialization, generated
sample configuration, schema examples, doctor output, descriptors, diagnostics,
health detail, telemetry, structured logs, exception messages, test snapshots,
OpenSpec fixtures, or committed configuration. Tests must cover the actual
remote-chat configuration and diagnostic surfaces used by the daemon.

## Risks / Trade-offs

- [Teams C# API differs from current documentation] → Phase 0 compiles and
  exercises the generated template before repository dependencies are pinned.
- [Public endpoint increases attack surface] → register only when enabled, use
  SDK token validation, endpoint size/rate limits, and no request-body logging.
- [Teams reply/thread fields vary by activity type] → maintain sanitized fixture
  captures and reject missing canonical identifiers rather than synthesizing.
- [Restarted cards can outlive pending state] → nonce/expiry validation returns
  a deterministic terminal card result.
- [Teams file URLs can require Graph or channel credentials] → support only
  spike-validated download shapes; deny other forms without hidden fallback.
- [Reminder retries duplicate user-visible posts] → persist delivery state and
  represent interrupted requests as `DeliveryUnknown`, not exactly-once.

## Migration Plan

1. Land the disabled project/config/schema and descriptor first; existing
   channel behavior and persisted session IDs remain unchanged.
2. Register the endpoint and runtime dependencies only for enabled Teams.
3. Release personal, channel, approval, attachments, and reminders in separate
   PRs behind the same `Teams.Enabled` gate.
4. Setting `Teams.Enabled=false` is the primary operational rollback and
   removes Teams ingress after process restart while retaining persisted Teams
   state for diagnosis. Binary rollback across persistence-schema changes is
   supported only where serialization compatibility tests prove the target older
   version tolerates the newer stored state.
5. Do not archive the OpenSpec change until offline tests, opt-in tenant smoke,
   runbook, and schema/doctor contracts pass.
