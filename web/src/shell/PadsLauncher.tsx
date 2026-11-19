import { useEffect, useRef, type CSSProperties, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { AudioLines, ChevronRight, Grid3x3, MonitorPlay, X } from 'lucide-react';
import { useDeck } from '../state/DeckContext';
import { useYouTube } from '../state/youtube';

export type PadsChoice = 'soundboard' | 'deck' | 'youtube';

/**
 * Phones and tablets: the "Pads" tab opens this full-screen chooser between the Soundboard, the
 * Smart Deck and YouTube, so all three live one tap away instead of hiding under "More".
 */
export function PadsLauncher({ current, onChoose, onClose }: { current: PadsChoice | null; onChoose(v: PadsChoice): void; onClose(): void }) {
  const { t } = useTranslation();
  const { state, profile } = useDeck();
  const youtube = useYouTube();

  const deckProfile = state.deck.profiles.find((p) => p.id === state.deck.activeProfileId) ?? state.deck.profiles[0];
  const deckButtons = deckProfile ? deckProfile.pages.reduce((n, p) => n + p.buttons.length, 0) : 0;

  return (
    <LauncherScreen eyebrow={t('pads.eyebrow')} title={t('pads.title')} onClose={onClose}>
      <div className="pads-choices">
        <LauncherCard
          kind="soundboard"
          current={current === 'soundboard'}
          icon={<AudioLines size={30} strokeWidth={1.6} />}
          title={t('nav.soundboard')}
          description={t('pads.soundboardBody')}
          meta={[t('pads.sounds', { count: state.sounds.length }), profile.name]}
          art={<WaveArt />}
          onClick={() => onChoose('soundboard')}
        />
        <LauncherCard
          kind="deck"
          current={current === 'deck'}
          icon={<Grid3x3 size={30} strokeWidth={1.6} />}
          title={t('nav.deck')}
          description={t('pads.deckBody')}
          meta={[t('pads.buttons', { count: deckButtons }), deckProfile?.name ?? '']}
          art={<KeysArt />}
          onClick={() => onChoose('deck')}
        />
        <LauncherCard
          kind="youtube"
          current={current === 'youtube'}
          icon={<MonitorPlay size={30} strokeWidth={1.6} />}
          title={t('nav.youtube')}
          description={t('pads.youtubeBody')}
          meta={[youtube?.track?.title ?? t(youtube?.connected ? 'youtube.connected' : 'youtube.disconnected')]}
          art={<MonitorPlay className="pads-art-icon" size={76} strokeWidth={1} />}
          onClick={() => onChoose('youtube')}
        />
      </div>
    </LauncherScreen>
  );
}

/** The full-screen frame shared by the tab bar's choosers ("Pads" and "More"). */
export function LauncherScreen({ eyebrow, title, className = '', onClose, children }: {
  eyebrow: string;
  title: string;
  className?: string;
  onClose(): void;
  children: ReactNode;
}) {
  const { t } = useTranslation();
  // Focus goes to the screen itself (not a card), so nothing looks selected before the user picks.
  const panel = useRef<HTMLDivElement>(null);
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    panel.current?.focus();
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onCloseRef.current();
    window.addEventListener('keydown', onKey);
    // The page underneath must not scroll while the chooser covers it.
    const overflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      window.removeEventListener('keydown', onKey);
      document.body.style.overflow = overflow;
      previous?.focus?.();
    };
  }, []);

  return (
    <div ref={panel} tabIndex={-1} className={`pads-launcher ${className}`} role="dialog" aria-modal="true" aria-labelledby="launcher-title">
      <header className="pads-head">
        <div>
          <span className="eyebrow">{eyebrow}</span>
          <h2 id="launcher-title">{title}</h2>
        </div>
        <button type="button" className="pads-close" aria-label={t('common.close')} onClick={onClose}>
          <X size={20} />
        </button>
      </header>
      {children}
    </div>
  );
}

export function LauncherCard(props: {
  kind: string;
  current: boolean;
  icon: ReactNode;
  title: string;
  description: string;
  meta: string[];
  art: ReactNode;
  onClick(): void;
}) {
  const { t } = useTranslation();
  return (
    <button type="button" className={`pads-choice is-${props.kind} ${props.current ? 'is-current' : ''}`} onClick={props.onClick}
      aria-current={props.current ? 'page' : undefined}>
      <span className="pads-art" aria-hidden>{props.art}</span>
      <span className="pads-icon">{props.icon}</span>
      <span className="pads-text">
        <span className="pads-title-row">
          <strong>{props.title}</strong>
          {props.current && <span className="pads-current">{t('pads.current')}</span>}
        </span>
        <span className="pads-desc">{props.description}</span>
        {props.meta.some(Boolean) && <span className="pads-meta num">{props.meta.filter(Boolean).join(' · ')}</span>}
      </span>
      <ChevronRight className="pads-chevron" size={22} aria-hidden />
    </button>
  );
}

/** Decorative sound wave for the Soundboard card. */
function WaveArt() {
  const bars = [0.35, 0.7, 0.5, 0.95, 0.6, 0.8, 0.4, 0.65, 0.3, 0.55, 0.85, 0.45];
  return (
    <span className="pads-wave">
      {bars.map((h, i) => <span key={i} style={{ '--h': h, '--i': i } as CSSProperties} />)}
    </span>
  );
}

/** Decorative key grid for the Smart Deck card, one key lit. */
function KeysArt() {
  return (
    <span className="pads-keys">
      {Array.from({ length: 6 }, (_, i) => <span key={i} className={i === 1 ? 'is-lit' : ''} />)}
    </span>
  );
}
