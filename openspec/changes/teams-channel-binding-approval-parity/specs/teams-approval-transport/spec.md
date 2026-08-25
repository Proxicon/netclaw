## Purpose

Define how Teams presents and protects a session-owned approval without turning
an Adaptive Card lifetime or callback into a separate authorization policy.

## ADDED Requirements

### Requirement: Teams card state is opaque transport state

Teams SHALL persist only the bounded correlation, nonce hash, prompt locator,
offered option keys, and terminal presentation data needed to bind and replay
protect a card action. It SHALL not persist a raw nonce, card JSON, raw tool
arguments, token, SDK object, or policy/grant decision. The session journal
remains authoritative for whether an approval call is pending and for its
selected option semantics.

#### Scenario: Valid card action forwards an exact session option

- **GIVEN** a current card action with valid opaque binding and requester
- **WHEN** the requester selects an offered option
- **THEN** Teams forwards that exact option key to the shared session response path
- **AND** Teams does not transform the option or write a policy or grant

### Requirement: Card-token expiry reissues presentation without deciding approval

Teams card nonce expiry SHALL invalidate only that card callback. When the
session still has a pending approval, Teams SHALL produce a fresh bounded card
binding that can be presented deterministically. Expiry SHALL NOT send a
synthetic `Deny`, resolve the session wait, or execute the tool. A stale card
SHALL never authorize after reissue.

#### Scenario: Expired card leaves the session pending

- **GIVEN** a Teams approval card whose nonce has expired while its session call remains pending
- **WHEN** the requester submits the expired action
- **THEN** Teams returns an expired presentation result and issues a fresh valid card binding
- **AND** no `ToolInteractionResponse` is sent for the expired action
- **AND** the session remains paused pending an explicit decision

#### Scenario: Reissued card resolves exactly once

- **GIVEN** an expired card has been reissued for a still-pending session call
- **WHEN** the requester submits a valid option on the replacement card
- **THEN** the session receives the exact selected option once
- **AND** an explicit deny releases the session wait without tool execution
- **AND** duplicate old or new callbacks cannot execute the tool a second time

### Requirement: Card presentation failure is visible without changing session state

Teams SHALL report card delivery failure through normal delivery observability.
It SHALL leave the session-owned approval pending and SHALL not manufacture a
deny merely because a particular card presentation fails.

#### Scenario: Approval card delivery fails

- **GIVEN** a pending session approval and a failed Teams card delivery
- **WHEN** Teams records the transport result
- **THEN** the failure is observable through channel delivery telemetry or feedback
- **AND** the session approval remains pending until an explicit session decision
