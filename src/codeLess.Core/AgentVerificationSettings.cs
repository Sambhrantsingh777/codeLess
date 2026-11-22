using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>What happens after the agent proposes edits (staged, not written).</summary>
    public enum AgentVerifyMode
    {
        /// <summary>No verification (previous behaviour).</summary>
        Off = 0,
        /// <summary>Run the verification command on a temporary copy with the staged edits applied.</summary>
        Verify = 1,
        /// <summary>Verify, and on failure send the diagnostics back to the model for up to N repair turns.</summary>
        VerifyAndRepair = 2
    }

    /// <summary>
    /// Agent trust settings (verification gate, auto-repair, Best-of-N, per-turn checkpoints). Persisted in
    /// settings.json with the rest of IdePersistentSettings on both UIs; 0 / empty values mean "default".
    /// Strict C# 5 (shared with WinForms).
    /// </summary>
    public partial class IdePersistentSettings
    {
        /// <summary>"off" (default), "verify" or "repair".</summary>
        public string AgentVerifyModeId { get; set; }
        /// <summary>User-level verification command (codeless.verifyCommand). Workspace values override it.</summary>
        public string AgentVerifyCommand { get; set; }
        /// <summary>Verification timeout in seconds (default 300).</summary>
        public int AgentVerifyTimeoutSeconds { get; set; }
        /// <summary>Maximum automatic repair turns after a failed verification (default 2, max 5).</summary>
        public int AgentRepairMaxAttempts { get; set; }
        /// <summary>Number of Best-of-N candidates (default 2, max 4).</summary>
        public int AgentBestOfNCount { get; set; }
        /// <summary>Workspace+command pairs the user allowed to run without asking again ("Always allow").</summary>
        public List<string> AgentVerifyApprovedCommands { get; set; }

        public const int DefaultAgentVerifyTimeoutSeconds = 300;
        public const int DefaultAgentRepairMaxAttempts = 2;
        public const int DefaultAgentBestOfNCount = 2;

        public AgentVerifyMode EffectiveAgentVerifyMode
        {
            get { return ParseAgentVerifyMode(AgentVerifyModeId); }
        }

        public void SetAgentVerifyMode(AgentVerifyMode mode)
        {
            AgentVerifyModeId = AgentVerifyModeToId(mode);
        }

        public int EffectiveAgentVerifyTimeoutSeconds
        {
            get { return AgentVerifyTimeoutSeconds > 0 ? Math.Max(10, Math.Min(3600, AgentVerifyTimeoutSeconds)) : DefaultAgentVerifyTimeoutSeconds; }
        }

        public int EffectiveAgentRepairMaxAttempts
        {
            get { return AgentRepairMaxAttempts > 0 ? Math.Min(5, AgentRepairMaxAttempts) : DefaultAgentRepairMaxAttempts; }
        }

        public int EffectiveAgentBestOfNCount
        {
            get { return AgentBestOfNCount > 1 ? Math.Min(4, AgentBestOfNCount) : DefaultAgentBestOfNCount; }
        }

        public static AgentVerifyMode ParseAgentVerifyMode(string id)
        {
            string s = (id ?? "").Trim().ToLowerInvariant();
            if (s == "verify") return AgentVerifyMode.Verify;
            if (s == "repair" || s == "verify+repair" || s == "verifyandrepair") return AgentVerifyMode.VerifyAndRepair;
            return AgentVerifyMode.Off;
        }

        public static string AgentVerifyModeToId(AgentVerifyMode mode)
        {
            if (mode == AgentVerifyMode.Verify) return "verify";
            if (mode == AgentVerifyMode.VerifyAndRepair) return "repair";
            return "off";
        }

        public static string AgentVerifyModeLabel(AgentVerifyMode mode)
        {
            if (mode == AgentVerifyMode.Verify) return "Verify";
            if (mode == AgentVerifyMode.VerifyAndRepair) return "Verify + Auto-Repair";
            return "Verify: Off";
        }

        public bool IsAgentVerifyCommandApproved(string workspaceRoot, string commandLine)
        {
            if (AgentVerifyApprovedCommands == null) return false;
            string key = AgentVerifyApprovalKey(workspaceRoot, commandLine);
            return AgentVerifyApprovedCommands.Contains(key);
        }

        public void ApproveAgentVerifyCommand(string workspaceRoot, string commandLine)
        {
            if (AgentVerifyApprovedCommands == null) AgentVerifyApprovedCommands = new List<string>();
            string key = AgentVerifyApprovalKey(workspaceRoot, commandLine);
            if (!AgentVerifyApprovedCommands.Contains(key)) AgentVerifyApprovedCommands.Add(key);
            while (AgentVerifyApprovedCommands.Count > 200) AgentVerifyApprovedCommands.RemoveAt(0);
        }

        public static string AgentVerifyApprovalKey(string workspaceRoot, string commandLine)
        {
            string ws = workspaceRoot ?? "";
            try { if (ws.Length > 0) ws = System.IO.Path.GetFullPath(ws).TrimEnd('\\', '/'); } catch { }
            return ws + "\n" + (commandLine ?? "").Trim();
        }
    }
}

namespace codeLess.Studio
{
    /// <summary>
    /// Status colors for verification results on both UIs (VS Code testing.iconPassed / errorForeground /
    /// editorWarning values for light and dark themes). Used for the result glyph and label only, never for buttons.
    /// </summary>
    public static class AgentTrustColors
    {
        public static System.Drawing.Color Passed(StudioTheme t)
        {
            return UiTokens.IsLight(t) ? System.Drawing.Color.FromArgb(56, 138, 52) : System.Drawing.Color.FromArgb(115, 201, 145);
        }

        public static System.Drawing.Color Failed(StudioTheme t)
        {
            return UiTokens.IsLight(t) ? System.Drawing.Color.FromArgb(161, 38, 13) : System.Drawing.Color.FromArgb(244, 135, 113);
        }

        public static System.Drawing.Color Warning(StudioTheme t)
        {
            return UiTokens.IsLight(t) ? System.Drawing.Color.FromArgb(191, 136, 3) : System.Drawing.Color.FromArgb(204, 167, 0);
        }

        public static System.Drawing.Color ForStatus(StudioTheme t, VerificationStatus s)
        {
            if (s == VerificationStatus.Passed) return Passed(t);
            if (s == VerificationStatus.Failed || s == VerificationStatus.TimedOut) return Failed(t);
            if (s == VerificationStatus.Error || s == VerificationStatus.Denied) return Warning(t);
            return t.FgSecondary;
        }
    }
}
