import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type PointerEvent as ReactPointerEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Check, Pencil, Plus, Trash2 } from 'lucide-react';
import { api, ApiError } from '../../lib/api';
import { latestSender } from '../../lib/latest';
import { SoundIcon } from '../../lib/icons';
import type { DeckButton, DeckProfile, HardwareSnapshot } from '../../lib/types';
import { useDeck } from '../../state/DeckContext';
import { usePresence } from '../../state/presence';
import { useHardware } from '../../state/hardware';
import { Button, Field, IconButton, Select } from '../../ui/controls';
import { EmptyState } from '../../ui/Card';
import { HOLD_MS, buttonState, describeAction, freeSlot, isActiveState, newId, sensorLevel, sensorText, shortLabel } from './actions';
import { DeckImage, useDeckMotion } from './DeckImage';
import { ButtonEditor } from './ButtonEditor';

const profileSender = latestSender<DeckProfile>((_, next) => next);
const MAX_ROWS = 8;
const MAX_COLUMNS = 10;
const MAX_PAGES = 10;

/**
 * The active deck profile, editable in place. Edits show immediately and are sent newest-first; the
 * PC's copy replaces the local one once nothing is waiting to be sent. A refused edit is rolled back
 * and the PC's reason is shown.
 */
export function useProfileDraft(profile: DeckProfile) {
  const { toast } = useDeck();
  const [draft, setDraft] = useState<DeckProfile | null>(null);
  useEffect(() => {
    if (!profileSender.busy(profile.id)) setDraft(null);
  }, [profile]);

  const save = useCallback(
    (next: DeckProfile) => {
      setDraft(next);
      profileSender.send(profile.id, next, (p) =>
        api.saveDeckProfile(p).catch((e) => {
          setDraft(null);
          toast(e instanceof Error ? e.message : String(e), 'error');
        }),
      );
    },
    [profile.id, toast],
  );

  /** Saves and waits, for the button editor: resolves to the PC's error message, or null. */
  const saveNow = useCallback(
    async (next: DeckProfile): Promise<string | null> => {
      try {
        await api.saveDeckProfile(next);
        setDraft(null);
        return null;
      } catch (e) {
        return e instanceof Error ? e.message : String(e);
      }
    },
    [],
  );

  return { profile: draft ?? profile, save, saveNow };
}

/** @param onOpenMonitor Sensor buttons set to "open the Hardware Monitor" call this. */
export function DeckGrid({ profile: stored, onOpenMonitor }: { profile: DeckProfile; onOpenMonitor?(): void }) {
  const { t } = useTranslation();
  const { state, playing } = useDeck();
  usePresence(); // Discord buttons show whether their activity is on the profile
  const { profile, save, saveNow } = useProfileDraft(stored);
  const [pageIndex, setPageIndex] = useState(0);
  const [editMode, setEditMode] = useState(false);
  const [editing, setEditing] = useState<DeckButton | null>(null);
  const page = profile.pages[Math.min(pageIndex, profile.pages.length - 1)];
  const capacity = profile.rows * profile.columns;
  const bySlot = useMemo(() => new Map(page.buttons.map((b) => [b.slot, b])), [page]);
  const playingSounds = useMemo(() => new Set(playing.map((p) => p.soundId)), [playing]);
  const hardware = useHardware(page.buttons.some((b) => b.action.type === 'sensor'));
  const motion = useDeckMotion();

  useEffect(() => {
    if (pageIndex >= profile.pages.length) setPageIndex(profile.pages.length - 1);
  }, [pageIndex, profile.pages.length]);

  const withPage = (buttons: DeckButton[], extra: Partial<DeckProfile> = {}): DeckProfile => ({
    ...profile,
    ...extra,
    pages: profile.pages.map((p) => (p.id === page.id ? { ...p, buttons } : p)),
  });

  const move = (id: string, slot: number) => {
    const from = page.buttons.find((b) => b.id === id);
    if (!from || from.slot === slot) return;
    const other = bySlot.get(slot);
    save(withPage(page.buttons.map((b) => (b.id === id ? { ...b, slot } : other && b.id === other.id ? { ...b, slot: from.slot } : b))));
  };

  // Buttons keep their slot when it still fits; the PC moves the rest to free slots, and refuses
  // (with the reason) a grid too small for a page's buttons instead of dropping any.
  const setGrid = (rows: number, columns: number) => save({ ...profile, rows, columns });

  const drag = useSlotDrag(editMode, move);

  return (
    <div className="deck-board">
      <div className="deck-toolbar">
        <div className="chips" role="tablist" aria-label={t('deck.pages')}>
          {profile.pages.map((p, i) => (
            <button key={p.id} role="tab" aria-selected={p.id === page.id} className={`chip ${p.id === page.id ? 'is-active' : ''}`} onClick={() => setPageIndex(i)}>
              {p.name || t('deck.pageN', { n: i + 1 })}
            </button>
          ))}
          {editMode && profile.pages.length < MAX_PAGES && (
            <button className="chip" aria-label={t('deck.addPage')} title={t('deck.addPage')} onClick={() => {
              save({ ...profile, pages: [...profile.pages, { id: newId(), name: '', buttons: [] }] });
              setPageIndex(profile.pages.length);
            }}>
              <Plus size={14} />
            </button>
          )}
        </div>
        <Button size="sm" variant={editMode ? 'primary' : 'secondary'} icon={editMode ? <Check size={15} /> : <Pencil size={15} />} onClick={() => setEditMode((v) => !v)}>
          {editMode ? t('common.done') : t('common.edit')}
        </Button>
      </div>

      {editMode && (
        <div className="deck-settings">
          <Field label={t('deck.pageName')}>
            <PageName key={page.id} value={page.name} placeholder={t('deck.pageN', { n: profile.pages.indexOf(page) + 1 })}
              onCommit={(name) => save({ ...profile, pages: profile.pages.map((p) => (p.id === page.id ? { ...p, name } : p)) })} />
          </Field>
          <Field label={t('deck.rows')}>
            <Select value={profile.rows} aria-label={t('deck.rows')} onChange={(e) => setGrid(Number(e.target.value), profile.columns)}>
              {Array.from({ length: MAX_ROWS }, (_, i) => <option key={i} value={i + 1}>{i + 1}</option>)}
            </Select>
          </Field>
          <Field label={t('deck.columns')}>
            <Select value={profile.columns} aria-label={t('deck.columns')} onChange={(e) => setGrid(profile.rows, Number(e.target.value))}>
              {Array.from({ length: MAX_COLUMNS }, (_, i) => <option key={i} value={i + 1}>{i + 1}</option>)}
            </Select>
          </Field>
          <IconButton label={t('deck.deletePage')} icon={<Trash2 size={16} />} disabled={profile.pages.length <= 1} onClick={() => {
            if (!confirm(t('deck.confirmDeletePage', { name: page.name || t('deck.pageN', { n: profile.pages.indexOf(page) + 1 }) }))) return;
            save({ ...profile, pages: profile.pages.filter((p) => p.id !== page.id) });
          }} />
        </div>
      )}

      {editMode && <p className="faint small deck-hint">{t('deck.editHint')}</p>}

      <div className={`deck-grid ${editMode ? 'is-editing' : ''} ${motion ? '' : 'no-motion'}`} style={{ '--cols': profile.columns } as CSSProperties}>
        {Array.from({ length: capacity }, (_, slot) => {
          const b = bySlot.get(slot);
          if (!b) {
            return editMode ? (
              <button key={`empty-${slot}`} data-slot={slot} className={`deck-key is-empty ${drag.target === slot ? 'is-target' : ''}`} aria-label={t('deck.addButton')}
                onClick={() => setEditing({ id: newId(), slot, label: '', icon: 'zap', color: '#a9d6ff', image: null, action: { type: 'none' } })}>
                <Plus size={18} />
              </button>
            ) : (
              <div key={`empty-${slot}`} data-slot={slot} className="deck-key is-blank" aria-hidden />
            );
          }
          return (
            <DeckKey
              key={b.id}
              button={b}
              status={buttonState(b, state, playingSounds)}
              hardware={hardware}
              motion={motion}
              onOpenMonitor={onOpenMonitor}
              editMode={editMode}
              dragging={drag.dragging === b.id}
              target={drag.target === slot}
              onPointerDown={(e) => drag.onPointerDown(b.id, e)}
              onEdit={() => !drag.consumeClick() && setEditing(b)}
            />
          );
        })}
      </div>

      {page.buttons.length === 0 && !editMode && (
        <EmptyState compact icon={<Plus size={20} strokeWidth={1.5} />} title={t('deck.emptyPage')} actions={<Button variant="primary" icon={<Pencil size={15} />} onClick={() => setEditMode(true)}>{t('deck.startEditing')}</Button>}>
          {t('deck.emptyPageBody')}
        </EmptyState>
      )}

      {editing && (
        <ButtonEditor
          button={editing}
          isNew={!page.buttons.some((b) => b.id === editing.id)}
          onClose={() => setEditing(null)}
          onSave={(b) => saveNow(withPage(page.buttons.some((x) => x.id === b.id) ? page.buttons.map((x) => (x.id === b.id ? b : x)) : [...page.buttons, b]))}
          onDelete={() => {
            save(withPage(page.buttons.filter((x) => x.id !== editing.id)));
            setEditing(null);
          }}
          onDuplicate={() => {
            const slot = freeSlot(page.buttons, capacity, editing.slot + 1);
            if (slot < 0) return t('deck.gridIsFull');
            save(withPage([...page.buttons, { ...editing, id: newId(), slot }]));
            setEditing(null);
            return null;
          }}
        />
      )}
    </div>
  );
}

function PageName({ value, placeholder, onCommit }: { value: string; placeholder: string; onCommit(v: string): void }) {
  const [name, setName] = useState(value);
  return (
    <input className="input" value={name} maxLength={24} placeholder={placeholder} onChange={(e) => setName(e.target.value)}
      onBlur={() => name.trim() !== value && onCommit(name.trim())} onKeyDown={(e) => e.key === 'Enter' && e.currentTarget.blur()} />
  );
}

/** One button. Taps run its action on the PC; feedback shows whether that worked. */
function DeckKey(props: {
  button: DeckButton;
  status: ReturnType<typeof buttonState>;
  hardware: HardwareSnapshot | null;
  motion: boolean;
  onOpenMonitor?(): void;
  editMode: boolean;
  dragging: boolean;
  target: boolean;
  onPointerDown(e: ReactPointerEvent<HTMLElement>): void;
  onEdit(): void;
}) {
  const { t } = useTranslation();
  const { state, toast } = useDeck();
  const { button: b, status, editMode } = props;
  const [flash, setFlash] = useState<'ok' | 'fail' | null>(null);
  const [busy, setBusy] = useState(false);
  const [holding, setHolding] = useState(false);
  const flashTimer = useRef<number>();
  const holdTimer = useRef<number>();
  /** The long-press action already ran for this touch, so the click that follows must do nothing. */
  const heldDone = useRef(false);
  useEffect(() => () => {
    window.clearTimeout(flashTimer.current);
    window.clearTimeout(holdTimer.current);
  }, []);
  const canHold = !!b.holdAction && !editMode;

  const show = (f: 'ok' | 'fail') => {
    setFlash(f);
    window.clearTimeout(flashTimer.current);
    flashTimer.current = window.setTimeout(() => setFlash(null), 700);
  };

  const send = async (hold: boolean) => {
    setBusy(true); // a second tap while the first is on its way does nothing (the PC also ignores double taps)
    try {
      await api.pressDeckButton(b.id, hold);
      show('ok');
    } catch (e) {
      show('fail');
      toast(e instanceof ApiError || e instanceof Error ? e.message : String(e), 'error');
    } finally {
      window.setTimeout(() => setBusy(false), 250);
    }
  };

  const press = () => {
    if (editMode) return props.onEdit();
    if (heldDone.current) {
      heldDone.current = false;
      return;
    }
    if (b.action.type === 'sensor' && b.action.mode === 'monitor') return props.onOpenMonitor?.();
    if (busy || b.action.type === 'none' || b.action.type === 'sensor') return;
    send(false);
  };

  // Long press: the hold action runs once the press has lasted HOLD_MS; letting go earlier is a normal tap.
  const startHold = () => {
    if (!canHold || busy) return;
    heldDone.current = false;
    setHolding(true);
    window.clearTimeout(holdTimer.current);
    holdTimer.current = window.setTimeout(() => {
      heldDone.current = true;
      setHolding(false);
      navigator.vibrate?.(15);
      send(true);
    }, HOLD_MS);
  };
  const endHold = () => {
    window.clearTimeout(holdTimer.current);
    setHolding(false);
  };

  const sensor = b.action.type === 'sensor' && b.action.sensor ? sensorText(props.hardware, b.action.sensor) : null;
  const level = sensor ? sensorLevel(sensor.value, b.warnAt, b.criticalAt) : null;
  const label = b.label || (b.action.type === 'none' ? '' : shortLabel(b.action, t, state));
  // The active appearance replaces the normal one only in an active state, and only where it was set.
  const active = isActiveState(status);
  const image = (active && b.activeImage) || b.image;
  const icon = (active && b.activeIcon) || b.icon;
  const color = (active && b.activeColor) || b.color;
  const mode = b.animation ?? 'loop';
  const animate = props.motion && (mode === 'loop' || (mode === 'active' && active));
  const classes = [
    'deck-key',
    status ? `is-${status}` : '',
    level ? `is-${level}` : '',
    flash ? `is-${flash}` : '',
    editMode ? 'is-editing' : '',
    props.dragging ? 'is-dragging' : '',
    props.target ? 'is-target' : '',
    image ? 'has-image' : '',
    sensor ? 'is-sensor' : '',
    canHold ? 'has-hold' : '',
    holding ? 'is-holding' : '',
  ].join(' ');

  return (
    <button
      type="button"
      data-slot={b.slot}
      className={classes}
      style={{ '--tag': color } as CSSProperties}
      aria-label={editMode ? t('deck.editButton', { name: label || t('deck.untitled') }) : label || t('deck.untitled')}
      aria-pressed={status === 'on' || status === 'active' ? true : undefined}
      title={b.action.type === 'none' ? undefined : describeAction(b.action, t, state)}
      onPointerDown={(e) => {
        props.onPointerDown(e);
        if (e.button === 0) startHold();
      }}
      onPointerUp={endHold}
      onPointerLeave={endHold}
      onPointerCancel={endHold}
      onClick={press}
      onContextMenu={(e) => (editMode || canHold) && e.preventDefault()}
    >
      {image && <DeckImage key={image} name={image} animate={animate} className={sensor ? 'deck-key-bg' : 'deck-key-image'} />}
      {sensor ? (
        <>
          {!image && <span className="deck-key-icon"><SoundIcon name={icon} size={16} /></span>}
          <span className={`deck-key-value num ${sensor.value === null ? 'is-unavailable' : ''}`}>
            {sensor.value === null ? t('hw.unavailableShort') : `${sensor.value}${b.action.unit ?? sensor.unit}`}
          </span>
          {sensor.share !== null && <span className="deck-key-bar"><span style={{ transform: `scaleX(${Math.min(1, Math.max(0, sensor.share))})` }} /></span>}
        </>
      ) : (
        !image && <span className="deck-key-icon"><SoundIcon name={icon} size={26} /></span>
      )}
      {label && <span className="deck-key-label">{label}</span>}
      {status === 'running' && <span className="deck-key-badge num">{t('deck.running')}</span>}
      {status === 'running' && <span className="deck-key-progress" aria-hidden />}
      {status === 'unavailable' && <span className="deck-key-badge is-off">{t('deck.unavailable')}</span>}
    </button>
  );
}

/**
 * Pointer drag between slots, for mouse, pen and touch: drop on a button to swap, on an empty slot to
 * move. A drag never counts as a tap, so the editor only opens on a genuine one.
 */
function useSlotDrag(enabled: boolean, move: (id: string, slot: number) => void) {
  const [dragging, setDragging] = useState<string | null>(null);
  const [target, setTarget] = useState<number | null>(null);
  const start = useRef<{ id: string; pointerId: number; x: number; y: number; active: boolean } | null>(null);
  const suppress = useRef(false);
  const moveRef = useRef(move);
  moveRef.current = move;

  useEffect(() => {
    if (!enabled) return;
    const slotAt = (x: number, y: number) => {
      const el = document.elementFromPoint(x, y)?.closest<HTMLElement>('[data-slot]');
      return el ? Number(el.dataset.slot) : null;
    };
    const onMove = (e: PointerEvent) => {
      const s = start.current;
      if (!s || e.pointerId !== s.pointerId) return;
      if (!s.active) {
        if (Math.hypot(e.clientX - s.x, e.clientY - s.y) < 8) return;
        s.active = true;
        setDragging(s.id);
      }
      e.preventDefault();
      setTarget(slotAt(e.clientX, e.clientY));
    };
    const onUp = (e: PointerEvent) => {
      const s = start.current;
      if (!s || e.pointerId !== s.pointerId) return;
      start.current = null;
      if (!s.active) return;
      suppress.current = true;
      const slot = slotAt(e.clientX, e.clientY);
      setDragging(null);
      setTarget(null);
      if (slot !== null) moveRef.current(s.id, slot);
    };
    window.addEventListener('pointermove', onMove, { passive: false });
    window.addEventListener('pointerup', onUp);
    window.addEventListener('pointercancel', onUp);
    return () => {
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      window.removeEventListener('pointercancel', onUp);
    };
  }, [enabled]);

  const onPointerDown = (id: string, e: ReactPointerEvent<HTMLElement>) => {
    if (!enabled || e.button !== 0) return;
    start.current = { id, pointerId: e.pointerId, x: e.clientX, y: e.clientY, active: false };
  };

  const consumeClick = () => {
    const was = suppress.current;
    suppress.current = false;
    return was;
  };

  return { dragging, target, onPointerDown, consumeClick };
}
