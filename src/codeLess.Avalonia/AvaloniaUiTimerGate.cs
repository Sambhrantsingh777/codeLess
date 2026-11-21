using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Keeps a purely cosmetic <see cref="DispatcherTimer"/> (caret blink, terminal cursor blink, progress animation)
    /// running only while its effect can actually be seen, so an idle IDE does not wake the CPU several times a second:
    /// the timer stops when the control is detached or hidden, when the window is minimized and, for blink timers
    /// (<c>requireFocus</c>), when the control does not have keyboard focus or the window is inactive (another app is
    /// in front). It restarts on focus, activation, restore or when the control becomes visible again, like VS Code.
    /// Owners subscribe to <see cref="RunningChanged"/> to show the steady (non-blinking) state while paused.
    /// </summary>
    public sealed class UiTimerGate
    {
        private readonly Control control;
        private readonly DispatcherTimer timer;
        private readonly bool requireFocus;
        private readonly Func<bool> wanted;
        private WindowBase window;
        private bool waitingForVisibility;

        /// <summary>Raised after the timer was started or stopped by the gate.</summary>
        public event Action RunningChanged;

        public UiTimerGate(Control control, DispatcherTimer timer, bool requireFocus, Func<bool> wanted = null)
        {
            this.control = control ?? throw new ArgumentNullException(nameof(control));
            this.timer = timer ?? throw new ArgumentNullException(nameof(timer));
            this.requireFocus = requireFocus;
            this.wanted = wanted;

            control.AttachedToVisualTree += (s, e) => { SetWindow(TopLevel.GetTopLevel(control) as WindowBase); Update(); };
            control.DetachedFromVisualTree += (s, e) => { SetWindow(null); Update(); };
            control.PropertyChanged += (s, e) =>
            {
                if (e.Property == Visual.IsVisibleProperty || e.Property == InputElement.IsKeyboardFocusWithinProperty)
                    Update();
            };
            // A hidden ancestor (collapsed panel, inactive tab) raises no event on this control: notice it on the next tick.
            timer.Tick += (s, e) => { if (!control.IsEffectivelyVisible) Update(); };
            if (control.GetVisualRoot() != null) SetWindow(TopLevel.GetTopLevel(control) as WindowBase);
        }

        public bool IsRunning => timer.IsEnabled;

        /// <summary>True while the control has keyboard focus inside an active, non-minimized window.</summary>
        public bool HasActiveFocus
        {
            get
            {
                if (!control.IsKeyboardFocusWithin) return false;
                if (window == null) return true;
                return window.IsActive && !IsMinimized(window);
            }
        }

        /// <summary>Re-evaluates whether the timer should run (call when the owner's <c>wanted</c> condition changes).</summary>
        public void Update()
        {
            bool attached = control.GetVisualRoot() != null;
            bool windowOk = window == null || !IsMinimized(window);
            bool wantedNow = wanted == null || wanted();
            bool focusOk = !requireFocus || HasActiveFocus;
            bool candidate = attached && windowOk && wantedNow && focusOk;
            bool visible = control.IsVisible && control.IsEffectivelyVisible;
            bool run = candidate && visible;

            // Paused only because an ancestor is hidden: re-check after the next layout pass (showing it again relayouts).
            bool wait = candidate && !visible && control.IsVisible;
            if (wait != waitingForVisibility)
            {
                waitingForVisibility = wait;
                if (wait) control.LayoutUpdated += OnLayoutUpdated;
                else control.LayoutUpdated -= OnLayoutUpdated;
            }

            if (run == timer.IsEnabled) return;
            if (run) timer.Start();
            else timer.Stop();
            RunningChanged?.Invoke();
        }

        private void OnLayoutUpdated(object sender, EventArgs e)
        {
            if (control.IsEffectivelyVisible) Update();
        }

        private static bool IsMinimized(WindowBase w) => w is Window win && win.WindowState == WindowState.Minimized;

        private void SetWindow(WindowBase w)
        {
            if (ReferenceEquals(window, w)) return;
            if (window != null)
            {
                window.Activated -= OnWindowActivationChanged;
                window.Deactivated -= OnWindowActivationChanged;
                window.PropertyChanged -= OnWindowPropertyChanged;
            }
            window = w;
            if (window != null)
            {
                window.Activated += OnWindowActivationChanged;
                window.Deactivated += OnWindowActivationChanged;
                window.PropertyChanged += OnWindowPropertyChanged;
            }
        }

        private void OnWindowActivationChanged(object sender, EventArgs e) => Update();

        private void OnWindowPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Window.WindowStateProperty || e.Property == WindowBase.IsActiveProperty)
                Update();
        }
    }
}
