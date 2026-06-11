# Support PoC Scripts

Day la entrypoint van hanh cho repo `ProjectThem5`. Root `README.md` da duoc xoa theo chu truong giu repo gon; neu ban la AI/coder moi vao project, doc file nay truoc khi tao script hoac chay smoke test.

PoC nay la **Internal Support Knowledge Assistant**: employee tao support ticket, Knowledge Service index tai lieu len Azure AI Search, AI Orchestrator dung Semantic Kernel/Azure OpenAI/MCP de tao suggestion, support agent resolve ticket.

## Doc can doc truoc

| File | Noi dung |
|------|----------|
| [`../docs/technical_learn.md`](../docs/technical_learn.md) | Checklist cong nghe can luyen. Chi doc, khong tu y sua neu owner khong yeu cau. |
| [`../docs/mini_business_poc.md`](../docs/mini_business_poc.md) | Mo ta nghiep vu mini va cach gan Microservices, Event-Driven, RAG, Saga, Idempotency, Inbox, Outbox vao flow. |
| [`../docs/auto-suggestion-proposal.md`](../docs/auto-suggestion-proposal.md) | Proposal pipeline auto-suggestion (thay saga). |
| [`../docs/saga-orchestration-timeout-recovery.md`](../docs/saga-orchestration-timeout-recovery.md) | **Deprecated** — saga timeout (tham khảo lịch sử). |
| [`../docs/zero-trust-identity.md`](../docs/zero-trust-identity.md) | Zero Trust Identity: muc tieu, Entra roles, tien trinh, link MS Learn. |
| [`../docs/user_stories.md`](../docs/user_stories.md) | User stories va acceptance criteria de implement/kiem tra PoC. |
| [`../SupportPoc.slnx`](../SupportPoc.slnx) | Solution .NET backend. |
| [`../frontend`](../frontend) | React UI. |
| [`../infra/terraform`](../infra/terraform) | Terraform scaffold cho Azure target. |

## Quy tac cho AI/coder sau

- Khong tao them script moi neu script hien co da lam duoc viec. Sua script hien co va cap nhat README nay.
- Khong ghi secret vao git. `src/*/appsettings.Development.json` da nam trong `.gitignore` — copy tu `appsettings.Development.json.example` roi dien key local. **Rotate** Azure/Entra key neu tung commit/push file Development that.
- Sau thay doi auto-suggestion pipeline: doc [`../docs/auto-suggestion-proposal.md`](../docs/auto-suggestion-proposal.md), chay `dotnet test`, `bash scripts/restart-services.sh`, `bash scripts/smoke-test.sh`.
- Azure Search Basic va Service Bus Standard ton phi co dinh. Dung `bash scripts/azure-resources-stop.sh` khi khong can chay nua.
- `docs/technical_learn.md` la input checklist cua owner; tranh sua file nay neu task khong noi ro.
- Terraform trong [`../infra/terraform`](../infra/terraform) la huong dai han de start/stop Azure resources. Scripts van duoc giu trong luc Terraform on dinh.

## Script dang dung

| Script | Muc dich | Khi nao dung |
|--------|----------|--------------|
| [`provision-azure.sh`](provision-azure.sh) | Tao moi Azure Resource Group, Storage, Service Bus, AI Search, Azure OpenAI, ghi `config/azure.local.json`, sync appsettings. | Lan dau tao Azure resources hoac can provision lai tu dau. |
| [`provision-entra.sh`](provision-entra.sh) | Tao Entra apps/roles/scopes tuong duong `infra/terraform/identity.tf` (khong `terraform apply`). Xem [`PROVISION-ENTRA.md`](PROVISION-ENTRA.md). | Bat dau Zero Trust Identity phase 1. |
| [`refresh-azure-config.sh`](refresh-azure-config.sh) | Doc resources dang co trong RG va ghi lai config local. | RG da ton tai, can cap nhat key/endpoint vao local. |
| [`sync-config.sh`](sync-config.sh) | Dong bo `config/azure.local.json` sang `src/*/appsettings.Development.json` **va dotnet user-secrets** (tranh stale override). | Da co config local va chi can apply vao services. |
| [`azure-resources-stop.sh`](azure-resources-stop.sh) | Luu state resource roi xoa RG de giam chi phi. | Khi dung PoC xong va muon tat Azure resources ton phi. |
| [`azure-resources-start.sh`](azure-resources-start.sh) | Bat lai resources: refresh neu RG con ton tai, provision neu RG da bi xoa. | Khi muon chay lai PoC sau khi stop. |
| [`restart-services.sh`](restart-services.sh) | Build va restart 4 backend local tren ports 5001-5004. **Can Azure Service Bus Emulator hoac Service Bus that** — khong tu bat HTTP bridge. | Sau khi sync config, emulator dang chay, hoac sua backend. |
| [`smoke-test.sh`](smoke-test.sh) | Chay flow dev end-to-end: health, MCP tools, re-index, tao ticket, AI suggestion, chat, resolve. Khi `AzureAd:Enabled=true`: tu dong dung client credentials; re-index can `SMOKE_BEARER_TOKEN` (user JWT). | De xac nhan PoC dang work. |
| [`integration-e2e.sh`](integration-e2e.sh) | Real integration smoke: Outbox path qua Service Bus/emulator; test HTTP bridge chi khi bat `USE_HTTP_BRIDGE=true` (debug). Dung `--no-launch-profile` de `ASPNETCORE_ENVIRONMENT` khong bi `launchSettings.json` ghi de. | Sau thay doi messaging/auth; bat bug runtime khong unit test. |
| [`integration-e2e-legacy-poison.sh`](integration-e2e-legacy-poison.sh) | DB legacy (`TicketSuggestionStates` only) + `__POISON_AI__` / DLQ. | Truoc commit khi sua orchestrator schema hoac MassTransit retry. |

**Entra login + E2E:** Doc day du [`../docs/zero-trust-identity.md`](../docs/zero-trust-identity.md) §5, §10, §11.

1. `bash scripts/provision-entra.sh` → `bash scripts/sync-config.sh`
2. `bash scripts/restart-services.sh` && `cd frontend && npm run dev`
3. Login tab Entra/Login — redirect URI: `localhost:5173` **va** `127.0.0.1:5173`
4. Handoff phien (gitignored): `config/entra-browser-session.local.json` (mau: `config/entra-browser-session.local.json.example`)

Saga timeout / fault injection / unit test: xem [`../docs/saga-orchestration-timeout-recovery.md`](../docs/saga-orchestration-timeout-recovery.md).

Hien khong co script nao du thua. Cac script co quan he nhu sau:

```text
azure-resources-start.sh
  -> refresh-azure-config.sh neu RG da co Search + Service Bus
  -> provision-azure.sh neu can tao lai resources

provision-azure.sh
  -> sync-config.sh

refresh-azure-config.sh
  -> sync-config.sh

restart-services.sh
  -> dotnet build va start 4 backend

smoke-test.sh
  -> can 4 backend dang chay
```

## Azure Service Bus Emulator (local Outbox path)

Reliable messaging local can emulator hoac Service Bus that — xem [`../docs/mini_business_poc.md`](../docs/mini_business_poc.md) §17.

1. Cai Docker, clone [azure-service-bus-emulator-installer](https://github.com/Azure/azure-service-bus-emulator-installer), `docker compose up`.
2. Copy `appsettings.Development.json.example` → `appsettings.Development.json` (TicketService + AiOrchestrator) — connection string mac dinh da co `UseDevelopmentEmulator=true`.
3. Chi dung `USE_HTTP_BRIDGE=true` khi co y debug shortcut (khong Outbox).

## Flow chay nhanh

Chay tu root repo `D:\ProjectThem5`:

```bash
# Emulator local HOAC Azure Service Bus that
# docker compose up   # trong thu muc emulator installer

# Tao hoac bat lai Azure resources (neu dung cloud thay vi emulator)
bash scripts/azure-resources-start.sh

# Restart 4 backend local
bash scripts/restart-services.sh

# Smoke test
bash scripts/smoke-test.sh

# Integration day du (Service Bus + bridge + fault markers, ~5–8 phut)
bash scripts/integration-e2e.sh
```

**Luu y:** `dotnet run` mac dinh doc `Properties/launchSettings.json` va co the ep `ASPNETCORE_ENVIRONMENT=Development`. Scripts `restart-services.sh` / `integration-e2e.sh` dung `--no-launch-profile` de test Production/not-ready chinh xac.

Ket qua smoke test hop le:

- MCP tools count >= 5 va Employee allowed-tools chi gom read-safe tools.
- Re-index `Completed`.
- Ticket moi di tu `Analyzing` sang `Suggested`.
- AI chat Employee khong duoc doc ticket cheo qua `get_ticket`; Agent/privileged flow moi doc ticket chung.
- Resolve ket thuc voi `status=Resolved OK`.

## Flow Terraform moi

Dung flow nay khi muon quan ly Azure resources bang Terraform thay vi scripts:

```bash
cd infra/terraform
terraform init
terraform apply

cd ../..
bash scripts/sync-config.sh
bash scripts/restart-services.sh
bash scripts/smoke-test.sh
```

Tat resources Terraform:

```bash
cd infra/terraform
terraform destroy
```

Chi tiet: [`../infra/terraform/README.md`](../infra/terraform/README.md).

## Flow chi cap nhat config

```bash
# RG da co resources, chi cap nhat key/endpoint vao local
bash scripts/refresh-azure-config.sh

# Hoac neu config/azure.local.json da dung, chi sync appsettings
bash scripts/sync-config.sh
```

## Tat Azure resources

Azure Search Basic va Service Bus Standard khong co pause mode. De tranh cost co dinh, dung:

```bash
bash scripts/azure-resources-stop.sh
```

Script se luu snapshot vao `config/azure.resources.state.json`, roi xoa RG neu khong con resource.

## Project context

- Frontend: [`../frontend`](../frontend)
- Backend services:
  - [`../src/TicketService`](../src/TicketService) on `http://localhost:5001`
  - [`../src/KnowledgeService`](../src/KnowledgeService) on `http://localhost:5002`
  - [`../src/AiOrchestrator`](../src/AiOrchestrator) on `http://localhost:5003`
  - [`../src/McpToolServer`](../src/McpToolServer) on `http://localhost:5004`
- Business docs:
  - [`../docs/mini_business_poc.md`](../docs/mini_business_poc.md)
  - [`../docs/user_stories.md`](../docs/user_stories.md)
- Technical checklist:
  - [`../docs/technical_learn.md`](../docs/technical_learn.md)
