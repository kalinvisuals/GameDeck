/** Helpers for the Discord screen: song links, music services, times and addresses. */

export const YOUTUBE_ICON = 'https://www.gstatic.com/youtube/img/branding/favicon/favicon_144x144.png';
const favicon = (domain: string) => `https://t3.gstatic.com/faviconV2?client=SOCIAL&type=FAVICON&fallback_opts=TYPE,SIZE,URL&url=https://${domain}&size=128`;

/** Music services: the badge on the cover and the sites their song links come from. */
export const SOURCES = [
  { name: 'YouTube', icon: YOUTUBE_ICON, hosts: ['youtube.com', 'youtu.be'] },
  { name: 'Spotify', icon: favicon('open.spotify.com'), hosts: ['open.spotify.com', 'spotify.link', 'spotify.com'] },
  { name: 'SoundCloud', icon: favicon('soundcloud.com'), hosts: ['soundcloud.com'] },
  { name: 'Apple Music', icon: favicon('music.apple.com'), hosts: ['music.apple.com'] },
  { name: 'Deezer', icon: favicon('deezer.com'), hosts: ['deezer.com', 'deezer.page.link'] },
];
export type Source = (typeof SOURCES)[number];

export const sourceByName = (name: string): Source | undefined => SOURCES.find((s) => s.name.toLowerCase() === name.trim().toLowerCase());

/** The music service a link belongs to (www., m., music., on. and other subdomains included). */
export function sourceOfLink(link: string): Source | undefined {
  try {
    const host = new URL(link.trim()).hostname.toLowerCase();
    return SOURCES.find((s) => s.hosts.some((h) => host === h || host.endsWith('.' + h)));
  } catch {
    return undefined;
  }
}
/** "3:45" or "1:02:03" → seconds; a plain number is seconds. NaN when unreadable. */
export function parseClock(text: string): number {
  const v = text.trim();
  if (!v) return 0;
  if (!/^\d+(:\d{1,2}){0,2}$/.test(v)) return NaN;
  return v.split(':').map(Number).reduce((total, part) => total * 60 + part, 0);
}

export const clock = (seconds: number) => {
  const s = Math.max(0, Math.floor(seconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const rest = String(s % 60).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${rest}` : `${String(m).padStart(2, '0')}:${rest}`;
};

/** An address Discord accepts: absolute http(s). Empty is fine (the field is optional). */
export const badUrl = (v: string) => {
  if (!v.trim()) return false;
  try {
    const u = new URL(v.trim());
    return !(u.protocol === 'https:' || u.protocol === 'http:') || !u.hostname;
  } catch {
    return true;
  }
};

/** The video id of a YouTube link, for its cover. */
export function youtubeId(link: string): string | null {
  try {
    const u = new URL(link.trim());
    const host = u.hostname.replace(/^www\.|^m\.|^music\./, '');
    if (host === 'youtu.be') return /^[\w-]{6,20}$/.test(u.pathname.slice(1)) ? u.pathname.slice(1) : null;
    if (host !== 'youtube.com') return null;
    const id = u.searchParams.get('v') ?? u.pathname.match(/^\/(?:shorts|live|embed)\/([\w-]{6,20})/)?.[1] ?? null;
    return id && /^[\w-]{6,20}$/.test(id) ? id : null;
  } catch {
    return null;
  }
}

