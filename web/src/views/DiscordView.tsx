import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Eye, EyeOff, Gamepad2, Loader2, Radio, Wand2, X, Headphones, Music, Pencil, Plus, RotateCcw, Trash2, Trophy } from 'lucide-react';
import { api } from '../lib/api';
import type { PresenceButton, PresenceKind, PresenceOptions, PresencePreset, PresenceState, PresenceType } from '../lib/types';
import { useDeck } from '../state/DeckContext';
import { usePresence } from '../state/presence';
import { Button, Field, IconButton, Segmented, StatusDot, Switch } from '../ui/controls';
import { Card, PageHeader } from '../ui/Card';
import { LogoGlyph, LogoMark } from '../shell/Logo';
import { Chip } from '../ui/extras';
import { badUrl, clock, parseClock, sourceByName, sourceOfLink, SOURCES, YOUTUBE_ICON, youtubeId } from '../lib/presence';

type Draft = Omit<PresencePreset, 'id'> & { id?: string };
type Slot = 'activity' | 'music';
const slotOf = (p: Draft): Slot => (p.kind === 'music' ? 'music' : 'activity');

const TYPE_ICONS: Record<PresenceType, typeof Gamepad2> = { playing: Gamepad2, listening: Headphones, watching: Eye, competing: Trophy };

const blank = (kind: PresenceKind): Draft => ({
  label: '', kind, type: kind === 'music' ? 'listening' : 'playing', name: kind === 'music' ? 'YouTube' : '',
  details: '', detailsUrl: '', state: '', stateUrl: '', largeImage: '', largeText: '',
  smallImage: kind === 'music' ? YOUTUBE_ICON : '', smallText: kind === 'music' ? 'YouTube' : '',
  buttons: [], timer: kind === 'music' ? 'track' : 'elapsed', countdownMinutes: 15, durationSec: 0, positionSec: 0,
  statusDisplay: kind === 'music' ? 'details' : 'name',
});

function useNow(active: boolean) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    const id = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(id);
  }, [active]);
  return now;
}

export function DiscordView() {
  const { t } = useTranslation();
  const { run } = useDeck();
  const presence = usePresence();
  const [draft, setDraft] = useState<Draft | null>(null);
  const [draftStarted, setDraftStarted] = useState(() => Date.now());
  const [filter, setFilter] = useState<'all' | 'activity' | 'music'>('all');
  const [newOpen, setNewOpen] = useState(false);

  useEffect(() => {
    // Live updates come over the socket; this covers a page opened before the first push.
    if (!presence) void api.presence().catch(() => undefined);
  }, [presence]);

  if (!presence) return <div className="page discord"><PageHeader title={t('discord.title')} subtitle={t('discord.subtitle')} /></div>;

  const byId = (id: string | null) => presence.presets.find((p) => p.id === id) ?? null;
  // Each card is either what was chosen here (manual) or detected (auto), never both.
  const activityAuto = presence.activityMode === 'auto';
  const musicAuto = presence.musicMode === 'auto';
  const auto = presence.enabled && musicAuto ? presence.autoSong : null;
  const live = {
    activity: presence.enabled && !activityAuto ? byId(presence.activePresetId) : null,
    music: musicAuto ? auto?.preset ?? null : presence.enabled ? byId(presence.activeMusicPresetId) : null,
  };
  const liveAt = { activity: presence.appliedAt ?? Date.now(), music: auto ? auto.startedAt : presence.musicAppliedAt ?? Date.now() };
  const isShown = (id?: string) => !!id && presence.enabled
    && ((id === presence.activePresetId && !activityAuto) || (id === presence.activeMusicPresetId && !musicAuto));
  const draftSlot: Slot | null = draft ? slotOf(draft) : null;
  const editingActive = isShown(draft?.id);
  const cards = (['activity', 'music'] as Slot[])
    .map((slot) => draftSlot === slot
      ? { slot, preset: draft!, appliedAt: editingActive ? liveAt[slot] : draftStarted, draft: true }
      : { slot, preset: live[slot], appliedAt: liveAt[slot], draft: false })
    .filter((c): c is { slot: Slot; preset: Draft; appliedAt: number; draft: boolean } => !!c.preset);

  const edit = (d: Draft) => {
    setDraft(structuredClone(d));
    setDraftStarted(Date.now());
  };
  const close = () => setDraft(null);
  const shownCount = presence.presets.filter((p) => filter === 'all' || p.kind === filter);

  return (
    <div className="page discord">
      <header className="rp-head">
        <span className="rp-head-logo" aria-hidden><LogoMark size={30} /></span>
        <div className="rp-head-text">
          <h1>{t('discord.title')}</h1>
          <p>{t('discord.subtitle')}</p>
        </div>
        <div className="rp-status-pill">
          <StatusLine presence={presence} />
          <Switch checked={presence.enabled} label={t('discord.showOnProfile')} onChange={(on) => run(() => api.setPresenceEnabled(on))} />
        </div>
      </header>
      {presence.enabled && presence.error && <p className="rp-error" role="alert">{t('discord.refused', { message: presence.error })}</p>}

      <div className="rp-bento">
        <section className="rp-tile rp-profile-tile" aria-label={t('discord.previewLive')}>
          <div className="rp-tile-head">
            <div><span className="eyebrow">{t('discord.previewLive')}</span><h2>{t('discord.profileTitle')}</h2></div>
            {presence.status === 'connected' && <span className="rp-live-badge"><i aria-hidden />{t('common.live')}</span>}
          </div>
          <div className="rp-dprofile">
            <div className="rp-dbanner" />
            <div className="rp-davatar" aria-hidden>{(presence.user ?? 'G').slice(0, 1).toUpperCase()}</div>
            <div className="rp-dwho"><b>{presence.user ?? 'GameDeck'}</b></div>
            <div className="rp-dacts">
              {presence.enabled && activityAuto && <p className="rp-dnote"><Gamepad2 size={15} aria-hidden />{t('discord.modes.activityAutoNote')}</p>}
              {presence.enabled && musicAuto && !auto && <p className="rp-dnote"><Radio size={15} aria-hidden />{t('discord.modes.musicWaiting')}</p>}
              {cards.filter((c) => !c.draft).length === 0 && !(presence.enabled && (activityAuto || musicAuto)) && (
                <p className="rp-dnote">{t(!presence.enabled ? 'discord.emptyOff' : 'discord.emptyNone')}</p>
              )}
              {cards.filter((c) => !c.draft).map((c) => (
                <div key={c.slot} className="rp-live">
                  <Preview preset={c.preset} appliedAt={c.appliedAt} hint={false} />
                  <div className="rp-card-tools">
                    {c.slot === 'music' && auto && <span className="rp-auto-tag"><Radio size={13} aria-hidden />{t('discord.auto.from', { app: auto.appName })}</span>}
                    {c.preset.timer !== 'none' && !(c.slot === 'music' && auto) && (
                      <button type="button" onClick={() => run(() => api.restartPresence(c.slot))} title={t(c.preset.timer === 'track' ? 'discord.restartSong' : 'discord.restartTimer')}>
                        <RotateCcw size={13} aria-hidden />{t('discord.restartShort')}
                      </button>
                    )}
                    <button type="button" onClick={() => run(() => api.hidePresence(c.slot))}>
                      <EyeOff size={13} aria-hidden />{t('discord.hideShort')}
                    </button>
                  </div>
                </div>
              ))}
            </div>
          </div>
          <p className="rp-foot">{t('discord.buttonsHint')} {t('discord.privacyShort')}</p>
        </section>

        <ModesCard presence={presence} />
      </div>

      <section className="rp-tile rp-library">
        <div className="rp-lib-bar">
          <h2>{t('discord.presets')}</h2>
          <div className="rp-filters" role="tablist" aria-label={t('discord.presets')}>
            {(['all', 'activity', 'music'] as const).map((f) => (
              <button key={f} type="button" role="tab" aria-selected={filter === f} className={filter === f ? 'is-on' : ''} onClick={() => setFilter(f)}>
                {t(`discord.filters.${f}`)}
              </button>
            ))}
          </div>
          <div className="rp-new">
            <button type="button" className="rp-new-btn" aria-haspopup="menu" aria-expanded={newOpen} onClick={() => setNewOpen(!newOpen)}>
              <Plus size={16} aria-hidden />{t('discord.new')}
            </button>
            {newOpen && (
              <div className="rp-new-menu" role="menu" onMouseLeave={() => setNewOpen(false)}>
                <button type="button" role="menuitem" onClick={() => { setNewOpen(false); edit(blank('activity')); }}><Gamepad2 size={16} aria-hidden />{t('discord.newActivity')}</button>
                <button type="button" role="menuitem" onClick={() => { setNewOpen(false); edit(blank('music')); }}><Music size={16} aria-hidden />{t('discord.newSongLink')}</button>
              </div>
            )}
          </div>
        </div>

        {presence.presets.length === 0 ? <Templates onPick={edit} /> : (
          <ul className="rp-rows">
            {shownCount.map((p) => {
              const Icon = p.kind === 'music' ? Music : TYPE_ICONS[p.type];
              const on = isShown(p.id);
              const sub = p.kind === 'music' ? [p.state, p.durationSec ? clock(p.durationSec) : ''].filter(Boolean).join(' · ') : p.details || p.name;
              return (
                <li key={p.id} className={`rp-row ${on ? 'is-live' : ''}`}>
                  <button type="button" className="rp-row-main" onClick={() => run(() => api.activatePresencePreset(p.id))} title={t('discord.showThis')}>
                    <span className="rp-thumb" aria-hidden>
                      {p.largeImage && !badUrl(p.largeImage) ? <img src={p.largeImage} alt="" loading="lazy" referrerPolicy="no-referrer" /> : <Icon size={22} />}
                    </span>
                    <span className="rp-row-text">
                      <b>{presetTitle(p, t)}</b>
                      <small><span className={`rp-kind ${p.kind}`}>{p.kind === 'music' ? p.name || t('discord.types.listening') : t(`discord.types.${p.type}`)}</span>{sub}</small>
                    </span>
                    <span className="rp-show">{on ? <><i aria-hidden />{t('discord.onProfile')}</> : t('discord.show')}</span>
                  </button>
                  <IconButton label={t('discord.edit')} icon={<Pencil size={16} />} onClick={() => edit(p)} />
                </li>
              );
            })}
          </ul>
        )}
      </section>

      {draft && (
        <EditorSheet onClose={close} eyebrow={t(draft.id ? 'discord.editing' : draft.kind === 'music' ? 'discord.newSong' : 'discord.newActivity')} title={presetTitle(draft, t)}>
          <Editor key={draft.id ?? 'new'} draft={draft} isActive={editingActive} onChange={setDraft} onClose={close} />
          <aside className="rp-sheet-side">
            <span className="eyebrow">{t('discord.previewDraft')}</span>
            <Preview preset={draft} appliedAt={editingActive ? liveAt[slotOf(draft)] : draftStarted} />
            <p className="faint small">{t(draft.kind === 'music' ? 'discord.fallbackMusic' : 'discord.fallbackActivity')}</p>
          </aside>
        </EditorSheet>
      )}
    </div>
  );
}

/** The editor over the page; on phones it rises from the bottom. Esc or a tap outside closes it. */
function EditorSheet({ onClose, eyebrow, title, children }: { onClose(): void; eyebrow: string; title: string; children: ReactNode }) {
  const { t } = useTranslation();
  useEffect(() => {
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', key);
    const overflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => { document.removeEventListener('keydown', key); document.body.style.overflow = overflow; };
  }, [onClose]);
  return (
    <div className="rp-scrim" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="rp-sheet" role="dialog" aria-modal="true">
        {/* Stays at the top while the sheet scrolls under it, like a browser's toolbar. */}
        <header className="rp-sheet-bar">
          <div className="rp-sheet-titles"><span className="eyebrow">{eyebrow}</span><h3>{title}</h3></div>
          <button type="button" className="rp-sheet-close" aria-label={t('common.close')} title={t('common.close')} onClick={onClose}><X size={18} /></button>
        </header>
        {children}
      </div>
    </div>
  );
}

function presetTitle(p: Draft, t: (k: string) => string) {
  if (p.label) return p.label;
  if (p.kind === 'music') return p.details || t('discord.untitledSong');
  return p.name || p.details || t('discord.untitledActivity');
}

function StatusLine({ presence }: { presence: PresenceState }) {
  const { t } = useTranslation();
  const tone = presence.status === 'connected' ? 'ok' : presence.status === 'waiting' ? 'warn' : presence.status === 'idle' ? 'accent' : 'off';
  const text = presence.status === 'connected'
    ? (presence.user ? t('discord.connectedAs', { user: presence.user }) : t('discord.status.connected'))
    : t(`discord.statusLong.${presence.status}`);
  return <span className="rp-status muted small"><StatusDot tone={tone} pulse={presence.status === 'waiting'} />{text}</span>;
}


/** The icon of the Discord application: the GameDeck logo, green with a note for GameDeck Music. */
function AppLogo({ music }: { music: boolean }) {
  return (
    <span className={`rp-art-empty ${music ? 'is-music' : ''}`}>
      <LogoGlyph model="bigkey" finish={music ? 'emerald' : 'cobalt'} size={44} />
      {music && <Music className="rp-art-note" size={15} strokeWidth={2.4} aria-hidden />}
    </span>
  );
}

/** Looks like the activity card on a Discord profile, ticking like it does. */
export function Preview({ preset: p, appliedAt, hint = true }: { preset: Draft; appliedAt: number; hint?: boolean }) {
  const { t } = useTranslation();
  const timed = p.timer !== 'none';
  const now = useNow(timed);
  const name = p.name || 'GameDeck';
  const music = p.kind === 'music' || p.type === 'listening';
  const buttons = p.buttons.filter((b) => b.label.trim() && b.url.trim() && !badUrl(b.url));
  const [failedImage, setFailedImage] = useState<string | null>(null);

  let timer: ReactNode = null;
  if (p.timer === 'elapsed') {
    timer = <span className="rp-time num">{t('discord.elapsed', { time: clock((now - appliedAt) / 1000) })}</span>;
  } else if (p.timer === 'countdown') {
    const left = (appliedAt + p.countdownMinutes * 60000 - now) / 1000;
    timer = left > 0 ? <span className="rp-time num">{t('discord.left', { time: clock(left) })}</span> : null;
  } else if (p.timer === 'track' && p.durationSec > 0) {
    const played = ((now - appliedAt) / 1000 + p.positionSec) % p.durationSec;
    timer = (
      <div className="rp-progress">
        <span className="num">{clock(played)}</span>
        <span className="rp-bar"><span style={{ width: `${(played / p.durationSec) * 100}%` }} /></span>
        <span className="num">{clock(p.durationSec)}</span>
      </div>
    );
  }

  return (
    <div className="rp-card">
      <div className="rp-card-head">{t(`discord.preview.${p.type}`, { name })}</div>
      <div className="rp-card-body">
        <div className="rp-art">
          {/* Without a working image Discord shows the application's own logo: GameDeck, or GameDeck Music for songs. */}
          {p.largeImage && !badUrl(p.largeImage) && failedImage !== p.largeImage
            ? <img src={p.largeImage} alt="" title={p.largeText || undefined} referrerPolicy="no-referrer" onError={() => setFailedImage(p.largeImage)} />
            : <AppLogo music={p.kind === 'music'} />}
          {p.smallImage && !badUrl(p.smallImage) && <img className="rp-art-small" src={p.smallImage} alt="" title={p.smallText || undefined} referrerPolicy="no-referrer" />}
        </div>
        <div className="rp-lines">
          {p.details && <strong className={p.detailsUrl ? 'is-link' : ''}>{p.details}</strong>}
          {p.state && <span className={p.stateUrl ? 'is-link' : ''}>{p.state}</span>}
          {music && p.largeText && <span>{p.largeText}</span>}
          {!music && timer}
          {music && p.timer !== 'track' && timer}
        </div>
      </div>
      {music && p.timer === 'track' && timer}
      {buttons.length > 0 && (
        <div className="rp-buttons">
          {buttons.map((b, i) => <span key={i} className="rp-btn">{b.label}</span>)}
          {hint && <span className="faint small">{t('discord.buttonsHint')}</span>}
        </div>
      )}
    </div>
  );
}

function Templates({ onPick }: { onPick(d: Draft): void }) {
  const { t } = useTranslation();
  const items: { key: string; icon: ReactNode; draft: Draft }[] = [
    { key: 'gaming', icon: <Gamepad2 size={22} />, draft: { ...blank('activity'), label: t('discord.templates.gaming.label'), details: t('discord.templates.gaming.details'), state: t('discord.templates.gaming.state') } },
    { key: 'music', icon: <Headphones size={22} />, draft: { ...blank('music'), label: t('discord.templates.music.label') } },
    { key: 'brb', icon: <RotateCcw size={22} />, draft: { ...blank('activity'), label: t('discord.templates.brb.label'), name: t('discord.templates.brb.name'), details: t('discord.templates.brb.details'), timer: 'countdown', countdownMinutes: 15 } },
    { key: 'watching', icon: <Eye size={22} />, draft: { ...blank('activity'), type: 'watching', label: t('discord.templates.watching.label'), name: t('discord.templates.watching.name'), details: t('discord.templates.watching.details') } },
  ];
  return (
    <div className="rp-templates">
      <p className="muted small">{t('discord.templatesIntro')}</p>
      <div className="rp-template-grid">
        {items.map((i) => (
          <button key={i.key} type="button" className="rp-template" onClick={() => onPick(i.draft)}>
            <span aria-hidden>{i.icon}</span>
            <strong>{i.draft.label}</strong>
            <span className="faint small">{t(`discord.templates.${i.key}.body`)}</span>
          </button>
        ))}
      </div>
    </div>
  );
}

function TextInput({ value, onChange, max = 128, placeholder, invalid, label }: {
  value: string; onChange(v: string): void; max?: number; placeholder?: string; invalid?: boolean; label: string;
}) {
  return (
    <input className={`input ${invalid ? 'is-invalid' : ''}`} value={value} maxLength={max} placeholder={placeholder} aria-label={label}
      aria-invalid={invalid || undefined} onChange={(e) => onChange(e.target.value)} />
  );
}

/** Whether an image address actually loads (a deleted picture, a page instead of an image). */
function useImageBroken(url: string, check: boolean) {
  const [broken, setBroken] = useState<string | null>(null);
  useEffect(() => {
    if (!check || !url.trim() || badUrl(url)) return;
    let alive = true;
    const img = new Image();
    img.referrerPolicy = 'no-referrer';
    img.onerror = () => alive && setBroken(url);
    img.onload = () => alive && setBroken(null);
    const timer = window.setTimeout(() => { img.src = url.trim(); }, 400);
    return () => { alive = false; window.clearTimeout(timer); };
  }, [url, check]);
  return check && broken === url;
}

function UrlField({ label, hint, value, onChange, placeholder, wrong, image }: { label: string; hint?: ReactNode; value: string; onChange(v: string): void; placeholder?: string; wrong?: boolean; image?: 'activity' | 'music' }) {
  const { t } = useTranslation();
  const bad = badUrl(value);
  const invalid = bad || !!wrong;
  // An image that doesn't load is not an error: Discord then shows the application's logo instead.
  const broken = useImageBroken(value, !!image);
  return (
    <Field label={label} hint={bad ? <span className="rp-invalid">{t('discord.badUrl')}</span>
      : broken ? <span className="rp-warn">{t(image === 'music' ? 'discord.imageBrokenMusic' : 'discord.imageBroken')}</span> : hint}>
      <TextInput value={value} onChange={onChange} max={512} placeholder={placeholder ?? 'https://'} invalid={invalid} label={label} />
    </Field>
  );
}

function Editor({ draft: d, isActive, onChange, onClose }: { draft: Draft; isActive: boolean; onChange(d: Draft): void; onClose(): void }) {
  const { t } = useTranslation();
  const { run, toast } = useDeck();
  const [duration, setDuration] = useState(d.durationSec ? clock(d.durationSec) : '');
  const [position, setPosition] = useState(d.positionSec ? clock(d.positionSec) : '');
  const [confirmDelete, setConfirmDelete] = useState(false);
  const music = d.kind === 'music';
  // A saved song keeps its service; a new one follows the first link pasted into it.
  const [sourceChosen, setSourceChosen] = useState(!!d.id);
  const [lookup, setLookup] = useState<{ state: 'idle' | 'busy' | 'done' | 'error'; text?: string }>({ state: 'idle' });
  const latest = useRef(d);
  latest.current = d;
  const lookedUp = useRef(d.detailsUrl.trim());
  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => onChange({ ...d, [key]: value });
  const patch = (p: Partial<Draft>) => onChange({ ...d, ...p });

  const durationSec = parseClock(duration);
  const positionSec = parseClock(position);
  const urls = [d.detailsUrl, d.stateUrl, d.largeImage, d.smallImage, ...d.buttons.map((b) => b.url)];
  const halfButtons = d.buttons.some((b) => !!b.label.trim() !== !!b.url.trim());
  // A song from Spotify needs a Spotify link; a service GameDeck doesn't know takes any link.
  const source = music ? sourceByName(d.name) : undefined;
  const linkSource = sourceOfLink(d.detailsUrl);
  const wrongSource = !!source && !!d.detailsUrl.trim() && !badUrl(d.detailsUrl) && linkSource !== source;
  const invalid = urls.some(badUrl) || Number.isNaN(durationSec) || Number.isNaN(positionSec) || halfButtons || wrongSource;

  const final = useMemo((): Draft => {
    const length = Number.isNaN(durationSec) ? 0 : durationSec;
    const timer = music ? (length > 0 ? 'track' : d.timer === 'track' ? 'elapsed' : d.timer) : d.timer === 'track' ? 'elapsed' : d.timer;
    return {
      ...d,
      durationSec: length,
      positionSec: Number.isNaN(positionSec) ? 0 : Math.min(positionSec, Math.max(0, length - 1)),
      timer,
      buttons: d.buttons.filter((b) => b.label.trim() && b.url.trim()),
    };
  }, [d, durationSec, positionSec, music]);

  // The preview above shows the draft as it will look, including the song length typed here.
  useEffect(() => {
    if (final.durationSec !== d.durationSec || final.positionSec !== d.positionSec || final.timer !== d.timer) onChange({ ...d, durationSec: final.durationSec, positionSec: final.positionSec, timer: final.timer });
  }, [final.durationSec, final.positionSec, final.timer]); // eslint-disable-line react-hooks/exhaustive-deps

  const save = (show: boolean) => run(async () => {
    const body = final;
    const saved = body.id ? await api.savePresencePreset(body as PresencePreset) : await api.createPresencePreset(body);
    if (show && !isActive) await api.activatePresencePreset(saved.id);
    toast(t(show || isActive ? 'discord.savedShown' : 'discord.saved'), 'success');
    onClose();
  });

  const setLink = (link: string) => {
    const id = youtubeId(link);
    // A YouTube link brings its own cover, unless one was chosen already.
    const cover = id && (!d.largeImage || /^https:\/\/i\.ytimg\.com\//.test(d.largeImage)) ? `https://i.ytimg.com/vi/${id}/hqdefault.jpg` : d.largeImage;
    const from = sourceOfLink(link);
    const follow = !sourceChosen && from && from.name !== d.name ? sourcePatch(from.name, d) : {};
    patch({ detailsUrl: link, largeImage: cover, ...follow });
  };

  /** The service name with its badge, unless the person chose a small image of their own. */
  function sourcePatch(name: string, from: Draft): Partial<Draft> {
    const known = sourceByName(name);
    const ours = !from.smallImage || SOURCES.some((s) => s.icon === from.smallImage);
    return { name, ...(ours ? { smallImage: known?.icon ?? '', smallText: known?.name ?? '' } : {}) };
  }

  const setSource = (name: string) => {
    setSourceChosen(true);
    patch(sourcePatch(name, d));
  };

  /** Title, artist, album, cover and length from the service the link belongs to. */
  const fillFromLink = useCallback(async (link: string) => {
    lookedUp.current = link;
    setLookup({ state: 'busy' });
    try {
      const song = await api.lookupSong(link);
      const now = latest.current;
      if (now.detailsUrl.trim() !== link) return; // the link changed meanwhile
      onChange({
        ...now,
        details: song.title || now.details,
        state: song.artist || now.state,
        largeText: song.album || now.largeText,
        largeImage: song.cover || now.largeImage,
      });
      if (song.durationSec > 0) setDuration(clock(song.durationSec));
      setLookup({ state: 'done', text: t('discord.lookupDone', { source: song.source }) });
    } catch (e) {
      if (latest.current.detailsUrl.trim() === link) setLookup({ state: 'error', text: e instanceof Error ? e.message : String(e) });
    }
  }, [onChange, t]);

  // Pasting a link fills the song in by itself, after a short pause in typing.
  useEffect(() => {
    const link = d.detailsUrl.trim();
    if (!music || !link || badUrl(link) || wrongSource || !sourceOfLink(link) || link === lookedUp.current) return;
    const timer = window.setTimeout(() => void fillFromLink(link), 500);
    return () => window.clearTimeout(timer);
  }, [d.detailsUrl, music, wrongSource, fillFromLink]);

  const setButton = (index: number, value: Partial<PresenceButton>) => {
    const buttons = [...d.buttons];
    while (buttons.length <= index) buttons.push({ label: '', url: '' });
    buttons[index] = { ...buttons[index], ...value };
    set('buttons', buttons);
  };

  const typeOptions = (['playing', 'listening', 'watching', 'competing'] as PresenceType[]).map((v) => {
    const Icon = TYPE_ICONS[v];
    return { value: v, label: <span className="rp-type"><Icon size={15} aria-hidden />{t(`discord.types.${v}`)}</span> };
  });

  return (
    <Card className="rp-editor" eyebrow={t(d.id ? 'discord.editing' : music ? 'discord.newSong' : 'discord.newActivity')} title={presetTitle(d, t)}>
      <div className="rp-form">
        <Field label={t('discord.f.label')} hint={t('discord.f.labelHint')}>
          <TextInput value={d.label} onChange={(v) => set('label', v)} max={40} label={t('discord.f.label')} placeholder={music ? t('discord.untitledSong') : t('discord.untitledActivity')} />
        </Field>

        {music ? (
          <>
            <Field label={t('discord.f.source')} hint={t('discord.f.sourceHint')}>
              <TextInput value={d.name} onChange={setSource} label={t('discord.f.source')} placeholder="YouTube" />
              <div className="rp-chips">
                {SOURCES.map((s) => <Chip key={s.name} active={source === s} icon={<img className="rp-chip-icon" src={s.icon} alt="" referrerPolicy="no-referrer" />} onClick={() => setSource(s.name)}>{s.name}</Chip>)}
              </div>
            </Field>
            <UrlField label={t('discord.f.songLink')} value={d.detailsUrl} onChange={setLink} wrong={wrongSource}
              placeholder={source ? `https://${source.hosts[0]}/…` : undefined}
              hint={wrongSource ? (
                <span className="rp-invalid">
                  {t('discord.wrongSource', { source: source!.name, host: source!.hosts[0] })}
                  {linkSource && <> <button type="button" className="rp-link-btn" onClick={() => setSource(linkSource.name)}>{t('discord.switchSource', { source: linkSource.name })}</button></>}
                </span>
              ) : lookup.state === 'busy' ? (
                <span className="rp-lookup"><Loader2 size={14} className="rp-spin" aria-hidden />{t('discord.lookupBusy')}</span>
              ) : lookup.state !== 'idle' ? (
                <span className={lookup.state === 'error' ? 'rp-invalid' : 'rp-ok'}>{lookup.text}</span>
              ) : t('discord.f.songLinkHint')} />
            {!!d.detailsUrl.trim() && !badUrl(d.detailsUrl) && !wrongSource && !!linkSource && lookup.state !== 'busy' && (
              <div className="rp-refill">
                <Button size="sm" variant="ghost" icon={<Wand2 size={15} />} onClick={() => void fillFromLink(d.detailsUrl.trim())}>{t('discord.lookupAgain')}</Button>
              </div>
            )}
            <div className="field-pair">
              <Field label={t('discord.f.song')}><TextInput value={d.details} onChange={(v) => set('details', v)} label={t('discord.f.song')} /></Field>
              <Field label={t('discord.f.artist')}><TextInput value={d.state} onChange={(v) => set('state', v)} label={t('discord.f.artist')} /></Field>
            </div>
            <div className="field-pair">
              <Field label={t('discord.f.album')}><TextInput value={d.largeText} onChange={(v) => set('largeText', v)} label={t('discord.f.album')} /></Field>
              <Field label={t('discord.f.duration')} hint={Number.isNaN(durationSec) ? <span className="rp-invalid">{t('discord.badTime')}</span> : t('discord.f.durationHint')}>
                <TextInput value={duration} onChange={setDuration} max={8} label={t('discord.f.duration')} placeholder="3:33" invalid={Number.isNaN(durationSec)} />
              </Field>
            </div>
            <UrlField label={t('discord.f.cover')} hint={t('discord.f.coverHint')} image="music" value={d.largeImage} onChange={(v) => set('largeImage', v)} />
          </>
        ) : (
          <>
            <Field label={t('discord.f.type')} hint={t('discord.f.typeHint')}>
              <Segmented value={d.type} options={typeOptions} onChange={(v) => set('type', v)} label={t('discord.f.type')} size="sm" />
            </Field>
            <Field label={t('discord.f.name')} hint={t('discord.f.nameHint')}>
              <TextInput value={d.name} onChange={(v) => set('name', v)} label={t('discord.f.name')} placeholder="GameDeck" />
            </Field>
            <div className="field-pair">
              <Field label={t('discord.f.details')}><TextInput value={d.details} onChange={(v) => set('details', v)} label={t('discord.f.details')} placeholder={t('discord.templates.gaming.details')} /></Field>
              <Field label={t('discord.f.state')}><TextInput value={d.state} onChange={(v) => set('state', v)} label={t('discord.f.state')} placeholder={t('discord.templates.gaming.state')} /></Field>
            </div>
            <UrlField label={t('discord.f.image')} hint={t('discord.f.imageHint')} image="activity" value={d.largeImage} onChange={(v) => set('largeImage', v)} />
            {d.largeImage && (
              <Field label={t('discord.f.imageText')} hint={t('discord.f.imageTextHint')}>
                <TextInput value={d.largeText} onChange={(v) => set('largeText', v)} label={t('discord.f.imageText')} />
              </Field>
            )}
            <Field label={t('discord.f.timer')}>
              <Segmented value={d.timer === 'track' ? 'elapsed' : d.timer} onChange={(v) => set('timer', v)} label={t('discord.f.timer')} size="sm"
                options={[{ value: 'none', label: t('discord.timers.none') }, { value: 'elapsed', label: t('discord.timers.elapsed') }, { value: 'countdown', label: t('discord.timers.countdown') }]} />
              {d.timer === 'countdown' && (
                <label className="rp-minutes">
                  <input className="input" type="number" min={1} max={1440} value={d.countdownMinutes} aria-label={t('discord.f.minutes')}
                    onChange={(e) => set('countdownMinutes', Math.max(1, Math.min(1440, Math.round(Number(e.target.value) || 1))))} />
                  <span className="muted small">{t('discord.f.minutes')}</span>
                </label>
              )}
            </Field>
          </>
        )}

        <Field label={t('discord.f.buttons')} hint={halfButtons ? <span className="rp-invalid">{t('discord.halfButton')}</span> : t('discord.f.buttonsHint')}>
          <div className="rp-button-rows">
            {[0, 1].map((i) => (
              <div key={i} className="rp-button-row">
                <TextInput value={d.buttons[i]?.label ?? ''} onChange={(v) => setButton(i, { label: v })} max={32} label={t('discord.f.buttonText', { n: i + 1 })}
                  placeholder={i === 0 && music ? t('discord.listenOn', { source: d.name || 'YouTube' }) : t('discord.f.buttonText', { n: i + 1 })} />
                <TextInput value={d.buttons[i]?.url ?? ''} onChange={(v) => setButton(i, { url: v })} max={512} label={t('discord.f.buttonUrl', { n: i + 1 })}
                  placeholder="https://" invalid={badUrl(d.buttons[i]?.url ?? '')} />
              </div>
            ))}
            {music && d.detailsUrl && !badUrl(d.detailsUrl) && !d.buttons.some((b) => b.url === d.detailsUrl) && (
              <Button size="sm" variant="ghost" icon={<Plus size={15} />}
                onClick={() => setButton(d.buttons[0]?.url ? 1 : 0, { label: t('discord.listenOn', { source: d.name || 'YouTube' }).slice(0, 32), url: d.detailsUrl })}>
                {t('discord.addListenButton')}
              </Button>
            )}
          </div>
        </Field>

        <details className="rp-advanced">
          <summary>{t('discord.advanced')}</summary>
          <Field label={t('discord.f.memberList')} hint={t('discord.f.memberListHint')}>
            <Segmented value={d.statusDisplay} onChange={(v) => set('statusDisplay', v)} label={t('discord.f.memberList')} size="sm"
              options={[
                { value: 'name', label: t('discord.display.name') },
                { value: 'details', label: t(music ? 'discord.display.song' : 'discord.display.details') },
                { value: 'state', label: t(music ? 'discord.display.artist' : 'discord.display.state') },
              ]} />
          </Field>
          {!music && (
            <div className="field-pair">
              <UrlField label={t('discord.f.detailsUrl')} value={d.detailsUrl} onChange={(v) => set('detailsUrl', v)} />
              <UrlField label={t('discord.f.stateUrl')} value={d.stateUrl} onChange={(v) => set('stateUrl', v)} />
            </div>
          )}
          {music && (
            <Field label={t('discord.f.startAt')} hint={Number.isNaN(positionSec) ? <span className="rp-invalid">{t('discord.badTime')}</span> : t('discord.f.startAtHint')}>
              <TextInput value={position} onChange={setPosition} max={8} label={t('discord.f.startAt')} placeholder="0:00" invalid={Number.isNaN(positionSec)} />
            </Field>
          )}
          <div className="field-pair">
            <UrlField label={t('discord.f.smallImage')} hint={t('discord.f.smallImageHint')} value={d.smallImage} onChange={(v) => set('smallImage', v)} />
            <Field label={t('discord.f.smallText')}>
              <TextInput value={d.smallText} onChange={(v) => set('smallText', v)} label={t('discord.f.smallText')} />
            </Field>
          </div>
        </details>
      </div>

      <div className="rp-editor-foot">
        {d.id && (confirmDelete
          ? <Button variant="danger" icon={<Trash2 size={15} />} onClick={() => run(async () => { await api.deletePresencePreset(d.id!); onClose(); })}>{t('discord.confirmDelete')}</Button>
          : <Button variant="ghost" icon={<Trash2 size={15} />} onClick={() => setConfirmDelete(true)}>{t('discord.delete')}</Button>)}
        <span className="rp-spacer" />
        <Button variant="ghost" onClick={onClose}>{t('discord.cancel')}</Button>
        {!isActive && <Button disabled={invalid} onClick={() => save(false)}>{t('discord.save')}</Button>}
        <Button variant="primary" disabled={invalid} onClick={() => save(true)}>{t(isActive ? 'discord.saveLive' : 'discord.saveShow')}</Button>
      </div>
    </Card>
  );
}

/**
 * What each card shows: what was chosen here (manual) or what is detected (auto). Automatic activity
 * leaves the card to Discord's own game detection; automatic music follows what plays.
 */
function ModesCard({ presence }: { presence: PresenceState }) {
  const { t } = useTranslation();
  const { run } = useDeck();
  const save = (o: PresenceOptions) => run(() => api.setPresenceOptions(o));
  const auto = presence.autoMusic;
  const toggleApp = (key: string) =>
    save({ musicApps: auto.apps.includes(key) ? auto.apps.filter((a) => a !== key) : [...auto.apps, key] });
  const song = presence.musicMode === 'auto' ? presence.autoSong : null;
  const summary = (slot: 'activity' | 'music') => {
    const mode = slot === 'music' ? presence.musicMode : presence.activityMode;
    if (mode === 'auto') return t(slot === 'music' ? 'discord.modes.musicAuto' : 'discord.modes.activityAuto');
    const chosen = presence.presets.find((p) => p.id === (slot === 'music' ? presence.activeMusicPresetId : presence.activePresetId));
    return chosen ? t('discord.modes.chosen', { name: presetTitle(chosen, t) }) : t('discord.modes.nothing');
  };

  return (
    <section className="rp-tile rp-modes">
      <div className="rp-tile-head"><div><span className="eyebrow">{t('discord.modes.eyebrow')}</span><h2>{t('discord.modes.title')}</h2></div></div>
      <div className="rp-mode-list">
        <section className={`rp-mode ${presence.activityMode === 'auto' ? 'is-auto' : ''}`}>
          <div className="rp-mode-row">
            <span className="rp-mode-icon" aria-hidden><Gamepad2 size={20} /></span>
            <div className="rp-mode-text"><b>{t('discord.modes.activity')}</b><small>{summary('activity')}</small></div>
            <SlideSwitch value={presence.activityMode} label={t('discord.modes.activity')} onChange={(v) => save({ activityMode: v })} manual={t('discord.modes.manual')} auto={t('discord.modes.auto')} />
          </div>
          {presence.activityMode === 'auto' && <p className="rp-mode-hint">{t('discord.modes.activityAutoHint')}</p>}
        </section>

        <section className={`rp-mode ${presence.musicMode === 'auto' ? 'is-auto' : ''}`}>
          <div className="rp-mode-row">
            <span className="rp-mode-icon" aria-hidden><Music size={20} /></span>
            <div className="rp-mode-text"><b>{t('discord.modes.music')}</b><small>{summary('music')}</small></div>
            <SlideSwitch value={presence.musicMode} label={t('discord.modes.music')} onChange={(v) => save({ musicMode: v })} manual={t('discord.modes.manual')} auto={t('discord.modes.auto')} />
          </div>
          {presence.musicMode === 'auto' && (
            <div className="rp-mode-extra">
              <p className={`rp-now ${song ? 'is-playing' : ''}`} role="status">
                {song ? <span className="rp-eq" aria-hidden><i /><i /><i /></span> : <Radio size={15} aria-hidden />}
                <span>{song ? t('discord.auto.now', { song: [song.preset.details, song.preset.state].filter(Boolean).join(' · '), app: song.appName }) : t('discord.auto.nothing')}</span>
              </p>
              <div className="rp-sources" aria-label={t('discord.auto.apps')}>
                {presence.musicApps.map((a) => (
                  <button key={a.key} type="button" className={`rp-source ${auto.apps.includes(a.key) ? 'is-on' : ''}`} aria-pressed={auto.apps.includes(a.key)} onClick={() => toggleApp(a.key)}>{a.name}</button>
                ))}
              </div>
              <p className="rp-mode-hint">{t('discord.auto.appsHint')}</p>
              <div className="rp-inline">
                <div><b>{t('discord.auto.covers')}</b><small>{t('discord.auto.coversHint')}</small></div>
                <Switch checked={auto.findCovers} label={t('discord.auto.covers')} onChange={(on) => save({ findCovers: on })} />
              </div>
            </div>
          )}
        </section>
      </div>
    </section>
  );
}

/** Manual or automatic, as one pill with a sliding thumb (only its position animates). */
function SlideSwitch({ value, onChange, label, manual, auto }: { value: 'manual' | 'auto'; onChange(v: 'manual' | 'auto'): void; label: string; manual: string; auto: string }) {
  return (
    <div className={`rp-slide ${value === 'auto' ? 'is-auto' : ''}`} role="radiogroup" aria-label={label}>
      <button type="button" role="radio" aria-checked={value === 'manual'} onClick={() => onChange('manual')}>{manual}</button>
      <button type="button" role="radio" aria-checked={value === 'auto'} onClick={() => onChange('auto')}>{auto}</button>
    </div>
  );
}
