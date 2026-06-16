import { useEffect, useRef, useState } from 'react';
import { api, type Ticket } from '../api';
import { EmptyState } from '../components/EmptyState';
import { Icon } from '../components/Icon';
import { SectionCard } from '../components/SectionCard';
import { StatusBadge } from '../components/StatusBadge';
import { autoSuggestionStatusText, formatDate } from '../utils/format';

export function DetailView({ ticketId, onBack }: { ticketId: string; onBack: () => void }) {
  const [ticket, setTicket] = useState<Ticket | null>(null);
  const [autoJobStatus, setAutoJobStatus] = useState<string | null>(null);
  const [finalAnswer, setFinalAnswer] = useState('');
  const [error, setError] = useState('');
  const finalAnswerRef = useRef<HTMLTextAreaElement | null>(null);
  const finalAnswerDirtyRef = useRef(false);

  const refreshTicket = async (syncFinalAnswer = false) => {
    const nextTicket = await api.getTicket(ticketId);
    setTicket(nextTicket);
    if (syncFinalAnswer) {
      setFinalAnswer(nextTicket.finalAnswer ?? nextTicket.aiSuggestedAnswer ?? '');
      finalAnswerDirtyRef.current = false;
    }
  };

  useEffect(() => {
    let cancelled = false;
    const run = async () => {
      const nextTicket = await api.getTicket(ticketId);
      if (cancelled) return;
      setTicket(nextTicket);
      setFinalAnswer(nextTicket.finalAnswer ?? nextTicket.aiSuggestedAnswer ?? '');
      finalAnswerDirtyRef.current = false;
      if (!nextTicket.aiSuggestedAnswer && nextTicket.status === 'New') {
        const job = await api.getAutoSuggestionJob(ticketId);
        if (!cancelled) setAutoJobStatus(job?.status ?? null);
      }
    };
    run();
    const timer = setInterval(() => {
      void (async () => {
        const nextTicket = await api.getTicket(ticketId);
        if (cancelled) return;
        setTicket(nextTicket);
        if (!finalAnswerDirtyRef.current) {
          setFinalAnswer(nextTicket.finalAnswer ?? nextTicket.aiSuggestedAnswer ?? '');
        }
        if (!nextTicket.aiSuggestedAnswer && nextTicket.status === 'New') {
          const job = await api.getAutoSuggestionJob(ticketId);
          if (!cancelled) setAutoJobStatus(job?.status ?? null);
        } else if (!cancelled) {
          setAutoJobStatus(null);
        }
      })();
    }, 4000);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [ticketId]);

  const resolve = async () => {
    setError('');
    const answer = finalAnswerRef.current?.value ?? finalAnswer;
    if (!answer.trim()) {
      setError('Câu trả lời cuối cùng không được để trống khi resolve.');
      return;
    }
    try {
      const updated = await api.resolveTicket(ticketId, answer);
      setTicket(updated);
      setFinalAnswer(updated.finalAnswer ?? answer);
      finalAnswerDirtyRef.current = false;
    } catch (e) {
      setError((e as Error).message);
    }
  };

  const saveAnswer = async () => {
    if (!ticket) return;
    const answer = finalAnswerRef.current?.value ?? finalAnswer;
    const updated = await api.patchTicket(ticketId, { status: ticket.status, finalAnswer: answer });
    setTicket(updated);
    setFinalAnswer(updated.finalAnswer ?? answer);
    finalAnswerDirtyRef.current = false;
  };

  const reopen = async () => {
    await api.reopenTicket(ticketId);
    await refreshTicket(true);
  };

  if (!ticket) {
    return (
      <SectionCard>
        <EmptyState title="Đang tải ticket" text="Đang lấy chi tiết ticket." />
      </SectionCard>
    );
  }

  return (
    <>
      <button className="secondary back-button" type="button" onClick={onBack}>
        ← Quay lại queue
      </button>
      <div className="detail-grid">
        <div className="detail-main">
          <SectionCard className="ticket-hero">
            <div>
              <h1>{ticket.id}</h1>
              <p>
                Danh mục: {ticket.category} <span>|</span> Nhân viên: {ticket.employeeId}
              </p>
            </div>
            <StatusBadge status={ticket.status} />
            <div className="ticket-meta">
              <span>
                <Icon name="calendar" /> Ngày tạo: {formatDate(ticket.createdAt)}
              </span>
              <span>
                <Icon name="calendar" /> Cập nhật: {formatDate(ticket.updatedAt)}
              </span>
              <span>
                <Icon name="users" /> Agent: Nguyễn An
              </span>
            </div>
          </SectionCard>

          <SectionCard>
            <h2>Câu hỏi</h2>
            <div className="readonly-box">{ticket.question}</div>
          </SectionCard>

          <SectionCard>
            <h2>Câu trả lời cuối cùng</h2>
            <textarea
              ref={finalAnswerRef}
              className="answer-box"
              value={finalAnswer}
              onChange={(e) => {
                finalAnswerDirtyRef.current = true;
                setFinalAnswer(e.target.value);
              }}
            />
            {error && <p className="error">{error}</p>}
            <div className="actions split">
              <button className="secondary" type="button" onClick={saveAnswer}>
                <Icon name="file" /> Lưu câu trả lời
              </button>
              <button className="primary" type="button" onClick={resolve}>
                <Icon name="check" /> Resolve
              </button>
              <button className="secondary" type="button" onClick={reopen}>
                ↻ Reopen
              </button>
            </div>
          </SectionCard>
        </div>

        <aside className="detail-side">
          <SectionCard>
            <h2>
              <span className="round-icon">
                <Icon name="sparkles" />
              </span>{' '}
              Gợi ý sơ bộ từ AI
            </h2>
            <div className="suggestion-box">
              {ticket.aiSuggestedAnswer || autoSuggestionStatusText(autoJobStatus)}
            </div>
            <div className="actions">
              <button className="secondary" type="button">
                <Icon name="copy" /> Sao chép
              </button>
              <button
                className="primary"
                type="button"
                onClick={() => {
                  finalAnswerDirtyRef.current = true;
                  setFinalAnswer(ticket.aiSuggestedAnswer ?? '');
                }}
                disabled={!ticket.aiSuggestedAnswer}
              >
                <Icon name="check" /> Áp dụng vào câu trả lời
              </button>
            </div>
          </SectionCard>
          <SectionCard>
            <h2>Tài liệu liên quan</h2>
            <div className="doc-links">
              {ticket.relatedDocuments?.length ? (
                ticket.relatedDocuments.map((document) => (
                  <div className="doc-link" key={document.documentId}>
                    <Icon name="file" />
                    <span>{document.title}</span>
                    <strong>Score {document.score.toFixed(2)}</strong>
                  </div>
                ))
              ) : (
                <p className="muted">Chưa có tài liệu liên quan.</p>
              )}
            </div>
            <a className="text-link" href="#knowledge">
              Xem tất cả tài liệu ›
            </a>
          </SectionCard>
        </aside>
      </div>
    </>
  );
}
