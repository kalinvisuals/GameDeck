/**
 * The GameDeck logo: designs and finishes offered in Settings. The Windows side draws the same shapes
 * in src/GameDeck.App/Tray/LogoRenderer.cs, and both use the ids from LogoCatalog.cs.
 */
export type LogoSurface = 'dark' | 'light';

interface KeySpec { x: number; y: number; s: number; r: number; turn: number; style: 'accent' | 'body' | 'frame' }
interface ModelSpec { key: KeySpec; bigHole?: boolean; sharpNotch?: boolean; litCore?: boolean; nobleBody?: boolean }
type Pair = readonly [string, string];
interface FinishSpec { swatch: string; bodyDark: Pair; accentDark: Pair; bodyLight: Pair; accentLight: Pair }

const STANDARD_KEY: KeySpec = { x: 46, y: 3, s: 15, r: 4, turn: 10, style: 'accent' };
const NEUTRAL_KEY: KeySpec = { ...STANDARD_KEY, style: 'body' };

const MODELS = {
  original: { key: STANDARD_KEY, sharpNotch: true },
  fine: { key: STANDARD_KEY },
  bigd: { key: STANDARD_KEY, bigHole: true },
  bigkey: { key: { x: 45, y: 1.5, s: 17.5, r: 4.6, turn: 10, style: 'accent' } },
  straight: { key: { x: 46, y: 4, s: 15, r: 4, turn: 0, style: 'accent' } },
  dynamic: { key: { x: 46.5, y: 1.5, s: 15, r: 4, turn: 22, style: 'accent' } },
  outline: { key: { x: 47.5, y: 4.5, s: 12, r: 3, turn: 10, style: 'frame' } },
  litd: { key: STANDARD_KEY, litCore: true },
  core: { key: NEUTRAL_KEY, litCore: true },
  noble: { key: NEUTRAL_KEY, nobleBody: true },
  mono: { key: NEUTRAL_KEY },
} satisfies Record<string, ModelSpec>;

const FINISHES = {
  champagne: { swatch: '#d9bd86', bodyDark: ['#ffffff', '#dfe2e7'], accentDark: ['#f6e6bf', '#b98f50'], bodyLight: ['#24262c', '#08090b'], accentLight: ['#c49a54', '#8c652c'] },
  platinum: { swatch: '#d5dae2', bodyDark: ['#ffffff', '#aeb5c0'], accentDark: ['#ffffff', '#c4cad3'], bodyLight: ['#30333a', '#08090b'], accentLight: ['#858c98', '#565c67'] },
  cobalt: { swatch: '#3f83e8', bodyDark: ['#ffffff', '#dfe4ec'], accentDark: ['#8fc4ff', '#2f72e4'], bodyLight: ['#1c2638', '#080c14'], accentLight: ['#2f7cf0', '#164fb4'] },
  arctic: { swatch: '#39bfe0', bodyDark: ['#ffffff', '#dce5ea'], accentDark: ['#a4efff', '#1ea8d4'], bodyLight: ['#17272e', '#070d10'], accentLight: ['#16a4cf', '#0b6f90'] },
  emerald: { swatch: '#22a982', bodyDark: ['#ffffff', '#dde6e2'], accentDark: ['#8df0cd', '#129c74'], bodyLight: ['#16261f', '#070d0a'], accentLight: ['#16a079', '#0b6c51'] },
  steel: { swatch: '#7d98b6', bodyDark: ['#ffffff', '#d7dee7'], accentDark: ['#c5d5e6', '#6e8cad'], bodyLight: ['#1d2630', '#090c10'], accentLight: ['#5f7fa2', '#3e5873'] },
  bronze: { swatch: '#b57f50', bodyDark: ['#ffffff', '#e3e0dc'], accentDark: ['#ecc39c', '#9c673b'], bodyLight: ['#29221d', '#0c0907'], accentLight: ['#ad7443', '#774a26'] },
  monochrome: { swatch: '#ffffff', bodyDark: ['#ffffff', '#ffffff'], accentDark: ['#ffffff', '#ffffff'], bodyLight: ['#0b0c0f', '#0b0c0f'], accentLight: ['#0b0c0f', '#0b0c0f'] },
} satisfies Record<string, FinishSpec>;

export type LogoModel = keyof typeof MODELS;
export type LogoFinish = keyof typeof FINISHES;

export const LOGO_MODELS = Object.keys(MODELS) as LogoModel[];
export const LOGO_FINISHES = Object.keys(FINISHES) as LogoFinish[];
export const DEFAULT_LOGO_MODEL: LogoModel = 'bigkey';
export const DEFAULT_LOGO_FINISH: LogoFinish = 'cobalt';
export const logoSwatch = (finish: LogoFinish) => FINISHES[finish].swatch;

export const asLogoModel = (id: string | undefined): LogoModel => (id && id in MODELS ? (id as LogoModel) : DEFAULT_LOGO_MODEL);
export const asLogoFinish = (id: string | undefined): LogoFinish => (id && id in FINISHES ? (id as LogoFinish) : DEFAULT_LOGO_FINISH);

function superellipse(half: number, n: number): string {
  const e = 2 / n;
  let d = '';
  for (let i = 0; i < 180; i++) {
    const a = (i / 180) * Math.PI * 2;
    const c = Math.cos(a);
    const s = Math.sin(a);
    const x = 32 + half * Math.sign(c) * Math.abs(c) ** e;
    const y = 32 + half * Math.sign(s) * Math.abs(s) ** e;
    d += `${i ? 'L' : 'M'}${x.toFixed(2)} ${y.toFixed(2)}`;
  }
  return `${d}z`;
}

const OUTER = superellipse(28, 4.5);
const TILE = superellipse(30, 5);
const LETTER = 'M18 21h9a11 11 0 0 1 0 22h-9a2 2 0 0 1-2-2V23a2 2 0 0 1 2-2z';
const BIG_LETTER = 'M17 19h9a13 13 0 0 1 0 26h-9a3 3 0 0 1-3-3V22a3 3 0 0 1 3-3z';
const SHARP_NOTCH = '<rect x="42" y="-10" width="32" height="36" fill="#000"/>';
const SOFT_NOTCH = '<rect x="42" y="-24" width="46" height="50" rx="6" fill="#000"/>';

const gradient = (id: string, x1: number, y1: number, x2: number, y2: number, [from, to]: Pair) =>
  `<linearGradient id="${id}" gradientUnits="userSpaceOnUse" x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}"><stop offset="0" stop-color="${from}"/><stop offset="1" stop-color="${to}"/></linearGradient>`;

/** SVG content on a 64-unit grid. `prefix` keeps gradient ids unique when several logos share a page. */
export function logoMarkup(modelId: LogoModel, finishId: LogoFinish, surface: LogoSurface, prefix: string): string {
  const model: ModelSpec = MODELS[modelId];
  const finish: FinishSpec = FINISHES[finishId];
  const light = surface === 'light';
  const body = `url(#${prefix}b)`;
  const accent = `url(#${prefix}a)`;
  const { x, y, s, r, turn, style } = model.key;
  const rotate = `transform="rotate(${turn} ${x + s / 2} ${y + s / 2})"`;
  const key = style === 'frame'
    ? `<rect x="${x}" y="${y}" width="${s}" height="${s}" rx="${r}" fill="none" stroke="${accent}" stroke-width="3" ${rotate}/>`
    : `<rect x="${x}" y="${y}" width="${s}" height="${s}" rx="${r}" fill="${style === 'body' ? body : accent}" ${rotate}/>`;
  const core = model.litCore ? `<path d="${LETTER}" fill="${accent}" transform="translate(27 32) scale(.66) translate(-27 -32)"/>` : '';
  return `<defs>${gradient(`${prefix}b`, 10, 4, 54, 60, light ? finish.bodyLight : finish.bodyDark)}${gradient(`${prefix}a`, 44, 0, 62, 20, light ? finish.accentLight : finish.accentDark)}`
    + `<mask id="${prefix}m" maskUnits="userSpaceOnUse" x="-8" y="-8" width="80" height="80"><rect x="-8" y="-8" width="80" height="80" fill="#fff"/>${model.sharpNotch ? SHARP_NOTCH : SOFT_NOTCH}</mask></defs>`
    + `<path d="${OUTER}${model.bigHole ? BIG_LETTER : LETTER}" fill-rule="evenodd" fill="${model.nobleBody ? accent : body}" mask="url(#${prefix}m)"/>${core}${key}`;
}

/** A complete SVG of the logo on its dark tile, for the browser tab. */
export function logoIconSvg(model: LogoModel, finish: LogoFinish): string {
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><defs><linearGradient id="t" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#2a2d34"/><stop offset="1" stop-color="#0d0e12"/></linearGradient></defs>`
    + `<path d="${TILE}" fill="url(#t)" stroke="#fff" stroke-opacity=".18" stroke-width=".8"/><g transform="translate(31 33) scale(.62) translate(-32 -32)">${logoMarkup(model, finish, 'dark', 'i')}</g></svg>`;
}
