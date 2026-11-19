import { useEffect, useRef, type ReactNode } from 'react';
import { AlertTriangle, Search, X } from 'lucide-react';
import { levelStore, type LevelChannel } from '../state/levels';
import { Button } from './controls';
import { useTranslation } from 'react-i18next';

export function Chip(props: { active?: boolean; onClick(): void; children: ReactNode; count?: number; icon?: ReactNode }) {
  return (
    <button type="button" className={`chip ${props.active ? 'is-active' : ''}`} aria-pressed={props.active} onClick={props.onClick}>
      {props.icon}
      <span>{props.children}</span>
      {props.count !== undefined && <span className="chip-count num">{props.count}</span>}
    </button>
  );
}

export function SearchField({ value, onChange, placeholder }: { value: string; onChange(v: string): void; placeholder: string }) {
  const { t } = useTranslation();
  const input = useRef<HTMLInputElement>(null);
  // "/" focuses search on desktop, like most pro tools.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement;
      if (e.key === '/' && t.tagName !== 'INPUT' && t.tagName !== 'TEXTAREA') {
        e.preventDefault();
        input.current?.focus();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  return (
    <label className="search">
      <Search size={16} strokeWidth={1.75} aria-hidden />
      <input
        ref={input}
        type="search"
        value={value}
        placeholder={placeholder}
        aria-label={placeholder}
        enterKeyHint="search"
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={(e) => e.key === 'Escape' && onChange('')}
      />
      {value ? (
        <button type="button" className="search-clear" aria-label={t('ui.clearSearch')} onClick={() => onChange('')}>
          <X size={14} />
        </button>
      ) : (
        <kbd className="search-kbd only-wide" aria-hidden>/</kbd>
      )}
    </label>
  );
}

export function Skeleton({ height = 16, width = '100%', radius }: { height?: number | string; width?: number | string; radius?: number }) {
  return <span className="skeleton" style={{ height, width, borderRadius: radius }} aria-hidden />;
}

export function ErrorState({ title, children, onRetry }: { title: string; children?: ReactNode; onRetry?(): void }) {
  const { t } = useTranslation();
  return (
    <div className="error-state" role="alert">
      <AlertTriangle size={18} strokeWidth={1.75} />
      <div>
        <strong>{title}</strong>
        {children && <p className="muted small">{children}</p>}
      </div>
      {onRetry && <Button size="sm" onClick={onRetry}>{t('common.retry')}</Button>}
    </div>
  );
}

/**
 * Rolling history of one real engine level (WebSocket "levels" frames). Canvas, no React renders;
 * scrolls only while there is signal and parks itself after it has decayed to silence.
 */
export function LevelHistory({ channel, label, height = 56 }: { channel: LevelChannel; label: string; height?: number }) {
  const { t } = useTranslation();
  const canvas = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const el = canvas.current!;
    const ctx = el.getContext('2d')!;
    const styles = getComputedStyle(document.documentElement);
    const accent = styles.getPropertyValue('--accent').trim() || '#a9d6ff';
    const faint = 'rgba(255,255,255,0.06)';
    const BAR = 3, GAP = 2;
    let bars: number[] = [];
    let raf = 0;
    let lastPush = 0;

    const resize = () => {
      const dpr = Math.min(2, window.devicePixelRatio || 1);
      el.width = Math.round(el.clientWidth * dpr);
      el.height = Math.round(height * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      const count = Math.max(8, Math.floor(el.clientWidth / (BAR + GAP)));
      bars = [...new Array(Math.max(0, count - bars.length)).fill(0), ...bars].slice(-count);
      draw();
    };

    const draw = () => {
      const w = el.clientWidth;
      ctx.clearRect(0, 0, w, height);
      bars.forEach((v, i) => {
        const x = w - (bars.length - i) * (BAR + GAP);
        const h = Math.max(2, v * (height - 4));
        ctx.fillStyle = v > 0.02 ? accent : faint;
        ctx.globalAlpha = v > 0.02 ? 0.35 + 0.65 * (i / bars.length) : 1;
        ctx.fillRect(x, (height - h) / 2, BAR, h);
      });
      ctx.globalAlpha = 1;
    };

    const toUnit = (p: number) => (p <= 0 ? 0 : Math.max(0, Math.min(1, (20 * Math.log10(p) + 60) / 60)));

    const tick = (now: number) => {
      if (now - lastPush >= 50) {
        lastPush = now;
        bars.push(toUnit(levelStore.get()[channel]));
        bars.shift();
        draw();
      }
      if (bars.every((v) => v === 0)) {
        raf = 0; // idle until the next level frame
        return;
      }
      raf = requestAnimationFrame(tick);
    };
    const wake = () => {
      if (!raf) raf = requestAnimationFrame(tick);
    };

    const ro = new ResizeObserver(resize);
    ro.observe(el);
    resize();
    const off = levelStore.subscribe(wake);
    return () => {
      off();
      ro.disconnect();
      cancelAnimationFrame(raf);
    };
  }, [channel, height]);

  return <canvas ref={canvas} className="level-history" style={{ height }} role="img" aria-label={t('common.levelHistory', { name: label })} />;
}
