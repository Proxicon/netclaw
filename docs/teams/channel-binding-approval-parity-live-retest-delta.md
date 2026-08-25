# Teams channel-binding parity live retest delta

Status: **NOT EXECUTED IN THIS CHANGE**.

Historical Phase 1 Teams evidence remains valid. This delta requests only the
boundaries changed by the channel-binding and approval-parity refactor.

1. Send one representative Personal approval request and select `Once` or
   `Deny`. Confirm one native terminal card and one session outcome.
2. With the owner-approved generic Team shell policy already configured, run
   one harmless Team-audience shell approval. Confirm it reaches a normal
   approval card and stays unavailable when any required generic gate is
   removed.
3. Let one pending card expire, then use the replacement card. Confirm the old
   card cannot act, the replacement resolves the same pending request once, and
   no implicit core Deny is created by expiry.
4. Restart or recover one binding with a pending approval, then resolve the
   recovered card once. Confirm no duplicate execution occurs.

Record only sanitized structural outcomes and counters. Do not change Azure
Bot, Entra, Teams package, tenant permissions, routes, secrets, or operator
configuration while carrying out this delta.
