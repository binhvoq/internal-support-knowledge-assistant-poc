# Entra ID — 100% Terraform (IaC)

Terraform (`identity.tf`) là **nguồn sự thật duy nhất** cho app registrations Entra của PoC này.

Script `scripts/provision-entra.sh` chỉ còn lịch sử / khẩn cấp; **không** chạy song song với Terraform trên cùng tenant (sẽ trùng app hoặc lệch state).

## Điều kiện

- `az login` đúng tenant (`var.tenant_id` hoặc tenant mặc định).
- Quyền **Application Administrator** hoặc **Global Administrator**.
- `terraform init` trong `infra/terraform`.

## Đường A — Tạo mới hoàn toàn (greenfield)

Dùng khi **chưa** có 3 app `supportpoc * (tf01)` hoặc bạn đã xóa chúng trên Entra.

```bash
cd infra/terraform
terraform init
terraform plan
terraform apply
cd ../..
bash scripts/sync-config.sh
```

Sau `apply`, `config/azure.local.json` có block `entra` (nếu `write_local_config = true`). Chạy `sync-config.sh` để đẩy sang `appsettings` và `frontend/.env.local`.

**Lưu ý:** Client ID mới → MSAL redirect vẫn đúng nếu `spa_redirect_uris` khớp; user phải consent lại.

## Đường B — Giữ app đã tạo bằng az cli (import)

Dùng khi đã có app với **cùng** display name và role/scope UUID như `identity.tf`.

### 1. Lấy object ID (Graph `id`, không phải `appId`)

```bash
API_APP_ID="dacbf464-d15e-499c-bc5d-6ee94ca26e7d"   # thay bằng appId thật
SPA_APP_ID="ca3acec4-f2b5-4074-aea7-e9169057021e"
MCP_APP_ID="5385b744-043a-403f-8216-1186e8b505f3"

API_OBJ=$(az rest --method GET --uri "https://graph.microsoft.com/v1.0/applications(appId='$API_APP_ID')" --query id -o tsv)
SPA_OBJ=$(az rest --method GET --uri "https://graph.microsoft.com/v1.0/applications(appId='$SPA_APP_ID')" --query id -o tsv)
MCP_OBJ=$(az rest --method GET --uri "https://graph.microsoft.com/v1.0/applications(appId='$MCP_APP_ID')" --query id -o tsv)

API_SP=$(az ad sp show --id "$API_APP_ID" --query id -o tsv)
SPA_SP=$(az ad sp show --id "$SPA_APP_ID" --query id -o tsv)
MCP_SP=$(az ad sp show --id "$MCP_APP_ID" --query id -o tsv)
```

### 2. Import vào state

```bash
cd infra/terraform

terraform import 'azuread_application.api[0]'         "$API_OBJ"
terraform import 'azuread_application.spa[0]'         "$SPA_OBJ"
terraform import 'azuread_application.mcp_service[0]' "$MCP_OBJ"

terraform import 'azuread_service_principal.api[0]'         "$API_SP"
terraform import 'azuread_service_principal.spa[0]'         "$SPA_SP"
terraform import 'azuread_service_principal.mcp_service[0]' "$MCP_SP"
```

Identifier URI (`api://{clientId}`) — import theo [tài liệu provider](https://registry.terraform.io/providers/hashicorp/azuread/latest/docs/resources/application_identifier_uri):

```bash
URI_B64=$(printf '%s' "api://${API_APP_ID}" | base64 | tr -d '\n')
terraform import 'azuread_application_identifier_uri.api[0]' "/applications/${API_OBJ}/identifierUris/${URI_B64}"
```

`azuread_application_pre_authorized.spa`, `azuread_app_role_assignment.*`, `azuread_application_password.mcp_service` thường import thủ công hoặc để Terraform tạo lại sau `plan` (xem diff). Secret MCP: import khó — có thể chấp nhận **rotate một lần** qua `terraform apply` (cập nhật `azure.local.json`).

### 3. Plan phải gần “no change”

```bash
terraform plan
```

Nếu plan muốn **đổi** app roles / scope IDs → không import; xóa app và chạy đường A.

## Identifier URI

Tenant PoC **không** chấp nhận URI tĩnh `api://supportpoc-tf01-supportpoc`. Terraform dùng:

- `azuread_application_identifier_uri` → `api://{client_id}`

Khớp với policy Entra và với bản đã provision bằng `provision-entra.sh`.

## MCP client secret

- Không dùng `timestamp()` mỗi plan (sẽ rotate liên tục).
- Dùng `time_offset.mcp_secret_expiry` + `azuread_application_password`.
- `terraform apply` lần đầu sau import có thể tạo secret mới — chạy lại `sync-config.sh`.

## Destroy / khôi phục

```bash
terraform destroy   # xóa Entra apps trong state
terraform apply     # tạo lại đúng identity.tf
```

Đó là lợi ích IaC: xóa tay trên portal rồi `apply` **có thể** build lại (client ID mới, cần sync config).

## Vướng mắt còn lại (ngoài Terraform)

| Vướng | Ghi chú |
|-------|---------|
| Quyền Entra | Không đủ quyền → apply fail |
| Bootstrap user | `bootstrap_user_id` phải là object ID user thật trong tenant |
| Azure RM vs Entra | `terraform apply` full stack cần subscription + quota Azure |
| CA / MFA | Chưa trong `identity.tf` (phase sau) |
| Admin consent | Một số tenant vẫn cần grant trên portal lần đầu |
