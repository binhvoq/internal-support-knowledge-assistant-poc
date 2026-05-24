import { useEffect, useRef, useState } from 'react';
import './App.css';
import { api, type KnowledgeDocument, type Ticket } from './api';

type View = 'employee' | 'queue' | 'detail' | 'knowledge' | 'chat';

const categories = ['IT', 'HR', 'Finance', 'Other'];
const statuses = ['New', 'Analyzing', 'Suggested', 'Resolved', 'Reopened'];

function App() {
  const [view, setView] = useState<View>('employee');
  const [selectedTicketId, setSelectedTicketId] = useState<string | null>(null);

  return (
    <div className="app">
      <h1>Internal Support Knowledge Assistant</h1>
      <nav>
        {(
          [
            ['employee', 'Employee'],
            ['queue', 'Support Queue'],
            ['knowledge', 'Knowledge Admin'],
            ['chat', 'AI Chat'],
          ] as const
        ).map(([id, label]) => (
          <button
            key={id}
            className={view === id ? 'active' : ''}
            onClick={() => {
              setView(id);
              setSelectedTicketId(null);
            }}
          >
            {label}
          </button>
        ))}
        {selectedTicketId && (
          <button className={view === 'detail' ? 'active' : ''} onClick={() => setView('detail')}>
            Ticket Detail
          </button>
        )}
      </nav>

      {view === 'employee' && <EmployeeView />}
      {view === 'queue' && (
        <QueueView
          onSelect={(id) => {
            setSelectedTicketId(id);
            setView('detail');
          }}
        />
      )}
      {view === 'detail' && selectedTicketId && (
        <DetailView ticketId={selectedTicketId} onBack={() => setView('queue')} />
      )}
      {view === 'knowledge' && <KnowledgeView />}
      {view === 'chat' && <ChatView />}
    </div>
  );
}

function EmployeeView() {
  const [employeeId, setEmployeeId] = useState('EMP-001');
  const [question, setQuestion] = useState('');
  const [category, setCategory] = useState('');
  const [created, setCreated] = useState<Ticket | null>(null);
  const [error, setError] = useState('');

  const submit = async () => {
    setError('');
    if (!employeeId.trim()) {
      setError('Employee ID khong duoc de trong.');
      return;
    }
    if (!question.trim()) {
      setError('Question khong duoc de trong.');
      return;
    }
    try {
      const ticket = await api.createTicket({
        employeeId,
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
    <div className="card">
      <h2>Tao cau hoi ho tro</h2>
      <label>Employee ID</label>
      <input
        value={employeeId}
        onChange={(e) => {
          setEmployeeId(e.target.value);
          if (error) setError('');
        }}
      />
      <label>Category (de trong = tu dong phan loai)</label>
      <select value={category} onChange={(e) => setCategory(e.target.value)}>
        <option value="">Tu dong</option>
        {categories.map((c) => (
          <option key={c}>{c}</option>
        ))}
      </select>
      <label>Question</label>
      <textarea
        value={question}
        onChange={(e) => {
          setQuestion(e.target.value);
          if (error) setError('');
        }}
      />
      {error && <p className="error">{error}</p>}
      <div className="actions">
        <button className="primary" onClick={submit}>
          Tao ticket
        </button>
      </div>
      {created && (
        <div className="success">
          Da tao {created.id} — status: {created.status}. AI se xu ly trong giay lat.
        </div>
      )}
    </div>
  );
}

function QueueView({ onSelect }: { onSelect: (id: string) => void }) {
  const [tickets, setTickets] = useState<Ticket[]>([]);
  const [status, setStatus] = useState('');
  const [category, setCategory] = useState('');

  useEffect(() => {
    let cancelled = false;
    const run = async () => {
      const data = await api.listTickets(status || undefined, category || undefined);
      if (!cancelled) setTickets(data);
    };
    run();
    const t = setInterval(run, 5000);
    return () => {
      cancelled = true;
      clearInterval(t);
    };
  }, [status, category]);

  return (
    <div className="card">
      <h2>Support Queue</h2>
      <div style={{ display: 'flex', gap: '1rem', flexWrap: 'wrap' }}>
        <div>
          <label>Status</label>
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">All</option>
            {statuses.map((s) => (
              <option key={s}>{s}</option>
            ))}
          </select>
        </div>
        <div>
          <label>Category</label>
          <select value={category} onChange={(e) => setCategory(e.target.value)}>
            <option value="">All</option>
            {categories.map((c) => (
              <option key={c}>{c}</option>
            ))}
          </select>
        </div>
      </div>
      <div className="table-scroll" aria-label="Ticket list">
        <table>
          <thead>
            <tr>
              <th>ID</th>
              <th>Employee</th>
              <th>Category</th>
              <th>Status</th>
              <th>Question</th>
              <th>AI</th>
              <th>Created</th>
            </tr>
          </thead>
          <tbody>
            {tickets.map((t) => (
              <tr
                key={t.id}
                style={{ cursor: 'pointer' }}
                onClick={() => onSelect(t.id)}
                role="button"
                tabIndex={0}
                aria-label={`Open ticket ${t.id}`}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    onSelect(t.id);
                  }
                }}
              >
                <td>{t.id}</td>
                <td>{t.employeeId}</td>
                <td>{t.category}</td>
                <td>{t.status}</td>
                <td>{t.question.slice(0, 60)}</td>
                <td>
                  {t.aiSuggestedAnswer ? (
                    <span className="badge ai">AI Ready</span>
                  ) : (
                    <span className="badge new">Pending</span>
                  )}
                </td>
                <td>{new Date(t.createdAt).toLocaleString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function DetailView({ ticketId, onBack }: { ticketId: string; onBack: () => void }) {
  const [ticket, setTicket] = useState<Ticket | null>(null);
  const [finalAnswer, setFinalAnswer] = useState('');
  const [error, setError] = useState('');
  const finalAnswerRef = useRef<HTMLTextAreaElement | null>(null);
  const finalAnswerDirtyRef = useRef(false);

  const refreshTicket = async (syncFinalAnswer = false) => {
    const t = await api.getTicket(ticketId);
    setTicket(t);
    if (syncFinalAnswer) {
      setFinalAnswer(t.finalAnswer ?? t.aiSuggestedAnswer ?? '');
      finalAnswerDirtyRef.current = false;
    }
  };

  useEffect(() => {
    let cancelled = false;
    const run = async () => {
      const t = await api.getTicket(ticketId);
      if (cancelled) return;
      setTicket(t);
      setFinalAnswer(t.finalAnswer ?? t.aiSuggestedAnswer ?? '');
      finalAnswerDirtyRef.current = false;
    };
    run();
    const t = setInterval(() => {
      void api.getTicket(ticketId).then((data) => {
        if (cancelled) return;
        setTicket(data);
        if (!finalAnswerDirtyRef.current) {
          setFinalAnswer(data.finalAnswer ?? data.aiSuggestedAnswer ?? '');
        }
      });
    }, 4000);
    return () => {
      cancelled = true;
      clearInterval(t);
    };
  }, [ticketId]);

  const resolve = async () => {
    setError('');
    const answer = finalAnswerRef.current?.value ?? finalAnswer;
    if (!answer.trim()) {
      setError('Final answer khong duoc de trong khi resolve.');
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
    const answer = finalAnswerRef.current?.value ?? finalAnswer;
    const updated = await api.patchTicket(ticketId, { finalAnswer: answer });
    setTicket(updated);
    setFinalAnswer(updated.finalAnswer ?? answer);
    finalAnswerDirtyRef.current = false;
  };

  const reopen = async () => {
    await api.reopenTicket(ticketId);
    await refreshTicket(true);
  };

  if (!ticket) return <div className="card">Dang tai...</div>;

  return (
    <div className="card">
      <button className="secondary" onClick={onBack}>
        Quay lai queue
      </button>
      <h2>{ticket.id}</h2>
      <p>
        <strong>Status:</strong> {ticket.status} | <strong>Category:</strong> {ticket.category}
      </p>
      <p>
        <strong>Question:</strong> {ticket.question}
      </p>
      <h3>AI Suggested Answer</h3>
      {!ticket.aiSuggestedAnswer ? (
        <p>{ticket.status === 'Analyzing' ? 'Dang phan tich...' : 'Chua co goi y.'}</p>
      ) : (
        <p>{ticket.aiSuggestedAnswer}</p>
      )}
      <h3>Related Documents</h3>
      <ul className="related">
        {ticket.relatedDocuments?.length ? (
          ticket.relatedDocuments.map((d) => (
            <li key={d.documentId}>
              {d.title} <small>(score {d.score.toFixed(2)})</small>
            </li>
          ))
        ) : (
          <li>Chua co tai lieu lien quan.</li>
        )}
      </ul>
      <label>Final / Edited Answer</label>
      <textarea
        ref={finalAnswerRef}
        value={finalAnswer}
        onChange={(e) => {
          finalAnswerDirtyRef.current = true;
          setFinalAnswer(e.target.value);
        }}
      />
      {error && <p className="error">{error}</p>}
      <div className="actions">
        <button className="secondary" onClick={saveAnswer}>
          Save Answer
        </button>
        <button className="primary" onClick={resolve}>
          Resolve
        </button>
        <button className="secondary" onClick={reopen}>
          Reopen
        </button>
      </div>
    </div>
  );
}

function KnowledgeView() {
  const [docs, setDocs] = useState<KnowledgeDocument[]>([]);
  const [title, setTitle] = useState('');
  const [category, setCategory] = useState('IT');
  const [content, setContent] = useState('');
  const [sourceUrl, setSourceUrl] = useState('');
  const [reindexStatus, setReindexStatus] = useState('Idle');
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');

  const load = async () => {
    setDocs(await api.listDocuments());
    const st = await api.reindexStatus();
    setReindexStatus(st.status);
  };

  useEffect(() => {
    void Promise.all([api.listDocuments(), api.reindexStatus()]).then(([documents, st]) => {
      setDocs(documents);
      setReindexStatus(st.status);
    });
  }, []);

  const addDoc = async () => {
    setError('');
    setMessage('');
    if (!title.trim()) {
      setError('Title khong duoc de trong.');
      return;
    }
    if (!content.trim()) {
      setError('Content khong duoc de trong.');
      return;
    }
    try {
      await api.createDocument({
        title: title.trim(),
        category,
        content: content.trim(),
        sourceUrl: sourceUrl.trim() || undefined,
      });
      setTitle('');
      setContent('');
      setSourceUrl('');
      setMessage('Da them tai lieu.');
      await load();
    } catch (e) {
      setError((e as Error).message);
    }
  };

  const reindex = async () => {
    setError('');
    setMessage('Dang re-index...');
    try {
      const result = await api.reindex();
      setMessage(`Re-index: ${result.status}, ${result.documentCount} documents.`);
      await load();
    } catch (e) {
      setMessage('');
      setError((e as Error).message);
    }
  };

  return (
    <div className="card">
      <h2>Knowledge Admin</h2>
      <p>
        Re-index status: <strong>{reindexStatus}</strong>
      </p>
      {message && <p className="success">{message}</p>}
      {error && <p className="error">{error}</p>}
      <label>Title</label>
      <input
        value={title}
        onChange={(e) => {
          setTitle(e.target.value);
          if (error) setError('');
        }}
      />
      <label>Category</label>
      <select value={category} onChange={(e) => setCategory(e.target.value)}>
        {categories.map((c) => (
          <option key={c}>{c}</option>
        ))}
      </select>
      <label>Content</label>
      <textarea
        value={content}
        onChange={(e) => {
          setContent(e.target.value);
          if (error) setError('');
        }}
      />
      <label>Source URL (tuy chon)</label>
      <input
        value={sourceUrl}
        onChange={(e) => setSourceUrl(e.target.value)}
        placeholder="internal://it/vpn-reset"
      />
      <div className="actions">
        <button className="primary" onClick={addDoc}>
          Them tai lieu
        </button>
        <button className="secondary" onClick={reindex}>
          Re-index
        </button>
      </div>
      <h3>Danh sach tai lieu</h3>
      <ul className="related">
        {docs.map((d) => (
          <li key={d.id}>
            <strong>{d.id}</strong> — {d.title} ({d.category})
            {d.sourceUrl && (
              <>
                {' '}
                <small>
                  <a href={d.sourceUrl} target="_blank" rel="noreferrer">
                    source
                  </a>
                </small>
              </>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}

function ChatView() {
  const [message, setMessage] = useState('Ticket TCK-001 cua toi xu ly den dau roi?');
  const [reply, setReply] = useState('');
  const [loading, setLoading] = useState(false);

  const send = async () => {
    setLoading(true);
    try {
      const res = await api.chat(message);
      setReply(res.reply);
    } catch (e) {
      setReply((e as Error).message);
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="card">
      <h2>AI Chat (Function Calling)</h2>
      <p>
        Thu hoi: tao ticket, hoi trang thai ticket, tim tai lieu noi bo, hoac resolve ticket qua lenh tu
        nhien.
      </p>
      <label>Message</label>
      <textarea value={message} onChange={(e) => setMessage(e.target.value)} />
      <div className="actions">
        <button className="primary" onClick={send} disabled={loading}>
          {loading ? 'Dang gui...' : 'Gui'}
        </button>
      </div>
      <h3>Reply</h3>
      <div className="chat-box" role="status" aria-live="polite">
        {reply || '(chua co phan hoi)'}
      </div>
    </div>
  );
}

export default App;
