import { useCallback, useEffect, useRef, useState, type PointerEvent } from 'react';

interface DragState {
  id: string;
  pointerId: number;
  x: number;
  y: number;
  active: boolean;
}

const THRESHOLD = 6;

function moveTo(list: string[], id: string, targetId: string): string[] {
  const from = list.indexOf(id);
  const to = list.indexOf(targetId);
  if (from < 0 || to < 0 || from === to) return list;
  const next = [...list];
  next.splice(from, 1);
  next.splice(to, 0, id);
  return next;
}

/**
 * Pointer-based reordering for the pad grid (mouse, pen and touch).
 * Mouse can drag a whole pad; touch must start on the grip so normal swipes still scroll.
 * A drag never counts as a click, so the editor only opens on a genuine tap.
 */
export function useReorder(layout: string[], enabled: boolean, commit: (next: string[]) => void) {
  const [order, setOrder] = useState<string[] | null>(null);
  const [draggingId, setDraggingId] = useState<string | null>(null);
  const drag = useRef<DragState | null>(null);
  const orderRef = useRef<string[]>(layout);
  const suppressClick = useRef(false);

  const current = order ?? layout;
  orderRef.current = current;

  // A fresh layout from the server replaces our local preview (unless a drag is in flight).
  useEffect(() => {
    if (!drag.current?.active) setOrder(null);
  }, [layout]);

  useEffect(() => {
    if (!enabled) return;
    const onMove = (e: globalThis.PointerEvent) => {
      const d = drag.current;
      if (!d || e.pointerId !== d.pointerId) return;
      if (!d.active) {
        if (Math.hypot(e.clientX - d.x, e.clientY - d.y) < THRESHOLD) return;
        d.active = true;
        setDraggingId(d.id);
        setOrder(orderRef.current);
      }
      e.preventDefault();
      const over = document.elementFromPoint(e.clientX, e.clientY)?.closest<HTMLElement>('[data-sound-id]');
      const targetId = over?.dataset.soundId;
      if (targetId && targetId !== d.id) setOrder((o) => moveTo(o ?? orderRef.current, d.id, targetId));
    };
    const onUp = (e: globalThis.PointerEvent) => {
      const d = drag.current;
      if (!d || e.pointerId !== d.pointerId) return;
      drag.current = null;
      if (!d.active) return;
      suppressClick.current = true;
      setDraggingId(null);
      const next = orderRef.current;
      if (next.join() !== layout.join()) commit(next);
      else setOrder(null);
    };
    window.addEventListener('pointermove', onMove, { passive: false });
    window.addEventListener('pointerup', onUp);
    window.addEventListener('pointercancel', onUp);
    return () => {
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      window.removeEventListener('pointercancel', onUp);
    };
  }, [enabled, layout, commit]);

  const onPointerDown = useCallback(
    (id: string, e: PointerEvent<HTMLElement>) => {
      if (!enabled || e.button !== 0) return;
      const onGrip = !!(e.target as HTMLElement).closest('[data-grip]');
      if (e.pointerType !== 'mouse' && !onGrip) return;
      if (onGrip) e.preventDefault();
      drag.current = { id, pointerId: e.pointerId, x: e.clientX, y: e.clientY, active: false };
    },
    [enabled],
  );

  /** Call at the start of a click handler; returns true when the click was really a drag end. */
  const consumeClick = useCallback(() => {
    const was = suppressClick.current;
    suppressClick.current = false;
    return was;
  }, []);

  return { order: current, draggingId, onPointerDown, consumeClick };
}
