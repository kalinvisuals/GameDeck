import { useEffect, useState, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { AppWindow, Check, Copy, Keyboard, Pencil, Play, Plus, RefreshCw, Save, Trash2, Volume2, VolumeX, X, Zap } from 'lucide-react';
import { api } from '../lib/api';
import { latestSender } from '../lib/latest';
import { SOUND_ICONS, SoundIcon } from '../lib/icons';
import type { AppAudio, Scene, SceneApp, SceneChannels, ScenePatch } from '../lib/types';
import { useDeck } from '../state/DeckContext';
import { Badge, Button, Field, IconButton, Select, Switch } from '../ui/controls';
import { Card, EmptyState } from '../ui/Card';
import { Dialog } from '../ui/Dialog';
import { Slider } from '../ui/Slider';

const pct = (v: number) => `${Math.round(v * 100)}%`;

type AppChange = { volume?: number; muted?: boolean };
const appSender = latestSender<AppChange>((a, b) => ({ ...a, ...b }));
const sceneSender = latestSender<ScenePatch>((a, b) => ({ ...a, ...b }));

/**
 * Re-reads the Windows volume mixer while the Mixer is on screen: on open, when the window regains
 * focus, and on demand. Nothing polls in the background.
 */
function useAppsRefresh() {
  useEffect(() => {
    const refresh = () => api.refreshApps().catch(() => undefined);
    const onVisible = () => document.visibilityState === 'visible' && refresh();
    refresh();
    window.addEventListener('focus', refresh);
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      window.removeEventListener('focus', refresh);
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, []);
}

/** Per-application volume and mute, the same controls as the Windows volume mixer. */
export function AppsCard() {
  const { t } = useTranslation();
  const { state, run } = useDeck();
  useAppsRefresh();
  const apps = state.apps;

  return (
    <Card
      className="apps-card"
      eyebrow={t('apps.eyebrow')}
      title={t('apps.title')}
      pad={false}
      actions={<Button size="sm" icon={<RefreshCw size={15} />} onClick={() => run(() => api.refreshApps())}>{t('apps.refresh')}</Button>}
    >
      {apps.length === 0 ? (
        <EmptyState compact icon={<AppWindow size={20} strokeWidth={1.5} />} title={t('apps.empty')}>
          {t('apps.emptyBody')}
        </EmptyState>
      ) : (
        <ul className="app-list">
          {apps.map((a) => <AppRow key={a.key} app={a} />)}
        </ul>
      )}
      <p className="faint small apps-note">{t('apps.note')}</p>
    </Card>
  );
}

function AppRow({ app }: { app: AppAudio }) {
  const { t } = useTranslation();
  const { run } = useDeck();
  // What the user just set, shown until the PC reports the same values (or gives up on it).
  const [pending, setPending] = useState<AppChange | null>(null);
  useEffect(() => {
    if (!pending) return;
    const confirmed = (pending.volume === undefined || Math.abs(pending.volume - app.volume) < 0.005) && (pending.muted === undefined || pending.muted === app.muted);
    if (confirmed && !appSender.busy(app.key)) {
      setPending(null);
      return;
    }
    const timer = setTimeout(() => setPending(null), 2000);
    return () => clearTimeout(timer);
  }, [pending, app.key, app.volume, app.muted]);

  const volume = pending?.volume ?? app.volume;
  const muted = pending?.muted ?? app.muted;
  const change = (c: AppChange) => {
    setPending((p) => ({ ...p, ...c }));
    appSender.send(app.key, c, (v) => run(() => api.setApp(app.key, v)));
  };

  return (
    <li className={`app-row ${muted ? 'is-muted' : ''}`}>
      <div className="app-name">
        <strong>{app.name}</strong>
        <span className="faint small">{app.key.startsWith('#') ? t('apps.windows') : app.key}</span>
      </div>
      <Slider label={t('apps.volumeOf', { name: app.name })} value={volume} max={1} onChange={(v) => change({ volume: v })} />
      <IconButton
        label={muted ? t('mixer.unmute') : t('mixer.mute')}
        active={muted}
        icon={muted ? <VolumeX size={17} /> : <Volume2 size={17} />}
        onClick={() => change({ muted: !muted })}
      />
    </li>
  );
}

/** Saved mixer and application volumes, applied with one tap, a shortcut, or automatically. */
export function ScenesSection() {
  const { t } = useTranslation();
  const { state, run } = useDeck();
  const { items, activeId, autoSwitch } = state.scenes;
  const [newName, setNewName] = useState('');
  const [editing, setEditing] = useState<string | null>(null);
  const editingScene = items.find((s) => s.id === editing);

  const create = () => {
    const name = newName.trim();
    if (!name) return;
    run(() => api.createScene(name));
    setNewName('');
  };

  return (
    <section className="scenes" aria-labelledby="scenes-title">
      <div className="scenes-head">
        <div className="card-titles">
          <span className="eyebrow">{t('scenes.eyebrow')}</span>
          <h2 id="scenes-title" className="scenes-title">{t('scenes.title')}</h2>
          <p className="faint small">{t('scenes.subtitle')}</p>
        </div>
        <label className="scenes-auto small">
          <span>{t('scenes.autoSwitch')}</span>
          <Switch checked={autoSwitch} label={t('scenes.autoSwitch')} onChange={(v) => run(() => api.setSceneOptions({ autoSwitch: v }))} />
        </label>
      </div>

      {items.length === 0 && (
        <EmptyState compact icon={<Zap size={20} strokeWidth={1.5} />} title={t('scenes.empty')}>
          {t('scenes.emptyBody')}
        </EmptyState>
      )}

      <div className="profile-grid scene-grid">
        {items.map((s) => (
          <SceneCard key={s.id} scene={s} active={s.id === activeId} onEdit={() => setEditing(s.id)} />
        ))}
        <form
          className="profile-card profile-new"
          onSubmit={(e) => {
            e.preventDefault();
            create();
          }}
        >
          <span className="eyebrow">{t('scenes.new')}</span>
          <input className="input" placeholder={t('scenes.placeholder')} value={newName} maxLength={40} onChange={(e) => setNewName(e.target.value)} />
          <Button type="submit" variant="primary" size="sm" icon={<Plus size={15} />} disabled={!newName.trim()}>{t('scenes.saveCurrent')}</Button>
        </form>
      </div>

      {editingScene && <SceneDialog scene={editingScene} onClose={() => setEditing(null)} />}
    </section>
  );
}

function SceneCard({ scene, active, onEdit }: { scene: Scene; active: boolean; onEdit(): void }) {
  const { t } = useTranslation();
  const { run, toast } = useDeck();

  const apply = () =>
    run(async () => {
      const r = await api.applyScene(scene.id);
      toast(r.skipped.length ? t('scenes.appliedSkipped', { name: scene.name, count: r.skipped.length }) : t('scenes.applied', { name: scene.name }), 'success');
    });

  const parts = [
    ...(scene.channels ? [t('scenes.gamedeck')] : []),
    ...scene.apps.slice(0, 3).map((a) => a.name),
  ];
  const more = scene.apps.length - Math.min(scene.apps.length, 3);

  return (
    <div className={`profile-card scene-card ${active ? 'is-active' : ''}`}>
      <div className="profile-card-top">
        <span className="scene-icon"><SoundIcon name={scene.icon} size={18} /></span>
        {active && <Badge tone="accent">{t('common.active')}</Badge>}
      </div>
      <div className="scene-name">
        <strong>{scene.name}</strong>
        <span className="faint small">{parts.length ? parts.join(' · ') + (more > 0 ? ` +${more}` : '') : t('scenes.nothing')}</span>
      </div>
      {(scene.hotkey || scene.autoApp) && (
        <div className="scene-tags">
          {scene.hotkey && <span className={`scene-tag num ${scene.hotkeyError ? 'is-warn' : ''}`} title={scene.hotkeyError ?? undefined}><Keyboard size={12} />{scene.hotkey}</span>}
          {scene.autoApp && <span className="scene-tag"><Zap size={12} />{scene.autoApp}</span>}
        </div>
      )}
      <div className="scene-actions">
        <Button size="sm" variant={active ? 'secondary' : 'primary'} icon={<Play size={14} />} onClick={apply}>{t('scenes.apply')}</Button>
        <IconButton label={t('scenes.capture')} icon={<Save size={16} />} onClick={() => run(async () => {
          await api.captureScene(scene.id);
          toast(t('scenes.captured', { name: scene.name }), 'success');
        })} />
        <IconButton label={t('scenes.edit')} icon={<Pencil size={16} />} onClick={onEdit} />
        <IconButton label={t('scenes.duplicate')} icon={<Copy size={16} />} onClick={() => run(() => api.duplicateScene(scene.id))} />
        <IconButton label={t('scenes.delete')} icon={<Trash2 size={16} />} onClick={() => confirm(t('scenes.confirmDelete', { name: scene.name })) && run(() => api.deleteScene(scene.id))} />
      </div>
    </div>
  );
}

function SceneDialog({ scene, onClose }: { scene: Scene; onClose(): void }) {
  const { t } = useTranslation();
  const { state, run } = useDeck();
  const [name, setName] = useState(scene.name);
  const patch = (p: ScenePatch) => run(() => api.updateScene(scene.id, p));
  // The app list is edited locally (sliders stay smooth) and sent newest-first; the PC's copy is
  // taken back only when nothing is waiting to be sent.
  const appsKey = `${scene.id}:apps`;
  const [apps, setLocalApps] = useState(scene.apps);
  useEffect(() => {
    if (!sceneSender.busy(appsKey)) setLocalApps(scene.apps);
  }, [scene.apps, appsKey]);
  const setApps = (next: SceneApp[]) => {
    setLocalApps(next);
    sceneSender.send(appsKey, { apps: next }, (p) => patch(p));
  };
  const saveHotkey = async (hotkey: string) => {
    try {
      await api.updateScene(scene.id, { hotkey });
      return null;
    } catch (e) {
      return e instanceof Error ? e.message : String(e);
    }
  };

  const commitName = () => {
    const v = name.trim();
    if (v && v !== scene.name) patch({ name: v });
  };
  const close = () => {
    commitName();
    onClose();
  };

  const current: SceneChannels = {
    micVolume: state.audio.micVolume, micMuted: state.audio.micMuted,
    virtualVolume: state.audio.virtualVolume, virtualMuted: state.audio.virtualMuted,
    monitorVolume: state.audio.monitorVolume, monitorMuted: state.audio.monitorMuted,
  };
  const inScene = new Set(apps.map((a) => a.key));
  const addable = state.apps.filter((a) => !inScene.has(a.key));
  const triggers = [...new Set([...state.apps.filter((a) => !a.key.startsWith('#')).map((a) => a.key), ...(scene.autoApp ? [scene.autoApp] : [])])];

  return (
    <Dialog
      title={t('scenes.editTitle')}
      onClose={close}
      footer={
        <>
          <Button variant="danger" icon={<Trash2 size={16} />} onClick={() => {
            if (!confirm(t('scenes.confirmDelete', { name: scene.name }))) return;
            run(() => api.deleteScene(scene.id));
            onClose();
          }}>{t('common.delete')}</Button>
          <Button variant="primary" icon={<Check size={16} />} onClick={close}>{t('common.done')}</Button>
        </>
      }
    >
      <Field label={t('scenes.name')}>
        <input className="input" value={name} maxLength={40} onChange={(e) => setName(e.target.value)} onBlur={commitName} onKeyDown={(e) => e.key === 'Enter' && e.currentTarget.blur()} />
      </Field>

      <Field label={t('edit.icon')}>
        <div className="icon-grid">
          {Object.keys(SOUND_ICONS).map((icon) => (
            <button key={icon} className={`icon-choice ${icon === scene.icon ? 'is-active' : ''}`} aria-label={t(`icons.${icon}`, { defaultValue: icon })} title={t(`icons.${icon}`, { defaultValue: icon })} aria-pressed={icon === scene.icon} onClick={() => patch({ icon })}>
              <SoundIcon name={icon} size={18} />
            </button>
          ))}
        </div>
      </Field>

      <Field label={t('scenes.includeMixer')} hint={t('scenes.includeMixerHint')} inline>
        <Switch checked={scene.channels !== null} label={t('scenes.includeMixer')} onChange={(v) => patch(v ? { channels: current } : { clearChannels: true })} />
      </Field>
      {scene.channels && (
        <p className="small scene-channels num">
          {t('mixer.microphone')} {pct(scene.channels.micVolume)}{scene.channels.micMuted ? ` (${t('common.muted')})` : ''} ·{' '}
          {t('mixer.soundsIntoCall')} {pct(scene.channels.virtualVolume)}{scene.channels.virtualMuted ? ` (${t('common.muted')})` : ''} ·{' '}
          {t('mixer.soundsInHeadphones')} {pct(scene.channels.monitorVolume)}{scene.channels.monitorMuted ? ` (${t('common.muted')})` : ''}
        </p>
      )}

      <Field label={t('scenes.apps')} hint={t('scenes.appsHint')}>
        {apps.length === 0 ? <p className="faint small">{t('scenes.noApps')}</p> : (
          <ul className="app-list app-list-edit">
            {apps.map((a, i) => (
              <li key={a.key} className={`app-row ${a.muted ? 'is-muted' : ''}`}>
                <div className="app-name">
                  <strong>{a.name}</strong>
                  <span className="faint small">{a.key.startsWith('#') ? t('apps.windows') : a.key}{state.apps.some((x) => x.key === a.key) ? '' : ` · ${t('scenes.notRunning')}`}</span>
                </div>
                <Slider label={t('apps.volumeOf', { name: a.name })} value={a.volume} max={1} onChange={(v) => setApps(apps.map((x, j) => (j === i ? { ...x, volume: v } : x)))} />
                <IconButton label={a.muted ? t('mixer.unmute') : t('mixer.mute')} active={a.muted} icon={a.muted ? <VolumeX size={17} /> : <Volume2 size={17} />} onClick={() => setApps(apps.map((x, j) => (j === i ? { ...x, muted: !x.muted } : x)))} />
                <IconButton label={t('scenes.removeApp', { name: a.name })} icon={<X size={16} />} onClick={() => setApps(apps.filter((_, j) => j !== i))} />
              </li>
            ))}
          </ul>
        )}
        {addable.length > 0 && (
          <Select aria-label={t('scenes.addApp')} value="" onChange={(e) => {
            const a = state.apps.find((x) => x.key === e.target.value);
            if (a) setApps([...apps, { key: a.key, name: a.name, volume: a.volume, muted: a.muted }]);
          }}>
            <option value="">{t('scenes.addApp')}</option>
            {addable.map((a) => <option key={a.key} value={a.key}>{a.name}</option>)}
          </Select>
        )}
      </Field>

      <Field label={t('scenes.hotkey')} hint={scene.hotkeyError ?? t('scenes.hotkeyHint')}>
        <div className="hotkey-row">
          <HotkeyField value={scene.hotkey} onSave={saveHotkey} />
          {scene.hotkey && <IconButton label={t('scenes.clearHotkey')} icon={<X size={16} />} onClick={() => patch({ hotkey: '' })} />}
        </div>
        {scene.hotkeyError && <p className="small text-warn">{scene.hotkeyError}</p>}
      </Field>

      <Field label={t('scenes.autoApp')} hint={state.scenes.autoSwitch ? t('scenes.autoAppHint') : t('scenes.autoAppOff')}>
        <Select aria-label={t('scenes.autoApp')} value={scene.autoApp ?? ''} onChange={(e) => patch({ autoApp: e.target.value })}>
          <option value="">{t('scenes.autoNone')}</option>
          {triggers.map((k) => <option key={k} value={k}>{state.apps.find((a) => a.key === k)?.name ?? k} · {k}</option>)}
        </Select>
      </Field>
    </Dialog>
  );
}

/** "Ctrl+Alt+1" from a key press; null while only modifiers are down or the key isn't supported. */
export function hotkeyFromEvent(e: Pick<KeyboardEvent, 'code' | 'ctrlKey' | 'altKey' | 'shiftKey' | 'metaKey'>): string | null {
  const c = e.code;
  let key: string | null = null;
  if (/^Key[A-Z]$/.test(c)) key = c.slice(3);
  else if (/^Digit[0-9]$/.test(c)) key = c.slice(5);
  else if (/^F([1-9]|1[0-9]|2[0-4])$/.test(c)) key = c;
  else if (/^Numpad[0-9]$/.test(c)) key = `Num${c.slice(6)}`;
  if (!key) return null;
  const mods = [e.ctrlKey && 'Ctrl', e.altKey && 'Alt', e.shiftKey && 'Shift', e.metaKey && 'Win'].filter(Boolean);
  return [...mods, key].join('+');
}

const modifiersOf = (e: Pick<KeyboardEvent, 'ctrlKey' | 'altKey' | 'shiftKey' | 'metaKey'>) =>
  [e.ctrlKey && 'Ctrl', e.altKey && 'Alt', e.shiftKey && 'Shift', e.metaKey && 'Win'].filter(Boolean).join('+');

/**
 * Focus it and press the combination; it is saved at once. While it listens, the PC releases its
 * scene shortcuts (otherwise Windows would hand an existing one to GameDeck, not to this page) and
 * takes them back on blur. A refused combination stays visible with the reason.
 */
function HotkeyField({ value, onSave }: { value: string | null; onSave(v: string): Promise<string | null> }) {
  const { t } = useTranslation();
  const [listening, setListening] = useState(false);
  const [held, setHeld] = useState('');
  const [attempt, setAttempt] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (!listening) return;
    const suspend = () => api.suspendHotkeys(20).catch(() => undefined);
    suspend();
    const keepAlive = setInterval(suspend, 15000);
    return () => {
      clearInterval(keepAlive);
      api.suspendHotkeys(0).catch(() => undefined);
    };
  }, [listening]);

  // A saved combination stays on screen until the PC's state shows it (no flash of the old value).
  useEffect(() => setAttempt(null), [value]);

  let shown: string;
  if (!listening) shown = (attempt && !error ? attempt : value) ?? t('scenes.noHotkey');
  else if (held) shown = `${held}+…`;
  else shown = attempt ?? t('scenes.pressKeys');

  return (
    <div className="hotkey-field">
      <button
        type="button"
        className={`input hotkey-input num ${listening ? 'is-listening' : ''} ${error ? 'is-error' : ''}`}
        aria-invalid={!!error}
        onFocus={() => {
          setListening(true);
          setError(null);
          setAttempt(null);
        }}
        onBlur={() => {
          setListening(false);
          setHeld('');
        }}
        onKeyUp={(e) => setHeld(modifiersOf(e))}
        onKeyDown={async (e) => {
          if (e.key === 'Tab') return;
          e.preventDefault();
          if (e.key === 'Escape') {
            e.stopPropagation(); // leave the dialog open
            e.currentTarget.blur();
            return;
          }
          const combo = hotkeyFromEvent(e);
          if (!combo) {
            setHeld(modifiersOf(e));
            return;
          }
          if (e.repeat || saving) return;
          const target = e.currentTarget;
          setHeld('');
          setAttempt(combo);
          setSaving(true);
          const problem = await onSave(combo);
          setSaving(false);
          setError(problem);
          if (!problem) target.blur();
        }}
      >
        {shown}
      </button>
      {error && <p className="small text-danger">{attempt}: {error}</p>}
    </div>
  );
}
