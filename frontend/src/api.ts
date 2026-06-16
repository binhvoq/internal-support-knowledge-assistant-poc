const ticketBase = '/api/tickets';
const knowledgeBase = '/api/knowledge';
const aiBase = '/api/ai';

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
  /** Deprecated — luon false; giu cho tuong thich API */
  autoSuggestionNotifyFailed?: boolean;
};

export type KnowledgeDocument = {
  id: string;
  title: string;
  category: string;
  content: string;
  sourceUrl?: string;
  fileName?: string;
  contentType?: string;
  ingestionStatus: string;
  ingestionMessage?: string;
  ingestedAt?: string;
  updatedAt: string;
};

let tokenProvider: (() => Promise<string | null>) | null = null;

/** Gan ham lay Bearer token (MSAL) — dung sau khi co AuthProvider */
export function setAccessTokenProvider(fn: () => Promise<string | null>) {
  tokenProvider = fn;
}

export function clearAccessTokenProvider() {
  tokenProvider = null;
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const isFormData = init?.body instanceof FormData;
  const headers: Record<string, string> = isFormData ? {} : { 'Content-Type': 'application/json' };
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
    request<Ticket>(ticketBase, { method: 'POST', body: JSON.stringify(body) }),
  listMyTickets: () => request<Ticket[]>(`${ticketBase}/mine`),
  listTickets: (status?: string, category?: string) => {
    const params = new URLSearchParams();
    if (status) params.set('status', status);
    if (category) params.set('category', category);
    const q = params.toString();
    return request<Ticket[]>(`${ticketBase}${q ? `?${q}` : ''}`);
  },
  getTicket: (id: string) => request<Ticket>(`${ticketBase}/${id}`),
  resolveTicket: (id: string, finalAnswer?: string) =>
    request<Ticket>(`${ticketBase}/${id}/resolve`, {
      method: 'POST',
      body: JSON.stringify({ finalAnswer }),
    }),
  reopenTicket: (id: string) =>
    request<Ticket>(`${ticketBase}/${id}/reopen`, { method: 'POST' }),
  patchTicket: (id: string, body: { status: string; finalAnswer?: string }) =>
    request<Ticket>(`${ticketBase}/${id}`, { method: 'PATCH', body: JSON.stringify(body) }),
  listDocuments: () => request<KnowledgeDocument[]>(`${knowledgeBase}/documents`),
  createDocument: (body: { title: string; category: string; content: string; sourceUrl?: string }) =>
    request<KnowledgeDocument>(`${knowledgeBase}/documents`, { method: 'POST', body: JSON.stringify(body) }),
  uploadPdfDocument: (body: { file: File; title?: string; category?: string }) => {
    const form = new FormData();
    form.append('file', body.file);
    if (body.title) form.append('title', body.title);
    if (body.category) form.append('category', body.category);
    return request<KnowledgeDocument>(`${knowledgeBase}/documents/upload-pdf`, { method: 'POST', body: form });
  },
  deleteDocument: (id: string) =>
    request<{ status: string; documentId: string }>(`${knowledgeBase}/documents/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  reindex: () => request<{ status: string; documentCount: number }>(`${knowledgeBase}/documents/reindex`, { method: 'POST' }),
  reindexStatus: () => request<{ status: string; lastError?: string }>(`${knowledgeBase}/documents/reindex-status`),
  chat: (message: string) => request<{ reply: string }>(`${aiBase}/chat`, { method: 'POST', body: JSON.stringify({ message }) }),
  getAutoSuggestionJob: (ticketId: string) =>
    request<{ jobId: string; ticketId: string; status: string; failureReason?: string; discardReason?: string }>(
      `${aiBase}/tickets/${encodeURIComponent(ticketId)}/auto-suggestion`
    ).catch(() => null),
};
