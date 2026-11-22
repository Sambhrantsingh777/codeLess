using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace codeLess.Studio
{
    /// <summary>VS Code "update.mode": none / manual / start / default.</summary>
    public static class UpdateModes
    {
        /// <summary>Never check (Check for Updates explains how to turn it back on).</summary>
        public const string None = "none";
        /// <summary>Only Help / app menu › Check for Updates (no automatic requests).</summary>
        public const string Manual = "manual";
        /// <summary>Check once at start-up and offer the update; download only when asked.</summary>
        public const string Start = "start";
        /// <summary>Check at start-up and every few hours, download in the background, then offer Restart to Update.</summary>
        public const string Default = "default";

        public static readonly string[] All = { None, Manual, Start, Default };

        public static string Label(string mode)
        {
            switch (Normalize(mode))
            {
                case None: return "None (never check)";
                case Manual: return "Manual (only when I check)";
                case Start: return "Check at start-up";
                default: return "Default (check and download automatically)";
            }
        }

        public static string Normalize(string mode)
        {
            string m = (mode ?? "").Trim().ToLowerInvariant();
            foreach (string a in All) if (a == m) return a;
            return Default;
        }

        public static bool ChecksAutomatically(string mode)
        {
            string m = Normalize(mode);
            return m == Start || m == Default;
        }
    }

    public partial class IdePersistentSettings
    {
        /// <summary>
        /// update.mode (VS Code): "none", "manual", "start" or "default". Empty in settings written before v3.3.4: then it is
        /// derived from <see cref="AutoCheckForUpdates"/> (on → default, off → manual), see <see cref="GetEffectiveUpdateMode"/>.
        /// </summary>
        public string UpdateMode { get; set; }

        /// <summary>The update mode, migrating the old on/off setting when <see cref="UpdateMode"/> is unset.</summary>
        public string GetEffectiveUpdateMode()
        {
            if (string.IsNullOrEmpty(UpdateMode)) return AutoCheckForUpdates ? UpdateModes.Default : UpdateModes.Manual;
            return UpdateModes.Normalize(UpdateMode);
        }

        /// <summary>Sets the mode and keeps the legacy <see cref="AutoCheckForUpdates"/> flag in sync (older builds read it).</summary>
        public void SetUpdateMode(string mode)
        {
            UpdateMode = UpdateModes.Normalize(mode);
            AutoCheckForUpdates = UpdateModes.ChecksAutomatically(UpdateMode);
        }
    }

    public enum UpdateState { Idle, Checking, Available, Downloading, Ready, Error }

    /// <summary>
    /// The update flow shared by the WinForms and Avalonia workbenches (VS Code style): Check for Updates... →
    /// Downloading Update... → Restart to Update. Work runs on worker threads; <see cref="Changed"/> is raised on those
    /// threads (marshal to the UI). Menu labels come from <see cref="MenuLabel"/>. C# 5.
    /// </summary>
    public sealed class UpdateService
    {
        private readonly object gate = new object();
        private volatile bool cancelRequested;

        public UpdateService(string currentVersion, UpdateEnvironment environment, string workRoot)
        {
            CurrentVersion = currentVersion ?? "0.0.0";
            Environment = environment ?? new UpdateEnvironment();
            WorkRoot = string.IsNullOrEmpty(workRoot) ? Path.Combine(Path.GetTempPath(), "codeLess-update") : workRoot;
            State = UpdateState.Idle;
            LastError = "";
        }

        public string CurrentVersion { get; private set; }
        public UpdateEnvironment Environment { get; private set; }
        public string WorkRoot { get; private set; }
        public UpdateState State { get; private set; }
        public UpdateCheckResult Release { get; private set; }
        public UpdateDownloadResult Download { get; private set; }
        public UpdateInstallPlan Plan { get; private set; }
        public string LastError { get; private set; }
        public long BytesDownloaded { get; private set; }
        public long BytesTotal { get; private set; }

        /// <summary>Raised (on a worker thread) whenever <see cref="State"/> or the progress changes.</summary>
        public event EventHandler Changed;

        /// <summary>The release asset this install updates from.</summary>
        public string AssetName { get { return UpdateAssets.AssetNameFor(Environment); } }

        /// <summary>Download progress 0..100 (-1 when the size is unknown).</summary>
        public int ProgressPercent
        {
            get { return BytesTotal > 0 ? (int)Math.Min(100, BytesDownloaded * 100 / BytesTotal) : -1; }
        }

        /// <summary>Label of the Check for Updates menu item for the current state.</summary>
        public string MenuLabel
        {
            get
            {
                switch (State)
                {
                    case UpdateState.Checking: return "Checking for Updates...";
                    case UpdateState.Available: return "Download Update...";
                    case UpdateState.Downloading: return "Downloading Update...";
                    case UpdateState.Ready: return Plan != null && Plan.CanInstall ? "Restart to Update" : "Install Update...";
                    default: return "Check for Updates...";
                }
            }
        }

        /// <summary>False while checking / downloading (like VS Code's disabled item).</summary>
        public bool MenuEnabled
        {
            get { return State != UpdateState.Checking && State != UpdateState.Downloading; }
        }

        /// <summary>True when an update is downloaded and verified (badge / notification).</summary>
        public bool IsReady { get { return State == UpdateState.Ready; } }

        private void Raise()
        {
            EventHandler h = Changed;
            if (h != null)
            {
                try { h(this, EventArgs.Empty); } catch { }
            }
        }

        private void SetState(UpdateState s, string error)
        {
            lock (gate)
            {
                State = s;
                if (error != null) LastError = error;
            }
            Raise();
        }

        /// <summary>Blocking check (worker thread). Moves to Available when a newer release exists, else back to Idle.</summary>
        public UpdateCheckResult CheckNow(int timeoutMs)
        {
            lock (gate)
            {
                if (State == UpdateState.Downloading || State == UpdateState.Ready || State == UpdateState.Checking) return Release;
                State = UpdateState.Checking;
            }
            Raise();
            UpdateCheckResult r = UpdateChecker.Check(CurrentVersion, timeoutMs);
            lock (gate)
            {
                if (r.Success) Release = r;
                if (r.Success && r.IsNewer) State = UpdateState.Available;
                else State = r.Success ? UpdateState.Idle : UpdateState.Error;
                LastError = r.Success ? "" : (r.Error ?? "");
            }
            Raise();
            return r;
        }

        /// <summary>Starts downloading + verifying + preparing the release on a background thread.</summary>
        public bool StartDownload()
        {
            UpdateCheckResult release;
            lock (gate)
            {
                if (State == UpdateState.Downloading || State == UpdateState.Ready) return false;
                if (Release == null || !Release.IsNewer) return false;
                release = Release;
                State = UpdateState.Downloading;
                BytesDownloaded = 0;
                UpdateReleaseAsset asset = release.FindAsset(AssetName);
                BytesTotal = asset != null ? asset.Size : 0;
                cancelRequested = false;
            }
            Raise();
            Thread t = new Thread(delegate () { DownloadWorker(release); });
            t.IsBackground = true;
            t.Name = "codeLess update download";
            t.Start();
            return true;
        }

        /// <summary>Blocking download + verification + preparation (tests call this directly).</summary>
        public void DownloadWorker(UpdateCheckResult release)
        {
            string dir = Path.Combine(WorkRoot, SafeVersion(release.LatestVersion));
            long lastRaise = 0;
            UpdateDownloadResult d = UpdateDownloader.Download(release, AssetName, dir,
                delegate (long done, long total)
                {
                    BytesDownloaded = done;
                    if (total > 0) BytesTotal = total;
                    long now = System.Environment.TickCount;
                    if (Math.Abs(now - lastRaise) > 200 || (total > 0 && done >= total)) { lastRaise = now; Raise(); }
                },
                delegate () { return cancelRequested; });
            Download = d;
            if (!d.Success)
            {
                SetState(d.Cancelled ? UpdateState.Available : UpdateState.Error, d.Cancelled ? "" : d.Error);
                return;
            }
            UpdateInstallPlan plan = UpdateInstaller.Prepare(Environment, d.FilePath, release.LatestVersion, Path.Combine(dir, "install"));
            lock (gate) { Plan = plan; }
            SetState(UpdateState.Ready, "");
        }

        /// <summary>Tests only: puts the service into a state (raises <see cref="Changed"/>).</summary>
        public void SetStateForTests(UpdateState state, UpdateCheckResult release, UpdateInstallPlan plan)
        {
            lock (gate)
            {
                Release = release;
                Plan = plan;
            }
            SetState(state, "");
        }

        public void CancelDownload()
        {
            cancelRequested = true;
        }

        /// <summary>Back to Idle after an error was shown (keeps the last release).</summary>
        public void ClearError()
        {
            lock (gate) { if (State == UpdateState.Error) State = UpdateState.Idle; }
            Raise();
        }

        /// <summary>Starts the helper that replaces the app after this process exits. The caller then quits.</summary>
        public bool LaunchInstaller(out string error)
        {
            UpdateInstallPlan plan = Plan;
            if (State != UpdateState.Ready || plan == null || !plan.CanInstall) { error = "No update is ready to install."; return false; }
            return UpdateInstaller.Launch(plan, out error);
        }

        /// <summary>Text for the "update ready" notification.</summary>
        public string ReadyMessage
        {
            get
            {
                string v = Release != null ? Release.LatestVersion : "";
                if (Plan != null && Plan.CanInstall) return "codeLess " + v + " has been downloaded and verified. Restart to update.";
                return "codeLess " + v + " has been downloaded and verified.";
            }
        }

        private static string SafeVersion(string v)
        {
            string s = (v ?? "latest").Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Length == 0 ? "latest" : s;
        }

        /// <summary>Whether the start-up / periodic check should run now (respects the mode, Offline Mode and a minimum interval).</summary>
        public static bool ShouldAutoCheck(string mode, string lastCheckUtc, bool offline, double minHours, DateTime nowUtc)
        {
            if (offline || !UpdateModes.ChecksAutomatically(mode)) return false;
            DateTime last;
            if (!string.IsNullOrEmpty(lastCheckUtc) &&
                DateTime.TryParse(lastCheckUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out last) &&
                (nowUtc - last).TotalHours < minHours && (nowUtc - last).TotalHours >= 0)
                return false;
            return true;
        }
    }
}
