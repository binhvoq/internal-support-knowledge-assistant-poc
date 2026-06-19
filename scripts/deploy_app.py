#!/usr/bin/env python3
from __future__ import annotations

import json
import os
import subprocess
from pathlib import Path


REPOS = ("frontend", "gateway", "ticket-service", "knowledge-service", "ai-orchestrator")

SERVICE_RULES = {
    "frontend": {
        "container_app": "frontend",
        "resource": "azurerm_container_app.frontend",
        "patterns": (
            "frontend/",
            "docker/Dockerfile.frontend",
            "docker/frontend-entrypoint.sh",
            "docker/nginx.frontend.conf",
        ),
        "affects_all_backend": False,
    },
    "gateway": {
        "container_app": "gateway",
        "resource": "azurerm_container_app.gateway",
        "patterns": (
            "src/Gateway/",
            "docker/Dockerfile.gateway",
        ),
        "affects_all_backend": False,
    },
    "ticket-service": {
        "container_app": "ticket-service",
        "resource": "azurerm_container_app.ticket_service",
        "patterns": (
            "src/TicketService/",
            "docker/Dockerfile.ticket-service",
        ),
        "affects_all_backend": False,
    },
    "knowledge-service": {
        "container_app": "knowledge-service",
        "resource": "azurerm_container_app.knowledge_service",
        "patterns": (
            "src/KnowledgeService/",
            "docker/Dockerfile.knowledge-service",
        ),
        "affects_all_backend": False,
    },
    "ai-orchestrator": {
        "container_app": "ai-orchestrator",
        "resource": "azurerm_container_app.ai_orchestrator",
        "patterns": (
            "src/AiOrchestrator/",
            "docker/Dockerfile.ai-orchestrator",
        ),
        "affects_all_backend": False,
    },
}

BACKEND_WIDE_PATTERNS = (
    "src/Shared/",
    "src/*.sln",
    "Directory.Build.",
    "global.json",
    "NuGet.config",
    "NuGet.Config",
)

COMMON_IMPORTS = (
    ("azurerm_resource_group.main", lambda e: f"/subscriptions/{e['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{e['RESOURCE_GROUP_NAME']}"),
    ("azurerm_container_registry.main", lambda e: f"/subscriptions/{e['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{e['RESOURCE_GROUP_NAME']}/providers/Microsoft.ContainerRegistry/registries/{e['ACR_NAME']}"),
    ("azurerm_container_app_environment.main", lambda e: f"/subscriptions/{e['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{e['RESOURCE_GROUP_NAME']}/providers/Microsoft.App/managedEnvironments/support-cae-{e['TARGET_ENV']}01"),
    ("azurerm_user_assigned_identity.containerapps", lambda e: f"/subscriptions/{e['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{e['RESOURCE_GROUP_NAME']}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/support-ca-id-{e['TARGET_ENV']}01"),
    ("azurerm_role_assignment.containerapps_acr_pull", lambda e: f"/subscriptions/{e['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{e['RESOURCE_GROUP_NAME']}/providers/Microsoft.ContainerRegistry/registries/{e['ACR_NAME']}", "AcrPull"),
    ("azurerm_role_assignment.containerapps_sql", lambda e: f"/subscriptions/{e['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{e['RESOURCE_GROUP_NAME']}/providers/Microsoft.Sql/servers/support-sql-{e['TARGET_ENV']}01", "Contributor"),
)


def run(
    cmd: list[str],
    cwd: Path | None = None,
    check: bool = True,
    capture: bool = False,
) -> subprocess.CompletedProcess[str]:
    kwargs = {"cwd": cwd, "text": True, "check": check}
    if capture:
        kwargs["capture_output"] = True
    return subprocess.run(cmd, **kwargs)


def git_changed_files(base_sha: str, head_sha: str) -> list[str]:
    result = run(["git", "diff", "--name-only", base_sha, head_sha], check=False, capture=True)
    if result.returncode != 0:
        # Fallback for shallow clones or missing base commit.
        result = run(["git", "diff-tree", "--no-commit-id", "--name-only", "-r", head_sha], check=True, capture=True)
    return [line.strip() for line in result.stdout.splitlines() if line.strip()]


def match_any(path: str, prefixes: tuple[str, ...]) -> bool:
    return any(path.startswith(prefix) or path == prefix for prefix in prefixes)


def detect_repos(changed_files: list[str]) -> set[str]:
    repos: set[str] = set()
    backend_wide = False

    for path in changed_files:
        for repo, rule in SERVICE_RULES.items():
            if match_any(path, rule["patterns"]):
                repos.add(repo)
                break
        else:
            if (
                path.startswith("src/")
                or path.endswith(".sln")
                or path.startswith("Directory.Build.")
                or path in {"global.json", "NuGet.config", "NuGet.Config"}
            ):
                backend_wide = True

    if backend_wide:
        repos.update({"gateway", "ticket-service", "knowledge-service", "ai-orchestrator"})

    return repos


def terraform_state_list(tf_root: Path) -> set[str]:
    result = run(["terraform", "state", "list"], cwd=tf_root, check=False, capture=True)
    if result.returncode != 0:
        return set()
    return {line.strip() for line in result.stdout.splitlines() if line.strip()}


def import_if_needed(tf_root: Path, tfvars_file: str, address: str, resource_id: str) -> None:
    if address in terraform_state_list(tf_root):
        return
    probe = run(["az", "resource", "show", "--ids", resource_id], check=False, capture=True)
    if probe.returncode == 0:
        run(["terraform", "import", "-var-file", tfvars_file, address, resource_id], cwd=tf_root, check=False)


def import_role_assignment_if_needed(tf_root: Path, tfvars_file: str, address: str, scope: str, role_name: str, assignee: str) -> None:
    if address in terraform_state_list(tf_root):
        return
    probe = run([
        "az", "role", "assignment", "list",
        "--assignee", assignee,
        "--scope", scope,
        "--role", role_name,
        "--query", "[0].id",
        "-o", "tsv",
    ], check=False, capture=True)
    assignment_id = probe.stdout.strip()
    if assignment_id:
        run(["terraform", "import", "-var-file", tfvars_file, address, assignment_id], cwd=tf_root, check=False)


def require_resource(resource_id: str, label: str) -> None:
    probe = run(["az", "resource", "show", "--ids", resource_id], check=False, capture=True)
    if probe.returncode != 0:
        raise SystemExit(f"Required infrastructure missing: {label}. Run infra.yml first.")


def require_resource_group(resource_group_name: str) -> None:
    probe = run(["az", "group", "show", "--name", resource_group_name], check=False, capture=True)
    if probe.returncode != 0:
        raise SystemExit(f"Required infrastructure missing: resource group {resource_group_name}. Run infra.yml first.")


def require_role_assignment(scope: str, role_name: str, assignee: str, label: str) -> None:
    probe = run([
        "az", "role", "assignment", "list",
        "--assignee", assignee,
        "--scope", scope,
        "--role", role_name,
        "--query", "[0].id",
        "-o", "tsv",
    ], check=False, capture=True)
    if not probe.stdout.strip():
        raise SystemExit(f"Required infrastructure missing: {label}. Run infra.yml first.")


def terraform_init(tf_root: Path, env: dict[str, str]) -> None:
    run([
        "terraform", "init", "-reconfigure",
        "-backend-config", f"storage_account_name={env['TFSTATE_STORAGE']}",
        "-backend-config", f"container_name={env['TFSTATE_CONTAINER']}",
        "-backend-config", f"key={env['BACKEND_KEY']}",
        "-backend-config", "use_azuread_auth=true",
    ], cwd=tf_root, check=True)


def repo_to_container_app_name(repo: str, env_suffix: str) -> str:
    if repo == "frontend":
        return f"support-web-{env_suffix}"
    if repo == "gateway":
        return f"support-gateway-{env_suffix}"
    if repo == "ticket-service":
        return f"support-ticket-{env_suffix}"
    if repo == "knowledge-service":
        return f"support-knowledge-{env_suffix}"
    if repo == "ai-orchestrator":
        return f"support-ai-{env_suffix}"
    raise KeyError(repo)


def build_image(acr_name: str, repo: str, image_tag: str) -> str:
    dockerfile = f"docker/Dockerfile.{repo}"
    if repo == "ticket-service":
        dockerfile = "docker/Dockerfile.ticket-service"
    elif repo == "knowledge-service":
        dockerfile = "docker/Dockerfile.knowledge-service"
    elif repo == "ai-orchestrator":
        dockerfile = "docker/Dockerfile.ai-orchestrator"
    elif repo == "gateway":
        dockerfile = "docker/Dockerfile.gateway"
    elif repo == "frontend":
        dockerfile = "docker/Dockerfile.frontend"

    print(f"Building {repo}:{image_tag}")
    run([
        "az", "acr", "build",
        "--registry", acr_name,
        "--image", f"{repo}:{image_tag}",
        "--file", dockerfile,
        ".",
    ], check=True)

    manifest = run([
        "az", "acr", "repository", "show-manifests",
        "--name", acr_name,
        "--repository", repo,
        "--query", f"[?tags && contains(tags, '{image_tag}')].digest | [0]",
        "-o", "tsv",
    ], check=True, capture=True)
    digest = manifest.stdout.strip()
    if not digest:
        raise RuntimeError(f"missing digest for {repo}:{image_tag}")
    return digest


def current_digest(resource_group_name: str, repo: str, env_suffix: str) -> str:
    app_name = repo_to_container_app_name(repo, env_suffix)
    result = run([
        "az", "containerapp", "show",
        "-g", resource_group_name,
        "-n", app_name,
        "--query", "properties.template.containers[0].image",
        "-o", "tsv",
    ], check=True, capture=True)
    image_ref = result.stdout.strip()
    if "@" not in image_ref:
        raise RuntimeError(f"Unexpected image reference for {app_name}: {image_ref}")
    return image_ref.split("@", 1)[1]


def main() -> int:
    env = {key: os.environ.get(key, "") for key in [
        "BASE_SHA",
        "HEAD_SHA",
        "TARGET_ENV",
        "RESOURCE_GROUP_NAME",
        "TFVARS_FILE",
        "TF_ROOT",
        "TFSTATE_STORAGE",
        "TFSTATE_CONTAINER",
        "BACKEND_KEY",
        "ACR_NAME",
        "AZURE_CLIENT_ID",
        "AZURE_SUBSCRIPTION_ID",
    ]}
    missing = [k for k, v in env.items() if not v]
    if missing:
        raise SystemExit(f"Missing env vars: {', '.join(missing)}")

    tf_root = Path(env["TF_ROOT"])
    env_suffix = f"{env['TARGET_ENV']}01"
    terraform_init(tf_root, env)
    changed_files = git_changed_files(env["BASE_SHA"], env["HEAD_SHA"])
    repos = detect_repos(changed_files)

    if not repos:
        print("No app changes detected; skipping deploy.")
        return 0

    # Ensure the current infra state is known for app-only deploys.
    identity_principal_id = run([
        "az", "identity", "show",
        "--name", f"support-ca-id-{env_suffix}",
        "--resource-group", env["RESOURCE_GROUP_NAME"],
        "--query", "principalId",
        "-o", "tsv",
    ], check=True, capture=True).stdout.strip()

    require_resource_group(env["RESOURCE_GROUP_NAME"])

    required_resources = [
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.ContainerRegistry/registries/{env['ACR_NAME']}",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.App/managedEnvironments/support-cae-{env_suffix}",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/support-ca-id-{env_suffix}",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Storage/storageAccounts/supportstore{env_suffix}",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.ServiceBus/namespaces/support-bus-{env_suffix}",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Search/searchServices/support-search-{env_suffix}",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.OperationalInsights/workspaces/support-logs-{env_suffix}",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.OperationalInsights/workspaces/support-ca-logs-{env_suffix}",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Insights/components/support-insights-{env_suffix}",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.CognitiveServices/accounts/support-oai-chat-{env_suffix}",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.CognitiveServices/accounts/support-oai-embed-{env_suffix}",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.CognitiveServices/accounts/support-oai-chat-{env_suffix}/deployments/gpt-4.1-mini",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.CognitiveServices/accounts/support-oai-embed-{env_suffix}/deployments/text-embedding-3-small",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Sql/servers/support-sql-{env_suffix}",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Sql/servers/support-sql-{env_suffix}/firewallRules/AllowAzureServices",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Sql/servers/support-sql-{env_suffix}/databases/supportpoc_tickets",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Sql/servers/support-sql-{env_suffix}/databases/supportpoc_knowledge",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Sql/servers/support-sql-{env_suffix}/databases/supportpoc_orchestrator",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.App/containerApps/support-web-{env_suffix}",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.App/containerApps/support-gateway-{env_suffix}",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.App/containerApps/support-ticket-{env_suffix}",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.App/containerApps/support-knowledge-{env_suffix}",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.App/containerApps/support-ai-{env_suffix}",
    ]
    for resource_id in required_resources:
        require_resource(resource_id, resource_id.rsplit("/", 1)[-1])

    require_role_assignment(
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.ContainerRegistry/registries/{env['ACR_NAME']}",
        "AcrPull",
        identity_principal_id,
        "Container apps ACR pull role assignment",
    )
    require_role_assignment(
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Sql/servers/support-sql-{env_suffix}",
        "Contributor",
        identity_principal_id,
        "Container apps SQL role assignment",
    )
    require_role_assignment(
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.ContainerRegistry/registries/{env['ACR_NAME']}",
        "AcrPush",
        env["AZURE_CLIENT_ID"],
        "CD pipeline ACR push role assignment",
    )

    import_if_needed(tf_root, env["TFVARS_FILE"], "azurerm_resource_group.main", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}")
    import_if_needed(tf_root, env["TFVARS_FILE"], "azurerm_container_registry.main", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.ContainerRegistry/registries/{env['ACR_NAME']}")
    import_if_needed(tf_root, env["TFVARS_FILE"], "azurerm_container_app_environment.main", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.App/managedEnvironments/support-cae-{env_suffix}")
    import_if_needed(tf_root, env["TFVARS_FILE"], "azurerm_user_assigned_identity.containerapps", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/support-ca-id-{env_suffix}")
    import_role_assignment_if_needed(
        tf_root,
        env["TFVARS_FILE"],
        "azurerm_role_assignment.containerapps_acr_pull",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.ContainerRegistry/registries/{env['ACR_NAME']}",
        "AcrPull",
        identity_principal_id,
    )
    import_role_assignment_if_needed(
        tf_root,
        env["TFVARS_FILE"],
        "azurerm_role_assignment.containerapps_sql",
        f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Sql/servers/support-sql-{env_suffix}",
        "Contributor",
        identity_principal_id,
    )

    foundation_imports = [
        ("azurerm_storage_account.docs", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Storage/storageAccounts/supportstore{env_suffix}"),
        ("azurerm_servicebus_namespace.bus", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.ServiceBus/namespaces/support-bus-{env_suffix}"),
        ("azurerm_servicebus_namespace_authorization_rule.app", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.ServiceBus/namespaces/support-bus-{env_suffix}/authorizationRules/support-app"),
        ("azurerm_servicebus_topic.events", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.ServiceBus/namespaces/support-bus-{env_suffix}/topics/support-events"),
        ("azurerm_servicebus_subscription.ai_orchestrator", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.ServiceBus/namespaces/support-bus-{env_suffix}/topics/support-events/subscriptions/ai-orchestrator"),
        ("azurerm_search_service.knowledge", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Search/searchServices/support-search-{env_suffix}"),
        ("azurerm_log_analytics_workspace.main", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.OperationalInsights/workspaces/support-logs-{env_suffix}"),
        ("azurerm_log_analytics_workspace.containerapps", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.OperationalInsights/workspaces/support-ca-logs-{env_suffix}"),
        ("azurerm_application_insights.main", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Insights/components/support-insights-{env_suffix}"),
        ("azurerm_cognitive_account.openai_chat", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.CognitiveServices/accounts/support-oai-chat-{env_suffix}"),
        ("azurerm_cognitive_account.openai_embed", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.CognitiveServices/accounts/support-oai-embed-{env_suffix}"),
        ("azurerm_cognitive_deployment.chat", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.CognitiveServices/accounts/support-oai-chat-{env_suffix}/deployments/gpt-4.1-mini"),
        ("azurerm_cognitive_deployment.embedding", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.CognitiveServices/accounts/support-oai-embed-{env_suffix}/deployments/text-embedding-3-small"),
        ("azurerm_container_app_environment.main", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.App/managedEnvironments/support-cae-{env_suffix}"),
        ("azurerm_storage_container.knowledge_docs", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Storage/storageAccounts/supportstore{env_suffix}/blobServices/default/containers/knowledge-docs"),
        ("azurerm_mssql_server.main", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Sql/servers/support-sql-{env_suffix}"),
        ("azurerm_mssql_firewall_rule.allow_azure", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Sql/servers/support-sql-{env_suffix}/firewallRules/AllowAzureServices"),
        ("azurerm_mssql_database.tickets", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Sql/servers/support-sql-{env_suffix}/databases/supportpoc_tickets"),
        ("azurerm_mssql_database.knowledge", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Sql/servers/support-sql-{env_suffix}/databases/supportpoc_knowledge"),
        ("azurerm_mssql_database.orchestrator", f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.Sql/servers/support-sql-{env_suffix}/databases/supportpoc_orchestrator"),
    ]
    for address, resource_id in foundation_imports:
        import_if_needed(tf_root, env["TFVARS_FILE"], address, resource_id)

    for repo in REPOS:
        import_if_needed(
            tf_root,
            env["TFVARS_FILE"],
            SERVICE_RULES[repo]["resource"],
            f"/subscriptions/{env['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{env['RESOURCE_GROUP_NAME']}/providers/Microsoft.App/containerApps/{repo_to_container_app_name(repo, env_suffix)}",
        )

    build_digests: dict[str, str] = {}
    for repo in repos:
        build_digests[repo] = build_image(env["ACR_NAME"], repo, env["HEAD_SHA"][:7])

    for repo in REPOS:
        if repo not in build_digests:
            build_digests[repo] = current_digest(env["RESOURCE_GROUP_NAME"], repo, env_suffix)

    payload = {f"{repo.replace('-', '_')}_image_digest": digest for repo, digest in build_digests.items()}
    out_path = tf_root / "generated.auto.tfvars.json"
    out_path.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    print(f"Wrote {out_path}")

    target_resources = [SERVICE_RULES[repo]["resource"] for repo in repos]
    cmd = [
        "terraform", "apply",
        "-auto-approve",
        "-var-file", env["TFVARS_FILE"],
        "-var-file", "generated.auto.tfvars.json",
    ]
    for target in target_resources:
        cmd.extend(["-target", target])

    print(f"Applying targets: {', '.join(target_resources)}")
    run(cmd, cwd=tf_root, check=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
