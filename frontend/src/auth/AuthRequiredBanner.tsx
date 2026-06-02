import { useAuth } from './AuthContext';
import { entraConfigured } from './msalConfig';

/** Nhac dang nhap truoc khi goi API co Bearer (khi Entra bat trong backend). */
export function AuthRequiredBanner({ action }: { action: string }) {
  const auth = useAuth();
  if (!entraConfigured || auth.account) return null;
  return (
    <p className="auth-required-banner" role="status">
      Can dang nhap Microsoft Entra de {action}. Vao tab <strong>Entra / Login</strong> → Đăng nhập
      Microsoft.
    </p>
  );
}
