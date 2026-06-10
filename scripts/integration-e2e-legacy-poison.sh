#!/usr/bin/env bash
# Case bat buoc truoc commit: DB migration legacy + Poison/DLQ (Service Bus that).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
ORCH_DB="$ROOT/src/AiOrchestrator/orchestrator.db"
CURL=curl; command -v curl.exe >/dev/null 2>&1 && CURL=curl.exe
PYTHON=python3; command -v python3 >/dev/null 2>&1 || PYTHON=python
CFG="$ROOT/src/TicketService/appsettings.Development.json"
PASS=0
FAIL=0

log() { echo ""; echo "=== $* ==="; }
ok() { echo "PASS: $*"; PASS=$((PASS + 1)); }
bad() { echo "FAIL: $*"; FAIL=$((FAIL + 1)); }

get_token() {
  "$PYTHON" - "$CFG" <<'PY'
import json, sys, urllib.parse, urllib.request
ad = json.load(open(sys.argv[1], encoding="utf-8")).get("AzureAd") or {}
if not ad.get("Enabled"):
    sys.exit(0)
data = urllib.parse.urlencode({
    "grant_type": "client_credentials",
    "client_id": ad["McpClientId"],
    "client_secret": ad["McpClientSecret"],
    "scope": f"{ad['Audience']}/.default",
}).encode()
url = f"{ad['Instance'].rstrip('/')}/{ad['TenantId']}/oauth2/v2.0/token"
print(json.load(urllib.request.urlopen(urllib.request.Request(url, data=data)))["access_token"])
PY
}

post_ticket() {
  E2E_TOKEN="$TOKEN" "$PYTHON" - "$1" <<'PY'
import json, os, sys, urllib.request
q = sys.argv[1]
body = json.dumps({"employeeId": "EMP-E2E", "category": "IT", "question": q}).encode()
req = urllib.request.Request("http://localhost:5001/tickets", data=body, method="POST",
    headers={"Content-Type": "application/json"})
tok = os.environ.get("E2E_TOKEN")
if tok:
    req.add_header("Authorization", f"Bearer {tok}")
print(urllib.request.urlopen(req).read().decode())
PY
}

stop_ports() {
  if command -v powershell.exe >/dev/null 2>&1; then
    powershell.exe -NoProfile -Command 'foreach ($p in 5001,5002,5003,5004) { Get-NetTCPConnection -LocalPort $p -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue } }' 2>/dev/null || true
  fi
  sleep 3
}

TOKEN=$(get_token || true)

# --- 1. Legacy DB migration ---
log "DB migration legacy"
stop_ports
# Backup *.db.bak-* da nam trong .gitignore — xoa sau test, khong giu tren disk.
rm -f "$ORCH_DB" "$ORCH_DB-shm" "$ORCH_DB-wal" 2>/dev/null || true

"$PYTHON" - "$ORCH_DB" <<'PY'
import sqlite3, sys
path = sys.argv[1]
con = sqlite3.connect(path)
con.execute("""CREATE TABLE TicketSuggestionStates (CorrelationId TEXT NOT NULL PRIMARY KEY)""")
con.commit()
tables = [r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'").fetchall()]
assert "TicketSuggestionStates" in tables
assert "AutoSuggestionJobs" not in tables
print("legacy tables:", tables)
con.close()
PY

bash scripts/restart-services.sh >/dev/null
sleep 2

"$PYTHON" - "$ORCH_DB" <<'PY'
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
tables = {r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'")}
for t in ("TicketSuggestionSagas", "InboxState", "OutboxMessage", "OutboxState"):
    assert t in tables, tables
assert "AutoSuggestionJobs" not in tables, tables
cols = [r[1] for r in con.execute("PRAGMA table_info(TicketSuggestionSagas)")]
for c in ("CorrelationId", "TicketId", "JobId", "CurrentState"):
    assert c in cols, cols
idx = list(con.execute("SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='TicketSuggestionSagas'"))
print("tables ok, saga columns:", len(cols), "indexes:", idx)
con.close()
PY
ok "schema: TicketSuggestionSagas + MassTransit inbox/outbox after restart"

TICKET=$(post_ticket "VPN legacy migration E2E")
ID=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['id'])" <<<"$TICKET")
for i in $(seq 1 14); do
  sleep 5
  d=$("$CURL" -sf "http://localhost:5001/tickets/$ID" ${TOKEN:+-H "Authorization: Bearer $TOKEN"})
  st=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['status'])" <<<"$d")
  j=$("$CURL" -sf "http://localhost:5003/tickets/$ID/auto-suggestion" ${TOKEN:+-H "Authorization: Bearer $TOKEN"} 2>/dev/null || echo "{}")
  js=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin).get('status',''))" <<<"$j" 2>/dev/null || echo "")
  echo "  poll $i: ticket=$st job=$js"
  [[ "$st" == "Suggested" && "$js" == "Completed" ]] && break
done
if [[ "$st" == "Suggested" && "$js" == "Completed" ]]; then
  ok "legacy DB: ticket $ID Suggested + job Completed"
else
  bad "legacy DB: ticket=$st saga=$js"
  "$CURL" -sf "http://localhost:5003/debug/saga-instances?ticketId=$ID" 2>/dev/null | head -c 600 || true
fi

# --- 2. Poison / DLQ ---
log "Poison/DLQ (__POISON_AI__)"
DLQ_BEFORE=$("$CURL" -sf "http://localhost:5003/debug/dlq?queue=generate-suggestion-requested")
FAILED_BEFORE=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin).get('totalFailedMessages',0))" <<<"$DLQ_BEFORE")
echo "  dlq totalFailedMessages before=$FAILED_BEFORE"

RG="${RG:-rg-support-poc}"
NS=$(python -c "import json,re; c=json.load(open('$CFG')); m=re.search(r'sb://([^/;]+)', c['ServiceBus']['ConnectionString']); print(m.group(1) if m else '')" 2>/dev/null || echo "")
if [[ -n "$NS" ]]; then
  az servicebus queue show -g "$RG" --namespace-name "$NS" -n generate-suggestion-requested \
    --query "{active:countDetails.activeMessageCount,dead:countDetails.deadLetterMessageCount}" -o json 2>/dev/null | tee /tmp/sb-before.json || true
fi

TICKET=$(post_ticket "VPN __POISON_AI__ dlq test")
PID=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['id'])" <<<"$TICKET")
echo "  poison ticket=$PID"

HEALTH_OK=true
for round in $(seq 1 18); do
  sleep 10
  hc=$("$CURL" -s -o /dev/null -w "%{http_code}" http://localhost:5003/health || echo 000)
  [[ "$hc" != "200" ]] && HEALTH_OK=false
  d=$("$CURL" -sf "http://localhost:5001/tickets/$PID" ${TOKEN:+-H "Authorization: Bearer $TOKEN"})
  st=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['status'])" <<<"$d")
  DLQ=$("$CURL" -sf "http://localhost:5003/debug/dlq?queue=generate-suggestion-requested")
  FAILED=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin).get('totalFailedMessages',0))" <<<"$DLQ")
  echo "  wait ${round}x10s: health=$hc ticket=$st dlqFailed=$FAILED (delta=$((FAILED - FAILED_BEFORE)))"
  if [[ "$st" == "New" && "$FAILED" -gt "$FAILED_BEFORE" ]]; then
    break
  fi
done

if [[ "$st" == "New" ]]; then ok "poison ticket still New"; else bad "poison ticket status=$st"; fi
if [[ "$FAILED" -gt "$FAILED_BEFORE" ]]; then ok "dlq totalFailedMessages increased ($FAILED_BEFORE -> $FAILED)"; else bad "dlq count did not increase"; fi
if [[ "$HEALTH_OK" == "true" ]]; then ok "AiOrchestrator health OK during poison"; else bad "orchestrator unhealthy"; fi

if [[ -n "$NS" ]]; then
  az servicebus queue show -g "$RG" --namespace-name "$NS" -n generate-suggestion-requested \
    --query "{active:countDetails.activeMessageCount,dead:countDetails.deadLetterMessageCount}" -o json 2>/dev/null | tee /tmp/sb-after.json || true
  ACTIVE=$(python -c "import json; print(json.load(open('/tmp/sb-after.json'))['active'])" 2>/dev/null || echo "?")
  if [[ "$ACTIVE" == "0" || "$ACTIVE" -lt 5 ]]; then
    ok "Azure queue active=$ACTIVE (khong ket)"
  else
    bad "Azure queue active=$ACTIVE (co the ket)"
  fi
fi

echo ""
echo "=== Tong ket legacy+poison: PASS=$PASS FAIL=$FAIL ==="
[[ "$FAIL" -eq 0 ]]
