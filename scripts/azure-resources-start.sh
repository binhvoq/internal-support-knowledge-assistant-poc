#!/usr/bin/env bash
# Bat lai tai nguyen Azure PoC (provision moi hoac tu bien moi truong).
set -euo pipefail

RG="${RG:-rg-support-poc}"
LOC="${LOC:-southeastasia}"

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
