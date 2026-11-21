# codeLess UI guidelines

The workbench should feel calm and consistent, like VS Code's *Modern* themes, while keeping codeLess's identity:
default theme **Savatarr Graphite**, accent **#1978E5**, 11 built-in themes (light and dark), and our own icon set
(no VS Code trademarks or codicon assets).

The spec was drafted from per-screen design reviews by Google Gemini (one screenshot per request, then a text-only
synthesis). Every finding was checked against the real screenshots and code before it was adopted; claims that were
wrong (for example "search matches are not highlighted", "section headers use a hyphen", "pill-shaped buttons") were
dropped. Where Gemini proposed a value that differs from what VS Code actually ships, the VS Code value wins; those
decisions are noted below.

**Single source of truth:** `src/codeLess.Core/UiDesignTokens.cs` (`UiTokens`, strict C# 5, used by both UIs).
Avalonia applies it through `src/codeLess.Avalonia/WorkbenchStyles.cs` (Fluent control re-skin, icon buttons,
notifications) and `MainWindow.Polish.cs` (status bar, hover styles). WinForms reads the same tokens directly.

## 1. Principles

- **Content first.** Chrome recedes: neutral backgrounds, 1 px `BorderSubtle` dividers, secondary text for labels.
- **Elevation for floating things only.** Find widget, suggest/hover, palette, menus, notifications and dialogs
  use the widget background, a 1 px widget border and a soft shadow. Docked surfaces never get shadows.
- **Accent is scarce.** It marks focus, the active item and the one primary action, never large areas.
- **Compact and regular.** 22 px rows, 26 px controls, 35 px headers, 22 px status bar; the same type scale everywhere.
- **No default OS controls, no text glyphs as icons.** Inputs, combos, buttons and check boxes are re-skinned;
  icons are vector glyphs (`AvaloniaVectorIcons` / `CodeLessVectorIcons`), never `x`, `>`, `^`, `✎`, `><`.

## 2. Design tokens

### Typography (px; WinForms points = px × 0.75 via `UiTokens.Pt`)

| Token | Size | Use |
|---|---|---|
| `FontUi` | 13 | tree/list rows, tabs, inputs, menus, buttons, chat text |
| `FontSmall` | 12 | status bar, hints, descriptions, breadcrumbs, secondary list text |
| `FontHeader` | 11, semibold, UPPERCASE | view titles, section headers, panel tabs (panel tabs: regular weight) |
| `FontBadge` | 10 | counters inside badges |
| `FontTitle` | 20 | welcome page / empty-state headings only |

Font family: Inter on macOS/Linux (bundled), Segoe UI on Windows; editor and terminal stay monospaced.

### Metrics (px)

| Token | Value | Notes |
|---|---|---|
| `TitleBarHeight` | 34 | command center 24 high, 6 px radius |
| `ActivityBarWidth` / `ActivityIconSize` | 48 / 24 | 48×48 cells |
| `ViewHeaderHeight` | 35 | side bar title row, chat header |
| `SectionHeaderHeight` | 22 | collapsible sections (OPEN EDITORS, OUTLINE, VARIABLES …) |
| `ListRowHeight` | 22 | explorer, search results, outline, SCM, terminal list, suggest rows |
| `TabHeight` / `PanelHeaderHeight` | 35 | editor tabs (34 + 1 px divider), panel tab row |
| `StatusBarHeight` | 22 | plus a 1 px top divider |
| `TreeIndent` / `TreeTwistieWidth` | 12 / 16 | VS Code defaults to 8; 12 keeps deep trees readable (owner asked for 8–16) |
| `ControlHeight` | 26 | inputs, combo boxes, dialog buttons (Fluent default was 32) |
| `IconButtonSize` / `IconSize` | 22 / 16 | toolbar buttons; 20/14 inside inputs and tabs |
| `ViewPaddingX` | 12 | horizontal padding of views and panels |
| `ControlRadius` | 2 | inputs, buttons, check boxes (VS Code Modern) |
| `ToolbarRadius` | 4 | hover fill of icon buttons, chat composer, code blocks |
| `PopupRadius` | 6 | widgets, notifications, palette, menus, dialogs (Gemini suggested 8; VS Code uses ~5–6) |
| `DividerWidth` | 1 | every divider |
| `TabIndicatorWidth` | 2 | active editor tab, top edge, accent |
| `PanelTabIndicatorWidth` | 1 | active panel tab, bottom edge, accent |

### Colors (formulas over theme roles; implemented in `UiTokens`)

| Role | Formula |
|---|---|
| Hover (rows, toolbar buttons) | surface + 7 % `FgPrimary` (light: 6 %) |
| Pressed / toggled | surface + 13 % `FgPrimary` (light: 11 %) |
| List active selection | surface + 24 % `AccentPrimary` (light: 16 %) — never a solid accent fill |
| List inactive selection / current item | surface + 10 % `FgPrimary` (light: 8 %) |
| Input background | `BgSidebar` + 4.5 % `FgPrimary`; light: white |
| Input border | `BgSidebar` + 13 % `FgPrimary`; light: `BgEditor` + 20 % `FgPrimary` |
| Focus border | 1 px `AccentPrimary` (inputs never turn black on focus) |
| Primary button | `AccentPrimary`, white text; hover = accent darkened 12 % |
| Secondary button | surface + 10 % `FgPrimary`, `FgPrimary` text; hover 15 % |
| Widget background / border | `BgElevated` / `BgElevated` + 10 % `FgPrimary` (light 16 %) |
| Widget shadow | 0 4 16, black 47 % (light 18 %); palette 0 8 24, dialogs 0 10 32 |
| Indent guides | surface + 11 % `FgPrimary`; active guide 32 % |
| Count badge | `BgSidebar` + 16 % `FgPrimary` (light 12 %), `FgPrimary` text |
| Disabled text | `FgSecondary` at ~47 % alpha |

### Status bar

| Role | Value |
|---|---|
| Background | neutral: `BgActivity` when the theme's `BgStatus` is a saturated dark-theme brand color (Savatarr Graphite, Obsidian), otherwise the theme's own (already neutral) `BgStatus` |
| Foreground | `FgPrimary` blended 18 % toward the bar (light 12 %) |
| Item hover | bar + 12 % `FgPrimary` |
| Top divider | 1 px `BorderSubtle` |
| Workspace badge (left, "remote" slot) | `AccentPrimary`, white — the only accent fill on the bar |
| Debugging | orange bar (`DebuggingStatusBarColor`), white text (unchanged) |
| Messages | single line, ellipsized at 90 chars (`ClampStatusMessage`, full text in tooltip), cleared after 8 s |

## 3. Accent usage

Use `AccentPrimary` for: focused input/list border, active editor tab top bar (2 px), active panel tab underline
(1 px), activity bar active bar (2 px, full height), checked check boxes and option toggles (tinted fill + 1 px
border), the single primary button of a surface, links on the welcome page, the status-bar workspace badge, the chat
header glyph, progress bars.

Do not use it for: list/palette selection fills, palette category prefixes, breadcrumb symbols, keybinding chips,
large banners, card borders, user chat bubbles, notification frames, or "Accept"/"Reject" in saturated green/red.

## 4. Components

**Title bar & command center.** 34 px on `BgActivity`. Command center: 24 px, 6 px radius, title bar + 5 %
`FgPrimary` fill, 12 % border, search glyph + workspace name in `FgSecondary` 12 px (shortcuts in the tooltip).
Layout toggles and window caption buttons are 26/34 px icon buttons.

**Activity bar.** 48 px wide, 48×48 cells, 24 px glyphs: active `FgPrimary`, inactive `FgSecondary`, hover
brightens the glyph (no fill). Active indicator: 2 px full-height accent bar on the left edge.

**Side bar.** View title row 35 px, 20 px left padding, 11 px UPPERCASE regular `FgSecondary`; actions are
22 px icon buttons (16 px glyphs). Section headers 22 px: vector chevron (12 px) + 11 px semibold UPPERCASE label.

**Explorer tree.** 22 px rows, indent guides 1 px, file-type badges, folder glyphs. Selected row = list active
selection; active editor file = inactive selection + 3 px accent marker; hover = hover token.

**Editor tabs.** 35 px row; tab label 13 px regular (preview tabs italic); active tab on `BgEditor` with a 2 px
accent top bar; inactive tabs on `TabBarBg`, hover fill on pointer-over. Close glyph (vector) visible on the active
tab and on hover; dirty dot in accent.

**Breadcrumbs.** 24 px, 12 px `FgSecondary` segments, `FgMuted` `›` separators, symbol segment is plain secondary
text (no accent, no `▾`).

**Find widget.** Docked to the editor's top-right edge, widget background, 1 px widget border on three sides,
6 px bottom radius, shadow. 24 px input, 230 px wide, with the Aa / ab / .* toggles *inside* the input (20 px,
accent-tinted + accent border when on). Vector buttons: toggle-replace chevron, previous/next arrows, close,
replace, replace-all. Match count 12 px `FgSecondary`.

**Suggest & hover.** Widget background/border/shadow, 4 px radius, 22 px rows (21 in code), 13 px labels,
12 px details in `FgSecondary`, kind glyphs tinted from the theme's syntax roles, selection = list active selection.

**Command palette / quick open.** 640 px wide, 40 px below the top, widget background, 1 px widget border, shadow
0 8 24. 26 px input, hint row 11.5 px `FgMuted`, 22 px rows, category prefix in `FgSecondary` regular, keybinding
chips = secondary-button fill, UI font 11 px, no border.

**Panel (bottom).** 35 px header; tabs 11 px UPPERCASE regular, `FgSecondary` → `FgPrimary` when active, 1 px
accent underline, no hover fill. Toolbar: borderless shell combo + 22 px icon buttons (plus, split, pencil, trash,
chevron-up, close) with 2 px spacing. Output view: 12 px monospace, 12 px padding.

**Terminal session list.** 22 px rows: terminal glyph (14 px) + `n: name` 12.5 px; active row = inactive
selection (no accent border); hover fill; trash icon shown on the active/hovered row.

**Status bar.** See the status-bar token table. Items: 6 px horizontal padding (badge 9 px), 12 px text, vector
glyphs for workspace (`remote`), branch, errors/warnings; branch hidden outside a repository; LSP / Python /
network / AI items use the same item style.

**Notifications.** 400 px, widget background/border/shadow, 6 px radius, severity glyph (16 px) + 13 px message,
optional 12 px detail, right-aligned buttons (first primary when it is the main action), close glyph top-right.
Themed on every theme switch (fixes white-on-white text in light themes).

**AI chat dock.** 35 px header: sparkle glyph (accent) + 11 px semibold UPPERCASE `FgSecondary` title; new /
history / close icon buttons. Review bar: widget background, "Accept All" primary, "Reject All" secondary. User
messages on the inactive-selection fill (no blue border); assistant cards unchanged. Composer: one input-colored
card (4 px radius, 1 px input border → accent on focus) holding context chips, the prompt (13 px, borderless),
mode segment (Agent / Ask / Edit / Auto-Fix; active = accent-tinted fill), borderless provider/model pickers and a
primary Send button with a vector glyph. Empty state: 14 px semibold title in `FgPrimary` with accent glyph,
starter prompts as bordered flat rows with a chevron glyph.

**Inline diff (editor).** Banner: widget background, 12 px text, arrow icon buttons, primary "Accept All",
secondary "Reject All". Per-hunk chips: primary "Accept", secondary "Reject" (no saturated green/red).

**Buttons.** 2 px radius; primary = accent, secondary = neutral fill, icon buttons transparent with 4 px-radius
hover fill. No borders.

**Inputs, combos, check boxes.** 26 px, 2 px radius, input background and border, 1 px accent focus border with
the same background. Check boxes 16 px, accent fill + white check when checked. Combo popups and menus use the
widget colors with list-selection highlight.

**Context menus.** Widget background/border, 6 px radius, 4 px inner padding, 13 px items with 10×4 px padding,
accent-tinted hover, separators 1 px.

**Dialogs (modal host).** Scrim black 43 %; card = widget background/border, 8 px radius, shadow 0 10 32; 14 px
semibold title, vector close button; section labels 11 px semibold UPPERCASE `FgSecondary`; primary + secondary
26 px buttons right-aligned.

**Welcome page.** 28 px semibold title, 14 px `FgSecondary` subtitle; 15 px regular section headings; Start links
in link color with the shortcut beside them in `FgMuted` 12 px; Recent: name in link color, path `FgMuted` 12 px;
walkthrough cards = editor + 3.5 % `FgPrimary`, 1 px `BorderSubtle`, accent glyph + 13 px semibold title.

**Empty states.** 12–13 px `FgSecondary` text, one clear action.

**Search view.** Query box with toggles inside; replace-toggle chevron icon; replace-all icon button; include /
exclude labels 11 px; results: 22 px rows, file header 13 px with `FgMuted` folder path, neutral count badge,
match highlight = accent at 33 % behind the text (no bold).

**Run and Debug.** Config row (start glyph, combo, gear), hint text `FgSecondary`, sections with vector chevrons,
breakpoint rows with a pencil icon button; selection = list active selection.

**Commit graph.** 26 px filter box, "All branches" check box, refresh icon button; selected commit = list active
selection; detail panel: copy-hash icon button, CHANGED FILES header 11 px semibold `FgSecondary`.

**Markdown preview / aux pane.** Aux tabs match editor tabs (34 px, 13 px, vector close). Preview content uses the
existing renderer.

**HTTP response.** Status badge, meta text 11.5 px, secondary small buttons (22 px), BODY / HEADERS / REQUEST tabs
styled like panel tabs (11 px regular, 1 px accent underline).

**Network Activity window.** 14 px semibold summary, note 11.5 px capped at 820 px, 26 px filter and buttons,
UPPERCASE 11 px column headers, 12 px rows; only the Status / Blocked cells use the error / warning color.

## 5. Platform notes

- **Avalonia:** `WorkbenchStyles.Apply(theme)` overrides Fluent resource keys (`TextControl*`, `Button*`,
  `ComboBox*`, `CheckBox*`, `MenuFlyout*`, `ToolTip*`, `ScrollBar*`, list highlight brushes) and installs metric
  styles once; `MainWindow.InstallChromeStyles` adds the `statusItem`, `statusBadge`, `icon`, `flat`, `paneltab`
  and `primary` button classes.
- **WinForms:** the same tokens drive the status bar (color, foreground, hover, divider, transient messages),
  list/tree/palette selections, terminal list rows, diff banners and per-hunk chips, notifications and drawn icons.
  WinForms controls are already owner-drawn, so no global re-skin layer is needed.
