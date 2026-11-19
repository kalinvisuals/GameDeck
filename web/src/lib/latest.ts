/**
 * Sends only the newest value per key: one request in flight, and values that arrive meanwhile are
 * merged into a single follow-up. A slider drag or a quick series of edits then ends exactly on the
 * last value, instead of overlapping requests finishing out of order.
 */
export function latestSender<T>(merge: (queued: T, next: T) => T) {
  type Job = { value: T; perform(v: T): Promise<unknown> };
  const slots = new Map<string, { busy: boolean; next?: Job }>();
  const go = (key: string, job: Job) => {
    const slot = slots.get(key)!;
    slot.busy = true;
    job.perform(job.value).finally(() => {
      const next = slot.next;
      slot.next = undefined;
      if (next) go(key, next);
      else slot.busy = false;
    });
  };
  return {
    send(key: string, value: T, perform: (v: T) => Promise<unknown>) {
      const slot = slots.get(key) ?? { busy: false };
      slots.set(key, slot);
      if (!slot.busy) go(key, { value, perform });
      else slot.next = { value: slot.next ? merge(slot.next.value, value) : value, perform };
    },
    busy: (key: string) => slots.get(key)?.busy ?? false,
  };
}
