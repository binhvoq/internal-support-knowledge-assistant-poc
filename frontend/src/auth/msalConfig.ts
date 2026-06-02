import { type Configuration, LogLevel } from '@azure/msal-browser';

const clientId = import.meta.env.VITE_AAD_CLIENT_ID ?? '';
const authority =
  import.meta.env.VITE_AAD_AUTHORITY ??
  (import.meta.env.VITE_AAD_TENANT_ID
    ? `https://login.microsoftonline.com/${import.meta.env.VITE_AAD_TENANT_ID}`
    : '');

export const entraConfigured = Boolean(clientId && authority);

export const apiScope = import.meta.env.VITE_AAD_API_SCOPE ?? '';

export const msalConfig: Configuration = {
  auth: {
    clientId,
    authority,
    redirectUri: typeof window !== 'undefined' ? window.location.origin + '/' : 'http://localhost:5173/',
    postLogoutRedirectUri:
      typeof window !== 'undefined' ? window.location.origin + '/' : 'http://localhost:5173/',
  },
  cache: {
    cacheLocation: 'sessionStorage',
  },
  system: {
    loggerOptions: {
      logLevel: LogLevel.Warning,
    },
  },
};

/** Scopes for login + API smoke test */
export function loginScopes(): string[] {
  const scopes = ['openid', 'profile', 'offline_access', 'User.Read'];
  if (apiScope) scopes.push(apiScope);
  return scopes;
}
