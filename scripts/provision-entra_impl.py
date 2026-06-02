#!/usr/bin/env python3
"""Entra provision via Azure CLI + Graph (mirror infra/terraform/identity.tf)."""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

AZ_CLI = shutil.which("az") or shutil.which("az.cmd") or "az"

PREFIX = os.environ.get("PREFIX", "supportpoc")
SUFFIX = os.environ.get("SUFFIX", "tf01")
TENANT_ID = os.environ.get("TENANT_ID", "88a56b4b-d214-4a74-bb3d-aacc38429f62")
TENANT_DOMAIN = os.environ.get("TENANT_DOMAIN", "binhthedevgmail.onmicrosoft.com")
# Tenant policy often requires identifier URI to include appId (see aka.ms/identifier-uri-formatting-error).
# Static URI from Terraform (api://prefix-suffix-supportpoc) may fail; we set api://{clientId} after create.
SCOPE_ID = "a8f3c2e1-9b4d-4f6a-bcde-1234567890ab"
IDENTIFIER_URI: str | None = None
SCOPE_FULL: str | None = None
SPA_REDIRECT = os.environ.get("SPA_REDIRECT", "http://localhost:5173/")
SPA_REDIRECT_EXTRA = os.environ.get("SPA_REDIRECT_EXTRA", "http://127.0.0.1:5173/")
SPA_REDIRECT_URIS = list(dict.fromkeys([SPA_REDIRECT, SPA_REDIRECT_EXTRA]))
BOOTSTRAP_USER_ID = os.environ.get(
    "BOOTSTRAP_USER_ID", "c0656246-0907-4c6f-8871-25b622341cb3"
)
BOOTSTRAP_EMPLOYEE_ID = os.environ.get("BOOTSTRAP_EMPLOYEE_ID", "") or BOOTSTRAP_USER_ID
BOOTSTRAP_AGENT_ID = os.environ.get("BOOTSTRAP_AGENT_ID", "") or BOOTSTRAP_USER_ID
BOOTSTRAP_KNOWLEDGE_ADMIN_ID = os.environ.get("BOOTSTRAP_KNOWLEDGE_ADMIN_ID", "") or BOOTSTRAP_USER_ID

BOOTSTRAP_ROLE_ASSIGNMENTS = {
    "Support.Employee": BOOTSTRAP_EMPLOYEE_ID,
    "Support.Agent": BOOTSTRAP_AGENT_ID,
    "Support.KnowledgeAdmin": BOOTSTRAP_KNOWLEDGE_ADMIN_ID,
}

API_NAME = f"{PREFIX} API ({SUFFIX})"
SPA_NAME = f"{PREFIX} SPA ({SUFFIX})"
MCP_NAME = f"{PREFIX} MCP Service ({SUFFIX})"

ROLE_EMPLOYEE_ID = "b1011111-1111-1111-1111-111111111101"
ROLE_AGENT_ID = "b1011111-1111-1111-1111-111111111102"
ROLE_KNOWLEDGE_ID = "b1011111-1111-1111-1111-111111111103"
ROLE_SERVICE_ID = "b1011111-1111-1111-1111-111111111104"

ROOT = Path(os.environ.get("ROOT", "."))
CONFIG_FILE = Path(os.environ.get("CONFIG_FILE", ROOT / "config/azure.local.json"))
STATE_FILE = Path(os.environ.get("STATE_FILE", ROOT / "config/entra.provision.state.json"))

APP_ROLES = [
    {
        "allowedMemberTypes": ["User"],
        "description": "Create tickets, use AI chat (read tools), view own tickets.",
        "displayName": "Support Employee",
        "id": ROLE_EMPLOYEE_ID,
        "isEnabled": True,
        "value": "Support.Employee",
    },
    {
        "allowedMemberTypes": ["User"],
        "description": "Support queue, resolve/reopen tickets, AI suggest.",
        "displayName": "Support Agent",
        "id": ROLE_AGENT_ID,
        "isEnabled": True,
        "value": "Support.Agent",
    },
    {
        "allowedMemberTypes": ["User"],
        "description": "Manage knowledge documents and re-index.",
        "displayName": "Knowledge Admin",
        "id": ROLE_KNOWLEDGE_ID,
        "isEnabled": True,
        "value": "Support.KnowledgeAdmin",
    },
    {
        "allowedMemberTypes": ["Application"],
        "description": "Machine identity for MCP and internal service-to-service API calls.",
        "displayName": "Support Service",
        "id": ROLE_SERVICE_ID,
        "isEnabled": True,
        "value": "Support.Service",
    },
]

OAUTH2_SCOPE = {
    "adminConsentDescription": "Allow Support PoC clients to call APIs on behalf of the signed-in user.",
    "adminConsentDisplayName": "Access Support PoC API as user",
    "id": SCOPE_ID,
    "isEnabled": True,
    "type": "User",
    "userConsentDescription": "Sign in and use the internal support assistant on your behalf.",
    "userConsentDisplayName": "Access Support PoC API",
    "value": "access_as_user",
}


def log(msg: str) -> None:
    print(f"==> {msg}", flush=True)


def run(cmd: list[str], *, check: bool = True) -> subprocess.CompletedProcess:
    try:
        return subprocess.run(cmd, check=check, capture_output=True, text=True)
    except subprocess.CalledProcessError as e:
        err = (e.stderr or e.stdout or "").strip()
        raise SystemExit(f"Command failed ({e.returncode}): {' '.join(cmd[:6])}...\n{err}") from e


def az_cmd(*args: str) -> list[str]:
    return [AZ_CLI, *args]


def az_json(args: list[str]) -> object:
    r = run(az_cmd(*args, "-o", "json"))
    return json.loads(r.stdout) if r.stdout.strip() else None


def az_tsv(args: list[str]) -> str:
    r = run(az_cmd(*args, "-o", "tsv"))
    return r.stdout.strip()


def graph_rest(method: str, url: str, body: dict | None = None) -> None:
    args = az_cmd("rest", "--method", method, "--url", url, "--headers", "Content-Type=application/json")
    if body is None:
        run(args)
        return
    with tempfile.NamedTemporaryFile(
        mode="w", suffix=".json", delete=False, encoding="utf-8"
    ) as tmp:
        json.dump(body, tmp)
        tmp_path = tmp.name
    try:
        body_arg = f"@{tmp_path}" if os.name == "nt" else tmp_path
        run([*args, "--body", body_arg])
    finally:
        try:
            os.unlink(tmp_path)
        except OSError:
            pass


def graph_patch(object_id: str, body: dict) -> None:
    graph_rest("PATCH", f"https://graph.microsoft.com/v1.0/applications/{object_id}", body)


def find_app(display_name: str) -> tuple[str | None, str | None]:
    apps = az_json(["ad", "app", "list", "--display-name", display_name]) or []
    if not apps:
        return None, None
    app = apps[0]
    return app.get("id"), app.get("appId")


def ensure_sp(client_id: str) -> str:
    sp_id = az_tsv(["ad", "sp", "list", "--filter", f"appId eq '{client_id}'", "--query", "[0].id"])
    if sp_id:
        return sp_id
    log(f"Tao service principal cho app {client_id}")
    return az_tsv(["ad", "sp", "create", "--id", client_id, "--query", "id"])


def assign_app_role(api_sp_id: str, principal_id: str, app_role_id: str) -> None:
    existing = az_tsv(
        [
            "rest",
            "--method",
            "GET",
            "--url",
            f"https://graph.microsoft.com/v1.0/servicePrincipals/{api_sp_id}/appRoleAssignedTo",
            "--query",
            f"value[?principalId=='{principal_id}' && appRoleId=='{app_role_id}'].id | [0]",
        ]
    )
    if existing and existing != "null":
        log(f"App role {app_role_id} da gan cho {principal_id}")
        return
    graph_rest(
        "POST",
        f"https://graph.microsoft.com/v1.0/servicePrincipals/{api_sp_id}/appRoleAssignedTo",
        {
            "principalId": principal_id,
            "resourceId": api_sp_id,
            "appRoleId": app_role_id,
        },
    )
    log(f"Da gan app role {app_role_id} -> {principal_id}")


def create_or_get_app(display_name: str, create_args: list[str]) -> tuple[str, str]:
    obj_id, client_id = find_app(display_name)
    if obj_id and client_id:
        log(f"{display_name} da ton tai: {obj_id}")
        return obj_id, client_id
    log(f"Tao application: {display_name}")
    created = az_json(["ad", "app", "create", "--display-name", display_name, *create_args])
    return created["id"], created["appId"]


def set_api_identifier_uri(api_obj_id: str, api_client_id: str) -> tuple[str, str]:
    """Apply tenant-safe Application ID URI and sync module-level audience/scope."""
    global IDENTIFIER_URI, SCOPE_FULL
    uri = f"api://{api_client_id}"
    graph_patch(api_obj_id, {"identifierUris": [uri]})
    IDENTIFIER_URI = uri
    SCOPE_FULL = f"{uri}/access_as_user"
    log(f"API identifier URI: {uri}")
    return uri, SCOPE_FULL


def main() -> int:
    # --- API ---
    log(f"API application: {API_NAME}")
    api_obj_id, api_client_id = create_or_get_app(
        API_NAME,
        ["--sign-in-audience", "AzureADMyOrg"],
    )
    set_api_identifier_uri(api_obj_id, api_client_id)
    assert IDENTIFIER_URI and SCOPE_FULL
    graph_patch(
        api_obj_id,
        {
            "appRoles": APP_ROLES,
            "api": {
                "requestedAccessTokenVersion": 2,
                "oauth2PermissionScopes": [OAUTH2_SCOPE],
            },
        },
    )
    api_sp_id = ensure_sp(api_client_id)

    # --- SPA ---
    log(f"SPA application: {SPA_NAME}")
    spa_obj_id, spa_client_id = create_or_get_app(
        SPA_NAME,
        ["--sign-in-audience", "AzureADMyOrg"],
    )
    graph_patch(spa_obj_id, {"spa": {"redirectUris": SPA_REDIRECT_URIS}})
    graph_patch(
        spa_obj_id,
        {
            "requiredResourceAccess": [
                {
                    "resourceAppId": api_client_id,
                    "resourceAccess": [{"id": SCOPE_ID, "type": "Scope"}],
                }
            ]
        },
    )
    ensure_sp(spa_client_id)

    log("Pre-authorize SPA tren API")
    graph_patch(
        api_obj_id,
        {
            "appRoles": APP_ROLES,
            "api": {
                "requestedAccessTokenVersion": 2,
                "oauth2PermissionScopes": [OAUTH2_SCOPE],
                "preAuthorizedApplications": [
                    {"appId": spa_client_id, "delegatedPermissionIds": [SCOPE_ID]}
                ],
            },
        },
    )

    # --- MCP ---
    log(f"MCP Service application: {MCP_NAME}")
    mcp_obj_id, mcp_client_id = create_or_get_app(
        MCP_NAME,
        ["--sign-in-audience", "AzureADMyOrg"],
    )
    graph_patch(
        mcp_obj_id,
        {
            "requiredResourceAccess": [
                {
                    "resourceAppId": api_client_id,
                    "resourceAccess": [{"id": ROLE_SERVICE_ID, "type": "Role"}],
                }
            ]
        },
    )
    mcp_sp_id = ensure_sp(mcp_client_id)

    log("Admin consent cho MCP application permission")
    r = run(az_cmd("ad", "app", "permission", "admin-consent", "--id", mcp_client_id), check=False)
    if r.returncode != 0:
        log(f"admin-consent warning: {r.stderr.strip() or r.stdout}")

    log("Gan app role Support.Service cho MCP SP")
    assign_app_role(api_sp_id, mcp_sp_id, ROLE_SERVICE_ID)

    for role_name, principal_id in BOOTSTRAP_ROLE_ASSIGNMENTS.items():
        role_id = {
            "Support.Employee": ROLE_EMPLOYEE_ID,
            "Support.Agent": ROLE_AGENT_ID,
            "Support.KnowledgeAdmin": ROLE_KNOWLEDGE_ID,
        }[role_name]
        log(f"Bootstrap user role {role_name} -> {principal_id}")
        assign_app_role(api_sp_id, principal_id, role_id)

    log("Tao client secret cho MCP (1 nam)")
    secret_resp = az_json(
        ["ad", "app", "credential", "reset", "--id", mcp_obj_id, "--display-name", f"azcli-mcp-{SUFFIX}", "--years", "1"]
    )
    mcp_secret = secret_resp["password"]

    authority = f"https://login.microsoftonline.com/{TENANT_ID}"
    entra = {
        "tenantId": TENANT_ID,
        "tenantDomain": TENANT_DOMAIN,
        "authority": authority,
        "api": {
            "displayName": API_NAME,
            "clientId": api_client_id,
            "applicationId": api_client_id,
            "audience": IDENTIFIER_URI or f"api://{api_client_id}",
            "identifierUri": IDENTIFIER_URI or f"api://{api_client_id}",
            "scopeName": "access_as_user",
            "scopeFull": SCOPE_FULL or f"api://{api_client_id}/access_as_user",
        },
        "spa": {
            "displayName": SPA_NAME,
            "clientId": spa_client_id,
            "redirectUris": SPA_REDIRECT_URIS,
        },
        "mcpService": {
            "displayName": MCP_NAME,
            "clientId": mcp_client_id,
            "clientSecret": mcp_secret,
        },
        "appRoles": {
            "Support.Employee": {
                "id": ROLE_EMPLOYEE_ID,
                "displayName": "Support Employee",
                "memberTypes": ["User"],
            },
            "Support.Agent": {
                "id": ROLE_AGENT_ID,
                "displayName": "Support Agent",
                "memberTypes": ["User"],
            },
            "Support.KnowledgeAdmin": {
                "id": ROLE_KNOWLEDGE_ID,
                "displayName": "Knowledge Admin",
                "memberTypes": ["User"],
            },
            "Support.Service": {
                "id": ROLE_SERVICE_ID,
                "displayName": "Support Service",
                "memberTypes": ["Application"],
            },
        },
        "bootstrapRoleAssignments": BOOTSTRAP_ROLE_ASSIGNMENTS,
        "bootstrapPrincipals": {
            "employee": BOOTSTRAP_EMPLOYEE_ID,
            "agent": BOOTSTRAP_AGENT_ID,
            "knowledgeAdmin": BOOTSTRAP_KNOWLEDGE_ADMIN_ID,
        },
    }

    CONFIG_FILE.parent.mkdir(parents=True, exist_ok=True)
    if CONFIG_FILE.exists():
        cfg = json.loads(CONFIG_FILE.read_text(encoding="utf-8"))
        cfg["entra"] = entra
        CONFIG_FILE.write_text(json.dumps(cfg, indent=2) + "\n", encoding="utf-8")
        log(f"Da merge entra vao {CONFIG_FILE}")
    else:
        CONFIG_FILE.write_text(json.dumps({"entra": entra}, indent=2) + "\n", encoding="utf-8")
        try:
            os.chmod(CONFIG_FILE, 0o600)
        except OSError:
            pass
        log(f"Da tao {CONFIG_FILE} (chi co entra)")

    STATE_FILE.write_text(json.dumps(entra, indent=2) + "\n", encoding="utf-8")
    try:
        os.chmod(STATE_FILE, 0o600)
    except OSError:
        pass

    log("Hoan tat Entra provision (az cli).")
    print()
    print(f"  tenantId:     {TENANT_ID}")
    print(f"  api client:   {api_client_id}")
    print(f"  audience:     {IDENTIFIER_URI}")
    print(f"  scope:        {SCOPE_FULL}")
    print(f"  spa client:   {spa_client_id}")
    print(f"  mcp client:   {mcp_client_id}")
    print(f"  config:       {CONFIG_FILE}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
