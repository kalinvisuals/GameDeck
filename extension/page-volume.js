// Runs in the YouTube page's own world, where the player's API lives. The volume is set through the
// player and not on the <video> element, so YouTube keeps it for the next video instead of restoring
// its old level. The player's level is published on <html data-gamedeck-volume> for content.js.
(() => {
  const root = document.documentElement;
  const player = () => document.getElementById('movie_player');
  const hasApi = (p) => !!p && typeof p.getVolume === 'function' && typeof p.setVolume === 'function';

  const publish = () => {
    const p = player();
    if (!hasApi(p)) return;
    const muted = typeof p.isMuted === 'function' && p.isMuted();
    root.dataset.gamedeckVolume = String(muted ? 0 : Math.round(p.getVolume()));
  };

  document.addEventListener('gamedeck-set-volume', (event) => {
    const level = Number(event.detail);
    const p = player();
    if (!Number.isFinite(level) || !hasApi(p)) return;
    if (typeof p.unMute === 'function') p.unMute();
    p.setVolume(Math.min(100, Math.max(0, Math.round(level))));
    publish();
  });

  // Media events do not bubble, so they are caught on the way down.
  for (const name of ['volumechange', 'loadedmetadata', 'play']) document.addEventListener(name, publish, true);
  publish();
})();
