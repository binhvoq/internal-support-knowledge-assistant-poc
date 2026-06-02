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
        "ChatEnabled": cfg["azureOpenAI"].get("chatEnabled", True),
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

ai_insights = cfg.get("applicationInsights") or {}
if ai_insights.get("connectionString"):
    base["ApplicationInsights"] = {"ConnectionString": ai_insights["connectionString"]}

def write(rel_path: str, data: dict):
    path = root / rel_path
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    print(f"  -> {rel_path}")

entra = cfg.get("entra") or {}
azure_ad = {}
if entra:
    api = entra.get("api") or {}
    mcp_entra = entra.get("mcpService") or {}
    azure_ad = {
        "Enabled": True,
        "Instance": "https://login.microsoftonline.com/",
        "TenantId": entra.get("tenantId", ""),
        "Authority": entra.get("authority", ""),
        "Audience": api.get("audience", ""),
        "ClientId": api.get("clientId", ""),
        "SpaClientId": (entra.get("spa") or {}).get("clientId", ""),
        "Scope": api.get("scopeFull", ""),
        "McpClientId": mcp_entra.get("clientId", ""),
        "McpClientSecret": mcp_entra.get("clientSecret", ""),
    }

ticket = {**base, "ConnectionStrings": {"Tickets": "Data Source=tickets.db"}}
if azure_ad:
    ticket["AzureAd"] = azure_ad
knowledge = {**base, "ConnectionStrings": {"Knowledge": "Data Source=knowledge.db"}}
if azure_ad:
    knowledge["AzureAd"] = azure_ad
orchestrator = {k: v for k, v in base.items() if k != "AzureStorage"}
orchestrator.pop("ConnectionStrings", None)
if azure_ad:
    orchestrator["AzureAd"] = azure_ad
mcp = {
    "Logging": base["Logging"],
    "Services": base["Services"],
}
if azure_ad:
    mcp_secret = azure_ad["McpClientSecret"]
    mcp["AzureAd"] = {
        "Enabled": True,
        "TenantId": azure_ad["TenantId"],
        "Authority": azure_ad["Authority"],
        "Audience": azure_ad["Audience"],
        "ClientId": azure_ad["ClientId"],
        "McpClientId": azure_ad["McpClientId"],
        "McpClientSecret": mcp_secret,
        "ClientSecret": mcp_secret,
    }

write("src/TicketService/appsettings.Development.json", ticket)
write("src/KnowledgeService/appsettings.Development.json", knowledge)
write("src/AiOrchestrator/appsettings.Development.json", orchestrator)
write("src/McpToolServer/appsettings.Development.json", mcp)

if entra:
    spa = entra.get("spa") or {}
    api = entra.get("api") or {}
    env_lines = [
        f"VITE_AAD_TENANT_ID={entra.get('tenantId', '')}",
        f"VITE_AAD_CLIENT_ID={spa.get('clientId', '')}",
        f"VITE_AAD_API_SCOPE={api.get('scopeFull', '')}",
        f"VITE_AAD_AUTHORITY={entra.get('authority', '')}",
        "",
    ]
    env_path = root / "frontend" / ".env.local"
    env_path.write_text("\n".join(env_lines), encoding="utf-8")
    print(f"  -> frontend/.env.local")

# User Secrets override appsettings.Development.json — sync tu cung azure.local.json.
import subprocess

def sync_secrets(rel_project: str, pairs: dict[str, str]) -> None:
    project = root / rel_project
    if not project.exists():
        return
    ok = 0
    for key, value in pairs.items():
        r = subprocess.run(
            ["dotnet", "user-secrets", "set", key, value],
            cwd=project,
            check=False,
            capture_output=True,
            text=True,
        )
        if r.returncode == 0:
            ok += 1
    print(f"  -> user-secrets {rel_project} ({ok}/{len(pairs)} keys)")

bus = cfg["serviceBus"]["connectionString"]
search = cfg["azureSearch"]
storage = cfg.get("storage", {}).get("connectionString", "")
oai = cfg["azureOpenAI"]
chat_ep = oai.get("chatEndpoint", oai["endpoint"])
chat_key = oai.get("chatApiKey", oai["apiKey"])
chat_enabled = str(oai.get("chatEnabled", True)).lower()

sync_secrets("src/TicketService", {
    "ServiceBus:Enabled": "true",
    "ServiceBus:ConnectionString": bus,
})
sync_secrets("src/KnowledgeService", {
    "ServiceBus:Enabled": "true",
    "ServiceBus:ConnectionString": bus,
    "AzureSearch:Endpoint": search["endpoint"],
    "AzureSearch:ApiKey": search["apiKey"],
    "AzureStorage:ConnectionString": storage,
    "AzureOpenAI:Enabled": "true",
    "AzureOpenAI:Endpoint": oai["endpoint"],
    "AzureOpenAI:ApiKey": oai["apiKey"],
})
sync_secrets("src/AiOrchestrator", {
    "ServiceBus:Enabled": "true",
    "ServiceBus:ConnectionString": bus,
    "AzureOpenAI:ChatEnabled": chat_enabled,
    "AzureOpenAI:Endpoint": oai["endpoint"],
    "AzureOpenAI:ApiKey": oai["apiKey"],
    "AzureOpenAI:ChatEndpoint": chat_ep,
    "AzureOpenAI:ChatApiKey": chat_key,
})
ai_insights = cfg.get("applicationInsights") or {}
if ai_insights.get("connectionString"):
    sync_secrets("src/AiOrchestrator", {
        "ApplicationInsights:ConnectionString": ai_insights["connectionString"],
    })
if entra:
    ad = cfg["entra"]
    api = ad.get("api") or {}
    mcp_e = ad.get("mcpService") or {}
    entra_secrets = {
        "AzureAd:Enabled": "true",
        "AzureAd:TenantId": ad.get("tenantId", ""),
        "AzureAd:Authority": ad.get("authority", ""),
        "AzureAd:Audience": api.get("audience", ""),
        "AzureAd:ClientId": api.get("clientId", ""),
        "AzureAd:Scope": api.get("scopeFull", ""),
    }
    for proj in ("src/TicketService", "src/KnowledgeService", "src/AiOrchestrator"):
        sync_secrets(proj, {
            **entra_secrets,
            "AzureAd:McpClientId": mcp_e.get("clientId", ""),
            "AzureAd:McpClientSecret": mcp_e.get("clientSecret", ""),
        })
    sync_secrets("src/McpToolServer", {
        **entra_secrets,
        "AzureAd:McpClientId": mcp_e.get("clientId", ""),
        "AzureAd:McpClientSecret": mcp_e.get("clientSecret", ""),
        "AzureAd:ClientSecret": mcp_e.get("clientSecret", ""),
    })
PY

echo "Da dong bo appsettings.Development.json + user-secrets cho 3 backend co UserSecretsId."
