using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// `codeLess.exe --smoke-test &lt;report.txt&gt; [workspace]`: CI launch smoke test for the WinForms UI.
    /// Opens the workspace and a few representative files, forces every control to paint (DrawToBitmap), records any
    /// UI-thread / AppDomain exception, writes a report and exits (0 = OK, 1 = exceptions, 2 = could not start).
    /// C# 5 only (compiled by legacy csc.exe).
    /// </summary>
    public partial class CodeLessMainForm
    {
        private static readonly List<string> smokeErrors = new List<string>();

        internal static bool TryRunSmokeTest(string[] args)
        {
            if (args == null || args.Length < 2 || args[0] != "--smoke-test") return false;
            string reportPath = args[1];
            string workspace = args.Length > 2 ? args[2] : "";
            StringBuilder report = new StringBuilder();
            int exitCode = 2;
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => { lock (smokeErrors) smokeErrors.Add("UIThread: " + e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => { lock (smokeErrors) smokeErrors.Add("AppDomain: " + e.ExceptionObject); };
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                CodeLessMainForm form = new CodeLessMainForm(Directory.Exists(workspace) ? workspace : "");
                Timer driver = new Timer();
                driver.Interval = 1500;
                int step = 0;
                driver.Tick += (s, e) =>
                {
                    step++;
                    try
                    {
                        if (step == 1)
                        {
                            report.AppendLine("started: " + form.Text);
                            foreach (string rel in new string[] { "README.md", "src/codeLess.Core/CoreModels.cs", "Compile.ps1", ".github/workflows/build-and-sign.yml" })
                            {
                                string p = string.IsNullOrEmpty(workspace) ? "" : Path.Combine(workspace, rel.Replace('/', Path.DirectorySeparatorChar));
                                if (File.Exists(p))
                                {
                                    form.OpenFileInTab(p);
                                    report.AppendLine("opened: " + rel);
                                }
                            }
                        }
                        else if (step == 2)
                        {
                            PaintAll(form, report);
                        }
                        else if (step == 3)
                        {
                            Size original = form.Size;
                            form.Size = new Size(Math.Max(900, original.Width - 300), Math.Max(600, original.Height - 200));
                            Application.DoEvents();
                            PaintAll(form, report);
                            form.Size = original;
                        }
                        else
                        {
                            driver.Stop();
                            form.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (smokeErrors) smokeErrors.Add("step " + step + ": " + ex);
                        driver.Stop();
                        form.Close();
                    }
                };
                form.Shown += (s, e) => driver.Start();
                Application.Run(form);

                lock (smokeErrors)
                {
                    exitCode = smokeErrors.Count == 0 ? 0 : 1;
                    report.AppendLine(exitCode == 0 ? "RESULT: OK" : "RESULT: " + smokeErrors.Count + " exception(s)");
                    foreach (string err in smokeErrors) report.AppendLine(err);
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("RESULT: failed to start: " + ex);
                exitCode = 2;
            }
            try { File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8); } catch { }
            Environment.Exit(exitCode);
            return true;
        }

        /// <summary>Renders the whole window and every visible child control off-screen, which runs all OnPaint code paths.</summary>
        private static void PaintAll(Form form, StringBuilder report)
        {
            int painted = 0;
            using (Bitmap bmp = new Bitmap(Math.Max(1, form.Width), Math.Max(1, form.Height)))
            {
                form.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                painted++;
            }
            Stack<Control> stack = new Stack<Control>();
            stack.Push(form);
            while (stack.Count > 0)
            {
                Control c = stack.Pop();
                foreach (Control child in c.Controls)
                {
                    stack.Push(child);
                    if (!child.Visible || child.Width <= 0 || child.Height <= 0) continue;
                    try
                    {
                        using (Bitmap cb = new Bitmap(child.Width, child.Height))
                            child.DrawToBitmap(cb, new Rectangle(0, 0, child.Width, child.Height));
                        painted++;
                    }
                    catch (Exception ex)
                    {
                        lock (smokeErrors) smokeErrors.Add("paint " + child.GetType().Name + ": " + ex);
                    }
                }
            }
            report.AppendLine("painted controls: " + painted);
        }
    }
}
