# Cross-Cutting QA & Bug Hunt Report (W7)

This report documents the cross-cutting audit of `codeLess` across all merged tracks (`win`, `keys`, `harness`, `benchqa`, `editorqa`, `mem` on `integ/main`), including audit truthfulness verification, full shortcut sweep, feature interaction conflict resolution, multi-instance isolation, and post-merge regression verification.

---

## Issues Found & Fixed

| ID | Issue | Severity | Root Cause | Files Changed | Verifying Test |
|----|-------|----------|------------|---------------|----------------|
| **QA-W7-01** | Pressing `Cmd+K` / `Ctrl+K` while the integrated terminal has focus is swallowed as a two-key chord prefix (`(cmd+k) was pressed. Waiting for second key of chord...`) instead of reaching `AvaloniaTerminalEmulatorControl` to clear the terminal on macOS or send to the shell | **High** | `KeybindingManager.Dispatch` checked `if (HasChordPrefix(keyGesture))` without checking `!isTerminalFocus`, intercepting `cmd+k` / `ctrl+k` during Tunnel routing before the terminal control saw the keystroke | `src/codeLess.Core/KeybindingManager.cs:444` | `CrossCuttingQaW7Tests.InteractionConflicts_TabPerContext_CmdKInTerminalVsEditor_CmdIInlineAi_AndEscapePriority` |
| **QA-W7-02** | Pressing `Tab` / `Shift+Tab` inside the open Command Palette / Quick Open moves focus out of `SearchInput` instead of cycling palette list items | **Medium** | `AvaloniaCommandPaletteOverlay` handled `Up`/`Down`/`Ctrl+P` in `OnSearchKeyDown`, but Avalonia's focus manager intercepted `Key.Tab` for tab navigation before bubbling | `src/codeLess.Avalonia/AvaloniaStudioDialogs.cs:107` | `CrossCuttingQaW7Tests.InteractionConflicts_TabPerContext_CmdKInTerminalVsEditor_CmdIInlineAi_AndEscapePriority` |
| **QA-W7-03** | `Escape` priority order in the editor did not close completion when `session == null` and did not cascade through `suggest -> find widget -> multi-cursor -> selection -> panel` | **High** | `MainWindow.LanguageServers.cs:805` guarded `Completion.IsVisible` with `session != null`, and `AvaloniaEditorCanvas.OnKeyDown` did not clear `buf.ExtraCarets` or close the bottom panel when all editor overlays/selections were already dismissed | `src/codeLess.Avalonia/MainWindow.LanguageServers.cs:835`, `src/codeLess.Avalonia/AvaloniaEditorCanvas.cs:4292` | `CrossCuttingQaW7Tests.InteractionConflicts_TabPerContext_CmdKInTerminalVsEditor_CmdIInlineAi_AndEscapePriority` |
| **QA-W7-04** | WinForms `CodeLessMainForm` did not wire `LspDiagnosticsProvider` onto `aiEngine.DiagnosticsProvider`, leaving M7b post-edit LSP diagnostics active only in Avalonia | **High** | `LspDiagnosticsProvider` was defined only in `src/codeLess.Avalonia/LspDiagnosticsProvider.cs` (`codeLess.AvaloniaApp`) and never assigned in `CodeLessMainForm` | `src/codeLess.Core/IDiagnosticsProvider.cs:13`, `src/codeLess.Avalonia/LspDiagnosticsProvider.cs:5`, `src/CodeLessMainForm.cs:345` | `CrossCuttingQaW7Tests.TruthfulnessAudit_AllThreeAuditDocs_EveryRowHasValidFileLineAndWiredInBothUis` |
| **QA-W7-05** | WinForms `CodeLessMainForm` did not instantiate `KeybindingManager` or `KeybindingsFileWatcher` or dispatch custom `keybindings.json` bindings in `ProcessCmdKey` | **High** | Keybinding engine wiring was added to `MainWindow.Keybindings.cs` (Avalonia) without a corresponding `InitKeybindingsWin()` / `DispatchKeybindingWin()` hook in WinForms | `src/CodeLessMainForm.cs:431`, `src/CodeLessMainForm.cs:12247`, `src/CodeLessMainForm.Debug.cs:1031` | `CrossCuttingQaW7Tests.TruthfulnessAudit_AllThreeAuditDocs_EveryRowHasValidFileLineAndWiredInBothUis` |
| **QA-W7-06** | WinForms parity gaps against `docs/WORKBENCH_PARITY.md`: `CodeLessExplorerTree` lacked `GetCompactDisplayText`, `ApplyTypeFilter`, and `ProblemCountByFullPath` badge rendering; `CodeLessMainForm` lacked `DismissSearchRowWin` (`Delete` key in search results), `ReplaceSingleSearchMatchWin`, `SetProblemsFilter`, and `SuppressedNotificationIds` (`"Do Not Show Again"`) | **Medium** | W6 wired these helpers in `codeLess.Core` and Avalonia first; WinForms needed direct callers in `CodeLessExplorerTree` and `CodeLessMainForm` | `src/CoreModelsAndThemes.cs:950`, `src/CodeLessMainForm.cs:4348`, `src/CodeLessMainForm.cs:4674`, `src/CodeLessMainForm.cs:7416`, `src/CodeLessMainForm.cs:12208` | `CrossCuttingQaW7Tests.TruthfulnessAudit_AllThreeAuditDocs_EveryRowHasValidFileLineAndWiredInBothUis` |
| **QA-W7-07** | Bundled rows and post-merge `File:Line` shifts across `docs/HARNESS_SPEC_AUDIT.md`, `docs/WORKBENCH_PARITY.md`, and `docs/KEYBINDING_AUDIT.md` | **Medium** | Merging `editorqa` and `mem` shifted line numbers in `src/LlmHarnessAndAgentEngine.cs` and `src/codeLess.Avalonia/MainWindow*.cs`; several audit rows bundled multiple behaviours or lacked `File:Line` / `Test Name` columns | `docs/HARNESS_SPEC_AUDIT.md`, `docs/WORKBENCH_PARITY.md`, `docs/KEYBINDING_AUDIT.md` | `CrossCuttingQaW7Tests.TruthfulnessAudit_AllThreeAuditDocs_EveryRowHasValidFileLineAndWiredInBothUis` |

---

## Audit & Verification Summary

1. **Truthfulness Audit (`docs/WORKBENCH_PARITY.md`, `docs/HARNESS_SPEC_AUDIT.md`, `docs/KEYBINDING_AUDIT.md`)**:
   - Every bundled row was split into atomic single-behaviour rows (`41` rows in `WORKBENCH_PARITY.md`, `66` rows in `HARNESS_SPEC_AUDIT.md`, `105` unique command rows in `KEYBINDING_AUDIT.md`).
   - All three audit documents pass `/tmp/codeless-work/tools/check_audit.sh` (`AUDIT CHECK: OK`) and `CrossCuttingQaW7Tests.TruthfulnessAudit_AllThreeAuditDocs_EveryRowHasValidFileLineAndWiredInBothUis`.
2. **Shortcut Sweep**:
   - Verified all commands in `DefaultCommands` and `WorkbenchMenuModel` across macOS (`⌘`) and Windows/Linux (`Ctrl`) keymaps, including two-key chords (`Cmd/Ctrl+K ...`), confirming execution in the target context without leaking characters into the editor or chat input.
3. **Feature Interaction Matrix**:
   - `Tab` in `ChatPromptInput` toggles `Build`/`Plan` mode without indenting the editor; `Tab` in `CommandPalette` cycles items without leaving the search box; `Tab` in the editor indents without changing agent mode.
   - `Cmd/Ctrl+K` in the focused terminal does not enter editor chord mode; `Cmd/Ctrl+K` in the editor enters chord mode (`Waiting for second key of chord...`).
   - `Cmd/Ctrl+I` opens the Inline AI bar in the editor.
   - `Escape` follows the strict priority cascade: `Suggest (Completion) -> Find Widget -> Inline AI Bar -> Multi-Cursor -> Selection -> Bottom Panel`.
4. **Live Settings, Keybindings & Multi-Instance Isolation**:
   - Per-workspace `.vscode/settings.json` overrides apply only to the owning window, while user `settings.json` and `keybindings.json` apply live across windows.
   - Second-instance CLI flags (`-n`, `-r`, `-g file:line:col`, `-d`, `-a`, `-w`), per-window isolation (`HarnessEngine`, `KeybindingManager`, `TerminalSessions`, file watchers), and dirty-close confirmation prompts (`Save` / `Don't Save` / `Cancel`) verified end-to-end.
