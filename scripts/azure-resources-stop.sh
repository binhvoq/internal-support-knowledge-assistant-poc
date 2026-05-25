#!/usr/bin/env bash
# Tat (xoa) tai nguyen Azure ton phi co dinh trong RG PoC.
# Luu trang thai truoc khi xoa de co the provision lai bang azure-resources-start.sh
set -euo pipefail

RG="${RG:-rg-support-poc}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STATE_FILE="${STATE_FILE:-$ROOT/config/azure.resources.state.json}"

if ! az group show -n "$RG" -o none 2>/dev/null; then
  echo "RG '$RG' khong ton tai — khong co gi de tat."
  exit 0
fi

echo "==> Luu trang thai tai nguyen vao $STATE_FILE"
mkdir -p "$(dirname "$STATE_FILE")"
az resource list -g "$RG" -o json > "$STATE_FILE"
COUNT=$(az resource list -g "$RG" --query "length(@)" -o tsv 2>/dev/null || echo 0)
echo "   Da luu snapshot ($COUNT tai nguyen trong RG)."

delete_if_exists() {
  local kind="$1" confirm="$2" name="$3"
  shift 3
  if az "$@" show -g "$RG" -n "$name" -o none 2>/dev/null; then
    echo "==> Xoa $kind: $name"
    if [[ "$confirm" == "yes" ]]; then
      az "$@" delete -g "$RG" -n "$name" --yes -o none
    else
      az "$@" delete -g "$RG" -n "$name" -o none
    fi
  else
    echo "   Bo qua $kind (khong ton tai): $name"
  fi
}

echo "==> Xoa Azure AI Search (ton phi ~\$75/thang, Basic SKU)"
for name in $(az search service list -g "$RG" --query "[].name" -o tsv); do
  delete_if_exists "Search" yes "$name" search service
done

echo "==> Xoa Service Bus (ton phi ~\$10/thang, Standard SKU)"
for name in $(az servicebus namespace list -g "$RG" --query "[].name" -o tsv); do
  delete_if_exists "Service Bus" no "$name" servicebus namespace
done

echo "==> Xoa Cognitive Services / OpenAI"
for name in $(az cognitiveservices account list -g "$RG" --query "[].name" -o tsv); do
  delete_if_exists "OpenAI" no "$name" cognitiveservices account
done

echo "==> Xoa Storage Account"
for name in $(az storage account list -g "$RG" --query "[].name" -o tsv); do
  if az storage account show -g "$RG" -n "$name" -o none 2>/dev/null; then
    echo "==> Xoa Storage: $name"
    az storage account delete -g "$RG" -n "$name" -y -o none
  fi
done

echo "==> Xoa Log Analytics / Application Insights (neu co)"
for name in $(az monitor log-analytics workspace list -g "$RG" --query "[].name" -o tsv 2>/dev/null || true); do
  delete_if_exists "Log Analytics" yes "$name" monitor log-analytics workspace
done
for id in $(az resource list -g "$RG" --resource-type "Microsoft.Insights/components" --query "[].id" -o tsv 2>/dev/null || true); do
  echo "==> Xoa Application Insights: $(basename "$id")"
  az resource delete --ids "$id" -o none 2>/dev/null || true
done

REMAINING=$(az resource list -g "$RG" --query "length(@)" -o tsv)
if [[ "$REMAINING" == "0" ]]; then
  echo "==> RG trong — xoa RG $RG"
  az group delete -n "$RG" --yes --no-wait -o none
  echo "   Dang xoa RG (no-wait)."
else
  echo "==> Con $REMAINING tai nguyen trong RG (kiem tra thu cong neu can)."
  az resource list -g "$RG" -o table
fi

echo ""
echo "Hoan tat. Bat lai: bash scripts/azure-resources-start.sh"
echo "Trang thai da luu: $STATE_FILE"
