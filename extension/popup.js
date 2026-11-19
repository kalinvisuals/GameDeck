// The toolbar panel: live status, the current video and a quick search.
const TEXT = {
  ro: {
    ready: 'Conectat', partial: 'Incomplet', off: 'Inactiv', online: 'Pornit', offline: 'Nu este pornit',
    tabOne: 'Filă conectată', tabMany: '{n} file deschise', noTab: 'Nicio filă deschisă',
    playing: 'Rulează acum', paused: 'În pauză', ad: 'Reclamă',
    noVideoTitle: 'Totul este pregătit', noVideoText: 'Caută ceva mai jos sau pornește un videoclip pe YouTube.',
    noTabTitle: 'Deschide YouTube', noTabText: 'Scrie o melodie mai jos și vezi rezultatele într-o filă nouă.',
    noDeckTitle: 'Pornește GameDeck', noDeckText: 'Poți controla YouTube de aici, dar aplicația nu este conectată.',
    needsClick: 'Apasă o dată în fila YouTube ca să pornească sunetul. Deschide fila',
    search: 'Caută o melodie', go: 'Caută', upNext: 'Urmează', fromPlaylist: 'Din playlist', results: 'Rezultate', searching: 'Caut „{q}”…', notFound: 'Nu am găsit rezultate pentru „{q}”.', searchFailed: 'Nu găsesc bara de căutare. Reîncarcă fila YouTube.', listEmpty: 'Lista apare imediat ce se încarcă pagina.', openYouTube: 'Deschide YouTube', showTab: 'Mergi la filă', openDeck: 'Deschide GameDeck',
    previous: 'Precedentul', next: 'Următorul', play: 'Pornește', pause: 'Pauză', seek: 'Poziția în videoclip', volume: 'Volum', cover: 'Mergi la fila YouTube',
  },
  en: {
    ready: 'Connected', partial: 'Partial', off: 'Idle', online: 'Running', offline: 'Not running',
    tabOne: 'Tab connected', tabMany: '{n} tabs open', noTab: 'No tab open',
    playing: 'Now playing', paused: 'Paused', ad: 'Ad',
    noVideoTitle: 'All set', noVideoText: 'Search below or start a video on YouTube.',
    noTabTitle: 'Open YouTube', noTabText: 'Type a song below to see the results in a new tab.',
    noDeckTitle: 'Start GameDeck', noDeckText: 'You can control YouTube here, but the app is not connected.',
    needsClick: 'Click once in the YouTube tab to start the sound. Open the tab',
    search: 'Search for a song', go: 'Search', upNext: 'Up next', fromPlaylist: 'From playlist', results: 'Results', searching: 'Searching for “{q}”…', notFound: 'No results for “{q}”.', searchFailed: 'The search box was not found. Reload the YouTube tab.', listEmpty: 'The list appears as soon as the page loads.', openYouTube: 'Open YouTube', showTab: 'Go to tab', openDeck: 'Open GameDeck',
    previous: 'Previous', next: 'Next', play: 'Play', pause: 'Pause', seek: 'Position in the video', volume: 'Volume', cover: 'Go to the YouTube tab',
  },
  es: {
    ready: 'Conectado', partial: 'Incompleto', off: 'Inactivo', online: 'Abierto', offline: 'No está abierto',
    tabOne: 'Pestaña conectada', tabMany: '{n} pestañas abiertas', noTab: 'Ninguna pestaña abierta',
    playing: 'Sonando ahora', paused: 'En pausa', ad: 'Anuncio',
    noVideoTitle: 'Todo listo', noVideoText: 'Busca algo abajo o pon un vídeo en YouTube.',
    noTabTitle: 'Abre YouTube', noTabText: 'Escribe una canción abajo para ver los resultados en una pestaña nueva.',
    noDeckTitle: 'Abre GameDeck', noDeckText: 'Puedes controlar YouTube desde aquí, pero la aplicación no está conectada.',
    needsClick: 'Haz clic una vez en la pestaña de YouTube para que empiece el sonido. Abrir la pestaña',
    search: 'Buscar una canción', go: 'Buscar', upNext: 'A continuación', fromPlaylist: 'De la lista', results: 'Resultados', searching: 'Buscando «{q}»…', notFound: 'No hay resultados para «{q}».', searchFailed: 'No se encontró el cuadro de búsqueda. Recarga la pestaña de YouTube.', listEmpty: 'La lista aparece en cuanto carga la página.', openYouTube: 'Abrir YouTube', showTab: 'Ir a la pestaña', openDeck: 'Abrir GameDeck',
    previous: 'Anterior', next: 'Siguiente', play: 'Reproducir', pause: 'Pausa', seek: 'Posición en el vídeo', volume: 'Volumen', cover: 'Ir a la pestaña de YouTube',
  },
  pt: {
    ready: 'Conectado', partial: 'Incompleto', off: 'Inativo', online: 'Aberto', offline: 'Não está aberto',
    tabOne: 'Aba conectada', tabMany: '{n} abas abertas', noTab: 'Nenhuma aba aberta',
    playing: 'Tocando agora', paused: 'Pausado', ad: 'Anúncio',
    noVideoTitle: 'Tudo pronto', noVideoText: 'Busque algo abaixo ou abra um vídeo no YouTube.',
    noTabTitle: 'Abra o YouTube', noTabText: 'Digite uma música abaixo para ver os resultados em uma aba nova.',
    noDeckTitle: 'Abra o GameDeck', noDeckText: 'Você pode controlar o YouTube daqui, mas o aplicativo não está conectado.',
    needsClick: 'Clique uma vez na aba do YouTube para o som começar. Abrir a aba',
    search: 'Buscar uma música', go: 'Buscar', upNext: 'A seguir', fromPlaylist: 'Da playlist', results: 'Resultados', searching: 'Buscando “{q}”…', notFound: 'Nenhum resultado para “{q}”.', searchFailed: 'A caixa de busca não foi encontrada. Recarregue a aba do YouTube.', listEmpty: 'A lista aparece assim que a página carregar.', openYouTube: 'Abrir o YouTube', showTab: 'Ir para a aba', openDeck: 'Abrir o GameDeck',
    previous: 'Anterior', next: 'Próxima', play: 'Tocar', pause: 'Pausar', seek: 'Posição no vídeo', volume: 'Volume', cover: 'Ir para a aba do YouTube',
  },
  de: {
    ready: 'Verbunden', partial: 'Unvollständig', off: 'Inaktiv', online: 'Läuft', offline: 'Läuft nicht',
    tabOne: 'Tab verbunden', tabMany: '{n} Tabs offen', noTab: 'Kein Tab offen',
    playing: 'Läuft gerade', paused: 'Pausiert', ad: 'Werbung',
    noVideoTitle: 'Alles bereit', noVideoText: 'Suche unten etwas oder starte ein Video auf YouTube.',
    noTabTitle: 'YouTube öffnen', noTabText: 'Gib unten einen Song ein, um die Ergebnisse in einem neuen Tab zu sehen.',
    noDeckTitle: 'GameDeck starten', noDeckText: 'Du kannst YouTube von hier steuern, aber die App ist nicht verbunden.',
    needsClick: 'Klicke einmal in den YouTube-Tab, damit der Ton startet. Tab öffnen',
    search: 'Song suchen', go: 'Suchen', upNext: 'Als Nächstes', fromPlaylist: 'Aus der Playlist', results: 'Ergebnisse', searching: 'Suche nach „{q}“…', notFound: 'Keine Ergebnisse für „{q}“.', searchFailed: 'Das Suchfeld wurde nicht gefunden. Lade den YouTube-Tab neu.', listEmpty: 'Die Liste erscheint, sobald die Seite geladen ist.', openYouTube: 'YouTube öffnen', showTab: 'Zum Tab', openDeck: 'GameDeck öffnen',
    previous: 'Zurück', next: 'Weiter', play: 'Abspielen', pause: 'Pause', seek: 'Position im Video', volume: 'Lautstärke', cover: 'Zum YouTube-Tab',
  },
  fr: {
    ready: 'Connecté', partial: 'Incomplet', off: 'Inactif', online: 'Lancé', offline: 'Pas lancé',
    tabOne: 'Onglet connecté', tabMany: '{n} onglets ouverts', noTab: 'Aucun onglet ouvert',
    playing: 'En lecture', paused: 'En pause', ad: 'Publicité',
    noVideoTitle: 'Tout est prêt', noVideoText: 'Cherche quelque chose ci-dessous ou lance une vidéo sur YouTube.',
    noTabTitle: 'Ouvre YouTube', noTabText: 'Écris un morceau ci-dessous pour voir les résultats dans un nouvel onglet.',
    noDeckTitle: 'Lance GameDeck', noDeckText: 'Tu peux contrôler YouTube d’ici, mais l’application n’est pas connectée.',
    needsClick: 'Clique une fois dans l’onglet YouTube pour lancer le son. Ouvrir l’onglet',
    search: 'Chercher un morceau', go: 'Chercher', upNext: 'À suivre', fromPlaylist: 'De la playlist', results: 'Résultats', searching: 'Recherche de « {q} »…', notFound: 'Aucun résultat pour « {q} ».', searchFailed: 'Le champ de recherche est introuvable. Recharge l’onglet YouTube.', listEmpty: 'La liste apparaît dès que la page est chargée.', openYouTube: 'Ouvrir YouTube', showTab: 'Aller à l’onglet', openDeck: 'Ouvrir GameDeck',
    previous: 'Précédent', next: 'Suivant', play: 'Lire', pause: 'Pause', seek: 'Position dans la vidéo', volume: 'Volume', cover: 'Aller à l’onglet YouTube',
  },
  pl: {
    ready: 'Połączono', partial: 'Niepełne', off: 'Nieaktywne', online: 'Działa', offline: 'Nie działa',
    tabOne: 'Karta połączona', tabMany: 'Otwarte karty: {n}', noTab: 'Brak otwartej karty',
    playing: 'Teraz odtwarzane', paused: 'Pauza', ad: 'Reklama',
    noVideoTitle: 'Wszystko gotowe', noVideoText: 'Wyszukaj coś poniżej lub włącz film na YouTube.',
    noTabTitle: 'Otwórz YouTube', noTabText: 'Wpisz utwór poniżej, aby zobaczyć wyniki w nowej karcie.',
    noDeckTitle: 'Uruchom GameDeck', noDeckText: 'Możesz stąd sterować YouTube, ale aplikacja nie jest połączona.',
    needsClick: 'Kliknij raz na karcie YouTube, aby włączyć dźwięk. Otwórz kartę',
    search: 'Szukaj utworu', go: 'Szukaj', upNext: 'Następne', fromPlaylist: 'Z playlisty', results: 'Wyniki', searching: 'Szukam „{q}”…', notFound: 'Brak wyników dla „{q}”.', searchFailed: 'Nie znaleziono pola wyszukiwania. Odśwież kartę YouTube.', listEmpty: 'Lista pojawi się, gdy strona się załaduje.', openYouTube: 'Otwórz YouTube', showTab: 'Przejdź do karty', openDeck: 'Otwórz GameDeck',
    previous: 'Poprzedni', next: 'Następny', play: 'Odtwórz', pause: 'Pauza', seek: 'Pozycja w filmie', volume: 'Głośność', cover: 'Przejdź do karty YouTube',
  },
  ru: {
    ready: 'Подключено', partial: 'Не полностью', off: 'Неактивно', online: 'Запущен', offline: 'Не запущен',
    tabOne: 'Вкладка подключена', tabMany: 'Открыто вкладок: {n}', noTab: 'Нет открытой вкладки',
    playing: 'Сейчас играет', paused: 'Пауза', ad: 'Реклама',
    noVideoTitle: 'Всё готово', noVideoText: 'Найди что-нибудь ниже или включи видео на YouTube.',
    noTabTitle: 'Открой YouTube', noTabText: 'Напиши песню ниже, чтобы увидеть результаты в новой вкладке.',
    noDeckTitle: 'Запусти GameDeck', noDeckText: 'Отсюда можно управлять YouTube, но приложение не подключено.',
    needsClick: 'Щёлкни один раз во вкладке YouTube, чтобы пошёл звук. Открыть вкладку',
    search: 'Найти песню', go: 'Найти', upNext: 'Далее', fromPlaylist: 'Из плейлиста', results: 'Результаты', searching: 'Поиск «{q}»…', notFound: 'По запросу «{q}» ничего не найдено.', searchFailed: 'Строка поиска не найдена. Перезагрузи вкладку YouTube.', listEmpty: 'Список появится, как только загрузится страница.', openYouTube: 'Открыть YouTube', showTab: 'Перейти во вкладку', openDeck: 'Открыть GameDeck',
    previous: 'Предыдущая', next: 'Следующая', play: 'Воспроизвести', pause: 'Пауза', seek: 'Позиция в видео', volume: 'Громкость', cover: 'Перейти во вкладку YouTube',
  },
};
const PLAY_ICON = 'M8 5v14l11-7z';
const PAUSE_ICON = 'M7 5h3.500v14H7zM13.500 5H17v14h-3.500z';

const el = (id) => document.getElementById(id);
const port = chrome.runtime.connect({ name: 'panel' });
const send = (message) => port.postMessage(message);
const control = (action, value = 0) => send({ type: 'control', action, value });
let status = null;
let receivedAt = 0;
let dragging = false;
let volumeTimer = 0;
let listTab = 'next';
let listKey = '';

const clock = (seconds) => {
  const s = Math.max(0, Math.floor(seconds || 0));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const rest = String(s % 60).padStart(2, '0');
  return h ? `${h}:${String(m).padStart(2, '0')}:${rest}` : `${m}:${rest}`;
};
const setTone = (element, tone) => {
  element.classList.toggle('is-ok', tone === 'ok');
  element.classList.toggle('is-warn', tone === 'warn');
};

function position() {
  const track = status?.track;
  if (!track) return 0;
  const moved = track.paused ? 0 : (performance.now() - receivedAt) / 1000;
  return Math.min(track.duration || Infinity, track.position + moved);
}

function tick() {
  const track = status?.track;
  if (!track) return;
  const now = position();
  el('elapsed').textContent = clock(now);
  if (!dragging) el('seek').value = Math.round(now);
}

function row(item, currentId) {
  const li = document.createElement('li');
  const button = document.createElement('button');
  button.type = 'button';
  button.className = item.id === currentId ? 'row is-current' : 'row';
  button.dataset.id = item.id;
  const image = document.createElement('img');
  image.alt = '';
  image.loading = 'lazy';
  image.src = `https://i.ytimg.com/vi/${encodeURIComponent(item.id)}/mqdefault.jpg`;
  const body = document.createElement('div');
  const title = document.createElement('strong');
  title.textContent = item.title;
  const details = document.createElement('span');
  details.textContent = [item.channel, item.duration].filter(Boolean).join(' · ');
  body.append(title, details);
  button.append(image, body);
  li.append(button);
  return li;
}

function renderLists(t) {
  const { results, upNext, upNextKind, searching, searchError, query, track } = status;
  const hasSearch = searching || !!searchError || results.length > 0;
  const hasNext = !!track;
  const shown = hasSearch || hasNext;
  el('lists').hidden = !shown;
  el('player').classList.toggle('is-compact', shown);
  if (!shown) return;
  if (!hasNext) listTab = 'results';
  else if (!hasSearch) listTab = 'next';
  el('tabNext').hidden = !hasNext;
  el('tabResults').hidden = !hasSearch;
  el('tabNext').textContent = upNextKind === 'playlist' ? t.fromPlaylist : t.upNext;
  el('tabResults').textContent = t.results;
  el('tabNext').setAttribute('aria-pressed', listTab === 'next');
  el('tabResults').setAttribute('aria-pressed', listTab === 'results');

  const items = listTab === 'results' ? results : upNext;
  const note = listTab === 'next' ? (items.length ? '' : t.listEmpty)
    : searching ? t.searching : searchError === 'searchBoxMissing' ? t.searchFailed : searchError ? t.notFound : '';
  el('listNote').hidden = !note;
  el('listNote').textContent = note.replace('{q}', query);
  const key = JSON.stringify([listTab, items, track?.id]);
  if (key === listKey) return;
  listKey = key;
  el('list').replaceChildren(...items.map((item) => row(item, track?.id)));
}

function render() {
  const t = TEXT[status.language] ?? TEXT.en;
  const { server, tabs, track } = status;
  document.documentElement.lang = status.language;

  el('pillText').textContent = server && tabs ? t.ready : server || tabs ? t.partial : t.off;
  setTone(el('pill'), server && tabs ? 'ok' : server || tabs ? 'warn' : '');
  el('deckText').textContent = server ? t.online : t.offline;
  setTone(el('linkDeck'), server ? 'ok' : 'warn');
  el('tabText').textContent = tabs > 1 ? t.tabMany.replace('{n}', tabs) : tabs ? t.tabOne : t.noTab;
  setTone(el('linkTab'), tabs ? 'ok' : 'warn');

  el('notice').hidden = status.notice !== 'needsClick';
  el('notice').textContent = t.needsClick;
  el('player').hidden = !track;
  el('empty').hidden = !!track;
  if (track) {
    const art = `https://i.ytimg.com/vi/${encodeURIComponent(track.id)}/mqdefault.jpg`;
    if (el('art').getAttribute('src') !== art) {
      el('art').src = art;
      el('ambient').style.backgroundImage = `url("${art}")`;
    }
    el('player').classList.toggle('is-paused', track.paused);
    el('eyebrow').textContent = track.isAd ? t.ad : track.paused ? t.paused : t.playing;
    el('title').textContent = track.title;
    el('channel').textContent = track.channel;
    el('seek').max = Math.max(1, Math.round(track.duration));
    el('seek').disabled = track.isAd || !track.duration;
    el('total').textContent = clock(track.duration);
    el('volume').value = track.volume;
    el('toggleIcon').setAttribute('d', track.paused ? PLAY_ICON : PAUSE_ICON);
    el('toggle').setAttribute('aria-label', track.paused ? t.play : t.pause);
    tick();
  } else {
    el('ambient').style.backgroundImage = '';
    const [title, text] = !tabs ? [t.noTabTitle, t.noTabText] : !server ? [t.noDeckTitle, t.noDeckText] : [t.noVideoTitle, t.noVideoText];
    el('emptyTitle').textContent = title;
    el('emptyText').textContent = text;
  }

  renderLists(t);
  el('query').placeholder = t.search;
  el('query').setAttribute('aria-label', t.search);
  el('go').textContent = t.go;
  el('openYouTube').textContent = tabs ? t.showTab : t.openYouTube;
  el('openDeck').textContent = t.openDeck;
  el('previous').setAttribute('aria-label', t.previous);
  el('next').setAttribute('aria-label', t.next);
  el('seek').setAttribute('aria-label', t.seek);
  el('volume').setAttribute('aria-label', t.volume);
  el('cover').setAttribute('aria-label', t.cover);
}

port.onMessage.addListener((message) => {
  if (message.type !== 'status') return;
  if (message.searching && !status?.searching) listTab = 'results';
  status = message;
  receivedAt = performance.now();
  render();
});

const showList = (tab) => {
  listTab = tab;
  render();
};
el('tabNext').onclick = () => showList('next');
el('tabResults').onclick = () => showList('results');
el('list').onclick = (event) => {
  const id = event.target.closest('.row')?.dataset.id;
  if (id) send({ type: 'openVideo', id });
};
el('toggle').onclick = () => control('toggle');
el('previous').onclick = () => control('previous');
el('next').onclick = () => control('next');
el('cover').onclick = () => send({ type: 'focusTab' });
el('notice').onclick = () => send({ type: 'focusTab' });
el('openDeck').onclick = () => send({ type: 'openDashboard' });
el('openYouTube').onclick = () => send({ type: status?.tabs ? 'focusTab' : 'openYouTube' });
el('seek').oninput = () => { dragging = true; el('elapsed').textContent = clock(Number(el('seek').value)); };
el('seek').onchange = () => { dragging = false; control('seek', Number(el('seek').value)); };
el('volume').oninput = () => {
  clearTimeout(volumeTimer);
  volumeTimer = setTimeout(() => control('volume', Number(el('volume').value)), 120);
};
el('search').onsubmit = (event) => {
  event.preventDefault();
  const query = el('query').value.trim();
  if (!query) return;
  send({ type: 'search', query });
  el('query').value = '';
};
setInterval(tick, 1000); // only runs while the panel is open
