import type { DeckState, HardwareSnapshot, Playing, PresenceState, UpdatesState, YouTubeState } from './types';
import { levelStore, type Levels } from '../state/levels';
import { hardwareStore } from '../state/hardware';
import { youtubeStore } from '../state/youtube';
import { updatesStore } from '../state/updates';
import { presenceStore } from '../state/presence';
import { reloadIfUpdated } from './appUpdate';

type ServerMessage =
  | { type: 'state'; data: DeckState }
  | { type: 'playing'; data: Playing[] }
  | { type: 'levels'; data: Levels }
  | { type: 'hardware'; data: HardwareSnapshot }
  | { type: 'youtube'; data: YouTubeState }
  | { type: 'updates'; data: UpdatesState }
  | { type: 'presence'; data: PresenceState }
  | { type: 'ack'; requestId?: string; result: string }
  | { type: 'error'; requestId?: string; message: string }
  | { type: 'pong' };

export interface SocketHandlers {
  onState(state: DeckState): void;
  onPlaying(playing: Playing[]): void;
  onError(message: string): void;
  onConnection(connected: boolean): void;
  /** Called when the socket drops; resolve false to stop reconnecting (no longer paired). */
  stillAuthorized(): Promise<boolean>;
}

/** Auto-reconnecting WebSocket. iOS kills sockets in the background, so we also reconnect on focus. */
export class DeckSocket {
  private ws: WebSocket | null = null;
  private attempt = 0;
  private timer: number | undefined;
  private disposed = false;

  constructor(private readonly handlers: SocketHandlers) {
    document.addEventListener('visibilitychange', this.onVisible);
    window.addEventListener('online', this.onVisible);
  }

  connect(): void {
    if (this.disposed || (this.ws && this.ws.readyState <= WebSocket.OPEN)) return;
    window.clearTimeout(this.timer);
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    const ws = new WebSocket(`${proto}://${location.host}/ws`);
    this.ws = ws;

    ws.onopen = () => {
      this.attempt = 0;
      if (hardwareStore.watched()) ws.send(JSON.stringify({ type: 'hardware', on: true }));
      this.handlers.onConnection(true);
      void reloadIfUpdated();
    };
    ws.onmessage = (e) => {
      let msg: ServerMessage;
      try {
        msg = JSON.parse(e.data as string);
      } catch {
        return;
      }
      if (msg.type === 'levels') levelStore.set(msg.data);
      else if (msg.type === 'hardware') hardwareStore.set(msg.data);
      else if (msg.type === 'youtube') youtubeStore.set(msg.data);
      else if (msg.type === 'updates') updatesStore.set(msg.data);
      else if (msg.type === 'presence') presenceStore.set(msg.data);
      else if (msg.type === 'state') this.handlers.onState(msg.data);
      else if (msg.type === 'playing') this.handlers.onPlaying(msg.data);
      else if (msg.type === 'error') this.handlers.onError(msg.message);
    };
    ws.onclose = async () => {
      if (this.ws !== ws) return;
      this.ws = null;
      this.handlers.onConnection(false);
      if (this.disposed) return;
      const ok = await this.handlers.stillAuthorized().catch(() => true);
      if (!ok || this.disposed) return;
      const delay = Math.min(5000, 300 * 2 ** this.attempt++);
      this.timer = window.setTimeout(() => this.connect(), delay);
    };
  }

  /** Returns false when not connected so callers can fall back to HTTP. */
  send(message: object): boolean {
    if (this.ws?.readyState !== WebSocket.OPEN) return false;
    this.ws.send(JSON.stringify(message));
    return true;
  }

  dispose(): void {
    this.disposed = true;
    window.clearTimeout(this.timer);
    document.removeEventListener('visibilitychange', this.onVisible);
    window.removeEventListener('online', this.onVisible);
    this.ws?.close();
    this.ws = null;
  }

  private onVisible = () => {
    if (document.visibilityState === 'visible' && !this.ws) {
      this.attempt = 0;
      this.connect();
    }
  };
}
