import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ArrowLeft, ArrowRight, MinusCircle, Trash2 } from 'lucide-react';
import { api } from '../lib/api';
import { SOUND_ICONS, SoundIcon, TAG_COLORS } from '../lib/icons';
import type { PlayMode, Profile, Sound } from '../lib/types';
import { useDeck } from '../state/DeckContext';
import { Button, Field, Segmented, Switch } from '../ui/controls';
import { Dialog } from '../ui/Dialog';
import { Slider } from '../ui/Slider';
import { formatDuration } from './SoundPad';
import { TrimEditor, playedMs } from './TrimEditor';

const MODES: PlayMode[] = ['Restart', 'Toggle', 'Overlap'];

export function EditDialog({ sound, profile, categories, onClose }: { sound: Sound; profile: Profile; categories: string[]; onClose(): void }) {
  const { t } = useTranslation();
  const { run, trigger, updateSound, setLayout } = useDeck();
  const [label, setLabel] = useState(sound.label);
  const [category, setCategory] = useState(sound.category);
  const patch = (p: Parameters<typeof api.updateSound>[1]) => updateSound(sound.id, p);

  const commitLabel = () => {
    const v = label.trim();
    if (v && v !== sound.label) patch({ label: v });
  };
  const commitCategory = () => {
    const v = category.trim();
    if (v !== sound.category) patch({ category: v });
  };
  const close = () => {
    commitLabel();
    commitCategory();
    onClose();
  };

  const index = profile.layout.indexOf(sound.id);
  const move = (delta: number) => {
    const layout = [...profile.layout];
    const j = index + delta;
    if (index < 0 || j < 0 || j >= layout.length) return;
    [layout[index], layout[j]] = [layout[j], layout[index]];
    setLayout(profile.id, layout);
  };

  return (
    <Dialog
      title={t('edit.title')}
      onClose={close}
      footer={
        <>
          <Button
            variant="ghost"
            icon={<MinusCircle size={16} />}
            onClick={() => {
              setLayout(profile.id, profile.layout.filter((id) => id !== sound.id));
              onClose();
            }}
          >
            {t('edit.removeFromProfile')}
          </Button>
          <Button
            variant="danger"
            icon={<Trash2 size={16} />}
            onClick={() => {
              if (!confirm(t('edit.confirmDelete', { name: sound.label }))) return;
              run(() => api.deleteSound(sound.id));
              onClose();
            }}
          >
            {t('common.delete')}
          </Button>
        </>
      }
    >
      <div className="edit-preview" style={{ ['--tag' as string]: sound.color }}>
        <span className="edit-preview-icon">
          <SoundIcon name={sound.icon} size={26} />
        </span>
        <div className="edit-preview-text">
          <strong>{label || t('edit.untitled')}</strong>
          <span className="faint small">
            {sound.originalFileName} · {formatDuration(playedMs(sound))}
          </span>
        </div>
        <Button size="sm" onClick={() => trigger(sound.id)}>{t('edit.preview')}</Button>
      </div>

      <Field label={t('trim.title')} hint={t('trim.hint')}>
        <TrimEditor sound={sound} onCommit={(trimStartMs, trimEndMs) => patch({ trimStartMs, trimEndMs })} />
      </Field>

      <Field label={t('edit.label')}>
        <input
          className="input"
          value={label}
          maxLength={40}
          onChange={(e) => setLabel(e.target.value)}
          onBlur={commitLabel}
          onKeyDown={(e) => e.key === 'Enter' && e.currentTarget.blur()}
        />
      </Field>

      <div className="field-pair">
        <Field label={t('edit.category')} hint={t('edit.categoryHint')}>
          <input
            className="input"
            list="gd-categories"
            value={category}
            maxLength={24}
            placeholder={t('edit.categoryPlaceholder')}
            onChange={(e) => setCategory(e.target.value)}
            onBlur={commitCategory}
            onKeyDown={(e) => e.key === 'Enter' && e.currentTarget.blur()}
          />
          <datalist id="gd-categories">
            {categories.map((c) => <option key={c} value={c} />)}
          </datalist>
        </Field>
        <Field label={t('edit.favorite')} hint={t('edit.favoriteHint')} inline>
          <Switch checked={sound.favorite} label={t('edit.favorite')} onChange={(v) => patch({ favorite: v })} />
        </Field>
      </div>

      <Field label={t('edit.volume')} hint={t('edit.volumeHint')}>
        <Slider label={t('edit.buttonVolume')} value={sound.volume} max={1.5} onChange={(v) => patch({ volume: v })} />
      </Field>

      <Field label={t('edit.whenTapped')} hint={t(`edit.modeHints.${sound.mode}`)}>
        <Segmented label={t('edit.playMode')} value={sound.mode} options={MODES.map((m) => ({ value: m, label: t(`edit.modes.${m}`) }))} onChange={(v) => patch({ mode: v })} />
      </Field>

      <Field label={t('edit.loop')} hint={t('edit.loopHint')} inline>
        <Switch checked={sound.loop} label={t('edit.loop')} onChange={(v) => patch({ loop: v })} />
      </Field>

      <Field label={t('edit.icon')}>
        <div className="icon-grid">
          {Object.keys(SOUND_ICONS).map((name) => (
            <button key={name} className={`icon-choice ${name === sound.icon ? 'is-active' : ''}`} aria-label={t(`icons.${name}`, { defaultValue: name })} title={t(`icons.${name}`, { defaultValue: name })} aria-pressed={name === sound.icon} onClick={() => patch({ icon: name })}>
              <SoundIcon name={name} size={20} />
            </button>
          ))}
        </div>
      </Field>

      <Field label={t('edit.tag')} hint={t('edit.tagHint')}>
        <div className="swatches">
          {TAG_COLORS.map((c) => (
            <button key={c} className={`swatch ${c === sound.color ? 'is-active' : ''}`} style={{ ['--sw' as string]: c }} aria-label={t('edit.tagColor', { color: c })} aria-pressed={c === sound.color} onClick={() => patch({ color: c })} />
          ))}
        </div>
      </Field>

      <Field label={t('edit.position')} hint={t('edit.positionHint')}>
        <div className="btn-row">
          <Button size="sm" icon={<ArrowLeft size={15} />} disabled={index <= 0} onClick={() => move(-1)}>{t('edit.earlier')}</Button>
          <Button size="sm" disabled={index < 0 || index >= profile.layout.length - 1} onClick={() => move(1)}>
            {t('edit.later')} <ArrowRight size={15} />
          </Button>
        </div>
      </Field>
    </Dialog>
  );
}
