#!/usr/bin/env bash
# Cap nhat config/azure.local.json tu tai nguyen da co trong 1 resource group.
set -euo pipefail

RG="${RG:-rg-support-poc}"
TOPIC="${TOPIC:-support-events}"

echo "==> Doc tai nguyen trong RG: $RG"
STORAGE_NAME=$(az storage account list -g "$RG" --query "[0].name" -o tsv | tr -d '\r')
BUS_NAMESPACE=$(az servicebus namespace list -g "$RG" --query "[0].name" -o tsv | tr -d '\r')
SEARCH_NAME=$(az search service list -g "$RG" --query "[0].name" -o tsv | tr -d '\r')
OPENAI_EMBED=$(az cognitiveservices account list -g "$RG" --query "[?kind=='OpenAI'] | [0].name" -o tsv | tr -d '\r')
OPENAI_CHAT=$(az cognitiveservices account list -g "$RG" --query "[?kind=='OpenAI'] | [-1].name" -o tsv | tr -d '\r')
CHAT_DEPLOYMENT="${CHAT_DEPLOYMENT:-gpt-4o-mini}"

[[ -z "$STORAGE_NAME" || -z "$BUS_NAMESPACE" || -z "$SEARCH_NAME" ]] && {
  echo "Thieu Storage/Service Bus/Search trong RG. Chay scripts/provision-azure.sh truoc."
  exit 1
}

STORAGE_CONN=$(az storage account show-connection-string -g "$RG" -n "$STORAGE_NAME" --query connectionString -o tsv)
BUS_CONN=$(az servicebus namespace authorization-rule keys list \
  -g "$RG" --namespace-name "$BUS_NAMESPACE" -n RootManageSharedAccessKey \
  --query primaryConnectionString -o tsv)
SEARCH_ENDPOINT="https://${SEARCH_NAME}.search.windows.net"
SEARCH_KEY=$(az search admin-key show -g "$RG" --service-name "$SEARCH_NAME" --query primaryKey -o tsv)

OPENAI_ENDPOINT=$(az cognitiveservices account show -g "$RG" -n "$OPENAI_EMBED" --query properties.endpoint -o tsv)
OPENAI_KEY=$(az cognitiveservices account keys list -g "$RG" -n "$OPENAI_EMBED" --query key1 -o tsv)
CHAT_ENDPOINT=$(az cognitiveservices account show -g "$RG" -n "$OPENAI_CHAT" --query properties.endpoint -o tsv)
CHAT_KEY=$(az cognitiveservices account keys list -g "$RG" -n "$OPENAI_CHAT" --query key1 -o tsv)

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PYTHON=python3
command -v python3 >/dev/null 2>&1 || PYTHON=python
RG="$RG" BUS_CONN="$BUS_CONN" TOPIC="$TOPIC" \
SEARCH_ENDPOINT="$SEARCH_ENDPOINT" SEARCH_KEY="$SEARCH_KEY" \
OPENAI_ENDPOINT="$OPENAI_ENDPOINT" OPENAI_KEY="$OPENAI_KEY" \
CHAT_DEPLOYMENT="$CHAT_DEPLOYMENT" CHAT_ENDPOINT="$CHAT_ENDPOINT" CHAT_KEY="$CHAT_KEY" \
STORAGE_CONN="$STORAGE_CONN" CONFIG_FILE="$ROOT/config/azure.local.json" "$PYTHON" <<'PY'
import json
import os
from pathlib import Path

data = {
    "resourceGroup": os.environ["RG"].strip(),
    "serviceBus": {
        "connectionString": os.environ["BUS_CONN"].strip(),
        "topicName": os.environ["TOPIC"].strip(),
    },
    "azureSearch": {
        "endpoint": os.environ["SEARCH_ENDPOINT"].strip(),
        "apiKey": os.environ["SEARCH_KEY"].strip(),
        "indexName": "knowledge-documents",
    },
    "azureOpenAI": {
        "endpoint": os.environ["OPENAI_ENDPOINT"].strip(),
        "apiKey": os.environ["OPENAI_KEY"].strip(),
        "chatDeployment": os.environ["CHAT_DEPLOYMENT"].strip(),
        "embeddingDeployment": "text-embedding-3-small",
        "chatEndpoint": os.environ["CHAT_ENDPOINT"].strip(),
        "chatApiKey": os.environ["CHAT_KEY"].strip(),
    },
    "storage": {
        "connectionString": os.environ["STORAGE_CONN"].strip(),
    },
}

Path(os.environ["CONFIG_FILE"]).write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
PY

"$ROOT/scripts/sync-config.sh"
echo "Da cap nhat config tu RG $RG."
