# Support PoC Scripts

Day la entrypoint van hanh cho repo `ProjectThem5`. Root `README.md` da duoc xoa theo chu truong giu repo gon; neu ban la AI/coder moi vao project, doc file nay truoc khi tao script hoac chay smoke test.

PoC nay la **Internal Support Knowledge Assistant**: employee tao support ticket, Knowledge Service index tai lieu len Azure AI Search, AI Orchestrator dung Semantic Kernel/Azure OpenAI/MCP de tao suggestion, support agent resolve ticket.

## Doc can doc truoc

| File | Noi dung |
|------|----------|
| [`../docs/technical_learn.md`](../docs/technical_learn.md) | Checklist cong nghe can luyen. Chi doc, khong tu y sua neu owner khong yeu cau. |
| [`../docs/mini_business_poc.md`](../docs/mini_business_poc.md) | Mo ta nghiep vu mini va cach gan Microservices, Event-Driven, RAG, Saga, Idempotency, Inbox, Outbox vao flow. |
| [`../docs/saga-orchestration-timeout-recovery.md`](../docs/saga-orchestration-timeout-recovery.md) | Saga timeout recovery, fault injection, unit test va debug. |
| [`../docs/user_stories.md`](../docs/user_stories.md) | User stories va acceptance criteria de implement/kiem tra PoC. |
| [`../SupportPoc.slnx`](../SupportPoc.slnx) | Solution .NET backend. |
| [`../frontend`](../frontend) | React UI. |
| [`../infra/terraform`](../infra/terraform) | Terraform scaffold cho Azure target. |

## Quy tac cho AI/coder sau

- Khong tao them script moi neu script hien co da lam duoc viec. Sua script hien co va cap nhat README nay.
- Khong ghi secret vao git. `config/azure.local.json` va `src/*/appsettings.Development.json` la local generated config.
- Sau thay doi saga/timeout/compensation: doc [`../docs/saga-orchestration-timeout-recovery.md`](../docs/saga-orchestration-timeout-recovery.md), chay `dotnet test`, `bash scripts/restart-services.sh`, `bash scripts/smoke-test.sh`.
- Azure Search Basic va Service Bus Standard ton phi co dinh. Dung `bash scripts/azure-resources-stop.sh` khi khong can chay nua.
- `docs/technical_learn.md` la input checklist cua owner; tranh sua file nay neu task khong noi ro.
- Terraform trong [`../infra/terraform`](../infra/terraform) la huong dai han de start/stop Azure resources. Scripts van duoc giu trong luc Terraform on dinh.

## Script dang dung

| Script | Muc dich | Khi nao dung |
|--------|----------|--------------|
| [`provision-azure.sh`](provision-azure.sh) | Tao moi Azure Resource Group, Storage, Service Bus, AI Search, Azure OpenAI, ghi `config/azure.local.json`, sync appsettings. | Lan dau tao Azure resources hoac can provision lai tu dau. |
| [`refresh-azure-config.sh`](refresh-azure-config.sh) | Doc resources dang co trong RG va ghi lai config local. | RG da ton tai, can cap nhat key/endpoint vao local. |
| [`sync-config.sh`](sync-config.sh) | Dong bo `config/azure.local.json` sang `src/*/appsettings.Development.json` **va dotnet user-secrets** (tranh stale override). | Da co config local va chi can apply vao services. |
| [`azure-resources-stop.sh`](azure-resources-stop.sh) | Luu state resource roi xoa RG de giam chi phi. | Khi dung PoC xong va muon tat Azure resources ton phi. |
| [`azure-resources-start.sh`](azure-resources-start.sh) | Bat lai resources: refresh neu RG con ton tai, provision neu RG da bi xoa. | Khi muon chay lai PoC sau khi stop. |
| [`restart-services.sh`](restart-services.sh) | Build va restart 4 backend local tren ports 5001-5004. | Sau khi sync config hoac sua backend. |
| [`smoke-test.sh`](smoke-test.sh) | Chay flow dev end-to-end: health, MCP tools, re-index, tao ticket, AI suggestion, chat, resolve. | De xac nhan PoC dang work. |

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

## Flow chay nhanh

Chay tu root repo `D:\ProjectThem5`:

```bash
# Tao hoac bat lai Azure resources
bash scripts/azure-resources-start.sh

# Restart 4 backend local
bash scripts/restart-services.sh

# Smoke test
bash scripts/smoke-test.sh
```

Ket qua smoke test hop le:

- MCP tools count >= 5.
- Re-index `Completed`.
- Ticket moi di tu `Analyzing` sang `Suggested`.
- AI chat tra ve status ticket.
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
