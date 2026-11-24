import { useSyncExternalStore } from 'react';
import type { PresenceState } from '../lib/types';

let latest: PresenceState | null = null;
const listeners = new Set<() => void>();

export const presenceStore = {
  set(state: PresenceState) {
    latest = state;
    listeners.forEach((l) => l());
  },
  get: () => latest,
  subscribe(listener: () => void) {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
};

export const usePresence = () => useSyncExternalStore(presenceStore.subscribe, presenceStore.get);
