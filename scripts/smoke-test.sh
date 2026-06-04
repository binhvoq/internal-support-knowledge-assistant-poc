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

echo "Readiness (messaging pipeline)..."
for spec in "5001:ticket-service" "5003:ai-orchestrator"; do
  port="${spec%%:*}"
  name="${spec##*:}"
  READY_CODE=$("$CURL" -s -o /tmp/ready-"$port".json -w "%{http_code}" "http://localhost:$port/ready")
  if [[ "$READY_CODE" != "200" ]]; then
    echo "FAIL /ready :$port ($name) HTTP $READY_CODE"
    cat /tmp/ready-"$port".json 2>/dev/null || true
    echo ""
    echo "Goi y: Service Bus unreachable -> bash scripts/restart-services.sh (tu bat HTTP bridge) hoac fix Azure SB."
    exit 1
  fi
  echo "  OK /ready :$port"
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

HAS_AI=False
STATUS=New
JOB_CODE=404
JOB_STATUS=""
for i in 1 2 3 4 5 6 7 8 9 10 11 12; do
  sleep 5
  DETAIL=$("$CURL" -sf "http://localhost:5001/tickets/$ID" "${AUTH_HEADER[@]}")
  STATUS=$(echo "$DETAIL" | "$PYTHON" -c "import sys,json; print(json.load(sys.stdin)['status'])")
  HAS_AI=$(echo "$DETAIL" | "$PYTHON" -c "import sys,json; print(json.load(sys.stdin).get('hasAiSuggestion', False))")
  JOB_CODE=$("$CURL" -s -o /tmp/job.json -w "%{http_code}" "http://localhost:5003/tickets/$ID/auto-suggestion")
  if [[ "$JOB_CODE" == "200" ]]; then
    JOB_STATUS=$(echo "$(cat /tmp/job.json)" | "$PYTHON" -c "import sys,json; print(json.load(sys.stdin).get('status',''))")
  else
    JOB_STATUS=""
  fi
  echo "  poll $i: status=$STATUS hasAi=$HAS_AI jobHttp=$JOB_CODE jobStatus=$JOB_STATUS"
  if [[ "$HAS_AI" == "True" || "$STATUS" == "Suggested" ]]; then
    break
  fi
  if [[ "$JOB_STATUS" == "Failed" ]]; then
    echo "FAIL: auto-suggestion job Failed."
    cat /tmp/job.json
    exit 1
  fi
done

if [[ "$HAS_AI" != "True" && "$STATUS" != "Suggested" ]]; then
  echo "FAIL: auto-suggestion khong hoan thanh trong 60s (can hasAi=True hoac status=Suggested)."
  echo "  job endpoint HTTP=$JOB_CODE"
  "$CURL" -sf "http://localhost:5003/debug/auto-suggestion-jobs?ticketId=$ID" | head -c 800 || true
  echo ""
  exit 1
fi

if [[ "$JOB_CODE" != "200" ]]; then
  echo "FAIL: khong tim thay auto-suggestion job cho ticket $ID (HTTP $JOB_CODE)."
  exit 1
fi

if [[ "$JOB_STATUS" != "Completed" ]]; then
  echo "FAIL: job status=$JOB_STATUS (can Completed sau happy path)."
  cat /tmp/job.json
  exit 1
fi

echo "Auto suggestion OK: status=$STATUS job=$JOB_STATUS"

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
