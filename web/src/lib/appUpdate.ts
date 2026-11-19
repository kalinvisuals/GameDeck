const BUNDLE = /assets\/index-[\w-]+\.js/;
const RELOADED_FOR = 'gamedeck.reloadedFor';

/**
 * Phones and tablets keep the page open for days, so after GameDeck is updated they would go on
 * running the old interface. Checked each time the connection to the PC is made.
 */
export async function reloadIfUpdated(): Promise<void> {
  const running = document.querySelector<HTMLScriptElement>('script[src*="/assets/index-"]')?.getAttribute('src')?.match(BUNDLE)?.[0];
  if (!running) return; // development server
  try {
    const page = await (await fetch('/', { cache: 'no-store' })).text();
    const current = page.match(BUNDLE)?.[0];
    if (!current || current === running || sessionStorage.getItem(RELOADED_FOR) === current) return;
    sessionStorage.setItem(RELOADED_FOR, current); // one attempt per version, never a reload loop
    location.reload();
  } catch {
    // the next connection checks again
  }
}
