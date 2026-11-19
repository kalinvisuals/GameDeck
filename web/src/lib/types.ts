import type { Language } from '../i18n';
export type PlayMode = 'Toggle' | 'Restart' | 'Overlap';

export interface Sound {
  id: string;
  label: string;
  color: string;
  icon: string;
  volume: number;
  loop: boolean;
  mode: PlayMode;
  durationMs: number;
  /** Part of the file that plays; trimEndMs 0 = to the end. */
  trimStartMs: number;
  trimEndMs: number;
  originalFileName: string;
  favorite: boolean;
  category: string;
}

export interface Profile {
  id: string;
  name: string;
  layout: string[];
}

export interface AudioSettings {
  monitorDeviceId: string | null;
  monitorEnabled: boolean;
  monitorVolume: number;
  virtualDeviceId: string | null;
  virtualVolume: number;
  micDeviceId: string | null;
  micEnabled: boolean;
  micVolume: number;
  latencyMs: number;
  micMuted: boolean;
  virtualMuted: boolean;
  monitorMuted: boolean;
}

export interface AudioDevice {
  id: string;
  name: string;
  adapter: string;
  isDefault: boolean;
}

export interface BusStatus {
  enabled: boolean;
  ok: boolean;
  deviceName: string | null;
  error: string | null;
}

export interface Playing {
  instanceId: string;
  soundId: string;
  elapsedMs: number;
  durationMs: number;
  loop: boolean;
}

export interface PairedDevice {
  id: string;
  name: string;
  createdUtc: string;
  lastSeenUtc: string;
}

export interface DeckState {
  sounds: Sound[];
  profiles: Profile[];
  activeProfileId: string;
  audio: AudioSettings;
  devices: { outputs: AudioDevice[]; inputs: AudioDevice[] };
  status: { monitor: BusStatus; virtual: BusStatus; mic: BusStatus };
  playing: Playing[];
  pairedDevices: PairedDevice[];
  server: { port: number; urls: string[]; version: string };
  routing: RoutingReport;
  ui: { language: Language; logoModel: string; logoFinish: string };
  /** Windows volume mixer, read on demand (see api.refreshApps). */
  apps: AppAudio[];
  scenes: ScenesState;
  deck: SmartDeckState;
  diagnostics: {
    mic: { underruns: number; drops: number; bufferedMs: number };
    startedUtc: string;
    dataDirectory: string;
    os: string;
    runtime: string;
  };
}

export interface EndpointInfo {
  id: string;
  name: string;
  adapter: string;
  state: string;
}

export interface RoutingIssue {
  level: 'error' | 'warning' | 'info';
  message: string;
  fix: string | null;
}

export interface RoutingReport {
  cables: { render: EndpointInfo; capture: EndpointInfo | null }[];
  selected: { render: EndpointInfo; capture: EndpointInfo | null } | null;
  issues: RoutingIssue[];
}

export interface RouteTestResult {
  detected: boolean;
  latencyMs: number;
  toneDbfs: number;
  noiseDbfs: number;
  captureDevice: string;
  message: string;
  frequencyHz: number;
  pitchOk: boolean;
}

export interface YouTubeTrack {
  id: string;
  title: string;
  channel: string;
  art: string;
  duration: number;
  position: number;
  paused: boolean;
  volume: number;
  isAd: boolean;
}

export interface YouTubeItem { id: string; title: string; channel: string; duration: string; art: string }

export interface YouTubeState {
  connected: boolean;
  track: YouTubeTrack | null;
  query: string;
  searching: boolean;
  results: YouTubeItem[];
  upNext: YouTubeItem[];
  upNextKind: 'related' | 'playlist';
  queue: string[];
  playlists: { id: string; title: string }[];
  notice: string | null;
}

export interface NewsMessage { id: string; title: string; body: string; link: string | null; linkText: string | null }

export interface UpdatesState {
  current: string;
  available: { version: string; notes: string; page: string; published: string | null } | null;
  canInstall: boolean;
  state: 'idle' | 'checking' | 'downloading' | 'installing' | 'failed';
  error: string | null;
  news: NewsMessage[];
  donateUrl: string | null;
  source: string;
  checkedAt: string | null;
}

export interface YouTubeAction { action: string; text?: string; id?: string; index?: number; value?: number }

export interface Session {
  authenticated: boolean;
  deviceId: string | null;
  deviceName: string | null;
  /** This browser runs on the GameDeck PC: only here can program, link and key buttons be added or changed. */
  local?: boolean;
}

/** Device id payloads use "" for "Windows default" (monitor) or "Off" (virtual / mic). */
export type AudioPatch = Partial<{ [K in keyof AudioSettings]: AudioSettings[K] extends string | null ? string : AudioSettings[K] }>;

/** One application in the Windows volume mixer. key = executable name ("discord.exe"), stable across restarts. */
export interface AppAudio {
  key: string;
  name: string;
  /** 0..1 */
  volume: number;
  muted: boolean;
  sessions: number;
}

/** GameDeck's own mixer levels, in the same units as AudioSettings. */
export interface SceneChannels {
  micVolume: number;
  micMuted: boolean;
  virtualVolume: number;
  virtualMuted: boolean;
  monitorVolume: number;
  monitorMuted: boolean;
}

export interface SceneApp {
  key: string;
  name: string;
  volume: number;
  muted: boolean;
}

export interface Scene {
  id: string;
  name: string;
  icon: string;
  createdUtc: string;
  modifiedUtc: string;
  /** null = the scene leaves GameDeck's channels alone. */
  channels: SceneChannels | null;
  apps: SceneApp[];
  hotkey: string | null;
  /** Why the shortcut isn't working right now (another program owns it). */
  hotkeyError: string | null;
  /** Executable that applies this scene when it comes to the front. */
  autoApp: string | null;
}

export interface ScenesState {
  items: Scene[];
  activeId: string | null;
  autoSwitch: boolean;
}

export interface ScenePatch {
  name?: string;
  icon?: string;
  /** "" clears it. */
  hotkey?: string;
  /** "" clears it. */
  autoApp?: string;
  apps?: SceneApp[];
  channels?: SceneChannels;
  clearChannels?: boolean;
}

export interface SceneApplyResult {
  sceneId: string;
  applied: string[];
  skipped: string[];
}

// ---------------------------------------------------------------- Smart Deck

export type DeckActionType =
  | 'none' | 'launch' | 'url' | 'keys' | 'media' | 'sound' | 'stopAll' | 'mute' | 'volume'
  | 'scene' | 'appMute' | 'macro' | 'sensor' | 'wait' | 'window';

export type DeckChannel = 'mic' | 'virtual' | 'monitor';
export type MediaKey = 'playPause' | 'next' | 'previous' | 'volumeUp' | 'volumeDown' | 'mute';
export type SensorId =
  | 'cpu.usage' | 'cpu.temp' | 'cpu.clock' | 'gpu.usage' | 'gpu.temp' | 'gpu.vram' | 'gpu.clock' | 'gpu.fan' | 'ram.usage' | 'ram.used';

/** What a button or a macro step does; only the fields its type uses are set. */
export interface DeckAction {
  type: DeckActionType;
  target?: string | null;
  keys?: string | null;
  channel?: DeckChannel | null;
  /** media key; on/off/toggle for mutes; set/up/down for volume. */
  mode?: string | null;
  value?: number | null;
  sensor?: SensorId | null;
  unit?: string | null;
  delayMs?: number | null;
}

export interface DeckButton {
  id: string;
  /** row * columns + column */
  slot: number;
  label: string;
  icon: string;
  color: string;
  image: string | null;
  action: DeckAction;
  /** Shown while the button's live state is active (see buttonState); null = same as normal. */
  activeImage?: string | null;
  activeIcon?: string | null;
  activeColor?: string | null;
  /** loop = always, active = only while active, off = first frame only. */
  animation?: DeckAnimation;
  /** Sensor buttons: amber from warnAt, red from criticalAt, in the sensor's unit. */
  warnAt?: number | null;
  criticalAt?: number | null;
  /** Runs when the button is held down (long press). */
  holdAction?: DeckAction | null;
  /** Two-state button: presses alternate between action and this one. */
  toggleAction?: DeckAction | null;
}

export type DeckAnimation = 'loop' | 'active' | 'off';

export interface DeckImageUpload {
  name: string;
  animated: boolean;
  width: number;
  height: number;
  frames: number;
}

export interface DeckPage {
  id: string;
  name: string;
  buttons: DeckButton[];
}

export interface DeckProfile {
  id: string;
  name: string;
  rows: number;
  columns: number;
  pages: DeckPage[];
}

export interface DeckMacro {
  id: string;
  name: string;
  steps: DeckAction[];
}

export interface SmartDeckState {
  profiles: DeckProfile[];
  activeProfileId: string;
  macros: DeckMacro[];
  runningMacros: string[];
  /** macro id → why its last run stopped early */
  macroErrors: Record<string, string>;
  /** Two-state buttons in their second state (the next press runs toggleAction). */
  toggled: string[];
}

export interface DeckExport {
  format: 'gamedeck-deck';
  version: 1;
  profile: DeckProfile;
  macros: DeckMacro[];
  images: Record<string, string>;
}

// A null reading means the PC or its driver doesn't provide it: shown as "Unavailable", never estimated.
export interface HardwareSnapshot {
  takenUtc: string;
  cpu: { model: string | null; logicalCores: number; usagePercent: number | null; temperatureC: number | null; clockMhz: number | null };
  gpu: {
    model: string | null; usagePercent: number | null; temperatureC: number | null; vramUsedMb: number | null; vramTotalMb: number | null;
    clockMhz: number | null; fanPercent: number | null; source: 'nvml' | 'windows' | 'none';
  };
  memory: { totalMb: number; usedMb: number; availableMb: number; usagePercent: number } | null;
  drives: { name: string; label: string; totalGb: number; freeGb: number; usagePercent: number }[];
}

/** A program with a main window open on the PC. */
export interface OpenWindow {
  program: string;
  title: string;
}
