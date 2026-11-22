using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Tasks (Run Task..., Run Build Task Ctrl/Cmd+Shift+B, Run Test Task, Terminate Task, Rerun Last Task) running in
    /// named integrated terminals with problem matchers feeding the Problems panel and editor squiggles, plus save
    /// participants (format on save via the language server, trim trailing whitespace, final newlines) and the
    /// files.autoSave modes (afterDelay / onFocusChange / onWindowChange). Shared logic: TaskConfiguration,
    /// ProblemMatcherEngine, TaskExecution, SaveActions (codeLess.Core).
    /// </summary>
    public partial class MainWindow
    {
        private sealed class RunningTask
        {
            public TaskExecution Exec;
            public TerminalSessionEntry Terminal;
            public TaskPipeProcess Pipe;
        }

        private readonly List<RunningTask> runningTasks = new List<RunningTask>();
        private TaskDefinition lastRunTask;
        private DispatcherTimer taskProblemsDebounce;
        private DispatcherTimer autoSaveTimer;
        private readonly HashSet<EditorTabBuffer> pendingAutoSave = new HashSet<EditorTabBuffer>();
        private int autoSaveDepth;
        private readonly List<DiagnosticItem> problemsPanelExtra = new List<DiagnosticItem>();

        public List<PaletteEntryItem> LastTaskPickerEntries { get; private set; } = new List<PaletteEntryItem>();
        public TaskExecution LastTaskExecution { get; private set; }
        public IReadOnlyList<TaskExecution> RunningTaskExecutions => runningTasks.Select(r => r.Exec).ToList();
        public IReadOnlyList<DiagnosticItem> ProblemsPanelTaskItems => problemsPanelExtra;

        private static bool IsWindowsHost => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private void InitializeTasksAndSaveParity()
        {
            taskProblemsDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            taskProblemsDebounce.Tick += (s, e) => { taskProblemsDebounce.Stop(); ApplyTaskProblemsToUi(); };
            autoSaveTimer = new DispatcherTimer();
            autoSaveTimer.Tick += (s, e) => { autoSaveTimer.Stop(); FlushPendingAutoSave(); };
            Opened += (s, e) => TaskProblemStore.Changed += OnTaskProblemsChanged;
            Closed += (s, e) =>
            {
                TaskProblemStore.Changed -= OnTaskProblemsChanged;
                foreach (var rt in runningTasks.ToList()) { try { rt.Exec.Terminate?.Invoke(); } catch { } }
            };
            Deactivated += (s, e) => { if (settings.FocusChangeSavesFiles) SaveAllDirtyFileBuffers(autoSave: true); };
        }

        // ------------------------------------------------------------------------------------------------
        // Task discovery and pickers
        // ------------------------------------------------------------------------------------------------

        public List<TaskDefinition> LoadWorkspaceTasks()
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return new List<TaskDefinition>();
            var tasks = TaskConfiguration.LoadAll(workspaceRoot, out string err);
            if (err != null) { SetStatusMessage(err); AppendOutputLog("[Tasks] " + err); }
            return tasks.Where(t => !t.Hide).ToList();
        }

        public void OpenRunTaskPicker() => ShowTaskPicker(LoadWorkspaceTasks(), "Select the task to run");

        private void ShowTaskPicker(List<TaskDefinition> tasks, string hint)
        {
            var entries = new List<PaletteEntryItem>();
            foreach (var t in tasks)
            {
                var task = t;
                entries.Add(new PaletteEntryItem
                {
                    Id = "task:" + task.DisplayLabel,
                    Category = task.Source == "Workspace" ? (task.Group == "build" ? "Build" : task.Group == "test" ? "Test" : "Task") : task.Source,
                    Title = task.Label,
                    stringSubtitleOrPath = string.IsNullOrEmpty(task.Detail) ? task.Command : task.Detail,
                    Execute = () => RunTask(task)
                });
            }
            entries.Add(new PaletteEntryItem { Category = "Tasks", Title = File.Exists(TaskConfiguration.TasksJsonPath(workspaceRoot) ?? "") ? "Open tasks.json" : "Configure Tasks (create tasks.json)", Execute = ConfigureTasksJson });
            LastTaskPickerEntries = entries;
            if (tasks.Count == 0) SetStatusMessage("No tasks found. Add .vscode/tasks.json or a package.json / Makefile / project file.");
            else SetStatusMessage(hint);
            CommandPalette.Open(">", entries, currentTheme?.Id);
        }

        /// <summary>Ctrl/Cmd+Shift+B: the default build task, else the build group (picker when several), else all tasks.</summary>
        public void RunBuildTask() => RunGroupTask("build");

        public void RunTestTask() => RunGroupTask("test");

        private void RunGroupTask(string group)
        {
            var tasks = LoadWorkspaceTasks();
            var def = TaskConfiguration.FindDefault(tasks, group);
            if (def != null) { RunTask(def); return; }
            var inGroup = TaskConfiguration.InGroup(tasks, group);
            if (inGroup.Count > 0) { ShowTaskPicker(inGroup, "Select the " + group + " task to run"); return; }
            ShowTaskPicker(tasks, "No " + group + " task found. Select a task to run");
        }

        public void RerunLastTask()
        {
            if (lastRunTask == null) { SetStatusMessage("No task has been run yet."); return; }
            RunTask(lastRunTask);
        }

        public void ConfigureTasksJson()
        {
            string path = TaskConfiguration.TasksJsonPath(workspaceRoot);
            if (path == null) { SetStatusMessage("Open a folder first."); return; }
            if (!File.Exists(path))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path,
                        "{\n  // See https://code.visualstudio.com/docs/editor/tasks for the tasks.json format\n  \"version\": \"2.0.0\",\n  \"tasks\": [\n    {\n      \"label\": \"build\",\n      \"type\": \"shell\",\n      \"command\": \"echo Hello\",\n      \"group\": { \"kind\": \"build\", \"isDefault\": true },\n      \"problemMatcher\": []\n    }\n  ]\n}\n");
                }
                catch (Exception ex) { SetStatusMessage("Could not create tasks.json: " + ex.Message); return; }
            }
            OpenFileInTab(path, preview: false);
        }

        // ------------------------------------------------------------------------------------------------
        // Running
        // ------------------------------------------------------------------------------------------------

        public TaskExecution RunTask(TaskDefinition task) => RunTask(task, new HashSet<string>(StringComparer.Ordinal));

        private TaskExecution RunTask(TaskDefinition task, HashSet<string> visiting)
        {
            if (task == null) return null;
            if (string.IsNullOrEmpty(workspaceRoot)) { SetStatusMessage("Open a folder to run tasks."); return null; }
            // task.saveBeforeRun "always" (VS Code default): save modified files first.
            SaveAllDirtyFileBuffers(autoSave: false);

            // dependsOn: run dependencies first (sequentially), then this task.
            if (task.DependsOn.Count > 0 && visiting.Add(task.Label))
            {
                var all = LoadWorkspaceTasks();
                var deps = task.DependsOn.Select(l => TaskConfiguration.FindByLabel(all, l)).Where(t => t != null).ToList();
                RunDependencyChain(deps, 0, task, visiting);
                return null;
            }
            return StartTask(task);
        }

        private void RunDependencyChain(List<TaskDefinition> deps, int index, TaskDefinition final, HashSet<string> visiting)
        {
            if (index >= deps.Count) { if (!string.IsNullOrEmpty(final.Command)) StartTask(final); return; }
            var exec = RunTask(deps[index], visiting);
            if (exec == null) { RunDependencyChain(deps, index + 1, final, visiting); return; }
            exec.Exited += ex => PostUi(() =>
            {
                if (ex.ExitCode == 0) RunDependencyChain(deps, index + 1, final, visiting);
                else SetStatusMessage("Task '" + final.Label + "' not started: dependency '" + deps[index].Label + "' failed.");
            });
        }

        private TaskExecution StartTask(TaskDefinition task)
        {
            lastRunTask = task;
            // A second run of the same task restarts it (VS Code offers Terminate/Restart).
            foreach (var running in runningTasks.Where(r => r.Exec.Task.DisplayLabel == task.DisplayLabel).ToList())
                TerminateRunningTask(running);

            var ctx = new DebugVariableContext
            {
                WorkspaceFolder = workspaceRoot,
                File = activeTab?.FilePath,
                LineNumber = activeTab != null ? activeTab.CursorLine + 1 : 0,
                SelectedText = activeTab != null && activeTab.HasSelection() ? activeTab.GetSelectedText() : "",
                PythonInterpreter = PythonInterpreterService.GetSelected(workspaceRoot)?.Path
            };
            var exec = TaskExecution.Prepare(task, ctx, IsWindowsHost);
            LastTaskExecution = exec;
            var rt = new RunningTask { Exec = exec };
            runningTasks.Add(rt);
            exec.MarkStarted();
            exec.Exited += ex => PostUi(() => OnTaskExited(rt));
            SetStatusMessage("Running task '" + task.Label + "'…");
            AppendOutputLog("[Tasks] " + task.DisplayLabel + ": " + exec.CommandLine + "  (cwd " + exec.Cwd + ")");

            SwitchBottomTab("terminal");
            var term = GetTaskTerminal(task, exec.Cwd);
            rt.Terminal = term;
            string header = "\x1b[1m> Executing task: " + exec.CommandLine + " <\x1b[0m\r\n\r\n";
            string termCmd = TaskConfiguration.BuildTerminalCommandLine(exec.CommandLine, exec.Env, IsWindowsHost, IsWindowsHost ? null : ConPtyTerminalSession.GetUserUnixShell());
            bool started = false;
            try { started = term.Control.StartShell(exec.Cwd, termCmd); } catch { started = false; }
            var session = term.Control.Session;
            if (started && session != null)
            {
                session.RawOutputTap = exec.OnOutputBytes;
                try { session.Buffer.ProcessText(header); } catch { }
                session.ProcessExited += code => exec.OnExited(code);
                exec.Terminate = () => { try { term.Control.StopShell(notifyExit: false); } catch { } exec.WasTerminated = true; exec.OnExited(-1); };
                if (!session.IsRunning) exec.OnExited(session.ExitCode);
            }
            else
            {
                // Headless / no PTY: run with pipes and mirror the output into the terminal view.
                term.Control.SeedInitialPrompt(header);
                var pipe = new TaskPipeProcess();
                rt.Pipe = pipe;
                var ctrl = term.Control;
                exec.Terminate = () => { exec.WasTerminated = true; pipe.Terminate(); };
                pipe.Start(exec.CommandLine, exec.Cwd, exec.Env, IsWindowsHost,
                    chunk =>
                    {
                        exec.OnOutputText(chunk);
                        PostUi(() => ctrl.SeedInitialPrompt(chunk.Replace("\r\n", "\n").Replace("\n", "\r\n")));
                    },
                    code =>
                    {
                        PostUi(() => ctrl.SeedInitialPrompt("\r\n\x1b[90m[process exited with code " + code + "]\x1b[0m\r\n"));
                        exec.OnExited(code);
                    });
            }
            return exec;
        }

        /// <summary>A terminal named after the task, reused when its previous run has finished ("shared" panel).</summary>
        private TerminalSessionEntry GetTaskTerminal(TaskDefinition task, string cwd)
        {
            string title = "Task - " + task.Label;
            bool reusable = task.Panel != "new";
            var existing = reusable ? terminalSessions.FirstOrDefault(t => t.TaskLabel == task.DisplayLabel && !runningTasks.Any(r => ReferenceEquals(r.Terminal, t) && r.Exec.IsRunning)) : null;
            if (existing != null)
            {
                ActivateTerminalSession(existing);
                return existing;
            }
            var termCtrl = new AvaloniaTerminalEmulatorControl();
            if (currentTheme != null) termCtrl.ApplyTheme(currentTheme);
            var entry = new TerminalSessionEntry { Title = title, ShellCommand = "task", Control = termCtrl, KeepOpenOnExit = true, TaskLabel = task.DisplayLabel };
            termCtrl.WorkspaceRootProvider = () => workspaceRoot;
            termCtrl.FileLinkActivated += (path, line, col) =>
            {
                if (OpenFileInTab(path, preview: false) != null && line > 0) PrimaryEditor.GoToLine(line, Math.Max(1, col));
            };
            terminalSessions.Add(entry);
            ActivateTerminalSession(entry);
            return entry;
        }

        private void OnTaskExited(RunningTask rt)
        {
            runningTasks.Remove(rt);
            var ex = rt.Exec;
            string label = ex.Task.Label;
            int errs = ex.ErrorCount, warns = ex.WarningCount;
            string probs = ex.Matchers.HasMatchers ? "  (" + errs + " error" + (errs == 1 ? "" : "s") + ", " + warns + " warning" + (warns == 1 ? "" : "s") + ")" : "";
            if (ex.WasTerminated) SetStatusMessage("Task '" + label + "' terminated.");
            else if (ex.ExitCode == 0) SetStatusMessage("Task '" + label + "' finished" + probs + ".");
            else SetStatusMessage("Task '" + label + "' failed with exit code " + ex.ExitCode + probs + ".");
            try
            {
                var session = rt.Terminal?.Control?.Session;
                if (session != null) session.Buffer.ProcessText("\r\n\x1b[90m *  Terminal will be reused by tasks.\x1b[0m\r\n");
                rt.Terminal?.Control?.InvalidateVisual();
            }
            catch { }
            ApplyTaskProblemsToUi();
        }

        public void TerminateTask()
        {
            var live = runningTasks.Where(r => r.Exec.IsRunning).ToList();
            if (live.Count == 0) { SetStatusMessage("No task is running."); return; }
            if (live.Count == 1) { TerminateRunningTask(live[0]); return; }
            var entries = live.Select(r => new PaletteEntryItem { Category = "Terminate", Title = r.Exec.Task.DisplayLabel, Execute = () => TerminateRunningTask(r) }).ToList();
            entries.Insert(0, new PaletteEntryItem { Category = "Terminate", Title = "All Running Tasks", Execute = () => { foreach (var r in live) TerminateRunningTask(r); } });
            CommandPalette.Open(">", entries, currentTheme?.Id);
        }

        private void TerminateRunningTask(RunningTask rt)
        {
            try { rt.Exec.Terminate?.Invoke(); } catch { }
            runningTasks.Remove(rt);
        }

        // ------------------------------------------------------------------------------------------------
        // Problems from tasks
        // ------------------------------------------------------------------------------------------------

        private void OnTaskProblemsChanged()
        {
            PostUi(() => { taskProblemsDebounce?.Stop(); taskProblemsDebounce?.Start(); });
        }

        /// <summary>Re-merges task problems into open buffers (squiggles) and refreshes the Problems panel now.</summary>
        public void ApplyTaskProblemsToUi()
        {
            foreach (var t in openTabs)
            {
                if (t == null || string.IsNullOrEmpty(t.FilePath) || t.IsLargeFileViewer) continue;
                LspDiagnosticsStore.MergeInto(t);
            }
            RefreshOutlineAndProblems(activeTab);
            PrimaryEditor?.Canvas?.InvalidateVisual();
            if (isEditorSplit) SplitEditor?.Canvas?.InvalidateVisual();
        }

        /// <summary>Problems panel hook: task problems of files other than the active editor.</summary>
        private void AppendTaskProblemsForOtherFiles(List<Control> items, EditorTabBuffer tab, ref int errCount, ref int warnCount)
        {
            problemsPanelExtra.Clear();
            string activeKey = tab != null && !string.IsNullOrEmpty(tab.FilePath) ? LspText.PathKey(tab.FilePath) : null;
            foreach (var d in TaskProblemStore.All())
            {
                if (activeKey != null && LspText.PathKey(d.FilePath) == activeKey) continue;
                bool isErr = string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase);
                if (isErr) errCount++; else warnCount++;
                problemsPanelExtra.Add(d);
                string rel = !string.IsNullOrEmpty(d.RelativePath) ? d.RelativePath : Path.GetFileName(d.FilePath);
                items.Add(new LiteralTextBlock((isErr ? "⊗" : "△") + " [" + d.Severity + "] " + rel + ":" + d.Line + ":" + d.Column + " — " + d.Message + "  (" + d.Source + ")")
                {
                    FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(isErr ? Color.FromRgb(248, 113, 113) : Color.FromRgb(250, 204, 21))
                });
            }
        }

        private bool TryNavigateTaskProblem(int extraIndex)
        {
            if (extraIndex < 0 || extraIndex >= problemsPanelExtra.Count) return false;
            var d = problemsPanelExtra[extraIndex];
            if (string.IsNullOrEmpty(d.FilePath) || !File.Exists(d.FilePath)) { SetStatusMessage(d.FilePath + " not found."); return true; }
            Dispatcher.UIThread.Post(() => OpenFileAtLocation(d.FilePath, Math.Max(1, d.Line), Math.Max(1, d.Column), 0, preview: false, focusEditor: true));
            return true;
        }

        // ------------------------------------------------------------------------------------------------
        // Save participants and auto save
        // ------------------------------------------------------------------------------------------------

        /// <summary>Hook in SaveBufferToDisk: trim trailing whitespace / final newlines before writing.</summary>
        private void ApplySaveParticipants(EditorTabBuffer tab)
        {
            if (tab == null || tab.IsLargeFileViewer) return;
            var opts = SaveActionOptions.FromSettings(settings, autoSaveDepth > 0);
            if (!opts.Any) return;
            if (SaveActions.ApplyToBuffer(tab, opts))
            {
                if (ReferenceEquals(PrimaryEditor?.CurrentBuffer, tab)) PrimaryEditor.Canvas.InvalidateVisual();
                if (isEditorSplit && ReferenceEquals(SplitEditor?.CurrentBuffer, tab)) SplitEditor.Canvas.InvalidateVisual();
            }
        }

        /// <summary>
        /// Hook in SaveActiveTab: editor.formatOnSave with a running language server formats first (1.5 s budget, like
        /// VS Code's formatOnSaveTimeout), then saves. Returns true when the save was taken over.
        /// </summary>
        private bool TryFormatThenSave(EditorTabBuffer tab)
        {
            if (tab == null || !settings.FormatOnSave || tab.IsLargeFileViewer || string.IsNullOrEmpty(tab.FilePath)) return false;
            string path = LspPathFor(tab);
            if (path == null || lsp == null || !lsp.IsActive(path)) return false;
            _ = FormatThenSaveAsync(tab, path);
            return true;
        }

        private async Task FormatThenSaveAsync(EditorTabBuffer tab, string path)
        {
            string before = LspEditing.BufferText(tab);
            List<LspTextEdit> edits = null;
            try
            {
                using (var cts = new CancellationTokenSource(1500))
                {
                    int tabSize = tab.CustomTabSize.HasValue && tab.CustomTabSize.Value > 0 ? tab.CustomTabSize.Value : Math.Max(1, settings.TabSize);
                    bool spaces = tab.CustomInsertSpaces ?? settings.InsertSpaces;
                    var req = lsp.RequestFormattingAsync(path, null, tabSize, spaces, cts.Token);
                    var done = await Task.WhenAny(req, Task.Delay(1600));
                    if (done == req) edits = req.Result;
                }
            }
            catch { edits = null; }
            if (edits != null && edits.Count > 0 && LspEditing.BufferText(tab) == before)
            {
                if (LspEditing.ApplyEditsToBuffer(tab, edits, true) && ReferenceEquals(PrimaryEditor.CurrentBuffer, tab))
                    PrimaryEditor.Canvas.CommitExternalEdit();
            }
            SaveBufferToDisk(tab);
        }

        /// <summary>files.autoSave = afterDelay: called on edits; saves after files.autoSaveDelay of inactivity.</summary>
        private void ScheduleAutoSave(EditorTabBuffer buf)
        {
            if (buf == null || string.IsNullOrEmpty(buf.FilePath) || !File.Exists(buf.FilePath)) return;
            pendingAutoSave.Add(buf);
            if (autoSaveTimer == null) { FlushPendingAutoSave(); return; }
            autoSaveTimer.Stop();
            autoSaveTimer.Interval = TimeSpan.FromMilliseconds(settings.EffectiveAutoSaveDelayMs);
            autoSaveTimer.Start();
        }

        /// <summary>Saves buffers waiting for the auto-save delay now (also used by tests: DispatcherTimer doesn't tick headless).</summary>
        public void FlushPendingAutoSave()
        {
            var list = pendingAutoSave.ToList();
            pendingAutoSave.Clear();
            foreach (var b in list)
            {
                if (b.IsDirty && openTabs.Contains(b) && !string.IsNullOrEmpty(b.FilePath) && File.Exists(b.FilePath)) AutoSaveBuffer(b);
            }
        }

        private void AutoSaveBuffer(EditorTabBuffer b)
        {
            autoSaveDepth++;
            try { SaveBufferToDisk(b); }
            finally { autoSaveDepth--; }
        }

        private void SaveAllDirtyFileBuffers(bool autoSave)
        {
            foreach (var t in openTabs.ToList())
            {
                if (t == null || !t.IsDirty || t.IsUntitled || t.IsLargeFileViewer || string.IsNullOrEmpty(t.FilePath) || !File.Exists(t.FilePath)) continue;
                if (autoSave) AutoSaveBuffer(t); else SaveBufferToDisk(t);
            }
        }

        /// <summary>Hook in ActivateTab: files.autoSave = onFocusChange saves the editor being left.</summary>
        private void OnEditorFocusLeaving(EditorTabBuffer leaving, EditorTabBuffer next)
        {
            if (leaving == null || ReferenceEquals(leaving, next) || !settings.FocusChangeSavesFiles || settings.EffectiveAutoSaveMode != IdePersistentSettings.AutoSaveOnFocusChange) return;
            if (leaving.IsDirty && !leaving.IsUntitled && !string.IsNullOrEmpty(leaving.FilePath) && File.Exists(leaving.FilePath) && openTabs.Contains(leaving))
                AutoSaveBuffer(leaving);
        }

        private List<PaletteEntryItem> BuildTaskPaletteCommands()
        {
            return new List<PaletteEntryItem>
            {
                new PaletteEntryItem { Category = "Tasks", Title = "Run Task...", Execute = OpenRunTaskPicker },
                new PaletteEntryItem { Category = "Tasks", Title = "Run Build Task", Shortcut = "Ctrl+Shift+B", Execute = RunBuildTask },
                new PaletteEntryItem { Category = "Tasks", Title = "Run Test Task", Execute = RunTestTask },
                new PaletteEntryItem { Category = "Tasks", Title = "Rerun Last Task", Execute = RerunLastTask },
                new PaletteEntryItem { Category = "Tasks", Title = "Terminate Task", Execute = TerminateTask },
                new PaletteEntryItem { Category = "Tasks", Title = "Configure Tasks...", Execute = ConfigureTasksJson },
                new PaletteEntryItem { Category = "Problems", Title = "Clear Task Problems", Execute = () => { TaskProblemStore.ClearAll(); ApplyTaskProblemsToUi(); } },
                new PaletteEntryItem { Category = "Files", Title = "Toggle Auto Save (afterDelay)", Execute = () => { settings.SetAutoSaveMode(settings.EffectiveAutoSaveMode == IdePersistentSettings.AutoSaveOff ? IdePersistentSettings.AutoSaveAfterDelay : IdePersistentSettings.AutoSaveOff); try { settings.Save(); } catch { } SetStatusMessage("Auto Save: " + settings.EffectiveAutoSaveMode); } },
            };
        }
    }
}
