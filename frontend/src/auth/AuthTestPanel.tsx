import { useCallback, useEffect, useState } from 'react';
import { apiScope, msalConfig } from './msalConfig';
import { useAuth } from './AuthContext';

type TokenPreview = {
  label: string;
  ok: boolean;
  tokenPrefix: string;
  payload: Record<string, unknown> | null;
  error?: string;
};

function decodeJwtPayload(token: string): Record<string, unknown> | null {
  try {
    const part = token.split('.')[1];
    if (!part) return null;
    const padded = part.replace(/-/g, '+').replace(/_/g, '/');
    const json = atob(padded);
    return JSON.parse(json) as Record<string, unknown>;
  } catch {
    return null;
  }
}

function summarizePayload(payload: Record<string, unknown> | null) {
  if (!payload) return null;
  const roles = payload.roles ?? payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'];
  return {
    aud: payload.aud,
    scp: payload.scp,
    roles,
    oid: payload.oid,
    tid: payload.tid,
    preferred_username: payload.preferred_username,
    name: payload.name,
    exp: payload.exp,
    iss: payload.iss,
  };
}

export function AuthTestPanel() {
  const { configured, ready, account, login, logout, getAccessToken } = useAuth();
  const [busy, setBusy] = useState(false);
  const [loginError, setLoginError] = useState('');
  const [tokens, setTokens] = useState<TokenPreview[]>([]);

  const refreshTokens = useCallback(async () => {
    if (!account) {
      setTokens([]);
      return;
    }
    setBusy(true);
    const out: TokenPreview[] = [];

    const tryScope = async (label: string, scopes: string[]) => {
      try {
        const token = await getAccessToken(scopes);
        if (!token) {
          out.push({
            label,
            ok: false,
            tokenPrefix: '',
            payload: null,
            error: 'Khong lay duoc token (co the dang redirect).',
          });
          return;
        }
        const payload = decodeJwtPayload(token);
        out.push({
          label,
          ok: true,
          tokenPrefix: `${token.slice(0, 24)}…${token.slice(-12)} (${token.length} chars)`,
          payload: summarizePayload(payload) as Record<string, unknown> | null,
        });
      } catch (e) {
        out.push({
          label,
          ok: false,
          tokenPrefix: '',
          payload: null,
          error: (e as Error).message,
        });
      }
    };

    if (apiScope) await tryScope(`Access token — API (${apiScope})`, [apiScope]);
    await tryScope('Access token — Graph User.Read', ['User.Read']);

    setTokens(out);
    setBusy(false);
  }, [account, getAccessToken]);

  useEffect(() => {
    if (account && ready) void refreshTokens();
    else setTokens([]);
  }, [account, ready, refreshTokens]);

  const onLogin = async () => {
    setLoginError('');
    try {
      await login();
    } catch (e) {
      setLoginError((e as Error).message);
    }
  };

  if (!configured) {
    return (
      <div className="card auth-panel">
        <h2>Đăng nhập Entra ID</h2>
        <p>
          Thiếu biến môi trường MSAL. Cấu hình Entra trực tiếp rồi chạy frontend:
        </p>
        <pre className="auth-pre">
          {'# Copy Terraform/Entra values into frontend/.env.local\n'}
          cd frontend && npm run dev
        </pre>
      </div>
    );
  }

  const clientHint = msalConfig.auth.clientId
    ? `${msalConfig.auth.clientId.slice(0, 8)}…`
    : '?';

  return (
    <div className="card auth-panel">
      <h2>Đăng nhập Entra ID (frontend)</h2>
      <p className="muted">
        Chỉ kiểm tra MSAL + gọi API có Bearer khi đã đăng nhập. Scope:{' '}
        <code>{apiScope || '(chưa set)'}</code> · SPA: <code>{clientHint}</code>
        <br />
        Đăng nhập chuyển hướng trên tab hiện tại rồi quay lại trang này sau khi xác thực.
      </p>

      {!ready && <p className="muted">Đang khởi tạo MSAL…</p>}

      <div className="auth-bar">
        {account ? (
          <>
            <span className="auth-user">
              ✓ {account.name ?? account.username}
            </span>
            <button type="button" onClick={() => logout()}>
              Đăng xuất
            </button>
            <button type="button" onClick={() => refreshTokens()} disabled={busy}>
              {busy ? 'Đang lấy token…' : 'Làm mới token'}
            </button>
          </>
        ) : (
          <button type="button" className="primary" onClick={onLogin} disabled={!ready || busy}>
            Đăng nhập Microsoft
          </button>
        )}
      </div>

      {loginError && <p className="auth-error">{loginError}</p>}

      {account && (
        <div className="auth-account-meta">
          <div><strong>username:</strong> {account.username}</div>
          <div><strong>tenant:</strong> {account.tenantId}</div>
          <div><strong>homeAccountId:</strong> {account.homeAccountId}</div>
        </div>
      )}

      {tokens.length > 0 && (
        <ul className="auth-results">
          {tokens.map((t) => (
            <li key={t.label} className={t.ok ? 'ok' : 'fail'}>
              <strong>{t.label}</strong> {t.ok ? 'OK' : 'FAIL'}
              {t.tokenPrefix && <div className="token-prefix">{t.tokenPrefix}</div>}
              {t.error && <pre className="auth-pre">{t.error}</pre>}
              {t.payload && (
                <pre className="auth-pre">{JSON.stringify(t.payload, null, 2)}</pre>
              )}
            </li>
          ))}
        </ul>
      )}

      {account && tokens.length === 0 && !busy && (
        <p className="muted">Chưa có token — bấm &quot;Làm mới token&quot;.</p>
      )}
    </div>
  );
}
