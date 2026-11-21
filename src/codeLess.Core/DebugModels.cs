using System;
using System.Collections.Generic;
using System.IO;

namespace codeLess.Studio
{
    public enum DebugSessionState
    {
        NotStarted,
        Initializing,
        Running,
        Stopped,
        Terminated
    }

    public sealed class DapThreadInfo
    {
        public int Id;
        public string Name;
        public override string ToString() { return Name ?? ("Thread " + Id); }
    }

    public sealed class DapStackFrameInfo
    {
        public int Id;
        public string Name;
        public string SourcePath;
        public string SourceName;
        public int SourceReference;
        public int Line;   // 1-based
        public int Column; // 1-based
        public string PresentationHint;
        public int ThreadId;

        public bool HasSource { get { return !string.IsNullOrEmpty(SourcePath); } }

        public override string ToString()
        {
            string where = !string.IsNullOrEmpty(SourceName) ? SourceName : (HasSource ? Path.GetFileName(SourcePath) : "unknown source");
            return (Name ?? "?") + "  " + where + ":" + Line;
        }
    }

    public sealed class DapScopeInfo
    {
        public string Name;
        public int VariablesReference;
        public bool Expensive;
        public int NamedVariables;
        public int IndexedVariables;
    }

    public sealed class DapVariableInfo
    {
        public string Name;
        public string Value;
        public string Type;
        public string EvaluateName;
        public int VariablesReference;
        public int NamedVariables;
        public int IndexedVariables;

        public bool HasChildren { get { return VariablesReference > 0; } }
    }

    public sealed class DapEvaluateResult
    {
        public bool Success;
        public string Result;
        public string Type;
        public int VariablesReference;
        public string Error;
    }

    /// <summary>Exception breakpoint filter advertised by the adapter (capabilities.exceptionBreakpointFilters).</summary>
    public sealed class DapExceptionFilter
    {
        public string Filter;
        public string Label;
        public bool Default;
        public bool Enabled;
    }

    /// <summary>A source breakpoint as the user sees it (gutter + BREAKPOINTS view). Line is 0-based like buffers.</summary>
    public sealed class DebugSourceBreakpoint
    {
        public string FilePath;
        public int Line;
        public bool Enabled = true;
        public string Condition;
        public string HitCondition;
        public string LogMessage;
        /// <summary>Null until an adapter answered; then true / false (false = gray hollow glyph).</summary>
        public bool? Verified;
        public string VerifyMessage;
        public int AdapterId;

        public bool IsLogpoint { get { return !string.IsNullOrEmpty(LogMessage); } }
        public bool IsConditional { get { return !string.IsNullOrEmpty(Condition) || !string.IsNullOrEmpty(HitCondition); } }
    }

    /// <summary>How the editor gutter draws a breakpoint line.</summary>
    public enum DebugBreakpointGlyph
    {
        None,
        Enabled,
        Disabled,
        Conditional,
        Logpoint,
        Unverified
    }

    /// <summary>Per-buffer debug decorations read by both editor canvases (small hooks in their Render/Paint code).</summary>
    public partial class EditorTabBuffer
    {
        /// <summary>0-based line of the current debug frame in this file, or -1.</summary>
        public int DebugStoppedLine = -1;
        /// <summary>True when the stopped line is the top frame (yellow); false for a selected outer frame (green).</summary>
        public bool DebugStoppedIsTopFrame = true;
        /// <summary>Glyph kind per breakpoint line; lines in Breakpoints without an entry draw as Enabled.</summary>
        public Dictionary<int, DebugBreakpointGlyph> BreakpointGlyphs;

        public DebugBreakpointGlyph GetBreakpointGlyph(int line)
        {
            if (Breakpoints == null || !Breakpoints.Contains(line)) return DebugBreakpointGlyph.None;
            DebugBreakpointGlyph g;
            if (BreakpointGlyphs != null && BreakpointGlyphs.TryGetValue(line, out g) && g != DebugBreakpointGlyph.None) return g;
            return DebugBreakpointGlyph.Enabled;
        }
    }

    /// <summary>
    /// Static hooks the editor canvases call (one line each) so the debugger learns about gutter toggles without the
    /// canvases depending on the debug controller.
    /// </summary>
    public static class EditorDebugHooks
    {
        /// <summary>Raised after a gutter click / F9 toggled a breakpoint in <c>buf.Breakpoints</c>.</summary>
        public static event Action<EditorTabBuffer, int> BreakpointToggled;

        public static void NotifyBreakpointToggled(EditorTabBuffer buf, int line)
        {
            Action<EditorTabBuffer, int> h = BreakpointToggled;
            if (h != null && buf != null)
            {
                try { h(buf, line); } catch { }
            }
        }

        /// <summary>Width (px) of the breakpoint glyph margin at the left edge of the gutter.</summary>
        public const int GlyphMarginWidth = 18;
    }

    /// <summary>Path helpers shared by the debugger (case / symlink tolerant comparison of adapter paths).</summary>
    public static class DebugPaths
    {
        public static bool IsWindows { get { return Path.DirectorySeparatorChar == '\\'; } }

        /// <summary>Normalizes for comparison: full path, trimmed separators, macOS /private/tmp ~ /tmp, case on Windows.</summary>
        public static string Key(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string p = path;
            try { p = Path.GetFullPath(path); } catch { }
            p = p.TrimEnd('/', '\\');
            if (!IsWindows)
            {
                if (p.StartsWith("/private/tmp/", StringComparison.Ordinal) || p == "/private/tmp" ||
                    p.StartsWith("/private/var/", StringComparison.Ordinal) || p.StartsWith("/private/etc/", StringComparison.Ordinal))
                    p = p.Substring("/private".Length);
            }
            else
            {
                p = p.Replace('/', '\\').ToLowerInvariant();
            }
            return p;
        }

        public static bool Same(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(Key(a), Key(b), StringComparison.Ordinal);
        }
    }
}
