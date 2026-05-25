#!/usr/bin/env bash
# Smoke test local (can chay khi 4 services dang bat).
set -euo pipefail
CURL=curl
if command -v curl.exe >/dev/null 2>&1; then
  CURL=curl.exe
fi

PYTHON=python3; command -v python3 >/dev/null 2>&1 || PYTHON=python

echo "Health checks..."
for port in 5001 5002 5003 5004; do
  "$CURL" -sf "http://localhost:$port/health" >/dev/null || { echo "Service :$port khong chay."; exit 1; }
done

echo "MCP tools/list (qua AiOrchestrator)..."
TOOLS=$("$CURL" -sf "http://localhost:5003/mcp/tools")
TOOL_COUNT=$(echo "$TOOLS" | "$PYTHON" -c "import sys,json; d=json.load(sys.stdin); print(d['count'])")
echo "  tools=$TOOL_COUNT"
[[ "$TOOL_COUNT" -ge 5 ]] || { echo "MCP tools/list thieu tool (can >= 5)."; exit 1; }

echo "Re-index..."
"$CURL" -sf -X POST http://localhost:5002/documents/reindex | tee /tmp/reindex.json
echo ""

echo "Tao ticket..."
TICKET=$("$CURL" -sf -X POST http://localhost:5001/tickets \
  -H "Content-Type: application/json" \
  -d '{"employeeId":"EMP-SMOKE","category":"IT","question":"Toi quen mat khau VPN, can lam gi?"}')
PYTHON=python3; command -v python3 >/dev/null 2>&1 || PYTHON=python
ID=$(echo "$TICKET" | "$PYTHON" -c "import sys,json; print(json.load(sys.stdin)['id'])")
echo "Ticket: $ID"

for i in 1 2 3 4 5 6; do
  sleep 5
  DETAIL=$("$CURL" -sf "http://localhost:5001/tickets/$ID")
  STATUS=$(echo "$DETAIL" | "$PYTHON" -c "import sys,json; print(json.load(sys.stdin)['status'])")
  HAS_AI=$(echo "$DETAIL" | "$PYTHON" -c "import sys,json; print(json.load(sys.stdin).get('hasAiSuggestion', False))")
  echo "  poll $i: status=$STATUS hasAi=$HAS_AI"
  [[ "$HAS_AI" == "True" ]] && break
done

echo "AI Chat (get status)..."
"$CURL" -sf -X POST http://localhost:5003/ai/chat \
  -H "Content-Type: application/json" \
  -d "{\"message\":\"Ticket $ID cua toi xu ly den dau roi?\"}" | head -c 400
echo ""

echo "Resolve ticket..."
"$CURL" -sf -X POST "http://localhost:5001/tickets/$ID/resolve" \
  -H "Content-Type: application/json" \
  -d '{"finalAnswer":"Smoke test: da huong dan reset VPN password."}' >/dev/null
RESOLVED=$("$CURL" -sf "http://localhost:5001/tickets/$ID")
echo "$RESOLVED" | "$PYTHON" -c "import sys,json; t=json.load(sys.stdin); assert t['status']=='Resolved', t['status']; print('  status=Resolved OK')"

echo "Smoke test xong."
