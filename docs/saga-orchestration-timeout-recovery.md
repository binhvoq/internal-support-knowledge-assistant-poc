# Saga timeout recovery

> **Deprecated (2026-06):** Luồng MassTransit saga `TicketSuggestionStateMachine` đã được thay bằng **proposal pipeline** (`TicketCreatedConsumer` + `ConsiderAutoSuggestion`). Tài liệu này chỉ để tham khảo lịch sử. Xem [`auto-suggestion-proposal.md`](auto-suggestion-proposal.md).

Tai lieu tham chieu cho orchestration `TicketSuggestion`: timeout = nghi ngo → probe TicketService → policy → outcome. Khong compensate/fail mu.

## Nguyen tac

```text
timeout = suspect → GET /internal/tickets/{id}/saga-progress → policy → outcome
```

Event fail ro (`AnalyzingMarkFailed`, `AiPipelineFailed`, `SuggestionSaveFailed`) → compensate ngay, khong probe (`AiPipelineFailed` / `SuggestionSaveFailed` → `Compensating`; `AnalyzingMarkFailed` → `RevertingBeforeFailed`).

TicketService **khong im lang** khi command stale/het han: publish `AnalyzingMarkFailed` / `SuggestionSaveFailed` (reason `Stale command` hoac `Concurrency conflict`) thay vi `return` im lang. Truong hop mark/save da apply nhung event mat → idempotent success (`AnalyzingMarked` / `SuggestionSaved`).

Saga `Failed` (timeout probe het / mark fail): chi vao state nay sau khi ticket da revert ve `OriginalStatus` (thuong `New`), co `SagaStopNote`, da clear lock/draft/suggestion va da tang epoch. Event worker muon → ignore.

| Buoc | Outcome timeout |
|------|-----------------|
| `Analyzing` | `Proceed`, `ResendMark`, `RetryVerify`, `Fail` |
| `RunningAi` | `Proceed` (saga payload hoac **AiDraft** tren ticket), `ResendRun`, `RetryVerify`, `Compensate`, `Fail` |
| `Saving` | `Complete`, `ResendSave`, `RetryVerify`, `Compensate`, `Fail` |
| `Compensating` | `Complete` → `Compensated`, `ResendCompensate`, `RetryVerify`, `Fail` → `RollbackFailed` |
| `RevertingBeforeFailed` | `Complete` → `Failed`, `ResendCompensate`, `RetryVerify`, `Fail` → `RollbackFailed` |

MassTransit dung **mot** schedule `StepTimeout` (delay theo buoc); moi state co policy/activity rieng.

## Compensating

| Probe | Outcome |
|-------|---------|
| Da revert (status goc, khong suggestion, khong owned) | `Complete` → `Compensated` |
| Van `Analyzing`, owned saga nay | `RetryVerify` / `ResendCompensate` (max 1) |
| Probe 503/timeout | `RetryVerify` den cap → `RollbackFailed` reason **unable to verify** (khong nham voi compensation that bai) |
| Saga khac / terminal | `RollbackFailed` + log |

`CompensateMarkAnalyzingConsumer`: ticket **da revert** → publish `MarkAnalyzingReverted`, khong mutate (idempotent).

## RevertingBeforeFailed

State nay dung cho contract: `Failed` = ticket da rollback xong va worker muon da bi chan bang epoch/correlation.

| Event/Probe | Outcome |
|-------------|---------|
| `MarkAnalyzingReverted` | `Failed` |
| Da revert nhung event bi mat | Probe `Complete` → `Failed` |
| Van `Analyzing`, owned saga nay | `RetryVerify` / `ResendCompensate` |
| Khong xac nhan duoc rollback | `RollbackFailed` (khong goi la `Failed`) |

## Log (Azure Monitor)

| EventName | Level | Khi |
|-----------|-------|-----|
| `SagaCompensationFailed` | Error | Verify → `Fail` (that bai / probe het retry) |
| `SagaCompensationProbeUnavailable` | Warning | Probe retry trong Compensating |

Fields: `SagaId`, `TicketId`, `Reason`, `ProbeError`, `VerifyAttempts`, `CompensateResendCount`.

## Config

`appsettings.json` — prod-like timeout. `appsettings.Development.json` — timeout ngan + log `SupportPoc.AiOrchestrator.Saga` = Debug.

## Debug

```text
GET http://localhost:5001/internal/tickets/{ticketId}/saga-progress
GET http://localhost:5003/debug/saga?ticketId={ticketId}
```

## Kiem tra

### Unit test (khong can Azure)

> **Luu y (2026-06):** Cac integration test saga/compensate cu da duoc go bo khi chuyen SQL Server-only. Chay `dotnet test` cho unit test hien tai (pipeline AI, MCP security, business rules).

### Chay local + Azure

```bash
# Start required Azure resources or the local Service Bus emulator directly.
# Then run backend services with dotnet run and verify endpoints with Invoke-RestMethod.
# See scripts/README.md for the direct-command policy.
```

Smoke test khong cover fault injection — chi `New → Suggested → Resolved`.

### Fault injection (API, khong UI)

Gan marker vao `question` khi `POST /tickets`:

| Marker | Muc dich | Saga ky vong |
|--------|----------|--------------|
| `__SKIP_MARK__` | Mark DB, skip event | Probe Analyzing → Completed |
| `__SKIP_SAVE_EVENT__` | Save DB, skip event | Probe Saving → Completed |
| `__SKIP_AI_EVENT__` | Ghi AiDraft DB, skip `AiPipelineCompleted` | Probe RunningAi → Proceed (khong resend LLM) |
| `__FAIL_AI__` | AI fail | Compensated (event) |
| `__FAIL_AI__` + `__SKIP_COMPENSATE_EVENT__` | Revert DB, skip event | Probe Compensating → Compensated |
| `__POISON_AI__` | Uncaught throw | DLQ (chua co unit test) |

Vi du:

```bash
curl -sf -X POST http://localhost:5001/tickets \
  -H "Content-Type: application/json" \
  -d '{"employeeId":"EMP-TEST","category":"IT","question":"VPN __FAIL_AI__ __SKIP_COMPENSATE_EVENT__"}'
```

Poll saga: `curl "http://localhost:5003/debug/saga?ticketId=TCK-xxx"`

### Pham vi da dong backend

- Policy + probe unavailable Compensating + compensate idempotent: **co unit test**
- 4 marker chinh tren Azure+local: **da chay tay pass**
- RunningAi: `RunAiPipelineConsumer` ghi **AiDraft** vao TicketService (`RecordAiPipelineDraft`) truoc khi publish `AiPipelineCompleted`
- Chua bat buoc: UI fault panel, `__POISON_AI__` / DLQ, RunningAi resend x2 integration
