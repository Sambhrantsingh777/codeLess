using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// Windows updates, same flow as the Avalonia workbench and VS Code: Help › Check for Updates... → Downloading Update...
    /// → Restart to Update (the menu item text follows the state, a status-bar item shows progress / Restart to Update).
    /// The installer (codeLess-Setup.exe) or the portable codeLess.exe is downloaded through NetworkLedger, verified
    /// against SHA256SUMS.txt and applied by a helper .cmd after codeLess quits (UpdateService / UpdateInstaller).
    /// STRICT C# 5.
    /// </summary>
    public partial class CodeLessMainForm
    {
        private static UpdateService sharedUpdates;
        private Label statusUpdateItem;
        private ToolStripMenuItem updateMenuItem;
        private System.Windows.Forms.Timer updatePeriodicTimer;
        private UpdateState lastSeenUpdateState = UpdateState.Idle;
        private bool updatesSubscribed;

        public static UpdateService Updates
        {
            get
            {
                if (sharedUpdates == null)
                {
                    int pid;
                    using (Process p = Process.GetCurrentProcess()) pid = p.Id;
                    sharedUpdates = new UpdateService(CurrentAppVersionString(), UpdateEnvironment.DetectCurrent(Application.ExecutablePath, pid),
                        Path.Combine(Path.GetTempPath(), "codeLess-update"));
                }
                return sharedUpdates;
            }
        }

        private ToolStripMenuItem CreateUpdateMenuItem()
        {
            updateMenuItem = CreateMenuItem(Updates.MenuLabel, "", CheckForUpdatesInteractive);
            EnsureUpdateSubscription();
            return updateMenuItem;
        }

        private void EnsureUpdateSubscription()
        {
            if (updatesSubscribed) return;
            updatesSubscribed = true;
            Updates.Changed += OnUpdatesChangedAnyThread;
            this.FormClosed += (s, e) =>
            {
                Updates.Changed -= OnUpdatesChangedAnyThread;
                if (updatePeriodicTimer != null) updatePeriodicTimer.Stop();
            };
        }

        private void OnUpdatesChangedAnyThread(object sender, EventArgs e)
        {
            if (!this.IsHandleCreated || this.IsDisposed) return;
            try { this.BeginInvoke(new Action(OnUpdatesChanged)); } catch { }
        }

        private void OnUpdatesChanged()
        {
            UpdateService u = Updates;
            ApplyUpdateUi();
            UpdateState prev = lastSeenUpdateState;
            lastSeenUpdateState = u.State;
            if (prev == u.State) return;
            if (u.State == UpdateState.Ready) ShowUpdateReadyPrompt();
            else if (u.State == UpdateState.Error && prev == UpdateState.Downloading)
                MessageBox.Show(this, "The update could not be downloaded:\n\n" + u.LastError, "codeLess Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void ApplyUpdateUi()
        {
            UpdateService u = Updates;
            if (updateMenuItem != null)
            {
                updateMenuItem.Text = u.MenuLabel;
                updateMenuItem.Enabled = u.MenuEnabled;
            }
            if (statusUpdateItem != null)
            {
                string text = null;
                if (u.State == UpdateState.Downloading)
                    text = u.ProgressPercent >= 0 ? "Downloading Update (" + u.ProgressPercent + "%)" : "Downloading Update...";
                else if (u.State == UpdateState.Ready)
                    text = u.MenuLabel;
                bool show = text != null;
                if (show) statusUpdateItem.Text = text;
                if (statusUpdateItem.Visible != show) statusUpdateItem.Visible = show;
                LayoutStatusBarItems();
            }
        }

        private void OnUpdateStatusItemClicked()
        {
            UpdateService u = Updates;
            if (u.State == UpdateState.Downloading)
            {
                if (MessageBox.Show(this, "Downloading codeLess " + (u.Release != null ? u.Release.LatestVersion : "") + "... Cancel the download?",
                        "codeLess Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    u.CancelDownload();
                return;
            }
            CheckForUpdatesInteractive();
        }

        /// <summary>Launch check (update mode start / default) and every 6 h in default mode.</summary>
        private void MaybeAutoCheckForUpdates()
        {
            if (settings == null || SuppressUnsavedPrompts) return;
            EnsureUpdateSubscription();
            string mode = settings.GetEffectiveUpdateMode();
            if (mode == UpdateModes.Default && updatePeriodicTimer == null)
            {
                updatePeriodicTimer = new System.Windows.Forms.Timer();
                updatePeriodicTimer.Interval = 6 * 60 * 60 * 1000;
                updatePeriodicTimer.Tick += (s, e) => MaybeAutoCheckForUpdates();
                updatePeriodicTimer.Start();
            }
            UpdateEnvironment env = Updates.Environment;
            Task.Run(() => UpdateInstaller.CleanupStaleBackup(env));
            if (!UpdateService.ShouldAutoCheck(mode, settings.LastUpdateCheckUtc, NetworkLedger.OfflineMode, 1.0, DateTime.UtcNow)) return;
            RunUpdateCheck(false);
        }

        /// <summary>Help › Check for Updates... (the action follows the update state).</summary>
        private void CheckForUpdatesInteractive()
        {
            EnsureUpdateSubscription();
            UpdateService u = Updates;
            if (u.State == UpdateState.Checking || u.State == UpdateState.Downloading) { OnUpdateStatusItemClicked(); return; }
            if (u.State == UpdateState.Ready)
            {
                if (u.Plan != null && u.Plan.CanInstall) RestartToUpdate();
                else ShowManualInstallInstructions();
                return;
            }
            if (u.State == UpdateState.Available && u.Release != null) { ShowUpdateAvailablePrompt(u.Release, true); return; }
            if (settings != null && settings.GetEffectiveUpdateMode() == UpdateModes.None &&
                MessageBox.Show(this, "Updates are turned off (Settings › Update mode: None). Check once anyway?", "Check for Updates",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                return;
            RunUpdateCheck(true);
        }

        private void RunUpdateCheck(bool interactive)
        {
            UpdateService u = Updates;
            if (u.State == UpdateState.Checking || u.State == UpdateState.Downloading) return;
            if (u.State == UpdateState.Error) u.ClearError();
            if (interactive && statusLeftLabel != null) statusLeftLabel.Text = "Checking for updates...";
            Task.Run(() =>
            {
                UpdateCheckResult r = u.CheckNow(8000) ?? new UpdateCheckResult();
                Action show = () =>
                {
                    if (interactive && statusLeftLabel != null) statusLeftLabel.Text = "";
                    if (r.Success)
                    {
                        settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                        try { settings.Save(); } catch { }
                    }
                    if (!r.Success)
                    {
                        if (interactive)
                            MessageBox.Show(this, (r.Blocked ? "Offline Mode is on, so codeLess did not contact GitHub.\n\n" : "Could not check for updates.\n\n") + r.Error,
                                "Check for Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        u.ClearError();
                        return;
                    }
                    if (!r.IsNewer)
                    {
                        if (interactive)
                            MessageBox.Show(this, "There are currently no updates available. codeLess " + CurrentAppVersionString() + " is the latest version.",
                                "Check for Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    if (!interactive && string.Equals(settings.SkippedUpdateVersion, r.LatestVersion, StringComparison.OrdinalIgnoreCase)) return;
                    if (!interactive && settings.GetEffectiveUpdateMode() == UpdateModes.Default) { u.StartDownload(); return; }
                    ShowUpdateAvailablePrompt(r, interactive);
                };
                if (this.IsHandleCreated && !this.IsDisposed) this.BeginInvoke(show);
            });
        }

        private void ShowUpdateAvailablePrompt(UpdateCheckResult r, bool interactive)
        {
            string notes = (r.ReleaseNotes ?? "").Trim();
            if (notes.Length > 900) notes = notes.Substring(0, 900) + "\n...";
            bool auto = Updates.Environment.SupportsAutomaticInstall;
            DialogResult choice = MessageBox.Show(this,
                "codeLess " + r.LatestVersion + " is available - you have " + CurrentAppVersionString() + ".\n\n" +
                (notes.Length > 0 ? notes + "\n\n" : "") +
                (auto ? "codeLess downloads it, verifies its SHA-256 checksum and installs it when you restart.\n\n" : "") +
                "Yes = " + (auto ? "Download and Install" : "Download") + "      No = Later      Cancel = Skip this version",
                "Update Available", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
            if (choice == DialogResult.Yes)
            {
                if (Updates.StartDownload() && statusLeftLabel != null) statusLeftLabel.Text = "Downloading codeLess " + r.LatestVersion + "...";
            }
            else if (choice == DialogResult.Cancel)
            {
                settings.SkippedUpdateVersion = r.LatestVersion;
                try { settings.Save(); } catch { }
            }
        }

        private void ShowUpdateReadyPrompt()
        {
            UpdateService u = Updates;
            if (u.Plan == null || !u.Plan.CanInstall)
            {
                ShowManualInstallInstructions();
                return;
            }
            DialogResult choice = MessageBox.Show(this, u.ReadyMessage + "\n\nYes = Restart Now      No = Later      Cancel = Release Notes",
                "Restart to Update", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
            if (choice == DialogResult.Yes) RestartToUpdate();
            else if (choice == DialogResult.Cancel && u.Release != null) UpdateChecker.OpenInBrowser(u.Release.ReleasePageUrl);
        }

        private void ShowManualInstallInstructions()
        {
            UpdateInstallPlan plan = Updates.Plan;
            if (plan == null) return;
            if (MessageBox.Show(this, plan.ManualMessage + "\n\nShow the download in Explorer?", "Install Update",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                UpdateInstaller.Reveal(plan.RevealPath);
        }

        /// <summary>Restart to Update: unsaved files go through the normal prompt, then the helper starts and codeLess quits.</summary>
        private void RestartToUpdate()
        {
            UpdateService u = Updates;
            if (u.State != UpdateState.Ready || u.Plan == null || !u.Plan.CanInstall) return;
            if (!ConfirmSaveOrDiscardAllDirtyBuffers(openTabs)) return;
            string error;
            if (!u.LaunchInstaller(out error))
            {
                MessageBox.Show(this, "The update could not be started: " + error, "Restart to Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SuppressUnsavedPrompts = true; // already confirmed above
            this.Close();
        }
    }
}
