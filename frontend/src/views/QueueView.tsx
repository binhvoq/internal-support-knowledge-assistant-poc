import { useEffect, useState } from 'react';
import { api, type Ticket } from '../api';
import { AuthRequiredBanner } from '../auth/AuthRequiredBanner';
import { useAuth } from '../auth/AuthContext';
import { categories, statuses } from '../constants';
import { EmptyState } from '../components/EmptyState';
import { SectionCard } from '../components/SectionCard';
import { StatusBadge } from '../components/StatusBadge';
import { formatDate, shortText } from '../utils/format';

export function QueueView({ onSelect }: { onSelect: (id: string) => void }) {
  const auth = useAuth();
  const [tickets, setTickets] = useState<Ticket[]>([]);
  const [status, setStatus] = useState('');
  const [category, setCategory] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    const run = async () => {
      if (auth.configured && (!auth.ready || !auth.account)) {
        if (!cancelled) {
          setTickets([]);
          setError('');
          setLoading(false);
        }
        return;
      }

      try {
        setLoading(true);
        const data = await api.listTickets(status || undefined, category || undefined);
        if (!cancelled) {
          setTickets(data);
          setError('');
        }
      } catch (e) {
        if (!cancelled) setError((e as Error).message);
      } finally {
        if (!cancelled) setLoading(false);
      }
    };
    run();
    const timer = setInterval(run, 5000);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [status, category, auth.configured, auth.ready, auth.account]);

  return (
    <>
      <div className="page-title-row">
        <h1>Support Queue</h1>
        <div className="live-indicator">
          <span /> Đang tự động cập nhật mỗi 5 giây
        </div>
      </div>
      <AuthRequiredBanner action="xem hàng đợi (cần role Support.Agent)" />
      {error && <p className="error panel-error">{error}</p>}
      <SectionCard className="filter-card">
        <label>
          <span>Trạng thái: {status || 'Tất cả'}</span>
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Tất cả</option>
            {statuses.map((item) => (
              <option key={item}>{item}</option>
            ))}
          </select>
        </label>
        <label>
          <span>Danh mục: {category || 'Tất cả'}</span>
          <select value={category} onChange={(e) => setCategory(e.target.value)}>
            <option value="">Tất cả</option>
            {categories.map((item) => (
              <option key={item}>{item}</option>
            ))}
          </select>
        </label>
        <label className="search-label">
          <span>Tìm kiếm</span>
          <input placeholder="Tìm theo ticket ID hoặc nội dung..." />
        </label>
      </SectionCard>
      <SectionCard className="table-card">
        {loading ? (
          <EmptyState title="Đang tải queue" text="Đang lấy danh sách ticket mới nhất." />
        ) : tickets.length === 0 ? (
          <EmptyState title="Không có ticket" text="Không có ticket phù hợp với bộ lọc hiện tại." />
        ) : (
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>ID</th>
                  <th>Nhân viên</th>
                  <th>Danh mục</th>
                  <th>Trạng thái</th>
                  <th>Câu hỏi</th>
                  <th>AI</th>
                  <th>Ngày tạo</th>
                </tr>
              </thead>
              <tbody>
                {tickets.map((ticket) => (
                  <tr
                    key={ticket.id}
                    onClick={() => onSelect(ticket.id)}
                    role="button"
                    tabIndex={0}
                    aria-label={`Mở ticket ${ticket.id}`}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault();
                        onSelect(ticket.id);
                      }
                    }}
                  >
                    <td>
                      <strong className="link-text">{ticket.id}</strong>
                    </td>
                    <td>{ticket.employeeId}</td>
                    <td>{ticket.category}</td>
                    <td>
                      <StatusBadge status={ticket.status} />
                    </td>
                    <td>{shortText(ticket.question)}</td>
                    <td>
                      <span className={ticket.aiSuggestedAnswer ? 'badge ai' : 'badge pending'}>
                        {ticket.aiSuggestedAnswer ? 'Auto ready' : 'Pending'}
                      </span>
                    </td>
                    <td>{formatDate(ticket.createdAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </SectionCard>
    </>
  );
}
