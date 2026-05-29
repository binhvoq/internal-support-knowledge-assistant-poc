# Saga timeout recovery

Tai lieu tham chieu cho orchestration `TicketSuggestion`: timeout = nghi ngo → probe TicketService → policy → outcome. Khong compensate/fail mu.

## Nguyen tac

```text
timeout = suspect → GET /internal/tickets/{id}/saga-progress → policy → outcome
```

Event fail ro (`AnalyzingMarkFailed`, `AiPipelineFailed`, `SuggestionSaveFailed`) → compensate ngay, khong probe.

| Buoc | Outcome timeout |
|------|-----------------|
| `Analyzing` | `Proceed`, `ResendMark`, `RetryVerify`, `Fail` |
| `RunningAi` | `Proceed`, `ResendRun`, `RetryVerify`, `Compensate`, `Fail` |
| `Saving` | `Complete`, `ResendSave`, `RetryVerify`, `Compensate`, `Fail` |
| `Compensating` | `Complete` → `Compensated`, `ResendCompensate`, `RetryVerify`, `Fail` |

MassTransit dung **mot** schedule `StepTimeout` (delay theo buoc); moi state co policy/activity rieng.

## Compensating

| Probe | Outcome |
|-------|---------|
| Da revert (status goc, khong suggestion, khong owned) | `Complete` → `Compensated` |
| Van `Analyzing`, owned saga nay | `RetryVerify` / `ResendCompensate` (max 1) |
| Probe 503/timeout | `RetryVerify` den cap → `Fail` reason **unable to verify** (khong nham voi compensation that bai) |
| Saga khac / terminal | `Fail` + log |

`CompensateMarkAnalyzingConsumer`: ticket **da revert** → publish `MarkAnalyzingReverted`, khong mutate (idempotent).

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

```bash
dotnet test tests/SupportPoc.AiOrchestrator.Tests   # policy 4 buoc + probe unavailable Compensating
dotnet test tests/SupportPoc.TicketService.Tests      # compensate idempotent already reverted
```

| Test | Chung minh |
|------|------------|
| `Compensating_probe_unavailable_retries_before_fail` | 503 → RetryVerify, het retry → Fail "unable to verify" |
| `Already_reverted_ticket_publishes_reverted_without_mutating` | Resend compensate khong mutate sai |

### Chay local + Azure

```bash
bash scripts/azure-resources-start.sh   # neu chua co config
bash scripts/restart-services.sh
bash scripts/smoke-test.sh              # happy path E2E
```

Smoke test khong cover fault injection — chi `New → Suggested → Resolved`.

### Fault injection (API, khong UI)

Gan marker vao `question` khi `POST /tickets`:

| Marker | Muc dich | Saga ky vong |
|--------|----------|--------------|
| `__SKIP_MARK__` | Mark DB, skip event | Probe Analyzing → Completed |
| `__SKIP_SAVE_EVENT__` | Save DB, skip event | Probe Saving → Completed |
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
- Chua bat buoc: UI fault panel, `__POISON_AI__` / DLQ, RunningAi resend x2 integration
