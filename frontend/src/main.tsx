import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import './index.css';

await new Promise<void>((resolve, reject) => {
  const configScript = document.createElement('script');
  configScript.src = '/config.js';
  configScript.onload = () => resolve();
  configScript.onerror = () => reject(new Error('Failed to load runtime config'));
  document.head.appendChild(configScript);
});

const { default: App } = await import('./App.tsx');
const { AuthProvider } = await import('./auth/AuthContext.tsx');

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AuthProvider>
      <App />
    </AuthProvider>
  </StrictMode>,
);
