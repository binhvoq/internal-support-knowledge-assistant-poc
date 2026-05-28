# Saga orchestration timeout recovery

This note documents the design intent behind timeout recovery in the
MassTransit saga orchestration. It is written for future maintainers and AI
agents that need to explain or extend the saga without rediscovering the
timeout/compensation problem.

Current implementation scope: phase 1 fixes only the `Saving` timeout path in
the `TicketSuggestionStateMachine`. `Analyzing`, `RunningAi`, and
`Compensating` keep their previous timeout behavior.

## Core idea

The old `Saving` timeout path sent `CompensateMarkAnalyzing` immediately when
`TicketSuggestionSaved` did not arrive before the saga timeout. That is unsafe in
a distributed system:

1. `SaveTicketSuggestionConsumer` can commit the ticket as `Suggested`.
2. The `TicketSuggestionSaved` event can arrive after the saga timeout.
3. If the saga compensates immediately, it can delete a valid AI suggestion.

The intended semantics for saga timeout handling are:

```text
timeout = suspect, not proof of failure
suspect -> verify source of truth -> deterministic decision
```

`SagaEpoch`, Inbox, and Outbox are still useful, but they do not prove that the
save step did not commit. The missing layer is a business verification step
against `TicketService`, which is the source of truth for ticket state.

## Implementation map

```text
Saving
  StepTimeout / VerifyDue
    -> EvaluateSavingTimeoutActivity
    -> ISavingTimeoutEvaluator
    -> ITicketProgressProbe
    -> SavingTimeoutPolicy
    -> PendingTimeoutOutcome on saga
    -> TicketSuggestionStateMachine branches explicitly
```

Responsibilities:

| Component | Responsibility |
|---|---|
| `TicketSuggestionStateMachine` | Correlation, schedules, visible transitions, send/publish commands. |
| `EvaluateSavingTimeoutActivity` | Resolve scoped services, evaluate timeout, write outcome/reason/counters to saga. No send, no transition. |
| `HttpTicketProgressProbe` | Read `GET /internal/tickets/{id}/saga-progress`; convert failures to `TicketProgressProbeResult`. |
| `SavingTimeoutPolicy` | Pure decision logic from probe result + saga state. |
| `SagaSaveCommandFactory` | Rebuild `SaveTicketSuggestion` from saga payload for resend. |

`StepTimeout` and `VerifyDue` are deliberately separate schedules:

| Schedule | Delay | Token |
|---|---|---|
| `StepTimeout` | `Saga:TimeoutSeconds` | `TimeoutTokenId` |
| `VerifyDue` | `Saga:VerifyRetrySeconds` | `VerifyTimeoutTokenId` |

Do not reuse the long step timeout for short verify retries.

## Saving timeout outcomes

| Outcome | State machine action |
|---|---|
| `Complete` | Unschedule timeout tokens, publish `IAiSuggestionGenerated`, transition `Completed`. |
| `RetryVerify` | Schedule `VerifyDue`, remain in `Saving`. |
| `ResendSave` | Mark `SaveResendIssued`, send `SaveTicketSuggestion`, schedule `VerifyDue`, remain in `Saving`. |
| `Compensate` | Only after post-resend grace verifies save still did not apply; send `CompensateMarkAnalyzing`, transition `Compensating`. |
| `Fail` | Unschedule timeout tokens, write reason, transition `Failed`. |

Hard invariants:

- No `Saving` timeout path may send `CompensateMarkAnalyzing` before probe/policy evaluation.
- `Unknown`, `unexpected`, `probe unavailable after retries`, wrong owner, or wrong epoch must go to `Fail`, not `Compensate`.
- `ResendSave` must never compensate in the same cycle. It must schedule `VerifyDue` first.
- `Compensate` is allowed only when the probe returns `Found`, the ticket still belongs to this saga, epoch matches, status is `Analyzing`, no suggestion exists, and post-resend grace is exhausted.
- Probe/network/parse failures should become `TicketProgressProbeResult`, so policy decides. They should not create blind MassTransit retry loops.
- Cancellation from shutdown/request cancellation can still bubble.

## SavingTimeoutPolicy summary

For `Found` ticket snapshots:

| Ticket state | Outcome |
|---|---|
| `Suggested`, correct `ActiveSagaCorrelationId`, correct `SagaEpoch`, `HasSuggestion=true` | `Complete` |
| `Analyzing`, correct owner/epoch, no suggestion, before max verify attempts | `RetryVerify` |
| `Analyzing`, correct owner/epoch, no suggestion, max pre-resend attempts exhausted, valid saga payload | `ResendSave` |
| `Analyzing`, correct owner/epoch, no suggestion, post-resend grace exhausted | `Compensate` |
| Owned by another saga | `Fail` |
| Wrong epoch | `Fail` |
| `Suggested`/`Resolved` without matching this saga's completed snapshot | `Fail` |
| Any other unexpected status | `Fail` |

For probe statuses:

| Probe status | Outcome |
|---|---|
| `NotFound` | `Fail` |
| `Unavailable` | `RetryVerify` until capped, then `Fail` |
| `InvalidResponse` | `Fail` |

## Phase 1 boundaries and technical debt

- `TicketService` does not clear `ActiveSagaCorrelationId` after a successful save.
  The current `Complete` rule therefore checks ownership + epoch. A later phase
  can add `FinalizeTicketSuggestionSaga` or clear ownership in the save step.
- Other timeout states are not yet reconciled.
- This is a POC SQLite setup; schema helpers add columns for existing local DBs.
  A production service should use normal EF migrations.

## Manual verification cases

Configure short delays for local tests, for example:

```json
{
  "Saga": {
    "TimeoutSeconds": 30,
    "VerifyRetrySeconds": 5,
    "MaxVerifyAttempts": 2,
    "PostResendVerifyAttempts": 1
  }
}
```

| # | Setup | Trigger | Expected |
|---|---|---|---|
| 1 | Save committed, `TicketSuggestionSaved` delayed | `StepTimeout` in `Saving` | Probe returns completed snapshot; saga `Completed`; no compensation. |
| 2 | Save not visible, ticket still `Analyzing`, correct owner/epoch | `StepTimeout` | `RetryVerify`, then `VerifyDue`. |
| 3 | Pre-resend verifies exhausted and saga payload is valid | `VerifyDue` | `ResendSave`, schedules another `VerifyDue`, remains `Saving`. |
| 4 | Resend just issued and save is still in-flight | next `VerifyDue` | `RetryVerify`; no compensation in same cycle as resend. |
| 5 | Post-resend grace exhausted, still `Analyzing`, correct owner/epoch, no suggestion | `VerifyDue` | `Compensate`. |
| 6 | Probe returns 404 | timeout/verify | `Failed`; no compensation. |
| 7 | TicketService down / 5xx / network error | timeout/verify | `RetryVerify` capped, then `Failed`; no compensation. |
| 8 | `ActiveSagaCorrelationId` belongs to another saga | verify | `Failed`; no compensation. |
| 9 | Wrong epoch or unexpected status | verify | `Failed`; no compensation. |
| 10 | Saga payload cannot rebuild `SaveTicketSuggestion` | verify after pre-resend attempts | `Failed`; no `.Send(...)` exception loop. |
| 11 | Activity/evaluator throws a non-cancellation exception | evaluate | Outcome `Fail`; state machine transitions `Failed` deterministically. |
| 12 | Normal `TicketSuggestionSaved` arrives before timeout | normal event | `Completed`; both schedules unscheduled. |

Useful endpoints:

```text
GET http://localhost:5001/internal/tickets/{ticketId}/saga-progress
GET http://localhost:5003/debug/saga?ticketId={ticketId}
```
