import { useCallback, useEffect, useState } from 'react';

/** Per-device display preferences. Kept in this browser only; never sent to the PC. */
export interface Prefs {
  density: 'compact' | 'regular' | 'large';
  showDurations: boolean;
  reduceMotion: boolean;
  /** Animated Smart Deck buttons; off shows their first frame. */
  deckAnimations: boolean;
}

const KEY = 'gamedeck.prefs';
const DEFAULTS: Prefs = { density: 'regular', showDurations: true, reduceMotion: false, deckAnimations: true };
const listeners = new Set<(p: Prefs) => void>();

function load(): Prefs {
  try {
    const legacyDensity = localStorage.getItem('gamedeck.density') as Prefs['density'] | null;
    const raw = localStorage.getItem(KEY);
    return { ...DEFAULTS, ...(legacyDensity ? { density: legacyDensity } : {}), ...(raw ? JSON.parse(raw) : {}) };
  } catch {
    return DEFAULTS;
  }
}

let current = load();

function apply(p: Prefs) {
  document.documentElement.dataset.motion = p.reduceMotion ? 'reduced' : '';
}
apply(current);

export function usePrefs(): [Prefs, (patch: Partial<Prefs>) => void] {
  const [prefs, setPrefs] = useState(current);
  useEffect(() => {
    listeners.add(setPrefs);
    return () => void listeners.delete(setPrefs);
  }, []);
  const update = useCallback((patch: Partial<Prefs>) => {
    current = { ...current, ...patch };
    apply(current);
    try {
      localStorage.setItem(KEY, JSON.stringify(current));
    } catch {
      /* storage unavailable: keep for this session */
    }
    listeners.forEach((l) => l(current));
  }, []);
  return [prefs, update];
}
