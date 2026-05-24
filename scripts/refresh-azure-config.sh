#!/usr/bin/env bash
# Cap nhat config/azure.local.json tu tai nguyen da co trong 1 resource group.
set -euo pipefail

RG="${RG:-rg-support-poc}"
TOPIC="${TOPIC:-support-events}"

echo "==> Doc tai nguyen trong RG: $RG"
STORAGE_NAME=$(az storage account list -g "$RG" --query "[0].name" -o tsv)
BUS_NAMESPACE=$(az servicebus namespace list -g "$RG" --query "[0].name" -o tsv)
SEARCH_NAME=$(az search service list -g "$RG" --query "[0].name" -o tsv)
OPENAI_EMBED=$(az cognitiveservices account list -g "$RG" --query "[?kind=='OpenAI'] | [0].name" -o tsv)
OPENAI_CHAT=$(az cognitiveservices account list -g "$RG" --query "[?kind=='OpenAI'] | [-1].name" -o tsv)
CHAT_DEPLOYMENT="${CHAT_DEPLOYMENT:-gpt-4.1-mini}"

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
cat > "$ROOT/config/azure.local.json" <<EOF
{
  "resourceGroup": "$RG",
  "serviceBus": {
    "connectionString": "$BUS_CONN",
    "topicName": "$TOPIC"
  },
  "azureSearch": {
    "endpoint": "$SEARCH_ENDPOINT",
    "apiKey": "$SEARCH_KEY",
    "indexName": "knowledge-documents"
  },
  "azureOpenAI": {
    "endpoint": "$OPENAI_ENDPOINT",
    "apiKey": "$OPENAI_KEY",
    "chatDeployment": "$CHAT_DEPLOYMENT",
    "embeddingDeployment": "text-embedding-3-small",
    "chatEndpoint": "$CHAT_ENDPOINT",
    "chatApiKey": "$CHAT_KEY"
  },
  "storage": {
    "connectionString": "$STORAGE_CONN"
  }
}
EOF

"$ROOT/scripts/sync-config.sh"
echo "Da cap nhat config tu RG $RG."
