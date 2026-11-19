import { describe, expect, it } from 'vitest';
import { hotkeyFromEvent } from '../src/features/scenes';

const press = (code: string, mods: Partial<Record<'ctrlKey' | 'altKey' | 'shiftKey' | 'metaKey', boolean>> = {}) =>
  hotkeyFromEvent({ code, ctrlKey: false, altKey: false, shiftKey: false, metaKey: false, ...mods });

describe('scene shortcut recorder', () => {
  it('builds the same text the PC parses, in a fixed modifier order', () => {
    expect(press('Digit1', { altKey: true, ctrlKey: true })).toBe('Ctrl+Alt+1');
    expect(press('KeyK', { shiftKey: true, altKey: true })).toBe('Alt+Shift+K');
    expect(press('F9', { ctrlKey: true })).toBe('Ctrl+F9');
    expect(press('Numpad5', { altKey: true })).toBe('Alt+Num5');
    expect(press('KeyG', { metaKey: true, altKey: true })).toBe('Alt+Win+G');
  });

  it('uses the physical key, so layouts and Shift do not change it', () => {
    expect(press('KeyZ', { ctrlKey: true, altKey: true })).toBe('Ctrl+Alt+Z'); // "Y" on a German layout
    expect(press('Digit2', { ctrlKey: true, shiftKey: true })).toBe('Ctrl+Shift+2'); // not "@"
  });

  it('waits while only modifiers are down and ignores unsupported keys', () => {
    expect(press('ControlLeft', { ctrlKey: true })).toBeNull();
    expect(press('Space', { ctrlKey: true, altKey: true })).toBeNull();
    expect(press('F25', { ctrlKey: true })).toBeNull();
  });
});
