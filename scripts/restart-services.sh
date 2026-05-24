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
    nohup "$DOTNET" run --project "src/$name" --urls "http://localhost:$port" --no-build >"$log" 2>&1 &
  else
    "$DOTNET" run --project "src/$name" --urls "http://localhost:$port" --no-build >"$log" 2>&1 &
  fi
done

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
    echo "OK :$port"
  else
    echo "FAIL :$port"
  fi
done
