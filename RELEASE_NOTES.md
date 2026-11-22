# codeLess v3.3.3

A VS Code-parity release driven by bug reports from v3.3.1 on macOS (v3.3.2 was tagged but never published: a flaky
test stopped its release build). Every fix ships on Windows, macOS and Linux.

## Fixed

- **Strings in long lines are coloured correctly.** Highlighting used to restart at the visible part of a long line, so a
  big JSON/dict literal in a `.py` file showed quoted text as variables once you scrolled or wrapped. Lines are now
  tokenized from their start (cached, so a 1 MB single-line file costs ~40 ms once and < 2 ms per frame), and multi-line
  strings and comments (`"""…"""`, `/* … */`, `@"…"`, template literals) stay coloured on every line. New bundled
  TextMate grammars for **Python** and **JSON**.
- **Find & Replace options you can see.** Match Case, Match Whole Word and Use Regular Expression are now proper
  toggles with a clear ON state, tooltips and VS Code's shortcuts (⌥⌘C / ⌥⌘W / ⌥⌘R on macOS, Alt+C / Alt+W / Alt+R
  elsewhere). New: **Preserve Case**, **Find in Selection**, Replace All with Ctrl/Cmd+Alt+Enter, and an "Invalid regex"
  message. Whole-word matching follows VS Code's rules (works for queries like `foo(`) in the editor and in Search.
- **Word Wrap in the menu.** View → Word Wrap (⌥Z / Alt+Z) is checkable and per editor; on macOS ⌥Z no longer types "Ω".
- **Ctrl+Tab switches editors** — from the editor, terminal, side bar or chat. Hold Ctrl and tap Tab to walk a
  "Recently used editors" list, release to open; a quick tap returns to the previous file. On macOS the key never reached
  the app before. Closing an editor now focuses the one you used last.
- **Code no longer paints over the line numbers** when the editor is scrolled horizontally.
- A suggestion list left open in one file can no longer insert into another after switching tabs.

## New

- **Path completion in imports** as you type: JS/TS `import` / `require` / `export … from`, CSS `@import` and `url()`,
  HTML `src`/`href`, C/C++ `#include "…"`, Markdown links. Relative paths, tsconfig/jsconfig aliases (`@/…`), npm
  packages; folders first and drill in as you go.
- **Python module completion** after `import` and `from`: workspace modules, relative imports, packages installed in the
  selected interpreter, the standard library, and names inside a module (`from x import …`, honouring `__all__`).
- **Complete menu bar, like VS Code's**: app menu (Settings ⌘,), File (Open Recent, Revert, Auto Save), Edit
  (Cut/Copy/Paste that work in any focused field, Find/Replace in Files, comments, Emmet), Selection, View (Appearance,
  Zoom, Minimap, Breadcrumbs, Sticky Scroll, panels), Go (Back/Forward, Switch Editor, Definition, References, Problems,
  Bracket), Run (breakpoints), Terminal (tasks), Window and Help. Check marks reflect live state; Windows has the same set.

## Trust and verification

Releases include GitHub build-provenance attestations and a Sigstore-signed `SHA256SUMS.txt`; Linux packages are also
OpenPGP-signed (`docs/codeLess-release-signing-key.asc`). See `docs/SIGNING.md`.

## Upgrade notes

- Windows SmartScreen and macOS Gatekeeper may still warn until code-signing certificates are in place.
