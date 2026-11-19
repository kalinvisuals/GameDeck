import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AppWindow, Check, Copy, ImagePlus, Trash2, X } from 'lucide-react';
import { api } from '../../lib/api';
import { SOUND_ICONS, SoundIcon, TAG_COLORS } from '../../lib/icons';
import type { DeckAction, DeckAnimation, DeckButton } from '../../lib/types';
import { useDeck } from '../../state/DeckContext';
import { useHardware } from '../../state/hardware';
import { Button, Field, IconButton, Segmented, Switch } from '../../ui/controls';
import { Dialog } from '../../ui/Dialog';
import { ActionFields } from './ActionFields';
import { SENSITIVE, STATEFUL, closingActionFor, defaultAction, programName, sensorLevel, sensorText } from './actions';
import { DeckImage, prepareUpload, useDeckMotion } from './DeckImage';

/** Mirrors the PC's limits so a too-large file is caught before it is sent. */
const MAX_STILL_BYTES = 1024 * 1024;
const MAX_ANIMATION_BYTES = 4 * 1024 * 1024;
const ACCEPT = 'image/png,image/jpeg,image/gif,image/webp,image/svg+xml,.svg';

type Look = 'normal' | 'active';

/**
 * Edits a copy of one button; "Done" saves it and keeps the dialog open if the PC refuses
 * (for example a path that doesn't exist or a script file), with the reason shown.
 */
export function ButtonEditor(props: {
  button: DeckButton;
  isNew: boolean;
  onClose(): void;
  onSave(b: DeckButton): Promise<string | null>;
  onDelete(): void;
  /** Returns an error message when there is no free slot. */
  onDuplicate(): string | null;
}) {
  const { t } = useTranslation();
  const { run, session, state } = useDeck();
  const motion = useDeckMotion();
  const [b, setB] = useState(props.button);
  const [look, setLook] = useState<Look>('normal');
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const file = useRef<HTMLInputElement>(null);
  const set = (patch: Partial<DeckButton>) => {
    setB((x) => ({ ...x, ...patch }));
    setError(null);
  };

  // The program this button opens ("obs64.exe", also through a shortcut), asked from the PC whenever the
  // path changes. Minimize/show/close actions on this button follow it unless the user picked another.
  const launchTarget = b.action.type === 'launch' ? b.action.target ?? '' : '';
  const [mainProgram, setMainProgram] = useState<string | null>(null);
  const previousProgram = useRef<string | null>(null);
  useEffect(() => {
    if (!launchTarget.trim() || !session.local) {
      setMainProgram(null);
      return;
    }
    let live = true;
    const timer = window.setTimeout(() => {
      api.deckProgramOf(launchTarget).then((r) => live && setMainProgram(r.program), () => live && setMainProgram(null));
    }, 350);
    return () => {
      live = false;
      window.clearTimeout(timer);
    };
  }, [launchTarget, session.local]);
  useEffect(() => {
    const before = previousProgram.current;
    previousProgram.current = mainProgram;
    if (!mainProgram || mainProgram === before) return;
    const follow = (a: DeckAction | null | undefined) =>
      a?.type === 'window' && (!a.target || a.target === before) ? { ...a, target: mainProgram } : a;
    setB((x) => ({ ...x, holdAction: follow(x.holdAction), toggleAction: follow(x.toggleAction) }));
  }, [mainProgram]);

  // A two-state button has a state of its own; otherwise only actions with a live state do.
  const stateful = !!b.toggleAction || STATEFUL.has(b.action.type);
  const sensor = b.action.type === 'sensor';
  const editingActive = stateful && look === 'active';
  const hardware = useHardware(sensor);

  const done = async () => {
    if (JSON.stringify(b) === JSON.stringify(props.button) && !props.isNew) return props.onClose();
    setSaving(true);
    // An action without a live state can't use an active look; don't keep one that would never show.
    const clean = stateful ? b : { ...b, activeImage: null, activeIcon: null, activeColor: null, animation: b.animation === 'active' ? 'loop' : b.animation };
    const problem = await props.onSave({ ...clean, label: b.label.trim() });
    setSaving(false);
    if (problem) setError(problem);
    else props.onClose();
  };

  /** The program's own icon becomes the button image; its name fills an empty label. */
  const takeProgramIcon = (path: string, fillLabel: boolean) =>
    run(async () => {
      const { name } = await api.deckProgramIcon(path);
      setB((x) => ({ ...x, image: name, label: fillLabel && !x.label.trim() ? programName(path) : x.label }));
      setError(null);
    });

  const pickImage = (f: File | undefined) => {
    if (!f) return;
    const animatedKind = /^image\/(gif|webp|png)$/.test(f.type) || /\.(gif|webp|png)$/i.test(f.name);
    if (f.size > (animatedKind ? MAX_ANIMATION_BYTES : MAX_STILL_BYTES)) return setError(t('deck.imageTooBig'));
    run(async () => {
      let upload: File;
      try {
        upload = await prepareUpload(f);
      } catch {
        throw new Error(t('deck.svgFailed'));
      }
      const { name } = await api.uploadDeckImage(upload);
      set(editingActive ? { activeImage: name } : { image: name });
    });
  };

  // What the preview shows: the look being edited, falling back to the normal one where unset.
  const image = editingActive ? b.activeImage ?? b.image : b.image;
  const icon = editingActive ? b.activeIcon ?? b.icon : b.icon;
  const color = editingActive ? b.activeColor ?? b.color : b.color;
  const mode = b.animation ?? 'loop';
  const animate = motion && (mode === 'loop' || (mode === 'active' && editingActive));
  const currentImage = editingActive ? b.activeImage : b.image;
  const currentIcon = editingActive ? b.activeIcon : b.icon;
  const currentColor = editingActive ? b.activeColor : b.color;
  const reading = sensor && b.action.sensor ? sensorText(hardware, b.action.sensor) : null;
  const level = reading ? sensorLevel(reading.value, b.warnAt, b.criticalAt) : null;

  return (
    <Dialog
      title={props.isNew ? t('deck.newButton') : t('deck.editTitle')}
      onClose={props.onClose}
      footer={
        <>
          {!props.isNew && (
            <>
              <Button variant="danger" icon={<Trash2 size={16} />} onClick={() => confirm(t('deck.confirmDeleteButton', { name: b.label || t('deck.untitled') })) && props.onDelete()}>
                {t('common.delete')}
              </Button>
              <IconButton label={t('deck.duplicate')} icon={<Copy size={16} />} onClick={() => {
                const problem = props.onDuplicate();
                if (problem) setError(problem);
              }} />
            </>
          )}
          <Button variant="primary" icon={<Check size={16} />} disabled={saving} onClick={done}>{t('common.done')}</Button>
        </>
      }
    >
      <div className="deck-editor-preview">
        <span className={`deck-key deck-key-sample ${editingActive ? 'is-active' : ''} ${level ? `is-${level}` : ''} ${image ? 'has-image' : ''}`} style={{ ['--tag' as string]: color }} aria-label={t('deck.preview')}>
          {image && <DeckImage key={`${image}-${animate}`} name={image} animate={animate} className={sensor ? 'deck-key-bg' : 'deck-key-image'} />}
          {reading ? (
            <span className={`deck-key-value num ${reading.value === null ? 'is-unavailable' : ''}`}>
              {reading.value === null ? t('hw.unavailableShort') : `${reading.value}${b.action.unit ?? reading.unit}`}
            </span>
          ) : (
            !image && <span className="deck-key-icon"><SoundIcon name={icon} size={24} /></span>
          )}
          {b.label && <span className="deck-key-label">{b.label}</span>}
        </span>
        <Field label={t('deck.label')}>
          <input className="input" value={b.label} maxLength={40} placeholder={t('deck.labelPlaceholder')} onChange={(e) => set({ label: e.target.value })} />
        </Field>
      </div>

      <ActionFields action={b.action} onChange={(action) => set({ action })} onPathPicked={(path) => takeProgramIcon(path, true)} />

      {SENSITIVE.has(b.action.type) && session.local && <p className="small faint deck-note">{t(b.action.type === 'keys' ? 'deck.keysWarning' : 'deck.launchWarning')}</p>}

      {!sensor && (
        <section className="deck-more" aria-labelledby="deck-more-title">
          <h3 id="deck-more-title" className="deck-section-title">{t('deck.moreActions')}</h3>

          <Field label={t('deck.holdTitle')} hint={t('deck.holdHint')} inline>
            <Switch checked={!!b.holdAction} label={t('deck.holdTitle')} onChange={(v) => set({ holdAction: v ? defaultAction('media', state) : null })} />
          </Field>
          {b.holdAction && (
            <div className="deck-more-action">
              <ActionFields secondary suggestProgram={mainProgram} action={b.holdAction} onChange={(holdAction) => set({ holdAction })} />
            </div>
          )}

          <Field label={t('deck.toggleTitle')} hint={t('deck.toggleHint')} inline>
            <Switch checked={!!b.toggleAction} label={t('deck.toggleTitle')} onChange={(v) => set({ toggleAction: v ? (b.action.type === 'launch' && session.local ? { ...closingActionFor(b.action), target: mainProgram ?? closingActionFor(b.action).target } : defaultAction('media', state)) : null })} />
          </Field>
          {b.toggleAction && (
            <div className="deck-more-action">
              <ActionFields secondary suggestProgram={mainProgram} action={b.toggleAction} onChange={(toggleAction) => set({ toggleAction })} />
            </div>
          )}
        </section>
      )}

      <section className="deck-appearance" aria-labelledby="deck-appearance-title">
        <h3 id="deck-appearance-title" className="deck-section-title">{t('deck.appearance')}</h3>

        {stateful && (
          <Field label={t('deck.lookFor')} hint={t(look === 'active' ? `deck.activeMeans.${b.toggleAction ? 'toggle' : b.action.type}` : 'deck.normalMeans')}>
            <Segmented size="sm" label={t('deck.lookFor')} value={look} onChange={(v) => setLook(v as Look)}
              options={[{ value: 'normal', label: t('deck.looks.normal') }, { value: 'active', label: t('deck.looks.active') }]} />
          </Field>
        )}

        <Field label={t('deck.image')} hint={editingActive ? t('deck.activeImageHint') : t('deck.imageHint')}>
          <div className="hotkey-row">
            <Button size="sm" icon={<ImagePlus size={15} />} onClick={() => file.current?.click()}>{currentImage ? t('deck.replaceImage') : t('deck.addImage')}</Button>
            {!editingActive && b.action.type === 'launch' && b.action.target && session.local && (
              <Button size="sm" icon={<AppWindow size={15} />} onClick={() => takeProgramIcon(b.action.target!, true)}>{t('deck.programIcon')}</Button>
            )}
            {currentImage && <IconButton label={t('deck.removeImage')} icon={<X size={16} />} onClick={() => set(editingActive ? { activeImage: null } : { image: null })} />}
            <input ref={file} type="file" hidden accept={ACCEPT} onChange={(e) => { pickImage(e.target.files?.[0]); e.target.value = ''; }} />
          </div>
        </Field>

        {(image || b.activeImage) && (
          <Field label={t('deck.animation')} hint={motion ? t('deck.animationHint') : t('deck.animationsOff')}>
            <Segmented size="sm" label={t('deck.animation')} value={mode} onChange={(animation: DeckAnimation) => set({ animation })}
              options={(['loop', ...(stateful ? ['active'] : []), 'off'] as DeckAnimation[]).map((m) => ({ value: m, label: t(`deck.animationModes.${m}`) }))} />
          </Field>
        )}

        <Field label={t('deck.color')}>
          <div className="swatches">
            {editingActive && (
              <button className={`swatch swatch-same ${!b.activeColor ? 'is-active' : ''}`} style={{ ['--sw' as string]: b.color }} aria-label={t('deck.sameAsNormal')} title={t('deck.sameAsNormal')}
                aria-pressed={!b.activeColor} onClick={() => set({ activeColor: null })} />
            )}
            {TAG_COLORS.map((c) => (
              <button key={c} className={`swatch ${c === currentColor ? 'is-active' : ''}`} style={{ ['--sw' as string]: c }} aria-label={t('edit.tagColor', { color: c })}
                aria-pressed={c === currentColor} onClick={() => set(editingActive ? { activeColor: c } : { color: c })} />
            ))}
          </div>
        </Field>

        {!currentImage && (
          <Field label={t('edit.icon')} hint={editingActive ? t('deck.activeIconHint') : undefined}>
            <div className="icon-grid">
              {Object.keys(SOUND_ICONS).map((name) => (
                <button key={name} className={`icon-choice ${name === currentIcon ? 'is-active' : ''}`} aria-label={t(`icons.${name}`, { defaultValue: name })}
                  title={t(`icons.${name}`, { defaultValue: name })} aria-pressed={name === currentIcon}
                  onClick={() => set(editingActive ? { activeIcon: name === b.activeIcon ? null : name } : { icon: name })}>
                  <SoundIcon name={name} size={18} />
                </button>
              ))}
            </div>
          </Field>
        )}

        {sensor && (
          <>
            <div className="field-pair">
              <Field label={t('deck.warnAt')} hint={t('deck.thresholdHint')}>
                <input className="input num" type="number" step="any" value={b.warnAt ?? ''} placeholder={t('deck.off')}
                  onChange={(e) => set({ warnAt: e.target.value === '' ? null : Number(e.target.value) })} />
              </Field>
              <Field label={t('deck.criticalAt')}>
                <input className="input num" type="number" step="any" value={b.criticalAt ?? ''} placeholder={t('deck.off')}
                  onChange={(e) => set({ criticalAt: e.target.value === '' ? null : Number(e.target.value) })} />
              </Field>
            </div>
            <Field label={t('deck.openMonitor')} hint={t('deck.openMonitorHint')} inline>
              <Switch checked={b.action.mode === 'monitor'} label={t('deck.openMonitor')} onChange={(v) => set({ action: { ...b.action, mode: v ? 'monitor' : null } })} />
            </Field>
          </>
        )}
      </section>

      {error && <p className="small text-danger" role="alert">{error}</p>}
    </Dialog>
  );
}
