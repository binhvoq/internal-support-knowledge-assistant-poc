export function Icon({ name }: { name: string }) {
  const common = {
    width: 22,
    height: 22,
    viewBox: '0 0 24 24',
    fill: 'none',
    stroke: 'currentColor',
    strokeWidth: 2,
    strokeLinecap: 'round' as const,
    strokeLinejoin: 'round' as const,
    'aria-hidden': true,
  };

  return (
    <svg {...common}>
      {name === 'headset' && (
        <>
          <path d="M4 12a8 8 0 0 1 16 0" />
          <path d="M4 12v4a2 2 0 0 0 2 2h2v-8H6a2 2 0 0 0-2 2Z" />
          <path d="M20 12v4a2 2 0 0 1-2 2h-2v-8h2a2 2 0 0 1 2 2Z" />
          <path d="M8 18c1 2 7 2 8 0" />
          <path d="M9 13h.01M12 13h.01M15 13h.01" />
        </>
      )}
      {name === 'lock' && (
        <>
          <rect x="5" y="10" width="14" height="10" rx="2" />
          <path d="M8 10V7a4 4 0 0 1 8 0v3" />
        </>
      )}
      {name === 'plus' && (
        <>
          <rect x="3" y="3" width="18" height="18" rx="3" />
          <path d="M12 8v8M8 12h8" />
        </>
      )}
      {name === 'users' && (
        <>
          <path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2" />
          <circle cx="9" cy="7" r="4" />
          <path d="M22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75" />
        </>
      )}
      {name === 'file' && (
        <>
          <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z" />
          <path d="M14 2v6h6M9 13h6M9 17h6" />
        </>
      )}
      {name === 'database' && (
        <>
          <ellipse cx="12" cy="5" rx="8" ry="3" />
          <path d="M4 5v14c0 1.66 3.58 3 8 3s8-1.34 8-3V5" />
          <path d="M4 12c0 1.66 3.58 3 8 3s8-1.34 8-3" />
        </>
      )}
      {name === 'sparkles' && (
        <>
          <path d="M12 3l1.5 4.5L18 9l-4.5 1.5L12 15l-1.5-4.5L6 9l4.5-1.5Z" />
          <path d="M19 15l.8 2.2L22 18l-2.2.8L19 21l-.8-2.2L16 18l2.2-.8Z" />
          <path d="M5 14l.7 1.8L8 16.5l-2.3.7L5 19l-.7-1.8L2 16.5l2.3-.7Z" />
        </>
      )}
      {name === 'calendar' && (
        <>
          <rect x="3" y="4" width="18" height="18" rx="2" />
          <path d="M16 2v4M8 2v4M3 10h18" />
        </>
      )}
      {name === 'check' && <path d="M20 6 9 17l-5-5" />}
      {name === 'copy' && (
        <>
          <rect x="9" y="9" width="13" height="13" rx="2" />
          <rect x="2" y="2" width="13" height="13" rx="2" />
        </>
      )}
      {name === 'upload' && (
        <>
          <path d="M12 16V4" />
          <path d="m7 9 5-5 5 5" />
          <path d="M20 16v4H4v-4" />
        </>
      )}
      {name === 'send' && <path d="m22 2-7 20-4-9-9-4Z" />}
    </svg>
  );
}
