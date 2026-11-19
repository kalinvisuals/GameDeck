/**
 * Localization guarantees for the dashboard: en/ro stay in sync, Romanian is written properly,
 * placeholders survive translation, plurals follow each language's rules, the language switch and
 * its persistence work, and no screen ships hardcoded interface text.
 */
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import ts from 'typescript';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { en } from '../src/i18n/en';
import { ro } from '../src/i18n/ro';
import { es } from '../src/i18n/es';
import { pt } from '../src/i18n/pt';
import { de } from '../src/i18n/de';
import { fr } from '../src/i18n/fr';
import { pl } from '../src/i18n/pl';
import { ru } from '../src/i18n/ru';

// A tiny in-memory localStorage, installed before i18n loads so the cache is exercised for real.
const store = new Map<string, string>();
globalThis.localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => void store.set(k, String(v)),
  removeItem: (k: string) => void store.delete(k),
  clear: () => store.clear(),
  key: (i: number) => [...store.keys()][i] ?? null,
  get length() {
    return store.size;
  },
} as Storage;

type I18nModule = typeof import('../src/i18n');
let mod: I18nModule;
beforeAll(async () => {
  mod = await import('../src/i18n');
});

const SRC = join(__dirname, '..', 'src');
type Tree = { [k: string]: string | Tree };

function flatten(tree: Tree, prefix = ''): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(tree)) {
    const key = prefix ? `${prefix}.${k}` : k;
    if (typeof v === 'string') out[key] = v;
    else Object.assign(out, flatten(v, key));
  }
  return out;
}

const EN = flatten(en as Tree);
const RO = flatten(ro as Tree);
/** Every translation next to English, by language code. */
const OTHERS: Record<string, Record<string, string>> = {
  ro: RO, es: flatten(es as Tree), pt: flatten(pt as Tree), de: flatten(de as Tree),
  fr: flatten(fr as Tree), pl: flatten(pl as Tree), ru: flatten(ru as Tree),
};
const ALL = { en: EN, ...OTHERS };
const PLURAL = /_(zero|one|two|few|many|other)$/;
/** True when `key` is a translation key, directly or as the base of plural forms. */
const exists = (key: string) => key in EN || `${key}_other` in EN;

function sourceFiles(dir = SRC): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    const p = join(dir, e.name);
    if (e.isDirectory()) return e.name === 'i18n' ? [] : sourceFiles(p);
    return /\.tsx?$/.test(e.name) ? [p] : [];
  });
}

describe('translation resources', () => {
  it('every language has exactly the same keys as English', () => {
    for (const [lang, table] of Object.entries(OTHERS)) {
      expect(Object.keys(EN).filter((k) => !(k in table)), `missing in ${lang}`).toEqual([]);
      expect(Object.keys(table).filter((k) => !(k in EN)), `extra in ${lang}`).toEqual([]);
    }
  });

  it('has no empty values', () => {
    for (const [lang, table] of Object.entries(ALL)) {
      expect(Object.entries(table).filter(([, v]) => !v.trim()).map(([k]) => k), lang).toEqual([]);
    }
  });

  it('every plural group has every form each language needs', () => {
    const bases = new Set(Object.keys(EN).filter((k) => PLURAL.test(k)).map((k) => k.replace(PLURAL, '')));
    for (const [lang, table] of Object.entries(ALL)) {
      const forms = new Intl.PluralRules(lang).resolvedOptions().pluralCategories;
      for (const base of bases) {
        for (const form of forms) expect(table, `${lang} ${base}_${form}`).toHaveProperty([`${base}_${form}`]);
      }
    }
  });

  it('the new languages are actually translated', () => {
    // Product names, units and words borrowed from English make a few identical strings normal.
    for (const lang of ['es', 'pt', 'de', 'fr', 'pl', 'ru']) {
      const same = Object.keys(EN).filter((k) => OTHERS[lang][k] === EN[k] && /\p{L}{4,}/u.test(EN[k].replace(/\{\{[^}]+\}\}|<[^>]+>/g, '')));
      expect(same.length, `${lang}: ${same.join(', ')}`).toBeLessThan(60); // German shares many words with English (Sound, Version, Filter)
    }
  });

  it('Romanian is actually translated (identical text only for names, units and symbols)', () => {
    // Product names, device-type names, units and symbols read the same in both languages.
    const allowed = new Set([
      'common.dash', 'common.defaultTag', 'nav.short.profiles', 'pair.devices.iphone', 'pair.devices.ipad', 'pair.devices.mac',
      'pair.devices.browser', 'soundboard.sizeS', 'soundboard.sizeM', 'soundboard.sizeL', 'soundboard.formats',
      'dashboard.uptimeMin', 'dashboard.uptimeHours', 'dashboard.ms', 'routing.eyebrow',
      'icons.radio', 'icons.film', 'settings.windows', 'settings.port',
      'nav.mixer', 'mixer.title', 'nav.status.mic', 'nav.short.mixer', 'icons.piano', 'icons.gamepad',
      'nav.youtube', 'settings.logoModels.original', 'settings.logoFinishes.cobalt', 'settings.logoFinishes.arctic',
      // Feature names kept as product vocabulary, words spelled the same in Romanian, and placeholder-only templates.
      'nav.soundboard', 'soundboard.eyebrow', 'mixer.soundboard', 'dashboard.soundboardRow',
      'dashboard.mixerLink', 'nav.control', 'settings.audioEyebrow', 'pad.state',
      'scenes.title', 'scenes.eyebrow', 'apps.windows',
      'nav.deck', 'nav.short.deck', 'deck.title', 'deck.groups.gamedeck', 'deck.groups.windows', 'deck.macro', 'macros.untitled',
      'deck.tabs.hardware', 'hw.gb', 'deckProfiles.grid', 'deckProfiles.placeholder', 'deck.looks.normal', 'deck.program',
      'settings.section.voice', // "Discord" in both languages
    ]);
    const same = Object.keys(EN).filter((k) => EN[k] === RO[k] && !allowed.has(k));
    expect(same).toEqual([]);
  });
});

describe('Romanian orthography', () => {
  const all = Object.values(RO).join('\n');

  it('uses all Romanian diacritics', () => {
    for (const ch of ['ă', 'â', 'î', 'ș', 'ț', 'Î', 'Ș', 'Ț']) expect(all, ch).toContain(ch);
  });

  it('uses comma-below ș ț, never the cedilla look-alikes ş ţ', () => {
    expect(Object.entries(RO).filter(([, v]) => /[şţŞŢ]/.test(v)).map(([k]) => k)).toEqual([]);
  });

  it('uses Romanian quotes „…”, not English “…”', () => {
    expect(Object.entries(RO).filter(([, v]) => v.includes('“')).map(([k]) => k)).toEqual([]);
  });

  it('has no common words written without diacritics', () => {
    // Lookarounds instead of \b: JavaScript's \b treats ă/ș/ț as word breaks.
    const bare = /(?<!\p{L})(si|sa|in|daca|asa|dupa|pana|inca|aceasta|tau|tai|fara|intre|catre)(?!\p{L})/iu;
    expect(Object.entries(RO).filter(([, v]) => bare.test(v.replace(/<[^>]+>|\{\{[^}]+\}\}/g, ''))).map(([k]) => `${k}: ${RO[k]}`)).toEqual([]);
  });
});

describe('interpolation and markup', () => {
  const tokens = (s: string) => [...s.matchAll(/\{\{\s*(\w+)\s*\}\}|<\/?(\w+)>/g)].map((m) => m[1] ?? `<${m[2]}>`).sort();

  it('keeps the same placeholders and tags in every language', () => {
    for (const [lang, table] of Object.entries(OTHERS)) {
      const broken = Object.keys(EN).filter((k) => JSON.stringify(tokens(EN[k])) !== JSON.stringify(tokens(table[k])));
      expect(broken, lang).toEqual([]);
    }
  });

  it('fills placeholders with user content verbatim', () => {
    const i18n = mod.default;
    void i18n.changeLanguage('ro');
    expect(i18n.t('profiles.soundsOn', { name: 'Ranked <3' })).toBe('Sunete în „Ranked <3”');
    expect(i18n.t('settings.steps.s3', { capture: 'CABLE Output (VB-Audio Virtual Cable)' })).toContain('<code>CABLE Output (VB-Audio Virtual Cable)</code>');
    void i18n.changeLanguage('en');
    expect(i18n.t('mixer.micBuffer', { underruns: 3, drops: 1, ms: '42' })).toBe('Mic buffer: 3 underruns · 1 drops · 42 ms');
  });
});

describe('plurals', () => {
  it('follows Romanian rules: 1 / 2–19 / 20+', () => {
    const i18n = mod.default;
    void i18n.changeLanguage('ro');
    expect(i18n.t('profiles.buttons', { count: 1 })).toBe('1 buton');
    expect(i18n.t('profiles.buttons', { count: 5 })).toBe('5 butoane');
    expect(i18n.t('profiles.buttons', { count: 0 })).toBe('0 butoane');
    expect(i18n.t('profiles.buttons', { count: 20 })).toBe('20 de butoane');
    expect(i18n.t('profiles.buttons', { count: 101 })).toBe('101 butoane');
    expect(i18n.t('toast.added', { count: 1 })).toBe('A fost adăugat 1 sunet');
  });

  it('follows Polish and Russian rules: 1 / 2–4 / 5+', () => {
    const i18n = mod.default;
    void i18n.changeLanguage('pl');
    expect(i18n.t('profiles.buttons', { count: 1 })).toBe('1 przycisk');
    expect(i18n.t('profiles.buttons', { count: 3 })).toBe('3 przyciski');
    expect(i18n.t('profiles.buttons', { count: 5 })).toBe('5 przycisków');
    expect(i18n.t('profiles.buttons', { count: 22 })).toBe('22 przyciski');
    void i18n.changeLanguage('ru');
    expect(i18n.t('profiles.buttons', { count: 1 })).toBe('1 кнопка');
    expect(i18n.t('profiles.buttons', { count: 21 })).toBe('21 кнопка');
    expect(i18n.t('profiles.buttons', { count: 3 })).toBe('3 кнопки');
    expect(i18n.t('profiles.buttons', { count: 11 })).toBe('11 кнопок');
  });

  it('follows English rules: 1 / other', () => {
    const i18n = mod.default;
    void i18n.changeLanguage('en');
    expect(i18n.t('profiles.buttons', { count: 1 })).toBe('1 button');
    expect(i18n.t('profiles.buttons', { count: 5 })).toBe('5 buttons');
    expect(i18n.t('soundboard.search', { count: 20 })).toBe('Search 20 sounds');
  });
});

describe('fallback', () => {
  it('falls back to English for a key missing in Romanian, then to the key itself', () => {
    const i18n = mod.default;
    i18n.addResource('en', 'translation', 'test.onlyInEnglish', 'English only');
    void i18n.changeLanguage('ro');
    expect(i18n.t('test.onlyInEnglish')).toBe('English only');
    expect(i18n.t('test.nowhere')).toBe('test.nowhere');
  });

  it('maps regional codes onto a supported language and anything else onto English', () => {
    expect(mod.normalizeLanguage('en-US')).toBe('en');
    expect(mod.normalizeLanguage('EN')).toBe('en');
    expect(mod.normalizeLanguage('ro-RO')).toBe('ro');
    expect(mod.normalizeLanguage('pt-BR')).toBe('pt');
    expect(mod.normalizeLanguage('de_AT')).toBe('de');
    expect(mod.normalizeLanguage('fr')).toBe('fr');
    expect(mod.normalizeLanguage('ja')).toBe('en');
    expect(mod.normalizeLanguage(undefined)).toBe('en');
  });
});

describe('language switching and persistence', () => {
  beforeEach(() => store.clear());

  it('defaults to English on a fresh install', () => {
    expect(mod.cachedLanguage()).toBe('en');
  });

  it('switches immediately and remembers the choice', () => {
    const i18n = mod.default;
    mod.applyLanguage('en');
    expect(i18n.language).toBe('en');
    expect(i18n.t('nav.settings')).toBe('Settings');
    expect(store.get('gamedeck.language')).toBe('en');
    expect(mod.cachedLanguage()).toBe('en');

    mod.applyLanguage('ro');
    expect(i18n.language).toBe('ro');
    expect(i18n.t('nav.settings')).toBe('Setări');
    expect(mod.cachedLanguage()).toBe('ro');

    mod.applyLanguage('de');
    expect(i18n.t('nav.settings')).toBe('Einstellungen');
    expect(mod.cachedLanguage()).toBe('de');
  });

  it('formats dates and numbers in the active language', () => {
    mod.applyLanguage('ro');
    expect(mod.locale()).toBe('ro-RO');
    mod.applyLanguage('en');
    expect(mod.locale()).toBe('en-US');
    mod.applyLanguage('pt');
    expect(mod.locale()).toBe('pt-BR');
  });

  it('ignores a corrupted cache value', () => {
    store.set('gamedeck.language', 'klingon');
    expect(mod.cachedLanguage()).toBe('en');
  });
});

describe('mobile interface', () => {
  const NAV_IDS = ['dashboard', 'soundboard', 'mixer', 'deck', 'profiles', 'guide', 'settings'];

  it('has short tab-bar labels for every screen, short enough for a phone', () => {
    for (const lang of Object.values(ALL)) {
      for (const id of NAV_IDS) {
        const label = lang[`nav.short.${id}`];
        expect(label, id).toBeTruthy();
        expect(label.length, `${id}: ${label}`).toBeLessThanOrEqual(10);
      }
      expect(lang['nav.more']).toBeTruthy();
    }
  });

  it('translates the compact status chips and pairing screen', () => {
    for (const k of ['nav.status.link', 'nav.status.call', 'nav.status.ears', 'pair.title', 'pair.code', 'pair.pair', 'pair.devices.androidPhone']) {
      for (const [lang, table] of Object.entries(OTHERS)) expect(table[k], `${lang} ${k}`).not.toBe(EN[k]);
    }
  });
});

describe('source code', () => {
  const files = sourceFiles();

  it('only references keys that exist', () => {
    const missing: string[] = [];
    for (const f of files) {
      const text = readFileSync(f, 'utf8');
      for (const m of text.matchAll(/\bt\(\s*'([\w.]+)'|i18nKey="([\w.]+)"/g)) {
        const key = m[1] ?? m[2];
        if (!exists(key)) missing.push(`${relative(SRC, f)}: ${key}`);
      }
    }
    expect(missing).toEqual([]);
  });

  it('resolves every dynamically built key', async () => {
    const { SOUND_ICONS } = await import('../src/lib/icons');
    const dynamic = [
      ...['dashboard', 'soundboard', 'mixer', 'deck', 'profiles', 'guide', 'settings'].flatMap((id) => [`nav.${id}`, `nav.short.${id}`]),
      'nav.control', 'nav.manage',
      ...['devices', 'voice', 'network', 'prefs', 'diag'].map((id) => `settings.section.${id}`),
      ...['s1', 's2', 's3', 's4', 's6'].map((s) => `settings.steps.${s}`),
      ...['none', 'launch', 'url', 'keys', 'media', 'sound', 'stopAll', 'mute', 'volume', 'scene', 'appMute', 'macro', 'sensor', 'wait', 'window'].map((x) => `deck.types.${x}`),
      ...['restore', 'minimize', 'close'].map((x) => `deck.windowModes.${x}`),
      ...['gamedeck', 'windows', 'display', 'steps'].map((x) => `deck.groups.${x}`),
      ...['mic', 'virtual', 'monitor'].map((x) => `deck.channels.${x}`),
      ...['playPause', 'next', 'previous', 'volumeUp', 'volumeDown', 'mute'].map((x) => `deck.media.${x}`),
      ...['toggle', 'on', 'off', 'up', 'down', 'set'].map((x) => `deck.modes.${x}`),
      ...['cpu.usage', 'cpu.temp', 'cpu.clock', 'gpu.usage', 'gpu.temp', 'gpu.vram', 'gpu.clock', 'gpu.fan', 'ram.usage', 'ram.used'].map((x) => `deck.sensors.${x}`),
      ...['deck', 'hardware', 'macros', 'profiles'].map((x) => `deck.tabs.${x}`),
      ...['Restart', 'Toggle', 'Overlap'].flatMap((m) => [`edit.modes.${m}`, `edit.modeHints.${m}`]),
      ...Object.keys(SOUND_ICONS).map((n) => `icons.${n}`),
      ...['compact', 'regular', 'large'].map((s) => `settings.sizes.${s}`),
      ...['s1', 's2', 's3', 's4', 's5', 's6'].map((s) => `guide.vb.${s}`),
      ...['s1', 's2', 's3', 's4', 's5'].flatMap((s) => [`guide.nvidia.${s}`, `guide.amd.${s}`]),
      ...['s1', 's2', 's3', 's4', 's5'].map((s) => `guide.gd.${s}`),
      ...['s1', 's2', 's3', 's4', 's5', 's6', 's7'].map((s) => `guide.dc.${s}`),
      ...['s1', 's2'].map((s) => `guide.verify.${s}`),
      ...['nothing', 'noVoice', 'noSounds', 'cut', 'pitch', 'crackle', 'echo', 'allPc', 'double'].flatMap((k) => [`guide.trouble.${k}.q`, `guide.trouble.${k}.a`]),
    ];
    expect(dynamic.filter((k) => !exists(k))).toEqual([]);
  });

  it('has no hardcoded interface text', () => {
    // Attributes a person reads or a screen reader announces.
    const USER_FACING = new Set(['label', 'title', 'placeholder', 'aria-label', 'aria-description', 'alt', 'hint', 'eyebrow', 'subtitle', 'empty', 'meterNote', 'role-text', 'name']);
    // Text that is the same in every language: brand, units, symbols, file formats.
    const ALLOWED = /^(GameDeck|Game|Deck|S|M|L|MP3 · WAV|dBFS|ms|dB|Hz|kHz|%|★|→|←|·|—|\+|\/|×)$/;
    const hasWords = (s: string) => /\p{L}{2,}/u.test(s);
    const found: string[] = [];

    for (const f of files.filter((p) => p.endsWith('.tsx'))) {
      const text = readFileSync(f, 'utf8');
      const sf = ts.createSourceFile(f, text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
      const report = (node: ts.Node, value: string) => {
        const v = value.trim();
        if (!v || !hasWords(v) || ALLOWED.test(v)) return;
        const { line } = sf.getLineAndCharacterOfPosition(node.getStart());
        found.push(`${relative(SRC, f)}:${line + 1} ${JSON.stringify(v)}`);
      };
      const visit = (node: ts.Node) => {
        if (ts.isJsxText(node)) report(node, node.getText());
        else if (ts.isJsxAttribute(node) && node.initializer) {
          const name = node.name.getText();
          const init = node.initializer;
          if (USER_FACING.has(name)) {
            if (ts.isStringLiteral(init)) report(init, init.text);
            else if (ts.isJsxExpression(init) && init.expression && (ts.isStringLiteral(init.expression) || ts.isNoSubstitutionTemplateLiteral(init.expression))) report(init, init.expression.text);
          }
        } else if (ts.isCallExpression(node) && ['confirm', 'alert', 'prompt', 'toast', 'push'].includes(node.expression.getText())) {
          const arg = node.arguments[0];
          if (arg && (ts.isStringLiteral(arg) || ts.isTemplateExpression(arg) || ts.isNoSubstitutionTemplateLiteral(arg))) report(arg, arg.getText().slice(1, -1));
        }
        ts.forEachChild(node, visit);
      };
      visit(sf);
    }
    expect(found).toEqual([]);
  });
});
