import { useTranslation } from 'react-i18next';
import { applyLanguage, LANGUAGES, normalizeLanguage, type Language } from '../i18n';
import { Select } from '../ui/controls';
import { useDeckOptional } from '../state/DeckContext';

/**
 * The interface language, each listed in its own language. When paired, the choice is saved on the
 * PC and applies to every device and to the PC windows; on the pairing screen (no session yet) it's
 * kept in this browser only.
 */
export function LanguageSwitch({ local }: { local?: boolean }) {
  const { t, i18n } = useTranslation();
  const deck = useDeckOptional();
  const value = normalizeLanguage(i18n.language);
  const change = (lang: Language) => {
    applyLanguage(lang); // immediate, no reload
    if (!local && deck) deck.setLanguage(lang);
  };
  return (
    <Select aria-label={t('settings.language')} value={value} onChange={(e) => change(normalizeLanguage(e.target.value))}>
      {LANGUAGES.map((l) => <option key={l.code} value={l.code} lang={l.code}>{l.label}</option>)}
    </Select>
  );
}
