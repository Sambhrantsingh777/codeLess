using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    internal sealed class TempDir : IDisposable
    {
        public readonly string Path;
        public TempDir()
        {
            Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codeLess_tl_" + Guid.NewGuid().ToString("N").Substring(0, 8)));
            Directory.CreateDirectory(Path);
            TestTrust.Trust(Path); // workspace-local tools / git / tasks need a trusted folder
        }
        public string File(string rel, string content = "x\n")
        {
            // normalized (platform separators) so expectations match what the product returns on Windows too
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
            System.IO.File.WriteAllText(full, content);
            return full;
        }
        public string Venv(string rel, params string[] scripts)
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            File(System.IO.Path.Combine(rel, "pyvenv.cfg"), "home = /usr/bin\n");
            foreach (string s in scripts) File(System.IO.Path.Combine(rel, s));
            return dir;
        }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    public class PythonVenvActivatorTests
    {
        [Fact]
        public void FindVenv_RequiresPyvenvCfg_AndPrefersDotVenv()
        {
            using (var t = new TempDir())
            {
                Directory.CreateDirectory(Path.Combine(t.Path, ".venv")); // no pyvenv.cfg: not a venv
                Assert.Null(PythonVenvActivator.FindVenvInFolder(t.Path));
                string venv = t.Venv("venv");
                Assert.Equal(venv, PythonVenvActivator.FindVenvInFolder(t.Path));
                string dotVenv = t.Venv(".venv");
                Assert.Equal(dotVenv, PythonVenvActivator.FindVenvInFolder(t.Path));
            }
        }

        [Fact]
        public void FindWorkspaceVenv_WalksUpToRoot_AndIgnoresCwdOutsideWorkspace()
        {
            using (var t = new TempDir())
            using (var other = new TempDir())
            {
                string root = Path.Combine(t.Path, "proj");
                string rootVenv = t.Venv(Path.Combine("proj", ".venv"));
                string sub = Path.Combine(root, "pkg", "inner");
                Directory.CreateDirectory(sub);

                Assert.Equal(rootVenv, PythonVenvActivator.FindWorkspaceVenv(root, null));
                Assert.Equal(rootVenv, PythonVenvActivator.FindWorkspaceVenv(root, sub));
                string nested = t.Venv(Path.Combine("proj", "pkg", "env"));
                Assert.Equal(nested, PythonVenvActivator.FindWorkspaceVenv(root, sub)); // nearest wins
                Assert.Null(PythonVenvActivator.FindWorkspaceVenv(root, other.Path));
                Assert.Null(PythonVenvActivator.FindWorkspaceVenv("", sub));
                // A sibling folder sharing the root's name prefix is not "inside" it.
                Directory.CreateDirectory(root + "-other");
                Assert.Null(PythonVenvActivator.FindWorkspaceVenv(root, root + "-other"));
            }
        }

        [Fact]
        public void DetectShellFlavor_FromExecutablesAndCommandLines()
        {
            Assert.Equal(TerminalShellFlavor.Posix, PythonVenvActivator.DetectShellFlavor("/bin/zsh", null));
            Assert.Equal(TerminalShellFlavor.Posix, PythonVenvActivator.DetectShellFlavor("/bin/bash -l", "bash"));
            Assert.Equal(TerminalShellFlavor.Posix, PythonVenvActivator.DetectShellFlavor("\"C:\\Program Files\\Git\\bin\\bash.exe\" --login -i", "Git Bash"));
            Assert.Equal(TerminalShellFlavor.Fish, PythonVenvActivator.DetectShellFlavor("/opt/homebrew/bin/fish", null));
            Assert.Equal(TerminalShellFlavor.Csh, PythonVenvActivator.DetectShellFlavor("/bin/tcsh", null));
            Assert.Equal(TerminalShellFlavor.PowerShell, PythonVenvActivator.DetectShellFlavor("powershell.exe -NoLogo", "PowerShell"));
            Assert.Equal(TerminalShellFlavor.PowerShell, PythonVenvActivator.DetectShellFlavor("C:\\Program Files\\PowerShell\\7\\pwsh.exe -NoLogo", null));
            Assert.Equal(TerminalShellFlavor.PowerShell, PythonVenvActivator.DetectShellFlavor("/usr/local/bin/pwsh", null));
            Assert.Equal(TerminalShellFlavor.Cmd, PythonVenvActivator.DetectShellFlavor("cmd.exe", "Command Prompt"));
            Assert.Equal(TerminalShellFlavor.Wsl, PythonVenvActivator.DetectShellFlavor("wsl.exe", "WSL (Linux)"));
            Assert.Equal(TerminalShellFlavor.Python, PythonVenvActivator.DetectShellFlavor("/usr/bin/python3 -i", "python"));
        }

        [Fact]
        public void BuildActivationCommand_PerShell()
        {
            using (var t = new TempDir())
            {
                string v = t.Venv(".venv", "bin/activate", "bin/activate.fish", "bin/activate.csh", "bin/Activate.ps1",
                    "Scripts/activate.bat", "Scripts/Activate.ps1");
                string q(string p) => PythonVenvActivator.QuotePosix(p);

                Assert.Equal(" source " + q(Path.Combine(v, "bin", "activate")), PythonVenvActivator.BuildActivationCommand(v, TerminalShellFlavor.Posix, false));
                Assert.Equal(" source " + q(Path.Combine(v, "bin", "activate.fish")), PythonVenvActivator.BuildActivationCommand(v, TerminalShellFlavor.Fish, false));
                Assert.Equal(" source " + q(Path.Combine(v, "bin", "activate.csh")), PythonVenvActivator.BuildActivationCommand(v, TerminalShellFlavor.Csh, false));
                Assert.Equal("& '" + Path.Combine(v, "bin", "Activate.ps1") + "'", PythonVenvActivator.BuildActivationCommand(v, TerminalShellFlavor.PowerShell, false));
                Assert.Equal("& '" + Path.Combine(v, "Scripts", "Activate.ps1") + "'", PythonVenvActivator.BuildActivationCommand(v, TerminalShellFlavor.PowerShell, true));
                Assert.Equal("\"" + Path.Combine(v, "Scripts", "activate.bat") + "\"", PythonVenvActivator.BuildActivationCommand(v, TerminalShellFlavor.Cmd, true));
                Assert.Null(PythonVenvActivator.BuildActivationCommand(v, TerminalShellFlavor.Python, false));
                Assert.Null(PythonVenvActivator.BuildActivationCommand(v, TerminalShellFlavor.Unknown, false));
            }
        }

        [Fact]
        public void BuildActivationCommand_MissingScripts_AndWslNeedsLinuxVenv()
        {
            using (var t = new TempDir())
            {
                string winVenv = t.Venv("winvenv", "Scripts/activate", "Scripts/activate.bat", "Scripts/Activate.ps1");
                Assert.Null(PythonVenvActivator.BuildActivationCommand(winVenv, TerminalShellFlavor.Fish, false));
                Assert.Null(PythonVenvActivator.BuildActivationCommand(winVenv, TerminalShellFlavor.Wsl, true)); // Windows venv unusable in WSL
                // Git Bash on a Windows-created venv uses Scripts/activate
                Assert.Contains("Scripts/activate", PythonVenvActivator.BuildActivationCommand(winVenv, TerminalShellFlavor.Posix, true));

                string linuxVenv = t.Venv("lin", "bin/activate");
                Assert.Contains("bin/activate", PythonVenvActivator.BuildActivationCommand(linuxVenv, TerminalShellFlavor.Wsl, true));
            }
            Assert.Equal("/mnt/c/proj/.venv/bin/activate", ConPtyTerminalSession.ConvertWindowsPathToPosix("C:\\proj\\.venv\\bin\\activate", true));
            Assert.Equal("/c/proj/.venv/Scripts/activate", ConPtyTerminalSession.ConvertWindowsPathToPosix("C:\\proj\\.venv\\Scripts\\activate", false));
        }

        [Fact]
        public void QuotePosix_QuotesOnlyWhenNeeded()
        {
            Assert.Equal("/Users/me/proj/.venv/bin/activate", PythonVenvActivator.QuotePosix("/Users/me/proj/.venv/bin/activate"));
            Assert.Equal("'/Users/me/my proj/.venv/bin/activate'", PythonVenvActivator.QuotePosix("/Users/me/my proj/.venv/bin/activate"));
            Assert.Equal("'/tmp/it'\\''s/$x/activate'", PythonVenvActivator.QuotePosix("/tmp/it's/$x/activate"));
        }

        [Fact]
        public void ResolveActivationCommand_SkipsAlreadyActiveAndUnsupported()
        {
            using (var t = new TempDir())
            {
                string v = t.Venv(".venv", "bin/activate");
                Assert.NotNull(PythonVenvActivator.ResolveActivationCommand(t.Path, t.Path, "/bin/zsh", "zsh", null, false));
                Assert.Null(PythonVenvActivator.ResolveActivationCommand(t.Path, t.Path, "/bin/zsh", "zsh", v + "/", false));
                Assert.NotNull(PythonVenvActivator.ResolveActivationCommand(t.Path, t.Path, "/bin/zsh", "zsh", "/some/other/venv", false));
                Assert.Null(PythonVenvActivator.ResolveActivationCommand(t.Path, t.Path, "/usr/bin/python3 -i", "python", null, false));
                Assert.Null(PythonVenvActivator.ResolveActivationCommand(null, t.Path, "/bin/zsh", "zsh", null, false));
                Assert.True(PythonVenvActivator.IsAlreadyActive(v, v));
                Assert.False(PythonVenvActivator.IsAlreadyActive(v, null));
            }
        }

        private static bool WaitFor(ConPtyTerminalSession s, string needle, int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                if (s.Buffer.GetScrollbackAndScreenText(1000).Contains(needle)) return true;
                Thread.Sleep(100);
            }
            return false;
        }

        private static string MakeProbeVenv(TempDir t)
        {
            // A fake venv whose activate script prints a marker computed at run time (so the echoed command
            // line itself can never satisfy the assertion).
            return t.Venv(".venv", "bin/activate");
        }

        [Fact]
        public void ScheduleActivation_TypesActivateIntoRealShell_OnceThePromptIsUp()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !File.Exists("/bin/bash")) return;
            using (var t = new TempDir())
            {
                string v = MakeProbeVenv(t);
                File.WriteAllText(Path.Combine(v, "bin", "activate"), "echo VENV_ON_$((40+2))\n");
                var s = new ConPtyTerminalSession("bash", "/bin/bash --noprofile --norc", t.Path, 100, 30);
                try
                {
                    string cmd = PythonVenvActivator.ScheduleActivation(s, t.Path);
                    Assert.NotNull(cmd);
                    Assert.True(WaitFor(s, "VENV_ON_42", 12000), s.Buffer.GetScrollbackAndScreenText(200));
                }
                finally { try { s.Dispose(); } catch { } }
            }
        }

        [Fact]
        public void ScheduleActivation_IsDropped_WhenSomethingElseIsTypedFirst()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !File.Exists("/bin/bash")) return;
            using (var t = new TempDir())
            {
                string v = MakeProbeVenv(t);
                File.WriteAllText(Path.Combine(v, "bin", "activate"), "echo VENV_ON_$((40+2))\n");
                var s = new ConPtyTerminalSession("bash", "/bin/bash --noprofile --norc", t.Path, 100, 30);
                try
                {
                    Assert.NotNull(PythonVenvActivator.ScheduleActivation(s, t.Path));
                    s.WriteInput("echo USER_$((1+1))_FIRST\r"); // e.g. a Run command typed into a brand-new terminal
                    Assert.True(WaitFor(s, "USER_2_FIRST", 8000), s.Buffer.GetScrollbackAndScreenText(200));
                    Thread.Sleep(1500);
                    Assert.DoesNotContain("VENV_ON_42", s.Buffer.GetScrollbackAndScreenText(500));
                }
                finally { try { s.Dispose(); } catch { } }
            }
        }

        [Fact]
        public void TakePendingActivation_LetsRunCommandsActivateFirst()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !File.Exists("/bin/bash")) return;
            using (var t = new TempDir())
            {
                string v = MakeProbeVenv(t);
                File.WriteAllText(Path.Combine(v, "bin", "activate"), "export PROBE_VENV=on\n");
                var s = new ConPtyTerminalSession("bash", "/bin/bash --noprofile --norc", t.Path, 100, 30);
                try
                {
                    string scheduled = PythonVenvActivator.ScheduleActivation(s, t.Path);
                    string claimed = PythonVenvActivator.TakePendingActivation(s);
                    Assert.Equal(scheduled, claimed);
                    Assert.Null(PythonVenvActivator.TakePendingActivation(s)); // claimed once
                    s.SendCommandLine(claimed);
                    s.SendCommandLine("echo RUN_${PROBE_VENV}_$((2+3))");
                    Assert.True(WaitFor(s, "RUN_on_5", 10000), s.Buffer.GetScrollbackAndScreenText(200));
                }
                finally { try { s.Dispose(); } catch { } }
            }
        }

        [Fact]
        public void Session_LiveWorkingDirectory_FollowsCd()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            using (var t = new TempDir())
            {
                string sub = Path.Combine(t.Path, "deeper");
                Directory.CreateDirectory(sub);
                var s = new ConPtyTerminalSession(null, "/bin/sh", t.Path, 100, 30);
                try
                {
                    s.WriteInput("cd deeper && echo CD_$((3*3))_DONE\r");
                    Assert.True(WaitFor(s, "CD_9_DONE", 8000));
                    string live = null;
                    for (int i = 0; i < 20 && (live == null || !live.EndsWith("deeper")); i++)
                    {
                        live = s.GetLiveWorkingDirectory();
                        Thread.Sleep(100);
                    }
                    Assert.EndsWith("deeper", live);
                }
                finally { try { s.Dispose(); } catch { } }
            }
        }
    }

    public class TerminalLinkDetectorTests
    {
        private static TerminalLinkMatch At(string line, string needle, IList<string> bases, int offset = 1)
        {
            int col = line.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(col >= 0, "needle not in line");
            return TerminalLinkDetector.FindLinkAt(line, col + offset, bases);
        }

        [Fact]
        public void Url_TrailingPunctuationAndBrackets()
        {
            var l = At("see https://example.com/a?b=1.", "https", null);
            Assert.True(l.IsUrl);
            Assert.Equal("https://example.com/a?b=1", l.Url);
            Assert.Equal(4, l.StartCol);

            var p = At("(docs at https://en.wikipedia.org/wiki/C_(lang)) ok", "https", null);
            Assert.Equal("https://en.wikipedia.org/wiki/C_(lang)", p.Url);
            Assert.Null(TerminalLinkDetector.FindLinkAt("see https://example.com now", 1, null));
        }

        [Fact]
        public void RelativePath_WithLineAndColumn_ResolvesAgainstBaseDirsInOrder()
        {
            using (var cwd = new TempDir())
            using (var root = new TempDir())
            {
                string app = root.File("src/app.py");
                var bases = new List<string> { cwd.Path, root.Path };
                string line = "  src/app.py:12:5: error E501";
                var l = At(line, "src/app.py", bases);
                Assert.NotNull(l);
                Assert.False(l.IsUrl);
                Assert.Equal(app, l.FilePath);
                Assert.Equal(12, l.Line);
                Assert.Equal(5, l.Column);
                Assert.Equal(2, l.StartCol);
                Assert.Equal("src/app.py:12:5".Length, l.Length);

                // Same name in the terminal cwd wins over the workspace root.
                string local = cwd.File("src/app.py");
                Assert.Equal(local, At(line, "src/app.py", bases).FilePath);
            }
        }

        [Fact]
        public void AbsoluteUnixPath_ParenPosition_AndPythonTraceback()
        {
            using (var t = new TempDir())
            {
                string f = t.File("pkg/mod.py");
                var l = At("Error in " + f + ":42 here", f, null);
                Assert.Equal(f, l.FilePath);
                Assert.Equal(42, l.Line);
                Assert.Equal(0, l.Column);

                var bases = new List<string> { t.Path };
                var ms = At("pkg/mod.py(7,3): warning CS0168", "pkg/mod.py", bases);
                Assert.Equal(7, ms.Line);
                Assert.Equal(3, ms.Column);
                Assert.Equal("pkg/mod.py(7,3)".Length, ms.Length);

                string tb = "  File \"" + f + "\", line 99, in <module>";
                var py = At(tb, f, null);
                Assert.Equal(f, py.FilePath);
                Assert.Equal(99, py.Line);
            }
        }

        [Fact]
        public void BareFileName_TrailingDot_MissingFiles_AndPlainWords()
        {
            using (var t = new TempDir())
            {
                t.File("main.py");
                var bases = new List<string> { t.Path };
                Assert.Equal(9, At("main.py:9", "main.py", bases).Line);
                var dot = At("see main.py.", "main.py", bases);
                Assert.Equal("main.py".Length, dot.Length);
                Assert.Null(At("see missing.py:3", "missing", bases));
                Assert.Null(At("hello world", "hello", bases));
                Assert.Null(At("at 12:30:45 today", "12", bases));
            }
        }

        [Fact]
        public void WordBounds_UseVsCodeSeparators()
        {
            string line = "foo /a/b.py:3 (bar)";
            Assert.True(TerminalLinkDetector.GetWordBounds(line, 6, out int s, out int e));
            Assert.Equal("/a/b.py:3", line.Substring(s, e - s + 1));
            Assert.False(TerminalLinkDetector.GetWordBounds(line, 3, out s, out e));
            Assert.True(TerminalLinkDetector.GetWordBounds(line, 15, out s, out e));
            Assert.Equal("bar", line.Substring(s, e - s + 1));
        }

        [Fact]
        public void PosixToWindowsPath_WslAndGitBash()
        {
            Assert.Equal("C:\\Users\\x\\a.py", TerminalLinkDetector.PosixToWindowsPath("/mnt/c/Users/x/a.py"));
            Assert.Equal("D:\\src\\a.py", TerminalLinkDetector.PosixToWindowsPath("/d/src/a.py"));
            Assert.Null(TerminalLinkDetector.PosixToWindowsPath("/usr/bin/env"));
        }
    }

    // Headless UI tests share one Avalonia dispatcher and mouse device: never run them in parallel with other UI tests.
    [Collection("SequentialWorkbench")]
    public class AvaloniaTerminalLinkAndSelectionTests
    {
        private static void Settle(Window window, AvaloniaTerminalEmulatorControl term = null)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (term != null)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 5000)
                {
                    if (term.Bounds.Width > 0 && term.Bounds.Height > 0 &&
                        window.InputHitTest(new Point(term.Bounds.Width / 2, term.Bounds.Height / 2)) != null)
                        break;
                    Thread.Sleep(5);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                }
            }
        }

        private static (Window, AvaloniaTerminalEmulatorControl) MakeTerminal()
        {
            var term = new AvaloniaTerminalEmulatorControl();
            var window = new Window { Width = 900, Height = 300, Content = term };
            window.Show();
            Settle(window, term);
            return (window, term);
        }

        private static Point CellCenter(AvaloniaTerminalEmulatorControl term, int row, int col)
        {
            object canvas = typeof(AvaloniaTerminalEmulatorControl).GetField("gridCanvas", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(term);
            Type ct = canvas.GetType();
            double cellW = (double)ct.GetProperty("CellW").GetValue(canvas);
            double cellH = (double)ct.GetProperty("CellH").GetValue(canvas);
            double padL = (double)ct.GetField("PadLeft").GetValue(null);
            double padT = (double)ct.GetField("PadTop").GetValue(null);
            return new Point(padL + col * cellW + cellW / 2, padT + row * cellH + cellH / 2);
        }

        private static RawInputModifiers LinkModifier => MacKeymap.IsMacOS ? RawInputModifiers.Meta : RawInputModifiers.Control;

        [AvaloniaFact]
        public void FileAndUrlLinks_AreDetected_AndActivated()
        {
            using (var t = new TempDir())
            {
                string f = t.File("src/app.py");
                var (window, term) = MakeTerminal();
                term.WorkspaceRootProvider = () => t.Path;
                term.FeedAnsiPreview("Traceback: src/app.py:12:3 failed\r\nsee https://example.com/x for help");

                string opened = null; int line = -1, col = -1; string url = null;
                term.FileLinkActivated += (p, l, c) => { opened = p; line = l; col = c; };
                term.UrlOpener = u => url = u;

                var link = term.FindLinkAtCell(0, 13);
                Assert.NotNull(link);
                term.ActivateLink(link);
                Assert.Equal(f, opened);
                Assert.Equal(12, line);
                Assert.Equal(3, col);

                var u1 = term.FindLinkAtCell(1, 8);
                Assert.True(u1.IsUrl);
                term.ActivateLink(u1);
                Assert.Equal("https://example.com/x", url);

                Assert.Null(term.FindLinkAtCell(0, 2)); // "Traceback:" is not a link
                window.Close();
            }
        }

        [AvaloniaFact]
        public void ModifierClick_OpensLink_PlainClickDoesNot()
        {
            using (var t = new TempDir())
            {
                string f = t.File("pkg/mod.py");
                var (window, term) = MakeTerminal();
                term.WorkspaceRootProvider = () => t.Path;
                term.FeedAnsiPreview("pkg/mod.py:7");
                Settle(window, term);
                string opened = null;
                term.FileLinkActivated += (p, l, c) => opened = p;

                var pt = CellCenter(term, 0, 3);
                Settle(window, term);
                window.MouseDown(pt, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(pt, MouseButton.Left, RawInputModifiers.None);
                Settle(window);
                Assert.Null(opened);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (opened == null && sw.ElapsedMilliseconds < 5000)
                {
                    window.MouseMove(pt, LinkModifier);
                    Settle(window);
                    window.MouseDown(pt, MouseButton.Left, LinkModifier);
                    window.MouseUp(pt, MouseButton.Left, LinkModifier);
                    Settle(window);
                    if (opened == null) Thread.Sleep(5);
                }
                Assert.Equal(f, opened);
                window.Close();
            }
        }

        [AvaloniaFact]
        public void PointerDoubleAndTripleClick_SelectWordThenLine_AndModifierHoverShowsLink()
        {
            using (var t = new TempDir())
            {
                t.File("pkg/mod.py");
                var (window, term) = MakeTerminal();
                term.WorkspaceRootProvider = () => t.Path;
                term.FeedAnsiPreview("error at pkg/mod.py:7 now");
                Settle(window, term);
                var pt = CellCenter(term, 0, 12);
                Settle(window, term);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (term.GetSelectedText() != "pkg/mod.py:7" && sw.ElapsedMilliseconds < 5000)
                {
                    window.MouseDown(pt, MouseButton.Left, RawInputModifiers.None);
                    window.MouseUp(pt, MouseButton.Left, RawInputModifiers.None);
                    window.MouseDown(pt, MouseButton.Left, RawInputModifiers.None);
                    window.MouseUp(pt, MouseButton.Left, RawInputModifiers.None);
                    Dispatcher.UIThread.RunJobs();
                    if (term.GetSelectedText() == "pkg/mod.py:7") break;
                    Settle(window, term);
                }
                Assert.Equal("pkg/mod.py:7", term.GetSelectedText());

                window.MouseDown(pt, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(pt, MouseButton.Left, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                while (term.GetSelectedText() != "error at pkg/mod.py:7 now" && sw.ElapsedMilliseconds < 5000)
                {
                    window.MouseDown(pt, MouseButton.Left, RawInputModifiers.None);
                    window.MouseUp(pt, MouseButton.Left, RawInputModifiers.None);
                    window.MouseDown(pt, MouseButton.Left, RawInputModifiers.None);
                    window.MouseUp(pt, MouseButton.Left, RawInputModifiers.None);
                    window.MouseDown(pt, MouseButton.Left, RawInputModifiers.None);
                    window.MouseUp(pt, MouseButton.Left, RawInputModifiers.None);
                    Dispatcher.UIThread.RunJobs();
                    if (term.GetSelectedText() == "error at pkg/mod.py:7 now") break;
                    Settle(window, term);
                }
                Assert.Equal("error at pkg/mod.py:7 now", term.GetSelectedText());

                object canvas = typeof(AvaloniaTerminalEmulatorControl).GetField("gridCanvas", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(term);
                PropertyInfo hovered = canvas.GetType().GetProperty("HoveredLink", BindingFlags.NonPublic | BindingFlags.Instance);
                window.MouseMove(pt, RawInputModifiers.None);
                Settle(window);
                Assert.Null(hovered.GetValue(canvas));
                window.MouseMove(pt, LinkModifier);
                Settle(window);
                var link = (TerminalLinkMatch)hovered.GetValue(canvas);
                Assert.NotNull(link);
                Assert.Equal(9, link.StartCol);
                window.MouseMove(pt, RawInputModifiers.None);
                Settle(window);
                Assert.Null(hovered.GetValue(canvas));
                window.Close();
            }
        }

        [AvaloniaFact]
        public void WordAndLineSelection()
        {
            var (window, term) = MakeTerminal();
            term.FeedAnsiPreview("$ python /tmp/x/app.py --flag (done)");
            Assert.True(term.SelectWordAtCell(0, 12));
            Assert.Equal("/tmp/x/app.py", term.GetSelectedText());
            Assert.True(term.SelectWordAtCell(0, 0)); // single-character word still selects
            Assert.Equal("$", term.GetSelectedText());
            Assert.False(term.SelectWordAtCell(0, 1)); // separator
            term.SelectLineAtCell(0);
            Assert.Equal("$ python /tmp/x/app.py --flag (done)", term.GetSelectedText());
            window.Close();
        }
    }
}
