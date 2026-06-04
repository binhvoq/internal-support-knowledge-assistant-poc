#!/usr/bin/env bash
# Real integration smoke — localhost + Azure. Khong thay unit test.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
CURL=curl; command -v curl.exe >/dev/null 2>&1 && CURL=curl.exe
PYTHON=python3; command -v python3 >/dev/null 2>&1 || PYTHON=python
DOTNET=dotnet; command -v dotnet.exe >/dev/null 2>&1 && DOTNET=dotnet.exe
CFG="$ROOT/src/TicketService/appsettings.Development.json"
PASS=0
FAIL=0
SKIP=0

log() { echo ""; echo "=== $* ==="; }
ok() { echo "PASS: $*"; PASS=$((PASS + 1)); }
bad() { echo "FAIL: $*"; FAIL=$((FAIL + 1)); }
skip() { echo "SKIP: $*"; SKIP=$((SKIP + 1)); }

get_token() {
  "$PYTHON" - "$CFG" <<'PY'
import json, sys, urllib.parse, urllib.request
ad = json.load(open(sys.argv[1], encoding="utf-8")).get("AzureAd") or {}
if not ad.get("Enabled"):
    print("")
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

AUTH=()
TOKEN=$(get_token || true)
if [[ -n "$TOKEN" ]]; then AUTH=(-H "Authorization: Bearer $TOKEN"); fi

stop_ports() {
  if command -v powershell.exe >/dev/null 2>&1; then
    powershell.exe -NoProfile -Command "foreach (\$p in 5001,5002,5003,5004) { Get-NetTCPConnection -LocalPort \$p -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id \$_.OwningProcess -Force -ErrorAction SilentlyContinue } }" 2>/dev/null || true
  fi
  sleep 2
}

build_run_env() {
  RUN_ENV=(ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}")
  if [[ -n "${E2E_SB_CONN+set}" ]]; then
    RUN_ENV+=(ServiceBus__ConnectionString="$E2E_SB_CONN")
  fi
  if [[ -n "${E2E_HTTP_BRIDGE+set}" ]]; then
    RUN_ENV+=(LocalMessaging__HttpBridgeEnabled="$E2E_HTTP_BRIDGE")
  fi
}

start_services() {
  "$DOTNET" build -v q >/dev/null
  mkdir -p .logs
  build_run_env
  for spec in TicketService:5001 KnowledgeService:5002 AiOrchestrator:5003 McpToolServer:5004; do
    local name="${spec%%:*}" port="${spec##*:}"
    if command -v nohup >/dev/null 2>&1; then
      nohup env "${RUN_ENV[@]}" \
        "$DOTNET" run --no-launch-profile --project "src/$name" --urls "http://localhost:$port" --no-build >".logs/${name}.log" 2>&1 &
    else
      env "${RUN_ENV[@]}" \
        "$DOTNET" run --no-launch-profile --project "src/$name" --urls "http://localhost:$port" --no-build >".logs/${name}.log" 2>&1 &
    fi
  done
  for port in 5001 5002 5003 5004; do
    for _ in {1..45}; do
      "$CURL" -sf "http://localhost:$port/health" >/dev/null && break
      sleep 1
    done
  done
}

wait_ready() {
  local port=$1 expect=$2
  for _ in {1..30}; do
    local code body
    code=$("$CURL" -s -o /tmp/ready-$port.json -w "%{http_code}" "http://localhost:$port/ready" || echo 000)
    body=$(cat /tmp/ready-$port.json 2>/dev/null || echo "")
    if [[ "$code" == "$expect" ]]; then
      echo "$body"
      return 0
    fi
    sleep 1
  done
  echo "{}"
  return 1
}

create_ticket() {
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

poll_ticket() {
  local id=$1 want_status=$2 max=${3:-12}
  for i in $(seq 1 "$max"); do
    local d st
    d=$("$CURL" -sf "http://localhost:5001/tickets/$id" "${AUTH[@]}")
    st=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['status'])" <<<"$d")
    echo "  poll $i: status=$st"
    [[ "$st" == "$want_status" ]] && { echo "$d"; return 0; }
    sleep 5
  done
  return 1
}

poll_job() {
  local id=$1
  "$CURL" -sf "http://localhost:5003/tickets/$id/auto-suggestion" "${AUTH[@]}" 2>/dev/null || echo "{}"
}

# --- 1. Ready matrix ---
log "Ready: SB off + bridge off -> 503"
export E2E_SB_CONN=""
export E2E_HTTP_BRIDGE=false
export ASPNETCORE_ENVIRONMENT=Development
stop_ports
start_services
c1=$(wait_ready 5001 503 && echo ok || echo fail)
c3=$(wait_ready 5003 503 && echo ok || echo fail)
if [[ "$c1" == *ok* && "$c3" == *ok* ]]; then ok "not-ready (sb off, bridge off)"; else bad "not-ready 503"; fi

log "Ready: SB off + bridge on + Production -> 503"
export E2E_SB_CONN=""
export E2E_HTTP_BRIDGE=true
export ASPNETCORE_ENVIRONMENT=Production
stop_ports
start_services
c1=$(wait_ready 5001 503 && echo ok || echo fail)
if [[ "$c1" == *ok* ]]; then ok "not-ready (prod + bridge)"; else bad "prod bridge should 503"; fi

log "Ready: HTTP bridge Development -> 200 transport=http-bridge"
export ASPNETCORE_ENVIRONMENT=Development
export E2E_SB_CONN=""
export E2E_HTTP_BRIDGE=true
stop_ports
start_services
body=$(wait_ready 5001 200 || true)
if echo "$body" | grep -q 'http-bridge'; then ok "http-bridge ready"; else bad "http-bridge ready: $body"; fi

log "Happy path HTTP bridge"
TICKET=$(create_ticket "VPN password reset E2E bridge")
ID=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['id'])" <<<"$TICKET")
ST=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['status'])" <<<"$TICKET")
[[ "$ST" == "New" ]] && ok "bridge ticket created New" || bad "bridge create status=$ST"
if poll_ticket "$ID" Suggested 14 >/dev/null; then
  ok "bridge ticket -> Suggested"
else
  bad "bridge ticket khong len Suggested"
fi

log "Ready + Happy: Service Bus that"
unset E2E_SB_CONN E2E_HTTP_BRIDGE
export ASPNETCORE_ENVIRONMENT=Development
stop_ports
start_services
body=$(wait_ready 5001 200 || true)
if echo "$body" | grep -q servicebus; then ok "servicebus ready"; else bad "servicebus ready: $body"; fi
TICKET=$(create_ticket "VPN E2E service bus real")
ID=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['id'])" <<<"$TICKET")
seen_running=false
for i in $(seq 1 14); do
  sleep 5
  J=$(poll_job "$ID")
  js=$("$PYTHON" -c "import json,sys; d=json.load(sys.stdin); print(d.get('status',''))" <<<"$J" 2>/dev/null || echo "")
  d=$("$CURL" -sf "http://localhost:5001/tickets/$ID" "${AUTH[@]}")
  st=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['status'])" <<<"$d")
  has=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin).get('hasAiSuggestion',False))" <<<"$d")
  echo "  poll $i: ticket=$st hasAi=$has job=$js"
  [[ "$js" == "Running" ]] && seen_running=true
  if [[ "$st" == "Suggested" && "$has" == "True" && "$js" == "Completed" ]]; then
    ok "SB happy path $ID"
    break
  fi
done
if [[ "${seen_running:-false}" != "true" ]]; then
  echo "  WARN: khong thay job Running (co the qua nhanh)"
fi

log "BC-03 __SKIP_CONSIDER__"
TICKET=$(create_ticket "VPN __SKIP_CONSIDER__ slow apply test")
ID=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['id'])" <<<"$TICKET")
sleep 8
J=$(poll_job "$ID")
js=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin).get('status',''))" <<<"$J")
d=$("$CURL" -sf "http://localhost:5001/tickets/$ID" "${AUTH[@]}")
st=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['status'])" <<<"$d")
ai=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin).get('aiSuggestedAnswer'))" <<<"$d")
if [[ "$js" == "Produced" && "$st" == "New" && "$ai" == "None" ]]; then
  ok "BC-03 Produced + ticket New"
else
  bad "BC-03: job=$js status=$st ai=$ai"
fi
"$CURL" -sf -X POST "http://localhost:5001/tickets/$ID/resolve" "${AUTH[@]}" \
  -H "Content-Type: application/json" -d '{"finalAnswer":"Agent resolve during Produced"}' >/dev/null
sleep 2
d=$("$CURL" -sf "http://localhost:5001/tickets/$ID" "${AUTH[@]}")
st=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['status'])" <<<"$d")
[[ "$st" == "Resolved" ]] && ok "BC-03 agent resolve" || bad "BC-03 resolve status=$st"

log "BC-06 race agent vs AI"
TICKET=$(create_ticket "VPN race agent wins")
ID=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['id'])" <<<"$TICKET")
"$CURL" -sf -X POST "http://localhost:5001/tickets/$ID/resolve" "${AUTH[@]}" \
  -H "Content-Type: application/json" -d '{"finalAnswer":"Agent won first"}' >/dev/null
echo "  waiting 65s for AI job..."
sleep 65
d=$("$CURL" -sf "http://localhost:5001/tickets/$ID" "${AUTH[@]}")
st=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['status'])" <<<"$d")
ai=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin).get('aiSuggestedAnswer'))" <<<"$d")
J=$(poll_job "$ID")
js=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin).get('status',''))" <<<"$J")
dr=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin).get('discardReason',''))" <<<"$J")
if [[ "$st" == "Resolved" && "$ai" == "None" && "$js" == "Discarded" ]]; then
  ok "BC-06 agent wins (Discarded)"
else
  bad "BC-06: status=$st ai=$ai job=$js discard=$dr"
fi

log "AI fail __FAIL_AI__"
TICKET=$(create_ticket "VPN __FAIL_AI__")
ID=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['id'])" <<<"$TICKET")
for i in $(seq 1 10); do
  sleep 5
  J=$(poll_job "$ID")
  js=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin).get('status',''))" <<<"$J" 2>/dev/null || echo "")
  [[ "$js" == "Failed" ]] && break
done
d=$("$CURL" -sf "http://localhost:5001/tickets/$ID" "${AUTH[@]}")
st=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['status'])" <<<"$d")
if [[ "$st" == "New" && "$js" == "Failed" ]]; then ok "AI fail"; else bad "AI fail: st=$st job=$js"; fi
LIST=$("$CURL" -sf "http://localhost:5001/tickets?status=Suggested" "${AUTH[@]}")
if echo "$LIST" | "$PYTHON" -c "import json,sys; ids=[t['id'] for t in json.load(sys.stdin)]; sys.exit(0 if '$ID' not in ids else 1)"; then
  ok "AI fail not in Suggested queue"
else
  bad "AI fail ticket in Suggested queue"
fi

log "Bridge notify failure (AiOrchestrator down)"
export E2E_SB_CONN=""
export E2E_HTTP_BRIDGE=true
export ASPNETCORE_ENVIRONMENT=Development
stop_ports
"$DOTNET" build -v q >/dev/null
build_run_env
for spec in TicketService:5001 KnowledgeService:5002 McpToolServer:5004; do
  name="${spec%%:*}"; port="${spec##*:}"
  nohup env "${RUN_ENV[@]}" \
    "$DOTNET" run --no-launch-profile --project "src/$name" --urls "http://localhost:$port" --no-build >".logs/${name}.log" 2>&1 &
done
sleep 8
TICKET=$(create_ticket "Bridge notify fail test")
flag=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin).get('autoSuggestionNotifyFailed',False))" <<<"$TICKET")
tid=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['id'])" <<<"$TICKET")
if [[ "$flag" == "True" ]]; then ok "autoSuggestionNotifyFailed=true"; else bad "expected notify failed flag"; fi
# restart all with bridge
stop_ports
start_services
TICKET2=$(create_ticket "Bridge notify recovery")
flag2=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin).get('autoSuggestionNotifyFailed',False))" <<<"$TICKET2")
id2=$("$PYTHON" -c "import json,sys; print(json.load(sys.stdin)['id'])" <<<"$TICKET2")
poll_ticket "$id2" Suggested 10 >/dev/null && ok "bridge recovery -> Suggested" || bad "bridge recovery"

# Restore SB for final state
unset E2E_SB_CONN E2E_HTTP_BRIDGE
export ASPNETCORE_ENVIRONMENT=Development
stop_ports
start_services

echo ""
echo "=== Tong ket: PASS=$PASS FAIL=$FAIL SKIP=$SKIP ==="
[[ "$FAIL" -eq 0 ]]
