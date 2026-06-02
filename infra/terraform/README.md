# Terraform

Terraform is the preferred path for Azure **and Microsoft Entra ID** resources in this PoC. Legacy scripts in `../../scripts` remain for refresh/sync until fully migrated.

**Identity roadmap:** [`../../docs/zero-trust-identity.md`](../../docs/zero-trust-identity.md)

## What It Creates

### Azure workload (`main.tf`)

- Resource Group
- Storage Account + `knowledge-docs` container
- Service Bus namespace + `support-events` topic + `ai-orchestrator` subscription
- Azure AI Search
- Azure OpenAI embedding + chat deployments
- Log Analytics + Application Insights

### Microsoft Entra ID (`identity.tf`) — when `enable_entra_identity = true` (default)

- **API app** — resource server, app roles, scope `access_as_user`
- **SPA app** — MSAL PKCE redirect URIs (default `http://localhost:5173/`)
- **MCP Service app** — client secret + app role `Support.Service`
- Pre-authorized SPA on API (reduced consent friction for PoC)
- Bootstrap app role assignments (`bootstrap_role_assignments`)

Writes merged config: `../../config/azure.local.json` (includes `entra` section when enabled).

## Prerequisites

- [Terraform](https://www.terraform.io/downloads) >= 1.5
- [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli) logged in: `az login`
- Entra permissions: **Application Administrator** or **Global Administrator** (required to create app registrations)

## Start

```bash
cd infra/terraform
terraform init
terraform plan
terraform apply
```

After apply:

```bash
cd ../..
bash scripts/sync-config.sh
bash scripts/restart-services.sh
# Entra login in app — after backend/frontend JWT work (see docs/zero-trust-identity.md)
bash scripts/smoke-test.sh
```

`terraform apply` writes `config/azure.local.json` by default (`write_local_config = true`). Set `write_local_config = false` to skip the local file.

### Useful outputs

```bash
terraform output entra_tenant_id
terraform output entra_spa_client_id
terraform output entra_api_audience
terraform output entra_scope_access_as_user
terraform output entra_app_roles
```

## Variables (Entra)

| Variable | Default | Purpose |
|----------|---------|---------|
| `enable_entra_identity` | `true` | Toggle Entra resources |
| `tenant_id` | (your tenant) | Entra tenant GUID |
| `spa_redirect_uris` | `http://localhost:5173/` | React MSAL redirects |
| `bootstrap_role_assignments` | demo user → 3 roles | Portal role assignments for PoC |
| `entra_client_secret_days` | `365` | MCP secret lifetime |

Apply without Entra (Azure only):

```bash
terraform apply -var="enable_entra_identity=false"
```

## Stop

```bash
cd infra/terraform
terraform destroy
```

**Warning:** `destroy` removes Entra app registrations created by Terraform. Azure Search / Service Bus incur cost until destroyed.

## Naming

Defaults: `rg-support-poc-tf`, suffix `tf01`. Change `suffix` if global names collide.

## App roles (demo)

See role matrix in [`../../docs/zero-trust-identity.md`](../../docs/zero-trust-identity.md#3-app-roles-terraform-tạo-ra-ma-trận-demo).
