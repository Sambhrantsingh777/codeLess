using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>Hosts the read-only large-file viewer and binary-file warning placeholder in place of the text canvas.</summary>
    public partial class AvaloniaEditorControl
    {
        private Grid largeFileHostGrid;
        private Border binaryPlaceholderHost;
        private TextBlock binaryMessageBlock;
        private Button binaryOpenAnywayBtn;
        private Action binaryOpenAnywayAction;

        public AvaloniaLargeFileViewer LargeFileViewer { get; private set; }
        public bool IsShowingLargeFile => LargeFileViewer != null && LargeFileViewer.IsVisible && LargeFileViewer.Document != null;
        public bool IsBinaryPlaceholderVisible => binaryPlaceholderHost != null && binaryPlaceholderHost.IsVisible;
        public string BinaryPlaceholderText => binaryMessageBlock?.Text ?? "";
        public string BinaryPlaceholderPath { get; private set; }
        public long LargeFileEditLimitBytes { get; set; } = LargeFilePolicy.DefaultEditLimitMB * 1024L * 1024L;

        public event Action<string> LargeFileOpenInEditorRequested;
        public event Action<string> LargeFileStatusMessage;

        private void InitLargeFileHost(Grid body)
        {
            largeFileHostGrid = body;
            // Commands routed to the (hidden) text canvas while the viewer is showing (Edit menu Copy/Paste, simulated
            // shortcuts) must never edit the empty placeholder buffer: copy goes to the viewer, the rest is swallowed.
            AddHandler(KeyDownEvent, OnLargeFileTunnelKeyDown, RoutingStrategies.Tunnel);
        }

        public void ShowBinaryPlaceholder(string filePath, Action onOpenAnyway)
        {
            BinaryPlaceholderPath = filePath;
            binaryOpenAnywayAction = onOpenAnyway;
            if (binaryPlaceholderHost == null && largeFileHostGrid != null)
            {
                binaryMessageBlock = new TextBlock
                {
                    Text = HotExitService.BinaryPlaceholderMessage,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                };
                binaryOpenAnywayBtn = new Button
                {
                    Content = "Open Anyway",
                    Padding = new Thickness(14, 6),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                var weakEd = new WeakReference<AvaloniaEditorControl>(this);
                binaryOpenAnywayBtn.Click += (s, e) => { if (weakEd.TryGetTarget(out var ed)) ed.ClickOpenBinaryAnyway(); };
                var stack = new StackPanel
                {
                    Spacing = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 540,
                    Children = { binaryMessageBlock, binaryOpenAnywayBtn }
                };
                binaryPlaceholderHost = new Border
                {
                    Child = stack,
                    IsVisible = false
                };
                largeFileHostGrid.Children.Add(binaryPlaceholderHost);
            }
            if (binaryPlaceholderHost != null)
            {
                binaryPlaceholderHost.Background = AvaloniaColorHelper.ToBrush(currentTheme.EditorBg);
                binaryMessageBlock.Foreground = AvaloniaColorHelper.ToBrush(currentTheme.TextSecondary);
                WorkbenchStyles.StylePrimaryButton(binaryOpenAnywayBtn, currentTheme);
                if (LargeFileViewer != null && LargeFileViewer.IsVisible)
                {
                    LargeFileViewer.UnloadDocument();
                    LargeFileViewer.IsVisible = false;
                }
                welcomeHost.IsVisible = false;
                emptyHost.IsVisible = false;
                Canvas.IsVisible = false;
                diffReviewBanner.IsVisible = false;
                findReplaceOverlay.IsVisible = false;
                inlineAiBar.IsVisible = false;
                binaryPlaceholderHost.IsVisible = true;
            }
        }

        public void HideBinaryPlaceholder()
        {
            if (binaryPlaceholderHost != null)
                binaryPlaceholderHost.IsVisible = false;
            binaryOpenAnywayAction = null;
        }

        public void ClickOpenBinaryAnyway()
        {
            var act = binaryOpenAnywayAction;
            HideBinaryPlaceholder();
            act?.Invoke();
        }

        private void OnLargeFileTunnelKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsShowingLargeFile || !ReferenceEquals(e.Source, Canvas)) return;
            if ((e.Key == Key.C || e.Key == Key.Insert) && MacKeymap.IsPrimary(e.KeyModifiers))
                LargeFileViewer.CopySelection();
            else if (e.Key == Key.A && MacKeymap.IsPrimary(e.KeyModifiers))
                LargeFileViewer.Viewport.SelectAll();
            e.Handled = true;
        }

        /// <summary>Shows the viewer for large-file tabs (returns true) or hides it for normal tabs (returns false).</summary>
        private bool SyncLargeFileViewer(EditorTabBuffer tab)
        {
            HideBinaryPlaceholder();
            bool large = tab != null && tab.LargeDocument != null && !tab.LargeDocument.IsDisposed;
            if (!large)
            {
                if (LargeFileViewer != null && LargeFileViewer.IsVisible)
                {
                    LargeFileViewer.UnloadDocument();
                    LargeFileViewer.IsVisible = false;
                }
                return false;
            }

            if (LargeFileViewer == null)
            {
                LargeFileViewer = new AvaloniaLargeFileViewer();
                LargeFileViewer.OpenInEditorRequested += p => LargeFileOpenInEditorRequested?.Invoke(p);
                LargeFileViewer.StatusMessagePublished += m => LargeFileStatusMessage?.Invoke(m);
                LargeFileViewer.ApplyTheme(currentTheme);
                largeFileHostGrid.Children.Insert(1, LargeFileViewer);
            }

            ShowWelcomeView = false;
            welcomeHost.IsVisible = false;
            Canvas.IsVisible = false;
            diffReviewBanner.IsVisible = false;
            findReplaceOverlay.IsVisible = false;
            inlineAiBar.IsVisible = false;
            FindMatches.Clear();
            breadcrumbBar.IsVisible = true;
            LargeFileViewer.IsVisible = true;
            LargeFileViewer.LoadDocument(tab.LargeDocument, LargeFileEditLimitBytes, Canvas.FontSizePx);
            RefreshBreadcrumbs();
            Dispatcher.UIThread.Post(() => { if (IsShowingLargeFile) LargeFileViewer.Viewport.Focus(); }, DispatcherPriority.Background);
            return true;
        }
    }
}
