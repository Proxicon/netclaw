## Purpose

Provide authenticated, replay-safe Teams Adaptive Card approvals that survive
binding passivation and daemon restart without silently authorizing tool calls.

## ADDED Requirements

### Requirement: Teams approval actions are bound to persisted pending state

An Adaptive Card payload SHALL be correlation data, not authorization. The
authoritative binding actor SHALL validate its version, tenant, conversation,
submitting sender/requester policy, session ID, call ID, selected option, nonce,
expiry, current pending status, and consume state against persisted pending
state. The binding actor SHALL serialize a consume-once state transition before
forwarding `ToolInteractionResponse`. That transition is atomic only within the
existing actor/persistence boundary; no wider distributed atomicity is claimed.

#### Scenario: Valid requester approval is consumed once

- **WHEN** the original requester submits a current valid approval action
- **THEN** the authoritative binding consumes the pending request before forwarding the selected decision
- **AND** the selected decision reaches the session once

#### Scenario: Forged or replayed action is rejected

- **WHEN** an action has an invalid version, nonce, sender, tenant, conversation,
  call, option, expiry, pending state, or consume state
- **THEN** it is rejected without changing tool approval state

### Requirement: Approval actions recover deterministically

The Teams channel SHALL rehydrate the same binding for a valid still-pending
approval action after passivation or daemon restart. Expired, unavailable, or
already-completed state SHALL produce a deterministic terminal result and SHALL
never be silently dropped. A terminal card-update failure is an outbound
presentation failure only; it SHALL NOT change an approval result already
determined by persisted state.

#### Scenario: Action after daemon restart

- **WHEN** a user submits a valid still-pending card action after daemon restart
- **THEN** the binding rehydrates and processes the action

#### Scenario: Interruption after consume

- **WHEN** the pending state was consumed and the process is interrupted before the visible card update is confirmed
- **THEN** recovery does not execute the tool decision twice
- **AND** a later card presentation retry cannot reopen or reconsume the decision

#### Scenario: Action after completion

- **WHEN** a user submits a card action after its request has completed
- **THEN** the card action receives a terminal expired or completed result
