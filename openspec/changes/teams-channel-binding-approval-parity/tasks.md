## 1. Generic policy and ingress safety

- [x] 1.1 Implement the generic Team shell opt-in predicate in `ToolAccessPolicy` and verify a configured Team invocation reaches the normal approval request while every missing gate fails closed.
- [x] 1.2 Apply `PromptClassifier` to Teams executable ingress through an additive dependency contract and verify safe, high-risk, and detector-unavailable paths preserve the required boundaries.

## 2. Teams approval and output lifecycle

- [x] 2.1 Route Teams approval callback session feedback through the shared pending lookup and approval response flow while preserving bounded Teams card validation and verify exact selected-key forwarding and requester rejection.
- [x] 2.2 Replace card-token expiry denial with persisted replacement-card binding and verify stale actions cannot authorize, a reissued action resolves once, and explicit deny releases the session wait.
- [x] 2.3 Route normal Teams reply failure through shared delivery feedback semantics and verify typing remains best effort while feedback-pipe failures are not swallowed.

## 3. Contracts, documentation, and validation

- [x] 3.1 Add Teams cross-channel binding contract fixtures for compatible lifecycle behavior and update the contract inventory with the verified assertion count.
- [x] 3.2 Update minimal Teams configuration and post-refactor retest-delta documentation, then verify it states no Graph permission or live tenant test was added.
- [x] 3.3 Run strict OpenSpec validation, focused Teams and actor tests, solution build/test, vulnerability, quality, header, whitespace, formatting, and lint checks; record results in the change artifacts.

## Validation record (2026-08-25)

- `dotnet restore Netclaw.slnx`: passed.
- `dotnet build Netclaw.slnx --no-restore --configuration Debug`: passed with 0 warnings and 0 errors.
- Focused Teams contract fixture: 71 passed.
- Focused actor approval gates: 78 passed.
- `dotnet test Netclaw.slnx --no-build --no-restore`: passed; environment-gated integration tests remained skipped.
- `dotnet list Netclaw.slnx package --vulnerable --include-transitive`: no vulnerable packages.
- `dotnet slopwatch analyze`: 0 issues.
- `./scripts/Add-FileHeaders.ps1 -Verify`, `scripts/check-no-bom.sh`, and `git diff --check`: passed.
- `npx --yes @fission-ai/openspec@latest validate teams-channel-binding-approval-parity --strict`: passed.
- `dotnet format Netclaw.slnx --verify-no-changes --no-restore`: reports pre-existing repository-wide whitespace violations outside this change. The affected project files contain legacy formatting diagnostics beyond the changed hunks; no broad formatting rewrite was applied.
