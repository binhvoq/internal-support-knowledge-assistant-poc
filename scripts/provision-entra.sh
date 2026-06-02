#!/usr/bin/env bash
# Provision Microsoft Entra apps equivalent to infra/terraform/identity.tf (no terraform apply).
# Writes/merges entra section into config/azure.local.json
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CONFIG_FILE="${CONFIG_FILE:-$ROOT/config/azure.local.json}"
STATE_FILE="${STATE_FILE:-$ROOT/config/entra.provision.state.json}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

PYTHON=python3
command -v python3 >/dev/null 2>&1 || PYTHON=python

export ROOT CONFIG_FILE STATE_FILE SCRIPT_DIR PYTHON
export PREFIX="${PREFIX:-supportpoc}"
export SUFFIX="${SUFFIX:-tf01}"
export TENANT_ID="${TENANT_ID:-88a56b4b-d214-4a74-bb3d-aacc38429f62}"
export TENANT_DOMAIN="${TENANT_DOMAIN:-binhthedevgmail.onmicrosoft.com}"
export SPA_REDIRECT="${SPA_REDIRECT:-http://localhost:5173/}"
export SPA_REDIRECT_EXTRA="${SPA_REDIRECT_EXTRA:-http://127.0.0.1:5173/}"
export BOOTSTRAP_USER_ID="${BOOTSTRAP_USER_ID:-c0656246-0907-4c6f-8871-25b622341cb3}"
export BOOTSTRAP_EMPLOYEE_ID="${BOOTSTRAP_EMPLOYEE_ID:-}"
export BOOTSTRAP_AGENT_ID="${BOOTSTRAP_AGENT_ID:-}"
export BOOTSTRAP_KNOWLEDGE_ADMIN_ID="${BOOTSTRAP_KNOWLEDGE_ADMIN_ID:-}"

log() { echo "==> $*"; }
die() { echo "ERROR: $*" >&2; exit 1; }

command -v az >/dev/null 2>&1 || die "Can cai Azure CLI va chay az login"
command -v "$PYTHON" >/dev/null 2>&1 || die "Can Python 3"

CURRENT_TENANT="$(az account show --query tenantId -o tsv 2>/dev/null || true)"
[[ -n "$CURRENT_TENANT" ]] || die "Chua az login"

exec "$PYTHON" "$SCRIPT_DIR/provision-entra_impl.py"
