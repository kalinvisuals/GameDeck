import { useSyncExternalStore } from 'react';
import type { YouTubeState } from '../lib/types';

export interface YouTubeView extends YouTubeState {
  /** When this snapshot arrived, so the progress bar can advance with the local clock. */
  receivedAt: number;
}

let latest: YouTubeView | null = null;

/** Keeps the previous list when nothing in it changed, so its rows are not drawn again. */
const sameList = <T>(next: T[], previous: T[] | undefined) =>
  previous && JSON.stringify(previous) === JSON.stringify(next) ? previous : next;
const listeners = new Set<() => void>();

export const youtubeStore = {
  set(state: YouTubeState) {
    latest = {
      ...state,
      results: sameList(state.results, latest?.results),
      upNext: sameList(state.upNext, latest?.upNext),
      receivedAt: performance.now(),
    };
    listeners.forEach((l) => l());
  },
  get: () => latest,
  subscribe(listener: () => void) {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
};

export const useYouTube = () => useSyncExternalStore(youtubeStore.subscribe, youtubeStore.get);
