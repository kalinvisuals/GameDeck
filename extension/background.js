// Connects the open YouTube tab to GameDeck on this PC and feeds the toolbar panel. It only runs while
// a YouTube tab or the panel is open: otherwise there is no connection, no timer and no retry.
const SERVER = 'ws://localhost:5280/ext/youtube';
const DASHBOARD = 'http://localhost:5280/';
const KEEP_ALIVE_MS = 20000; // the browser ends idle service workers after 30 s
const RETRY_MS = 5000;

const ports = new Set();
const panels = new Set();
let activePort = null;
let socket = null;
let serverOnline = false;
let language = 'en';
const LANGUAGES = ['en', 'ro', 'es', 'pt', 'de', 'fr', 'pl', 'ru'];
let track = null;
let notice = null;
let query = '';
let searching = false;
let searchError = '';
let results = [];
let upNext = [];
let upNextKind = 'related';
let badge = '';
let keepAlive = 0;
let retry = 0;

const wanted = () => ports.size > 0 || panels.size > 0;

function status() {
  return { type: 'status', server: serverOnline, tabs: ports.size, track, notice, language, query, searching, searchError, results, upNext, upNextKind, dashboard: DASHBOARD };
}

function publish() {
  const message = status();
  panels.forEach((panel) => panel.postMessage(message));
  const ready = serverOnline && ports.size > 0;
  const text = ready ? ' ' : ports.size > 0 ? '!' : '';
  if (text === badge) return;
  badge = text;
  chrome.action.setBadgeText({ text });
  chrome.action.setBadgeBackgroundColor({ color: ready ? '#2fd07f' : '#f0a63c' });
}

function sendToServer(message) {
  if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
}

function startSearch(text) {
  query = text;
  searching = true;
  searchError = '';
  results = [];
}

function setActive(port) {
  activePort = port;
  track = null;
  notice = null;
  searching = false;
  searchError = '';
  results = [];
  upNext = [];
  port?.postMessage({ type: 'sync' });
  publish();
}

function connect() {
  clearTimeout(retry);
  if (socket || !wanted()) return;
  const ws = new WebSocket(SERVER);
  socket = ws;
  ws.onopen = () => {
    serverOnline = true;
    sendToServer({ type: 'hello', version: chrome.runtime.getManifest().version });
    activePort?.postMessage({ type: 'sync' });
    keepAlive = setInterval(() => sendToServer({ type: 'ping' }), KEEP_ALIVE_MS);
    publish();
  };
  ws.onmessage = (event) => {
    const message = JSON.parse(event.data);
    if (message.type === 'config') {
      language = LANGUAGES.includes(message.language) ? message.language : 'en';
      publish();
      return;
    }
    if (message.type === 'search') {
      startSearch(message.query);
      publish();
    }
    try {
      activePort?.postMessage(message);
    } catch {
      // the tab was closed while the message was on its way
    }
  };
  ws.onclose = () => {
    clearInterval(keepAlive);
    if (socket === ws) socket = null;
    serverOnline = false;
    publish();
    if (wanted()) retry = setTimeout(connect, RETRY_MS);
  };
}

function disconnectIfUnused() {
  if (wanted()) return;
  clearTimeout(retry);
  socket?.close();
}

function connectTab(port) {
  ports.add(port);
  if (!activePort) activePort = port;
  port.onMessage.addListener((message) => {
    // With several YouTube tabs, the one that started playing is the one being controlled.
    if (message.type === 'claim') {
      if (port !== activePort) setActive(port);
      return;
    }
    if (port !== activePort) return;
    if (message.type === 'track') {
      track = message.track;
      publish();
    } else if (message.type === 'notice') {
      notice = message.code || null;
      // The tab comes to the front inside the browser, without pulling the browser over a game.
      if (notice === 'needsClick' && port.sender?.tab?.id) chrome.tabs.update(port.sender.tab.id, { active: true });
      publish();
    } else if (message.type === 'results' && !(message.requestId === 0 && searching)) {
      query = message.query;
      results = message.items;
      searching = false;
      searchError = results.length > 0 ? '' : message.error || 'notFound';
      publish();
    } else if (message.type === 'upNext') {
      upNext = message.items;
      upNextKind = message.kind;
      publish();
    }
    sendToServer(message);
  });
  port.onDisconnect.addListener(() => {
    ports.delete(port);
    if (activePort === port) setActive([...ports].pop() ?? null);
    else publish();
    disconnectIfUnused();
  });
  if (socket?.readyState === WebSocket.OPEN && port === activePort) port.postMessage({ type: 'sync' });
  connect();
  publish();
}

function focusActiveTab() {
  const tab = activePort?.sender?.tab;
  if (!tab?.id) return;
  chrome.tabs.update(tab.id, { active: true });
  chrome.windows.update(tab.windowId, { focused: true });
}

function connectPanel(panel) {
  panels.add(panel);
  panel.postMessage(status());
  connect();
  panel.onMessage.addListener((message) => {
    if (message.type === 'control') activePort?.postMessage(message);
    else if (message.type === 'focusTab') focusActiveTab();
    else if (message.type === 'openDashboard') chrome.tabs.create({ url: DASHBOARD });
    else if (message.type === 'search') {
      const text = String(message.query ?? '').slice(0, 200);
      startSearch(text);
      if (activePort) activePort.postMessage({ type: 'search', requestId: -1, query: text, autoplay: false });
      else chrome.tabs.create({ url: `https://www.youtube.com/results?search_query=${encodeURIComponent(text)}` });
      publish();
    } else if (message.type === 'openVideo') activePort?.postMessage({ type: 'openVideo', id: String(message.id ?? '') });
    else if (message.type === 'openYouTube') chrome.tabs.create({ url: 'https://www.youtube.com/' });
  });
  panel.onDisconnect.addListener(() => {
    panels.delete(panel);
    disconnectIfUnused();
  });
}

chrome.runtime.onConnect.addListener((port) => {
  if (port.name === 'youtube') connectTab(port);
  else if (port.name === 'panel') connectPanel(port);
});
