import type { Language } from '../i18n';
import i18n from 'i18next';
import type { AppAudio, AudioPatch, DeckExport, DeckImageUpload, DeckMacro, DeckProfile, DeckState, HardwareSnapshot, OpenWindow, PlayMode, PresenceOptions, PresencePreset, PresenceState, SongInfo, Profile, RouteTestResult, Scene, SceneApplyResult, ScenePatch, Session, Sound, UpdatesState, YouTubeAction } from './types';

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

/** fetch() that turns a network failure (browser text like “Failed to fetch”) into a localized error. */
async function send(path: string, init: RequestInit): Promise<Response> {
  try {
    return await fetch(path, init);
  } catch {
    throw new ApiError(0, i18n.t('common.networkError'));
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const isForm = body instanceof FormData;
  const res = await send(path, {
    method,
    credentials: 'same-origin',
    headers: body !== undefined && !isForm ? { 'Content-Type': 'application/json' } : undefined,
    body: isForm ? body : body !== undefined ? JSON.stringify(body) : undefined,
  });
  if (res.status === 204) return undefined as T;
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new ApiError(res.status, (data as { error?: string }).error ?? i18n.t('common.requestFailed', { status: res.status }));
  return data as T;
}

export interface UploadResult {
  created: Sound[];
  errors: string[];
}

export const api = {
  session: () => request<Session>('GET', '/api/session'),
  pair: (code: string, deviceName: string) => request<Session>('POST', '/api/pair', { code, deviceName }),
  logout: () => request<void>('POST', '/api/logout'),

  state: () => request<DeckState>('GET', '/api/state'),
  refreshDevices: () => request<DeckState>('POST', '/api/devices/refresh'),
  updateAudio: (patch: AudioPatch) => request<void>('PUT', '/api/audio', patch),
  routeTest: () => request<RouteTestResult>('POST', '/api/audio/route-test'),
  setLanguage: (language: Language) => request<void>('PUT', '/api/ui', { language }),
  checkUpdates: () => request<UpdatesState>('POST', '/api/updates/check'),
  installUpdate: () => request<void>('POST', '/api/updates/install'),
  youtube: (action: YouTubeAction) => request<void>('POST', '/api/youtube', action),
  youtubeExtension: () => request<{ path: string }>('GET', '/api/youtube/extension'),
  setLogo: (model: string, finish: string) => request<void>('PUT', '/api/ui/logo', { model, finish }),
  autoStart: () => request<{ enabled: boolean }>('GET', '/api/autostart'),
  setAutoStart: (enabled: boolean) => request<{ enabled: boolean }>('PUT', '/api/autostart', { enabled }),

  refreshApps: () => request<AppAudio[]>('POST', '/api/apps/refresh'),
  setApp: (key: string, patch: { volume?: number; muted?: boolean }) => request<void>('PUT', `/api/apps/${encodeURIComponent(key)}`, patch),

  createScene: (name: string, icon?: string) => request<Scene>('POST', '/api/scenes', { name, icon }),
  updateScene: (id: string, patch: ScenePatch) => request<Scene>('PATCH', `/api/scenes/${id}`, patch),
  captureScene: (id: string) => request<Scene>('POST', `/api/scenes/${id}/capture`),
  duplicateScene: (id: string) => request<Scene>('POST', `/api/scenes/${id}/duplicate`),
  applyScene: (id: string) => request<SceneApplyResult>('POST', `/api/scenes/${id}/apply`),
  deleteScene: (id: string) => request<void>('DELETE', `/api/scenes/${id}`),
  setSceneOptions: (patch: { autoSwitch?: boolean }) => request<void>('PUT', '/api/scenes/options', patch),
  /** Releases scene shortcuts on the PC while one is being recorded; 0 resumes. */
  suspendHotkeys: (seconds: number) => request<void>('POST', '/api/scenes/hotkeys/suspend', { seconds }),

  /** Only the button id is sent: the PC runs the action it has stored for it. */
  pressDeckButton: (id: string, hold = false) => request<{ ok: boolean; ignored: boolean }>('POST', `/api/deck/buttons/${id}/press${hold ? '?hold=true' : ''}`),
  createDeckProfile: (name: string, copyFrom?: string) => request<DeckProfile>('POST', '/api/deck/profiles', { name, copyFrom }),
  saveDeckProfile: (p: DeckProfile) => request<DeckProfile>('PUT', `/api/deck/profiles/${p.id}`, p),
  deleteDeckProfile: (id: string) => request<void>('DELETE', `/api/deck/profiles/${id}`),
  activateDeckProfile: (id: string) => request<void>('POST', `/api/deck/profiles/${id}/activate`),
  exportDeckProfile: (id: string) => request<DeckExport>('GET', `/api/deck/profiles/${id}/export`),
  importDeck: (file: unknown) => request<DeckProfile>('POST', '/api/deck/import', file),
  createMacro: (m: Omit<DeckMacro, 'id'>) => request<DeckMacro>('POST', '/api/deck/macros', m),
  saveMacro: (m: DeckMacro) => request<DeckMacro>('PUT', `/api/deck/macros/${m.id}`, m),
  deleteMacro: (id: string) => request<void>('DELETE', `/api/deck/macros/${id}`),
  runMacro: (id: string) => request<void>('POST', `/api/deck/macros/${id}/run`),
  stopMacro: (id: string) => request<void>('POST', `/api/deck/macros/${id}/stop`),
  uploadDeckImage: (file: File) => {
    const form = new FormData();
    form.append('file', file);
    return request<DeckImageUpload>('POST', '/api/deck/images', form);
  },
  hardware: () => request<HardwareSnapshot>('GET', '/api/hardware'),
  /** Programs with a window open on the PC (only from a browser on the PC itself). */
  deckWindows: () => request<OpenWindow[]>('GET', '/api/deck/windows'),
  /** The program a launch path runs, e.g. "obs64.exe", also through a shortcut; null when it can't be known. */
  deckProgramOf: (path: string) => request<{ program: string | null }>('POST', '/api/deck/program-of', { path }),
  /** Opens the Windows file or folder picker on the PC (only from a browser on the PC itself); null when cancelled. */
  /** Saves the Windows icon of a program, shortcut, file or folder as a button image (PC only). */
  deckProgramIcon: (path: string) => request<{ name: string }>('POST', '/api/deck/icon', { path }),
  browseDeckPath: (folder: boolean, current: string) => request<{ path: string | null }>('POST', '/api/deck/browse', { folder, current }),

  trigger: (id: string) => request<{ result: string }>('POST', `/api/sounds/${id}/trigger`),
  stop: (id: string) => request<void>('POST', `/api/sounds/${id}/stop`),
  stopAll: () => request<void>('POST', '/api/stop-all'),

  async upload(files: File[], profileId: string): Promise<UploadResult> {
    const form = new FormData();
    files.forEach((f) => form.append('files', f, f.name));
    form.append('profileId', profileId);
    const res = await send('/api/sounds', { method: 'POST', body: form, credentials: 'same-origin' });
    const data = await res.json().catch(() => ({ created: [], errors: [i18n.t('common.requestFailed', { status: res.status })] }));
    if (res.status === 401 || res.status === 413) throw new ApiError(res.status, data.error ?? i18n.t('common.uploadRejected'));
    return { created: data.created ?? [], errors: data.errors ?? (data.error ? [data.error] : []) };
  },
  updateSound: (id: string, patch: Partial<{ label: string; color: string; icon: string; volume: number; loop: boolean; mode: PlayMode; favorite: boolean; category: string; trimStartMs: number; trimEndMs: number }>) =>
    request<Sound>('PATCH', `/api/sounds/${id}`, patch),
  deleteSound: (id: string) => request<void>('DELETE', `/api/sounds/${id}`),

  createProfile: (name: string) => request<Profile>('POST', '/api/profiles', { name }),
  updateProfile: (id: string, patch: { name?: string; layout?: string[] }) => request<Profile>('PATCH', `/api/profiles/${id}`, patch),
  addToProfile: (id: string, soundId: string) => request<void>('POST', `/api/profiles/${id}/sounds`, { soundId }),
  deleteProfile: (id: string) => request<void>('DELETE', `/api/profiles/${id}`),
  activateProfile: (id: string) => request<void>('POST', `/api/profiles/${id}/activate`),

  /** Song details from a YouTube, Spotify, SoundCloud, Apple Music or Deezer link (read by the PC). */
  lookupSong: (url: string) => request<SongInfo>('POST', '/api/presence/lookup', { url }),
  setPresenceOptions: (o: PresenceOptions) => request<PresenceState>('PUT', '/api/presence/options', o),
  presence: () => request<PresenceState>('GET', '/api/presence'),
  setPresenceEnabled: (enabled: boolean) => request<PresenceState>('PUT', '/api/presence/enabled', { enabled }),
  createPresencePreset: (p: Omit<PresencePreset, 'id'>) => request<PresencePreset>('POST', '/api/presence/presets', p),
  savePresencePreset: (p: PresencePreset) => request<PresencePreset>('PUT', `/api/presence/presets/${p.id}`, p),
  deletePresencePreset: (id: string) => request<void>('DELETE', `/api/presence/presets/${id}`),
  activatePresencePreset: (id: string) => request<PresenceState>('POST', `/api/presence/presets/${id}/activate`),
  restartPresence: (slot: 'activity' | 'music') => request<PresenceState>('POST', `/api/presence/${slot}/restart`),
  hidePresence: (slot: 'activity' | 'music') => request<PresenceState>('POST', `/api/presence/${slot}/hide`),

  revokeDevice: (id: string) => request<void>('DELETE', `/api/paired-devices/${id}`),
};
