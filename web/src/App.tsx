import { DeckProvider } from './state/DeckContext';
import { AppShell } from './shell/AppShell';
import { PairView } from './views/PairView';
import { LogoMark } from './shell/Logo';
import { Skeleton } from './ui/extras';
import { useTranslation } from 'react-i18next';

/** Skeleton of the real layout, so the first paint over Wi-Fi doesn't jump. */
function Splash() {
  const { t } = useTranslation();
  return (
    <div className="splash" aria-busy="true" aria-label={t('common.connecting')}>
      <div className="splash-mark">
        <LogoMark size={34} />
        <span className="faint small">{t('common.connecting')}</span>
      </div>
      <div className="splash-grid" aria-hidden>
        {Array.from({ length: 6 }, (_, i) => <Skeleton key={i} height="100%" radius={16} />)}
      </div>
    </div>
  );
}

export function App() {
  return (
    <DeckProvider loading={<Splash />} pair={(onPaired) => <PairView onPaired={onPaired} />}>
      <AppShell />
    </DeckProvider>
  );
}
