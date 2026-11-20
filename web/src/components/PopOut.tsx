import { useEffect, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';

/**
 * Shows its children in a browser window of their own, which can be moved to another screen. They are still part of the app, so they follow its state.
 * When the browser will not open a window (a pop-up blocker), the children are shown in a panel on the right of the page instead, so nothing is out of
 * reach. `onClose` is called when the window or the panel is closed.
 */
export default function PopOut({ title, width, height, onClose, children }: {
  title: string; width: number; height: number; onClose: () => void; children: ReactNode;
}) {
  const [host, setHost] = useState<HTMLElement | null>(null);
  const [inline, setInline] = useState(false);

  useEffect(() => {
    const win = window.open('', '_blank', `popup,width=${width},height=${height}`);
    if (!win) { setInline(true); return; }
    win.document.title = title;
    // the window starts empty, so it is given the page's styles
    document.head.querySelectorAll('link[rel=stylesheet], style').forEach((node) => win.document.head.appendChild(node.cloneNode(true)));
    win.document.body.className = 'bg-ink-900 text-slate-200';
    const root = win.document.createElement('div');
    root.style.cssText = 'height:100vh;overflow-y:auto';
    win.document.body.appendChild(root);
    setHost(root);
    const closeWith = () => win.close(); // the window goes when this page does
    win.addEventListener('pagehide', onClose);
    window.addEventListener('pagehide', closeWith);
    return () => { win.removeEventListener('pagehide', onClose); window.removeEventListener('pagehide', closeWith); win.close(); setHost(null); };
  }, []);

  // the browser would not open a window: the same thing in a panel on the page
  if (inline) {
    return createPortal(
      <div role="dialog" aria-label={title} className="fixed inset-y-0 right-0 z-50 flex max-w-full flex-col border-l border-white/10 bg-ink-900 text-slate-200 shadow-2xl shadow-black/70" style={{ width }}>
        <div className="flex items-center justify-between gap-2 border-b border-white/5 px-3 py-2">
          <span className="text-[11px] text-slate-400">{title}. The browser blocked the pop-up window, so it is shown here.</span>
          <button type="button" className="chip" onClick={onClose}>Close</button>
        </div>
        <div className="min-h-0 flex-1 overflow-y-auto">{children}</div>
      </div>, document.body);
  }
  return host ? createPortal(children, host) : null;
}
