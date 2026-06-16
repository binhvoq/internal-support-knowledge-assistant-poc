import { Icon } from './Icon';

export function EmptyState({ title, text }: { title: string; text: string }) {
  return (
    <div className="empty-state">
      <Icon name="file" />
      <h3>{title}</h3>
      <p>{text}</p>
    </div>
  );
}
