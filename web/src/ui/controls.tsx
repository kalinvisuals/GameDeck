import type { ButtonHTMLAttributes, ReactNode, SelectHTMLAttributes } from 'react';

type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger';
type ButtonSize = 'sm' | 'md' | 'lg';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  icon?: ReactNode;
  block?: boolean;
}

export function Button({ variant = 'secondary', size = 'md', icon, block, className = '', children, ...rest }: ButtonProps) {
  return (
    <button className={`btn btn-${variant} btn-${size} ${block ? 'btn-block' : ''} ${className}`} {...rest}>
      {icon}
      {children && <span>{children}</span>}
    </button>
  );
}

interface IconButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  label: string;
  icon: ReactNode;
  active?: boolean;
}

export function IconButton({ label, icon, active, className = '', ...rest }: IconButtonProps) {
  return (
    <button aria-label={label} title={label} aria-pressed={active} className={`icon-btn ${active ? 'is-active' : ''} ${className}`} {...rest}>
      {icon}
    </button>
  );
}

export function Switch({ checked, onChange, label }: { checked: boolean; onChange(v: boolean): void; label: string }) {
  return (
    <button type="button" role="switch" aria-checked={checked} aria-label={label} className={`switch ${checked ? 'is-on' : ''}`} onClick={() => onChange(!checked)}>
      <span className="switch-thumb" />
    </button>
  );
}

export function Segmented<T extends string>(props: {
  value: T;
  options: { value: T; label: ReactNode }[];
  onChange(v: T): void;
  label: string;
  size?: 'sm' | 'md';
}) {
  return (
    <div role="radiogroup" aria-label={props.label} className={`segmented segmented-${props.size ?? 'md'}`}>
      {props.options.map((o) => (
        <button key={o.value} role="radio" aria-checked={o.value === props.value} className={o.value === props.value ? 'is-active' : ''} onClick={() => props.onChange(o.value)}>
          {o.label}
        </button>
      ))}
    </div>
  );
}

export function Select({ className = '', ...rest }: SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <div className={`select ${className}`}>
      <select {...rest} />
    </div>
  );
}

export function Field({ label, hint, children, inline }: { label: ReactNode; hint?: ReactNode; children: ReactNode; inline?: boolean }) {
  return (
    <div className={`field ${inline ? 'field-inline' : ''}`}>
      <div className="field-text">
        <span className="field-label">{label}</span>
        {hint && <span className="field-hint">{hint}</span>}
      </div>
      <div className="field-control">{children}</div>
    </div>
  );
}

export type Tone = 'ok' | 'warn' | 'bad' | 'off' | 'accent';

export function StatusDot({ tone, pulse, label }: { tone: Tone; pulse?: boolean; label?: string }) {
  return <span className={`dot dot-${tone} ${pulse ? 'dot-pulse' : ''}`} role={label ? 'img' : undefined} aria-label={label} aria-hidden={!label} />;
}

export function Badge({ tone = 'off', children }: { tone?: Tone; children: ReactNode }) {
  return <span className={`badge badge-${tone}`}>{children}</span>;
}
