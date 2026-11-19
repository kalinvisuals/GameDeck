export type LevelChannel = 'mic' | 'sounds' | 'monitor' | 'virtual';
export type Levels = Record<LevelChannel, number>;

const ZERO: Levels = { mic: 0, sounds: 0, monitor: 0, virtual: 0 };
/** Frames older than this are treated as silence (socket dropped or audio stopped). */
const STALE_MS = 400;

let current: Levels = ZERO;
let receivedAt = 0;
const listeners = new Set<() => void>();

/**
 * Outside React on purpose: meters read it from requestAnimationFrame. Meters stop animating once
 * they've decayed to silence and `subscribe` wakes them when the next frame arrives.
 */
export const levelStore = {
  set(levels: Partial<Levels>) {
    current = { ...ZERO, ...levels };
    receivedAt = performance.now();
    listeners.forEach((l) => l());
  },
  get(): Levels {
    return performance.now() - receivedAt > STALE_MS ? ZERO : current;
  },
  subscribe(listener: () => void): () => void {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
};

