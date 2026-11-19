# Architecture

```
┌──────────── GameDeck.exe (one process, tray app) ───────────────────────────┐
│  Tray (WinForms)          Kestrel (ASP.NET Core, LAN only)                   │
│  pairing window ─┐        ├─ static dashboard (wwwroot)                      │
│  tray menu      ─┼──► GameDeckService ◄── JSON API /api/*                    │
│                  │        (config ⇄ engine)    WebSocket /ws  ◄──► phones    │
│                  └──► ConfigStore (config.json)                              │
│                       AudioEngine ── Monitor bus ─► headphones               │
│                                   ├─ Virtual bus ─► virtual cable            │
│                                   └─ MicInput ────► Virtual bus only         │
└──────────────────────────────────────────────────────────────────────────────┘
```

## Projects

- **GameDeck.Core** holds no UI and no web code.
  - `Audio/AudioEngine`: the buses, triggering, device recovery, level meters, idle state.
  - `Audio/OutputBus`: one WASAPI shared-mode stream with a mixer, master gain and soft limiter.
  - `Audio/Voice`: one playback of one decoded sound on one bus. It allocates nothing and takes no locks.
  - `Audio/CachedSound`, `SoundCache`: each file is decoded once to 48 kHz stereo float, for instant triggering. Files are capped at 10 minutes.
  - `Audio/MicInput`: WASAPI capture → buffer → resample. Backlog over 120 ms is dropped to keep latency bounded.
  - `Config/ConfigStore`: one lock and atomic writes. A corrupt file is backed up, never silently overwritten.
  - `Security/PairingService`, `NetworkGuard`.
- **GameDeck.App** contains `GameDeckService` (the only place that knows both config and engine), `Web/DashboardServer` (middleware plus endpoints), `Web/WsHub`, and the tray UI.

## Playback lifecycle

1. A press reaches the PC as a WebSocket `play` message (HTTP fallback: `POST /api/sounds/{id}/trigger`).
2. The engine creates one `Voice` per open bus over the shared decoded buffer and adds it to each bus mixer. It then broadcasts `playing`.
3. At the end of the file, a non-looping voice returns a short read. It marks itself finished, the bus mixer detaches it on that audio callback (or the next one), and the engine removes the instance and broadcasts `playing` again. Every controller and the tray tooltip return to idle.
4. **Restart** (the default) fades out the current copy (10 ms) and starts a new one from 0. **Toggle** stops it. **Overlap** layers copies. **Stop / Stop All** fade out in 10 ms to avoid clicks.
5. After a sound ends nothing about it remains: no voice, no mixer input, no timer. Tests prove this on the real device (`EngineLifecycleTests`).

What stays alive on purpose: the two WASAPI device streams (headphones, virtual) stay open while the app runs. Opening a device per press would add tens of milliseconds and audible pops. Rendering silence in shared mode costs very little CPU. Decoded sounds stay cached so the next press is instant. Deleting a sound evicts its cache entry.

Idle cost: the level pusher sleeps on a signal (no timer) whenever nothing is playing and no mic is captured, and dashboard meters stop their animation frames at zero. The only always-on timer is the engine's 3-second device health check.

## Threading rules

- Audio threads (WASAPI callbacks) never take a lock that a control thread can hold while waiting for them. `OutputBus.Close` waits for the playback thread, so voice-finished bookkeeping uses a concurrent dictionary and the engine's work is posted to the thread pool.
- Core Audio objects are created on thread-pool (MTA) threads, not on the STA UI thread.
- Device notifications are debounced by 400 ms and reconciled under the engine lock.

## WebSocket protocol (`/ws`, JSON text frames)

| Direction | Message |
| --- | --- |
| client → server | `{type:"play", soundId, requestId?}` · `{type:"stop", soundId}` · `{type:"stopAll"}` · `{type:"ping"}` |
| server → client | `state` (full snapshot) · `playing` (list with elapsed/duration) · `levels` (peaks, about 15 Hz while audible) · `ack` · `error` · `pong` |

Progress bars run as CSS animations started at `-elapsedMs`, so controllers need no timers.
