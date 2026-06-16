#!/bin/sh
set -eu

cat >/usr/share/nginx/html/config.js <<EOF
window.__APP_CONFIG__ = {
  VITE_AAD_TENANT_ID: "${VITE_AAD_TENANT_ID:-}",
  VITE_AAD_AUTHORITY: "${VITE_AAD_AUTHORITY:-}",
  VITE_AAD_CLIENT_ID: "${VITE_AAD_CLIENT_ID:-}",
  VITE_AAD_API_SCOPE: "${VITE_AAD_API_SCOPE:-}"
};
EOF
