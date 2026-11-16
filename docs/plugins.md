# Interface plugins — format draft (v0, not implemented)

Status: design draft, open for discussion. Nothing here exists in the app yet.

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

Published as a zip (a GitHub release asset, for example). Size cap: 20 MB unpacked.

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

- `sdk`: the format version it was written for. The app loads a plugin whose `sdk` major version matches its own
  and whose minor version is lower or equal. Otherwise the plugin is listed as "needs an update" and Omega is used.
- `update`: checked like `remote-config.json`. A new version is downloaded, validated, then swapped in on the next launch.

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
  tests it on a TV, adds the entry in the `gh-pages` worktree (a small script fills `sha256`, `version`, `sdk`
  and `id` from the zip), and pushes. Removing an entry stops listing it; installed copies keep working but become
  "not validated" (with a notice).
- The site page follows the public wording rules (no claims about ads; keep the non-affiliation disclaimer).

### Installing

Settings › Interface lists, in this order: Omega (built in), the official plugins (from the index, with preview
and description), and installed non-official plugins.

**Add by URL** (type it, or scan a QR code that opens a small page on the phone) installs any plugin. When its
`id` + `sha256` is not in the official index, the app asks for confirmation first:

> **This plugin is not validated by OmegaTube.**
> It cannot access your accounts or your data, but it has not been checked and may look wrong or be slow.
> Install anyway? — [Cancel] [Install]

`Cancel` has the focus first, as in the quit dialog. Installed plugins carry an "Official" or "Not validated" tag
in the list. If the index cannot be downloaded, the last cached copy is used; with no copy at all, every plugin
counts as not validated.

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

`surfaces` replace `Widgets.Panel`, `Widgets.GlassCard` and the `PillButton` background. A surface is either
`{ "color": ..., "radius": ... }` (flat, Omega) or `{ "image": "...9.9.png" }` (nine-patch). This is all the
Settings / Account / dialogs screens take from a plugin: their layout stays native, only their skin changes. That
is what Glorious does today with `if (Theme.Glorious)`.

## cards.json — card looks

A card look is **one native info style plus its parameters**, not a free tree of primitives. Cards are the
most numerous views in the app (hundreds per page, built in two steps while scrolling, rounded with masks and
nine-patches because Tizen 5.5 has no `CornerRadius`), so the host keeps their layout native and fast, and a
plugin tunes it. A new info style is a host PR, like a new rail mode.

| `info` | what | used by |
|---|---|---|
| `box` | a box glued under the thumbnail: title on two lines, then the meta line; lit when focused | Omega |
| `overlay` | focused: a dark title panel fades in on the thumbnail (long titles scroll), the meta line under it | Glorious rows 2+ |
| `none` | the thumbnail alone | Glorious featured row |
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

Common to every look: `focusScale` (1–1.2), `mask` (the alpha mask that rounds the thumbnail), `shadow` (a
pre-blurred nine-patch; its blur margin and drop travel with it), `outline` (nine-patch ring round the focused
card), `preview` (the animated preview may play, `overlay` only). The duration / LIVE badge and the watched bar
are always there. Colours left out follow `theme.json`.

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
  "background": { "image": "images/nav_fade.png" },
  "icons": { "home": "images/icons/home.png", "search": "images/icons/search.png" },
  "entry": {
    "height": 84, "iconSize": 40,
    "idle":    { "opacity": 0.25 },
    "current": { "underline": { "color": "accent", "height": 4 } },
    "focused": { "surface": "pill", "labelColor": "text" }
  },
  "folded": { "chip": { "surface": "panel", "height": 60 } },
  "avatar": { "size": 72, "ring": "accent" }
}
```

- `mode`: `drawer` (Omega: a panel that widens with labels) or `overlay` (Glorious: a folded chip that opens over
  the content). Both are native. A new kind of rail is a host PR that adds a mode.
- Tube is a `drawer` with `background: "background"` and `focused.surface: { "color": "#e8e8e8", "radius": 42 }`
  plus `focused.labelColor: "#0f0f0f"`.
- Icons default to the app's set; a plugin may replace any of them by key.

## home.json — home layout

```json
{
  "feeds": "rows",
  "rows": {
    "top": 120, "inset": 75, "edgeFades": { "left": "images/fade_l.png", "right": "images/fade_r.png", "bottom": "images/fade_b.png" },
    "first":  { "card": "featured", "height": 340, "top": 500, "title": true },
    "others": { "card": "glass", "height": 374, "title": true },
    "flat":   { "featured": 10, "gridColumns": 5, "gridCard": "glass" }
  },
  "grid": { "columns": 3, "card": "wall", "gapX": 34, "gapY": 48, "top": 160 },
  "header": false,
  "page": { "background": "background", "belowFirstRow": "pageTint" },
  "hero": {
    "rows": [0],
    "art": "maxres",
    "scrims": [ "images/hero_left.png", "images/hero_bottom.png" ],
    "title": { "x": 75, "bottom": 604, "width": 768, "lines": 2, "size": 59 },
    "meta":  { "bind": "item.meta", "size": 26 },
    "videoPreview": { "after": 3000, "ring": true }
  }
}
```

- `feeds`: `rows` (every section as titled rows, like Glorious), `auto` (Omega: Home as rows, other sections as a
  grid) or `grid`.
- `hero`: the native `Hero` + `Backdrop` + `VideoPreview` block, enabled on the listed rows. Omit it for no hero.
- `page.belowFirstRow`: the tint Glorious switches to under the first row.
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

## Host work this needs

In the order it would land. Each step is useful on its own.

1. **Tokens and surfaces**: `Theme` becomes an instance (`Theme.Current`) loaded from defaults, then
   `theme.json`. `Widgets.Panel` / `GlassCard` / `PillButton` take a surface instead of testing `Theme.Glorious`.
2. **Card looks**: `Card` builds from a `CardSpec` (info style + parameters). Classic, glass and featured are
   specs in the theme; `FeedRow` / `ShelfList` take a spec instead of `glass` / `thumbOnly`. Tube adds the
   `lines` style.
3. **Rail modes**: `Rail(entries, overlay)` becomes `Rail(entries, RailSpec)`.
4. **Home layout**: `HomeScreen.FeedFor`, `ShelfList(bigFirst, heroMode)` and `UpdateHero` read `HomeSpec`. The
   hero becomes optional per spec, not per `Theme.Glorious`.
5. **Plugin loader**: download, unzip into the data dir, validate (JSON schema, sizes, paths stay inside the
   package), Settings › Interface lists Omega + official + installed plugins, the "not validated" warning,
   updates (through the index for official ones), fallback. On `gh-pages`: `plugins/index.json`, the
   "Official plugins" page and the script that writes an entry from a release zip.
6. **Script**: Jint 3.x, `omega.on` events, limits, disable-on-failure.
7. **Move Glorious and Tube out**: publish them as plugins in their authors' repositories, then delete their code
   from the app (cleanup below).

Parts of PR #35 that are not about the Tube look belong to the app regardless and can land first:
`Screen.FitToWindow`, the Sharp / Data saver thumbnails, the `CropToMask` fix, the line-height fix and
`tools/ui-preview` (which should then render plugin specs instead of parsing C# constants).

## Cleanup once Glorious and Tube are plugins

Only Omega stays in the app. To remove:

- `Theme.Glorious`, `Theme.Tube`, `Theme.GlassPanel` / `GlassHigh` / `PageTint` / `TubePill` / `TubeSub`, every
  `Glorious*`, `Big*`, `Grid*`, `Nav*`, `Hero*` and `Tube*` constant in `Ui/Theme.cs`.
- `AppConfig.UiStyle` values other than `omega` (migrate `glorious` / `tube` to "install the plugin" on first launch,
  or to Omega).
- Every `if (Theme.Glorious)` / `Glorious ?` / `Theme.Tube ?` branch in `HomeScreen`, `ShelfList`, `FeedRow`,
  `FeedGrid`, `Card`, `Rail`, `Widgets`, `SettingsScreen`, `AccountScreen`, `PlayerScreen` (by then they read specs).
- The hard-coded interface choice in `SettingsScreen` (replaced by the plugin list) and its strings in `I18n.cs`.
- Assets only Glorious uses (`glass*9.9.png`, outline icons, hero scrims, nav fade) and their generators in
  `tools/` (`glass-panel`, `hero-scrims`, `nav-fade`, `outline-icons`) move to the Glorious plugin repository.
- Kept as host building blocks, not as Glorious code: `Hero`, `Backdrop`, `VideoPreview`, `CardPreviews`,
  `PreviewRing` (any plugin can enable them through `home.json` / `cards.json`).

## Open questions

- Fonts: undecided. Whether a font file loaded from the data dir works on the TV must first be tested on a
  Tizen 6 TV (then 5.5). Until then `type.font` is not part of SDK 1.0.
- Nine-patch images from the data dir instead of `res/`: to check on the TV.
