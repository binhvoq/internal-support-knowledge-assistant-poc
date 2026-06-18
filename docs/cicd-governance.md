# CI/CD Governance

This repo splits responsibilities into three layers:

- `CI`: branch-based validation for pull requests and pushes to `dev`, `test`, and `main`.
- `Infra create`: manual bootstrap workflow that is gated by a GitHub Environment.
- `CD`: automatic deploy workflow on push to `dev`, `test`, and `main`.

## Current policy

- Protect `dev`, `test`, and `main` with branch protection.
- Keep `.github/workflows/**` under review control.
- Use one infra bootstrap workflow with `workflow_dispatch`.
- Pass the target GitHub Environment as the workflow input for infra create.
- Bind deploy jobs to `environment: ${{ github.ref_name }}` for push-based CD so GitHub can enforce reviewers and environment-scoped secrets.

## Environment model

Use three GitHub Environments:

- `dev`
- `test`
- `prod`

Recommended settings:

- `dev`: no approval or a light reviewer gate.
- `test`: reviewer gate if the team wants an extra checkpoint.
- `prod`: required reviewers and deployment branch policy.

## Why this works

- A manual bootstrap workflow can still be protected.
- Push-based CD can still stop at the environment gate before deployment starts.
- The deploy job will not get environment secrets or continue until the environment is approved.
- CI remains branch-driven, so changes still go through PR review and branch protection.

## Operational rule

- CI validates code on protected branches.
- CD deploys code automatically on pushes to the protected branches.
- Infra create is only allowed through the environment gate.
- Prod changes should never depend on a free-form branch name check inside shell scripts.
