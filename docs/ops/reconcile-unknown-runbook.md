# ReconcileUnknown Runbook

## Ý nghĩa

`ReconcileUnknown` là trạng thái **parking lot recoverable** của auto-suggestion saga. Saga vào state này khi TicketService không phản hồi đáng tin sau nhiều lỗi HTTP transient trong `Reconciling`.

Khác với `Failed`:
- `Failed` = quyết định nghiệp vụ rõ ràng (NotFound, abandon, hết retry).
- `ReconcileUnknown` = **không xác định** được trạng thái ticket thực tế.

Transient HTTP reconcile **không bao giờ** chuyển saga sang `Failed`.

## Khi nào cần can thiệp

Alert / điều tra khi:
- `reconcileUnknownCount` hoặc `reconcileUnknownExhausted` tăng trên `/ops/saga-health`
- App Insights event `SagaReconcileUnknownExhausted` xuất hiện
- UI ticket hiển thị status `Unknown` kéo dài

## Kiểm tra nhanh

### Health tổng quan

```http
GET /ops/saga-health
```

Fields quan trọng:
- `reconcileUnknownCount`
- `reconcileUnknownPendingRedrive`
- `reconcileUnknownExhausted`

### Danh sách saga cần xử lý

```http
GET /ops/reconcile-unknown?take=50
```

Mỗi item có:
- `sagaId`, `ticketId`, `jobId`, `failureReason`
- `attemptCount`, `maxAutoRedriveAttempts`
- `status`: `pending` | `exhausted` | `missing-item`
- `nextAutoRedriveEligibleAt` (auto-redrive tiếp theo nếu còn quota)

Thứ tự ưu tiên: **exhausted trước**, sau đó `updatedAt` cũ nhất.

### Trạng thái UI theo ticket

```http
GET /tickets/{ticketId}/auto-suggestion
```

`status = Unknown` khi `sagaState = ReconcileUnknown`.

## Manual redrive

Chỉ dùng khi saga đang `ReconcileUnknown`:

```http
POST /ops/sagas/{sagaId}/redrive-reconcile
```

- **Không** bị giới hạn `MaxReconcileUnknownRedriveAttempts` (manual không tăng auto attempt count).
- Request được audit log + App Insights event `SagaReconcileUnknownManualRedrive` với `callerIdentity`.
- Auth: `.WithDebugOrServicePolicy` — Development anonymous; production cần Entra **Service** role.

## Expected outcomes sau redrive

| Kết quả reconcile | Saga state | UI status |
|---|---|---|
| `AlreadyAppliedBySameJob` | `Completed` | `Completed` |
| `Resolved` / `VersionChanged` / `AlreadySuggestedByOtherJob` | `Discarded` | `Discarded` |
| `NotFound` hoặc hết retry thật | `Failed` | `Failed` |
| HTTP 503/timeout (transient) | `ReconcileUnknown` | `Unknown` |

## Auto-redrive

Sweeper tự publish `IReconcileRedrive` khi:
- Saga parked >= `ReconcileUnknownRedriveAfterMinutes`
- Backoff elapsed (dựa trên `LastAttemptAt` + `AttemptCount`)
- `AttemptCount < MaxReconcileUnknownRedriveAttempts`

Khi schedule auto-redrive, reconciliation item được cập nhật **ngay** (`LastAttemptAt`, `AttemptCount++`) để chống duplicate enqueue.

Saga thiếu reconciliation item (orphan) được **backfill** tự động trước mỗi sweep.

## App Insights queries

### Saga mới escalate

```kusto
customEvents
| where name == "SagaReconcileEscalatedUnknown"
| project timestamp, customDimensions.sagaId, customDimensions.ticketId, customDimensions.reason
| order by timestamp desc
```

### Auto-redrive

```kusto
customEvents
| where name == "SagaReconcileUnknownAutoRedrive"
| summarize count() by bin(timestamp, 1h), tostring(customDimensions.ticketId)
```

### Exhausted (cần manual)

```kusto
customEvents
| where name == "SagaReconcileUnknownExhausted"
| project timestamp, customDimensions.sagaId, customDimensions.ticketId, customDimensions.attemptCount
```

### Manual redrive audit

```kusto
customEvents
| where name == "SagaReconcileUnknownManualRedrive"
| project timestamp, customDimensions.sagaId, customDimensions.callerIdentity
```

### Recovered vs vẫn parked

```kusto
customEvents
| where name in ("SagaReconcileUnknownRecovered", "SagaReconcileUnknownStayedParked")
| summarize count() by name, bin(timestamp, 1h)
```

## Config cần review (appsettings / env)

```json
{
  "AutoSuggestion": {
    "ReconcileUnknownRedriveAfterMinutes": 15,
    "MaxReconcileUnknownRedriveAttempts": 10,
    "ReconcileUnknownBackoffBaseSeconds": 300,
    "ReconcileUnknownBackoffMaxSeconds": 3600,
    "StuckReconcilingSweepIntervalSeconds": 60
  }
}
```

Defaults trong code đã bảo thủ. Chỉ giảm backoff/limit khi TicketService ổn định và có monitoring.

## Staging drill checklist

1. Làm TicketService trả 503 cho reconcile endpoint.
2. Tạo ticket mới, đợi saga vào `Reconciling` rồi `ReconcileUnknown`.
3. Verify `GET /ops/reconcile-unknown` thấy saga `pending`.
4. Khôi phục TicketService.
5. Đợi auto-redrive hoặc gọi `POST /ops/sagas/{id}/redrive-reconcile`.
6. Verify saga về `Completed`/`Discarded`/`Failed` tùy trạng thái ticket — **không** về `Failed` chỉ vì HTTP transient.

## Security checklist

- [ ] Entra enabled ở non-dev
- [ ] Ops endpoints yêu cầu Service role (không bypass production)
- [ ] Không log token/secret trong manual redrive audit
- [ ] Không sửa DB tay trừ khi có incident approval

## Không làm

- Không set saga `Failed` thủ công chỉ vì TicketService down.
- Không xóa `SagaReconciliationItems` để “reset” — dùng manual redrive sau khi TicketService hồi phục.
- Không tăng `MaxReconcileUnknownRedriveAttempts` vô hạn — dùng manual redrive cho exhausted cases.
