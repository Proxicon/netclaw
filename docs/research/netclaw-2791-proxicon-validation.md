# Issue #2791: Proxicon test branch

## Scope and state

This repair targets `Proxicon/netclaw` for a test build.
The branch starts from upstream `dev` commit `78e4e384e4e47a836b1f260263f58cc8877b37a6`.
No upstream commit, upstream PR, deployment, provider-setting change, or business action forms part of this repair.

The [issue](https://github.com/netclaw-dev/netclaw/issues/2791) and its
[native reproduction comment](https://github.com/netclaw-dev/netclaw/issues/2791#issuecomment-6679638977)
were read before code changes.
The repeated native intervals are 642.482, 642.766, and approximately 642.8 seconds.
The reporter observed missing arguments, safe rationale rejection, and later valid calls.
Those logs contain no raw provider response or sanitized provider event sequence.

**Production-cause status: unproven.**
The repair fixes two controlled defects in the same provider path.
It does not establish which defect, if either, caused the captured production intervals or missing arguments.

## Trace and controlled causes

`openai` with `OAuthDevice` uses the Codex Responses endpoint through the OpenAI SDK and its `AsIChatClient` adapter.
The API key path also uses Responses.
Both official 0.28.2 and the branch base pin OpenAI 2.12.0 and Microsoft.Extensions.AI.OpenAI 10.8.3.

The adapter parses function arguments from `response.output_item.done`.
It exposes argument deltas as updates without parsed function content.
`StreamingResponseReader` retains the updates and assembles a `ChatResponse` after stream enumeration ends.
Only then can the session dispatch the response's tools.
The required-rationale and authorization checks remain in their existing owners.

Two controlled failures occur before the repair:

1. The provider emits a complete function item and `response.completed`, then holds HTTP EOF.
   The adapter exposes the function and usage but requests another transport read.
   The session therefore waits for HTTP EOF before it returns the assembled response.
   A held transport tail deterministically reproduces this delay for API key and OAuthDevice paths.
2. The provider emits a function item, then a failed or incomplete event, or clean EOF without a terminal event.
   The adapter exposes the failed or incomplete event as an ordinary update.
   The stream then assembles a normal response that contains the function call.
   A valid call can therefore cross the model-response boundary despite an unsuccessful provider response.

Five regression rows fail against an untouched base checkout.
The checkout uses the new fixtures with only the old plugin constructor signature restored.
The two held-EOF rows time out at five seconds.
The other three rows return calls when the tests require a provider failure.
The same rows pass after the repair.

The fixture controls the transport tail directly.
It does not reproduce a natural 643-second provider delay.
The separate slow-stream test advances only its diagnostic clock by 643 seconds.
That accelerated test is not a native elapsed-time reproduction.

## Repair owner and invariants

`OpenAiResponseStreamClient` wraps the SDK adapter at the provider boundary.
It accepts `response.completed` only with `Completed` status and then disposes the stream without another read.
It converts failed, incomplete, error, and absent terminal events into a safe `ProviderException`.
The session's response reader never dispatches tools from a failed enumeration.

The wrapper keeps all counters within one call.
It emits shape and elapsed-time metadata through the existing model pipeline's session scope.
It does not log prompt text, credentials, function identities, argument values, or provider error messages.
The counters compare the argument deltas, done events, terminal output, and adapter parse result.
They do not reconstruct or replace arguments.

A real logger-provider test also exposed loss of the session scope between iterator yields.
`LoggingChatClient` now opens the existing scope for each downstream stream operation, disposal, and completion log.
This keeps the diagnostic tail within the original session.
It does not change session ownership or the protocol.

A successfully completed response with empty or partial arguments retains its SDK parse result.
The existing required-rationale guard rejects an unusable call.
A later well-formed request remains independent.
No authorization check, tool execution rule, turn identity, completed result, or journal format changes.

## Timeout and retry decisions

- `ProcessingWatchdog` owns the session's call deadline and cancellation.
  Defaults remain 1800 seconds for prefill, 600 seconds between deltas, and 1200 seconds without substantive progress.
  Keepalives refresh liveness but do not refresh the independent no-progress deadline.
- The actor fences results by model-call identity and treats an exhausted provider failure as terminal.
  This repair adds no actor retry, input resend, or tool replay.
- `RetryingChatClient` permits transient retries before substantive output.
  `StreamCommitGate` determines that boundary.
  The new terminal failures have no retryable HTTP status.
- `RoutingChatClient` can still select another provider before substantive output.
  It does not replay tools or discard completed results from earlier model calls.
- The SDK retains its own internal HTTP retry policy.
  The OAuth policy can refresh and retry one HTTP 401.
  The new stream counters do not identify internal SDK attempts before its first update.

No timer or retry sum establishes the captured 643-second cause.

## Verification record

| Check | Result |
| --- | --- |
| Five provider regression rows against untouched `78e4e384` | All five fail as expected before the repair. |
| Focused provider, native socket, and logger tests | 30 pass. |
| Solution checks plus the final affected-suite rerun | 16,254 pass across 14 assemblies; 100 existing cases skip. |
| Final `Netclaw.Daemon.Tests` run | 1,793 pass; 40 existing cases skip. |
| `scripts/run-response-terminal-release-mutations.sh` | Three exact mutants run and die; final run takes 97 seconds. |
| `dotnet slopwatch analyze` | Zero issues. |
| `pwsh scripts/Add-FileHeaders.ps1 -Verify` | Pass. |
| `git diff --check` | Pass. |
| `./evals/run-evals.sh` | Preflight blocks because no eval provider type, endpoint, or model ID is configured. |

The aggregate counts combine the solution run with the final daemon-suite rerun after the last test-harness correction.
They do not claim a fresh single-invocation green solution run after that correction.
The final daemon run covers every changed provider, logger, and SignalR test.

The test runner uses .NET 10.0.400 and PowerShell on `PATH`.
A Linux child subreaper supplies orphan-process cleanup within this container.
Before these environment repairs, three existing process tests also fail against the untouched base.
All three pass with the corrected runner, without source changes to those tests or their production code.
No tests or quality gates are disabled.

The socket tests use an actual loopback Kestrel server and the native SignalR WebSocket client.
They reach the authenticated hub, gateway, session pipeline, actor, journal, and real SDK adapter.
The provider bytes remain synthetic, and the actor scheduler uses virtual time for the timeout case.
These tests verify native protocol integration; they do not verify a live OAuth provider or a deployed daemon image.

The tests cover a failed response, a watchdog timeout, visible error and failed-turn outcomes, and a later successful turn.
They assert the same session, ordered turn numbers, one request per explicit input, and no tool-dispatch notices from failed responses.
Provider tests also retain prior completed tool results and count request attempts.
The same-turn case reaches the real `LoadToolTool`, rationale guard, and authorization dispatcher.
Its first `ToolBatchStarted` consumes the exact admitted input ID.
Its terminal record retains the original user text after an empty call and a valid call.
This journal assertion does not add an exact-input outcome field to SignalR history.

## Local build acceptance and remaining gaps

Build this branch through the fork's normal image-build procedure.
Use an isolated image tag so the test build remains identifiable.
Do not report the test image as an official upstream release.

For a fresh local checkout with Bash and the required SDK and Docker tools:

```bash
git clone --branch fix/2791-oauth-stream-termination https://github.com/proxicon/netclaw.git netclaw-2791-test
cd netclaw-2791-test
IMAGE_REPO=ghcr.io/proxicon/netclaw scripts/docker/build-image.sh 2791-test
```

The build command creates `ghcr.io/proxicon/netclaw:2791-test` locally.
It does not push or deploy the image.
The assembly version still comes from this repository's `Directory.Build.props`.
Keep the test image tag and source commit with the verification evidence.

With the reporter's OAuth provider, use a new disposable native session and a harmless read-only request.
Collect the Information-level `response_stream_*` milestones and the original `turn_llm_call_start` line.
Retain the required-rationale result and actual final turn outcome.
Record whether any long interval precedes the first update, function announcement, argument delta, or terminal event.
Do not replay an old uncertain business request.

The environment status lists GitHub credentials and no inference credential or live NetClaw service binding.
No fresh live OAuthDevice run, production capture, or full image deployment occurs here.
The reporter's preserved native logs remain the production evidence.
The local build test must establish whether the terminal repair removes the observed symptom.

## RatelDesk coordination

[RatelDesk #351](https://github.com/BostonTechnologies/rateldesk/issues/351) owns the separate approximately 300-second observation cutoff.
[RatelDesk #308](https://github.com/BostonTechnologies/rateldesk/issues/308) owns exact-turn historical recovery.
Their issue bodies and latest #351 investigation comments informed this boundary.
This branch changes no RatelDesk code and adds no SignalR fields or historical replay guarantee.
A provider repair cannot restore consumer events that RatelDesk already discarded.
Both RatelDesk requirements remain separate and open.
