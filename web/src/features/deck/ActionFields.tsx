import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { FileSearch, FolderOpen, Keyboard, RefreshCw, ShieldAlert } from 'lucide-react';
import { api } from '../../lib/api';
import type { DeckAction, DeckActionType, DeckChannel, OpenWindow, SensorId } from '../../lib/types';
import { useDeck } from '../../state/DeckContext';
import { usePresence } from '../../state/presence';
import { Button, Field, IconButton, Segmented, Select } from '../../ui/controls';
import { Slider } from '../../ui/Slider';
import { ACTION_GROUPS, CHANNELS, DISCORD_MODES, MEDIA_KEYS, SECONDARY_GROUPS, SENSITIVE, SENSORS, STEP_TYPES, WINDOW_MODES, channelMax, defaultAction, keysFromEvent } from './actions';

/** Key names are the same in every language, as Windows writes them. */
const KEYS_EXAMPLE = 'Ctrl+Shift+S';

/**
 * Edits one action. Launch, link and key actions can only be set on the GameDeck PC itself: elsewhere
 * they are shown read-only with the reason (the PC refuses such changes anyway).
 */
/** @param onPathPicked Called after a program, file or folder was chosen in the Windows dialog (the button editor takes its icon). */
/** @param secondary A long-press or second-state action: no sensor display, no "nothing". */
/** @param suggestProgram The program this button opens, offered first for minimize/show/close. */
export function ActionFields({ action, onChange, inMacro = false, secondary = false, onPathPicked, suggestProgram }: { action: DeckAction; onChange(a: DeckAction): void; inMacro?: boolean; secondary?: boolean; onPathPicked?(path: string): void; suggestProgram?: string | null }) {
  const { t } = useTranslation();
  const { state, session, run } = useDeck();
  const presence = usePresence();
  const local = !!session.local;
  const locked = SENSITIVE.has(action.type) && !local;
  const set = (patch: Partial<DeckAction>) => onChange({ ...action, ...patch });
  // The picker opens on the PC's screen (a browser can't see full paths), so only on the PC itself.
  const browse = (folder: boolean) =>
    run(async () => {
      const { path } = await api.browseDeckPath(folder, action.target ?? '');
      if (!path) return;
      set({ target: path });
      onPathPicked?.(path);
    });

  const typeOptions = inMacro
    ? [{ group: 'steps', types: STEP_TYPES }]
    : secondary ? SECONDARY_GROUPS : ACTION_GROUPS;

  return (
    <div className="action-fields">
      <Field label={inMacro ? t('deck.stepType') : t('deck.action')}>
        <Select value={action.type} disabled={locked} aria-label={t('deck.action')} onChange={(e) => {
          const next = defaultAction(e.target.value as DeckActionType, state);
          onChange(next.type === 'window' && suggestProgram ? { ...next, target: suggestProgram } : next);
        }}>
          {typeOptions.map((g) => (
            <optgroup key={g.group} label={t(`deck.groups.${g.group}`)}>
              {g.types.map((type) => (
                <option key={type} value={type} disabled={SENSITIVE.has(type) && !local && type !== action.type}>
                  {t(`deck.types.${type}`)}
                </option>
              ))}
            </optgroup>
          ))}
        </Select>
      </Field>

      {locked && <p className="small text-warn deck-locked"><ShieldAlert size={14} /> {t('deck.localOnly')}</p>}

      {(action.type === 'launch' || action.type === 'url') && (
        <Field label={t(action.type === 'launch' ? 'deck.path' : 'deck.url')} hint={t(action.type === 'launch' ? 'deck.pathHint' : 'deck.urlHint')}>
          <input
            className="input num"
            value={action.target ?? ''}
            disabled={locked}
            spellCheck={false}
            placeholder={action.type === 'launch' ? 'C:\\Games\\Game.exe' : 'https://'}
            onChange={(e) => set({ target: e.target.value })}
          />
          {action.type === 'launch' && local && (
            <div className="deck-browse">
              <Button size="sm" icon={<FileSearch size={15} />} onClick={() => browse(false)}>{t('deck.browseFile')}</Button>
              <Button size="sm" icon={<FolderOpen size={15} />} onClick={() => browse(true)}>{t('deck.browseFolder')}</Button>
            </div>
          )}
        </Field>
      )}

      {action.type === 'keys' && <KeysField value={action.keys ?? ''} disabled={locked} onChange={(keys) => set({ keys })} />}

      {action.type === 'window' && (
        <>
          <WindowProgramField value={action.target ?? ''} suggest={suggestProgram ?? null} disabled={locked} onChange={(target) => set({ target })} />
          <Field label={t('deck.windowMode')} hint={action.mode === 'close' ? t('deck.windowCloseNote') : undefined}>
            <Segmented size="sm" label={t('deck.windowMode')} value={action.mode ?? 'close'} onChange={(mode) => !locked && set({ mode })}
              options={WINDOW_MODES.map((m) => ({ value: m, label: t(`deck.windowModes.${m}`) }))} />
          </Field>
        </>
      )}

      {action.type === 'media' && (
        <Field label={t('deck.mediaKey')}>
          <Select value={action.mode ?? 'playPause'} aria-label={t('deck.mediaKey')} onChange={(e) => set({ mode: e.target.value })}>
            {MEDIA_KEYS.map((m) => <option key={m} value={m}>{t(`deck.media.${m}`)}</option>)}
          </Select>
        </Field>
      )}

      {action.type === 'discord' && (
        <>
          <Field label={t('deck.discordMode')}>
            <Select value={action.mode ?? 'toggle'} aria-label={t('deck.discordMode')}
              onChange={(e) => set({ mode: e.target.value, target: e.target.value === 'show' ? action.target ?? presence?.presets[0]?.id ?? null : null })}>
              {DISCORD_MODES.map((m) => <option key={m} value={m}>{t(`deck.discordModes.${m}`)}</option>)}
            </Select>
          </Field>
          {action.mode === 'show' && (
            <Field label={t('deck.discordPreset')} hint={!presence?.presets.length ? t('deck.noPresets') : undefined}>
              <Select value={action.target ?? ''} aria-label={t('deck.discordPreset')} onChange={(e) => set({ target: e.target.value })}>
                {!presence?.presets.some((p) => p.id === action.target) && <option value={action.target ?? ''}>{t('deck.choose')}</option>}
                {presence?.presets.map((p) => <option key={p.id} value={p.id}>{p.label || p.details || p.name || p.id}</option>)}
              </Select>
            </Field>
          )}
        </>
      )}

      {action.type === 'sound' && (
        <Field label={t('deck.sound')}>
          <Select value={action.target ?? ''} aria-label={t('deck.sound')} onChange={(e) => set({ target: e.target.value })}>
            {!state.sounds.some((s) => s.id === action.target) && <option value={action.target ?? ''}>{t('deck.choose')}</option>}
            {[...state.sounds].sort((a, b) => a.label.localeCompare(b.label)).map((s) => <option key={s.id} value={s.id}>{s.label}</option>)}
          </Select>
        </Field>
      )}

      {action.type === 'scene' && (
        <Field label={t('deck.scene')} hint={state.scenes.items.length === 0 ? t('deck.noScenes') : undefined}>
          <Select value={action.target ?? ''} aria-label={t('deck.scene')} onChange={(e) => set({ target: e.target.value })}>
            {!state.scenes.items.some((s) => s.id === action.target) && <option value={action.target ?? ''}>{t('deck.choose')}</option>}
            {state.scenes.items.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
          </Select>
        </Field>
      )}

      {action.type === 'macro' && (
        <Field label={t('deck.macro')} hint={state.deck.macros.length === 0 ? t('deck.noMacros') : t('deck.macroHint')}>
          <Select value={action.target ?? ''} aria-label={t('deck.macro')} onChange={(e) => set({ target: e.target.value })}>
            {!state.deck.macros.some((m) => m.id === action.target) && <option value={action.target ?? ''}>{t('deck.choose')}</option>}
            {state.deck.macros.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}
          </Select>
        </Field>
      )}

      {action.type === 'appMute' && (
        <Field label={t('deck.app')} hint={t('deck.appHint')}>
          <Select value={action.target ?? ''} aria-label={t('deck.app')} onChange={(e) => set({ target: e.target.value })}>
            {!state.apps.some((a) => a.key === action.target) && <option value={action.target ?? ''}>{action.target || t('deck.choose')}</option>}
            {state.apps.map((a) => <option key={a.key} value={a.key}>{a.name}</option>)}
          </Select>
        </Field>
      )}

      {(action.type === 'mute' || action.type === 'volume') && (
        <Field label={t('deck.channel')}>
          <Select value={action.channel ?? 'mic'} aria-label={t('deck.channel')} onChange={(e) => {
            const channel = e.target.value as DeckChannel;
            set({ channel, value: action.mode === 'set' ? Math.min(action.value ?? 1, channelMax(channel)) : action.value });
          }}>
            {CHANNELS.map((c) => <option key={c} value={c}>{t(`deck.channels.${c}`)}</option>)}
          </Select>
        </Field>
      )}

      {(action.type === 'mute' || action.type === 'appMute') && (
        <Field label={t('deck.when')}>
          <Segmented size="sm" label={t('deck.when')} value={action.mode ?? 'toggle'} onChange={(mode) => set({ mode })}
            options={['toggle', 'on', 'off'].map((m) => ({ value: m, label: t(`deck.modes.${m}`) }))} />
        </Field>
      )}

      {action.type === 'volume' && (
        <>
          <Field label={t('deck.volumeMode')}>
            <Segmented size="sm" label={t('deck.volumeMode')} value={action.mode ?? 'up'}
              onChange={(mode) => set({ mode, value: mode === 'set' ? 1 : 0.1 })}
              options={['up', 'down', 'set'].map((m) => ({ value: m, label: t(`deck.modes.${m}`) }))} />
          </Field>
          <Field label={action.mode === 'set' ? t('deck.level') : t('deck.step')}>
            <Slider label={t('deck.level')} value={action.value ?? 0.1} min={action.mode === 'set' ? 0 : 0.01}
              max={action.mode === 'set' ? channelMax(action.channel) : 0.5} onChange={(value) => set({ value })} />
          </Field>
        </>
      )}

      {action.type === 'sensor' && (
        <div className="field-pair">
          <Field label={t('deck.sensor')}>
            <Select value={action.sensor ?? 'cpu.usage'} aria-label={t('deck.sensor')} onChange={(e) => set({ sensor: e.target.value as SensorId })}>
              {SENSORS.map((s) => <option key={s} value={s}>{t(`deck.sensors.${s}`)}</option>)}
            </Select>
          </Field>
          <Field label={t('deck.unit')} hint={t('deck.unitHint')}>
            <input className="input" value={action.unit ?? ''} maxLength={8} onChange={(e) => set({ unit: e.target.value })} />
          </Field>
        </div>
      )}

      {action.type === 'wait' && (
        <Field label={t('deck.waitSeconds')}>
          <input className="input num" type="number" min={0.05} max={60} step={0.1} value={(action.delayMs ?? 1000) / 1000}
            onChange={(e) => set({ delayMs: Math.round(Math.min(60, Math.max(0.05, Number(e.target.value) || 0.05)) * 1000) })} />
        </Field>
      )}
    </div>
  );
}

/** Type it ("Ctrl+Shift+S") or press the record button and then the keys. */
function KeysField({ value, onChange, disabled }: { value: string; onChange(v: string): void; disabled: boolean }) {
  const { t } = useTranslation();
  const [recording, setRecording] = useState(false);
  return (
    <Field label={t('deck.keys')} hint={t('deck.keysHint')}>
      <div className="hotkey-row">
        <input
          className={`input num ${recording ? 'is-listening' : ''}`}
          value={recording ? t('scenes.pressKeys') : value}
          disabled={disabled}
          readOnly={recording}
          spellCheck={false}
          placeholder={KEYS_EXAMPLE}
          onChange={(e) => onChange(e.target.value)}
          onBlur={() => setRecording(false)}
          onKeyDown={(e) => {
            if (!recording) return;
            e.preventDefault();
            if (e.key === 'Escape' && !e.ctrlKey && !e.altKey) {
              e.stopPropagation(); // keep the dialog open
              setRecording(false);
              return;
            }
            const combo = keysFromEvent(e.nativeEvent);
            if (combo) {
              onChange(combo);
              setRecording(false);
            }
          }}
          ref={(el) => {
            if (recording) el?.focus();
          }}
        />
        <IconButton label={t('deck.recordKeys')} icon={<Keyboard size={16} />} active={recording} disabled={disabled} onClick={() => setRecording((r) => !r)} />
      </div>
    </Field>
  );
}

/**
 * The program a window action acts on: picked from the programs that have a window open on the PC, or
 * typed (for one that isn't running now). The list is read only on the PC itself.
 */
function WindowProgramField({ value, suggest, onChange, disabled }: { value: string; suggest: string | null; onChange(v: string): void; disabled: boolean }) {
  const { t } = useTranslation();
  const { session, run } = useDeck();
  const [windows, setWindows] = useState<OpenWindow[]>([]);
  const load = () => run(async () => setWindows(await api.deckWindows()));
  // The button's own program comes first, even when it isn't running right now.
  const choices = suggest && !windows.some((w) => w.program === suggest) ? [{ program: suggest, title: suggest }, ...windows] : windows;
  useEffect(() => {
    if (session.local && !disabled) load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session.local, disabled]);

  return (
    <Field label={t('deck.program')} hint={suggest ? t('deck.programFollows', { program: suggest }) : t('deck.programHint')}>
      <div className="hotkey-row">
        <input className="input num" list="gd-open-windows" value={value} disabled={disabled} spellCheck={false}
          placeholder={PROGRAM_EXAMPLE} onChange={(e) => onChange(e.target.value)} />
        {session.local && <IconButton label={t('deck.refreshWindows')} icon={<RefreshCw size={16} />} disabled={disabled} onClick={load} />}
      </div>
      <datalist id="gd-open-windows">
        {windows.map((w) => <option key={w.program} value={w.program}>{w.title}</option>)}
      </datalist>
      {session.local && choices.length > 0 && !disabled && (
        <div className="chips deck-window-chips">
          {choices.map((w) => (
            <button key={w.program} type="button" className={`chip ${w.program === value ? 'is-active' : ''}`} title={w.title} onClick={() => onChange(w.program)}>
              {w.program}
            </button>
          ))}
        </div>
      )}
    </Field>
  );
}

/** Program names are the same in every language. */
const PROGRAM_EXAMPLE = 'obs64.exe';
