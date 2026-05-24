# Internal Support Knowledge Assistant (PoC)

Mini PoC theo `docs/mini_business_poc.md` và `docs/user_stories.md`.

## Cau truc

```text
frontend/                 React (Vite)
src/TicketService/        :5001
src/KnowledgeService/     :5002
src/AiOrchestrator/       :5003 — Semantic Kernel, RAG (qua MCP), Service Bus worker
src/McpToolServer/        :5004/mcp
src/Shared/
infra/terraform/
config/azure.local.json   (gitignored — tao bang az cli)
```

## Chay local

```bash
dotnet build
cd frontend && npm install && npm run dev
```

Backend (4 terminal):

```bash
dotnet run --project src/TicketService --urls http://localhost:5001
dotnet run --project src/KnowledgeService --urls http://localhost:5002
dotnet run --project src/McpToolServer --urls http://localhost:5004
dotnet run --project src/AiOrchestrator --urls http://localhost:5003
```

Sau khi co Azure config trong `src/*/appsettings.Development.json`:

```bash
curl -X POST http://localhost:5002/documents/reindex
```

## Azure (1 resource group)

```bash
# Tao RG + Storage + Service Bus + AI Search + OpenAI (embed: eastus, chat: gpt-4.1-mini)
# -> ghi config/azure.local.json + dong bo appsettings.Development.json
bash scripts/provision-azure.sh

# Neu RG da ton tai tu lan truoc, co the chi cap nhat config tu tai nguyen hien co:
# (sua ten resource trong script hoac dat bien moi truong STORAGE_NAME, SEARCH_NAME, ...)

# Chi dong bo lai config (neu da co azure.local.json)
bash scripts/sync-config.sh
```

Xoa toan bo: `az group delete -n rg-support-poc --yes --no-wait`

Khoi dong lai 4 backend: `bash scripts/restart-services.sh`

Smoke test (4 services dang chay): `bash scripts/smoke-test.sh`

## Terraform

```bash
cd infra/terraform && terraform init && terraform validate
```

Bao gom Service Bus topic **va** subscription `ai-orchestrator`.

## Luong chinh

1. Re-index knowledge → Azure AI Search (vector + hybrid).
2. Employee tao ticket → Service Bus `TicketCreated` → AI Orchestrator.
3. Orchestrator goi **MCP** `search_knowledge` → prompt RAG co **noi dung** tai lieu.
4. Support Agent xem suggestion + related docs; Resolve.
5. AI Chat dung MCP qua Semantic Kernel (`get_ticket_status`, `search_policy_documents`, `update_ticket_status`).
6. Knowledge Admin them tai lieu → luu SQLite + **Azure Blob** (neu co Storage).

## Do phu (vs docs)

| Yeu cau | Trang thai |
|---------|------------|
| React: Employee / Queue / Detail / Knowledge / Chat | Co |
| Ticket Service CRUD + events | Co |
| Knowledge + re-index + vector/hybrid search | Co |
| AI Orchestrator + Semantic Kernel + RAG | Co |
| Function Calling (chat) | Co |
| MCP: get_ticket, search_knowledge, update_ticket_status, list_support_categories | Co |
| Event-Driven (Service Bus) | Co |
| Terraform validate | Co |
| Azure Blob cho raw documents | Co |
| Unit test / CI/CD | Bo qua theo yeu cau |
| Auto phan loai category (Other / de trong) | Co (AI Orchestrator) |
| create_ticket (Function Calling) | Co (MCP + Semantic Kernel) |
| Application Insights (Terraform scaffold) | Co |
