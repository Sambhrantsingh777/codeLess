using System;
using System.Threading;
using Avalonia.Threading;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Posts UI work raised by background events (network ledger, process output, file watchers). The job runs inside
    /// the ExecutionContext of the UI component that subscribed (captured on the UI thread), never the background
    /// thread's context, so context-scoped services (e.g. the headless platform's AvaloniaLocator scope) resolve.
    /// </summary>
    internal sealed class UiPost
    {
        private readonly ExecutionContext uiContext;

        private UiPost(ExecutionContext ctx) { uiContext = ctx; }

        /// <summary>Call on the UI thread (e.g. in a control's constructor or when subscribing to events).</summary>
        public static UiPost CaptureCurrent() => new UiPost(ExecutionContext.Capture());

        public void Post(Action action, DispatcherPriority priority = default)
        {
            if (action == null) return;
            if (priority == default) priority = DispatcherPriority.Normal;
            var ctx = uiContext;
            Action run = ctx == null ? action : () => ExecutionContext.Run(ctx.CreateCopy(), _ => action(), null);
            using (ExecutionContext.SuppressFlow())
                Dispatcher.UIThread.Post(run, priority);
        }
    }
}
