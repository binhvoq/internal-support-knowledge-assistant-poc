#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
CONFIG_PATH = ROOT / "config" / "cicd-governance.json"


def load_config() -> dict[str, Any]:
    if not CONFIG_PATH.is_file():
        raise SystemExit(f"missing governance config: {CONFIG_PATH}")
    return json.loads(CONFIG_PATH.read_text(encoding="utf-8"))


def privileged_logins(config: dict[str, Any]) -> set[str]:
    values = config.get("privileged_github_logins") or []
    if not values:
        raise SystemExit("privileged_github_logins must not be empty")
    return {str(item).lower() for item in values}


def authorize_pipeline(actor: str, config: dict[str, Any]) -> None:
    allowed = privileged_logins(config)
    current = actor.lower()
    if current not in allowed:
        allowed_list = ", ".join(sorted(allowed))
        raise SystemExit(
            "This pipeline can only be started by privileged maintainers "
            f"({allowed_list}). Ask a maintainer to run it or approve the environment gate."
        )


def authorize_infra(actor: str, config: dict[str, Any]) -> None:
    authorize_pipeline(actor, config)


def gh_api(path: str) -> Any:
    command = ["gh", "api", path]
    try:
        result = subprocess.run(command, check=True, capture_output=True, text=True)
    except FileNotFoundError as exc:
        raise SystemExit("gh CLI is required for verify-github (https://cli.github.com/)") from exc
    except subprocess.CalledProcessError as exc:
        detail = (exc.stderr or exc.stdout or "").strip()
        raise SystemExit(f"gh api {path} failed: {detail}") from exc
    return json.loads(result.stdout)


def repo_slug() -> str:
    env_repo = os.environ.get("GITHUB_REPOSITORY", "").strip()
    if env_repo:
        return env_repo
    try:
        result = subprocess.run(
            ["gh", "repo", "view", "--json", "nameWithOwner", "-q", ".nameWithOwner"],
            check=True,
            capture_output=True,
            text=True,
        )
    except (FileNotFoundError, subprocess.CalledProcessError) as exc:
        raise SystemExit("Set GITHUB_REPOSITORY or run inside a gh-authenticated git repo.") from exc
    slug = result.stdout.strip()
    if not slug:
        raise SystemExit("Could not resolve repository slug.")
    return slug


def verify_branch_protection(repo: str, branch: str, config: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    expected = config["branch_protection"]
    try:
        protection = gh_api(f"repos/{repo}/branches/{branch}/protection")
    except SystemExit as exc:
        return [f"[branch:{branch}] no protection rule: {exc}"]

    pr = protection.get("required_pull_request_reviews") or {}
    if not protection.get("required_pull_request_reviews"):
        errors.append(f"[branch:{branch}] missing required pull request reviews")

    if expected["require_pull_request"]:
        if not protection.get("required_pull_request_reviews"):
            errors.append(f"[branch:{branch}] require pull request is not enabled")

    required_count = pr.get("required_approving_review_count")
    if required_count != expected["required_approving_review_count"]:
        errors.append(
            f"[branch:{branch}] required approvals expected "
            f"{expected['required_approving_review_count']}, got {required_count!r}"
        )

    if bool(pr.get("require_code_owner_reviews")) != expected["require_code_owner_reviews"]:
        errors.append(f"[branch:{branch}] require_code_owner_reviews mismatch")

    enforce_admins = protection.get("enforce_admins", {}).get("enabled")
    if expected["allow_admin_bypass"] and enforce_admins:
        errors.append(
            f"[branch:{branch}] enforce_admins is enabled; privileged maintainer needs admin bypass"
        )

    return errors


def verify_environment(repo: str, environment: str, config: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    expected = config["environment_protection"]
    privileged = {login.lower() for login in config["privileged_github_logins"]}

    try:
        payload = gh_api(f"repos/{repo}/environments/{environment}")
    except SystemExit as exc:
        return [f"[env:{environment}] missing or unreadable: {exc}"]

    reviewer_logins: set[str] = set()
    prevent_self_review = False
    for rule in payload.get("protection_rules") or []:
        if rule.get("type") != "required_reviewers":
            continue
        prevent_self_review = bool(rule.get("prevent_self_review") or False)
        for reviewer in rule.get("reviewers") or []:
            login = (reviewer.get("reviewer") or {}).get("login") or ""
            if login:
                reviewer_logins.add(login.lower())

    if expected["required_reviewers_from_privileged_logins"]:
        if not reviewer_logins:
            errors.append(f"[env:{environment}] no required reviewers configured")
        elif not reviewer_logins.issubset(privileged):
            errors.append(
                f"[env:{environment}] reviewers {sorted(reviewer_logins)} must be subset of privileged logins"
            )
        elif reviewer_logins != privileged:
            errors.append(
                f"[env:{environment}] expected reviewers {sorted(privileged)}, got {sorted(reviewer_logins)}"
            )

    if prevent_self_review != expected["prevent_self_review"]:
        errors.append(
            f"[env:{environment}] prevent_self_review expected {expected['prevent_self_review']}, "
            f"got {prevent_self_review}"
        )

    return errors


def apply_branch_protection(repo: str, branch: str, config: dict[str, Any]) -> None:
    expected = config["branch_protection"]
    if expected["allow_admin_bypass"]:
        gh_api_delete(f"repos/{repo}/branches/{branch}/protection/enforce_admins")
        print(f"enabled admin bypass on branch {branch}")
        return

    gh_api_put(
        f"repos/{repo}/branches/{branch}/protection/enforce_admins",
        {"enabled": True},
    )
    print(f"disabled admin bypass on branch {branch}")


def gh_api_delete(path: str) -> None:
    command = ["gh", "api", "-X", "DELETE", path]
    try:
        subprocess.run(command, check=True, capture_output=True, text=True)
    except FileNotFoundError as exc:
        raise SystemExit("gh CLI is required for apply-github (https://cli.github.com/)") from exc
    except subprocess.CalledProcessError as exc:
        detail = (exc.stderr or exc.stdout or "").strip()
        raise SystemExit(f"gh api DELETE {path} failed: {detail}") from exc


def gh_api_put(path: str, payload: dict[str, Any]) -> Any:
    command = ["gh", "api", "-X", "PUT", path, "--input", "-"]
    try:
        result = subprocess.run(
            command,
            check=True,
            capture_output=True,
            text=True,
            input=json.dumps(payload),
        )
    except FileNotFoundError as exc:
        raise SystemExit("gh CLI is required for apply-github (https://cli.github.com/)") from exc
    except subprocess.CalledProcessError as exc:
        detail = (exc.stderr or exc.stdout or "").strip()
        raise SystemExit(f"gh api PUT {path} failed: {detail}") from exc
    if not result.stdout.strip():
        return None
    return json.loads(result.stdout)


def apply_github(config: dict[str, Any]) -> None:
    repo = repo_slug()
    for branch in config["protected_branches"]:
        apply_branch_protection(repo, branch, config)
    print("GitHub branch protection applied. Environment reviewers are managed in GitHub UI/API.")
    print("Run verify-github to confirm.")


def verify_github(config: dict[str, Any]) -> None:
    repo = repo_slug()
    errors: list[str] = []

    for branch in config["protected_branches"]:
        errors.extend(verify_branch_protection(repo, branch, config))

    for environment in config["protected_environments"]:
        errors.extend(verify_environment(repo, environment, config))

    if errors:
        print("GitHub governance verification failed:", file=sys.stderr)
        for item in errors:
            print(f"  - {item}", file=sys.stderr)
        raise SystemExit(1)

    print(f"GitHub governance OK for {repo}")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="CI/CD governance helpers")
    subparsers = parser.add_subparsers(dest="command", required=True)

    pipeline = subparsers.add_parser(
        "authorize-pipeline",
        help="Allow only privileged actors to start gated manual pipelines",
    )
    pipeline.add_argument("--actor", required=True)

    infra = subparsers.add_parser("authorize-infra", help="Alias for authorize-pipeline")
    infra.add_argument("--actor", required=True)

    subparsers.add_parser("verify-github", help="Verify branch/environment protection via gh api")
    subparsers.add_parser("apply-github", help="Apply branch protection settings via gh api")
    return parser


def main() -> None:
    parser = build_parser()
    args = parser.parse_args()
    config = load_config()

    if args.command in {"authorize-pipeline", "authorize-infra"}:
        authorize_pipeline(args.actor, config)
        return

    if args.command == "verify-github":
        verify_github(config)
        return

    if args.command == "apply-github":
        apply_github(config)
        return

    raise SystemExit(f"unsupported command: {args.command}")


if __name__ == "__main__":
    main()
