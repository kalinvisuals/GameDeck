// Works on the YouTube page the way a person would: types in the search box, presses search, reads
// the results on screen and clicks. It reacts to page events and has no polling loop.
(() => {
  // A tab in the background may not draw its results at all, so it is given less time.
  const SEARCH_TIMEOUT_MS = 6000;
  const HIDDEN_SEARCH_TIMEOUT_MS = 2500;
  const PENDING_VIDEO_KEY = 'gamedeck-pending-video';
  const PENDING_SEARCH_KEY = 'gamedeck-pending-search';
  const SEARCH_PRESS_MS = 900;
  const SEARCH_DATA_TIMEOUT_MS = 5000;
  const CHOSEN_ROW_TIMEOUT_MS = 4000;
  const HIDDEN_CHOSEN_ROW_TIMEOUT_MS = 1500;
  const SEARCH_SETTLE_MS = 1500;
  const LIST_DELAY_MS = 500;
  const OPEN_TIMEOUT_MS = 2000;
  const LIST_LIMIT = 30;
  const DURATION = /^\d{1,2}(?::\d{2}){1,2}$/;
  const SKIPPED_ROWS = 'ytd-ad-slot-renderer, ytd-in-feed-ad-layout-renderer, ytd-reel-shelf-renderer';
  const TITLES = '#video-title, h3, [class*="lockup-metadata-view-model__title" i], [class*="LockupMetadataViewModelTitle" i]';
  const CHANNELS = 'ytd-channel-name a, #channel-name a, a[href^="/@"], a[href^="/channel/"], ytd-channel-name #text, #channel-name #text, #byline';
  const METADATA_ROWS = '[class*="metadata-row" i], [class*="MetadataRow" i]';
  const DURATIONS = 'ytd-thumbnail-overlay-time-status-renderer #text, badge-shape, [class*="badge" i]';
  const RESULT_ROWS = 'ytd-search ytd-video-renderer, ytd-search yt-lockup-view-model';
  const RELATED_ROWS = ['#related', 'ytd-watch-next-secondary-results-renderer']
    .flatMap((list) => [`ytd-watch-flexy ${list} ytd-compact-video-renderer`, `ytd-watch-flexy ${list} yt-lockup-view-model`]).join(', ');
  const PLAYLIST_ROWS = 'ytd-playlist-panel-renderer:not([hidden]) ytd-playlist-panel-video-renderer';
  const LIST_CHANGES = { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['href', 'title'] };
  const LIST_AREAS = 'ytd-search, #related, ytd-watch-next-secondary-results-renderer, ytd-playlist-panel-renderer';
  const HOVER_PARTS = '#mouseover-overlay, #hover-overlays, ytd-moving-thumbnail-renderer';
  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
  const text = (element) => element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const videoIdOf = (href) => {
    try {
      const url = new URL(href, location.origin);
      return url.pathname === '/watch' ? url.searchParams.get('v') : null;
    } catch {
      return null;
    }
  };

  let port = chrome.runtime.connect({ name: 'youtube' });
  let lastTrack = '';
  let timer = 0;
  let searchToken = 0;
  let searchRunning = false;
  let openToken = 0;
  let listTimer = 0;
  let watchedList = null;
  let sentResults = '';
  let sentUpNext = '';
  let waitingForPage = false;
  let lastSearch = null;
  let lastWatched = null;
  let shownQuery = '';
  let shownCount = 0;

  const send = (message) => {
    try {
      port?.postMessage(message);
    } catch {
      port = null;
    }
  };
  const video = () => $('#movie_player video') ?? $('video.html5-main-video');
  const player = () => $('#movie_player');

  function readTrack() {
    const media = video();
    if (!media) return null;
    const id = videoIdOf(location.href);
    // YouTube can keep playing after leaving the video page, and the controls must stay available.
    const stillPlaying = !media.paused && !media.ended && !!media.currentSrc;
    if (!id && !(stillPlaying && lastWatched)) return null;
    // YouTube's own level (page-volume.js); the element's volume also carries loudness normalisation.
    const playerVolume = document.documentElement.dataset.gamedeckVolume;
    const live = {
      duration: Number.isFinite(media.duration) ? media.duration : 0,
      position: media.currentTime || 0,
      paused: media.paused,
      volume: playerVolume !== undefined ? Number(playerVolume) : Math.round((media.muted ? 0 : media.volume) * 100),
      isAd: player()?.classList.contains('ad-showing') ?? false,
    };
    if (!id) return { ...lastWatched, ...live };
    const session = navigator.mediaSession?.metadata;
    // After a switch the page can still show the previous video's details for a moment.
    const pageIsCurrent = $('ytd-watch-flexy')?.getAttribute('video-id') === id;
    const tabTitle = document.title.replace(/^\(\d+\+?\)\s*/, '').replace(/ - YouTube$/, '');
    lastWatched = {
      id,
      title: (pageIsCurrent && text($('ytd-watch-metadata h1'))) || tabTitle || session?.title || '',
      channel: (pageIsCurrent && text($('ytd-watch-metadata ytd-channel-name a'))) || session?.artist || '',
    };
    return { ...lastWatched, ...live };
  }

  function reportTrack(force = false) {
    clearTimeout(timer);
    timer = setTimeout(() => {
      const track = readTrack();
      // The position is left out here: the dashboard advances it by itself between real changes.
      const key = JSON.stringify(track && { ...track, position: Math.round(track.position / 5) });
      if (!force && key === lastTrack) return;
      lastTrack = key;
      send({ type: 'track', track });
    }, 150);
  }

  function setInputValue(input, value) {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, value);
    input.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
    input.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
  }

  const onResultsFor = (query) => location.pathname === '/results' && sameText(pageQuery(), query);

  function clickSearchButton(input) {
    const button = $('button.ytSearchboxComponentSearchButton')
      ?? $('#search-icon-legacy')
      ?? input.closest('form')?.querySelector('button[type="submit"], button[aria-label]');
    button?.click();
  }

  function pressEnter(input) {
    for (const type of ['keydown', 'keypress', 'keyup']) {
      input.dispatchEvent(new KeyboardEvent(type, { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, composed: true, cancelable: true }));
    }
  }

  // True once YouTube has moved to the results of this search.
  async function pressSearch(input, query, token) {
    for (const press of [clickSearchButton, pressEnter]) {
      press(input);
      const deadline = performance.now() + SEARCH_PRESS_MS;
      while (performance.now() < deadline) {
        await sleep(50);
        if (token !== searchToken || onResultsFor(query)) return true;
      }
    }
    return false;
  }

  const usable = (value) => {
    const clean = value?.replace(/\s+/g, ' ').trim() ?? '';
    return DURATION.test(clean) ? '' : clean;
  };
  const firstOf = (elements, read) => {
    for (const element of elements) {
      const value = usable(read(element));
      if (value) return value;
    }
    return '';
  };

  // The first link of a row is often the thumbnail, whose only text is the duration.
  function readRow(row, inPlaylist) {
    const href = $('a[href*="/watch?v="]', row)?.getAttribute('href');
    const id = href && videoIdOf(href);
    if (!id || (!inPlaylist && new URL(href, location.origin).searchParams.has('list'))) return null;
    const titles = $$(TITLES, row);
    const title = firstOf(titles, (e) => e.getAttribute('title')) || firstOf(titles, (e) => e.textContent) || firstOf(titles, (e) => e.getAttribute('aria-label'));
    if (!title) return null; // still rendering: the list is read again when it appears
    const channel = firstOf($$(CHANNELS, row), (e) => e.textContent) || firstOf($$(METADATA_ROWS, row), (e) => e.textContent.split('•')[0]);
    const duration = $$(DURATIONS, row).map(text).find((value) => DURATION.test(value)) ?? '';
    return { id, title, channel, duration };
  }

  function readRows(rows, { inPlaylist = false, except = null } = {}) {
    const seen = new Set();
    const items = [];
    for (const row of rows) {
      if (row.closest(SKIPPED_ROWS)) continue;
      const item = readRow(row, inPlaylist);
      if (!item || item.id === except || seen.has(item.id)) continue;
      seen.add(item.id);
      items.push(item);
      if (items.length === LIST_LIMIT) break;
    }
    return items;
  }

  const readResults = () => readRows($$(RESULT_ROWS));
  const idsOf = (items) => items.map((item) => item.id).join();
  const pageQuery = () => new URL(location.href).searchParams.get('search_query') ?? '';
  const sameText = (a, b) => a.replace(/\s+/g, ' ').trim().toLowerCase() === b.replace(/\s+/g, ' ').trim().toLowerCase();
  const resultsKey = (query, items) => JSON.stringify([query, items]);
  const noResultsShown = () => $$('ytd-search ytd-background-promo-renderer').some((e) => e.getClientRects().length > 0);

  // While YouTube swaps its results, a few rows of the previous search remain. Those are not the answer.
  function waitForResults(query, token, previousIds) {
    const previous = new Set(previousIds.split(','));
    return new Promise((resolve) => {
      const onPage = () => sameText(pageQuery(), query);
      const fresh = () => {
        if (!onPage()) return false;
        const items = readResults();
        return items.length > 0 && !items.some((item) => previous.has(item.id));
      };
      let observer = null;
      let settle = 0;
      const finish = (found) => {
        observer?.disconnect();
        clearTimeout(limit);
        clearTimeout(settle);
        document.removeEventListener('yt-navigate-finish', navigated);
        if (token === searchToken) waitingForPage = false;
        resolve(found);
      };
      const check = () => {
        if (token !== searchToken) finish(false);
        else if (fresh()) finish(true);
      };
      function navigated() {
        if (settle || !onPage()) return;
        settle = setTimeout(() => {
          if (!onPage()) return;
          if (noResultsShown()) finish(false);
          else if (readResults().length > 0) finish(true); // the same videos really are the answer
        }, SEARCH_SETTLE_MS);
      }
      const limit = setTimeout(() => finish(fresh()), document.hidden ? HIDDEN_SEARCH_TIMEOUT_MS : SEARCH_TIMEOUT_MS);
      observer = new MutationObserver(check);
      observer.observe(document.documentElement, { childList: true, subtree: true });
      document.addEventListener('yt-navigate-finish', navigated);
      check();
    });
  }

  const runsText = (value) => value?.simpleText ?? value?.runs?.map((run) => run.text).join('') ?? '';

  function findDuration(node) {
    if (typeof node === 'string') return DURATION.test(node) ? node : '';
    if (!node || typeof node !== 'object') return '';
    for (const value of Object.values(node)) {
      const found = findDuration(value);
      if (found) return found;
    }
    return '';
  }

  function videoFromData(key, value) {
    if (key === 'videoRenderer' && value.videoId) {
      return {
        id: value.videoId,
        title: runsText(value.title),
        channel: runsText(value.ownerText) || runsText(value.longBylineText),
        duration: runsText(value.lengthText),
      };
    }
    if (key === 'lockupViewModel' && value.contentType === 'LOCKUP_CONTENT_TYPE_VIDEO' && value.contentId) {
      const metadata = value.metadata?.lockupMetadataViewModel;
      return {
        id: value.contentId,
        title: metadata?.title?.content ?? '',
        channel: metadata?.metadata?.contentMetadataViewModel?.metadataRows?.[0]?.metadataParts?.[0]?.text?.content ?? '',
        duration: findDuration(value.contentImage),
      };
    }
    return null;
  }

  function collectVideos(node, items, seen) {
    if (!node || typeof node !== 'object' || items.length === LIST_LIMIT) return;
    for (const [key, value] of Object.entries(node)) {
      if (key === 'adSlotRenderer' || key === 'promotedVideoRenderer' || key === 'reelShelfRenderer') continue;
      const item = value && typeof value === 'object' ? videoFromData(key, value) : null;
      if (item?.title && !seen.has(item.id) && items.length < LIST_LIMIT) {
        seen.add(item.id);
        items.push(item);
      } else if (!item) collectVideos(value, items, seen);
    }
  }

  // The data is followed by more script, so the object is measured instead of searched for an ending.
  function jsonObjectAt(html, start) {
    let depth = 0;
    let inString = false;
    for (let i = start; i < html.length; i++) {
      const c = html[i];
      if (inString) {
        if (c === '\\') i++;
        else if (c === '"') inString = false;
      } else if (c === '"') inString = true;
      else if (c === '{') depth++;
      else if (c === '}' && --depth === 0) return html.slice(start, i + 1);
    }
    return null;
  }

  // Reads the same results page as data, for when the tab has not drawn it.
  async function fetchResults(query) {
    try {
      const response = await fetch(`/results?search_query=${encodeURIComponent(query)}`, { signal: AbortSignal.timeout(SEARCH_DATA_TIMEOUT_MS) });
      if (!response.ok) return { items: [], how: `data-http-${response.status}` };
      const html = await response.text();
      const match = /(?:var\s+ytInitialData|window\[["']ytInitialData["']\])\s*=\s*\{/.exec(html);
      if (!match) return { items: [], how: 'data-missing' };
      const json = jsonObjectAt(html, match.index + match[0].length - 1);
      if (!json) return { items: [], how: 'data-cut' };
      const items = [];
      collectVideos(JSON.parse(json).contents, items, new Set());
      return { items, how: items.length > 0 ? 'data' : 'data-empty' };
    } catch (error) {
      return { items: [], how: error instanceof SyntaxError ? 'data-invalid' : 'data-failed' };
    }
  }

  function readUpNext() {
    const current = videoIdOf(location.href);
    if (new URL(location.href).searchParams.has('list')) {
      const rows = $$(PLAYLIST_ROWS);
      if (rows.length > 0) {
        // Only the rows after the current song are read, however long the playlist is.
        const isCurrent = (row) => videoIdOf($('a[href*="/watch?v="]', row)?.getAttribute('href') ?? '') === current;
        return { kind: 'playlist', items: readRows(rows.slice(rows.findIndex(isCurrent) + 1), { inPlaylist: true }) };
      }
    }
    return { kind: 'related', items: readRows($$(RELATED_ROWS), { except: current }) };
  }

  function reportResults() {
    if (searchRunning || waitingForPage) return;
    const query = pageQuery();
    const items = readResults();
    const key = resultsKey(query, items);
    if (items.length === 0 || key === sentResults) return;
    // The page draws its rows gradually and must not shrink a list that is already complete.
    if (sameText(query, shownQuery) && items.length < shownCount) return;
    sentResults = key;
    shownQuery = query;
    shownCount = items.length;
    send({ type: 'results', requestId: 0, query, items });
  }

  function reportUpNext() {
    const list = readUpNext();
    const key = JSON.stringify(list);
    if (key === sentUpNext) return;
    sentUpNext = key;
    send({ type: 'upNext', ...list });
  }

  function scheduleLists() {
    if (!listTimer) listTimer = setTimeout(flushLists, LIST_DELAY_MS);
  }

  function flushLists() {
    listTimer = 0;
    if (watchedList && !watchedList.isConnected) watchLists();
    else if (location.pathname === '/results') reportResults();
    else if (location.pathname === '/watch') reportUpNext();
  }

  const listRoot = () => (location.pathname === '/results' ? $('ytd-search') : location.pathname === '/watch' ? $('ytd-watch-flexy') : null);
  const changesList = (node) => {
    const element = node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement;
    return !!element?.closest(LIST_AREAS) && !element.closest(HOVER_PARTS);
  };
  const listObserver = new MutationObserver((records) => {
    if (!watchedList) {
      if (listRoot()) watchLists();
    } else if (records.some((record) => changesList(record.target))) scheduleLists();
  });

  // Only the part of the page holding a list is watched, and only on pages that have one.
  function watchLists() {
    const root = listRoot();
    if (!root || root !== watchedList) {
      listObserver.disconnect();
      watchedList = root;
      if (root) listObserver.observe(root, LIST_CHANGES);
      else if (location.pathname === '/results' || location.pathname === '/watch') listObserver.observe(document.documentElement, { childList: true, subtree: true });
    }
    scheduleLists();
  }

  // Browsers refuse to start sound in a tab the person has not touched yet. One click there is enough
  // for as long as the page stays open, so we ask for exactly that and then continue by ourselves.
  let clickPrompt = null;
  const CLICK_ONCE = {
    en: 'GameDeck: click here once to start the sound',
    ro: 'GameDeck: apasă aici o dată ca să pornească sunetul',
    es: 'GameDeck: haz clic aquí una vez para que empiece el sonido',
    pt: 'GameDeck: clique aqui uma vez para o som começar',
    de: 'GameDeck: einmal hier klicken, damit der Ton startet',
    fr: 'GameDeck : clique ici une fois pour lancer le son',
    pl: 'GameDeck: kliknij tu raz, aby włączyć dźwięk',
    ru: 'GameDeck: щёлкни здесь один раз, чтобы пошёл звук',
  };

  function hideClickPrompt() {
    clickPrompt?.remove();
    clickPrompt = null;
    document.removeEventListener('pointerdown', onFirstClick, true);
  }

  function onFirstClick() {
    hideClickPrompt();
    send({ type: 'notice', code: '' });
    setTimeout(() => video()?.play().catch(() => {}), 0);
  }

  function askForClick() {
    send({ type: 'notice', code: 'needsClick' });
    if (clickPrompt) return;
    const lang = (document.documentElement.lang || navigator.language).slice(0, 2).toLowerCase();
    clickPrompt = document.createElement('div');
    const shadow = clickPrompt.attachShadow({ mode: 'closed' });
    const box = document.createElement('button');
    box.type = 'button';
    box.textContent = CLICK_ONCE[lang] ?? CLICK_ONCE.en;
    box.style.cssText = 'position:fixed;left:50%;bottom:32px;z-index:2147483647;transform:translateX(-50%);padding:14px 22px;border:1px solid #5ea2ff;border-radius:999px;background:#0b1220;color:#fff;font:600 15px system-ui,sans-serif;cursor:pointer;box-shadow:0 16px 40px #0009';
    shadow.append(box);
    document.documentElement.append(clickPrompt);
    document.addEventListener('pointerdown', onFirstClick, true);
  }

  function startPlayback() {
    const media = video();
    if (!media) return Promise.resolve();
    return media.play().catch((error) => {
      if (error?.name === 'NotAllowedError') askForClick();
    });
  }

  async function ensurePlaying(id) {
    for (const wait of [1500, 2500]) {
      await sleep(wait);
      if (videoIdOf(location.href) !== id) return;
      const media = video();
      if (media && !media.paused) return;
      if (media && media.readyState > 0) {
        await startPlayback();
        if (clickPrompt) return;
      }
    }
  }

  const findVideoLink = (id) => {
    const links = $$('a[href*="/watch?v="]').filter((a) => videoIdOf(a.getAttribute('href')) === id && !a.closest(SKIPPED_ROWS));
    // Results stay in the page while a video plays, so a hidden row still opens without a reload.
    return links.find((a) => a.getClientRects().length > 0) ?? links[0];
  };

  function waitForVideoLink(id) {
    return new Promise((resolve) => {
      const finish = (link) => {
        observer.disconnect();
        clearTimeout(limit);
        resolve(link);
      };
      const observer = new MutationObserver(() => {
        const link = findVideoLink(id);
        if (link) finish(link);
      });
      const limit = setTimeout(() => finish(findVideoLink(id)), document.hidden ? HIDDEN_CHOSEN_ROW_TIMEOUT_MS : CHOSEN_ROW_TIMEOUT_MS);
      observer.observe(document.documentElement, { childList: true, subtree: true });
    });
  }

  function loadVideoPage(id) {
    sessionStorage.setItem(PENDING_VIDEO_KEY, id); // playback is checked again after the reload
    location.assign(`/watch?v=${encodeURIComponent(id)}`);
  }

  function clickVideoLink(link, id, token) {
    const startedFrom = location.href;
    link.click();
    ensurePlaying(id);
    setTimeout(() => {
      if (token === openToken && location.href === startedFrom) loadVideoPage(id);
    }, OPEN_TIMEOUT_MS);
  }

  // The chosen song is reached like a person would: search bar, search, click on its row.
  async function openThroughSearch(id, query, token) {
    const input = $('input[name="search_query"]');
    if (!input) {
      loadVideoPage(id);
      return;
    }
    const search = ++searchToken;
    searchRunning = false;
    waitingForPage = true;
    input.focus();
    setInputValue(input, query);
    await sleep(60);
    const moved = search === searchToken && await pressSearch(input, query, search);
    const link = moved && search === searchToken ? await waitForVideoLink(id) : null;
    if (search === searchToken) waitingForPage = false;
    if (token !== openToken) return;
    if (link) clickVideoLink(link, id, token);
    else loadVideoPage(id);
  }

  function openVideo(id) {
    const token = ++openToken;
    if (videoIdOf(location.href) === id) {
      startPlayback();
      return;
    }
    const link = findVideoLink(id);
    if (link) clickVideoLink(link, id, token);
    else if (lastSearch?.ids.has(id)) openThroughSearch(id, lastSearch.query, token);
    else loadVideoPage(id);
  }

  const videoIsOpen = () => {
    const media = video();
    return !!media && !media.ended && !!readTrack();
  };

  function publishResults(requestId, query, items, how) {
    searchRunning = false;
    shownQuery = query;
    shownCount = items.length;
    sentResults = resultsKey(pageQuery(), items);
    lastSearch = { query, ids: new Set(items.map((item) => item.id)) };
    send({ type: 'results', requestId, query, items, how });
  }

  async function search({ requestId, query, autoplay }) {
    const token = ++searchToken;
    searchRunning = true;
    waitingForPage = false;
    shownCount = 0;
    const data = fetchResults(query); // starts at once, so the list does not wait for the page to draw
    // With a song open the tab stays on it, so playback and its controls are not interrupted.
    if (!autoplay && videoIsOpen()) {
      const { items, how } = await data;
      if (token !== searchToken) return;
      if (items.length > 0 || how === 'data-empty') {
        publishResults(requestId, query, items, how);
        return;
      }
    }
    const input = $('input[name="search_query"]');
    if (!input) {
      searchRunning = false;
      send({ type: 'results', requestId, query, items: [], error: 'searchBoxMissing' });
      return;
    }
    const previousIds = sameText(pageQuery(), query) ? '' : idsOf(readResults());
    waitingForPage = true;
    input.focus();
    setInputValue(input, query);
    await sleep(60);
    if (token !== searchToken) return;
    const answer = waitForResults(query, token, previousIds); // listens before the page can respond
    finishSearch({ requestId, query, autoplay }, token, answer, data);
    if (await pressSearch(input, query, token) || token !== searchToken) return;
    // The page ignored the search button, so its results page is opened directly.
    if (searchRunning) sessionStorage.setItem(PENDING_SEARCH_KEY, JSON.stringify({ requestId, query, autoplay }));
    location.assign(`/results?search_query=${encodeURIComponent(query)}`);
  }

  async function finishSearch({ requestId, query, autoplay }, token, answer, data) {
    // The data is the whole list at once. The page is used when it is missing or to click a song.
    const fromData = await data;
    const hasData = fromData.items.length > 0;
    const onPage = (autoplay || !hasData) && await answer;
    if (token !== searchToken) return;
    const pageItems = onPage ? readResults() : [];
    const items = hasData ? fromData.items : pageItems;
    const how = hasData || pageItems.length === 0 ? fromData.how : 'page';
    publishResults(requestId, query, items, how);
    if (autoplay && items.length > 0) {
      searchToken++; // the search is over, nothing may pull the tab back to the results
      waitingForPage = false;
      openVideo(pageItems[0]?.id ?? items[0].id);
    }
    watchLists();
  }

  function resumeSearch() {
    const saved = sessionStorage.getItem(PENDING_SEARCH_KEY);
    sessionStorage.removeItem(PENDING_SEARCH_KEY);
    if (!saved) return;
    const pending = JSON.parse(saved);
    if (!onResultsFor(pending.query)) return;
    const token = ++searchToken;
    searchRunning = true;
    waitingForPage = true;
    finishSearch(pending, token, waitForResults(pending.query, token, ''), fetchResults(pending.query));
  }

  function readPlaylists() {
    const seen = new Set();
    const items = [];
    for (const link of $$('ytd-guide-entry-renderer a[href*="playlist?list="]')) {
      const id = new URL(link.href).searchParams.get('list');
      const title = link.getAttribute('title') || text($('.title', link)) || text(link);
      if (id && title && !seen.has(id)) {
        seen.add(id);
        items.push({ id, title });
      }
    }
    send({ type: 'playlists', items });
  }

  function control({ action, value }) {
    const media = video();
    if (action === 'next') $('.ytp-next-button')?.click();
    else if (action === 'previous') {
      const button = $('.ytp-prev-button');
      if (button && button.getClientRects().length > 0 && button.getAttribute('aria-disabled') !== 'true') button.click();
      else history.back();
    } else if (!media) {
      send({ type: 'notice', code: 'noVideo' });
    } else if (action === 'play') startPlayback();
    else if (action === 'pause') media.pause();
    else if (action === 'toggle') {
      if (media.paused) startPlayback();
      else media.pause();
    } else if (action === 'volume') {
      // Through YouTube's player (page-volume.js), so the level survives the next video.
      const level = Math.min(100, Math.max(0, Math.round(value)));
      document.dispatchEvent(new CustomEvent('gamedeck-set-volume', { detail: level }));
      if (document.documentElement.dataset.gamedeckVolume !== String(level)) {
        // the player's API is not ready yet: set the element directly as before
        media.muted = false;
        media.volume = level / 100;
      }
    } else if (action === 'seek' && Number.isFinite(media.duration)) {
      media.currentTime = Math.min(media.duration, Math.max(0, value));
    }
  }

  port.onMessage.addListener((message) => {
    if (message.type === 'sync') {
      sentResults = '';
      sentUpNext = '';
      reportTrack(true);
      readPlaylists();
      watchLists();
    } else if (message.type === 'search') search(message);
    else if (message.type === 'openVideo') openVideo(message.id);
    else if (message.type === 'openPlaylist') location.assign(`/playlist?list=${encodeURIComponent(message.id)}&playnext=1`);
    else if (message.type === 'refreshPlaylists') readPlaylists();
    else if (message.type === 'control') control(message);
  });
  port.onDisconnect.addListener(() => { port = null; });

  // Media events do not bubble, so they are caught on the way down.
  for (const name of ['play', 'pause', 'seeked', 'volumechange', 'durationchange', 'loadedmetadata']) {
    document.addEventListener(name, (event) => {
      if (event.target !== video()) return;
      if (name === 'play') {
        send({ type: 'claim' });
        hideClickPrompt();
        send({ type: 'notice', code: '' });
      }
      reportTrack(name === 'seeked');
    }, true);
  }
  document.addEventListener('ended', (event) => {
    if (event.target === video() && !player()?.classList.contains('ad-showing')) send({ type: 'ended' });
  }, true);
  document.addEventListener('yt-navigate-finish', () => {
    reportTrack(true);
    readPlaylists();
    watchLists();
  });
  // YouTube renames the tab once the new video is known, which is when its name can be read.
  const titleElement = $('title');
  if (titleElement) new MutationObserver(() => {
    reportTrack(true);
    scheduleLists();
  }).observe(titleElement, { childList: true });
  reportTrack(true);
  watchLists();
  const pendingVideo = sessionStorage.getItem(PENDING_VIDEO_KEY);
  sessionStorage.removeItem(PENDING_VIDEO_KEY);
  if (pendingVideo && pendingVideo === videoIdOf(location.href)) ensurePlaying(pendingVideo);
  resumeSearch();
})();
