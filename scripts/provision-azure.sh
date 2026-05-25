#!/usr/bin/env bash
# Tao tat ca tai nguyen PoC trong 1 resource group (khong Docker).
set -euo pipefail

RG="${RG:-rg-support-poc}"
LOC="${LOC:-southeastasia}"
EMBED_LOC="${EMBED_LOC:-eastus}"
CHAT_LOC="${CHAT_LOC:-eastus}"
PREFIX="${PREFIX:-supportpoc}"
SUFFIX="${SUFFIX:-$(printf '%04x' $((RANDOM % 65536)))}"
STORAGE_NAME="${STORAGE_NAME:-${PREFIX}store${SUFFIX}}"
BUS_NAMESPACE="${BUS_NAMESPACE:-${PREFIX}bus${SUFFIX}}"
SEARCH_NAME="${SEARCH_NAME:-${PREFIX}search${SUFFIX}}"
OPENAI_EMBED="${OPENAI_EMBED:-${PREFIX}-oai-embed-${SUFFIX}}"
OPENAI_CHAT="${OPENAI_CHAT:-${PREFIX}-oai-chat-${SUFFIX}}"
TOPIC="support-events"
SUB="ai-orchestrator"
CHAT_DEPLOYMENT="${CHAT_DEPLOYMENT:-gpt-4.1-mini}"
CHAT_MODEL_VERSION="${CHAT_MODEL_VERSION:-2025-04-14}"

echo "==> Resource group: $RG ($LOC)"
az group create -n "$RG" -l "$LOC" -o none

echo "==> Storage: $STORAGE_NAME"
az storage account create -g "$RG" -n "$STORAGE_NAME" -l "$LOC" --sku Standard_LRS -o none 2>/dev/null || true
STORAGE_CONN=$(az storage account show-connection-string -g "$RG" -n "$STORAGE_NAME" --query connectionString -o tsv)
az storage container create --name knowledge-docs --connection-string "$STORAGE_CONN" -o none 2>/dev/null || true

echo "==> Service Bus: $BUS_NAMESPACE"
az servicebus namespace create -g "$RG" -n "$BUS_NAMESPACE" -l "$LOC" --sku Standard -o none 2>/dev/null || true
az servicebus topic create -g "$RG" --namespace-name "$BUS_NAMESPACE" -n "$TOPIC" -o none 2>/dev/null || true
az servicebus topic subscription create -g "$RG" --namespace-name "$BUS_NAMESPACE" --topic-name "$TOPIC" -n "$SUB" -o none 2>/dev/null || true
BUS_CONN=$(az servicebus namespace authorization-rule keys list \
  -g "$RG" --namespace-name "$BUS_NAMESPACE" -n RootManageSharedAccessKey \
  --query primaryConnectionString -o tsv)

echo "==> AI Search: $SEARCH_NAME"
az search service create -g "$RG" -n "$SEARCH_NAME" -l "$LOC" --sku basic -o none 2>/dev/null || true
SEARCH_ENDPOINT="https://${SEARCH_NAME}.search.windows.net"
SEARCH_KEY=$(az search admin-key show -g "$RG" --service-name "$SEARCH_NAME" --query primaryKey -o tsv)

echo "==> OpenAI embedding ($EMBED_LOC): $OPENAI_EMBED"
az cognitiveservices account create -g "$RG" -n "$OPENAI_EMBED" -l "$EMBED_LOC" --kind OpenAI --sku S0 --yes -o none
OPENAI_ENDPOINT=$(az cognitiveservices account show -g "$RG" -n "$OPENAI_EMBED" --query properties.endpoint -o tsv)
OPENAI_KEY=$(az cognitiveservices account keys list -g "$RG" -n "$OPENAI_EMBED" --query key1 -o tsv)
az cognitiveservices account deployment create -g "$RG" -n "$OPENAI_EMBED" \
  --deployment-name text-embedding-3-small --model-name text-embedding-3-small \
  --model-version "1" --model-format OpenAI --sku-capacity 10 --sku-name Standard -o none 2>/dev/null || true

echo "==> OpenAI chat ($CHAT_LOC): $OPENAI_CHAT — deployment $CHAT_DEPLOYMENT"
az cognitiveservices account create -g "$RG" -n "$OPENAI_CHAT" -l "$CHAT_LOC" --kind OpenAI --sku S0 --yes -o none
CHAT_ENDPOINT=$(az cognitiveservices account show -g "$RG" -n "$OPENAI_CHAT" --query properties.endpoint -o tsv)
CHAT_KEY=$(az cognitiveservices account keys list -g "$RG" -n "$OPENAI_CHAT" --query key1 -o tsv)
az cognitiveservices account deployment create -g "$RG" -n "$OPENAI_CHAT" \
  --deployment-name "$CHAT_DEPLOYMENT" --model-name "$CHAT_DEPLOYMENT" \
  --model-version "$CHAT_MODEL_VERSION" --model-format OpenAI --sku-capacity 10 --sku-name Standard -o none 2>/dev/null || \
az cognitiveservices account deployment create -g "$RG" -n "$OPENAI_CHAT" \
  --deployment-name "$CHAT_DEPLOYMENT" --model-name gpt-4.1-mini \
  --model-version "2025-04-14" --model-format OpenAI --sku-capacity 10 --sku-name Standard -o none

mkdir -p "$(dirname "$0")/../config"
CONFIG_FILE="$(cd "$(dirname "$0")/.." && pwd)/config/azure.local.json"
PYTHON=python3
command -v python3 >/dev/null 2>&1 || PYTHON=python
RG="$RG" LOC="$LOC" BUS_CONN="$BUS_CONN" TOPIC="$TOPIC" \
SEARCH_ENDPOINT="$SEARCH_ENDPOINT" SEARCH_KEY="$SEARCH_KEY" \
OPENAI_ENDPOINT="$OPENAI_ENDPOINT" OPENAI_KEY="$OPENAI_KEY" \
CHAT_DEPLOYMENT="$CHAT_DEPLOYMENT" CHAT_ENDPOINT="$CHAT_ENDPOINT" CHAT_KEY="$CHAT_KEY" \
STORAGE_CONN="$STORAGE_CONN" CONFIG_FILE="$CONFIG_FILE" "$PYTHON" <<'PY'
import json
import os
from pathlib import Path

data = {
    "resourceGroup": os.environ["RG"].strip(),
    "location": os.environ["LOC"].strip(),
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

echo "==> Ghi config/azure.local.json"
"$(dirname "$0")/sync-config.sh"

echo ""
echo "Hoan tat. Xoa RG: az group delete -n $RG --yes --no-wait"
