#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CFG="$ROOT/config/azure.local.json"
VARS="$ROOT/infra/terraform/variables.tf"

PYTHON=python3
command -v python3 >/dev/null 2>&1 || PYTHON=python

echo "== Identity hardening quick check =="

if [[ ! -f "$CFG" ]]; then
  echo "[WARN] Thieu config/azure.local.json (bo qua check bootstrap role assignment)."
else
  "$PYTHON" - "$CFG" <<'PY'
import json, sys
cfg = json.load(open(sys.argv[1], encoding="utf-8"))
entra = cfg.get("entra") or {}
roles = (entra.get("bootstrapRoleAssignments") or {})
if not roles:
    principals = entra.get("bootstrapPrincipals") or {}
    if principals:
        roles = {
            "Support.Employee": principals.get("employee", ""),
            "Support.Agent": principals.get("agent", ""),
            "Support.KnowledgeAdmin": principals.get("knowledgeAdmin", ""),
        }
if not roles:
    print("[INFO] Khong co bootstrapRoleAssignments.")
    raise SystemExit(0)
owners = {}
for role, principal in roles.items():
    owners.setdefault(principal, []).append(role)
multi = {k: v for k, v in owners.items() if len(v) > 1}
if multi:
    print("[WARN] Co principal duoc gan nhieu role (chi nen cho PoC):")
    for principal, rs in multi.items():
        print(f"  - {principal}: {', '.join(rs)}")
else:
    print("[OK] Bootstrap role assignment da tach principal theo role.")
PY
fi

"$PYTHON" - "$ROOT/src" <<'PY'
import pathlib, re, sys
src = pathlib.Path(sys.argv[1])
hits = []
for p in src.rglob("Program.cs"):
    text = p.read_text(encoding="utf-8", errors="ignore")
    for idx, line in enumerate(text.splitlines(), start=1):
        if "AllowAnonymous()" in line:
            hits.append((p, idx, line.strip()))
if hits:
    print("[INFO] Endpoint anonymous hien tai (review de tranh mo qua muc):")
    for p, idx, line in hits:
        print(f"  - {p}:{idx}: {line}")
else:
    print("[OK] Khong tim thay AllowAnonymous trong Program.cs.")
PY

"$PYTHON" - "$VARS" <<'PY'
import pathlib, sys
text = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8", errors="ignore")
if "allow_bootstrap_multi_role_principal" in text:
    print("[OK] Terraform co guardrail allow_bootstrap_multi_role_principal.")
else:
    print("[WARN] Chua co guardrail bootstrap multi-role trong Terraform.")
PY

echo "== Done =="
