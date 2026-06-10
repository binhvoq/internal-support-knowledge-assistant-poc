# Auto Suggestion — Saga Orchestration

## Model

- **Ticket** is sovereign: lifecycle `New → Suggested → Resolved → Reopened`.
- **TicketService** is the final gate: `ProposeTicketSuggestion` applies suggestion only when invariants pass (`Version`, status, no final answer).
- **AiOrchestrator** runs a **MassTransit saga** (`TicketSuggestionStateMachine`) — AI worker replies to saga only; saga publishes audit events.

## Flow

1. `POST /tickets` → outbox `ITicketCreated` (`JobId`, `TicketVersion`, …).
2. Saga `Initially(TicketCreated)` → publish `IGenerateSuggestionRequested` → state `GeneratingSuggestion`.
3. `GenerateSuggestionRequestedConsumer`: `AiPipelineService` → publish `ISuggestionGenerated` or `ISuggestionGenerationFailed`.
4. Saga receives result → request `IProposeTicketSuggestion` → `ProposeTicketSuggestionConsumer` (TicketService).
5. TicketService respond `IProposeTicketSuggestionResult` (accept/reject); saga publish `IAiSuggestionGenerated` / `IAutoSuggestionDiscarded` / `IAutoSuggestionFailed`.
6. Timeout → `IStepTimeout` → reconcile (read ticket snapshot, retry/fail/discard — no blind rollback).

## UI

- “Đang tạo gợi ý…” from `GET /tickets/{id}/auto-suggestion` (saga state mapped to `Running` / `Produced` / `Completed` / …), not from ticket status.

## Fault injection (Question markers)

- `__FAIL_AI__` — pipeline throws; saga reconciles / retries / fails; ticket stays `New` unless already accepted.
- `__POISON_AI__` — uncaught throw in AI worker → DLQ on `generate-suggestion-requested`.
- `__SKIP_GENERATE__` (alias `__SKIP_CONSIDER__`) — AI worker skips publish; step timeout → reconcile (test only).

## Debug

- `GET /tickets/{ticketId}/auto-suggestion` — latest saga for ticket.
- `GET /debug/saga-instances?ticketId=` — saga rows in orchestrator DB.
- `GET /debug/dlq?queue=generate-suggestion-requested` — poison / DLQ probe.
