import { AuthTestPanel } from '../auth/AuthTestPanel';
import { Icon } from '../components/Icon';
import { SectionCard } from '../components/SectionCard';

export function LoginView() {
  return (
    <div className="login-wrap">
      <SectionCard className="login-card">
        <div className="security-mark">
          <Icon name="lock" />
          <span className="microsoft-mark" aria-hidden="true">
            <i />
            <i />
            <i />
            <i />
          </span>
        </div>
        <h1>Đăng nhập Entra ID 123</h1>
        <p className="lead">Đăng nhập bằng tài khoản Microsoft để truy cập hệ thống hỗ trợ nội bộ.</p>
        <AuthTestPanel />
        <div className="info-callout">
          <span className="info-dot">i</span>
          <span>Bạn cần đăng nhập trước khi tạo ticket, xem queue hoặc dùng Copilot.</span>
        </div>
      </SectionCard>
    </div>
  );
}
