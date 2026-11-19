# Obsidian design system

Original identity for GameDeck: near-black, graphite, thin lines and a single ice-blue accent. No RGB gaming colours, no glow, no glassmorphism.

## Tokens: `web/src/styles/tokens.css`

| Group | Values |
| --- | --- |
| Surfaces | `--bg #090A0C`, `--surface #14161A`, `--surface-raised`, `--surface-hover`, `--surface-sunken` |
| Lines | `--line` 7% white, `--line-strong` 13% white, `--line-focus` (accent) |
| Text | `--text #F4F5F7` (16:1), `--text-2 #A4A9B3` (7.4:1), `--text-3 #7B818C` (4.6:1), all measured on `--surface` |
| Accent | `--accent #A9D6FF` ice blue, used for the active state, playing state, focus and primary actions only |
| Semantic | desaturated `--ok`, `--warn`, `--danger` |
| Shape | radii 6/8/12/16 px, restrained shadows `--shadow-1/2` |
| Motion | 150/200/220 ms, `cubic-bezier(.2,.8,.2,1)`; all set to 0 under `prefers-reduced-motion` |

Button "colours" are now small **tag** markers, not fills, which keeps the grid calm.

## Components: `web/src/ui/`

`Button` (primary, secondary, ghost, danger × sm/md/lg) · `IconButton` · `Switch` · `Segmented` · `Select` · `Field` · `Slider` (throttled live updates) · `StatusDot` · `Badge` · `Card`, `PageHeader`, `EmptyState` · `Dialog` (bottom sheet on phones, centred on desktop) · `Meter` (rAF, real engine levels, sleeps when silent) · `Toasts`.

## Layout

- **Desktop (>860 px):** left navigation (Dashboard, Soundboard, Audio Mixer, Profiles, Settings) and a floating Stop All dock.
- **Phone/tablet:** a slim top bar with a navigation sheet; the Soundboard is the landing view; the Stop All dock is full width. At least 3 pad columns on a phone.
- **Phone landscape:** compact bar, with Stop All in the bottom-right corner.
- **Focus mode:** pads and Stop All only, using browser fullscreen where it's supported.
