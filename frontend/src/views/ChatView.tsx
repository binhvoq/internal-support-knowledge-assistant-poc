import { useState } from 'react';
import { api } from '../api';
import { AuthRequiredBanner } from '../auth/AuthRequiredBanner';
import { EmptyState } from '../components/EmptyState';
import { Icon } from '../components/Icon';
import { SectionCard } from '../components/SectionCard';
import type { ChatMessage } from '../types';

const suggestions = ['Tạo ticket IT', 'Kiểm tra trạng thái ticket', 'Tìm chính sách VPN', 'Hướng dẫn resolve ticket'];

export function ChatView() {
  const [message, setMessage] = useState('Ticket TK-2026-001 của tôi xử lý đến đâu rồi?');
  const [messages, setMessages] = useState<ChatMessage[]>([
    {
      role: 'assistant',
      text: 'Ticket TK-2026-001 đang ở trạng thái Suggested. AI đã tạo gợi ý trả lời và đang chờ support agent xác nhận.',
    },
  ]);
  const [loading, setLoading] = useState(false);

  const send = async (nextMessage = message) => {
    if (!nextMessage.trim()) return;
    setLoading(true);
    setMessages((items) => [...items, { role: 'user', text: nextMessage }]);
    setMessage('');
    try {
      const response = await api.chat(nextMessage);
      setMessages((items) => [...items, { role: 'assistant', text: response.reply }]);
    } catch (e) {
      setMessages((items) => [...items, { role: 'assistant', text: (e as Error).message }]);
    } finally {
      setLoading(false);
    }
  };

  return (
    <>
      <div className="copilot-heading">
        <h1>Support Copilot</h1>
        <p>Hỏi trạng thái ticket, tìm tài liệu nội bộ hoặc nhờ AI hỗ trợ xử lý ticket.</p>
      </div>
      <AuthRequiredBanner action="dùng Support Copilot" />
      <SectionCard className="chat-card">
        <div className="chat-welcome">
          <span className="bot-icon">
            <Icon name="sparkles" />
          </span>
          <h2>Xin chào, tôi có thể giúp gì cho bạn?</h2>
          <div className="suggestion-chips">
            {suggestions.map((item) => (
              <button type="button" key={item} onClick={() => void send(item)}>
                <Icon name={item.includes('ticket') ? 'plus' : item.includes('VPN') ? 'file' : 'sparkles'} />
                {item}
              </button>
            ))}
          </div>
        </div>
        <div className="chat-thread" role="log" aria-live="polite">
          {messages.length === 0 ? (
            <EmptyState title="Chưa có phản hồi chat" text="Nhập câu hỏi để bắt đầu trao đổi với Copilot." />
          ) : (
            messages.map((item, index) => (
              <div className={`chat-message ${item.role}`} key={`${item.role}-${index}`}>
                {item.role === 'assistant' && (
                  <span className="bot-avatar">
                    <Icon name="sparkles" />
                  </span>
                )}
                <div>
                  <p>{item.text}</p>
                  <time>10:32</time>
                </div>
              </div>
            ))
          )}
          {loading && (
            <div className="chat-message assistant">
              <span className="bot-avatar">
                <Icon name="sparkles" />
              </span>
              <div>
                <p>
                  Copilot đang trả lời... <span className="typing">•••</span>
                </p>
                <time>10:32</time>
              </div>
            </div>
          )}
        </div>
        <form
          className="chat-input"
          onSubmit={(e) => {
            e.preventDefault();
            void send();
          }}
        >
          <input value={message} onChange={(e) => setMessage(e.target.value)} placeholder="Nhập câu hỏi cho Copilot..." />
          <button className="primary" type="submit" disabled={loading}>
            <Icon name="send" /> Gửi
          </button>
        </form>
      </SectionCard>
    </>
  );
}
