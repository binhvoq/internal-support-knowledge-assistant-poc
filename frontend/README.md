# Support PoC — Frontend

## Đăng nhập Entra + gọi API (Zero Trust)

```bash
# Configure Entra/Azure values directly in appsettings.Development.json, user-secrets,
# and frontend/.env.local. See ../scripts/README.md for the direct-command policy.
cd frontend && npm install && npm run dev
```

Mở **http://localhost:5173** hoặc **http://127.0.0.1:5173** → tab **Entra / Login** → **Đăng nhập Microsoft**.

Sau login, UI hiển thị token decode (`aud`, `scp`, `roles`, `oid`). `api.ts` gắn `Authorization: Bearer` cho mọi request backend.

### E2E sau login

| Tab | Cần role | Kỳ vọng |
|-----|----------|---------|
| Employee | Employee+ | Tạo ticket OK |
| Support Queue | Agent | List tickets OK |
| Knowledge Admin | KnowledgeAdmin | CRUD / re-index OK |
| AI Chat | Employee+ | HTTP 200 (nội dung chat: xem bug mở trong doc) |

Không login → API trả **401 Unauthorized** (đúng Zero Trust).

Refresh trang **không** cần login lại (MSAL `sessionStorage`). Đóng tab → login lại.

## Env (`.env.local`)

| Biến | Mô tả |
|------|--------|
| `VITE_AAD_CLIENT_ID` | SPA app (Entra) |
| `VITE_AAD_AUTHORITY` | `https://login.microsoftonline.com/{tenant}` |
| `VITE_AAD_API_SCOPE` | `api://{api-app-id}/access_as_user` |
| `VITE_TICKET_API` | Mặc định `http://localhost:5001` |

Mẫu: [`.env.example`](.env.example)

## Handoff cho AI khác

Sau login, lưu phiên (gitignored): [`../config/entra-browser-session.local.json`](../config/entra-browser-session.local.json)
Mẫu: [`../config/entra-browser-session.local.json.example`](../config/entra-browser-session.local.json.example)

## Ghi chú MSAL

- **loginPopup**; popup bị chặn → redirect.
- Redirect URI Entra: `http://localhost:5173/` **và** `http://127.0.0.1:5173/` (tránh AADSTS50011).
- Lỗi redirect URI: chạy lại `provision-entra.sh` hoặc xem §10 trong doc Zero Trust.

Tài liệu đầy đủ: [`../docs/zero-trust-identity.md`](../docs/zero-trust-identity.md)
