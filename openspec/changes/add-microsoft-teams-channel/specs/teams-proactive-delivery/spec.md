## Purpose

Provide restart-safe Teams reminder delivery using previously authenticated
conversation destinations without persisting tokens or requesting Graph access.

## ADDED Requirements

### Requirement: Teams reminder destinations are explicit and validated

The Teams channel SHALL resolve current-session delivery and explicit Teams
conversation, user, or channel targets only when a matching persisted,
tenant-bound destination is known. It SHALL reject unknown or ambiguous targets
without sending a message or performing Graph discovery.

#### Scenario: Known current-session reminder is delivered

- **WHEN** a reminder targets an active or recoverable Teams session
- **THEN** it is delivered through that session's Teams conversation

#### Scenario: Unknown user target is rejected

- **WHEN** a reminder targets a Teams user with no persisted approved personal destination
- **THEN** target resolution fails with an actionable error and no Graph lookup occurs

#### Scenario: A stale destination generation is rejected

- **GIVEN** a reminder reservation records a destination generation
- **WHEN** a later authenticated activity refreshes that destination
- **THEN** output for the old reservation does not post to the refreshed destination
- **AND** the result remains associated with the original delivery identity

### Requirement: Proactive diagnostics use authoritative binding state

The Teams binding actor SHALL provide proactive diagnostics from its recovered
durable state. The diagnostics SHALL contain only health states, migration
states, bounded counts, and safe reason codes. They SHALL NOT contain Teams
identifiers, service URLs, content, credentials, headers, tokens, or provider
exception text.

#### Scenario: Destination data is not disclosed

- **GIVEN** a binding has a captured personal or channel destination
- **WHEN** an operator requests its proactive diagnostics
- **THEN** the response reports availability and bounded counts
- **AND** it does not include destination values

### Requirement: Legacy Teams proactive state migrates fail closed

The Teams channel SHALL decode historical Teams persistence manifests only for
recovery. It SHALL convert valid legacy state to Teams-owned v2 snapshots. A
failed migration snapshot SHALL leave migration recoverable on restart. It
SHALL NOT synthesize a destination from malformed or insufficient legacy state.

#### Scenario: A legacy recovery writes a retained v2 snapshot

- **GIVEN** a binding has valid legacy Teams journal or snapshot state
- **WHEN** the binding recovers
- **THEN** it writes a Teams-owned v2 snapshot before legacy journal compaction
- **AND** a sequence-1 snapshot remains available for the next restart

#### Scenario: Insufficient legacy destination state is rejected

- **GIVEN** a legacy Teams destination omits required identity or service URL data
- **WHEN** the binding recovers
- **THEN** recovery fails closed
- **AND** no destination is fabricated

### Requirement: Proactive delivery state is durable and classified

The Teams channel SHALL persist reminder delivery state before initiating an
outbound send. The state model is `Pending`, `Sending`, `Sent`,
`FailedRetryable`, `FailedPermanent`, and `DeliveryUnknown`. It SHALL classify
transient throttling and server failures separately from permanent credential,
uninstalled-app, authorization, malformed-target, and expired-destination
failures. It SHALL NOT claim exactly-once external delivery without tenant-backed
proof of a Teams server-enforced idempotency mechanism.

#### Scenario: Confirmed reminder execution is not resent

- **GIVEN** a reminder execution is durably recorded as `Sent`
- **WHEN** the same reminder execution is received again
- **THEN** no additional Teams send is attempted

#### Scenario: Retryable failure is retried using the same execution identity

- **GIVEN** a reminder execution is durably recorded as `FailedRetryable`
- **WHEN** retry policy permits another attempt
- **THEN** the retry uses the same reminder execution identity
- **AND** its state transition is persisted

#### Scenario: Delivery outcome is unknown after interruption

- **WHEN** outbound delivery was initiated but confirmation was not durably recorded before interruption
- **THEN** the execution is recorded as `DeliveryUnknown`
- **AND** the system does not falsely report confirmed delivery
- **AND** recovery follows an explicit retry-or-operator-review policy

#### Scenario: Confirmed permanent failure is not automatically retried

- **GIVEN** a reminder execution is durably recorded as `FailedPermanent`
- **WHEN** normal retry processing runs
- **THEN** no automatic Teams send is attempted
