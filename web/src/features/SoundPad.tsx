import { useRef, useState, type CSSProperties, type PointerEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { GripVertical, Repeat, Star } from 'lucide-react';
import i18n from 'i18next';
import { SoundIcon } from '../lib/icons';
import type { Sound } from '../lib/types';
import type { PlayingView } from '../state/DeckContext';
import { playedMs } from './TrimEditor';

interface Props {
  sound: Sound;
  playing?: PlayingView;
  editMode: boolean;
  reorderable: boolean;
  showDuration: boolean;
  dragging: boolean;
  onActivate(): void;
  onPointerDown(e: PointerEvent<HTMLButtonElement>): void;
}

/** "6.0s" / "6,0 s" (decimal separator and spacing follow the interface language); "1:05" above a minute. */
export const formatDuration = (ms: number) => {
  const s = Math.round(ms / 1000);
  if (s >= 60) return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  const ro = i18n.language === 'ro';
  const n = new Intl.NumberFormat(ro ? 'ro-RO' : 'en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(ms / 1000);
  return ro ? `${n} s` : `${n}s`;
};

export function SoundPad({ sound, playing, editMode, reorderable, showDuration, dragging, onActivate, onPointerDown }: Props) {
  const { t } = useTranslation();
  // Instant local feedback on touch-down, before the PC has even answered.
  const [pressed, setPressed] = useState(false);
  const release = useRef<number>();

  // Progress is a CSS animation started "in the past" by the elapsed time; no timers.
  const elapsed = playing ? playing.elapsedMs + (performance.now() - playing.receivedAt) : 0;
  const style = {
    '--tag': sound.color,
    '--dur': `${Math.max(playedMs(sound), 1)}ms`,
    '--delay': `-${Math.round(elapsed)}ms`,
  } as CSSProperties;

  const state = playing ? (sound.loop ? t('common.looping') : t('common.playing')) : showDuration ? formatDuration(playedMs(sound)) : sound.category || ' ';

  return (
    <button
      className={`pad ${playing ? 'is-playing' : ''} ${pressed ? 'is-pressed' : ''} ${editMode ? 'is-editing' : ''} ${dragging ? 'is-dragging' : ''}`}
      style={style}
      data-sound-id={sound.id}
      title={sound.label}
      aria-pressed={editMode ? undefined : !!playing}
      aria-label={editMode ? t('pad.edit', { name: sound.label }) : t('pad.state', { name: sound.label, favorite: sound.favorite ? t('pad.favorite') : '', state: playing ? state : t('common.ready') })}
      onClick={onActivate}
      onPointerDown={(e) => {
        window.clearTimeout(release.current);
        setPressed(true);
        release.current = window.setTimeout(() => setPressed(false), 180);
        onPointerDown(e);
      }}
      onContextMenu={(e) => editMode && e.preventDefault()}
    >
      <span className="pad-sheen" aria-hidden />
      <span className="pad-tag" aria-hidden />
      <span className="pad-meta" aria-hidden>
        {sound.favorite && <Star size={12} strokeWidth={2} className="pad-fav" />}
        {sound.loop && <Repeat size={12} strokeWidth={2} />}
      </span>
      <span className="pad-icon">
        <SoundIcon name={sound.icon} />
      </span>
      <span className="pad-label">{sound.label}</span>
      <span className="pad-sub num">{state}</span>
      {editMode && reorderable && (
        <span className="pad-grip" data-grip aria-hidden>
          <GripVertical size={16} />
        </span>
      )}
      {playing && (
        <span className="pad-progress" aria-hidden>
          <span key={playing.instanceId} className={sound.loop ? 'is-loop' : ''} />
        </span>
      )}
    </button>
  );
}
