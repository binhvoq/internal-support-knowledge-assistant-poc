#!/usr/bin/env bash
# Bat lai tai nguyen Azure PoC (provision moi hoac tu bien moi truong).
set -euo pipefail

RG="${RG:-rg-support-poc}"
LOC="${LOC:-southeastasia}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STATE_FILE="${STATE_FILE:-$ROOT/config/azure.resources.state.json}"
CONFIG_FILE="${CONFIG_FILE:-$ROOT/config/azure.local.json}"

load_saved_names() {
  local python_bin=python3
  command -v python3 >/dev/null 2>&1 || python_bin=python
  "$python_bin" "$STATE_FILE" "$CONFIG_FILE" <<'PY'
import json
import os
import re
import shlex
import sys
from pathlib import Path

state_path = Path(sys.argv[1])
config_path = Path(sys.argv[2])
state = json.loads(state_path.read_text(encoding="utf-8")) if state_path.exists() else []
cfg = json.loads(config_path.read_text(encoding="utf-8")) if config_path.exists() else {}

def from_state(resource_type):
    for item in state:
        if item.get("type") == resource_type and item.get("name"):
            return item["name"]
    return None

def host_name(url):
    if not url:
        return None
    match = re.match(r"https://([^./]+)", url)
    return match.group(1) if match else None

values = {
    "STORAGE_NAME": from_state("Microsoft.Storage/storageAccounts"),
    "BUS_NAMESPACE": from_state("Microsoft.ServiceBus/namespaces"),
    "SEARCH_NAME": from_state("Microsoft.Search/searchServices") or host_name(cfg.get("azureSearch", {}).get("endpoint")),
    "OPENAI_EMBED": host_name(cfg.get("azureOpenAI", {}).get("endpoint")),
    "OPENAI_CHAT": host_name(cfg.get("azureOpenAI", {}).get("chatEndpoint")),
}

for key, value in values.items():
    if value and not os.environ.get(key):
        print(f"export {key}={shlex.quote(value)}")
PY
}

if [[ -f "$STATE_FILE" || -f "$CONFIG_FILE" ]]; then
  eval "$(load_saved_names)"
fi

if az group show -n "$RG" -o none 2>/dev/null; then
  HAS_SEARCH=$(az search service list -g "$RG" --query "length(@)" -o tsv 2>/dev/null || echo 0)
  HAS_BUS=$(az servicebus namespace list -g "$RG" --query "length(@)" -o tsv 2>/dev/null || echo 0)
  if [[ "$HAS_SEARCH" -gt 0 && "$HAS_BUS" -gt 0 ]]; then
    echo "RG '$RG' da co Search + Service Bus — chi dong bo config."
    "$(dirname "$0")/refresh-azure-config.sh"
    exit 0
  fi
  echo "RG '$RG' ton tai nhung thieu tai nguyen — provision lai..."
else
  echo "RG '$RG' chua ton tai — tao moi..."
fi

export RG LOC
"$(dirname "$0")/provision-azure.sh"
