# Kinh nghiem: provision Entra bang Azure CLI (khong terraform apply)

Tai lieu nay mo ta cach chay **`scripts/provision-entra.sh`** — tuong duong [`infra/terraform/identity.tf`](../infra/terraform/identity.tf) khi Terraform chi la IaC tham chieu.

## Khi nao dung

- Da co `az login` + quyen tao app registration (Global Admin / Application Administrator).
- **Khong** chay `terraform apply` nhung can Entra giong thiet ke TF.
- Muon ghi/merge section `entra` vao `config/azure.local.json`.

## Lenh

```bash
# Tu root repo
bash scripts/provision-entra.sh

# Tuy chon
export BOOTSTRAP_USER_ID="<entra-user-object-id>"
export SPA_REDIRECT="http://localhost:5173/"
bash scripts/provision-entra.sh

bash scripts/sync-config.sh   # appsettings + frontend/.env.local (xem luu y duoi)
```

## Mapping Terraform -> az cli / Graph

| Terraform (`identity.tf`) | az cli / Graph tuong ung |
|---------------------------|---------------------------|
| `azuread_application.api` | `az ad app create` + PATCH `appRoles`, `api.oauth2PermissionScopes` |
| `identifier_uris` static | **Sau create:** PATCH `identifierUris: ["api://{clientId}"]` (tenant policy) |
| `azuread_service_principal.api` | `az ad sp create --id {clientId}` |
| `azuread_application.spa` | `az ad app create` + PATCH `spa.redirectUris` |
| `required_resource_access` (SPA) | PATCH `requiredResourceAccess` |
| `azuread_application_pre_authorized` | PATCH `api.preAuthorizedApplications` |
| `azuread_application.mcp_service` | `az ad app create` + PATCH `requiredResourceAccess` (Role) |
| `azuread_application_password.mcp` | `az ad app credential reset --years 1` |
| `azuread_app_role_assignment` (MCP + bootstrap) | POST `/servicePrincipals/{apiSp}/appRoleAssignedTo` |
| `local_sensitive_file` entra block | Merge vao `config/azure.local.json` |

## Loi da gap va cach xu ly

### 1. `jq` khong co tren Windows

- **Triệu chứng:** script bash ban dau yeu cau `jq`.
- **Cach xu ly:** tach `provision-entra_impl.py` (Python 3) — khong phu thuoc `jq`.

### 2. Python khong tim thay `az`

- **Triệu chứng:** `FileNotFoundError: az` khi goi subprocess tu Python tren Windows.
- **Cach xu ly:** `shutil.which("az.cmd")` — dung duong dan day du `Azure CLI2\wbin\az.cmd`.

### 3. Identifier URI `api://supportpoc-tf01-supportpoc` bi tu choi

- **Triệu chứng:** `Failed to add identifier URI ... must contain a tenant verified domain, tenant ID, or app ID`.
- **Nguyen nhan:** tenant policy Entra (khac voi URI static trong TF).
- **Cach xu ly:** tao app **khong** `--identifier-uris`, sau do PATCH `api://{applicationId}`.
- **Anh huong config:** `audience` / `scopeFull` trong `entra` dung `api://{clientId}`, **khong** phai chuoi `prefix-suffix-supportpoc` trong TF.
- **Viec sau:** cap nhat `identity.tf` hoac comment khi sau nay chay `terraform apply` tren cung tenant.

### 4. Graph PATCH that bai (exit 255) — body JSON dai

- **Triệu chứng:** PATCH appRoles + oauth2 scope mot lan tren Windows.
- **Cach xu ly:** truyen body qua file: `az rest --body @C:\temp\xxx.json` (khong nhet JSON tren command line).

### 5. `az ad app create --spa-redirect-uris` khong ton tai

- **Triệu chứng:** `unrecognized arguments: --spa-redirect-uris`.
- **Cach xu ly:** PATCH Graph `"spa": { "redirectUris": ["http://localhost:5173/"] }`.

### 6. `az ad app permission admin-consent` loi Bad Request (MCP)

- **Triệu chứng:** Portal-style consent qua CLI fail cho app moi.
- **Cach xu ly:** tiep tuc — **gan app role** `Support.Service` cho MCP service principal qua Graph POST **van thanh cong**; du cho client credentials sau nay.
- Neu can admin consent thu cong: Entra portal -> Enterprise applications -> API permissions -> Grant admin consent.

### 7. `sync-config.sh` — `dotnet user-secrets set AzureAd:Enabled` fail

- **Triệu chứng:** project chua co `UserSecretsId` hoac dotnet loi.
- **Trang thai:** `appsettings.Development.json` va `frontend/.env.local` **da duoc ghi**; chi user-secrets Entra co the fail.
- **Cach xu ly tam:** doc `AzureAd` tu `appsettings.Development.json` hoac set user-secrets thu cong sau khi them `UserSecretsId` vao `.csproj`.

## Output sau khi chay thanh cong

- `config/azure.local.json` — section `entra` (gitignored).
- `config/entra.provision.state.json` — ban sao entra (gitignored).
- Ba app tren Entra:
  - `supportpoc API (tf01)`
  - `supportpoc SPA (tf01)`
  - `supportpoc MCP Service (tf01)`
- Bootstrap roles (mac dinh): user `BOOTSTRAP_USER_ID` -> Employee, Agent, KnowledgeAdmin.

## Idempotent

- Chay lai script: tim app theo `displayName`, bo qua create, cap nhat lai URI/roles neu can.
- **Khong** xoa app khi chay lai.
- MCP secret: moi lan `credential reset` tao secret **moi** — tranh chay lai script neu khong muon rotate.

## Terraform van la IaC chu

- File `identity.tf` giu **vai tro tham chieu** va target state mong muon.
- Khi tenant cho phep URI static, can dong bo lai TF; hien tai **az cli path** dung `api://{clientId}` cho dung policy tenant nay.
- Khong import state TF tu ban ghi az cli tru khi co ke hoach migration rieng.

## Kiem tra UI (chi frontend)

```bash
bash scripts/sync-config.sh
cd frontend && npm install && npm run dev
```

Mo **Chrome/Edge** (khong phai trinh duyet nhung): http://localhost:5173 → **Entra / Login** → **Dang nhap Microsoft**.

Sau login: trang hien username + JWT decode (API scope + Graph). Khong can Ticket Service.

**Luu y:** popup bi chan → MSAL fallback `loginRedirect`; can redirect URI `http://localhost:5173/` tren Entra SPA.

## Buoc tiep theo (code — xem docs/zero-trust-identity.md)

1. ~~Frontend MSAL + hien token~~ (done — `frontend/src/auth/`).
2. Backend JWT + policies `Support.*` (chua lam).
3. MCP client credentials.

---

*Ghi lai sau lan chay provision thanh cong dau tien (az cli path, Windows + tenant binhthedevgmail.onmicrosoft.com).*
