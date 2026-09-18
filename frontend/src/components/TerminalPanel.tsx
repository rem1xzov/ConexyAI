import { useEffect, useRef } from 'react';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';
import { signalrService } from '../services/signalrService';

interface TerminalPanelProps {
  sessionId: string;
}

/**
 * Interactive terminal backed by the server-side pty (SignalR hub). The pty is
 * keyed by sessionId, so it survives UI unmounts; we deliberately do NOT call
 * StopTerminal on unmount (only an explicit "close terminal" button would do that).
 */
export function TerminalPanel({ sessionId }: TerminalPanelProps) {
  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!containerRef.current) return;

    const term = new Terminal({
      theme: { background: '#1e1e1e' },
      fontSize: 13,
      cursorBlink: true,
      fontFamily: 'Consolas, "Courier New", monospace',
    });
    const fitAddon = new FitAddon();
    term.loadAddon(fitAddon);
    term.open(containerRef.current);
    fitAddon.fit();

    const unsubscribe = signalrService.onTerminalOutput(sessionId, (data) => {
      term.write(data);
    });

    const inputDisposable = term.onData((data) => {
      void signalrService.sendTerminalInput(sessionId, data);
    });

    // Join the terminal SignalR group (task_{chatId}) so this connection receives both
    // pty output and the agent's mirrored bash output in the same feed.
    void signalrService.joinTask(sessionId);
    void signalrService.startTerminal(sessionId);

    const handleResize = () => {
      fitAddon.fit();
      void signalrService.resizeTerminal(sessionId, term.cols, term.rows);
    };
    window.addEventListener('resize', handleResize);

    return () => {
      window.removeEventListener('resize', handleResize);
      unsubscribe();
      inputDisposable.dispose();
      term.dispose();
    };
  }, [sessionId]);

  return <div ref={containerRef} className="terminal-panel" />;
}
