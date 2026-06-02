# Zero Trust Identity — mục tiêu, tài liệu tham chiếu, tiến trình

Tài liệu này là **điểm vào (entry point)** cho mọi AI/coder tiếp tục triển khai bảo mật identity trên repo `ProjectThem5`. Đọc file này trước khi sửa Terraform, backend JWT, hoặc frontend MSAL.

**Phạm vi PoC:** Tenant **Entra ID Free** — demo Identity **dừng tại §5 (close criteria)**. PIM, Identity Protection, Conditional Access là **out-of-scope** (§14–§15), không phải việc “chưa làm”.

## 0. Trạng thái chốt PoC (2026-06-01)

| | |
|---|---|
| **Trạng thái** | **Đã chốt** — Identity PoC đủ tiêu chí §5; không còn TODO code bắt buộc trên tenant Free |
| **Tenant** | `binhthedevgmail.onmicrosoft.com` (Entra ID Free) |
| **Verify nhanh** | `bash scripts/identity-hardening-check.sh` + `bash scripts/smoke-test.sh` (re-index: thêm `SMOKE_BEARER_TOKEN` hoặc UI) |

**Đã làm (in-scope PoC):** Entra apps + JWT/policies + MSAL + S2S client credentials + MCP inbound `/mcp` + MCP tool policy contract + SK chỉ advertise allowed tools + `OwnerOid` / `/tickets/mine`.

**Out-of-scope (không tính “chưa làm”):** Conditional Access (§14, P1+), PIM/JIT (§15.1, P2), Identity Protection (§15.2, P2).

**Optional sau này (không chặn chốt doc):** khóa `/debug/*` khi deploy ngoài localhost; thu hẹp CORS; prod tách 3 OID bootstrap; cập nhật `user_stories.md` acceptance auth; xem §13 về hướng production cho Employee-scoped MCP tool.

### Mục lục (thứ tự đọc)

| § | Nội dung |
|---|----------|
| 1–3 | Mục tiêu, kiến trúc, app roles |
| 4 | Checklist phase |
| **5** | **Tiêu chí đóng PoC** |
| 6–8 | Chạy thử, Terraform, config |
| 9 | Map code / endpoint |
| 10–11 | Handoff MSAL, E2E |
| 12 | Ghi chú cho AI |
| 13 | Employee-only authorization test / MCP gap |
| 14–15 | CA deferred, PIM / Identity Protection (prod-only) |

## 1. Mục tiêu lớn đang làm

Chúng ta đưa **Internal Support Knowledge Assistant PoC** từ “API mở, không đăng nhập” sang mô hình **Zero Trust Identity** theo Microsoft:

- **Verify explicitly** — mọi người dùng và service phải xác thực qua **một** control plane: **Microsoft Entra ID**.
- **Least privilege** — quyền theo vai trò nghiệp vụ (Employee / Agent / Knowledge Admin / Service), không tin localhost hay mạng nội bộ.
- **Assume breach** — service-to-service (MCP → API) dùng **client credentials**, không gọi HTTP trần.

Đây là **trụ cột Identity** trong Zero Trust; các trụ khác (Network, Application, Data…) là phase sau.

### Tài liệu Microsoft (tham chiếu bắt buộc cho AI sau)

| Chủ đề | Link |
|--------|------|
| Identity pillar (mục tiêu triển khai) | https://learn.microsoft.com/en-us/security/zero-trust/deploy/identity |
| Applications pillar (SSO, tích hợp app) | https://learn.microsoft.com/en-us/security/zero-trust/deploy/applications |
| Memo 22-09 + Entra (MFA, device signal) | https://learn.microsoft.com/en-us/entra/standards/memo-22-09-meet-identity-requirements |
| Conditional Access planning | https://learn.microsoft.com/en-us/entra/identity/conditional-access/plan-conditional-access |
| Terraform azuread provider | https://registry.terraform.io/providers/hashicorp/azuread/latest/docs |

### Tài liệu trong repo liên quan

| File | Nội dung |
|------|----------|
| [`infra/terraform/README.md`](../infra/terraform/README.md) | Cách `terraform apply`, output Entra |
| [`infra/terraform/identity.tf`](../infra/terraform/identity.tf) | IaC Entra (apps, roles, scopes, bootstrap) |
| [`docs/mini_business_poc.md`](mini_business_poc.md) | Nghiệp vụ PoC (actor Employee / Agent / Admin) |
| [`docs/user_stories.md`](user_stories.md) | Acceptance criteria nghiệp vụ; auth/role xem file này §9 |
| [`scripts/sync-config.sh`](../scripts/sync-config.sh) | Đồng bộ `config/azure.local.json` → appsettings / user-secrets |
| [`config/entra-browser-session.local.json`](../config/entra-browser-session.local.json) | Handoff phiên MSAL đã login (gitignored) — xem §10 |
| [`config/entra-browser-session.local.json.example`](../config/entra-browser-session.local.json.example) | Mẫu cấu trúc handoff (không secret) |
| [`infra/app-insights/mcp-tool-invocation.kql`](../infra/app-insights/mcp-tool-invocation.kql) | KQL queries cho audit MCP trên App Insights |
| [`src/Shared/Auth/`](../src/Shared/Auth/) | JWT, policies, client credentials dùng chung |

---

## 2. Kiến trúc identity (demo bài bản)

```text
                    Microsoft Entra ID
                    (tenant — IaC: Terraform)
                              |
         +--------------------+--------------------+
         |                    |                    |
    SPA (PKCE)           API resource           MCP Service
  support-poc-spa      support-poc-api         (client secret)
  delegated scope      app roles + JWT         Support.Service
  access_as_user       validation              app role
         |                    ^                    |
         +-------- REST + Bearer ------------------+
```

- **Một IAM** — không thêm auth song song; PoC tắt Entra bằng `enable_entra_identity = false` chỉ khi dev thuần offline.
- **Hai loại token**:
  - **User delegated** — SPA → API (`scope`: `api://…/access_as_user`).
  - **Application** — MCP Service → API (app role `Support.Service`).

---

## 3. App roles Terraform tạo ra (ma trận demo)

Các role được định nghĩa trong [`identity.tf`](../infra/terraform/identity.tf) trên application **Support PoC API**. Giá trị claim JWT: `roles` (app roles).

| App role (`value`) | Actor PoC | Quyền demo (mục tiêu code) | Member types |
|--------------------|-----------|----------------------------|--------------|
| `Support.Employee` | Employee | Tạo ticket, AI chat (read), xem ticket của mình | User |
| `Support.Agent` | Support Agent | Queue, resolve/reopen, gợi ý AI, xem mọi ticket | User |
| `Support.KnowledgeAdmin` | Knowledge Admin | CRUD tài liệu, re-index | User |
| `Support.Service` | MCP / Orchestrator | Gọi API downstream với client credentials | Application |

### Bootstrap (gán user demo sau `terraform apply`)

Mỗi app role có **principal riêng** (Entra object ID). Biến Terraform / env:

| Role | Terraform | Env (`provision-entra.sh`) |
|------|-----------|----------------------------|
| `Support.Employee` | `bootstrap_employee_principal_id` | `BOOTSTRAP_EMPLOYEE_ID` |
| `Support.Agent` | `bootstrap_agent_principal_id` | `BOOTSTRAP_AGENT_ID` |
| `Support.KnowledgeAdmin` | `bootstrap_knowledge_admin_principal_id` | `BOOTSTRAP_KNOWLEDGE_ADMIN_ID` |

Nếu biến role-specific **trống** → fallback `bootstrap_user_id` / `BOOTSTRAP_USER_ID` (PoC dev: một user chạy full demo). Production: set 3 OID khác nhau và `allow_bootstrap_multi_role_principal=false`.

### OAuth2 scope (delegated)

| Scope | Client | Mục đích |
|-------|--------|----------|
| `access_as_user` | SPA | User đăng nhập; API nhận token on-behalf-of user |

Application ID URI (audience): `api://{prefix}-{suffix}-supportpoc` (ví dụ `api://supportpoc-tf01-supportpoc`).

MSAL scope đầy đủ: `{identifierUri}/access_as_user` — xem output `entra_scope_access_as_user` sau `apply`.

### Ứng dụng Entra (Terraform tạo)

| App | Terraform resource | Mục đích |
|-----|-------------------|----------|
| `{prefix} API ({suffix})` | `azuread_application.api` | Resource server — JWT, app roles |
| `{prefix} SPA ({suffix})` | `azuread_application.spa` | React MSAL PKCE |
| `{prefix} MCP Service ({suffix})` | `azuread_application.mcp_service` | Client secret; role `Support.Service` |

SPA được **pre-authorized** (`azuread_application_pre_authorized`) để giảm friction consent trong PoC.

---

## 4. Tiến trình triển khai (checklist)

Cập nhật trạng thái khi hoàn thành từng bước.

| Phase | Mục tiêu ZT (Microsoft) | Việc | Trạng thái |
|-------|-------------------------|------|------------|
| **0** | — | IaC Entra: `identity.tf` (tham chiếu); provision thực tế: `scripts/provision-entra.sh` | **Done (az cli)** |
| **1** | I — Identity foundation | `provision-entra.sh` + `sync-config.sh`; xác nhận apps trên Entra portal | **Done (az cli)** |
| **2** | I — Verify explicitly | Backend JWT + policies (Ticket, Knowledge, AiOrchestrator) | **Done (PoC)** |
| **3** | I — Integrate apps | Frontend: MSAL popup + hiển thị token (tab **Entra / Login**) | **Done (PoC)** |
| **4** | I — Least privilege (service) | MCP `HttpClient` dùng client credentials; API chấp nhận `Support.Service` | **Done (PoC)** |
| **4b** | I — Assume breach (MCP inbound) | `/mcp` yêu cầu JWT `Support.Service`; AiOrchestrator gọi MCP qua `EntraBearerTokenHandler` | **Done (2026-06-01)** |
| **5** | II — Conditional Access | Entra CA: MFA, block legacy (portal / TF phase 2) | **Out of scope (PoC)** — cần Entra ID **P1+** (xem §14) |
| **6** | III — Analytics | Audit log structured `McpToolInvocation` + App Insights custom event + KQL | **Done (2026-06-01)** |
| **7** | IV — Governance | Tách bootstrap roles; MCP tool policy contract theo role | **Done (PoC)** — bootstrap tách principal (env/TF) |
| **7b** | IV — Governance (prod) | PIM / JIT cho `Support.Agent`, `Support.KnowledgeAdmin` | **Out of scope (PoC)** — cần Entra ID **P2**; xem §15.1 |
| **—** | UX / ticket ownership (PoC) | Banner login, lỗi 401/403; `OwnerOid` + `/tickets/mine` | **Done (2026-06-01)** |
| **8** | V–VI — Risk / Defender | Identity Protection, device compliance CA | **Out of scope (PoC)** — cần Entra ID **P2**; xem §15.2 |

### Liên kết review kỹ thuật khác

- **Semantic Kernel / MCP**: `/ai/chat` đã gắn JWT; allowed tools lấy từ MCP policy contract rồi đưa vào `FunctionChoiceBehavior.Auto(functions: ...)` — xem §9.

---

## 5. Tiêu chí đóng PoC (close criteria)

Phase **Identity PoC được coi là hoàn tất** khi đạt đủ:

1. Role matrix được enforce ở API (`Employee`, `Agent`, `KnowledgeAdmin`, `Service`).
2. MCP service gọi downstream bằng client credentials (`Support.Service`), không HTTP trần.
3. Employee chỉ đọc ticket của mình (`OwnerOid` / `/tickets/mine`).
4. MCP tool exposure có policy contract theo role; Employee không được advertise `get_ticket` chung.
5. Frontend hiển thị rõ nhắc login và lỗi 401/403.

### Hardening — PoC vs Production

**PoC (tenant Free) — đã xong:**

- Bootstrap role assignment — cấu trúc env/TF tách principal; PoC dev có thể 1 user nhiều role (WARN từ `identity-hardening-check.sh` là **chấp nhận được**).
- Audit MCP — App Insights custom event `McpToolInvocation` + KQL.

**Optional (không chặn chốt doc):**

- Khóa `/debug/*` khi deploy ngoài localhost (hiện `AllowAnonymous` — chỉ dev).
- Thu hẹp CORS `AllowAnyOrigin` khi có origin cố định prod.

**Production-only (out-of-scope PoC — §14–§15):**

- Conditional Access (MFA + block legacy) — **P1+**.
- PIM/JIT — **P2**.
- Identity Protection + device compliance CA — **P2** (+ CA **P1**).

### Quy tắc bảo trì

1. **Không** thêm script `az ad` song song Terraform — Entra SSOT: `identity.tf` / `provision-entra.sh`.
2. Khi sửa role names: `identity.tf` → §3 doc → `AppRoleNames.cs` → policies → (tuỳ chọn) `user_stories.md`.
3. JWT audience: app-only token thường `aud={clientId}`; delegated có thể `api://{clientId}` — giữ `ValidAudiences` trong `EntraAuthExtensions.cs`.
4. **Không commit** `config/azure.local.json`, `config/entra-browser-session.local.json`, `appsettings.Development.json`, `.env.local`.
5. Không có mock auth trong repo — `SMOKE_BEARER_TOKEN` là token thật tùy chọn cho script.
6. Chạy `bash scripts/identity-hardening-check.sh` trước khi merge branch identity.

---

## 6. Kiểm tra đăng nhập (frontend + backend)

```bash
bash scripts/provision-entra.sh
bash scripts/sync-config.sh
cd frontend && npm install && npm run dev
```

http://localhost:5173 **hoặc** http://127.0.0.1:5173 → **Entra / Login** → **Đăng nhập Microsoft** → xem token API (JWT decode).

Sau login, test backend thật:

1. Tab **Employee** → tạo ticket (phải thành công; không login → `Unauthorized`).
2. Tab **Support Queue** → thấy ticket mới.
3. Tab **AI Chat** → gửi câu hỏi — auth + MCP chain **OK** (§11); nội dung trả lời phụ thuộc Azure OpenAI config hoặc MCP fallback.

Chi tiết frontend: [`../frontend/README.md`](../frontend/README.md).

## 7. Vận hành nhanh (sau khi có Terraform)

```bash
cd infra/terraform
terraform init
terraform apply

cd ../..
bash scripts/sync-config.sh
# Kiểm tra config/azure.local.json có section "entra"
```

Tắt Entra trong TF (chỉ Azure workload):

```bash
terraform apply -var="enable_entra_identity=false"
```

---

## 8. Config sinh ra (cho code bước sau)

Sau `apply`, `config/azure.local.json` (gitignored) có dạng:

```json
{
  "entra": {
    "tenantId": "...",
    "authority": "https://login.microsoftonline.com/...",
    "api": { "clientId": "...", "audience": "api://...", "scopeFull": "api://.../access_as_user" },
    "spa": { "clientId": "...", "redirectUris": ["http://localhost:5173/", "http://127.0.0.1:5173/"] },
    "mcpService": { "clientId": "...", "clientSecret": "..." },
    "appRoles": { "Support.Employee": { ... }, ... }
  }
}
```

**Không commit** file này hoặc client secret.

Frontend (bước 3) cần `.env.local` (gitignored), ví dụ:

```env
VITE_AAD_TENANT_ID=<tenantId>
VITE_AAD_CLIENT_ID=<spa.clientId>
VITE_AAD_API_SCOPE=<api.scopeFull>
```

---

## 9. Code đã implement (map file → hành vi)

Đọc section này trước khi sửa auth. Toggle: `AzureAd:Enabled` trong `appsettings.json` (mặc định `false`) hoặc `appsettings.Development.json` sau `sync-config.sh` (`true` khi có Entra).

### Shared (`src/Shared/Auth/`)

| File | Vai trò |
|------|---------|
| `AppRoleNames.cs` | Hằng số role — đồng bộ với `identity.tf` |
| `PolicyNames.cs` | Tên policy ASP.NET |
| `EntraAuthExtensions.cs` | `AddSupportPocEntraAuth`, `WithEntraPolicy`, JWT `ValidAudiences` (aud = clientId **và** api://…) |
| `ClientCredentialsTokenProvider.cs` | OAuth2 client credentials + `EntraBearerTokenHandler` (`McpClientSecret` hoặc `ClientSecret`) |
| `EntraTicketAccess.cs` | Kiểm tra đọc ticket theo `OwnerOid` / username |

### Telemetry (`src/Shared/Telemetry/`)

| File | Vai trò |
|------|---------|
| `TelemetryExtensions.cs` | `AddSupportPocApplicationInsights` — bật khi `ApplicationInsights:ConnectionString` có giá trị |
| `ApplicationInsightsOptions.cs` | Section config |
| [`infra/app-insights/mcp-tool-invocation.kql`](../infra/app-insights/mcp-tool-invocation.kql) | KQL: allowed/denied, top tools, alert gợi ý |

| Service | Endpoint | Policy |
|---------|----------|--------|
| **TicketService** | `POST /tickets` | `UserOrService` — user: `OwnerOid` + `preferred_username` |
| | `GET /tickets` | `AgentOrService` |
| | `GET /tickets/mine` | `EmployeeOrAbove` — filter theo `OwnerOid` |
| | `GET /tickets/{id}` | `UserOrService` — Employee chỉ ticket của mình |
| | `POST …/resolve`, `…/reopen` | `AgentOrService` |
| | `GET /internal/…/saga-progress` | `Service` |
| | `/health`, `/debug/*` | Anonymous |
| **KnowledgeService** | `GET /documents`, `/search`, `/categories` | `UserOrService` |
| | `POST /documents`, `/documents/reindex` | `KnowledgeAdmin` |
| | `/health`, `/debug/idempotency` | Anonymous |
| **AiOrchestrator** | `POST /ai/chat` | `EmployeeOrAbove` — lấy MCP tool policy contract rồi chỉ advertise allowed functions |
| | `POST /ai/suggest-answer` | `AgentOrService` |
| | `POST /ai/classify-ticket` | `Service` |
| | `GET /mcp/tools` | `AgentOrService` |
| | `GET /mcp/allowed-tools` | `EmployeeOrAbove` — debug allowed tools theo roles hiện tại |
| | `/health`, `/debug/*` | Anonymous |
| **McpToolServer** | `GET /health` | Anonymous |
| | `GET /internal/mcp/tool-policies` | `Service` — source of truth tool-level policy contract |
| | MCP → Ticket/Knowledge HTTP | Bearer outbound (client credentials) |
| | **inbound** `POST /mcp` | JWT `Support.Service` khi Entra bật |
| **AiOrchestrator → MCP** | Named HttpClient `mcp-server` | `EntraBearerTokenHandler` |

**Ghi chú:** Khi `AzureAd:Enabled=false`, policies không áp dụng — API mở cho dev offline.

### Semantic Kernel + MCP least privilege

| File | Vai trò |
|------|---------|
| `AiOrchestrator/Options/AzureOpenAIOptions.cs` | `ChatEnabled` — tắt SK chat khi endpoint lỗi; fallback `OfflineChatAsync` (MCP) |
| `McpToolServer/Tools/SupportToolPolicyAttribute.cs` | Attribute gắn trực tiếp lên MCP tool method: role/risk/notes |
| `McpToolServer/Tools/SupportToolPolicyCatalog.cs` | Reflect `[McpServerTool(Name=...)]` + `[SupportToolPolicy]` để sinh policy contract |
| `McpToolServer/Tools/SupportTools.cs` | Source of truth co-located: implementation + explicit tool name + tool policy |
| `Shared/Contracts/McpToolPolicyDto.cs` | Contract DTO để MCP server trả tool policy cho AiOrchestrator |
| `AiOrchestrator/Mcp/McpToolAccessService.cs` | Lấy tool policies từ MCP server, validate với `tools/list`, chọn allowed functions cho SK |
| `AiOrchestrator/Mcp/McpToolPolicyCatalog.cs` | Role → allowed tool names; fail-fast nếu tool/policy lệch |
| `AiOrchestrator/Services/McpRoleInvocationFilter.cs` | Hiện intentionally pass-through; reserved cho human approval/DLP/risk scoring/rate limit trước khi invoke tool |
| `AiOrchestrator/Services/McpToolAudit.cs` | Helper audit + `TrackInvocation` → App Insights custom event |
| `AiOrchestrator/Mcp/McpToolGateway.cs` | HttpClient `mcp-server` + Bearer tới McpToolServer |
| `McpToolServer/Tools/SupportTools.cs` | Named HttpClient `ticket-api` / `knowledge-api` + Bearer |

---

## 10. Handoff phiên đăng nhập (cho AI tiếp theo)

Sau khi user login Entra trên frontend, tạo/cập nhật (gitignored):

**`config/entra-browser-session.local.json`**

Mẫu: [`config/entra-browser-session.local.json.example`](../config/entra-browser-session.local.json.example)

| Field | Dùng để |
|-------|---------|
| `account` | oid, roles, username |
| `entraApps` | clientId, scope, authority |
| `apiAccessToken` | curl/scripts (~1h); hết hạn → UI **Làm mới token** |
| `e2eResults` | Bước đã verify |
| `_handoff.nextStepsForAi` | Việc còn lại |

**Vì sao refresh không cần login lại:** MSAL lưu refresh token trong `sessionStorage` (`msal.*`). Đóng tab = mất session.

**Redirect URI (lỗi AADSTS50011):** Entra SPA phải có **cả hai**:

- `http://localhost:5173/`
- `http://127.0.0.1:5173/`

(`provision-entra.sh` và Terraform `spa_redirect_uris` đã cập nhật.)

---

## 11. Kiểm tra E2E (đã chạy thật)

Prerequisite: `bash scripts/restart-services.sh`, frontend `npm run dev`, user login tab **Entra / Login**.

| Bước | Kết quả (2026-06-01) |
|------|----------------------|
| MSAL login + token API | OK — roles Employee/Agent/KnowledgeAdmin |
| Employee → tạo ticket | OK — TCK-044, Bearer → 201 |
| Support Queue | OK — list tickets với Agent role |
| AI Chat auth + MCP chain | OK — browser 2026-06-01; `/mcp` inbound JWT; smoke-test `/mcp/tools` Bearer |
| UX login banner / 401 | **OK** — `AuthRequiredBanner`, `api.ts` format lỗi |
| `/tickets/mine` | **OK** — gắn `OwnerOid` khi tạo ticket |
| Re-index (KnowledgeAdmin) | **OK qua UI** (tab Knowledge + login); smoke script cần `SMOKE_BEARER_TOKEN` |

### Smoke test với Entra bật

```bash
bash scripts/smoke-test.sh
# Tự lấy client credentials (Support.Service) cho ticket/MCP
# Re-index skip nếu chưa set SMOKE_BEARER_TOKEN (user token)

export SMOKE_BEARER_TOKEN="<delegated JWT tu UI hoac entra-browser-session.local.json>"
bash scripts/smoke-test.sh   # chạy cả re-index
```

Tắt Entra (dev offline): `AzureAd:Enabled=false` trong appsettings — API mở, smoke test như cũ.

---

## 12. Ghi chú cho AI kế tiếp

**Đọc theo thứ tự:** §0 trạng thái chốt → §4 checklist → §5 close criteria → §9 code map → §10 handoff → §11 E2E → §13 test/gap → §15 (prod-only).

### ⚠️ Quan trọng — đừng đánh giá sai trạng thái PoC

**Phase Identity PoC đã chốt (§0, §5).** Tenant **Entra ID Free** — demo **cố ý dừng ở đây**.

Các mục sau **KHÔNG phải “chưa làm” / gap / TODO code** — chúng là **quyết định out-of-scope** cho PoC, chỉ áp dụng khi lên production và nâng license:

| Mục | Trạng thái đúng | License cần | Chi tiết |
|-----|-----------------|-------------|----------|
| Conditional Access (Phase 5) | **Deferred / out-of-scope PoC** | Entra ID **P1+** | §14 |
| PIM / JIT (Phase 7b) | **Out-of-scope PoC** | Entra ID **P2** | §15.1 |
| Identity Protection / Defender (Phase 8) | **Out-of-scope PoC** | Entra ID **P2** | §15.2 |

**AI sau:** Không liệt kê PIM, Identity Protection, Conditional Access vào “việc còn lại” trừ khi user **chủ động** yêu cầu triển khai production hoặc đã nâng license. Không đánh giá PoC “chưa hoàn thiện” vì thiếu các tính năng premium này.

### Việc ưu tiên còn lại (trong phạm vi PoC Free)

1. ~~**Azure OpenAI chat local**~~ — đã thêm `AzureOpenAI:ChatEnabled`.
2. ~~**MCP inbound auth**~~ — `/mcp` JWT `Support.Service`.
3. ~~**Phase 6 App Insights**~~ — custom event + KQL + `sync-config.sh`.
4. ~~**Phase 7 bootstrap structure**~~ — env/TF tách principal; prod: set 3 OID khác nhau.
5. ~~**Phase 7 MCP tool policy theo role**~~ — MCP server trả `/internal/mcp/tool-policies`; SK chỉ advertise allowed functions; `McpRoleInvocationFilter` pass-through/reserved.
6. **Phase 5 Conditional Access** — **out-of-scope PoC** (§14); bật khi có Entra ID P1+.
7. **Phase 7b PIM / Phase 8 Identity Protection** — **out-of-scope PoC** (§15); bật khi có Entra ID P2+.

## 13. Employee-only authorization test / MCP gap (2026-06-01)

Section này ghi lại test thực tế với guest user thấp quyền để AI/coder sau không đánh giá nhầm giữa **direct API authorization** và **AI/MCP tool authorization**.

### Setup test user

Guest user:

| Field | Value |
|-------|-------|
| Email | `binh.voquoc@gmail.com` |
| Entra display name | `Test Employee` |
| Object ID | `aa728322-0973-4a95-93e0-cae072279b35` |
| App role assigned | `Support.Employee` only |

JWT API sau login frontend:

```json
{
  "preferred_username": "binh.voquoc@gmail.com",
  "oid": "aa728322-0973-4a95-93e0-cae072279b35",
  "roles": ["Support.Employee"],
  "scp": "access_as_user",
  "aud": "dacbf464-d15e-499c-bc5d-6ee94ca26e7d"
}
```

Session handoff đã lưu tại `config/entra-browser-session.local.json` (gitignored). Token hết hạn theo field `apiAccessTokenExpiresLocal`.

### Kết quả test direct API

| Test | Kết quả | Ý nghĩa |
|------|---------|---------|
| `GET /tickets` bằng Employee-only | `403 Forbidden` | Policy `AgentOrService` chặn đúng queue toàn cục |
| `GET /tickets/TCK-047` bằng Employee-only | `403 Forbidden` | `CanReadTicket(...)` chặn ticket không thuộc owner |
| `POST /tickets` với body giả `employeeId=victim@example.com` | `201 Created`, nhưng response `employeeId=binh.voquoc@gmail.com` | Backend không tin identity từ frontend |

Code chặn đọc ticket:

```csharp
if (entraEnabled && !EntraTicketAccess.CanReadTicket(entity.OwnerOid, entity.EmployeeId, httpContext.User))
    return Results.Forbid();
```

Biến debug đại diện:

```text
TCK-047:
  EmployeeId = binh.thedev@gmail.com
  OwnerOid   = zQd2L7HoUYX8af-KA2bgxeYvhOh0yn8x-gB9Pg1LQK8

Current user:
  preferred_username = binh.voquoc@gmail.com
  oid                = aa728322-0973-4a95-93e0-cae072279b35
  roles              = [Support.Employee]

CanReadTicket(...) => false
HTTP => 403
```

Code ép identity khi tạo ticket:

```csharp
var employeeId = isService
    ? request.EmployeeId.Trim()
    : (httpContext.User.FindFirstValue("preferred_username")
       ?? httpContext.User.Identity?.Name
       ?? request.EmployeeId.Trim());

OwnerOid = isService ? null : ownerOid;
```

### Gap cũ: AI chat qua MCP từng đọc được ticket chéo

Test:

```text
POST /ai/chat
message = "Cho toi xem ticket TCK-047"
user = binh.voquoc@gmail.com
roles = [Support.Employee]
```

Kết quả trước refactor:

```text
200 OK
reply = "Ticket TCK-047 dang o trang thai New..."
```

Nguyên nhân theo code:

1. `/ai/chat` cho Employee vào endpoint:

```csharp
}).WithEntraPolicy(entraEnabled, PolicyNames.EmployeeOrAbove);
```

2. Employee allowlist cũ có `get_ticket` trong AiOrchestrator:

```csharp
public static readonly HashSet<string> EmployeeReadOnly =
[
    "get_ticket",
    "search_knowledge",
    "list_support_categories",
];
```

3. MCP ToolServer gọi TicketService bằng service token:

```csharp
registration.AddHttpMessageHandler<EntraBearerTokenHandler>();
```

4. TicketService coi `Support.Service` là privileged reader:

```csharp
public static bool IsPrivilegedReader(ClaimsPrincipal user) =>
    user.IsInRole(AppRoleNames.Service)
    || user.IsInRole(AppRoleNames.Agent)
    || user.IsInRole(AppRoleNames.KnowledgeAdmin);
```

Do đó, direct API đã chặn đúng Employee-only, nhưng đường AI/MCP hiện bypass ownership vì downstream chỉ thấy `Support.Service`, không thấy user gốc.

### Refactor đã áp dụng: MCP policy contract + SK allowed functions

Sau refactor, source of truth tool-level không còn là `McpToolAllowlists.cs` bên AiOrchestrator. MCP ToolServer expose contract:

```csharp
app.MapGet("/internal/mcp/tool-policies", () => Results.Ok(SupportToolPolicyCatalog.FromToolType<SupportTools>()))
    .WithEntraPolicy(entraEnabled, PolicyNames.Service);
```

Policy hiện tại nằm ngay trên method MCP tool:

```csharp
[McpServerTool(Name = "search_knowledge", ReadOnly = true, OpenWorld = false)]
[SupportToolPolicy(
    SupportToolRisks.Low,
    AppRoleNames.Employee,
    AppRoleNames.Agent,
    AppRoleNames.KnowledgeAdmin,
    Notes = "Read-only knowledge search...")]
public async Task<string> SearchKnowledge(...)

[McpServerTool(Name = "get_ticket", ReadOnly = true, OpenWorld = false)]
[SupportToolPolicy(
    SupportToolRisks.Medium,
    AppRoleNames.Agent,
    Notes = "Privileged ticket lookup...")]
public async Task<string> GetTicket(...)
```

AiOrchestrator lấy policy từ MCP server, validate với `tools/list`, rồi chỉ advertise function được phép:

```csharp
var policies = new McpToolPolicyCatalog(await gateway.ListToolPoliciesAsync(cancellationToken));
policies.ValidateAgainst(catalog);

var allowedNames = policies.AllowedToolsForRoles(roles);
var functions = plugin
    .Where(function => allowedNames.Contains(function.Name))
    .ToList();

FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(functions: allowedFunctions);
```

Biến debug đại diện sau refactor:

```text
user roles   = [Support.Employee]
allowedTools = [search_knowledge, list_support_categories]

LLM advertised functions:
- search_knowledge
- list_support_categories

not advertised:
- get_ticket
- create_ticket
- update_ticket_status
```

Fallback local cũng fail closed:

```csharp
var allowedTools = await _toolAccess.GetAllowedToolNamesAsync(roles ?? [], cancellationToken);
if (!allowedTools.Contains(toolName))
    return $"Tai khoan hien tai khong duoc dung MCP tool '{toolName}' de doc ticket...";
```

### Hướng production cho Employee-scoped ticket reads

Chọn một trong các hướng sau, tùy mục tiêu sản phẩm:

1. Tạo tool riêng `get_my_ticket` cho Employee.
2. Truyền user context chuẩn từ AiOrchestrator sang MCP/TicketService bằng OBO token hoặc signed actor context.
3. TicketService vẫn là nơi quyết resource-level authorization (`CanReadTicket`) vì nó sở hữu `OwnerOid`.
4. Tránh để service-token path biến mọi read thành privileged read nếu request gốc là user thấp quyền.

**Ghi chú:** Tool-level source of truth là MCP ToolServer. Resource-level source of truth vẫn là TicketService.

## 14. Phase 5 — Conditional Access (deferred)

**Quyết định PoC:** Bỏ qua MFA/CA trong code hiện tại; không block login dev. Khi cần Zero Trust đầy đủ → bật lại theo hướng dẫn dưới.

Đã thử tạo policy qua Microsoft Graph (`POST /policies/conditionalAccessPolicies`, state=disabled):

```text
AccessDenied: Your tenant is not licensed for this feature. Please upgrade your subscription to access it.
```

Tenant hiện tại: `88a56b4b-d214-4a74-bb3d-aacc38429f62` (`binhthedevgmail.onmicrosoft.com`). Danh sách CA policies rỗng; đọc Security Defaults cũng bị Forbidden (thiếu scope/license).

**Cần từ bạn trước khi làm tiếp Phase 5:**

1. Nâng tenant lên **Microsoft Entra ID P1** (hoặc P2 / M365 E3+) — Conditional Access là tính năng premium.
2. Hoặc bật **Security defaults** trên portal (Entra → Properties → Manage security defaults) nếu chấp nhận MFA toàn tenant (không granular như CA).
3. Sau khi có license: thêm `azuread_conditional_access_policy` trong `identity.tf` (MFA users + block legacy auth) hoặc làm thủ công portal theo [CA planning guide](https://learn.microsoft.com/en-us/entra/identity/conditional-access/plan-conditional-access).

**Không block Phase 6–7:** App Insights dashboard và tách bootstrap roles không phụ thuộc license CA.

## 15. Production-only — PIM/JIT và Identity Protection (kiến thức tham chiếu)

> **Ghi chú cho AI:** Section này là **tài liệu tham chiếu / quay xe sau**, không phải checklist “còn thiếu” của PoC. Demo trên tenant **Free** **cố ý không triển khai** các mục dưới. Khi user nâng license và chuyển production → áp dụng theo §15.

### 15.1 PIM / JIT (Privileged Identity Management)

**Là gì:** PIM quản lý quyền **đặc quyền** (privileged). JIT = **Just-In-Time** — cấp quyền đúng lúc cần, tự hết hạn sau vài giờ thay vì gán role vĩnh viễn 24/7.

**Liên quan Zero Trust:** Mở rộng **Least privilege** — không chỉ “cho quyền gì?” mà còn “cho bao lâu?”. Giảm blast radius nếu tài khoản Agent/Admin bị chiếm.

**Liên quan PoC của chúng ta:** App roles `Support.Agent`, `Support.KnowledgeAdmin` hiện gán trực tiếp (hoặc bootstrap qua Terraform). PoC chấp nhận 1 user nhiều role để dev/test nhanh.

**Làm gì khi lên production (không sửa code app):**

1. Nâng tenant lên **Microsoft Entra ID P2** (PIM là tính năng P2).
2. Gán app role qua **Security Group** thay vì user trực tiếp; bật PIM trên group/role assignment.
3. Agent/Admin **Activate** quyền trên portal PIM trước ca trực (có thể yêu cầu MFA + lý do + approval).
4. JWT sau khi activate vẫn chứa claim `roles` như hiện tại — backend **không cần đổi** policy/filter.

**Vì sao không làm trong PoC Free:**

- License **P2** (chi phí cao hơn P1).
- Ma sát dev: cứ vài giờ phải activate lại role → chậm test.
- PoC chỉ cần chứng minh RBAC + MCP tool policy contract; PIM là lớp governance prod.

**Tài liệu:** [What is Privileged Identity Management?](https://learn.microsoft.com/en-us/entra/id-governance/privileged-identity-management/pim-configure)

### 15.2 Identity Protection / Risk & Defender

**Là gì:** Dịch vụ ML của Microsoft phân tích hàng tỷ sign-in để phát hiện hành vi bất thường và gán **risk score**:

- **Sign-in risk:** đăng nhập từ Tor/VPN lạ, impossible travel (VN → US trong 10 phút), brute force.
- **User risk:** credential bị lộ (phát hiện trên dark web), tài khoản bị compromise.

**Liên quan Zero Trust:** **Verify explicitly** — đúng password chưa đủ; hành vi lạ → ép MFA, block, hoặc bắt reset password.

**Kết hợp Conditional Access (P1+):** Policy ví dụ: `Sign-in risk = high` → require MFA hoặc block; `User risk = high` → force password change.

**Device compliance (Defender for Endpoint / Intune):** CA có thể yêu cầu thiết bị “compliant” (antivirus, patch, không malware) trước khi cấp token gọi API.

**Làm gì khi lên production (chủ yếu portal + CA, không sửa code PoC):**

1. Nâng tenant **Entra ID P2** (Identity Protection).
2. Bật risk policies trên Entra → Identity Protection.
3. Tạo CA policies dùng điều kiện `signInRiskLevels` / `userRiskLevels` (§14 + P1 CA).
4. (Tuỳ chọn) Intune/Defender cho device compliance signal.

**Vì sao không làm trong PoC Free:**

- License **P2**.
- Dev/PoC sign-in từ localhost/IP cố định — AI risk engine không có dữ liệu hành vi thực để học.
- Khó/không cần mô phỏng “dark web leak” hay “infected device” trong demo.

**Tài liệu:** [What is Identity Protection?](https://learn.microsoft.com/en-us/entra/id-protection/overview-identity-protection)

### 15.3 Bảng license — PoC dừng ở đâu, prod cần gì

| Tính năng | PoC (Free) | Production |
|-----------|------------|------------|
| App registration, JWT, app roles | ✅ Đã làm | Giữ nguyên |
| Client credentials S2S | ✅ Đã làm | Giữ + rotate secret/cert |
| MCP tool policy contract | ✅ Đã làm | Giữ + dashboard/audit khi thêm risk guard |
| Conditional Access (MFA, block legacy) | ❌ Out-of-scope | **P1+** |
| PIM / JIT privileged roles | ❌ Out-of-scope | **P2** |
| Identity Protection / risk-based CA | ❌ Out-of-scope | **P2** (+ CA **P1**) |
| Device compliance CA | ❌ Out-of-scope | **P1** CA + Intune/Defender |

---

*Chốt doc: 2026-06-01 — §0 trạng thái; §5 close criteria; §9 đồng bộ code (`/mcp/tools` = AgentOrService, `McpToolAudit` trong AiOrchestrator); §12 ghi chú AI; §13 Employee-only test/gap; §14–§15 prod-only.*
