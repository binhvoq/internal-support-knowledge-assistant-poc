# AI-Powered Internal Support Platform

An end-to-end internal support platform where employees can create tickets, search company knowledge, and receive AI-assisted answers grounded in internal documents.

## Highlights

- **AI support:** Azure OpenAI-powered chat and ticket suggestions.
- **RAG knowledge base:** PDF ingestion to Azure Blob Storage with hybrid, vector, and semantic search in Azure AI Search.
- **Reliable workflows:** Event-driven ticket suggestions with Azure Service Bus, MassTransit Saga, and outbox/inbox patterns.
- **Secure access:** Microsoft Entra ID authentication and role-based authorization.
- **Cloud delivery:** Dockerized services, Terraform-managed Azure infrastructure, and GitHub Actions pipelines for Dev, Test, and Production.

## Architecture

```text
React + TypeScript
        |
      Gateway
        |
  +-----+-----------------------------+
  | Ticket Service | Knowledge Service |
  +-----+-----------------------------+
        |                |
 Azure Service Bus   Blob Storage + Azure AI Search
        |
 AI Orchestrator -> Azure OpenAI / Semantic Kernel / MCP tools
```

## Technology

`.NET 10` · `ASP.NET Core` · `C#` · `React` · `TypeScript` · `SQL Server` · `Azure OpenAI` · `Azure AI Search` · `Azure Blob Storage` · `Azure Service Bus` · `MassTransit` · `Microsoft Entra ID` · `Docker` · `Terraform` · `GitHub Actions`

## Run locally

Prerequisites: Docker Desktop with Docker Compose.

```bash
docker compose up --build
```

Open [http://localhost:3000](http://localhost:3000). The default Docker setup uses SQL Server and the Azure Service Bus Emulator; Azure AI services are configuration-dependent.

## Repository layout

- `frontend/` — React user interface
- `src/TicketService/` — ticket lifecycle and support queue APIs
- `src/KnowledgeService/` — document ingestion, indexing, and search
- `src/AiOrchestrator/` — AI chat, RAG orchestration, and ticket suggestions
- `src/McpToolServer/` — controlled internal tools for the AI assistant
- `src/Gateway/` — reverse proxy and API gateway
- `infra/terraform/` — Azure infrastructure as code
- `.github/workflows/` — CI/CD workflows

## Quality and reliability

The solution includes unit, integration, and end-to-end tests for ticket lifecycle management, idempotency, asynchronous message handling, knowledge search, and AI orchestration.
