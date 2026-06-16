import { useEffect, useState } from 'react';
import { api, type Ticket } from '../api';
import { AuthRequiredBanner } from '../auth/AuthRequiredBanner';
import { useAuth } from '../auth/AuthContext';
import { categories } from '../constants';
import { EmptyState } from '../components/EmptyState';
import { Icon } from '../components/Icon';
import { SectionCard } from '../components/SectionCard';
import { StatusBadge } from '../components/StatusBadge';
import { formatDate, shortText } from '../utils/format';

export function EmployeeView() {
  const auth = useAuth();
  const [employeeId, setEmployeeId] = useState('nguyen.an@company.com');
  const [question, setQuestion] = useState('');
  const [category, setCategory] = useState('');
  const [created, setCreated] = useState<Ticket | null>(null);
  const [myTickets, setMyTickets] = useState<Ticket[]>([]);
  const [error, setError] = useState('');
  const effectiveEmployeeId = auth.account?.username ?? employeeId;

  useEffect(() => {
    if (auth.configured && (!auth.ready || !auth.account)) {
      queueMicrotask(() => setMyTickets([]));
      return;
    }
    let cancelled = false;
    api
      .listMyTickets()
      .then((data) => {
        if (!cancelled) setMyTickets(data);
      })
      .catch(() => {
        if (!cancelled) setMyTickets([]);
      });
    return () => {
      cancelled = true;
    };
  }, [auth.configured, auth.ready, auth.account, created?.id]);

  const submit = async () => {
    setError('');
    if (auth.configured && (!auth.ready || !auth.account)) {
      setError('Cần đăng nhập Microsoft Entra trước khi tạo ticket.');
      return;
    }
    if (!effectiveEmployeeId.trim()) {
      setError('Employee ID không được để trống.');
      return;
    }
    if (!question.trim()) {
      setError('Câu hỏi không được để trống.');
      return;
    }
    try {
      const ticket = await api.createTicket({
        employeeId: effectiveEmployeeId,
        question,
        category: category || undefined,
      });
      setCreated(ticket);
      setQuestion('');
    } catch (e) {
      setError((e as Error).message);
    }
  };

  return (
    <div className="content-narrow">
      <SectionCard>
        <h1>Tạo câu hỏi hỗ trợ</h1>
        <AuthRequiredBanner action="tạo ticket" />
        <div className="form-grid two">
          <label>
            <span>Employee ID</span>
            <input
              value={effectiveEmployeeId}
              readOnly={Boolean(auth.account)}
              onChange={(e) => {
                setEmployeeId(e.target.value);
                if (error) setError('');
              }}
            />
          </label>
          <label>
            <span>Danh mục</span>
            <select value={category} onChange={(e) => setCategory(e.target.value)}>
              <option value="">Tự động phân loại</option>
              {categories.map((item) => (
                <option key={item}>{item}</option>
              ))}
            </select>
            <small>Tùy chọn: Tự động phân loại, IT, HR, Finance, Other</small>
          </label>
        </div>
        <label>
          <span>Câu hỏi</span>
          <textarea
            className="question-input"
            value={question}
            placeholder="Ví dụ: Tôi không truy cập được VPN sau khi đổi mật khẩu."
            onChange={(e) => {
              setQuestion(e.target.value);
              if (error) setError('');
            }}
          />
        </label>
        {error && <p className="error">{error}</p>}
        <div className="actions center">
          <button className="primary" type="button" onClick={submit} disabled={auth.configured && (!auth.ready || !auth.account)}>
            <Icon name="plus" />
            Tạo ticket
          </button>
        </div>
      </SectionCard>

      {created && (
        <div className={created.autoSuggestionNotifyFailed ? 'toast error-bg' : 'toast success-bg'}>
          <Icon name={created.autoSuggestionNotifyFailed ? 'file' : 'check'} />
          <div>
            <strong>Đã tạo ticket {created.id}</strong>
            <p>
              {created.autoSuggestionNotifyFailed
                ? 'Ticket đã lưu nhưng pipeline auto-suggestion chưa được kích hoạt.'
                : 'AI đang tạo gợi ý trả lời trong vài giây.'}
            </p>
          </div>
          <Icon name="sparkles" />
        </div>
      )}

      <h2 className="section-title">Ticket của tôi</h2>
      {myTickets.length > 0 ? (
        <div className="ticket-list">
          {myTickets.map((ticket) => (
            <div className="mine-row" key={ticket.id}>
              <strong>{ticket.id}</strong>
              <StatusBadge status={ticket.status} />
              <span>{ticket.category}</span>
              <span>{shortText(ticket.question, 42)}</span>
              <span className="date">
                <Icon name="calendar" /> {formatDate(ticket.createdAt)}
              </span>
              <span className="row-arrow">›</span>
            </div>
          ))}
        </div>
      ) : (
        <EmptyState title="Chưa có ticket" text="Ticket bạn tạo sẽ xuất hiện tại đây." />
      )}
    </div>
  );
}
