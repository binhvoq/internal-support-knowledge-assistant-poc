#!/usr/bin/env bash
# Smoke test local (can chay khi 4 services dang bat).
set -euo pipefail
CURL=curl
if command -v curl.exe >/dev/null 2>&1; then
  CURL=curl.exe
fi

PYTHON=python3; command -v python3 >/dev/null 2>&1 || PYTHON=python

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CFG_DEV="$ROOT/src/TicketService/appsettings.Development.json"

AUTH_HEADER=()
ENTRA_ENABLED=false
HAS_USER_TOKEN=false

if [[ -f "$CFG_DEV" ]]; then
  ENTRA_JSON=$("$PYTHON" - "$CFG_DEV" <<'PY'
import json, sys, urllib.parse, urllib.request
path = sys.argv[1]
ad = json.load(open(path, encoding="utf-8")).get("AzureAd") or {}
enabled = bool(ad.get("Enabled"))
out = {"enabled": enabled}
if enabled:
    out.update({
        "scope": ad.get("Scope", ""),
        "mcpId": ad.get("McpClientId", ""),
        "mcpSecret": ad.get("McpClientSecret", ""),
        "tenant": ad.get("TenantId", ""),
        "instance": ad.get("Instance", "https://login.microsoftonline.com/"),
        "audience": ad.get("Audience", ""),
    })
print(json.dumps(out))
PY
)
  ENTRA_ENABLED=$("$PYTHON" -c "import json,sys; print(json.loads(sys.argv[1])['enabled'])" "$ENTRA_JSON")
fi

if [[ "$ENTRA_ENABLED" == "True" || "$ENTRA_ENABLED" == "true" ]]; then
  if [[ -n "${SMOKE_BEARER_TOKEN:-}" ]]; then
    AUTH_HEADER=(-H "Authorization: Bearer $SMOKE_BEARER_TOKEN")
    HAS_USER_TOKEN=true
    echo "Entra: dung SMOKE_BEARER_TOKEN (user delegated)."
  else
    TOKEN=$("$PYTHON" - "$ENTRA_JSON" <<'PY'
import json, sys, urllib.parse, urllib.request
ad = json.loads(sys.argv[1])
data = urllib.parse.urlencode({
    "grant_type": "client_credentials",
    "client_id": ad["mcpId"],
    "client_secret": ad["mcpSecret"],
    "scope": f"{ad['audience']}/.default",
}).encode()
url = f"{ad['instance'].rstrip('/')}/{ad['tenant']}/oauth2/v2.0/token"
print(json.load(urllib.request.urlopen(urllib.request.Request(url, data=data)))["access_token"])
PY
)
    AUTH_HEADER=(-H "Authorization: Bearer $TOKEN")
    echo "Entra: client credentials (Support.Service). Re-index can user token."
  fi
fi

echo "Health checks..."
for port in 5001 5002 5003 5004; do
  "$CURL" -sf "http://localhost:$port/health" >/dev/null || { echo "Service :$port khong chay."; exit 1; }
done

echo "MCP tools/list (qua AiOrchestrator)..."
TOOLS=$("$CURL" -sf "http://localhost:5003/mcp/tools" "${AUTH_HEADER[@]}")
TOOL_COUNT=$(echo "$TOOLS" | "$PYTHON" -c "import sys,json; d=json.load(sys.stdin); print(d['count'])")
echo "  tools=$TOOL_COUNT"
[[ "$TOOL_COUNT" -ge 5 ]] || { echo "MCP tools/list thieu tool (can >= 5)."; exit 1; }

echo "Re-index..."
if [[ "$HAS_USER_TOKEN" == "true" ]]; then
  "$CURL" -sf -X POST http://localhost:5002/documents/reindex "${AUTH_HEADER[@]}" | tee /tmp/reindex.json
  echo ""
elif [[ "$ENTRA_ENABLED" == "True" || "$ENTRA_ENABLED" == "true" ]]; then
  echo "  skip (can user token KnowledgeAdmin — set SMOKE_BEARER_TOKEN)"
else
  "$CURL" -sf -X POST http://localhost:5002/documents/reindex | tee /tmp/reindex.json
  echo ""
fi

echo "Tao ticket..."
TICKET=$("$CURL" -sf -X POST http://localhost:5001/tickets \
  "${AUTH_HEADER[@]}" \
  -H "Content-Type: application/json" \
  -d '{"employeeId":"EMP-SMOKE","category":"IT","question":"Toi quen mat khau VPN, can lam gi?"}')
ID=$(echo "$TICKET" | "$PYTHON" -c "import sys,json; print(json.load(sys.stdin)['id'])")
echo "Ticket: $ID"

for i in 1 2 3 4 5 6; do
  sleep 5
  DETAIL=$("$CURL" -sf "http://localhost:5001/tickets/$ID" "${AUTH_HEADER[@]}")
  STATUS=$(echo "$DETAIL" | "$PYTHON" -c "import sys,json; print(json.load(sys.stdin)['status'])")
  HAS_AI=$(echo "$DETAIL" | "$PYTHON" -c "import sys,json; print(json.load(sys.stdin).get('hasAiSuggestion', False))")
  echo "  poll $i: status=$STATUS hasAi=$HAS_AI"
  [[ "$HAS_AI" == "True" ]] && break
done

echo "AI Chat (get status)..."
CHAT_CODE=$("$CURL" -s -o /tmp/chat.json -w "%{http_code}" -X POST http://localhost:5003/ai/chat \
  "${AUTH_HEADER[@]}" \
  -H "Content-Type: application/json" \
  -d "{\"message\":\"Ticket $ID cua toi xu ly den dau roi?\"}")
if [[ "$CHAT_CODE" == "401" || "$CHAT_CODE" == "403" ]]; then
  echo "  skip /ai/chat (can user delegated token — service token khong du EmployeeOrAbove)"
else
  head -c 400 /tmp/chat.json
  echo ""
fi

echo "Resolve ticket..."
RESOLVE_CODE=$("$CURL" -s -o /dev/null -w "%{http_code}" -X POST "http://localhost:5001/tickets/$ID/resolve" \
  "${AUTH_HEADER[@]}" \
  -H "Content-Type: application/json" \
  -d '{"finalAnswer":"Smoke test: da huong dan reset VPN password."}')
if [[ "$RESOLVE_CODE" == "403" ]]; then
  echo "  skip resolve (can Support.Agent — service token khong co role Agent)"
else
  "$CURL" -sf -X POST "http://localhost:5001/tickets/$ID/resolve" \
    "${AUTH_HEADER[@]}" \
    -H "Content-Type: application/json" \
    -d '{"finalAnswer":"Smoke test: da huong dan reset VPN password."}' >/dev/null
  RESOLVED=$("$CURL" -sf "http://localhost:5001/tickets/$ID" "${AUTH_HEADER[@]}")
  echo "$RESOLVED" | "$PYTHON" -c "import sys,json; t=json.load(sys.stdin); assert t['status']=='Resolved', t['status']; print('  status=Resolved OK')"
fi

echo "Smoke test xong."
