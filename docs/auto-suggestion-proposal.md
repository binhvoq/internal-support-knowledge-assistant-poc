# Auto Suggestion — Proposal Pipeline

## Model

- **Ticket** is sovereign: lifecycle `New → Suggested → Resolved → Reopened`.
- **AI orchestrator** runs a job (`Running → Produced`) then sends **one DB command**: `ConsiderAutoSuggestion`.
- **No** `Analyzing` status, **no** `SagaEpoch`, **no** compensate Mark/Save, **no** timeout probes.

## Flow

1. `POST /tickets` → outbox `TicketCreated` (`JobId`, …).
2. `TicketCreatedConsumer` (AiOrchestrator): AI-only (`AiPipelineService`), persist `AutoSuggestionJob`.
3. Request/response `ConsiderAutoSuggestion` → `ConsiderAutoSuggestionConsumer` (TicketService).
4. Accept → `Suggested` + `AiSuggestedAnswer`; reject → noop + optional `AutoSuggestionDiscarded` event.
5. On accept → publish `AiSuggestionGenerated`.

## UI

- “Đang tạo gợi ý…” from `GET /tickets/{id}/auto-suggestion` (`Running` / `Produced`), not from ticket status.

## Fault injection (Question markers)

- `__FAIL_AI__` — pipeline fails, ticket stays `New`.
- `__POISON_AI__` — uncaught throw → DLQ.
- `__SKIP_CONSIDER__` — job stuck at `Produced` (test only).

## Removed

- Entire `AiOrchestrator/Saga/` (state machine, timeouts, probes).
- Ticket consumers: Mark, Save, Compensate, RecordDraft.
- `internal/tickets/{id}/saga-progress`.
