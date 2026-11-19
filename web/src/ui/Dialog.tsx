import { useEffect, useRef, type ReactNode } from 'react';
import { X } from 'lucide-react';
import { IconButton } from './controls';
import { useTranslation } from 'react-i18next';

/** Bottom sheet on phones, centred dialog on larger screens. Esc and backdrop close it. */
export function Dialog({ title, onClose, children, footer }: { title: string; onClose(): void; children: ReactNode; footer?: ReactNode }) {
  const { t } = useTranslation();
  const panel = useRef<HTMLDivElement>(null);
  // Parents pass a new onClose on every render (each keystroke in a field re-renders them).
  // Keep the latest one in a ref so the focus/keyboard setup below runs only once, on open;
  // re-running it used to pull focus out of the text field after every letter.
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    panel.current?.focus();
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onCloseRef.current();
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('keydown', onKey);
      previous?.focus?.();
    };
  }, []);

  return (
    <div className="dialog-backdrop" onClick={() => onCloseRef.current()}>
      <div ref={panel} tabIndex={-1} className="dialog" role="dialog" aria-modal="true" aria-label={title} onClick={(e) => e.stopPropagation()}>
        <header className="dialog-head">
          <h2>{title}</h2>
          <IconButton label={t('common.close')} icon={<X size={18} />} onClick={() => onCloseRef.current()} />
        </header>
        <div className="dialog-body">{children}</div>
        {footer && <footer className="dialog-foot">{footer}</footer>}
      </div>
    </div>
  );
}
