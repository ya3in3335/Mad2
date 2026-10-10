# VELTRIX OS — Brand Guidelines

**Slogan:** *Powering Business Intelligence.*
VELTRIX OS is a business operating platform that runs on Windows and Android. Never present it as an operating
system replacing Windows or Android.

All assets are generated from code: `branding/tools/symbol.py` (symbol geometry), `branding/tools/wordmark.py`
(custom geometric letterforms) and `branding/tools/generate.py` (exports). Do not edit exported files by hand —
change the source and run `python3 branding/tools/generate.py`.

> No image-generation model was used. The identity is an original vector construction written for this project.
> Trademark availability has **not** been verified: a basic web search (Oct 2026) found an unrelated
> "Veltrix AI" e-commerce analytics product and a "veltrix" brand page; a professional trademark search in Algeria
> (INAPI) and target export markets (classes 9 and 42) is required before commercial launch.

## 1. The symbol

An original geometric **V** made of two precision blades that never touch:

| Element | Meaning |
|---|---|
| Wide left blade (electric blue → cyan) | Speed, reliability |
| Narrow right blade (violet → electric blue), cut short | Precision, controlled intelligence |
| Circuit trace with two nodes along the left blade | Connectivity, synchronization |
| Cyan core node in the gap | The business "core" — data at the centre |

## 2. Logo set (`branding/logo`)

| File | Use |
|---|---|
| `veltrix-logo-primary.svg` | Default lock-up on navy background |
| `veltrix-logo-horizontal-dark.svg` / `-light.svg` | Horizontal, transparent, for dark / light backgrounds |
| `veltrix-logo-vertical-dark.svg` / `-light.svg` | Stacked lock-up (splash, documents, packaging) |
| `veltrix-logo-mono-light.svg` / `-mono-dark.svg` | Single colour (fax, stamps, engraving, 1-colour print) |
| `veltrix-symbol.svg` | Symbol only, colour |
| `veltrix-symbol-mono-light.svg` / `-mono-dark.svg` | Symbol only, single colour |
| `veltrix-app-icon.svg` | Rounded-square application icon |
| `veltrix-splash-emblem.svg` | Startup animation emblem |

Exports (`branding/exports`): PNG sizes 16–1024 px, Windows `veltrix.ico` (16–256), tray icons, favicon,
Android mipmaps (mdpi–xxxhdpi), adaptive-icon foreground and monochrome notification vectors, Play Store 512 px icon,
Inno Setup wizard bitmaps.

## 3. Colour

| Token | Hex | Usage |
|---|---|---|
| `bg.primary` | `#07111F` | App background (dark), icon plate |
| `bg.secondary` | `#0D1D33` | Panels, cards, sidebars (dark) |
| `brand.blue` | `#3478FF` | Primary brand colour, focus rings, charts |
| `brand.cyan` | `#00D9FF` | Accent, highlights, "OS" in the wordmark on dark |
| `brand.violet` | `#765BFF` | Secondary accent, gradients |
| `text.primary` | `#F5F8FF` | Text on dark |
| `text.secondary` | `#A3B3CB` | Secondary text on dark |
| `status.success` | `#17C78B` | Success |
| `status.warning` | `#FFB547` | Warning |
| `status.danger` | `#F35C6E` | Errors, destructive actions |
| `brand.blue.accessible` | `#2563EB` | Buttons/links **with white text** or on white (5.2:1) |

Measured WCAG contrast (`branding/tools`, Oct 2026):

| Foreground / background | Ratio | Verdict |
|---|---|---|
| `#F5F8FF` on `#07111F` | 17.8 : 1 | AAA |
| `#A3B3CB` on `#07111F` | 8.9 : 1 | AAA |
| `#A3B3CB` on `#0D1D33` | 7.95 : 1 | AAA |
| `#00D9FF` on `#07111F` | 11.2 : 1 | AAA |
| `#3478FF` on `#07111F` | 4.76 : 1 | AA (body text) |
| `#765BFF` on `#07111F` | 4.25 : 1 | AA large text / UI only |
| `#F35C6E` on `#07111F` | 5.9 : 1 | AA |
| White on `#3478FF` | 3.98 : 1 | **Fails AA for body text** → use `#2563EB` for filled buttons |

## 4. Typography

* Wordmark: custom geometric capitals (do not re-type the logo in a font).
* UI Latin: **Inter** (fallback Segoe UI on Windows, Roboto on Android).
* UI Arabic: **Noto Sans Arabic** / **Noto Naskh Arabic** (fallback Segoe UI / system Arabic). Arabic layouts are RTL.
* Numbers in tables use tabular figures.

## 5. Clear space & minimum size

* Clear space around any lock-up = height of the core node × 4 (≈ 11 % of the symbol height) on all sides.
* Minimum symbol size: 16 px (screen) / 6 mm (print). Below 24 px use the app icon (with plate) rather than the bare symbol.
* Minimum horizontal lock-up width: 120 px (screen) / 30 mm (print). Hide the tagline below 240 px width.

## 6. Icon treatment

* Application icons always use the navy plate (`#07111F`), 22 % corner radius on desktop, platform masks on Android.
* Android notification icon is a white silhouette (no gradient, no trace) as required by the platform.
* System tray: colour icon on light taskbars, `veltrix-tray-dark.ico` (white) on dark taskbars.

## 7. Prohibited usage

Do not: recolour the blades outside the palette; swap blade order; close the gap between blades; rotate or skew
the symbol; add drop shadows/bevels/outlines; place the colour logo on busy photos or on mid-blue backgrounds;
stretch the wordmark; use the brand to imply that VELTRIX OS replaces Windows or Android; use the symbol as a
generic "V" in other words.

## 8. Motion

See `branding/motion/MOTION_SPEC.md`. Motion is restrained: precise, fast, never flashing; it always respects the
operating system's reduced-motion setting and never delays application readiness.
