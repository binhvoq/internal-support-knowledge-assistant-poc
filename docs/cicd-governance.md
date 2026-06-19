# CI/CD Governance

This repo uses **one human approval gate for code** (PR review) and **a separate gate only for manual infrastructure pipelines** (Infra create, Destroy).

- **Privileged maintainer** (`binhvoq`, Admin): merge own PRs via admin bypass; start Infra/Destroy; approve environment gates on those manual pipelines.
- **Contributors** (Write): cannot self-approve PRs; cannot merge without maintainer review; cannot start Infra/Destroy.

Machine-readable policy: `config/cicd-governance.json`.

## Gate model

| Path | Gate 1 — PR review | Gate 2 — CI | Gate 3 — Environment |
|------|--------------------|-------------|----------------------|
| PR → merge → **CD** (push) | Yes for contributors; admin bypass for maintainer | Yes, must pass | **No** — deploy runs after merge |
| Manual **Infra** (create/bootstrap) | No | No | **Yes** |
| Manual **Destroy** | No | No | **Yes** |

PR approval is the only human gate on the normal code/deploy path. Once a PR is approved and merged, CD deploys automatically with no second approval step.

Manual Infra/Destroy still require privileged actor + environment approval before touching Azure.

## Workflow layers

- `CI` — validation on pushes and pull requests (`ci.yml`), no environment gate.
- `Infra` — manual Terraform bootstrap (`infra.yml`), privileged actor + environment gate.
- `Destroy` — manual Terraform destroy (`destroy.yml`), privileged actor + environment gate.
- `CD Dev` / `CD Test` / `CD Prod` — app deploy on push to `dev` / `test` / `main`, **no environment gate**.
- `Verify Governance` — manual GitHub settings check (`verify-governance.yml`).

## GitHub settings

### Repository roles

| User | Role |
|------|------|
| `binhvoq` | **Admin** (branch admin bypass enabled) |
| Other devs | **Write** only |

### Branch protection — `dev`, `test`, `main`

- Require pull request before merging
- Required approving reviews: **1**
- Require review from Code Owners
- Require status checks: **CI**
- **Allow administrators to bypass required pull requests** — **ON**
- Do **not** enforce rules on administrators

### Environment protection — `dev`, `test`, `prod`

Used **only** by manual `Infra` and `Destroy` workflows:

- Required reviewers: **`binhvoq` only**
- **Prevent self-review**: **OFF**
- CD workflows do **not** reference these environments anymore

## Expected behaviour

### Maintainer

| Action | Result |
|--------|--------|
| Own PR | Merge with admin bypass after CI |
| Contributor PR approved + merged | CD runs immediately, no extra gate |
| Run Infra / Destroy | Start workflow → approve environment gate → runs |

### Contributors

| Action | Result |
|--------|--------|
| Open PR | Needs `@binhvoq` review; cannot self-approve |
| Merge PR | Blocked until maintainer approves |
| Run Infra / Destroy | Blocked at workflow start |
| After their PR is merged | CD deploys automatically (PR review was the control point) |

## Verify

```bash
gh auth login
python scripts/check_cicd_governance.py apply-github
python scripts/check_cicd_governance.py verify-github
```

## Operational rules

- **CI** validates every PR.
- **Infra** only for create/re-bootstrap.
- **Destroy** only when intentionally tearing down an environment.
- **CD** promotes app changes on `dev`, `test`, and `main` after merge.
