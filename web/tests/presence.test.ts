import { describe, expect, it } from 'vitest';
import { badUrl, parseClock, sourceByName, sourceOfLink, youtubeId } from '../src/lib/presence';

describe('Discord song links', () => {
  it('recognises the music service of a link', () => {
    expect(sourceOfLink('https://www.youtube.com/watch?v=dQw4w9WgXcQ')?.name).toBe('YouTube');
    expect(sourceOfLink('https://youtu.be/dQw4w9WgXcQ')?.name).toBe('YouTube');
    expect(sourceOfLink('https://music.youtube.com/watch?v=dQw4w9WgXcQ')?.name).toBe('YouTube');
    expect(sourceOfLink('https://open.spotify.com/intl-ro/track/4cOdK2wGLETKBW3PvgPWqT')?.name).toBe('Spotify');
    expect(sourceOfLink('https://spotify.link/abc')?.name).toBe('Spotify');
    expect(sourceOfLink('https://on.soundcloud.com/abc')?.name).toBe('SoundCloud');
    expect(sourceOfLink('https://music.apple.com/ro/album/x/123?i=456')?.name).toBe('Apple Music');
    expect(sourceOfLink('https://www.deezer.com/en/track/3135556')?.name).toBe('Deezer');
    expect(sourceOfLink('https://example.com/song')).toBeUndefined();
    // A look-alike address is not the service.
    expect(sourceOfLink('https://notyoutube.com/watch?v=dQw4w9WgXcQ')).toBeUndefined();
    expect(sourceOfLink('https://youtube.com.evil.example/watch')).toBeUndefined();
  });

  it('finds services by name regardless of case', () => {
    expect(sourceByName(' spotify ')?.name).toBe('Spotify');
    expect(sourceByName('Tidal')).toBeUndefined();
  });

  it('takes the video id from YouTube links for the cover', () => {
    expect(youtubeId('https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=10')).toBe('dQw4w9WgXcQ');
    expect(youtubeId('https://youtu.be/dQw4w9WgXcQ')).toBe('dQw4w9WgXcQ');
    expect(youtubeId('https://www.youtube.com/shorts/abcdefghijk')).toBe('abcdefghijk');
    expect(youtubeId('https://open.spotify.com/track/x')).toBeNull();
  });

  it('reads song lengths and rejects other addresses', () => {
    expect(parseClock('3:33')).toBe(213);
    expect(parseClock('1:02:03')).toBe(3723);
    expect(parseClock('')).toBe(0);
    expect(parseClock('3m')).toBeNaN();
    expect(badUrl('https://a.example/x.png')).toBe(false);
    expect(badUrl('C:\Users\me\cover.png')).toBe(true);
    expect(badUrl('javascript:alert(1)')).toBe(true);
    expect(badUrl('')).toBe(false);
  });
});
