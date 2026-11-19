import { useRef, useState, type CSSProperties } from 'react';

interface Props {
  value: number;
  min?: number;
  max: number;
  step?: number;
  label: string;
  /** Throttled while dragging (so changes are audible live) and once on release. */
  onChange(v: number): void;
  format?(v: number): string;
}

const percent = (v: number) => `${Math.round(v * 100)}%`;

export function Slider({ value, min = 0, max, step = 0.01, label, onChange, format = percent }: Props) {
  const [local, setLocal] = useState<number | null>(null);
  const lastSent = useRef(0);
  const shown = local ?? value;

  const send = (v: number, final: boolean) => {
    const now = performance.now();
    if (final || now - lastSent.current > 120) {
      lastSent.current = now;
      onChange(v);
    }
  };
  const release = () => {
    if (local !== null) send(local, true);
    setLocal(null);
  };

  return (
    <div className="slider">
      <input
        type="range"
        aria-label={label}
        min={min}
        max={max}
        step={step}
        value={shown}
        style={{ '--fill': `${((shown - min) / (max - min)) * 100}%` } as CSSProperties}
        onChange={(e) => {
          const v = Number(e.target.value);
          setLocal(v);
          send(v, false);
        }}
        onPointerUp={release}
        onKeyUp={release}
        onBlur={release}
      />
      <span className="slider-value num">{format(shown)}</span>
    </div>
  );
}
