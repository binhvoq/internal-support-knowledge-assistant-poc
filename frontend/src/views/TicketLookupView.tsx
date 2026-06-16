import { useState } from 'react';
import { api } from '../api';
import { EmptyState } from '../components/EmptyState';
import { SectionCard } from '../components/SectionCard';

export function TicketLookupView({ onSelect }: { onSelect: (ticketId: string) => void }) {
  const [ticketId, setTicketId] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const openTicket = async () => {
    const id = ticketId.trim();
    setError('');
    if (!id) {
      setError('Nhập Ticket ID trước khi mở chi tiết.');
      return;
    }
    try {
      setLoading(true);
      await api.getTicket(id);
      onSelect(id);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="content-narrow">
      <SectionCard>
        <h1>Ticket Detail</h1>
        <p className="lead">Nhập Ticket ID hoặc mở một dòng từ Support Queue để xem chi tiết.</p>
        <label>
          <span>Ticket ID</span>
          <input
            value={ticketId}
            onChange={(e) => {
              setTicketId(e.target.value);
              if (error) setError('');
            }}
            onKeyDown={(e) => {
              if (e.key === 'Enter') void openTicket();
            }}
            placeholder="Ví dụ: TK-2026-001"
          />
        </label>
        {error && <p className="error">{error}</p>}
        <div className="actions">
          <button className="primary" type="button" onClick={openTicket} disabled={loading}>
            {loading ? 'Đang mở...' : 'Mở ticket'}
          </button>
        </div>
      </SectionCard>
      <EmptyState title="Chưa chọn ticket" text="Bạn cũng có thể vào Support Queue rồi click vào một dòng ticket." />
    </div>
  );
}
