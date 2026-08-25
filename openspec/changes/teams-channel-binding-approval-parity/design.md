## Context

See [proposal.md](proposal.md) for motivation and the local parity audit in
`docs/teams/channel-binding-approval-parity-audit.md`. Teams has a durable
binding actor with SDK-free input contracts and its own card replay state.
Slack, Discord, and Mattermost instead share approval response, output, and
transport-failure lifecycle components.

The session journal owns pending approval state and intentionally waits without
a timer. Teams currently introduces a 15-minute card lifetime that records an
`expired` transport decision and forwards `Deny`; that violates the session
contract. Teams also currently bypasses the shared prompt classifier.

## Goals / Non-Goals

**Goals:**

- Make generic session approval authority and Team shell eligibility explicit.
- Apply common approval response and delivery contracts to Teams without
  weakening card callback validation or replay protection.
- Preserve backwards-readable Teams persistence while adding only bounded
  transport metadata needed for card replacement.
- Add cross-channel and focused replay/expiry/classification test evidence.

**Non-Goals:**

- No Microsoft Graph reads, history hydration capability, or tenant permission
  change.
- No Teams-specific policy or grant store and no mutation of operator config.
- No live tenant validation, deployment, upstream push, merge, or automerge.
- No replacement of Teams proactive delivery persistence or channel routing.

## Decisions

### Generic Team shell gate lives in ToolAccessPolicy

`ToolAccessPolicy` will determine whether a Team shell invocation is eligible
before it analyzes or prompts. The predicate requires HostAllowed, an explicit
Team `AllowedTools` entry, an exact `shell_execute = Approval` override, and an
available interactive-approval capability. The existing Personal path remains
unchanged.

This is evaluated from audience policy and the generic run scope. It does not
inspect `ChannelType.Teams`, because channel type is neither a policy input nor
proof that interaction is available. An alternative of enabling shell inside
the Teams adapter was rejected because it would create a policy bypass and make
another Team-capable channel behave differently.

### Teams uses shared classifiers and response lifecycle

`TeamsConversationDependencies` gains an additive detector property. The
binding turns inbound handling asynchronous and calls `PromptClassifier` after
ACL/mention acceptance but before durable reservation and pipeline enqueue.
Safe input keeps the already-resolved Teams trust context. Blocked or
unavailable classification does not reserve an activity or create a model turn.

The Teams actor continues to validate opaque correlation, nonce, destination,
prompt locator, requester, and offered option before it invokes the shared
response path. The shared path then owns the session feedback round trip and
whether the response is accepted. This preserves actor serialization rather
than adding locks or a second mutable authorization store.

### Expiry replaces a card binding, not the session decision

When a callback reaches an expired card, Teams persists a replacement nonce
hash before it returns the expiry result. It does not persist an approval
decision and does not forward a `ToolInteractionResponse`. The old card cannot
match the replacement hash. A valid action on the new card uses the normal
consume-once flow.

The existing persistence records are read compatibly. Newly written pending
state remains bounded; snapshots continue to capture the current transport
binding. A replacement does not need raw nonce persistence, only the hash and
new delivery locator after a successful send.

### Delivery failure follows the common escalation contract

Normal Teams text output and approval-card delivery use the same telemetry and
session failure feedback semantics as `SafeTransportCall`; a failure in the
feedback pipe is allowed to escape the actor so supervision can recreate its
pipeline. Typing remains a best-effort effect because it is not user content.

## Risks / Trade-offs

- [Legacy Teams journals lack enough presentation metadata] -> continue to
  render deterministic unavailable terminal cards and never map legacy tokens.
- [Replacement-card delivery fails] -> keep the session approval pending,
  surface the transport failure, and allow a later valid presentation attempt;
  never synthesize a denial.
- [Actor restart occurs after transport consume but before session response] ->
  preserve consume-once safety and let the session's journal reject stale or
  replayed feedback deterministically.
- [Tests formerly build dependencies without a detector] -> test helpers
  explicitly supply a safe detector; a missing production detector is treated
  as unavailable and fails closed.

## Migration Plan

1. Deploy code that reads existing Teams records and writes the bounded updated
   state.
2. Existing active cards with a legacy offered-key record remain unavailable;
   new session requests render a new card under the new binding behavior.
3. Rollback leaves existing generic session state untouched. The old binary
   reads the same existing Teams transport records; no external migration or
   database rewrite is required.
