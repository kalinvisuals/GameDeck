import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { BookOpen, Gauge, Grid3x3, Layers, LayoutGrid, Minimize2, MonitorPlay, MoreHorizontal, Settings, SlidersHorizontal, Square } from 'lucide-react';
import { useDeck } from '../state/DeckContext';
import { IconButton, StatusDot, type Tone } from '../ui/controls';
import { LogoMark, Wordmark } from './Logo';
import { DashboardView } from '../views/DashboardView';
import { SoundboardView } from '../views/SoundboardView';
import { MixerView } from '../views/MixerView';
import { DeckView } from '../views/DeckView';
import { ProfilesView } from '../views/ProfilesView';
import { SettingsView } from '../views/SettingsView';
import { GuideView } from '../views/GuideView';
import { YouTubeView } from '../views/YouTubeView';
import { PadsLauncher, type PadsChoice } from './PadsLauncher';
import { MoreLauncher, type MoreChoice } from './MoreLauncher';
import { MenuExtras, UpdatePrompt } from '../features/Updates';
import { openSettingsSection } from '../views/SettingsView';
import { asLogoFinish, asLogoModel, logoIconSvg } from '../lib/logo';
import type { BusStatus } from '../lib/types';

export type ViewId = 'dashboard' | 'soundboard' | 'mixer' | 'deck' | 'youtube' | 'profiles' | 'guide' | 'settings';

/** Labels come from i18n: nav.<id> (sidebar) and nav.short.<id> (phone tab bar). */
type NavItem = { id: ViewId; icon: typeof Gauge };
const NAV: { group: 'control' | 'manage'; items: NavItem[] }[] = [
  {
    group: 'control',
    items: [
      { id: 'dashboard', icon: Gauge },
      { id: 'soundboard', icon: LayoutGrid },
      { id: 'deck', icon: Grid3x3 },
      { id: 'youtube', icon: MonitorPlay },
      { id: 'mixer', icon: SlidersHorizontal },
    ],
  },
  {
    group: 'manage',
    items: [
      { id: 'profiles', icon: Layers },
      { id: 'guide', icon: BookOpen },
      { id: 'settings', icon: Settings },
    ],
  },
];
const ALL = NAV.flatMap((g) => g.items);
/** Phone tab bar: the screens behind the "Pads" chooser, and those listed under "More". */
const PADS: PadsChoice[] = ['soundboard', 'deck', 'youtube'];
const isPads = (v: ViewId): v is PadsChoice => (PADS as ViewId[]).includes(v);
const MORE = ALL.filter((n): n is NavItem & { id: MoreChoice } => n.id !== 'dashboard' && n.id !== 'mixer' && !isPads(n.id));
const VIEW_KEY = 'gamedeck.view';

function initialView(): ViewId {
  try {
    const v = localStorage.getItem(VIEW_KEY) as ViewId | null;
    if (v && ALL.some((n) => n.id === v)) return v;
  } catch {
    /* storage unavailable */
  }
  return window.matchMedia('(max-width: 860px), (max-width: 960px) and (max-height: 500px) and (orientation: landscape)').matches ? 'soundboard' : 'dashboard';
}

export function AppShell() {
  const { t } = useTranslation();
  const { connected, playing, stopAll, state } = useDeck();
  const { logoModel, logoFinish } = state.ui;
  const [view, setViewState] = useState<ViewId>(initialView);
  const [moreOpen, setMoreOpen] = useState(false);
  const [padsOpen, setPadsOpen] = useState(false);
  const [focus, setFocus] = useState(false);
  const enteredFullscreen = useRef(false);

  const setView = useCallback((v: ViewId) => {
    setViewState(v);
    setMoreOpen(false);
    try {
      localStorage.setItem(VIEW_KEY, v);
    } catch {
      /* ignore */
    }
  }, []);

  const enterFocus = useCallback(() => {
    setView('soundboard');
    setFocus(true);
    // Real fullscreen where allowed (desktop, iPad, Android). iPhone Safari doesn't allow it for
    // pages; Add to Home Screen gives the chrome-free equivalent there.
    document.documentElement.requestFullscreen?.()
      .then(() => { enteredFullscreen.current = true; })
      .catch(() => undefined);
  }, [setView]);

  const exitFocus = useCallback(() => {
    setFocus(false);
    if (document.fullscreenElement) document.exitFullscreen?.().catch(() => undefined);
  }, []);

  useEffect(() => {
    const onChange = () => {
      if (!document.fullscreenElement && enteredFullscreen.current) {
        enteredFullscreen.current = false;
        setFocus(false);
      }
    };
    document.addEventListener('fullscreenchange', onChange);
    return () => document.removeEventListener('fullscreenchange', onChange);
  }, []);

  // The browser tab follows the logo chosen in Settings.
  useEffect(() => {
    const link = document.querySelector<HTMLLinkElement>('link[rel="icon"]');
    if (link) link.href = `data:image/svg+xml,${encodeURIComponent(logoIconSvg(asLogoModel(logoModel), asLogoFinish(logoFinish)))}`;
  }, [logoModel, logoFinish]);

  const openAbout = useCallback(() => {
    openSettingsSection('about');
    setView('settings');
  }, [setView]);

  const live = playing.length > 0;
  const label = (id: ViewId) => t(`nav.${id}`);
  const short = (id: ViewId) => t(`nav.short.${id}`);

  const stopButton = (extra = '', short = false) => (
    <button
      className={`stop-all ${live ? 'is-live' : ''} ${extra}`}
      onClick={stopAll}
      aria-label={live ? t('common.stopAllCount', { count: playing.length }) : t('common.stopAll')}
    >
      <Square size={14} fill="currentColor" strokeWidth={0} />
      <span className="stop-all-label">{t(short ? 'common.stopShort' : 'common.stopAll')}</span>
      {live && <span className="stop-all-count num">{playing.length}</span>}
    </button>
  );

  const tab = ({ id, icon: Icon }: NavItem) => (
    <button key={id} className={`tab ${id === view ? 'is-active' : ''}`} aria-current={id === view ? 'page' : undefined} onClick={() => setView(id)}>
      <Icon size={21} strokeWidth={1.75} />
      <span>{short(id)}</span>
    </button>
  );

  return (
    <div className={`shell ${focus ? 'is-focus' : ''}`}>
      <aside className="sidebar" aria-label={t('nav.main')}>
        <div className="sidebar-brand">
          <Wordmark />
        </div>
        <nav className="nav">
          {NAV.map((g) => (
            <div className="nav-group" key={g.group}>
              <span className="nav-group-label">{t(`nav.${g.group}`)}</span>
              {g.items.map(({ id, icon: Icon }) => (
                <button key={id} className={`nav-item ${id === view ? 'is-active' : ''}`} aria-current={id === view ? 'page' : undefined} title={label(id)} onClick={() => setView(id)}>
                  <Icon size={18} strokeWidth={1.75} />
                  <span className="nav-label">{label(id)}</span>
                </button>
              ))}
            </div>
          ))}
        </nav>
        <div className="sidebar-foot">
          <MenuExtras onAbout={openAbout} />
          <SystemStatus connected={connected} />
        </div>
      </aside>

      <div className="main">
        <header className="mobilebar">
          <button className="mobilebar-brand" onClick={() => setView('dashboard')} aria-label={label('dashboard')}>
            <LogoMark size={28} />
          </button>
          <span className="mobilebar-title">{label(view)}</span>
          <StatusDot tone={connected ? 'ok' : 'warn'} pulse={!connected} label={connected ? t('nav.connected') : t('nav.reconnectingShort')} />
        </header>

        {!connected && (
          <div className="notice notice-warn" role="status">
            <StatusDot tone="warn" pulse /> {t('nav.reconnecting')}
          </div>
        )}

        <main className="content" key={view}>
          {view === 'dashboard' && <DashboardView go={setView} />}
          {view === 'soundboard' && <SoundboardView focus={focus} onFocus={enterFocus} />}
          {view === 'mixer' && <MixerView />}
          {view === 'deck' && <DeckView />}
          {view === 'youtube' && <YouTubeView />}
          {view === 'profiles' && <ProfilesView go={setView} />}
          {view === 'guide' && <GuideView go={setView} />}
          {view === 'settings' && <SettingsView go={setView} />}
        </main>

        {/* Desktop floating Stop All, and focus-mode controls */}
        <div className="dock">
          {focus && <IconButton className="dock-exit" label={t('nav.exitFocus')} icon={<Minimize2 size={18} />} onClick={exitFocus} />}
          {stopButton()}
        </div>
      </div>

      {/* Phone and tablet: native-style tab bar with Stop All in the middle. "Pads" opens the
          Soundboard / Smart Deck / YouTube chooser; the rest of the screens are under "More". */}
      <nav className="tabbar" aria-label={t('nav.main')}>
        {tab(ALL[0])}
        <button className={`tab ${isPads(view) ? 'is-active' : ''}`} aria-current={isPads(view) ? 'page' : undefined} onClick={() => setPadsOpen(true)} aria-haspopup="dialog">
          <LayoutGrid size={21} strokeWidth={1.75} />
          <span>{short('soundboard')}</span>
        </button>
        <div className="tab-stop">{stopButton('stop-all-tab', true)}</div>
        {tab(ALL.find((n) => n.id === 'mixer')!)}
        <button className={`tab ${MORE.some((n) => n.id === view) ? 'is-active' : ''}`} onClick={() => setMoreOpen(true)} aria-haspopup="dialog">
          <MoreHorizontal size={21} strokeWidth={1.75} />
          <span>{t('nav.more')}</span>
        </button>
      </nav>

      {padsOpen && (
        <PadsLauncher
          current={isPads(view) ? view : null}
          onChoose={(v) => {
            setView(v);
            setPadsOpen(false);
          }}
          onClose={() => setPadsOpen(false)}
        />
      )}

      <UpdatePrompt />

      {moreOpen && (
        <MoreLauncher
          items={MORE}
          current={view}
          status={<><MenuExtras onAbout={openAbout} /><SystemStatus connected={connected} /></>}
          onChoose={setView}
          onClose={() => setMoreOpen(false)}
        />
      )}
    </div>
  );
}

function tone(b: BusStatus): Tone {
  if (!b.enabled) return 'off';
  return b.ok ? 'ok' : 'bad';
}

/** Compact live status: control link, mic, call (virtual output) and headphones. */
function SystemStatus({ connected }: { connected: boolean }) {
  const { t } = useTranslation();
  const { state } = useDeck();
  const { status, audio } = state;
  const rows: { key: string; tone: Tone; note: string }[] = [
    { key: 'link', tone: connected ? 'ok' : 'warn', note: connected ? t('nav.connected') : t('nav.reconnectingShort') },
    {
      key: 'mic',
      tone: audio.micMuted && status.mic.enabled ? 'warn' : tone(status.mic),
      note: !status.mic.enabled ? t('common.off') : audio.micMuted ? t('common.muted') : status.mic.ok ? t('nav.status.live') : t('common.problem'),
    },
    { key: 'call', tone: tone(status.virtual), note: !status.virtual.enabled ? t('common.off') : status.virtual.ok ? t('nav.status.routed') : t('common.problem') },
    { key: 'ears', tone: tone(status.monitor), note: !status.monitor.enabled ? t('common.off') : status.monitor.ok ? t('common.active') : t('common.problem') },
  ];
  return (
    <div className="sys-status">
      {rows.map((r) => (
        <div key={r.key} className="sys-row">
          <StatusDot tone={r.tone} pulse={r.tone === 'warn' && r.key === 'link'} />
          <span className="sys-label">{t(`nav.status.${r.key}`)}</span>
          <span className="sys-note">{r.note}</span>
        </div>
      ))}
      <div className="sys-version faint num">{t('nav.versionLine', { version: state.server.version })}</div>
    </div>
  );
}
