#!/usr/bin/env bash
# Dung va khoi dong lai 4 backend services (local, khong Docker).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

DOTNET=dotnet
if ! command -v "$DOTNET" >/dev/null 2>&1 && command -v dotnet.exe >/dev/null 2>&1; then
  DOTNET=dotnet.exe
fi
CURL=curl
if command -v curl.exe >/dev/null 2>&1; then
  CURL=curl.exe
fi
PYTHON=python3; command -v python3 >/dev/null 2>&1 || PYTHON=python

CFG_DEV="$ROOT/src/TicketService/appsettings.Development.json"
if [[ -f "$CFG_DEV" ]]; then
  BUS_HOST=$("$PYTHON" - "$CFG_DEV" <<'PY'
import json, sys, re
cfg = json.load(open(sys.argv[1], encoding="utf-8"))
conn = (cfg.get("ServiceBus") or {}).get("ConnectionString") or ""
m = re.search(r"sb://([^/;]+)", conn, re.I)
print(m.group(1) if m else "")
PY
)
  if [[ -n "$BUS_HOST" ]]; then
    if ! "$PYTHON" -c "import socket,sys; socket.getaddrinfo(sys.argv[1], 443)" "$BUS_HOST" >/dev/null 2>&1; then
      echo "WARN: Service Bus host '$BUS_HOST' khong resolve duoc — bat HTTP dev bridge (bo qua ConnectionString)."
      export ServiceBus__ConnectionString=
      export LocalMessaging__HttpBridgeEnabled=true
    fi
  fi
fi

if command -v powershell.exe >/dev/null 2>&1; then
  powershell.exe -NoProfile -Command "foreach (\$p in 5001,5002,5003,5004) { Get-NetTCPConnection -LocalPort \$p -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id \$_.OwningProcess -Force -ErrorAction SilentlyContinue } }" 2>/dev/null || true
fi
sleep 2

"$DOTNET" build -v q
mkdir -p .logs
for spec in TicketService:5001 KnowledgeService:5002 AiOrchestrator:5003 McpToolServer:5004; do
  name="${spec%%:*}"
  port="${spec##*:}"
  log=".logs/${name}.log"
  if command -v nohup >/dev/null 2>&1; then
    nohup env ASPNETCORE_ENVIRONMENT=Development "$DOTNET" run --no-launch-profile --project "src/$name" --urls "http://localhost:$port" --no-build >"$log" 2>&1 &
  else
    env ASPNETCORE_ENVIRONMENT=Development "$DOTNET" run --no-launch-profile --project "src/$name" --urls "http://localhost:$port" --no-build >"$log" 2>&1 &
  fi
done

FAIL=0
for port in 5001 5002 5003 5004; do
  ready=0
  for _ in {1..45}; do
    if "$CURL" -sf "http://localhost:$port/health" >/dev/null; then
      ready=1
      break
    fi
    sleep 1
  done

  if [[ "$ready" == "1" ]]; then
    echo "OK health :$port"
  else
    echo "FAIL health :$port"
    FAIL=1
  fi
done

for port in 5001 5003; do
  rdy=0
  for _ in {1..30}; do
    code=$("$CURL" -s -o /dev/null -w "%{http_code}" "http://localhost:$port/ready" || true)
    if [[ "$code" == "200" ]]; then
      rdy=1
      break
    fi
    sleep 1
  done
  if [[ "$rdy" == "1" ]]; then
    echo "OK ready :$port"
  else
    echo "FAIL ready :$port (messaging pipeline chua san sang — xem .logs/)"
    FAIL=1
  fi
done

exit "$FAIL"
