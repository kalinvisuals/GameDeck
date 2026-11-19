# Localization (Română / English)

GameDeck ships in Romanian (`ro-RO`, the default for new installations) and English (`en-US`).

## How it works

| Layer | Where | Notes |
|---|---|---|
| Dashboard (desktop + mobile) | `web/src/i18n/en.ts`, `ro.ts`, `index.ts` | i18next + react-i18next. `en.ts` defines the key shape; `ro.ts` is type-checked against it. |
| Server messages (API errors, WebSocket, routing advice, engine status, route test) | `src/GameDeck.Core/Localization/L.cs` | `L.T(key, args)`, falling back from the current language to English, then to the key. |
| PC windows (tray menu, pairing window, Audio setup window) | `L.cs` (`tray.*`, `pair.*`, `setup.*`) | Rebuilt live when the language changes. |

**Choosing the language:**
- Settings → Preferences → Language (Română / English).
- The change applies immediately, with no reload or restart.
- It is saved on the PC in `config.json` (`ui.language`) through `PUT /api/ui`, and is pushed to every connected device over the WebSocket.
- Each browser also caches the choice in `localStorage` (`gamedeck.language`), so the pairing screen (before login) already uses it; that screen has its own local switch.
- `<html lang>` follows the choice, and dates and decimals are formatted in the active language (`3,5 dB` vs `3.5 dB`).

**Romanian conventions:**
- Comma-below diacritics ș ț (never the cedilla forms ş ţ), plus ă â î.
- „…” quotation marks.
- Plurals use one / few / other (1 buton, 5 butoane, 20 de butoane).

## Coverage

| Area | Keys | Status |
|---|---|---|
| Dashboard UI (all screens, dialogs, toasts, empty and loading states, tooltips, aria labels, mobile nav) | 436 per language | 100 % translated |
| Server and PC windows | ~138 per language | 100 % translated |

Screens audited in both languages:
- Dashboard.
- Soundboard (normal, edit mode, Edit dialog).
- Audio Mixer.
- Profiles.
- All five Settings sections.
- Pairing screen.

They were audited at 1440, 1280, 960, iPad, iPhone, iPhone SE, 360 px and phone landscape, with a layout audit (overflow, clipping, unequal buttons) and a scan for visible text from the other language.

**Result:**
- No untranslated interface text.
- No overflow caused by translation.
- The only layout notes are long user-named sounds, which clamp to two lines by design.

### Intentionally not translated

- **User content:** sound names, categories, profile names, paired-device names.
- **Names reported by Windows:** device names (e.g. "Microphone (USB Mic)", "Speakers (…)"), endpoint names, file paths, OS and runtime strings.
- **Product names:** GameDeck, Discord, TeamSpeak, VB-CABLE, "CABLE Input/Output", Windows, Krisp.
- **Feature names kept as product vocabulary:** Soundboard, Smart Audio Scenes.
- **Audio and streaming terms Romanian users know in English:** Noise gate, De-esser, Bypass, Low-cut, Buffer, Mixer, soft knee, Focus mode. Other audio terms follow the OBS Studio ro-RO translation: Amplificare (gain), Suprimare zgomot, Compresor, Prag, Egalizator, Pune pe mut / Scoate de pe mut.
- **TeamSpeak menu labels** (Options → Capture, "Remove background noise", "Echo reduction", "Automatic voice gain"): TeamSpeak has no Romanian interface, so translating them would make them impossible to find.
- **Units and symbols:** dB, dBFS, Hz, kHz, ms, %, MP3 · WAV, S/M/L.

Discord menu paths are translated, because Discord has a Romanian interface.

## Tests

- `web/tests/i18n.test.ts` (`npm test`, also run by `build.ps1`) checks:
  - key parity;
  - no empty values;
  - plural groups;
  - "actually translated";
  - diacritics, no cedilla, Romanian quotes, no common words without diacritics;
  - placeholder and markup parity;
  - interpolation;
  - Romanian and English plurals;
  - fallback to English and code normalization;
  - default language, switching and persistence;
  - locale formatting;
  - mobile tab labels;
  - every `t('…')` key and every dynamically built key exists;
  - a TypeScript-AST scan that fails on any hardcoded JSX text, user-facing attribute or `confirm()` text.
- `tests/GameDeck.Tests/LocalizationTests.cs` checks:
  - server key and placeholder parity;
  - diacritics;
  - interpolation;
  - default and persistence across restarts;
  - API errors in the selected language;
  - every `L.T("…")` key used in the source exists.

## Adding a string

1. Add the key to `web/src/i18n/en.ts` and the same key to `ro.ts` (TypeScript and the tests enforce this). For server text, add the key to both tables in `L.cs`.
2. Use `t('area.key', { value })` in components, `i18n.t(...)` outside React, or `<Trans>` for strings with `<b>` or `<code>`.
3. For counts, add `_one`, `_few` and `_other` in both files and pass `{ count }`.
