# Demo script — Dual AI flow (P1)

Prerequisite: `bash scripts/restart-services.sh`, frontend `npm run dev`, Entra login nếu `AzureAd:Enabled=true`.

Chi tiết thiết kế: [`docs/poc-ai-dual-flow.md`](../docs/poc-ai-dual-flow.md).

---

## Case 1 — Auto suggestion + Copilot sâu hơn

1. Tab **Employee** → tạo ticket (VD: VPN khi đi nước ngoài).
2. Tab **Support Queue** → mở ticket khi status **Suggested** và có **Auto suggestion**.
3. So sánh block **Auto suggestion (gợi ý sơ bộ)** — thường ngắn / tổng quan.
4. Tab **Support Copilot** → hỏi: "Cho tôi các bước cụ thể để reset VPN."
5. **Kỳ vọng:** Copilot có thể trả lời chi tiết hơn auto suggestion — **đúng thiết kế**, không phải lỗi.

---

## Case 2 — Quyền theo role (Employee vs Agent)

1. Login **Employee** → **Support Copilot** → hỏi ticket người khác (`TCK-xxx` không phải của mình).
2. **Kỳ vọng:** Không tra được ticket nhạy cảm / bị từ chối theo MCP policy.
3. Login **Agent** → hỏi cùng câu.
4. **Kỳ vọng:** Agent xem được (nếu policy cho `get_ticket`).

---

## Case 3 — Agent resolve khi auto-suggestion đang chạy (BC-03)

1. Tạo ticket mới → ticket ở **New**, UI detail có thể hiện *Đang tạo gợi ý…* (saga `GeneratingSuggestion` / `ApplyingSuggestion`).
2. Agent mở **Ticket Detail** ngay → **Resolve** với final answer.
3. Đợi vài giây (saga có thể vẫn đang propose `ProposeTicketSuggestion`).
4. **Kỳ vọng:** Status vẫn **Resolved**, không bị ghi đè thành **Suggested** (`ProposeTicketSuggestion` reject → saga `Discarded`).

---

## Ghi chú nhanh khi demo

| Thuật ngữ UI | Ý nghĩa |
|--------------|---------|
| Auto suggestion | Saga orchestration sau `TicketCreated` → AI worker → `ProposeTicketSuggestion` |
| Support Copilot | Chat tương tác theo role user |

Smoke nhanh (không Entra): `bash scripts/smoke-test.sh`
