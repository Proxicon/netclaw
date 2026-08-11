## ADDED Requirements

### Requirement: Teams channel ACL is fail closed

SDK authentication SHALL establish the inbound activity identity; Netclaw ACL
SHALL separately decide whether that identity is allowed. The authenticated
tenant SHALL exactly match the configured tenant. Channel traffic SHALL require
both an explicitly allowed team and an explicitly allowed channel; empty team
or channel allow-lists admit no channel traffic. Personal traffic SHALL require
`AllowDirectMessages=true` and an explicit sender allow-list match. A nonempty
user allow-list SHALL additionally restrict channel senders. Mention policy
SHALL run after identity/access checks and before model dispatch.

Unauthorized or malformed activities SHALL be rejected with a safe reason code.
Otherwise allowed channel activity that is unmentioned while mention-only is
enabled SHALL be ignored without a model turn. Neither outcome may create a
session actor or model turn, except for a non-session audit record required by
existing diagnostics.

#### Scenario: Wrong tenant is rejected

- **WHEN** SDK-authenticated activity has a tenant other than the configured tenant
- **THEN** it is rejected before actor creation and model execution

#### Scenario: Empty channel allow-lists do not admit channel traffic

- **WHEN** Teams channel traffic is received with an empty team or channel allow-list
- **THEN** the activity is rejected before model execution

#### Scenario: Team and channel must both be allowed

- **WHEN** a channel is allowed but its team is not, or a team is allowed but its channel is not
- **THEN** the activity is rejected before model execution

#### Scenario: User allow-list restricts an otherwise allowed channel

- **WHEN** a Teams team and channel are allowed but the sender is absent from a nonempty allowed-user list
- **THEN** the activity is rejected before model execution

#### Scenario: Personal traffic requires explicit enablement and sender access

- **WHEN** direct messages are disabled or the personal sender is not explicitly allowed
- **THEN** the activity is rejected before model execution

#### Scenario: Unmentioned allowed channel activity is ignored

- **WHEN** a fully allowed channel message is unmentioned and mention-only is enabled
- **THEN** it is ignored without a session actor or model turn

#### Scenario: Mentioned allowed channel activity is dispatchable

- **WHEN** tenant, team, channel, sender, and mention checks all pass
- **THEN** the activity may be dispatched to the model pipeline
