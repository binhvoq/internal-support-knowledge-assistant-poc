import { Icon } from './Icon';
import { SectionCard } from './SectionCard';

export function Kpi({
  icon,
  value,
  label,
  tone = '',
}: {
  icon: string;
  value: string | number;
  label: string;
  tone?: string;
}) {
  return (
    <SectionCard className="kpi-card">
      <span className={`kpi-icon ${tone}`}>
        <Icon name={icon} />
      </span>
      <div>
        <strong>{value}</strong>
        <span>{label}</span>
      </div>
    </SectionCard>
  );
}
