import type { ReactNode } from 'react';
import { navItems, viewIcons, viewLabels } from '../constants';
import type { View } from '../types';
import { Icon } from './Icon';

export function AppLayout({
  activeView,
  userName,
  onNavigate,
  children,
}: {
  activeView: View;
  userName?: string;
  onNavigate: (view: View) => void;
  children: ReactNode;
}) {
  const displayName = userName ?? 'Nguyễn An';

  return (
    <div className="app-shell">
      <header className="app-header">
        <div className="brand">
          <span className="brand-icon">
            <Icon name="headset" />
          </span>
          <span>Internal Support Knowledge Assistant</span>
        </div>
        <div className="user-menu" aria-label="Người dùng hiện tại">
          <span className="avatar">{displayName.slice(0, 2).toUpperCase()}</span>
          <span>{displayName}</span>
          <span className="chevron">⌄</span>
        </div>
      </header>

      <nav className="tabs" aria-label="Điều hướng chính">
        {navItems.map((id) => (
          <button
            key={id}
            type="button"
            className={activeView === id ? 'active' : ''}
            onClick={() => onNavigate(id)}
          >
            <Icon name={viewIcons[id]} />
            <span>{viewLabels[id]}</span>
          </button>
        ))}
      </nav>

      <main className="page">{children}</main>
    </div>
  );
}
