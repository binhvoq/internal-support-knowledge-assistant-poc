# CI/CD Governance

This repo now uses four clear layers:

- `CI`: branch-based validation on pushes and pull requests.
- `Infra`: manual Terraform bootstrap for environment/state setup.
- `Deploy Nonprod`: automatic app deploys on push to `dev` and `test`, gated by the matching GitHub Environment.
- `CD`: automatic app deploys on push to `main`, with `prod` protection.

## What lives where

- Source code and Dockerfiles live in the repo.
- Terraform lives in `infra/terraform`.
- Runtime deploy logic lives in `scripts/deploy_app.py`.
- Bootstrap remains in `.github/workflows/infra.yml`.
- Nonprod deploy is in `.github/workflows/deploy-nonprod.yml`.
- Prod deploy is in `.github/workflows/cd.yml`.

## Execution model

- App deploy workflows only build the images that changed.
- App deploy workflows only apply the matching `azurerm_container_app` targets.
- Unchanged container app images are reused from the current running environment.
- Infra bootstrap stays separate and manual.

## Environment model

- `dev`, `test`, and `prod` are all protected environments.
- Branch protection still guards source changes.

## Why this is better

- Small UI or service changes no longer rebuild every image.
- App deploys no longer run a full Terraform apply when only one service changed.
- Dev/test/prod all go through the intended protection gate.
- Infra bootstrap stays a separate explicit action instead of being mixed into every deploy.

## Operational rule

- Use CI to validate code.
- Use `Infra` only when you need to create or re-bootstrap state/resources.
- Use `Deploy Nonprod` for day-to-day `dev` and `test` deploys.
- Use `CD` for `main` and production promotion.
