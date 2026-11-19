import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { api } from '../lib/api';
import { useDeck } from '../state/DeckContext';
import { Segmented } from '../ui/controls';
import { PageHeader } from '../ui/Card';
import { DeckGrid } from '../features/deck/DeckGrid';
import { HardwarePanel } from '../features/deck/HardwarePanel';
import { MacrosPanel } from '../features/deck/MacrosPanel';
import { DeckProfilesPanel } from '../features/deck/DeckProfilesPanel';

type Tab = 'deck' | 'hardware' | 'macros' | 'profiles';
const TABS: Tab[] = ['deck', 'hardware', 'macros', 'profiles'];
const TAB_KEY = 'gamedeck.deckTab';

function initialTab(): Tab {
  try {
    const v = localStorage.getItem(TAB_KEY) as Tab | null;
    if (v && TABS.includes(v)) return v;
  } catch {
    /* storage unavailable */
  }
  return 'deck';
}

/** Smart Deck: buttons that act on the PC, live hardware readings, macros and deck profiles. */
export function DeckView() {
  const { t } = useTranslation();
  const { state, run } = useDeck();
  const [tab, setTabState] = useState<Tab>(initialTab);
  const { profiles, activeProfileId } = state.deck;
  const profile = profiles.find((p) => p.id === activeProfileId) ?? profiles[0];

  const setTab = (v: Tab) => {
    setTabState(v);
    try {
      localStorage.setItem(TAB_KEY, v);
    } catch {
      /* ignore */
    }
  };

  return (
    <div className="page deck-page">
      <PageHeader title={t('deck.title')} subtitle={t('deck.subtitle')} />
      <div className="deck-tabs">
        <Segmented size="sm" label={t('deck.title')} value={tab} onChange={setTab} options={TABS.map((v) => ({ value: v, label: t(`deck.tabs.${v}`) }))} />
        {tab === 'deck' && profiles.length > 1 && (
          <div className="profile-switch" role="tablist" aria-label={t('deck.tabs.profiles')}>
            {profiles.map((p) => (
              <button key={p.id} role="tab" aria-selected={p.id === profile.id} className={`profile-tab ${p.id === profile.id ? 'is-active' : ''}`}
                onClick={() => p.id !== profile.id && run(() => api.activateDeckProfile(p.id))}>
                {p.name}
              </button>
            ))}
          </div>
        )}
      </div>

      {tab === 'deck' && <DeckGrid key={profile.id} profile={profile} onOpenMonitor={() => setTab('hardware')} />}
      {tab === 'hardware' && <HardwarePanel />}
      {tab === 'macros' && <MacrosPanel />}
      {tab === 'profiles' && <DeckProfilesPanel />}
    </div>
  );
}
