# Interface plugins — format (SDK 1.0, in progress)

Status: the JSON part is implemented (host plan steps 1 to 5 and 7 below): `theme.json`, `cards.json`,
`rail.json`, `home.json`, `strings.json`, installing by address, the official index. Glorious is a plugin
(`glorious-omegatube-plugin`); only Omega is built in. Still to come: `player.json`, the script (step 6, when a
plugin needs it), Tube (with the `lines` card style).

## Goal

Anyone can publish an interface for OmegaTube from their own repository (for example
`glorious-omegatube-plugin`), and anyone can install it by URL, **without the OmegaTube maintainers having to
review it**. That is only acceptable if a plugin cannot do anything harmful, so a plugin is **data** (JSON,
images, fonts) plus an optional **sandboxed script**. It never ships C# or native code.

What a plugin cannot do covers the whole threat model:

| A plugin cannot... | Because |
|---|---|
| read accounts or tokens, call YouTube or any server | the script has no network, no .NET access, no host object that reaches them |
| leak what you watch | images load only from the plugin's own package or from thumbnails the host hands over (no arbitrary URLs: a URL is a way out) |
| phish | no text input; sign-in, account and settings screens always stay native |
| freeze or crash the app | schema-checked JSON, clamped values, script time / memory / recursion limits, every callback under UiGuard; any failure falls back to Omega |

The worst a plugin can do is look bad or be slow. The user uninstalls it.

Omega stays built in. It is the default, the fallback, and the reference implementation of every building block.

## Package

```
my-plugin/
  plugin.json        manifest (required)
  theme.json         tokens: colours, type sizes, radii, surfaces (optional, defaults = Omega)
  home.json          home layout (optional, defaults = Omega)
  cards.json         card looks (optional)
  rail.json          navigation rail (optional)
  player.json        player suggestions row (optional)
  strings.json       the plugin's own labels per language (optional)
  script.js          optional, sandboxed (see Script)
  images/ fonts/     assets referenced by relative path
```

Published as a zip (a GitHub release asset, for example). The files sit at the root of the zip or in a single top
folder (as in GitHub's source archives). The app unpacks it with strict rules, and refuses or skips anything else:

- at most 20 MB downloaded, 20 MB unpacked (counted while unpacking) and 1000 files;
- plain relative names, at most 6 folders deep, no hidden files, no `..`;
- only `.json`, `.png`, `.jpg`, `.jpeg`, `.webp`, `.js`, `.txt` and `.md` files are kept;
- JSON files up to 1 MB, nested at most 128 levels.

Only `plugin.json` can make a package unusable. In the other files, unknown keys are ignored, and a wrong type or
a missing image is logged and leaves the default; numbers out of range are clamped.

### plugin.json

```json
{
  "id": "io.github.someone.glorious",
  "name": "Glorious",
  "version": "1.2.0",
  "author": "someone",
  "description": "Immersive home: the focused video fills the screen.",
  "homepage": "https://github.com/someone/glorious-omegatube-plugin",
  "sdk": "1.0",
  "update": "https://github.com/someone/glorious-omegatube-plugin/releases/latest/download/plugin.zip",
  "preview": "images/screenshot.jpg"
}
```

- `id`: a reverse domain name (`io.github.<user>.<name>`), lower case. It names the install folder.
- `name`, `author`, `description`: plain text, or `"@key"` to take it from `strings.json`.
- `sdk`: the format version it was written for. The app loads a plugin whose `sdk` major version matches its own
  and whose minor version is lower or equal. Otherwise the plugin is listed as "needs a newer OmegaTube" and Omega
  is used.
- `update` (https): where Settings › Interface looks when the user asks to update a plugin that is not official.
  Official plugins update through the index only.

### Official plugins

"Official" means **validated by the OmegaTube maintainer**: someone looked at it, tried it on a TV and found it
works and looks right. The sandbox is what makes any plugin safe; validation is about quality and trust, and it
decides whether the app shows a warning.

**One list, two readers.** The list lives on the `gh-pages` branch (the showcase site's worktree), as
`plugins/index.json`, served at `https://iiyn.github.io/omegatube/plugins/index.json`:

- the site's "Official plugins" page (`plugins/index.html`) renders it;
- the app downloads it (like `remote-config.json`) and shows it in Settings › Interface.

```json
{
  "format": 1,
  "plugins": [
    {
      "id": "io.github.someone.glorious",
      "name": "Glorious",
      "author": "someone",
      "description": { "en": "Immersive home: the focused video fills the screen.", "fr": "Accueil immersif : la vidéo sélectionnée remplit l'écran." },
      "homepage": "https://github.com/someone/glorious-omegatube-plugin",
      "version": "1.2.0",
      "sdk": "1.0",
      "url": "https://github.com/someone/glorious-omegatube-plugin/releases/download/v1.2.0/plugin.zip",
      "sha256": "9f2c...e41a",
      "preview": "img/glorious.webp",
      "validated": "2026-11-20"
    }
  ]
}
```

- **A validation is pinned to one exact file**: `url` points to that version's release asset (never `latest`) and
  `sha256` is checked after download. If the author publishes 1.3.0, it is not official until the index is updated.
- **Official installs update through the index.** The app follows the plugin's own `update` URL only for
  non-official installs. A validated plugin therefore never silently turns into an unvalidated version.
- `preview` images are copied into `gh-pages` (`plugins/img/`), so neither the site nor the app loads images from
  third-party servers to show the list.
- Maintainer workflow: the author opens an issue ("Plugin validation request") with the release URL. The maintainer
  tests it on a TV, runs `tools/plugin-index/add_plugin.py <release zip URL>` (it fills `id`, `name`,
  `version`, `sdk`, `sha256` and the screenshot from the zip into the `gh-pages` worktree), and pushes. Removing an entry stops listing it; installed copies keep working but become
  "not validated" (with a notice).
- The site page follows the public wording rules (no claims about ads; keep the non-affiliation disclaimer).

### Installing

Settings › Interface lists, in this order: Omega (built in), the official plugins (from the index, with preview
and description), and installed non-official plugins.

**Add a plugin by its address** installs any plugin: the address of its zip, typed with the remote (a QR code that
opens a small page on the phone is planned). Addresses are `https://`, or `http://` to a local network address
(`10.x`, `172.16-31.x`, `192.168.x`) so that an author can try a plugin served from their computer
(`python3 -m http.server`; from the TV emulator, the computer is `10.0.2.2`). The transport does not change what
a plugin can do, and official files are checked against their hash. When its `id` + `sha256` is not in the
official index, the app asks for confirmation first:

> **This plugin is not validated by OmegaTube.**
> It cannot access your accounts or your data, but it has not been checked and may look wrong or be slow.
> Install anyway? — [Cancel] [Install]

`Cancel` has the focus first, as in the quit dialog. Installed plugins carry an "Official" or "Not validated" tag
in the list. If the index cannot be downloaded, the last cached copy is used; with no copy at all, every plugin
counts as not validated.

A plugin is used as soon as it is installed; the Interface row switches between Omega and the installed plugins. Installed plugins live in the app's data folder (`plugins/<id>/`, with a
`.install.json` record of the zip's hash and address), and the index is cached as `plugins/index.json`. If the app
dies while starting with a plugin (before the home shows), the next start uses Omega and says so in Settings.

## Units and values

- Pixels on the 1920x1080 design surface. The host scales the finished page to the window
  (`Screen.FitToWindow`, from PR #35).
- Colours: `"#rrggbb"`, `"#rrggbbaa"`, or a token name from `theme.json` (`"accent"`).
- Every number has a documented range. Out-of-range values are clamped and logged, never fatal.
- Times are in milliseconds.

## theme.json — tokens

```json
{
  "colors": {
    "background": "#000000", "pageTint": "#0f121c", "surface": "#262626", "surfaceHigh": "#3a3a3a",
    "text": "#f1f1f1", "textDim": "#aaaaaa", "textMuted": "#6f6f6f", "accent": "#e52d27",
    "scrim": "#000000a6"
  },
  "type": { "title": 34, "body": 24, "small": 19 },
  "radius": { "card": 14, "panel": 20 },
  "surfaces": {
    "panel":      { "image": "images/glass9.9.png" },
    "panelLit":   { "image": "images/glass_lit9.9.png" },
    "pill":       { "image": "images/glass_pill9.9.png" },
    "pillLit":    { "image": "images/glass_pill_lit9.9.png" }
  }
}
```

`surfaces` replace `Widgets.Panel`, `Widgets.SurfaceCard` and the `PillButton` background. A surface is
`{ "image": "...9.9.png" }` (nine-patch); left out, the screens use flat rounded fills in the theme's colours
(Omega). The four go together: with image surfaces, Settings / Account / dialogs take their "glass" variant
(transparent page, surfaces instead of panels). Their layout stays native, only their skin changes.

Colour names usable as values elsewhere: `background`, `surface`, `surfaceHigh`, `surfaceFocused`, `cardInfo`,
`cardInfoFocused`, `cardTextFocused`, `accent`, `pageTint`, `text`, `textDim`, `textMuted`, `scrim`, `panel`,
`panelLine`, `success`, `glassButton`, `glassPanel`, `glassHigh`, `icon`, `iconDim`, `iconFocused`,
`textFocusedDim`. Ranges: type sizes 12 to 80, radii 0 to 60.

The app's icons are white images drawn through `icon` (on the page), `iconDim` (receding, e.g. the other Settings
categories) and `iconFocused` (on a focused light element, `cardInfoFocused`); `textFocusedDim` is a hint on such
an element. They default to white, grey, near black and dark grey: a light theme sets them dark / light.

The player (controls, watch section, side panels) keeps Omega's dark palette because it sits over the video; only
`accent` follows the theme (progress bar, focus).

**Images**, in every file: a path inside the package (`"images/x.png"`, `.png` / `.jpg` / `.webp`), or the name of
one of the app's own images without extension (`"round_mask"`). Where an image is optional (`shadow`, `outline`,
fades, surfaces), `null` turns it off.

## cards.json — card looks

A card look is **one native info style plus its parameters**, not a free tree of primitives. Cards are the
most numerous views in the app (hundreds per page, built in two steps while scrolling, rounded with masks and
nine-patches because Tizen 5.5 has no `CornerRadius`), so the host keeps their layout native and fast, and a
plugin tunes it. A new info style is a host PR, like a new rail mode.

| `info` | what | used by |
|---|---|---|
| `box` | a box glued under the thumbnail: title on two lines, then the meta line; lit when focused | Omega |
| `overlay` | focused: a dark title panel fades in on the thumbnail (long titles scroll), the meta line under it | Glorious rows 2+ (`glass`) |
| `none` | the thumbnail alone | Glorious featured row (`featured`) |
| `lines` *(planned)* | title on two lines, channel, then views · age, on the page colour | Tube |

```json
{
  "classic":  { "info": "box", "focusScale": 1.1, "boxHeight": 104,
                "boxColor": "#1f1f1f", "boxFocusedColor": "#e3e3e3", "textFocusedColor": "#2a2a2a" },
  "glass":    { "info": "overlay", "focusScale": 1.1, "shadow": "images/card_shadow9.9.png",
                "shadowMargin": 36, "shadowDrop": 10, "outline": "images/card_ring9.9.png",
                "overlayPanel": "images/card_info9.9.png", "preview": true },
  "featured": { "info": "none", "shadow": "images/card_shadow9.9.png", "outline": "images/card_ring9.9.png" }
}
```

`overlay` also takes `overlayTextColor` (default `text`), for a title panel lighter or darker than the
page.

Common to every look: `focusScale` (1–1.2), `mask` (the alpha mask that rounds the thumbnail), `shadow` (a
pre-blurred nine-patch; its blur margin `shadowMargin` 0–100 and drop `shadowDrop` 0–50 travel with it), `outline`
(nine-patch ring round the focused card), `preview` (the animated preview may play, `overlay` only). `box`:
`boxHeight` 60–200. The duration / LIVE badge and the watched bar are always there. Colours left out follow
`theme.json`.

A look named like a built-in one starts from it; a new name starts from `base` (default `classic`). At most 16
looks; names are letters, digits, `-` and `_`.

Layouts refer to looks by name (`home.json` rows, the player's suggestions); `classic`, `glass` and `featured`
always exist (built in), a plugin may redefine them or add its own names.

The host chooses the thumbnail file from the drawn width and the user's data-saver setting (`Data/Thumbs.cs` from
PR #35), so a plugin never asks for a resolution. Texts are host-formatted and translated (title, channel,
views, age); a plugin never sees ids or URLs.

## rail.json — navigation

```json
{
  "mode": "overlay",
  "width": 110, "openWidth": 330, "shift": 25,
  "fade": "images/nav_fade.png", "fadeTail": 150,
  "chip": "images/glass_dark9.9.png",
  "idleOpacity": 0.25, "openOpacity": 0.7, "underlineColor": "accent",
  "iconSuffix": "_outline",
  "icons": { "home": "images/icons/home.png", "search": "images/icons/search.png" }
}
```

- `mode`: `drawer` (Omega: a panel that widens with the labels while focused) or `overlay` (Glorious: folded into
  one pill with the current section, opens over the content with a fade and a glass pill on the focused entry).
  Both are native. A new kind of rail is a host PR that adds a mode.
- Geometry: `width` (icons only), `openWidth` (with labels), `shift` (distance from the screen edge).
- `drawer` colours: `panelColor`, `panelOpenColor`, `currentColor`, `focusedColor` (default: theme tokens), and
  `focusedTextColor`, the focused entry's text and icon (default: `text` and `icon`).
- `overlay`: `fade` (image behind the open list) and `fadeTail`, `chip` (folded pill background), `idleOpacity` /
  `openOpacity` (entries other than the current one, folded / open), `underlineColor`. The focused entry's pill is
  the theme's `pillLit` surface.
- Icons: `icons` replaces an entry's icon by key (package images only; keys: `home`, `search`, `subscriptions`,
  `history`, `set_account`, `settings`, and the section icons `music`, `gaming`, `trending`, `news`, `sports`,
  `live`, `kids`); otherwise `iconSuffix` picks a variant of the app's icon when it exists; otherwise the app's icon.
- Ranges: `width` 60–300, `openWidth` up to 700, `shift` 0–100, `fadeTail` 0–600, opacities 0–1.
- Tube is a `drawer` with `panelOpenColor: "background"`, a light `focusedColor` and `focusedTextColor` dark.

## home.json — home layout

```json
{
  "feeds": "rows",
  "header": false,
  "rows": {
    "top": 120, "underRail": true, "inset": 75,
    "card": "glass", "cardWidth": 352, "cardGap": 30, "rowHeight": 374,
    "firstCard": "featured", "firstCardWidth": 317, "firstCardGap": 34, "firstRowHeight": 340, "firstRowTop": 500,
    "flatFeatured": 10, "gridColumns": 5, "gridCardWidth": 330, "gridGap": 30,
    "gridRowHeight": 300, "gridTitledRowHeight": 360
  },
  "grid": { "top": 600, "left": 45 },
  "hero": {
    "leftScrim": "images/hero_left.png", "bottomScrim": "images/hero_bottom.png",
    "titleWidth": 768, "titleSize": 59, "videoPreview": true
  },
  "tintBelowFirstRow": true,
  "fades": { "left": "images/scrim_left.png", "right": "images/scrim_right.png", "bottom": "images/scrim_rowbottom.png" }
}
```

- `feeds`: `auto` (Omega: Home as rows, the other sections as a grid) or `rows` (every section as rows; a flat
  feed becomes a first row of `flatFeatured` videos, then grid rows of `gridColumns`).
- Left out, every key has Omega's value (`rows.top` 90, `inset` 30, `cardWidth` 352, `cardGap` 30, `rowHeight`
  470, `grid.top` 110...). `firstCardWidth`, `firstCardGap` and `firstRowHeight` default to the other rows' values.
- `rows`: where the list sits (`top`; `underRail`: full width so cards slide under the nav, else beside the rail),
  the focused card's x (`inset`), the card look and geometry of the rows, and an optional different first row
  (`firstCard`...; `firstRowTop` holds it lower while focused, under the hero text).
- `grid`: the grid feeds' position (`left` omitted: beside the rail).
- `hero`: the native backdrop block (focused video's picture, title, details, muted video preview after a rest)
  behind the first row. Omit it for no hero. Its text block ends just above the first row.
- `tintBelowFirstRow`: the page turns `pageTint` once the focus leaves the first row (hero homes).
- `fades`: edge fades over the rows band while the hero shows.
- The user-facing toggles (hero video preview, card preview, data saver) stay app settings. A plugin can only
  enable a block; the user can still switch the behaviour off.

## player.json — suggestions row

```json
{ "suggestions": { "card": "wallSmall", "height": 441 } }
```

In SDK 1.0 the player itself (controls, captions, watch section) stays native, and only the suggestions row uses
plugin cards. **Planned for later**: themeable player controls (button surfaces, icons, progress bar colours and
height, layout of the control bar), in the same token / surface / native-style-with-parameters approach. They are wanted, but not first.

## strings.json

```json
{ "en": { "tagline": "Immersive home" }, "fr": { "tagline": "Accueil immersif" } }
```

Used for the plugin's own texts shown by the app, such as `"name": "@name"` and `"description": "@tagline"` in `plugin.json`. Missing language → `en` → the key itself.

## Script (optional)

`script.js` runs in Jint 3.x (Jint 4 does not load on .NET Core 3.x TVs, see the probe notes below). The script
sees one global, `omega`, and nothing else: no `fetch`, no `require`, no .NET.

```js
// Called when the focus moves on the home. Returns overrides for this frame's state; never touches views.
omega.on("homeFocus", (ev) => {
  // ev = { section: "home", row: 2, col: 4, rowCount: 12, item: { title, channel, duration, live, progress } }
  return { page: ev.row === 0 ? "background" : "pageTint", rows: { others: { card: ev.row > 5 ? "compact" : "glass" } } };
});

omega.on("cardState", (ev) => ({ focusScale: ev.item.live ? 1.12 : 1.08 }));

omega.log("loaded");   // goes to the dev log only
```

- Events: `homeFocus`, `cardState`, `railFocus`, `sectionOpen`. A handler returns plain data (overrides of the same
  keys as the JSON files). The host validates and applies it.
- Limits: 1 s for the first run (cold JIT on the TV), then 50 ms per call, 16 MB, recursion 128. A script that
  breaks a limit or throws three times is disabled for the session (the JSON part keeps working) and the user is told.
- The engine is created in the background during start-up. Measured on a UE65AU6905 (Tizen 6, .NET Core 3.1.3 ARM):
  about 0.9 s cold (engine + parse + first call), then about 0.1 ms per focus callback and about 3 ms for a
  50-card computation. That is why scripts react to focus and never run per frame. Animations are always native.

## Evolving the format

The app keeps changing (a Playlists section, Shorts, a new screen...). Plugins describe looks, not sections, so new
parts of the app show up in every plugin, drawn with the plugin's colours and surfaces. The rules that keep it so:

- **New things get defaults that look right in any theme**: built from the theme's tokens and surfaces, never from
  hard-coded colours. A plugin that does not know them still shows them properly.
- **Adding** a key, a card look, a rail entry icon key or a colour token is a **minor** SDK version (1.0 -> 1.1):
  older plugins keep loading and get the defaults; a plugin using the new keys declares the new `sdk`, and older
  apps list it as "needs a newer OmegaTube" instead of half-loading it. Bump `PluginPackage.SdkMinor` and list
  the new keys here.
- **Renaming, removing or changing the meaning** of a key is a **major** version (2.0): every plugin needs an
  update. Avoid it; add a new key next to the old one instead.
- A new section only needs its icon key listed under `rail.json`; a new kind of item (vertical Shorts...) needs a
  new built-in card look or info style with defaults, then plugins may restyle it.

SDK versions: **1.0**, the first format (still open to changes until the first release that ships it).

A complete example using every key: the Catppuccin Latte plugin (`catppuccin-latte-omegatube-plugin`).

## Host work this needs

In the order it would land. Each step is useful on its own.

1. **Tokens and surfaces**: `Theme` becomes an instance (`Theme.Current`) loaded from defaults, then
   `theme.json`. `Widgets.Panel` / `GlassCard` / `PillButton` take a surface instead of testing `Theme.Glorious`.
2. **Card looks**: `Card` builds from a `CardSpec` (info style + parameters). Classic, glass and featured are
   specs in the theme; `FeedRow` / `ShelfList` take a spec instead of `glass` / `thumbOnly`. Tube adds the
   `lines` style.
3. **Rail modes**: `Rail(entries, overlay)` becomes `Rail(entries, RailSpec)`; the rail geometry leaves `Theme`.
4. **Home layout**: `HomeScreen.FeedFor`, `ShelfList` and `UpdateHero` read `HomeSpec` (rows, grid, hero,
   tint, fades). The hero is optional per spec. `Theme.Glorious` is gone: Glorious is entirely data
   (`ThemeSpec.Glorious()`), which is what the plugin loader will read from JSON.
5. **Plugin loader** (done): `Plugins/PluginPackage.cs` reads a package into a `ThemeSpec`,
   `Plugins/PluginStore.cs` downloads (`omega_download` in the core: https or local http, size cap, sha256),
   unpacks, installs, removes and reads the index; Settings › Interface lists Omega + official + installed
   plugins, with the "not validated" warning; start-up fallback to Omega. On `gh-pages`: `plugins/index.json`
   and the "Official plugins" page; `tools/plugin-index/add_plugin.py` writes an entry from a release zip.
   Later: the phone page (QR) to send an address, preview images in the app's list.
6. **Script** (postponed until a plugin needs it: Glorious and Tube do not): Jint 3.x, `omega.on` events, limits,
   disable-on-failure.
7. **Move Glorious and Tube out**: Glorious done (see below). Tube: the `lines` card style, then its own
   repository.

Parts of PR #35 that are not about the Tube look belong to the app regardless and can land first:
`Screen.FitToWindow`, the Sharp / Data saver thumbnails, the `CropToMask` fix, the line-height fix and
`tools/ui-preview` (which should then render plugin specs instead of parsing C# constants).

## Glorious as a plugin (done)

Glorious lives in `glorious-omegatube-plugin` (its author's repository): `plugin.json`, `strings.json`,
`theme.json` (the four glass surfaces), `rail.json` (overlay menu, outline icons) and `home.json` (rows home with
hero, featured first row, tint and edge fades), plus the generators of its images. It needs no `cards.json`: the
built-in `glass` and `featured` looks are what it uses.

What left the app: `ThemeSpec.Glorious()` and `HomeSpec.Glorious`, the built-in interface choice, the glass surfaces
(`glass*.9.png` but `glass_dark`), the row edge fades (`scrim_left` / `scrim_right` / `scrim_rowbottom`), the
outline menu icons and `tools/outline-icons`. A TV still set to `glorious` starts in Omega and Settings › Interface
says that Glorious is now a plugin.

What stays as host building blocks any plugin can turn on, with their default images: `Hero` + `Backdrop` +
`VideoPreview` (hero scrims `hero_left` / `hero_bottom`, `tools/hero-scrims`), overlay cards with `CardPreviews`
and `PreviewRing` (`card_shadow`, `card_ring`, `card_info`, `tools/glass-panel`), the overlay menu (`nav_fade`,
`glass_dark`, `tools/nav-fade`).

Tube (PR #35) will follow the same path once the `lines` card style exists.

## Open questions

- Fonts: undecided. Whether a font file loaded from the data dir works on the TV must first be tested on a
  Tizen 6 TV (then 5.5). Until then `type.font` is not part of SDK 1.0.
- ~~Nine-patch images from the data dir instead of `res/`~~: they work on a Tizen 6 TV (UE65AU6905, 2026-11-16),
  as does unpacking the zip there (.NET Core 3.1). Still to check on a Tizen 5.5 TV.
