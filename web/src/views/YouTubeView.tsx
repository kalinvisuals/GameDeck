import { memo, useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Copy, ListMusic, Pause, Play, Search, SkipBack, SkipForward, Trash2, Upload, Volume2, X } from 'lucide-react';
import { api } from '../lib/api';
import type { YouTubeAction, YouTubeItem, YouTubeTrack } from '../lib/types';
import { useDeck } from '../state/DeckContext';
import { useYouTube, type YouTubeView as Snapshot } from '../state/youtube';
import { Button, IconButton, StatusDot } from '../ui/controls';
import { Card, PageHeader } from '../ui/Card';

const clock = (seconds: number) => {
  const s = Math.max(0, Math.floor(seconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const rest = String(s % 60).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${rest}` : `${m}:${rest}`;
};

export function YouTubeView() {
  const { t } = useTranslation();
  const { run, session } = useDeck();
  const youtube = useYouTube();
  const [text, setText] = useState('');
  const [listMode, setListMode] = useState(false);
  const [closedQuery, setClosedQuery] = useState<string | null>(null);
  const file = useRef<HTMLInputElement>(null);
  const [pickedQuery, setPickedQuery] = useState<string | null>(null);
  const query = youtube?.query;
  const showResults = !!youtube?.results.length && query !== closedQuery;
  const send = (action: YouTubeAction) => run(() => api.youtube(action));
  const open = useCallback((id: string) => run(() => api.youtube({ action: 'open', id })), [run]);
  // The player shrinks to announce a new list and returns to full size once a song is picked from it.
  const openResult = useCallback((id: string) => {
    setPickedQuery(query ?? null);
    return open(id);
  }, [open, query]);

  const submit = (e?: FormEvent) => {
    e?.preventDefault();
    const value = text.trim();
    if (!value) return;
    setClosedQuery(null);
    setPickedQuery(null);
    void send({ action: 'command', text: value });
    setText('');
  };
  const loadFile = async (picked: File | undefined) => {
    if (!picked) return;
    setText(await picked.text());
    setListMode(true);
  };

  return (
    <div className="page youtube">
      <PageHeader title={t('youtube.title')} subtitle={t('youtube.subtitle')} />
      <div className="yt-status">
        <StatusDot tone={youtube?.connected ? 'ok' : 'warn'} />
        <span>{t(youtube?.connected ? 'youtube.connected' : 'youtube.disconnected')}</span>
      </div>

      <form className="yt-search" onSubmit={submit}>
        {listMode ? (
          <textarea className="input" rows={6} value={text} placeholder={t('youtube.listPlaceholder')} aria-label={t('youtube.listPlaceholder')} onChange={(e) => setText(e.target.value)} />
        ) : (
          <input className="input" value={text} maxLength={200} placeholder={t('youtube.placeholder')} aria-label={t('youtube.placeholder')} onChange={(e) => setText(e.target.value)} />
        )}
        <div className="btn-row">
          <Button type="submit" variant="primary" icon={<Search size={16} />} disabled={!text.trim() || !youtube?.connected}>{t(listMode ? 'youtube.playList' : 'youtube.search')}</Button>
          <Button type="button" variant="ghost" icon={<ListMusic size={16} />} onClick={() => setListMode(!listMode)}>{t(listMode ? 'youtube.oneSong' : 'youtube.writeList')}</Button>
          <Button type="button" variant="ghost" icon={<Upload size={16} />} onClick={() => file.current?.click()}>{t('youtube.loadFile')}</Button>
          <input ref={file} type="file" accept=".txt,text/plain" hidden onChange={(e) => { void loadFile(e.target.files?.[0]); e.target.value = ''; }} />
        </div>
        <p className="faint small">{t('youtube.commands')}</p>
      </form>

      {youtube?.searching && <p className="muted" role="status">{t('youtube.searching', { query: youtube.query })}</p>}
      {youtube?.notice && <p className="yt-notice" role="status">{t(`youtube.notices.${youtube.notice}`, { query: youtube.query })}</p>}

      {!youtube?.connected && <Setup local={!!session.local} />}
      {youtube?.track && <NowPlaying track={youtube.track} snapshot={youtube} send={send} compact={showResults && query !== pickedQuery} />}

      {youtube && showResults && (
        <Card key={youtube.query} className="yt-new-list" eyebrow={t('youtube.resultsFor', { query: youtube.query })} title={t('youtube.results')} pad={false}
          actions={<Button size="sm" variant="ghost" icon={<X size={15} />} onClick={() => setClosedQuery(youtube.query)}>{t('youtube.closeResults')}</Button>}>
          <VideoList items={youtube.results} currentId={youtube.track?.id} open={openResult} />
        </Card>
      )}

      {!!youtube?.track && !!youtube.upNext.length && (
        <Card eyebrow={t(youtube.upNextKind === 'playlist' ? 'youtube.upNextPlaylist' : 'youtube.upNextRelated')} title={t('youtube.upNext')} pad={false}>
          <VideoList items={youtube.upNext} currentId={youtube.track.id} open={open} />
        </Card>
      )}

      {!!youtube?.queue.length && (
        <Card eyebrow={t('youtube.queueCount', { count: youtube.queue.length })} title={t('youtube.queue')} pad={false}
          actions={<Button size="sm" variant="ghost" icon={<Trash2 size={15} />} onClick={() => send({ action: 'clearQueue' })}>{t('youtube.clearQueue')}</Button>}>
          <ol className="yt-queue">
            {youtube.queue.map((line, index) => (
              <li key={`${index}-${line}`}>
                <span>{line}</span>
                <IconButton label={t('youtube.removeQueued')} icon={<X size={15} />} onClick={() => send({ action: 'removeQueued', index })} />
              </li>
            ))}
          </ol>
        </Card>
      )}

      {!!youtube?.playlists.length && (
        <Card eyebrow={t('youtube.yourAccount')} title={t('youtube.playlists')}>
          <div className="yt-playlists">
            {youtube.playlists.map((p) => (
              <button key={p.id} type="button" onClick={() => send({ action: 'playlist', id: p.id })}><ListMusic size={16} aria-hidden />{p.title}</button>
            ))}
          </div>
        </Card>
      )}
    </div>
  );
}

const VideoList = memo(function VideoList({ items, currentId, open }: { items: YouTubeItem[]; currentId?: string; open(id: string): unknown }) {
  return (
    <ul className="yt-list">
      {items.map((item) => (
        <li key={item.id}>
          <button type="button" className={`yt-row ${item.id === currentId ? 'is-current' : ''}`} onClick={() => open(item.id)}>
            <img src={item.art} alt="" loading="lazy" />
            <span className="yt-row-text"><strong>{item.title}</strong><span>{[item.channel, item.duration].filter(Boolean).join(' · ')}</span></span>
            <Play size={16} aria-hidden />
          </button>
        </li>
      ))}
    </ul>
  );
});

function NowPlaying({ track, snapshot, send, compact }: { track: YouTubeTrack; snapshot: Snapshot; send(a: YouTubeAction): void; compact: boolean }) {
  const { t } = useTranslation();
  const [now, setNow] = useState(() => performance.now());
  const [volume, setVolume] = useState(track.volume);
  const volumeTimer = useRef<number | undefined>(undefined);

  useEffect(() => setVolume(track.volume), [track.volume]);
  useEffect(() => {
    setNow(performance.now());
    if (track.paused) return;
    const id = window.setInterval(() => setNow(performance.now()), 1000);
    return () => window.clearInterval(id);
  }, [track.paused, track.id, snapshot.receivedAt]);
  useEffect(() => () => window.clearTimeout(volumeTimer.current), []);

  const elapsed = track.paused ? 0 : (now - snapshot.receivedAt) / 1000;
  const position = Math.min(track.duration || Infinity, track.position + Math.max(0, elapsed));
  const control = (text: string, value?: number) => send({ action: 'control', text, value });
  const changeVolume = (value: number) => {
    setVolume(value);
    window.clearTimeout(volumeTimer.current);
    volumeTimer.current = window.setTimeout(() => control('volume', value), 150);
  };

  return (
    <Card className={`yt-player ${compact ? 'is-compact' : ''}`} eyebrow={t(track.isAd ? 'youtube.ad' : 'youtube.nowPlaying')} title={track.title}>
      <div className="yt-now">
        <img src={track.art} alt="" />
        <div className="yt-now-main">
          <strong className="yt-mini-title">{track.title}</strong>
          <span className="muted yt-channel">{track.channel}</span>
          <input type="range" min={0} max={Math.max(1, Math.round(track.duration))} value={Math.round(position)} aria-label={t('youtube.position')} disabled={track.isAd || !track.duration} onChange={(e) => control('seek', Number(e.target.value))} />
          <div className="yt-times num"><span>{clock(position)}</span><span>{clock(track.duration)}</span></div>
          <div className="yt-controls">
            <IconButton label={t('youtube.previous')} icon={<SkipBack size={19} />} onClick={() => control('previous')} />
            <button type="button" className="yt-play" aria-label={t(track.paused ? 'youtube.play' : 'youtube.pause')} onClick={() => control('toggle')}>
              {track.paused ? <Play size={22} fill="currentColor" /> : <Pause size={22} fill="currentColor" />}
            </button>
            <IconButton label={t('youtube.next')} icon={<SkipForward size={19} />} onClick={() => control('next')} />
            <label className="yt-volume"><Volume2 size={17} aria-hidden /><input type="range" min={0} max={100} value={volume} aria-label={t('youtube.volume')} onChange={(e) => changeVolume(Number(e.target.value))} /></label>
          </div>
        </div>
      </div>
    </Card>
  );
}

function Setup({ local }: { local: boolean }) {
  const { t } = useTranslation();
  const { toast } = useDeck();
  const [path, setPath] = useState('');
  useEffect(() => {
    if (local) api.youtubeExtension().then((r) => setPath(r.path)).catch(() => setPath(''));
  }, [local]);
  const copy = () => navigator.clipboard?.writeText(path).then(() => toast(t('youtube.copied'), 'success'));

  return (
    <Card eyebrow={t('youtube.setupEyebrow')} title={t('youtube.setupTitle')}>
      {local ? (
        <>
          <ol className="steps">
            {(['s1', 's2', 's3', 's4', 's5'] as const).map((s) => <li key={s}>{t(`youtube.steps.${s}`)}</li>)}
          </ol>
          {path && <div className="yt-path"><code>{path}</code><Button size="sm" icon={<Copy size={15} />} onClick={copy}>{t('youtube.copyPath')}</Button></div>}
        </>
      ) : <p className="muted">{t('youtube.setupOnPc')}</p>}
    </Card>
  );
}
