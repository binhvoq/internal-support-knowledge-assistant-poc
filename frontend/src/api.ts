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

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...init?.headers },
  });
  if (!res.ok) {
    const err = await res.text();
    throw new Error(err || res.statusText);
  }
  return res.json() as Promise<T>;
}

export const api = {
  createTicket: (body: { employeeId: string; question: string; category?: string }) =>
    request<Ticket>(`${ticketBase}/tickets`, { method: 'POST', body: JSON.stringify(body) }),
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
