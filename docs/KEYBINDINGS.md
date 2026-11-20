# Keyboard Shortcuts & Selection Reference

On macOS, `Ctrl` shortcuts map to `⌘` (`Cmd`) and `Alt` maps to `⌥` (`Option`) unless noted otherwise (such as `` Ctrl+` `` for toggling the integrated terminal).

## 1. General

| Shortcut | Action | Description |
| :--- | :--- | :--- |
| `Ctrl+Shift+P`, `F1` | Show Command Palette | Opens Command Palette in `>` command mode |
| `Ctrl+P` | Quick Open | Fuzzy-searches workspace files |
| `Ctrl+Shift+N` | New Window | Opens a new `codeLess` instance |
| `Ctrl+Shift+W` | Close Window | Closes the workbench window |
| `Ctrl+,` | User Settings | Opens Preferences & BYOK AI Settings |
| `Ctrl+K Ctrl+S` | Keyboard Shortcuts | Opens searchable Keyboard Shortcuts reference |
| `Ctrl+K Ctrl+T` | Color Theme | Opens live-preview Color Theme picker |

## 2. Basic Editing

| Shortcut | Action | Description |
| :--- | :--- | :--- |
| `Ctrl+X` | Cut | Cuts selection, or cuts the entire current line when no text is selected |
| `Ctrl+C` | Copy | Copies selection, or copies the entire current line when no text is selected |
| `Alt+Up` / `Alt+Down` | Move Line Up / Down | Moves active line or selected block up or down |
| `Shift+Alt+Up` / `Shift+Alt+Down` | Copy Line Up / Down | Duplicates active line or selected block up or down |
| `Ctrl+Shift+K` | Delete Line | Deletes current line or selected lines |
| `Ctrl+Enter` | Insert Line Below | Inserts an indented blank line below the current line |
| `Ctrl+Shift+Enter` | Insert Line Above | Inserts an indented blank line above the current line |
| `Ctrl+Shift+\` | Jump to Bracket | Jumps cursor to matching `()`, `{}`, or `[]` delimiter |
| `Ctrl+]` / `Ctrl+[` | Indent / Outdent Line | Indents or outdents current line or multi-line selection |
| `Home` / `End` | Line Start / End | Smart `Home` toggles between first non-whitespace character and column 0 |
| `Ctrl+Home` / `Ctrl+End` | File Start / End | Moves cursor to beginning or end of document |
| `Ctrl+Up` / `Ctrl+Down` | Scroll Line Up / Down | Scrolls viewport by 1 line without moving the cursor |
| `Alt+PgUp` / `Alt+PgDn` | Scroll Page Up / Down | Scrolls viewport by 1 page without moving the cursor |
| `Ctrl+Shift+[` / `Ctrl+Shift+]` | Fold / Unfold Region | Folds or unfolds the enclosing code block |
| `Ctrl+K Ctrl+[` / `Ctrl+K Ctrl+]` | Fold / Unfold Subregions | Folds or unfolds nested subregions within the current block |
| `Ctrl+K Ctrl+0` / `Ctrl+K Ctrl+J` | Fold / Unfold All | Folds all regions (`Ctrl+K Ctrl+0`) or unfolds all regions (`Ctrl+K Ctrl+J`) |
| `Ctrl+K Ctrl+C` / `Ctrl+K Ctrl+U` | Add / Remove Line Comment | Explicitly comments or uncomments active line or selection |
| `Ctrl+/` | Toggle Line Comment | Toggles language-aware line comment (`//`, `#`, `<!-- -->`, `/* */`) |
| `Shift+Alt+A` | Toggle Block Comment | Toggles block comment around selection |
| `Alt+Z` | Toggle Word Wrap | Toggles visual word wrap |
| `Ctrl+Z` | Undo | Restores previous buffer state |
| `Ctrl+Y`, `Ctrl+Shift+Z` | Redo | Reapplies undone buffer state |
| `Ctrl+Backspace` / `Ctrl+Delete` | Delete Word Left / Right | Deletes word boundary to the left or right of each cursor |

## 3. Multi-Cursor & Selection

| Shortcut / Gesture | Action | Description |
| :--- | :--- | :--- |
| `Alt+Click` | Insert / Remove Cursor | Adds a secondary cursor at clicked position (or removes an existing one) |
| `Ctrl+Alt+Up` / `Ctrl+Alt+Down` | Add Cursor Above / Below | Inserts a secondary cursor on the line above or below |
| `Ctrl+U` | Cursor Undo | Restores previous cursor/selection state |
| `Shift+Alt+I` | Add Cursors to Line Ends | Places a cursor at the end of every selected line |
| `Ctrl+L` | Expand Line Selection | Selects current line; repeating extends selection line-by-line |
| `Ctrl+Shift+L` | Select All Occurrences | Places a cursor on every occurrence of the current selection |
| `Ctrl+F2` | Change All Occurrences | Selects all occurrences of the word under the cursor |
| `Shift+Alt+Right` / `Shift+Alt+Left` | Expand / Shrink Selection | Expands or shrinks selection across token, line, and AST block boundaries |
| `Shift+Alt+Drag`, `Middle-Drag` | Column (Box) Selection | Creates rectangular multi-cursor selection across dragged rows and columns |
| `Ctrl+Shift+Alt+Arrow` | Column Selection (Keys) | Extends rectangular box selection with arrow keys |
| `Ctrl+D` | Add Next Occurrence | Selects word under cursor, then adds next match on repeat |
| `Ctrl+K Ctrl+D` | Skip & Add Next Occurrence | Skips current match and selects the next occurrence |
| `Double-Click` / `Triple-Click` | Select Word / Line | Selects word or full line (drag to extend by words or lines) |
| `Gutter Click / Drag` | Line-Number Selection | Selects entire lines from the line-number margin |

## 4. Search & Replace

| Shortcut | Action | Description |
| :--- | :--- | :--- |
| `Ctrl+F` | Find | Opens inline Find bar |
| `Ctrl+H` | Replace | Opens inline Find & Replace bar |
| `F3` / `Shift+F3` | Find Next / Previous | Jumps to next or previous match |
| `Alt+Enter` | Select All Find Matches | Places cursors on all matches of the current Find query |
| `Alt+C` / `Alt+R` / `Alt+W` | Toggle Search Flags | Toggles Case Sensitive (`Aa`), Regex (`.*`), or Whole Word (`ab`) |
| `Ctrl+Shift+F` | Find in Files | Opens workspace-wide Search sidebar |
| `Ctrl+Shift+H` | Replace in Files | Opens workspace-wide Search & Replace sidebar |

## 5. Code Intelligence & AI Editing

| Shortcut | Action | Description |
| :--- | :--- | :--- |
| `Ctrl+Space` | Trigger Suggestions | Opens symbol and snippet autocomplete |
| `Ctrl+Shift+Space` | Parameter Hints | Displays signature/parameter tooltip |
| `Ctrl+I`, `Ctrl+K` | Inline AI Edit | Opens floating inline AI prompt anchored at cursor/selection |
| `Tab` / `Ctrl+Right` | Accept Ghost Text | Accepts full inline completion (`Tab`) or next word (`Ctrl+Right`) |
| `Shift+Alt+F` | Format Document | Formats the active document |
| `Ctrl+K Ctrl+F` | Format Selection | Formats only the selected line range |
| `F12`, `Ctrl+Click` | Go to Definition | Jumps to symbol definition across workspace |
| `Alt+F12` | Peek Definition | Opens inline Peek Definition preview |
| `Ctrl+K F12` | Open Definition to Side | Opens symbol definition in the split editor |
| `Shift+F12` | Find References | Lists workspace references to the symbol |
| `F2` | Rename Symbol | Whole-word symbol rename |
| `Ctrl+.` | Quick Fix | Triggers diagnostic quick-fix remediation |

## 6. Navigation, Tabs & Workbench

| Shortcut | Action | Description |
| :--- | :--- | :--- |
| `Ctrl+G` | Go to Line / Column | Jumps to `:line` or `:line:col` |
| `Ctrl+Shift+O` | Go to Symbol in File | Opens Command Palette with `@` document symbol filter |
| `Ctrl+T` | Go to Symbol in Workspace | Opens Command Palette with `#` workspace symbol filter |
| `Ctrl+Shift+M` | Show Problems | Opens bottom panel on the `PROBLEMS` tab |
| `F8` / `Shift+F8` | Next / Previous Problem | Jumps to next or previous diagnostic |
| `Alt+Left` / `Alt+Right` | Go Back / Forward | Navigates cursor location history |
| `Ctrl+N` | New Untitled File | Creates an in-memory `Untitled` tab |
| `Ctrl+O` | Open File | Opens native file dialog |
| `Ctrl+S` / `Ctrl+Shift+S` | Save / Save As | Saves active buffer preserving encoding, BOM, and line endings |
| `Ctrl+K S` | Save All | Saves all modified tabs |
| `Ctrl+W`, `Ctrl+F4` | Close Editor | Closes active tab (prompts if unsaved) |
| `Ctrl+K Ctrl+W` | Close All Editors | Closes all open tabs |
| `Ctrl+K U` | Close Saved Editors | Closes all unmodified tabs |
| `Ctrl+Shift+T` | Reopen Closed Editor | Reopens the most recently closed file tab |
| `Ctrl+\` | Split Editor | Toggles side-by-side split editor |
| `Ctrl+1` / `Ctrl+2` | Focus Editor Group | Focuses primary (`Ctrl+1`) or split (`Ctrl+2`) editor |
| `Ctrl+Tab` / `Ctrl+PageUp` / `Ctrl+PageDown` | Switch Tabs | Cycles through open editor tabs |
| `Ctrl+B` / `Ctrl+Alt+B` | Toggle Sidebars | Toggles primary left sidebar (`Ctrl+B`) or right AI dock (`Ctrl+Alt+B`) |
| `Ctrl+Shift+E` / `F` / `G` / `D` / `X` | Switch Sidebar View | Opens Explorer, Search, Source Control, Run, or Extensions |
| `Ctrl+=` / `Ctrl+-` / `Ctrl+Numpad0` | Zoom In / Out / Reset | Adjusts and persists editor font size |
| `F11` / `Ctrl+K Z` | Full Screen / Zen Mode | Toggles full screen (`F11`) or distraction-free Zen Mode (`Ctrl+K Z`) |

## 7. Run, Tasks & Integrated Terminal

| Shortcut | Action | Description |
| :--- | :--- | :--- |
| `F9` | Toggle Breakpoint | Toggles gutter breakpoint on current line |
| `F5`, `Ctrl+F5` | Run Active File | Executes active file in the integrated terminal |
| `Ctrl+Shift+B` | Run Build Task | Runs workspace build task |
| `` Ctrl+` `` | Toggle Terminal | Shows or hides the integrated terminal panel |
| `` Ctrl+Shift+` `` | New Terminal | Spawns a new PTY shell session in the workspace directory |
| `Ctrl+Shift+5` | Split Terminal | Splits the terminal panel side-by-side |
| `Ctrl+Shift+C` / `Ctrl+Shift+V` | Terminal Copy / Paste | Copies terminal selection or pastes clipboard into active PTY |
| `Ctrl+PageUp` / `Ctrl+PageDown` | Switch Terminal Session | Switches between open terminal sessions when terminal is focused |
