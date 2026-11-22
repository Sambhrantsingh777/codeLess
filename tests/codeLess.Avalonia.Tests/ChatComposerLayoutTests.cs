using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>QA #4: the chat composer shows its whole placeholder and grows with the prompt (VS Code chat input).</summary>
    [Collection("SequentialWorkbench")]
    public class ChatComposerLayoutTests : IDisposable
    {
        private readonly string settingsPath = IdePersistentSettings.GetSettingsFilePath();
        private readonly string settingsBackup;
        private readonly string ws = Path.Combine(Path.GetTempPath(), "cl-composer-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        public ChatComposerLayoutTests()
        {
            settingsBackup = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
            Directory.CreateDirectory(ws);
        }

        public void Dispose()
        {
            try { if (settingsBackup != null) File.WriteAllText(settingsPath, settingsBackup); else if (File.Exists(settingsPath)) File.Delete(settingsPath); } catch { }
            try { Directory.Delete(ws, true); } catch { }
        }

        private static TextBox Composer(Window w) => w.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "ChatPromptInput");

        private static void Layout(Window w)
        {
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        [AvaloniaFact]
        public void Placeholder_IsNotClipped_AtCommonDockWidths()
        {
            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true, Width = 1400, Height = 900 };
            w.Show();
            try
            {
                Layout(w);
                var box = Composer(w);
                Assert.True(box.IsEffectivelyVisible);
                foreach (double width in new[] { 260.0, 320.0, 420.0 })
                {
                    box.Width = width;
                    Layout(w);
                    var watermark = box.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "PART_Watermark" || t.Name == "PART_Placeholder" || t.Text == box.Watermark);
                    Assert.NotNull(watermark);
                    // The placeholder's full wrapped height fits inside the box (nothing cut off at the bottom).
                    watermark.Measure(new Size(watermark.Bounds.Width, double.PositiveInfinity));
                    double needed = watermark.DesiredSize.Height;
                    var origin = watermark.TranslatePoint(new Point(0, 0), box) ?? new Point(0, 0);
                    Assert.True(origin.Y + needed <= box.Bounds.Height + 0.5, $"width {width}: placeholder needs {needed:0.#}px from y={origin.Y:0.#} but the box is {box.Bounds.Height:0.#}px");
                    Assert.True(box.Bounds.Height >= 2 * box.FontSize, "at least two lines");
                }
            }
            finally { w.Close(); }
        }

        [AvaloniaFact]
        public void Composer_GrowsWithThePrompt_UpToAMaximum_AndShiftEnterInsertsANewline()
        {
            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true, Width = 1400, Height = 900 };
            w.Show();
            try
            {
                Layout(w);
                var box = Composer(w);
                box.Width = 320;
                Layout(w);
                double empty = box.Bounds.Height;
                box.Text = string.Join("\n", Enumerable.Range(1, 6).Select(i => "line " + i));
                Layout(w);
                Assert.True(box.Bounds.Height > empty + 20, $"grows: {empty} -> {box.Bounds.Height}");
                box.Text = string.Join("\n", Enumerable.Range(1, 80).Select(i => "line " + i));
                Layout(w);
                Assert.True(box.Bounds.Height <= box.MaxHeight + 0.5);
                Assert.True(box.MaxHeight < 400);

                box.Text = "first";
                box.CaretIndex = 5;
                box.Focus();
                w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Shift);
                Assert.Contains("\n", box.Text.Replace("\r\n", "\n"));
                Assert.StartsWith("first", box.Text);
            }
            finally { w.Close(); }
        }
    }
}
