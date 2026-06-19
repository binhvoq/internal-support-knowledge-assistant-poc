import type { View } from './types';

export const categories = ['IT', 'HR', 'Finance', 'Other'];
export const statuses = ['New', 'Suggested', 'Resolved', 'Reopened'];

export const viewLabels: Record<View, string> = {
  auth: 'Đăng nhập',
  employee: 'Tạo ticket',
  queue: 'Support Queue',
  detail: 'Ticket Detail',
  knowledge: 'Knowledge Admin',
  chat: 'Copilot',
};

export const viewIcons: Record<View, string> = {
  auth: 'lock',
  employee: 'plus',
  queue: 'users',
  detail: 'file',
  knowledge: 'database',
  chat: 'sparkles',
};

// Keep nav order stable for UI layout and route grouping.
export const navItems: View[] = ['auth', 'employee', 'queue', 'detail', 'knowledge', 'chat'];
