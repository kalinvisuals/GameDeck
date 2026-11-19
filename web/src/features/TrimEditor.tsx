import { useCallback, useEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Play, RotateCcw, Square } from 'lucide-react';
import type { Sound } from '../lib/types';
import { Button } from '../ui/controls';
import { formatDuration } from './SoundPad';

/** Shortest selection the server accepts (CachedSound.MinTrimMs). */
const MIN_MS = 50;
const BUCKETS = 800;

type Peaks = { min: Float32Array; max: Float32Array };
type Load = { kind: 'loading' } | { kind: 'ready'; buffer: AudioBuffer; peaks: Peaks } | { kind: 'failed' };

/** Played length of a sound after trimming. */
export const playedMs = (s: Sound) => (s.trimEndMs || s.durationMs) - s.trimStartMs;

const seconds = (ms: number) => (ms / 1000).toFixed(2);

function computePeaks(buffer: AudioBuffer): Peaks {
  const min = new Float32Array(BUCKETS);
  const max = new Float32Array(BUCKETS);
  const per = buffer.length / BUCKETS;
  for (let c = 0; c < Math.min(buffer.numberOfChannels, 2); c++) {
    const data = buffer.getChannelData(c);
    for (let b = 0; b < BUCKETS; b++) {
      const from = Math.floor(b * per);
      const to = Math.max(from + 1, Math.floor((b + 1) * per));
      let lo = min[b];
      let hi = max[b];
      for (let i = from; i < to && i < data.length; i++) {
        const v = data[i];
        if (v < lo) lo = v;
        if (v > hi) hi = v;
      }
      min[b] = lo;
      max[b] = hi;
    }
  }
  return { min, max };
}

/**
 * Picks the part of the file a button plays. The waveform is decoded in the browser from the
 * imported file; "Listen to selection" plays on this device only, never into the call.
 */
export function TrimEditor({ sound, onCommit }: { sound: Sound; onCommit(startMs: number, endMs: number): void }) {
  const { t } = useTranslation();
  const total = Math.max(sound.durationMs, MIN_MS);
  const [range, setRange] = useState<[number, number]>([sound.trimStartMs, sound.trimEndMs || total]);
  const [load, setLoad] = useState<Load>({ kind: 'loading' });
  const [playhead, setPlayhead] = useState<number | null>(null);
  const [start, end] = range;

  const ctx = useRef<AudioContext | null>(null);
  const source = useRef<AudioBufferSourceNode | null>(null);
  const frame = useRef(0);
  const track = useRef<HTMLDivElement>(null);
  const canvas = useRef<HTMLCanvasElement>(null);
  const drag = useRef<'start' | 'end' | null>(null);
  const committed = useRef(range);

  // Follow changes saved elsewhere (another device) while not dragging.
  useEffect(() => {
    if (drag.current) return;
    const next: [number, number] = [sound.trimStartMs, sound.trimEndMs || total];
    committed.current = next;
    setRange(next);
  }, [sound.trimStartMs, sound.trimEndMs, total]);

  const audio = () => (ctx.current ??= new AudioContext());

  useEffect(() => {
    let cancelled = false;
    setLoad({ kind: 'loading' });
    fetch(`/api/sounds/${sound.id}/audio`, { credentials: 'same-origin' })
      .then((r) => (r.ok ? r.arrayBuffer() : Promise.reject(new Error(String(r.status)))))
      .then((bytes) => audio().decodeAudioData(bytes))
      .then((buffer) => !cancelled && setLoad({ kind: 'ready', buffer, peaks: computePeaks(buffer) }))
      .catch(() => !cancelled && setLoad({ kind: 'failed' }));
    return () => {
      cancelled = true;
    };
  }, [sound.id]);

  const stop = useCallback(() => {
    cancelAnimationFrame(frame.current);
    try {
      source.current?.stop();
    } catch {
      /* already ended */
    }
    source.current = null;
    setPlayhead(null);
  }, []);

  useEffect(
    () => () => {
      stop();
      void ctx.current?.close();
    },
    [stop],
  );

  // Waveform: real decoded peaks, brighter inside the selection.
  useEffect(() => {
    const el = canvas.current;
    if (!el || load.kind !== 'ready') return;
    const draw = () => {
      const dpr = window.devicePixelRatio || 1;
      const w = el.clientWidth;
      const h = el.clientHeight;
      el.width = Math.round(w * dpr);
      el.height = Math.round(h * dpr);
      const g = el.getContext('2d');
      if (!g || w === 0) return;
      g.setTransform(dpr, 0, 0, dpr, 0, 0);
      g.clearRect(0, 0, w, h);
      const css = getComputedStyle(el);
      const on = css.getPropertyValue('--wave-on').trim() || '#a9d6ff';
      const off = css.getPropertyValue('--wave-off').trim() || '#7c838f';
      const { min, max } = load.peaks;
      const mid = h / 2;
      const a = (start / total) * w;
      const b = (end / total) * w;
      for (let x = 0; x < w; x++) {
        const i = Math.min(BUCKETS - 1, Math.floor((x / w) * BUCKETS));
        const top = Math.max(1, -min[i] * mid * 0.92);
        const bottom = Math.max(1, max[i] * mid * 0.92);
        g.fillStyle = x >= a && x <= b ? on : off;
        g.fillRect(x, mid - bottom, 1, top + bottom);
      }
    };
    draw();
    const ro = new ResizeObserver(draw);
    ro.observe(el);
    return () => ro.disconnect();
  }, [load, start, end, total]);

  const commit = (next: [number, number]) => {
    const [s, e] = next;
    if (s === committed.current[0] && e === committed.current[1]) return;
    committed.current = next;
    onCommit(Math.round(s), e >= total ? 0 : Math.round(e));
  };

  const clamp = (which: 'start' | 'end', ms: number, current: [number, number]): [number, number] =>
    which === 'start'
      ? [Math.min(Math.max(0, ms), current[1] - MIN_MS), current[1]]
      : [current[0], Math.max(Math.min(total, ms), current[0] + MIN_MS)];

  const msAt = (clientX: number) => {
    const r = track.current!.getBoundingClientRect();
    return ((clientX - r.left) / r.width) * total;
  };

  const onPointerDown = (e: PointerEvent<HTMLDivElement>) => {
    if (e.button !== 0) return;
    const ms = msAt(e.clientX);
    const which = Math.abs(ms - start) <= Math.abs(ms - end) ? 'start' : 'end';
    drag.current = which;
    e.currentTarget.setPointerCapture(e.pointerId);
    stop();
    setRange((r) => clamp(which, ms, r));
  };
  const onPointerMove = (e: PointerEvent<HTMLDivElement>) => {
    const which = drag.current;
    if (which) setRange((r) => clamp(which, msAt(e.clientX), r));
  };
  const onPointerUp = () => {
    if (!drag.current) return;
    drag.current = null;
    commit(range);
  };

  const onHandleKey = (which: 'start' | 'end') => (e: KeyboardEvent) => {
    const step = e.shiftKey ? 100 : 10;
    const delta = e.key === 'ArrowLeft' || e.key === 'ArrowDown' ? -step : e.key === 'ArrowRight' || e.key === 'ArrowUp' ? step : 0;
    if (!delta) return;
    e.preventDefault();
    const next = clamp(which, (which === 'start' ? start : end) + delta, range);
    setRange(next);
    commit(next);
  };

  const onField = (which: 'start' | 'end', text: string) => {
    const v = Number(text.replace(',', '.'));
    if (!Number.isFinite(v)) return setRange(committed.current);
    const next = clamp(which, v * 1000, range);
    setRange(next);
    commit(next);
  };

  const play = async () => {
    if (load.kind !== 'ready') return;
    stop();
    const ac = audio();
    await ac.resume();
    const node = ac.createBufferSource();
    node.buffer = load.buffer;
    const gain = ac.createGain();
    gain.gain.value = sound.volume;
    node.connect(gain).connect(ac.destination);
    const from = start / 1000;
    const length = Math.max(0, Math.min(end / 1000, load.buffer.duration) - from);
    const began = ac.currentTime;
    node.onended = () => source.current === node && stop();
    node.start(0, from, length);
    source.current = node;
    const tick = () => {
      setPlayhead(start + (ac.currentTime - began) * 1000);
      frame.current = requestAnimationFrame(tick);
    };
    tick();
  };

  const pct = (ms: number) => `${(ms / total) * 100}%`;
  const trimmed = start > 0 || end < total;

  return (
    <div className="trim">
      <div
        ref={track}
        className={`trim-track ${load.kind}`}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerUp}
      >
        {load.kind === 'ready' ? (
          <canvas ref={canvas} className="trim-wave" aria-hidden />
        ) : (
          <span className="trim-status faint small">{load.kind === 'loading' ? t('trim.loading') : t('trim.noWave')}</span>
        )}
        <span className="trim-shade" style={{ left: 0, width: pct(start) }} aria-hidden />
        <span className="trim-shade" style={{ left: pct(end), right: 0 }} aria-hidden />
        <span className="trim-sel" style={{ left: pct(start), width: pct(end - start) }} aria-hidden />
        {(['start', 'end'] as const).map((which) => {
          const value = which === 'start' ? start : end;
          return (
            <span
              key={which}
              className={`trim-handle is-${which}`}
              style={{ left: pct(value) }}
              role="slider"
              tabIndex={0}
              aria-label={t(`trim.${which}`)}
              aria-valuemin={0}
              aria-valuemax={total}
              aria-valuenow={Math.round(value)}
              aria-valuetext={formatDuration(value)}
              onKeyDown={onHandleKey(which)}
            />
          );
        })}
        {playhead !== null && <span className="trim-playhead" style={{ left: pct(Math.min(playhead, end)) }} aria-hidden />}
      </div>

      <div className="trim-row">
        <label className="trim-field">
          <span className="faint small">{t('trim.start')}</span>
          <input
            key={`s${start}`}
            className="input num"
            inputMode="decimal"
            defaultValue={seconds(start)}
            onBlur={(e) => onField('start', e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && e.currentTarget.blur()}
          />
        </label>
        <label className="trim-field">
          <span className="faint small">{t('trim.end')}</span>
          <input
            key={`e${end}`}
            className="input num"
            inputMode="decimal"
            defaultValue={seconds(end)}
            onBlur={(e) => onField('end', e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && e.currentTarget.blur()}
          />
        </label>
        <span className="trim-length small num">{t('trim.length', { value: formatDuration(end - start) })}</span>
      </div>

      <div className="btn-row">
        {playhead === null ? (
          <Button size="sm" icon={<Play size={15} />} disabled={load.kind !== 'ready'} onClick={() => void play()}>
            {t('trim.play')}
          </Button>
        ) : (
          <Button size="sm" icon={<Square size={14} />} onClick={stop}>
            {t('trim.stop')}
          </Button>
        )}
        <Button
          size="sm"
          variant="ghost"
          icon={<RotateCcw size={15} />}
          disabled={!trimmed}
          onClick={() => {
            const next: [number, number] = [0, total];
            setRange(next);
            commit(next);
          }}
        >
          {t('trim.reset')}
        </Button>
      </div>
    </div>
  );
}
