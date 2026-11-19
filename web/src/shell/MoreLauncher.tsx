import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type { LucideIcon } from 'lucide-react';
import { useDeck } from '../state/DeckContext';
import { LauncherCard, LauncherScreen } from './PadsLauncher';

export type MoreChoice = 'profiles' | 'guide' | 'settings';

/** Phones and tablets: the "More" tab opens the remaining screens as full-screen cards. */
export function MoreLauncher({ items, current, status, onChoose, onClose }: {
  items: { id: MoreChoice; icon: LucideIcon }[];
  current: string;
  status: ReactNode;
  onChoose(v: MoreChoice): void;
  onClose(): void;
}) {
  const { t } = useTranslation();
  const { state, profile } = useDeck();

  const meta: Record<MoreChoice, string[]> = {
    profiles: [t('more.profiles', { count: state.profiles.length }), profile.name],
    guide: [],
    settings: [t('nav.versionLine', { version: state.server.version })],
  };

  return (
    <LauncherScreen className="is-more" eyebrow={t('nav.more')} title={t('more.title')} onClose={onClose}>
      <div className="pads-choices">
        {items.map(({ id, icon: Icon }) => (
          <LauncherCard
            key={id}
            kind={id}
            current={current === id}
            icon={<Icon size={28} strokeWidth={1.6} />}
            title={t(`nav.${id}`)}
            description={t(`more.${id}Body`)}
            meta={meta[id]}
            art={<Icon className="pads-art-icon" size={76} strokeWidth={1} />}
            onClick={() => onChoose(id)}
          />
        ))}
      </div>
      <div className="more-status">{status}</div>
    </LauncherScreen>
  );
}
