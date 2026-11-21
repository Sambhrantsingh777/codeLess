using System;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>Cosmetic timers (caret blink, spinner) must not wake an idle / unfocused / hidden window.</summary>
    [Collection("SequentialWorkbench")]
    public class IdleTimerGateHeadlessTests
    {
        [AvaloniaFact]
        public void FocusGate_RunsOnlyWhileFocusedInActiveWindow()
        {
            var target = new Border { Focusable = true, Width = 50, Height = 20 };
            var other = new TextBox { Width = 50 };
            var panel = new StackPanel();
            panel.Children.Add(target);
            panel.Children.Add(other);
            var window = new Window { Content = panel, Width = 300, Height = 200 };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            var gate = new UiTimerGate(target, timer, requireFocus: true);
            window.Show();
            window.Activate();
            Dispatcher.UIThread.RunJobs();

            Assert.False(timer.IsEnabled); // nothing focused yet
            target.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(timer.IsEnabled);

            other.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.False(timer.IsEnabled);

            target.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(timer.IsEnabled);

            window.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();
            Assert.False(timer.IsEnabled);
            window.WindowState = WindowState.Normal;
            Dispatcher.UIThread.RunJobs();
            Assert.True(timer.IsEnabled);

            target.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(timer.IsEnabled);
            window.Close();
            Assert.False(timer.IsEnabled);
        }

        [AvaloniaFact]
        public void Spinner_AnimatesOnlyWhileShownAndProgressing()
        {
            var bar = new AvaloniaIndeterminateBar { Height = 2, Width = 100 };
            var window = new Window { Content = bar, Width = 300, Height = 200 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.False(bar.IsAnimationTimerRunning);
            bar.StartProgress();
            Dispatcher.UIThread.RunJobs();
            Assert.True(bar.IsAnimationTimerRunning);
            window.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();
            Assert.False(bar.IsAnimationTimerRunning);
            window.WindowState = WindowState.Normal;
            Dispatcher.UIThread.RunJobs();
            Assert.True(bar.IsAnimationTimerRunning);
            bar.StopProgress();
            Dispatcher.UIThread.RunJobs();
            Assert.False(bar.IsAnimationTimerRunning);
            window.Close();
        }

        [AvaloniaFact]
        public void EditorCaretBlink_StopsWhenEditorLosesFocus()
        {
            var editor = new AvaloniaEditorControl();
            var tab = new EditorTabBuffer { FilePath = "", FileName = "a.cs", RelativePath = "a.cs", Language = "cs", Content = "class A { }\n" };
            tab.SyncLinesFromContent();
            var box = new TextBox();
            var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            grid.Children.Add(editor);
            Grid.SetRow(box, 1);
            grid.Children.Add(box);
            var window = new Window { Content = grid, Width = 800, Height = 600 };
            window.Show();
            window.Activate();
            editor.LoadBuffer(tab);
            Dispatcher.UIThread.RunJobs();

            editor.Canvas.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.Canvas.IsCaretBlinkTimerRunning);

            box.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.False(editor.Canvas.IsCaretBlinkTimerRunning);
            Assert.True(editor.Canvas.IsCaretShown); // steady caret while paused

            editor.Canvas.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.Canvas.IsCaretBlinkTimerRunning);
            window.Close();
            Assert.False(editor.Canvas.IsCaretBlinkTimerRunning);
        }
    }
}
