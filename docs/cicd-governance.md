# CI/CD Governance

This repo splits responsibilities into three layers:

- `CI`: branch-based validation for pull requests and pushes to `dev`, `test`, and `main`.
- `Infra create`: manual deploy workflow that is gated by a GitHub Environment.
- `CD`: application deploys that should also use environment protection for sensitive targets.

## Current policy

- Protect `dev`, `test`, and `main` with branch protection.
- Keep `.github/workflows/**` under review control.
- Use one infra workflow with `workflow_dispatch`.
- Pass the target GitHub Environment as the workflow input.
- Bind the deploy job to `environment: ${{ inputs.environment }}` so GitHub can enforce reviewers and environment-scoped secrets.

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

- A manual workflow can still be protected.
- The run can be created by anyone with repo permission, but the job will stop at the environment gate.
- The deploy job will not get environment secrets or continue until the environment is approved.
- CI remains branch-driven, so changes still go through PR review and branch protection.

## Operational rule

- CI validates code on protected branches.
- Infra create is only allowed through the environment gate.
- Prod changes should never depend on a free-form branch name check inside shell scripts.
