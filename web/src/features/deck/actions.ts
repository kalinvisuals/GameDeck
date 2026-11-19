import type { TFunction } from 'i18next';
import type { DeckAction, DeckActionType, DeckButton, DeckChannel, DeckState, HardwareSnapshot, MediaKey, SensorId } from '../../lib/types';

/** Button actions, grouped as in the editor. Launch, link and keys act outside GameDeck. */
export const ACTION_GROUPS: { group: string; types: DeckActionType[] }[] = [
  { group: 'gamedeck', types: ['sound', 'stopAll', 'mute', 'volume', 'scene', 'appMute', 'macro'] },
  { group: 'windows', types: ['launch', 'window', 'url', 'keys', 'media'] },
  { group: 'display', types: ['sensor', 'none'] },
];

/** Long-press and second-state actions: anything a button does, except a read-only display or nothing. */
export const SECONDARY_GROUPS = ACTION_GROUPS.map((g) => ({ ...g, types: g.types.filter((x) => x !== 'sensor' && x !== 'none') })).filter((g) => g.types.length > 0);

/** How long a press must last to count as a long press. */
export const HOLD_MS = 500;

/** What a macro step can do: no other macros (no loops) and no read-only widgets. */
export const STEP_TYPES: DeckActionType[] = ['wait', 'sound', 'stopAll', 'mute', 'volume', 'scene', 'appMute', 'launch', 'window', 'url', 'keys', 'media'];

/** Only the GameDeck PC itself may add or change these (the server enforces it too). */
export const SENSITIVE = new Set<DeckActionType>(['launch', 'url', 'keys', 'window']);

export const WINDOW_MODES = ['restore', 'minimize', 'close'] as const;

export const CHANNELS: DeckChannel[] = ['mic', 'virtual', 'monitor'];
export const MEDIA_KEYS: MediaKey[] = ['playPause', 'previous', 'next', 'volumeDown', 'volumeUp', 'mute'];
export const SENSORS: SensorId[] = ['cpu.usage', 'cpu.temp', 'cpu.clock', 'gpu.usage', 'gpu.temp', 'gpu.vram', 'gpu.clock', 'gpu.fan', 'ram.usage', 'ram.used'];

/** Highest "set" value per channel, as in the Mixer sliders. */
export const channelMax = (c: DeckChannel | null | undefined) => (c === 'mic' ? 2 : 1.5);

export function defaultAction(type: DeckActionType, state?: DeckState): DeckAction {
  switch (type) {
    case 'sound': return { type, target: state?.sounds[0]?.id ?? null };
    case 'mute': return { type, channel: 'mic', mode: 'toggle' };
    case 'volume': return { type, channel: 'virtual', mode: 'up', value: 0.1 };
    case 'scene': return { type, target: state?.scenes.items[0]?.id ?? null };
    case 'appMute': return { type, target: state?.apps[0]?.key ?? null, mode: 'toggle' };
    case 'macro': return { type, target: state?.deck.macros[0]?.id ?? null };
    case 'media': return { type, mode: 'playPause' };
    case 'sensor': return { type, sensor: 'cpu.usage' };
    case 'wait': return { type, delayMs: 1000 };
    case 'launch': case 'url': return { type, target: '' };
    case 'keys': return { type, keys: '' };
    case 'window': return { type, target: '', mode: 'close' };
    default: return { type };
  }
}

/** A sensor value ready to show; null `value` means the PC can't read it ("Unavailable"). */
export interface SensorText {
  value: string | null;
  unit: string;
  /** 0..1 for a bar, when the reading is a share of something. */
  share: number | null;
  detail?: string;
}

const pct = (v: number | null | undefined): SensorText =>
  v == null ? { value: null, unit: '%', share: null } : { value: Math.round(v).toString(), unit: '%', share: v / 100 };
const temp = (v: number | null | undefined): SensorText => ({ value: v == null ? null : Math.round(v).toString(), unit: '°C', share: null });
const ghz = (mhz: number | null | undefined): SensorText => ({ value: mhz == null ? null : (mhz / 1000).toFixed(2), unit: 'GHz', share: null });
const gb = (mb: number) => (mb / 1024).toFixed(1);

export function sensorText(h: HardwareSnapshot | null, id: SensorId): SensorText {
  if (!h) return { value: null, unit: '', share: null };
  switch (id) {
    case 'cpu.usage': return pct(h.cpu.usagePercent);
    case 'cpu.temp': return temp(h.cpu.temperatureC);
    case 'cpu.clock': return ghz(h.cpu.clockMhz);
    case 'gpu.usage': return pct(h.gpu.usagePercent);
    case 'gpu.temp': return temp(h.gpu.temperatureC);
    case 'gpu.clock': return ghz(h.gpu.clockMhz);
    case 'gpu.fan': return pct(h.gpu.fanPercent);
    case 'gpu.vram': {
      const { vramUsedMb: used, vramTotalMb: total } = h.gpu;
      if (used == null) return { value: null, unit: '', share: null };
      if (!total) return { value: gb(used), unit: 'GB', share: null };
      return { value: Math.round((100 * used) / total).toString(), unit: '%', share: used / total, detail: `${gb(used)} / ${gb(total)} GB` };
    }
    case 'ram.usage': return h.memory ? { ...pct(h.memory.usagePercent), detail: `${gb(h.memory.usedMb)} / ${gb(h.memory.totalMb)} GB` } : pct(null);
    case 'ram.used': return h.memory ? { value: gb(h.memory.usedMb), unit: 'GB', share: h.memory.usedMb / h.memory.totalMb } : { value: null, unit: 'GB', share: null };
  }
}

/** One line saying what a button or step does, for cards and lists. */
export function describeAction(a: DeckAction, t: TFunction, state: DeckState): string {
  const name = t(`deck.types.${a.type}`);
  switch (a.type) {
    case 'launch': case 'url': return a.target ? `${name}: ${a.target}` : name;
    case 'keys': return a.keys ? `${name}: ${a.keys}` : name;
    case 'window': return `${t(`deck.windowModes.${a.mode ?? 'close'}`)}: ${a.target || t('deck.choose')}`;
    case 'media': return a.mode ? t(`deck.media.${a.mode}`) : name;
    case 'sound': return `${name}: ${state.sounds.find((s) => s.id === a.target)?.label ?? t('deck.missing')}`;
    case 'scene': return `${name}: ${state.scenes.items.find((s) => s.id === a.target)?.name ?? t('deck.missing')}`;
    case 'macro': return `${name}: ${state.deck.macros.find((m) => m.id === a.target)?.name ?? t('deck.missing')}`;
    case 'appMute': return `${name}: ${state.apps.find((x) => x.key === a.target)?.name ?? a.target ?? ''} · ${t(`deck.modes.${a.mode ?? 'toggle'}`)}`;
    case 'mute': return `${t(`deck.channels.${a.channel}`)} · ${t(`deck.modes.${a.mode ?? 'toggle'}`)}`;
    case 'volume': {
      const amount = `${Math.round((a.value ?? 0) * 100)}%`;
      return `${t(`deck.channels.${a.channel}`)} · ${a.mode === 'set' ? amount : `${a.mode === 'up' ? '+' : '−'}${amount}`}`;
    }
    case 'sensor': return a.sensor ? t(`deck.sensors.${a.sensor}`) : name;
    case 'wait': return t('deck.waitFor', { seconds: ((a.delayMs ?? 0) / 1000).toFixed(1) });
    default: return name;
  }
}

/** "Discord" for a program or shortcut, the site name for a link; what an unnamed button shows. */
export function programName(target: string | null | undefined): string {
  const name = (target ?? '').split(/[\\/]/).filter(Boolean).pop() ?? '';
  return name.replace(/\.(exe|lnk|url)$/i, '');
}

/**
 * The "close the program" action that undoes a launch button, for a new two-state button. Only an .exe
 * names its program; a shortcut (.lnk) or a file doesn't, so the program is then chosen from the list.
 */
export function closingActionFor(main: DeckAction): DeckAction {
  const exe = main.type === 'launch' && /\.exe$/i.test(main.target ?? '') ? (main.target ?? '').split(/[\\/]/).pop()!.toLowerCase() : '';
  return { type: 'window', target: exe, mode: 'close' };
}

/** Text on a button that has no name of its own. */
export function shortLabel(a: DeckAction, t: TFunction, state: DeckState): string {
  if ((a.type === 'launch' || a.type === 'window') && a.target) return programName(a.target);
  if (a.type === 'url' && a.target) {
    try {
      return new URL(a.target).hostname.replace(/^www\./, '') || a.target;
    } catch {
      return a.target;
    }
  }
  return describeAction(a, t, state);
}

/** Live state a button shows: muted channel, active scene, running macro, playing sound. */
export type ButtonState = 'on' | 'active' | 'running' | 'error' | 'unavailable' | null;

/** Actions whose button has a live state, so an "active" appearance means something for them. */
export const STATEFUL = new Set<DeckActionType>(['mute', 'appMute', 'scene', 'sound', 'macro']);

/** States in which the button shows its active appearance. */
export const isActiveState = (s: ButtonState) => s === 'on' || s === 'active' || s === 'running';

export function buttonState(b: DeckButton, state: DeckState, playingSounds: Set<string>): ButtonState {
  // A two-state button shows its own state: active while in its second state.
  if (b.toggleAction) return state.deck.toggled?.includes(b.id) ? 'active' : null;
  const a = b.action;
  const gone =
    (a.type === 'sound' && !state.sounds.some((x) => x.id === a.target)) ||
    (a.type === 'scene' && !state.scenes.items.some((x) => x.id === a.target)) ||
    (a.type === 'macro' && !state.deck.macros.some((x) => x.id === a.target));
  if (gone) return 'unavailable';
  switch (a.type) {
    case 'mute': {
      const muted = a.channel === 'mic' ? state.audio.micMuted : a.channel === 'virtual' ? state.audio.virtualMuted : state.audio.monitorMuted;
      return muted ? 'on' : null;
    }
    case 'appMute': {
      const app = state.apps.find((x) => x.key === a.target);
      return !app ? 'unavailable' : app.muted ? 'on' : null;
    }
    case 'scene': return a.target && state.scenes.activeId === a.target ? 'active' : null;
    case 'sound': return a.target && playingSounds.has(a.target) ? 'active' : null;
    case 'macro':
      if (a.target && state.deck.runningMacros.includes(a.target)) return 'running';
      return a.target && state.deck.macroErrors[a.target] ? 'error' : null;
    default: return null;
  }
}

/**
 * "Ctrl+Shift+S" from a key press, with the same names the PC parses. Unlike scene shortcuts this
 * also takes Tab, Esc, arrows and the like. Null while only modifiers are down.
 */
export function keysFromEvent(e: Pick<KeyboardEvent, 'code' | 'ctrlKey' | 'altKey' | 'shiftKey' | 'metaKey'>): string | null {
  const c = e.code;
  const named: Record<string, string> = {
    Tab: 'Tab', Enter: 'Enter', NumpadEnter: 'Enter', Escape: 'Esc', Space: 'Space', Backspace: 'Backspace', Delete: 'Delete',
    Insert: 'Insert', Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown',
    ArrowLeft: 'Left', ArrowUp: 'Up', ArrowRight: 'Right', ArrowDown: 'Down', PrintScreen: 'PrintScreen',
  };
  let key: string | null = named[c] ?? null;
  if (/^Key[A-Z]$/.test(c)) key = c.slice(3);
  else if (/^Digit[0-9]$/.test(c)) key = c.slice(5);
  else if (/^F([1-9]|1[0-9]|2[0-4])$/.test(c)) key = c;
  else if (/^Numpad[0-9]$/.test(c)) key = `Num${c.slice(6)}`;
  if (!key) return null;
  const mods = [e.ctrlKey && 'Ctrl', e.altKey && 'Alt', e.shiftKey && 'Shift', e.metaKey && 'Win'].filter(Boolean);
  return [...mods, key].join('+');
}

/** amber/red for a sensor value against the button's thresholds. */
export function sensorLevel(value: string | null, warnAt?: number | null, criticalAt?: number | null): 'warn' | 'critical' | null {
  const v = value === null ? NaN : Number(value);
  if (!Number.isFinite(v)) return null;
  if (criticalAt != null && v >= criticalAt) return 'critical';
  if (warnAt != null && v >= warnAt) return 'warn';
  return null;
}

/** First empty slot of a page, or -1 when the grid is full. */
export function freeSlot(buttons: DeckButton[], capacity: number, from = 0): number {
  const taken = new Set(buttons.map((b) => b.slot));
  for (let i = 0; i < capacity; i++) {
    const slot = (from + i) % capacity;
    if (!taken.has(slot)) return slot;
  }
  return -1;
}

/** Placeholder id for a button the PC hasn't stored yet; the PC assigns the real one. */
export const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(6)), (b) => b.toString(16).padStart(2, '0')).join('');
