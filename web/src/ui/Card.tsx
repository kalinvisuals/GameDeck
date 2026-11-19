import type { ReactNode } from 'react';

export function Card(props: { title?: ReactNode; eyebrow?: ReactNode; actions?: ReactNode; children?: ReactNode; className?: string; pad?: boolean }) {
  const hasHead = props.title || props.eyebrow || props.actions;
  return (
    <section className={`card ${props.pad === false ? 'card-flush' : ''} ${props.className ?? ''}`}>
      {hasHead && (
        <header className="card-head">
          <div className="card-titles">
            {props.eyebrow && <span className="eyebrow">{props.eyebrow}</span>}
            {props.title && <h3>{props.title}</h3>}
          </div>
          {props.actions && <div className="card-actions">{props.actions}</div>}
        </header>
      )}
      {props.children}
    </section>
  );
}

export function EmptyState(props: { icon: ReactNode; title: string; children?: ReactNode; actions?: ReactNode; compact?: boolean }) {
  return (
    <div className={`empty ${props.compact ? 'empty-compact' : ''}`}>
      <div className="empty-mark" aria-hidden>
        {props.icon}
      </div>
      <h2>{props.title}</h2>
      {props.children && <p className="muted">{props.children}</p>}
      {props.actions && <div className="empty-actions">{props.actions}</div>}
    </div>
  );
}

export function PageHeader({ title, subtitle, actions }: { title: string; subtitle?: ReactNode; actions?: ReactNode }) {
  return (
    <header className="page-head">
      <div>
        <h1>{title}</h1>
        {subtitle && <p className="muted small">{subtitle}</p>}
      </div>
      {actions && <div className="page-actions">{actions}</div>}
    </header>
  );
}
