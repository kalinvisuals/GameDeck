import { describe, expect, it } from 'vitest';
import { buttonState, closingActionFor, freeSlot, isActiveState, keysFromEvent, programName, sensorLevel, sensorText, shortLabel } from '../src/features/deck/actions';
import type { DeckButton, DeckState, HardwareSnapshot } from '../src/lib/types';

const snapshot = (over: Partial<HardwareSnapshot> = {}): HardwareSnapshot => ({
  takenUtc: '2026-11-14T10:00:00Z',
  cpu: { model: 'CPU', logicalCores: 16, usagePercent: 12.6, temperatureC: null, clockMhz: 4250 },
  gpu: { model: 'GPU', usagePercent: 40, temperatureC: 55.4, vramUsedMb: 2048, vramTotalMb: 8192, clockMhz: 1800, fanPercent: 30, source: 'nvml' },
  memory: { totalMb: 32768, usedMb: 16384, availableMb: 16384, usagePercent: 50 },
  drives: [],
  ...over,
});

const press = (code: string, mods: Partial<Record<'ctrlKey' | 'altKey' | 'shiftKey' | 'metaKey', boolean>> = {}) =>
  keysFromEvent({ code, ctrlKey: false, altKey: false, shiftKey: false, metaKey: false, ...mods });

describe('hardware readings', () => {
  it('formats what the PC reports', () => {
    const h = snapshot();
    expect(sensorText(h, 'cpu.usage')).toMatchObject({ value: '13', unit: '%' });
    expect(sensorText(h, 'cpu.clock')).toMatchObject({ value: '4.25', unit: 'GHz' });
    expect(sensorText(h, 'gpu.temp')).toMatchObject({ value: '55', unit: '°C' });
    expect(sensorText(h, 'gpu.vram')).toMatchObject({ value: '25', unit: '%', share: 0.25, detail: '2.0 / 8.0 GB' });
    expect(sensorText(h, 'ram.used')).toMatchObject({ value: '16.0', unit: 'GB', share: 0.5 });
  });

  it('never invents a value the PC could not read', () => {
    const h = snapshot({ gpu: { model: null, usagePercent: null, temperatureC: null, vramUsedMb: null, vramTotalMb: null, clockMhz: null, fanPercent: null, source: 'none' }, memory: null });
    for (const id of ['cpu.temp', 'gpu.usage', 'gpu.temp', 'gpu.vram', 'gpu.clock', 'gpu.fan', 'ram.usage', 'ram.used'] as const) {
      expect(sensorText(h, id).value, id).toBeNull();
    }
    expect(sensorText(null, 'cpu.usage').value).toBeNull(); // before the first reading arrives
  });
});

describe('key recorder', () => {
  it('names keys the way the PC parses them', () => {
    expect(press('KeyS', { ctrlKey: true, shiftKey: true })).toBe('Ctrl+Shift+S');
    expect(press('Tab', { altKey: true })).toBe('Alt+Tab');
    expect(press('ArrowLeft', { altKey: true })).toBe('Alt+Left');
    expect(press('Escape')).toBe('Esc');
    expect(press('NumpadEnter', { ctrlKey: true })).toBe('Ctrl+Enter');
    expect(press('F12')).toBe('F12');
  });

  it('waits for a real key', () => {
    expect(press('ShiftLeft', { shiftKey: true })).toBeNull();
    expect(press('Semicolon', { ctrlKey: true })).toBeNull();
  });
});

describe('grid', () => {
  const b = (slot: number) => ({ slot }) as DeckButton;

  it('finds the next free slot, wrapping around, or -1 when full', () => {
    expect(freeSlot([b(0), b(1), b(3)], 4)).toBe(2);
    expect(freeSlot([b(0), b(1), b(3)], 4, 3)).toBe(2);
    expect(freeSlot([b(1)], 4, 2)).toBe(2);
    expect(freeSlot([b(0), b(1)], 2)).toBe(-1);
  });

  it('shows live state from the PC: muted channel, active scene, running or failed macro', () => {
    const state = {
      audio: { micMuted: true, virtualMuted: false, monitorMuted: false },
      apps: [{ key: 'discord.exe', muted: true }],
      sounds: [{ id: 'x' }, { id: 'y' }],
      scenes: { activeId: 's1', items: [{ id: 's1' }, { id: 's2' }] },
      deck: { runningMacros: ['m1'], macroErrors: { m2: 'Stopped at step 1' }, macros: [{ id: 'm1' }, { id: 'm2' }], toggled: ['t1'] },
    } as unknown as DeckState;
    const button = (action: DeckButton['action']) => ({ action }) as DeckButton;
    expect(buttonState(button({ type: 'mute', channel: 'mic' }), state, new Set())).toBe('on');
    expect(buttonState(button({ type: 'mute', channel: 'virtual' }), state, new Set())).toBeNull();
    expect(buttonState(button({ type: 'appMute', target: 'discord.exe' }), state, new Set())).toBe('on');
    expect(buttonState(button({ type: 'scene', target: 's1' }), state, new Set())).toBe('active');
    expect(buttonState(button({ type: 'macro', target: 'm1' }), state, new Set())).toBe('running');
    expect(buttonState(button({ type: 'macro', target: 'm2' }), state, new Set())).toBe('error');
    expect(buttonState(button({ type: 'sound', target: 'x' }), state, new Set(['x']))).toBe('active');
    expect(buttonState(button({ type: 'sound', target: 'y' }), state, new Set(['x']))).toBeNull();
    expect(buttonState(button({ type: 'scene', target: 's2' }), state, new Set())).toBeNull();
    // What a button points at is gone or not running: shown as unavailable, never as working.
    expect(buttonState(button({ type: 'sound', target: 'deleted' }), state, new Set())).toBe('unavailable');
    expect(buttonState(button({ type: 'scene', target: 'deleted' }), state, new Set())).toBe('unavailable');
    expect(buttonState(button({ type: 'macro', target: 'deleted' }), state, new Set())).toBe('unavailable');
    expect(buttonState(button({ type: 'appMute', target: 'spotify.exe' }), state, new Set())).toBe('unavailable');
    expect(buttonState(button({ type: 'media', mode: 'playPause' }), state, new Set())).toBeNull(); // no playback status exists to show
    // A two-state button shows its own state, whatever its actions are.
    const twoState = (id: string) => ({ id, action: { type: 'mute', channel: 'mic' }, toggleAction: { type: 'media', mode: 'mute' } }) as DeckButton;
    expect(buttonState(twoState('t1'), state, new Set())).toBe('active');
    expect(buttonState(twoState('t2'), state, new Set())).toBeNull();
  });
});

describe('button names', () => {
  it('uses the program or site name when a button has no name of its own', () => {
    expect(programName('C:\\Users\\Me\\Start Menu\\Discord.lnk')).toBe('Discord');
    expect(programName('C:\\Games\\Game.EXE')).toBe('Game');
    expect(programName('C:\\Games\\my.exe.backup')).toBe('my.exe.backup');
    expect(programName('C:\\Games\\Saves\\')).toBe('Saves');
    const t = ((k: string) => k) as never;
    expect(shortLabel({ type: 'launch', target: 'C:\\Windows\\notepad.exe' }, t, {} as DeckState)).toBe('notepad');
    expect(shortLabel({ type: 'url', target: 'https://www.twitch.tv/dashboard' }, t, {} as DeckState)).toBe('twitch.tv');
  });
});

describe('appearance', () => {
  it('uses the active look only in a live active state', () => {
    expect([null, 'on', 'active', 'running', 'error', 'unavailable'].map((s) => isActiveState(s as never))).toEqual([false, true, true, true, false, false]);
  });

  it('colours a sensor value by the button thresholds, and never an unavailable one', () => {
    expect(sensorLevel('64', 70, 85)).toBeNull();
    expect(sensorLevel('70', 70, 85)).toBe('warn');
    expect(sensorLevel('91', 70, 85)).toBe('critical');
    expect(sensorLevel('91', null, 85)).toBe('critical');
    expect(sensorLevel('91', null, null)).toBeNull();
    expect(sensorLevel(null, 70, 85)).toBeNull();
    expect(sensorLevel('4.25', 4, null)).toBe('warn');
  });
});

describe('opening and closing a program', () => {
  it('suggests closing the same program a launch button opens, when its file names it', () => {
    expect(closingActionFor({ type: 'launch', target: 'C:\\Program Files\\obs-studio\\bin\\64bit\\OBS64.EXE' })).toEqual({ type: 'window', target: 'obs64.exe', mode: 'close' });
    // A shortcut doesn't say which program it starts: the user picks it from the open programs.
    expect(closingActionFor({ type: 'launch', target: 'C:\\Users\\Me\\Desktop\\Discord.lnk' })).toEqual({ type: 'window', target: '', mode: 'close' });
  });

  it('names a window button after its program', () => {
    const t = ((k: string) => k) as never;
    expect(shortLabel({ type: 'window', target: 'obs64.exe', mode: 'close' }, t, {} as DeckState)).toBe('obs64');
  });
});
