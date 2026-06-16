export type View = 'auth' | 'employee' | 'queue' | 'detail' | 'knowledge' | 'chat';

export type ChatMessage = {
  role: 'user' | 'assistant';
  text: string;
};
