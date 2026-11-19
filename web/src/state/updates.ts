import { useSyncExternalStore } from 'react';
import type { UpdatesState } from '../lib/types';

let latest: UpdatesState | null = null;
const listeners = new Set<() => void>();

export const updatesStore = {
  set(state: UpdatesState) {
    latest = state;
    listeners.forEach((l) => l());
  },
  get: () => latest,
  subscribe(listener: () => void) {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
};

export const useUpdates = () => useSyncExternalStore(updatesStore.subscribe, updatesStore.get);

/** Announcements already closed on this device. */
const READ_KEY = 'gamedeck.news.read';
let read: string[] = (() => {
  try {
    return JSON.parse(localStorage.getItem(READ_KEY) ?? '[]') as string[];
  } catch {
    return [];
  }
})();
const readListeners = new Set<() => void>();

export const newsRead = {
  get: () => read,
  mark(id: string) {
    read = [...read.filter((r) => r !== id), id].slice(-50);
    try {
      localStorage.setItem(READ_KEY, JSON.stringify(read));
    } catch {
      /* storage unavailable: closed for this visit only */
    }
    readListeners.forEach((l) => l());
  },
  subscribe(listener: () => void) {
    readListeners.add(listener);
    return () => readListeners.delete(listener);
  },
};

export const useNewsRead = () => useSyncExternalStore(newsRead.subscribe, newsRead.get);
