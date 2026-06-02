# PoC: Hai luồng AI và luật tranh quyền trên Ticket

Tài liệu này chốt **ý tưởng thiết kế** và **đặc tả hành vi** cho Support PoC khi có đồng thời:

- **Auto Suggestion** — automation nền (saga / pipeline sau `TicketCreated`)
- **Support Copilot** — chat tương tác theo role người dùng

**North star:** Hai flow được phép khác nhau về cách suy luận và công cụ, nhưng **cùng chạm aggregate `Ticket`** thì **TicketService** phải có **luật tranh quyền** rõ ràng. Không tranh “có nên tồn tại song song không”; câu hỏi chuẩn là: *khi automation và agent cùng muốn đổi ticket, ai thắng và hệ thống xử lý thế nào?*

**Liên quan:** [`mini_business_poc.md`](mini_business_poc.md), [`zero-trust-identity.md`](zero-trust-identity.md), [`saga-orchestration-timeout-recovery.md`](saga-orchestration-timeout-recovery.md).

---

## 1. Phạm vi

| Trong phạm vi | Ngoài phạm vi (PoC) |
|---------------|---------------------|
| Định nghĩa hai vai trò AI, naming UI/doc | Gộp saga + chat thành một flow Semantic Kernel duy nhất |
| Luật tranh quyền `Ticket` + epoch/saga | Policy HR theo category + data sensitivity (backlog) |
| P0: lifecycle guard + audit MCP gateway | Hot reload catalog MCP |
| 3 kịch bản demo | Status `NeedsManualReview` (chưa có trong model) |

---

## 2. Hai sản phẩm AI (không gọi chung là “một AI”)

### 2.1 Auto Suggestion (Saga / Pipeline)

| Thuộc tính | Mô tả |
|------------|--------|
| **Kích hoạt** | Sau `TicketCreated` → MassTransit saga (`TicketSuggestionStateMachine`) |
| **Identity** | Service account (client credentials) khi gọi MCP / API |
| **Mục tiêu** | Tạo **gợi ý sơ bộ** (`AiSuggestedAnswer`, related docs) cho agent |
| **Giới hạn** | **Không** tự resolve ticket; không thay agent quyết định cuối |
| **Implementation** | `AiPipelineService` + `RunAiPipelineConsumer`; MCP `search_knowledge` qua `McpToolGateway` (hardcode tên tool); LLM qua `IChatCompletionService` (không bắt buộc qua `Kernel` cho pipeline) |

### 2.2 Support Copilot (AI Chat)

| Thuộc tính | Mô tả |
|------------|--------|
| **Kích hoạt** | `POST /ai/chat` (user tương tác) |
| **Identity** | JWT user — role `Employee` / `Agent` / … |
| **Mục tiêu** | Trợ lý hỏi đáp, tìm tài liệu, tra ticket (theo quyền) |
| **Công cụ** | MCP tools được **lọc policy** trước khi advertise cho model (`McpToolAccessService` + `FunctionChoiceBehavior.Auto`) |
| **Implementation** | `TicketSuggestionService.ChatAsync` + Semantic Kernel `Kernel` + plugin `Mcp` động |

### 2.3 Naming (UI / demo / doc)

| Cũ (dễ gây hiểu nhầm) | Nên dùng |
|----------------------|----------|
| AI Suggested Answer | **Auto suggestion** / **Gợi ý sơ bộ** |
| AI Chat | **Support Copilot** / **Agent Copilot** |

**Lý do:** Stakeholder không kỳ vọng cùng chất lượng câu trả lời, cùng quyền, cùng audit giữa gợi ý tự động lúc tạo ticket và chat khi agent làm việc.

---

## 3. Ownership và kiến trúc

```text
                    ┌─────────────────────┐
                    │   TicketService     │  ← source of truth lifecycle ticket
                    │   SagaEpoch         │
                    │   ActiveSagaCorrelationId
                    └──────────▲──────────┘
                               │
         ┌─────────────────────┼─────────────────────┐
         │                     │                     │
  ┌──────┴──────┐      ┌───────┴────────┐    ┌──────┴──────┐
  │ Saga /      │      │ HTTP resolve/  │    │ Support     │
  │ Pipeline    │      │ reopen (+ MCP  │    │ Copilot     │
  │ (automation)│      │  → Ticket API) │    │ (chat)      │
  └──────┬──────┘      └────────────────┘    └──────┬──────┘
         │                                            │
         └────────────────┬───────────────────────────┘
                          │
                 ┌────────▼────────┐
                 │ McpToolGateway  │  ← P0: audit mọi MCP call
                 └────────┬────────┘
                          │
                 ┌────────▼────────┐
                 │  McpToolServer  │
                 └─────────────────┘
```

| Thành phần | Vai trò |
|------------|---------|
| **TicketService** | Owner duy nhất lifecycle ticket; áp dụng luật tranh quyền |
| **AiOrchestrator (saga)** | Orchestration bước analyzing → AI → save suggestion; **không** sở hữu ticket entity |
| **Semantic Kernel** | Connector + tool loop cho **chat**; pipeline PoC **không** unify hết vào SK |
| **McpToolServer** | Tool implementation + policy contract; **không** duplicate rule lifecycle ticket |

### Trạng thái ticket (model thực tế)

`TicketStatus`: `New`, `Analyzing`, `Suggested`, `Resolved`, `Reopened`.

Saga state (MassTransit): `Analyzing`, `RunningAi`, `Saving`, `Completed`, `Failed`, `Compensated`, … — **layer khác** với status ticket.

**Doc debt:** Một số doc cũ nhắc `NeedsManualReview` — **chưa có** trong code. Khi cần “manual review”, dùng `SagaStopNote` / saga `Failed` hoặc thêm status sau (P2).

---

## 4. Luật tranh quyền trên Ticket (đặc tả P0)

### 4.1 Nguyên tắc

1. **Agent thắng automation** — người chịu trách nhiệm cuối không bị automation “khóa” vĩnh viễn.
2. **Agent không ghi đè tùy tiện** — mọi thay đổi lifecycle phải qua **TicketService**.
3. **Automation không được kéo ticket ngược** sau khi agent đã quyết định — lệnh saga **stale** phải bị chặn.

### 4.2 Cơ chế hiện có (nền)

- `TicketEntity.ActiveSagaCorrelationId` — saga nào đang “giữ” ticket.
- `TicketEntity.SagaEpoch` — tăng khi mark analyzing / compensate; dùng `ExpectedEpoch` trên command.
- Consumers đã stale-check, ví dụ `SaveTicketSuggestionConsumer.IsEpochValid`.

### 4.3 Hành vi đã implement (P0)

Khi agent mutate lifecycle (`resolve`, `reopen`, `PATCH`) và `ActiveSagaCorrelationId != null`:

```text
clear ActiveSagaCorrelationId
SagaEpoch++
ghi status / final answer mới
```

Lệnh saga đến muộn (epoch/correlation không khớp) → stale, **không** ghi đè (vd. `Resolved` → `Suggested`).

**Helper:** `TicketLifecycleMutation.TryMutateStatus` → `TicketSagaOwnership.ApplyAgentLifecycleOverride`.

### 4.4 Đường HTTP / MCP

| Đường | Implementation |
|-------|----------------|
| `POST /tickets/{id}/resolve` | `TicketLifecycleMutation` → `Resolved` + `finalAnswer` |
| `POST /tickets/{id}/reopen` | Cùng helper → `Reopened` |
| `PATCH /tickets/{id}` | Body `{ status, finalAnswer? }` — MCP `update_ticket_status` (non-Resolved) |
| MCP `update_ticket_status` | Không rule riêng; gọi Ticket API ở trên |

### 4.5 PATCH — mọi `TicketStatus` (chủ ý PoC)

`TicketLifecycleMutation` chỉ validate tên status thuộc `TicketStatus.All`. **Không** có ma trận chuyển trạng thái (vd. cho phép `New` → `Resolved` trực tiếp). Đây là **chủ ý PoC** để MCP/UI/agent thử nhanh; production nên thêm allowed transitions.

### 4.6 Race (demo case 3)

Agent resolve/reopen/PATCH trong lúc saga active → bump epoch → `SaveTicketSuggestion` muộn bị bỏ qua.

---

## 5. Cross-cutting (unify mỏng, không unify SK)

### 5.1 Audit MCP tại `McpToolGateway` (P0 — đã implement)

`McpToolGateway.CallToolAsync` + `McpRoleInvocationFilter` (plugin `Mcp` từ SK chat) → event `McpToolInvocation` trên App Insights.

Mọi `CallToolAsync` (pipeline, chat, offline fallback) log **cùng format**:

| Field | Ghi chú |
|-------|---------|
| `source` | `pipeline` \| `chat` \| `offline_chat` |
| `tool` | Tên tool MCP |
| `outcome` | success / error + message ngắn |
| `oid`, `roles` | Khi có `HttpContext` (chat) |
| `sagaCorrelationId` | Pipeline — truyền từ caller (optional parameter) |
| `ticketId` | Parse từ arguments nếu có |

Dùng `McpToolAudit` → Application Insights event `McpToolInvocation`.

**Không** chỉ dựa `IFunctionInvocationFilter` của SK — pipeline không đi qua filter đó.

Caller truyền `McpCallContext` (`source`, `sagaCorrelationId`, `ticketId`); chat SK qua `McpRoleInvocationFilter`.

### 5.2 Tool policy (đã có)

- Policy trên MCP server (`SupportToolPolicy`) → contract `/internal/mcp/tool-policies`.
- Chat: `McpToolAccessService` lọc function advertise cho model.
- Pipeline: service account — **khác actor** với user chat; ghi rõ trong doc/demo, không coi là “lỗi governance” nếu đã thiết kế cố ý.

### 5.3 Semantic Kernel — vai trò PoC

| Thành phần | SK |
|------------|-----|
| `/ai/chat` | `Kernel` + plugin `Mcp` + auto function calling |
| Pipeline classify/generate | `IChatCompletionService` + prompt string |
| Saga orchestration | MassTransit — **không** dùng SK (cố ý demo) |

**Không** yêu cầu PoC: memory, planner, prompt template factory, gộp pipeline vào `Kernel.InvokeAsync`.

### 5.4 Catalog MCP

`McpDynamicPluginLoader` cache catalog process-wide — **cả chat và pipeline** cần restart (hoặc invalidate sau) khi đổi tool trên MCP server. Pipeline `search_knowledge` fail → catch → `[]` (gợi ý yếu, saga có thể vẫn chạy) — không giả định “saga chết hàng loạt” khi đổi tên tool.

---

## 6. Ưu tiên triển khai

| Ưu tiên | Hạng mục | Lý do |
|---------|----------|--------|
| **P0** | Lifecycle guard + MCP audit + `PATCH` + `TicketLifecycleMutation` | **Done** |
| **P1** | Đổi label UI + script demo 3 case | Tiếp theo |
| **P2** | Sửa doc bỏ `NeedsManualReview` hoặc implement status | Doc ↔ code |
| **Backlog** | Policy HR / category + sensitivity | Production thinking, không chặn PoC |

---

## 7. Kịch bản demo (P1)

### Case 1 — Auto suggestion + Copilot sâu hơn

1. Employee tạo ticket (VPN nước ngoài).
2. Saga chạy → ticket `Suggested` + **gợi ý sơ bộ**.
3. Agent mở ticket → **Support Copilot** hỏi chi tiết bước xử lý.
4. **Kỳ vọng:** Hai câu trả lời **có thể khác độ sâu** — đúng thiết kế; UI gọi rõ “Auto suggestion” vs “Copilot”.

### Case 2 — Quyền theo role

1. **Employee** chat: hỏi ticket người khác / `get_ticket` → bị giới hạn policy.
2. **Agent** chat cùng nội dung → xem được (nếu policy cho phép).
3. **Kỳ vọng:** Governance theo **user role** trên copilot; pipeline automation dùng **service account** (giải thích riêng).

### Case 3 — Agent resolve khi saga active

1. Tạo ticket → saga đang chạy (`ActiveSagaCorrelationId` set).
2. Agent **resolve** (UI hoặc MCP) trước khi save suggestion xong.
3. **Kỳ vọng:** Ticket `Resolved`; save suggestion muộn **không** kéo về `Suggested` (P0 guard).

**Bonus (backlog):** Ticket category HR — policy nội dung nhạy cảm theo category + actor.

---

## 8. File code tham chiếu

| File | Liên quan |
|------|-----------|
| `src/TicketService/Data/TicketEntity.cs` | `SagaEpoch`, `ActiveSagaCorrelationId` |
| `src/TicketService/Program.cs` | resolve, reopen, PATCH |
| `src/TicketService/Services/TicketLifecycleMutation.cs` | lifecycle mutate + saga override |
| `src/TicketService/Services/TicketSagaOwnership.cs` | clear saga + bump epoch |
| `src/TicketService/Consumers/SaveTicketSuggestionConsumer.cs` | stale epoch check |
| `src/TicketService/Consumers/MarkTicketAnalyzingConsumer.cs` | set active saga, bump epoch |
| `src/AiOrchestrator/Saga/TicketSuggestionStateMachine.cs` | automation flow |
| `src/AiOrchestrator/Services/AiPipelineService.cs` | auto suggestion logic |
| `src/AiOrchestrator/Services/TicketSuggestionService.cs` | copilot chat |
| `src/AiOrchestrator/Mcp/McpToolGateway.cs` | MCP calls — audit P0 |
| `src/AiOrchestrator/Services/McpToolAudit.cs` | audit helper |
| `src/AiOrchestrator/Mcp/McpToolAccessService.cs` | role → allowed tools (chat) |
| `src/McpToolServer/Tools/SupportTools.cs` | MCP → Ticket HTTP |

---

## 9. Bản chốt một đoạn (handoff)

```text
PoC giữ dual flow: Auto Suggestion (saga, service account) và Support Copilot (chat, role policy).
TicketService owns ticket lifecycle. Agent wins over automation via clear ActiveSagaCorrelationId
and SagaEpoch++ on lifecycle mutate; stale saga commands must not overwrite.
MCP mutate goes through TicketService only; audit all MCP calls at McpToolGateway.
Do not unify entire pipeline into Semantic Kernel for PoC.
UI/doc: separate "Auto suggestion" and "Support Copilot".
P0: lifecycle guard then MCP audit. P1: three demo scenarios. P2: doc/status alignment.
```

---

*Cập nhật: 2026-06-02 — đồng thuận từ review kiến trúc AiOrchestrator (dual flow, contention, PoC scope).*
