import { useEffect, useSyncExternalStore } from 'react';
import type { HardwareSnapshot } from '../lib/types';

/**
 * Latest hardware reading, pushed by the PC every ~2 s, but only while something on screen needs it
 * (the Hardware Monitor or a sensor button). Components call useHardware(); the first one turns the
 * feed on, the last one to unmount turns it off, so a phone showing only buttons costs nothing.
 */
let latest: HardwareSnapshot | null = null;
let watchers = 0;
let sendSubscription: ((on: boolean) => void) | null = null;
const listeners = new Set<() => void>();

export const hardwareStore = {
  set(snapshot: HardwareSnapshot) {
    latest = snapshot;
    listeners.forEach((l) => l());
  },
  get: () => latest,
  watched: () => watchers > 0,
  subscribe(listener: () => void) {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
  /** The socket registers how to (un)subscribe; called again after it reconnects. */
  connect(send: ((on: boolean) => void) | null) {
    sendSubscription = send;
  },
  watch(): () => void {
    if (watchers++ === 0) sendSubscription?.(true);
    return () => {
      if (--watchers === 0) sendSubscription?.(false);
    };
  },
};

/** Live hardware readings while the calling component is mounted (and `enabled`). */
export function useHardware(enabled = true): HardwareSnapshot | null {
  useEffect(() => (enabled ? hardwareStore.watch() : undefined), [enabled]);
  return useSyncExternalStore(hardwareStore.subscribe, hardwareStore.get);
}
