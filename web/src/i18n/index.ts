import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import { en } from './en';
import { ro } from './ro';
import { es } from './es';
import { pt } from './pt';
import { de } from './de';
import { fr } from './fr';
import { pl } from './pl';
import { ru } from './ru';

/** Every language GameDeck speaks. English is the default and the fallback for missing text. */
export const LANGUAGES = [
  { code: 'en', label: 'English', locale: 'en-US' },
  { code: 'es', label: 'Español', locale: 'es-ES' },
  { code: 'pt', label: 'Português (Brasil)', locale: 'pt-BR' },
  { code: 'de', label: 'Deutsch', locale: 'de-DE' },
  { code: 'fr', label: 'Français', locale: 'fr-FR' },
  { code: 'pl', label: 'Polski', locale: 'pl-PL' },
  { code: 'ru', label: 'Русский', locale: 'ru-RU' },
  { code: 'ro', label: 'Română', locale: 'ro-RO' },
] as const;
export type Language = (typeof LANGUAGES)[number]['code'];
const CODES = LANGUAGES.map((l) => l.code) as Language[];

/** Cached per browser so the pairing screen (before login) already speaks the user's language. */
const STORAGE_KEY = 'gamedeck.language';

/** Maps regional codes (pt-BR, de-AT) onto a supported language; anything else is English. */
export function normalizeLanguage(value: unknown): Language {
  const base = typeof value === 'string' ? value.toLowerCase().split(/[-_]/)[0] : '';
  return CODES.includes(base as Language) ? (base as Language) : 'en';
}

export function cachedLanguage(): Language {
  try {
    const v = localStorage.getItem(STORAGE_KEY);
    if (v) return normalizeLanguage(v);
  } catch {
    /* storage unavailable */
  }
  return 'en'; // English is the default for new installations
}

export function rememberLanguage(lang: Language) {
  try {
    localStorage.setItem(STORAGE_KEY, lang);
  } catch {
    /* ignore */
  }
}

export const resources = {
  en: { translation: en }, ro: { translation: ro }, es: { translation: es }, pt: { translation: pt },
  de: { translation: de }, fr: { translation: fr }, pl: { translation: pl }, ru: { translation: ru },
} as const;

const localeOf = (lang: Language) => LANGUAGES.find((l) => l.code === lang)?.locale ?? 'en-US';

void i18n.use(initReactI18next).init({
  resources,
  lng: cachedLanguage(),
  fallbackLng: 'en',
  supportedLngs: CODES,
  interpolation: { escapeValue: false }, // React escapes already
  returnNull: false,
});

/** Apply a language everywhere at once: i18n, <html lang>, and the browser cache. */
export function applyLanguage(lang: Language) {
  rememberLanguage(lang);
  if (typeof document !== 'undefined') document.documentElement.lang = localeOf(lang);
  if (i18n.language !== lang) void i18n.changeLanguage(lang);
}

if (typeof document !== 'undefined') document.documentElement.lang = localeOf(cachedLanguage());

export default i18n;

/** BCP-47 locale for dates and numbers in the current language. */
export const locale = () => localeOf(normalizeLanguage(i18n.language));
