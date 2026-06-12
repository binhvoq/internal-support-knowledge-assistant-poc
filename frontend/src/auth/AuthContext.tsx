import {
  type AccountInfo,
  EventType,
  InteractionRequiredAuthError,
  PublicClientApplication,
} from '@azure/msal-browser';
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';
import { apiScope, entraConfigured, loginScopes, msalConfig } from './msalConfig';

type AuthContextValue = {
  configured: boolean;
  ready: boolean;
  account: AccountInfo | null;
  login: () => Promise<void>;
  logout: () => Promise<void>;
  getAccessToken: (scopes?: string[]) => Promise<string | null>;
};

const AuthContext = createContext<AuthContextValue | null>(null);

let msalSingleton: PublicClientApplication | null = null;

function getMsal(): PublicClientApplication | null {
  if (!entraConfigured) return null;
  if (!msalSingleton) msalSingleton = new PublicClientApplication(msalConfig);
  return msalSingleton;
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [ready, setReady] = useState(!entraConfigured);
  const [account, setAccount] = useState<AccountInfo | null>(null);

  useEffect(() => {
    const msal = getMsal();
    if (!msal) return;

    let cancelled = false;
    (async () => {
      await msal.initialize();
      const result = await msal.handleRedirectPromise();
      if (cancelled) return;
      if (result?.account) setAccount(result.account);
      else if (msal.getAllAccounts().length > 0) setAccount(msal.getAllAccounts()[0]);
      setReady(true);
    })();

    const id = msal.addEventCallback((event) => {
      if (event.eventType === EventType.LOGIN_SUCCESS && event.payload && 'account' in event.payload) {
        setAccount(event.payload.account ?? null);
      }
      if (event.eventType === EventType.LOGOUT_SUCCESS) setAccount(null);
    });

    return () => {
      cancelled = true;
      if (id) msal.removeEventCallback(id);
    };
  }, []);

  const login = useCallback(async () => {
    const msal = getMsal();
    if (!msal) throw new Error('Entra chưa cấu hình (VITE_AAD_*). Copy giá trị từ config/azure.local.json vào frontend/.env.local.');
    await msal.loginRedirect({ scopes: loginScopes() });
  }, []);

  const logout = useCallback(async () => {
    const msal = getMsal();
    if (!msal || !account) return;
    await msal.logoutPopup({ account });
    setAccount(null);
  }, [account]);

  const getAccessToken = useCallback(
    async (scopes?: string[]) => {
      const msal = getMsal();
      if (!msal || !account) return null;
      const useScopes = scopes?.length ? scopes : apiScope ? [apiScope] : loginScopes();
      try {
        const result = await msal.acquireTokenSilent({ account, scopes: useScopes });
        return result.accessToken;
      } catch (e) {
        if (e instanceof InteractionRequiredAuthError) {
          await msal.acquireTokenRedirect({ account, scopes: useScopes });
          return null;
        }
        throw e;
      }
    },
    [account],
  );

  const value = useMemo<AuthContextValue>(
    () => ({
      configured: entraConfigured,
      ready,
      account,
      login,
      logout,
      getAccessToken,
    }),
    [ready, account, login, logout, getAccessToken],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
