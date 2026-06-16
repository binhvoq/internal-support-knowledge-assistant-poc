import { useLayoutEffect, useState } from 'react';
import './App.css';
import { clearAccessTokenProvider, setAccessTokenProvider } from './api';
import { useAuth } from './auth/AuthContext';
import { apiScope } from './auth/msalConfig';
import { AppLayout } from './components/AppLayout';
import type { View } from './types';
import { ChatView } from './views/ChatView';
import { DetailView } from './views/DetailView';
import { EmployeeView } from './views/EmployeeView';
import { KnowledgeView } from './views/KnowledgeView';
import { LoginView } from './views/LoginView';
import { QueueView } from './views/QueueView';
import { TicketLookupView } from './views/TicketLookupView';

function App() {
  const [view, setView] = useState<View>('auth');
  const [selectedTicketId, setSelectedTicketId] = useState<string | null>(null);
  const { account, configured, getAccessToken } = useAuth();

  useLayoutEffect(() => {
    if (!configured) {
      clearAccessTokenProvider();
      return;
    }
    setAccessTokenProvider(() => getAccessToken(apiScope ? [apiScope] : undefined));
  }, [configured, getAccessToken, account]);

  const navigate = (nextView: View) => {
    if (nextView !== 'detail') setSelectedTicketId(null);
    setView(nextView);
  };

  return (
    <AppLayout
      activeView={view}
      userName={account?.name}
      onNavigate={navigate}
    >
      {view === 'auth' && <LoginView />}
      {view === 'employee' && <EmployeeView />}
      {view === 'queue' && (
        <QueueView
          onSelect={(id) => {
            setSelectedTicketId(id);
            setView('detail');
          }}
        />
      )}
      {view === 'detail' &&
        (selectedTicketId ? (
          <DetailView ticketId={selectedTicketId} onBack={() => setView('queue')} />
        ) : (
          <TicketLookupView
            onSelect={(id) => {
              setSelectedTicketId(id);
              setView('detail');
            }}
          />
        ))}
      {view === 'knowledge' && <KnowledgeView />}
      {view === 'chat' && <ChatView />}
    </AppLayout>
  );
}

export default App;
