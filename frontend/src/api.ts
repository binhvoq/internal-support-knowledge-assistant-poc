const ticketBase = import.meta.env.VITE_TICKET_API ?? 'http://localhost:5001';
const knowledgeBase = import.meta.env.VITE_KNOWLEDGE_API ?? 'http://localhost:5002';
const aiBase = import.meta.env.VITE_AI_API ?? 'http://localhost:5003';

export type Ticket = {
  id: string;
  employeeId: string;
  category: string;
  question: string;
  status: string;
  aiSuggestedAnswer?: string;
  finalAnswer?: string;
  relatedDocuments: { documentId: string; title: string; score: number }[];
  createdAt: string;
  updatedAt: string;
  hasAiSuggestion: boolean;
};

export type KnowledgeDocument = {
  id: string;
  title: string;
  category: string;
  content: string;
  sourceUrl?: string;
  updatedAt: string;
};

let tokenProvider: (() => Promise<string | null>) | null = null;

/** Gan ham lay Bearer token (MSAL) — dung sau khi co AuthProvider */
export function setAccessTokenProvider(fn: () => Promise<string | null>) {
  tokenProvider = fn;
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (init?.headers) {
    const h = init.headers as Record<string, string>;
    Object.assign(headers, h);
  }
  if (tokenProvider) {
    const token = await tokenProvider();
    if (token) headers.Authorization = `Bearer ${token}`;
  }
  const res = await fetch(url, {
    ...init,
    headers,
  });
  if (!res.ok) {
    const err = await res.text();
    let msg = err || res.statusText;
    try {
      const body = JSON.parse(err) as { error?: string; title?: string };
      if (body.error) msg = body.error;
      else if (body.title) msg = body.title;
    } catch {
      /* plain text */
    }
    if (res.status === 401) {
      msg = `Chua dang nhap hoac token het han. Vao tab Entra / Login de lay Bearer token. (${msg})`;
    } else if (res.status === 403) {
      msg = `Khong du quyen Entra (403). Can role phu hop (Employee / Agent / KnowledgeAdmin). (${msg})`;
    } else {
      msg = `HTTP ${res.status}: ${msg}`;
    }
    throw new Error(msg);
  }
  return res.json() as Promise<T>;
}

export const api = {
  createTicket: (body: { employeeId: string; question: string; category?: string }) =>
    request<Ticket>(`${ticketBase}/tickets`, { method: 'POST', body: JSON.stringify(body) }),
  listMyTickets: () => request<Ticket[]>(`${ticketBase}/tickets/mine`),
  listTickets: (status?: string, category?: string) => {
    const params = new URLSearchParams();
    if (status) params.set('status', status);
    if (category) params.set('category', category);
    const q = params.toString();
    return request<Ticket[]>(`${ticketBase}/tickets${q ? `?${q}` : ''}`);
  },
  getTicket: (id: string) => request<Ticket>(`${ticketBase}/tickets/${id}`),
  resolveTicket: (id: string, finalAnswer?: string) =>
    request<Ticket>(`${ticketBase}/tickets/${id}/resolve`, {
      method: 'POST',
      body: JSON.stringify({ finalAnswer }),
    }),
  reopenTicket: (id: string) =>
    request<Ticket>(`${ticketBase}/tickets/${id}/reopen`, { method: 'POST' }),
  patchTicket: (id: string, body: Record<string, unknown>) =>
    request<Ticket>(`${ticketBase}/tickets/${id}`, { method: 'PATCH', body: JSON.stringify(body) }),
  listDocuments: () => request<KnowledgeDocument[]>(`${knowledgeBase}/documents`),
  createDocument: (body: { title: string; category: string; content: string; sourceUrl?: string }) =>
    request<KnowledgeDocument>(`${knowledgeBase}/documents`, { method: 'POST', body: JSON.stringify(body) }),
  reindex: () => request<{ status: string; documentCount: number }>(`${knowledgeBase}/documents/reindex`, { method: 'POST' }),
  reindexStatus: () => request<{ status: string; lastError?: string }>(`${knowledgeBase}/documents/reindex-status`),
  chat: (message: string) => request<{ reply: string }>(`${aiBase}/ai/chat`, { method: 'POST', body: JSON.stringify({ message }) }),
};
