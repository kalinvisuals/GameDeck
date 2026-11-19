import { useEffect, useRef } from 'react';
import { levelStore, type LevelChannel } from '../state/levels';

const FLOOR_DB = -60;
const toUnit = (linear: number) => {
  if (linear <= 0) return 0;
  const db = 20 * Math.log10(linear);
  return Math.min(1, Math.max(0, (db - FLOOR_DB) / -FLOOR_DB));
};

/**
 * Peak meter fed by real engine levels (WebSocket "levels" frames). Animates with rAF and writes
 * straight to the DOM, so 15 Hz updates never re-render React. When the meter has fallen back to
 * zero the loop stops entirely; the next level frame restarts it.
 */
export function Meter({ channel, active, label }: { channel: LevelChannel; active: boolean; label: string }) {
  const fill = useRef<HTMLSpanElement>(null);
  const hold = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    let raf = 0;
    let shown = 0;
    let peak = 0;
    let peakAt = 0;
    let last = performance.now();
    const tick = (now: number) => {
      const dt = (now - last) / 1000;
      last = now;
      const target = toUnit(levelStore.get()[channel]);
      shown = target > shown ? target : Math.max(target, shown - dt * 1.6); // fast attack, ~600 ms release
      if (shown >= peak) {
        peak = shown;
        peakAt = now;
      } else if (now - peakAt > 900) {
        peak = Math.max(shown, peak - dt * 0.8);
      }
      if (fill.current) {
        fill.current.style.transform = `scaleX(${shown})`;
        fill.current.dataset.hot = shown > 0.95 ? '1' : '';
      }
      if (hold.current) hold.current.style.transform = `translateX(${peak * 100}%)`;
      if (target === 0 && shown === 0 && peak === 0) {
        raf = 0; // idle: no frames until new levels arrive
        return;
      }
      raf = requestAnimationFrame(tick);
    };
    const wake = () => {
      if (raf) return;
      last = performance.now();
      raf = requestAnimationFrame(tick);
    };
    wake();
    const unsubscribe = levelStore.subscribe(wake);
    return () => {
      unsubscribe();
      cancelAnimationFrame(raf);
    };
  }, [channel]);

  return (
    <div className={`meter ${active ? '' : 'is-idle'}`} role="meter" aria-label={label} aria-valuemin={FLOOR_DB} aria-valuemax={0}>
      <span className="meter-track">
        <span ref={fill} className="meter-fill" />
        <span ref={hold} className="meter-hold" />
      </span>
    </div>
  );
}
