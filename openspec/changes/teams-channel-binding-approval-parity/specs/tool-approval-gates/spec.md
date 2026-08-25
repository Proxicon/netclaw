## ADDED Requirements

### Requirement: Team host shell approval is an explicit generic opt-in

The system SHALL expose and authorize `shell_execute` for Team audience only
when all of the following are true: the resolved shell mode is `HostAllowed`;
the Team profile's explicit `AllowedTools` list contains `shell_execute`; the
Team profile has an exact `ApprovalPolicy.ToolOverrides.shell_execute =
Approval`; and the active invocation has interactive approval available.

This is an audience-policy rule at the generic interactive-approval boundary.
It SHALL NOT depend on a channel type, a Teams identity mapping, a Personal
profile default, or configuration mutation. Persistent approvals remain grants
in `tool-approvals.json` and SHALL NOT modify `netclaw.json` policy.

#### Scenario: Explicit Team configuration produces a normal approval request

- **GIVEN** HostAllowed shell mode
- **AND** the Team profile explicitly lists `shell_execute`
- **AND** the exact Team shell override is `Approval`
- **AND** interactive approval is available
- **WHEN** a Team invocation passes hard-deny and protected-path checks
- **THEN** an unapproved shell call reaches the ordinary `ToolInteractionRequest` flow
- **AND** the selected option is handled by the normal session and grant rules

#### Scenario: Missing or broader Team configuration fails closed

- **GIVEN** a Team invocation with any one of no explicit shell list entry, no exact override, an `Auto` override, a `Deny` override, unavailable interaction, or non-HostAllowed shell mode
- **WHEN** it invokes `shell_execute`
- **THEN** the system denies the invocation before shell execution
- **AND** it does not create an approval request

#### Scenario: Public and hard-denied invocations remain unavailable

- **GIVEN** a Public invocation or a Team invocation that hits a hard deny or protected path
- **WHEN** it invokes `shell_execute`
- **THEN** the system denies it before any approval surface
- **AND** no Team policy opt-in or stored grant widens that boundary
