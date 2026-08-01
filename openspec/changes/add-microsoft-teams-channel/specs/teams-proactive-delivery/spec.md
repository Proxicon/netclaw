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
