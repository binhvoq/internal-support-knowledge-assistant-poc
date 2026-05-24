#!/usr/bin/env bash
# Dong bo config/azure.local.json -> appsettings.Development.json (4 services).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CFG="$ROOT/config/azure.local.json"

if [[ ! -f "$CFG" ]]; then
  echo "Thieu $CFG — chay scripts/provision-azure.sh truoc."
  exit 1
fi

PYTHON=python3
command -v python3 >/dev/null 2>&1 || PYTHON=python
"$PYTHON" - "$CFG" "$ROOT" <<'PY'
import json, sys
from pathlib import Path

cfg_path, root = sys.argv[1], Path(sys.argv[2])
cfg = json.loads(Path(cfg_path).read_text(encoding="utf-8"))

base = {
    "Logging": {"LogLevel": {"Default": "Information", "Microsoft.AspNetCore": "Warning"}},
    "AllowedHosts": "*",
    "ServiceBus": {
        "ConnectionString": cfg["serviceBus"]["connectionString"],
        "TopicName": cfg["serviceBus"].get("topicName", "support-events"),
    },
    "AzureSearch": {
        "Endpoint": cfg["azureSearch"]["endpoint"],
        "ApiKey": cfg["azureSearch"]["apiKey"],
        "IndexName": cfg["azureSearch"].get("indexName", "knowledge-documents"),
    },
    "AzureOpenAI": {
        "Endpoint": cfg["azureOpenAI"]["endpoint"],
        "ApiKey": cfg["azureOpenAI"]["apiKey"],
        "ChatEndpoint": cfg["azureOpenAI"].get("chatEndpoint", cfg["azureOpenAI"]["endpoint"]),
        "ChatApiKey": cfg["azureOpenAI"].get("chatApiKey", cfg["azureOpenAI"]["apiKey"]),
        "ChatDeployment": cfg["azureOpenAI"].get("chatDeployment", "gpt-4.1-mini"),
        "EmbeddingDeployment": cfg["azureOpenAI"].get("embeddingDeployment", "text-embedding-3-small"),
    },
    "AzureStorage": {
        "ConnectionString": cfg.get("storage", {}).get("connectionString", ""),
        "ContainerName": "knowledge-docs",
    },
    "Services": {
        "AiOrchestrator": "http://localhost:5003",
        "TicketService": "http://localhost:5001",
        "KnowledgeService": "http://localhost:5002",
        "McpToolServer": "http://localhost:5004",
    },
}

def write(rel_path: str, data: dict):
    path = root / rel_path
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    print(f"  -> {rel_path}")

ticket = {**base, "ConnectionStrings": {"Tickets": "Data Source=tickets.db"}}
knowledge = {**base, "ConnectionStrings": {"Knowledge": "Data Source=knowledge.db"}}
orchestrator = {k: v for k, v in base.items() if k != "AzureStorage"}
orchestrator.pop("ConnectionStrings", None)
mcp = {
    "Logging": base["Logging"],
    "Services": base["Services"],
}

write("src/TicketService/appsettings.Development.json", ticket)
write("src/KnowledgeService/appsettings.Development.json", knowledge)
write("src/AiOrchestrator/appsettings.Development.json", orchestrator)
write("src/McpToolServer/appsettings.Development.json", mcp)
PY

echo "Da dong bo appsettings.Development.json cho 4 services."
