import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { api, ApiError } from '../lib/api';
import { DeckSocket } from '../lib/socket';
import type { AudioPatch, AudioSettings, DeckState, Playing, Profile, Session, Sound } from '../lib/types';
import { Toasts, useToasts, type ToastKind } from '../ui/Toasts';
import i18n from 'i18next';
import { applyLanguage, type Language } from '../i18n';
import { hardwareStore } from './hardware';

/** Server "elapsed" plus when we received it, so progress is drawn with the local clock. */
export interface PlayingView extends Playing {
  receivedAt: number;
}

interface Deck {
  session: Session;
  state: DeckState;
  profile: Profile;
  soundsById: Map<string, Sound>;
  playing: PlayingView[];
  playingBySound: Map<string, PlayingView>;
  connected: boolean;
  trigger(id: string): void;
  stop(id: string): void;
  stopAll(): void;
  upload(files: File[]): Promise<void>;
  run(action: () => Promise<unknown>): Promise<void>;
  toast(message: string, kind?: ToastKind): void;
  signOut(): void;
  // Optimistic edits: the UI changes immediately, the PC confirms in the background.
  updateAudio(patch: AudioPatch): void;
  updateSound(id: string, patch: Partial<Sound>): void;
  activateProfile(id: string): void;
  setLayout(profileId: string, layout: string[]): void;
  setLanguage(language: Language): void;
  setLogo(model: string, finish: string): void;
}

type Override =
  | { kind: 'audio'; patch: Partial<AudioSettings> }
  | { kind: 'sound'; id: string; patch: Partial<Sound> }
  | { kind: 'active'; profileId: string }
  | { kind: 'layout'; profileId: string; layout: string[] }
  | { kind: 'language'; language: Language }
  | { kind: 'logo'; model: string; finish: string };

interface OverrideEntry {
  key: number;
  override: Override;
  /** Request finished; drop the overlay when the next server snapshot arrives. */
  settled: boolean;
}

const DEVICE_KEYS = new Set(['monitorDeviceId', 'virtualDeviceId', 'micDeviceId']);

/** Wire format uses "" for "default/off"; state uses null. */
function toSettings(patch: AudioPatch): Partial<AudioSettings> {
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(patch)) out[k] = DEVICE_KEYS.has(k) && v === '' ? null : v;
  return out as Partial<AudioSettings>;
}

function applyOverrides(state: DeckState, entries: OverrideEntry[]): DeckState {
  if (entries.length === 0) return state;
  let s = state;
  for (const { override: o } of entries) {
    if (o.kind === 'audio') s = { ...s, audio: { ...s.audio, ...o.patch } };
    else if (o.kind === 'sound') s = { ...s, sounds: s.sounds.map((x) => (x.id === o.id ? { ...x, ...o.patch } : x)) };
    else if (o.kind === 'active') s = { ...s, activeProfileId: o.profileId };
    else if (o.kind === 'language') s = { ...s, ui: { ...s.ui, language: o.language } };
    else if (o.kind === 'logo') s = { ...s, ui: { ...s.ui, logoModel: o.model, logoFinish: o.finish } };
    else s = { ...s, profiles: s.profiles.map((p) => (p.id === o.profileId ? { ...p, layout: o.layout } : p)) };
  }
  return s;
}

const DeckCtx = createContext<Deck | null>(null);

/** For components that also render before pairing (e.g. the language switch). */
export function useDeckOptional(): Deck | null {
  return useContext(DeckCtx);
}

export function useDeck(): Deck {
  const deck = useContext(DeckCtx);
  if (!deck) throw new Error('useDeck outside DeckProvider');
  return deck;
}

const SIGNED_OUT: Session = { authenticated: false, deviceId: null, deviceName: null };

/**
 * Owns the session, the socket and server state. Renders `fallback` until there is a paired
 * session with a first state snapshot, and `pair` when the device isn't paired.
 */
export function DeckProvider(props: {
  children: ReactNode;
  pair(onPaired: (s: Session) => void): ReactNode;
  loading: ReactNode;
}) {
  const [session, setSession] = useState<Session | null>(null);
  const [serverState, setServerState] = useState<DeckState | null>(null);
  const [overrides, setOverrides] = useState<OverrideEntry[]>([]);
  const nextKey = useRef(1);
  const state = useMemo(() => (serverState ? applyOverrides(serverState, overrides) : null), [serverState, overrides]);
  // The PC's setting is the source of truth for every device; apply it the moment it arrives or changes.
  const language = state?.ui?.language;
  useEffect(() => {
    if (language) applyLanguage(language);
  }, [language]);
  const [playing, setPlaying] = useState<PlayingView[]>([]);
  const [connected, setConnected] = useState(false);
  const { toasts, push } = useToasts();
  const socketRef = useRef<DeckSocket | null>(null);

  const receivePlaying = useCallback((list: Playing[]) => {
    const now = performance.now();
    setPlaying(list.map((p) => ({ ...p, receivedAt: now })));
  }, []);

  useEffect(() => {
    api.session().then(setSession).catch(() => setSession(SIGNED_OUT));
  }, []);

  useEffect(() => {
    if (!session?.authenticated) return;
    const socket = new DeckSocket({
      onState: (s) => {
        setServerState(s);
        // The PC's snapshot now reflects every finished request.
        setOverrides((list) => (list.some((e) => e.settled) ? list.filter((e) => !e.settled) : list));
        receivePlaying(s.playing);
      },
      onPlaying: receivePlaying,
      onError: (m) => push(m, 'error'),
      onConnection: setConnected,
      stillAuthorized: async () => {
        const s = await api.session();
        if (!s.authenticated) setSession(s);
        return s.authenticated;
      },
    });
    socketRef.current = socket;
    hardwareStore.connect((on) => socket.send({ type: 'hardware', on }));
    socket.connect();
    return () => {
      hardwareStore.connect(null);
      socket.dispose();
      socketRef.current = null;
    };
  }, [session?.authenticated, push, receivePlaying]);

  const run = useCallback(
    async (action: () => Promise<unknown>) => {
      try {
        await action();
      } catch (e) {
        if (e instanceof ApiError && e.status === 401) setSession(SIGNED_OUT);
        push(e instanceof Error ? e.message : String(e), 'error');
      }
    },
    [push],
  );

  /** Show the change now; send it; keep the overlay until confirmed, roll it back on error. */
  const optimistic = useCallback(
    (override: Override, request: () => Promise<unknown>) => {
      const key = nextKey.current++;
      setOverrides((list) => [...list, { key, override, settled: false }]);
      request().then(
        () => setOverrides((list) => list.map((e) => (e.key === key ? { ...e, settled: true } : e))),
        (e) => {
          setOverrides((list) => list.filter((x) => x.key !== key));
          if (e instanceof ApiError && e.status === 401) setSession(SIGNED_OUT);
          push(e instanceof Error ? e.message : String(e), 'error');
        },
      );
    },
    [push],
  );

  const updateAudio = useCallback((patch: AudioPatch) => optimistic({ kind: 'audio', patch: toSettings(patch) }, () => api.updateAudio(patch)), [optimistic]);
  const updateSound = useCallback((id: string, patch: Partial<Sound>) => optimistic({ kind: 'sound', id, patch }, () => api.updateSound(id, patch)), [optimistic]);
  const activateProfile = useCallback((id: string) => optimistic({ kind: 'active', profileId: id }, () => api.activateProfile(id)), [optimistic]);
  const setLanguage = useCallback((language: Language) => optimistic({ kind: 'language', language }, () => api.setLanguage(language)), [optimistic]);
  // Sent one at a time, so quick clicks in the gallery always end on the last choice.
  const logoQueue = useRef<Promise<unknown>>(Promise.resolve());
  const setLogo = useCallback((model: string, finish: string) => optimistic({ kind: 'logo', model, finish }, () => {
    const request = logoQueue.current.catch(() => undefined).then(() => api.setLogo(model, finish));
    logoQueue.current = request;
    return request;
  }), [optimistic]);
  const setLayout = useCallback(
    (profileId: string, layout: string[]) => optimistic({ kind: 'layout', profileId, layout }, () => api.updateProfile(profileId, { layout })),
    [optimistic],
  );

  const trigger = useCallback((id: string) => {
    if (!socketRef.current?.send({ type: 'play', soundId: id })) run(() => api.trigger(id));
  }, [run]);

  const stop = useCallback((id: string) => {
    if (!socketRef.current?.send({ type: 'stop', soundId: id })) run(() => api.stop(id));
  }, [run]);

  const stopAll = useCallback(() => {
    if (!socketRef.current?.send({ type: 'stopAll' })) run(() => api.stopAll());
  }, [run]);

  const profile = useMemo(() => state?.profiles.find((p) => p.id === state.activeProfileId) ?? state?.profiles[0], [state]);
  const soundsById = useMemo(() => new Map(state?.sounds.map((s) => [s.id, s])), [state]);
  const playingBySound = useMemo(() => {
    const m = new Map<string, PlayingView>();
    for (const p of playing) {
      const prev = m.get(p.soundId);
      if (!prev || p.elapsedMs < prev.elapsedMs) m.set(p.soundId, p); // newest instance wins
    }
    return m;
  }, [playing]);

  const upload = useCallback(
    async (files: File[]) => {
      if (!profile || files.length === 0) return;
      push(files.length === 1 ? i18n.t('toast.importingOne', { name: files[0].name }) : i18n.t('toast.importingMany', { count: files.length }));
      await run(async () => {
        const result = await api.upload(files, profile.id);
        if (result.created.length) push(i18n.t('toast.added', { count: result.created.length }), 'success');
        result.errors.forEach((e) => push(e, 'error'));
      });
    },
    [profile, push, run],
  );

  const signOut = useCallback(() => setSession(SIGNED_OUT), []);

  let body: ReactNode;
  if (!session) body = props.loading;
  else if (!session.authenticated) body = props.pair(setSession);
  else if (!state || !profile) body = props.loading;
  else {
    const deck: Deck = {
      session, state, profile, soundsById, playing, playingBySound, connected,
      trigger, stop, stopAll, upload, run, toast: push, signOut,
      updateAudio, updateSound, activateProfile, setLayout, setLanguage, setLogo,
    };
    body = <DeckCtx.Provider value={deck}>{props.children}</DeckCtx.Provider>;
  }

  return (
    <>
      {body}
      <Toasts toasts={toasts} />
    </>
  );
}
