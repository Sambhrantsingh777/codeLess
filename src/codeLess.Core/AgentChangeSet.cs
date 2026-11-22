using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace codeLess.Studio
{
    /// <summary>One proposed file edit of an agent turn.</summary>
    public sealed class AgentChangeSetFile
    {
        public string FullPath = "";
        public string RelativePath = "";
        /// <summary>The file did not exist when the edit was proposed.</summary>
        public bool IsNewFile;
        /// <summary>Text the proposal was computed against (open buffer or disk), "\n" line endings.</summary>
        public string BaseText = "";
        public string ProposedText = "";
        /// <summary>SHA-1 of the file bytes on disk when the edit was proposed (null = absent): detects later changes.</summary>
        public string DiskHashAtStage;
        /// <summary>"Pending", "Accepted", "Rejected".</summary>
        public string Status = "Pending";
        public int Added;
        public int Removed;

        public bool IsPending { get { return Status == "Pending"; } }
    }

    /// <summary>
    /// The reviewable result of one agent turn (or an adopted Best-of-N candidate): proposed file contents, diff
    /// stats, verification results and per-file accept/reject state. Nothing is written until the user accepts.
    /// </summary>
    public sealed class AgentChangeSet
    {
        public string Id = "cs-" + Guid.NewGuid().ToString("N").Substring(0, 10);
        /// <summary>Per-turn checkpoint id that accepted writes are recorded under.</summary>
        public string TurnId = "";
        public string Label = "";
        public string WorkspaceRoot = "";
        /// <summary>"agent", "repair" or "best-of-n".</summary>
        public string Origin = "agent";
        public string ProviderLabel = "";
        public DateTime CreatedUtc = DateTime.UtcNow;
        public readonly List<AgentChangeSetFile> Files = new List<AgentChangeSetFile>();
        public readonly List<VerificationResult> Verifications = new List<VerificationResult>();
        private string verifiedFingerprint;

        public VerificationResult LatestVerification
        {
            get { return Verifications.Count > 0 ? Verifications[Verifications.Count - 1] : null; }
        }

        public int PendingCount { get { return Files.Count(f => f.IsPending); } }
        public bool HasPending { get { return Files.Any(f => f.IsPending); } }
        public int TotalAdded { get { return Files.Sum(f => f.Added); } }
        public int TotalRemoved { get { return Files.Sum(f => f.Removed); } }

        /// <summary>True when the latest verification passed for exactly the proposals that are still pending.</summary>
        public bool IsVerifiedPassing
        {
            get
            {
                VerificationResult v = LatestVerification;
                return v != null && v.Passed && verifiedFingerprint != null && verifiedFingerprint == Fingerprint();
            }
        }

        public void AddVerification(VerificationResult r)
        {
            if (r == null) return;
            Verifications.Add(r);
            verifiedFingerprint = r.Passed ? Fingerprint() : null;
        }

        /// <summary>
        /// Hash of the proposals that are (or will be) applied: path + text of every file not rejected. Changes when a
        /// proposal is edited or a file is rejected (a subset of a verified change set wasn't verified on its own).
        /// </summary>
        public string Fingerprint()
        {
            StringBuilder sb = new StringBuilder();
            foreach (AgentChangeSetFile f in Files.Where(x => x.Status != "Rejected").OrderBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase))
                sb.Append(f.FullPath).Append('\0').Append(f.ProposedText ?? "").Append('\0');
            return AgentHash.Sha1Hex(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        public Dictionary<string, string> AllProposals()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (AgentChangeSetFile f in Files) d[f.FullPath] = f.ProposedText;
            return d;
        }

        public Dictionary<string, string> PendingProposals()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (AgentChangeSetFile f in Files) if (f.IsPending) d[f.FullPath] = f.ProposedText;
            return d;
        }

        public AgentChangeSetFile Find(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;
            string norm;
            try { norm = Path.GetFullPath(fullPath); } catch { norm = fullPath; }
            foreach (AgentChangeSetFile f in Files)
            {
                string fp;
                try { fp = Path.GetFullPath(f.FullPath); } catch { fp = f.FullPath; }
                if (string.Equals(fp, norm, StringComparison.OrdinalIgnoreCase)) return f;
            }
            return null;
        }

        /// <summary>
        /// Builds a change set from staged contents. <paramref name="baseTextProvider"/> returns the open-buffer text
        /// for a path (null when the file isn't open); otherwise the disk text is the base.
        /// </summary>
        public static AgentChangeSet FromStaged(IDictionary<string, string> staged, string workspaceRoot, Func<string, string> baseTextProvider, string label)
        {
            AgentChangeSet cs = new AgentChangeSet();
            cs.WorkspaceRoot = workspaceRoot ?? "";
            cs.Label = label ?? "";
            if (staged == null) return cs;
            foreach (KeyValuePair<string, string> kv in staged)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                AgentChangeSetFile f = new AgentChangeSetFile();
                f.FullPath = kv.Key;
                f.RelativePath = LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, kv.Key);
                bool exists = File.Exists(kv.Key);
                f.IsNewFile = !exists;
                string buf = baseTextProvider != null ? baseTextProvider(kv.Key) : null;
                f.BaseText = Normalize(buf ?? (exists ? AgentChangeSetApplier.ReadText(kv.Key) : ""));
                f.ProposedText = kv.Value ?? "";
                f.DiskHashAtStage = exists ? AgentHash.FileSha1(kv.Key) : null;
                RecountDiff(f);
                if (!f.IsNewFile && Normalize(f.ProposedText) == f.BaseText) continue; // no-op proposal
                cs.Files.Add(f);
            }
            return cs;
        }

        public static void RecountDiff(AgentChangeSetFile f)
        {
            int added = 0, removed = 0;
            foreach (EditorDiffHunk h in LlmHarnessAndAgentEngine.ComputeLineDiffHunks(f.BaseText, Normalize(f.ProposedText)))
            {
                added += h.AddedLines.Count;
                removed += h.RemovedLines.Count;
            }
            f.Added = added;
            f.Removed = removed;
        }

        public static string Normalize(string s)
        {
            return (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
        }
    }

    public sealed class AgentApplyResult
    {
        public readonly List<string> Written = new List<string>();
        /// <summary>Files changed on disk since the proposal (not written unless overwrite was requested).</summary>
        public readonly List<string> Conflicts = new List<string>();
        public readonly List<string> Errors = new List<string>();
        /// <summary>Informational notes (e.g. a Latin-1 file saved as UTF-8 so the proposal's characters aren't lost).</summary>
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>Writes accepted proposals to disk (encoding / line endings preserved) and records them in the turn checkpoint.</summary>
    public static class AgentChangeSetApplier
    {
        public static string ReadText(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                string eol; Encoding enc; bool bom; bool finalNl;
                return EditorTabBuffer.ReadFilePreservingMetadata(path, out eol, out enc, out bom, out finalNl);
            }
            catch { return ""; }
        }

        /// <summary>
        /// Writes <paramref name="text"/> keeping the existing file's encoding, BOM, line endings and final-newline
        /// state; new files are UTF-8 without BOM with the text's own line endings.
        /// </summary>
        public static void WriteTextLikeExisting(string path, string text)
        {
            string note;
            WriteTextLikeExisting(path, text, out note);
        }

        /// <summary>
        /// <see cref="WriteTextLikeExisting(string, string)"/>; <paramref name="note"/> is set when the existing file's legacy
        /// encoding couldn't represent the text and it was written as UTF-8 instead (nothing is replaced by '?').
        /// </summary>
        public static void WriteTextLikeExisting(string path, string text, out string note)
        {
            note = null;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string raw = text ?? "";
            if (File.Exists(path))
            {
                string eol; Encoding enc; bool bom; bool finalNl;
                EditorTabBuffer.ReadFilePreservingMetadata(path, out eol, out enc, out bom, out finalNl);
                if (new FileInfo(path).Length == 0)
                {
                    eol = raw.Contains("\r\n") ? "\r\n" : "\n";
                    finalNl = raw.EndsWith("\n");
                }
                Encoding before = enc;
                string norm0 = (raw ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
                if (!EditorTabBuffer.CanEncodeLosslessly(norm0, enc) && !(enc is UTF8Encoding) && !(enc is UnicodeEncoding) && !(enc is UTF32Encoding))
                {
                    enc = new UTF8Encoding(false);
                    bom = false;
                    note = Path.GetFileName(path) + " was saved as UTF-8 (it was " + before.WebName + ", which can't hold some of the new characters).";
                }
                EditorTabBuffer.WriteFilePreservingMetadata(path, raw, enc, bom, eol, finalNl);
                return;
            }
            string newEol = raw.Contains("\r\n") ? "\r\n" : "\n";
            string norm = AgentChangeSet.Normalize(raw);
            EditorTabBuffer.WriteFilePreservingMetadata(path, norm, new UTF8Encoding(false), false, newEol, norm.EndsWith("\n"));
        }

        /// <summary>
        /// Accepts <paramref name="files"/> of <paramref name="cs"/>: checks each file still matches what the proposal was
        /// computed against, writes it and records pre/post bytes in <paramref name="store"/> under cs.TurnId.
        /// </summary>
        public static AgentApplyResult Accept(AgentChangeSet cs, IEnumerable<AgentChangeSetFile> files, AgentTurnCheckpointStore store, bool overwriteConflicts)
        {
            AgentApplyResult res = new AgentApplyResult();
            if (cs == null || files == null) return res;
            foreach (AgentChangeSetFile f in files.ToList())
            {
                if (!f.IsPending) continue;
                // Symlink-aware check at write time too: a link created inside the workspace after the proposal was staged
                // (e.g. by an approved terminal command) must not redirect the write outside it.
                if (!LlmHarnessAndAgentEngine.IsPathInsideWorkspaceStrict(cs.WorkspaceRoot, f.FullPath))
                {
                    res.Errors.Add(f.RelativePath + ": outside the workspace");
                    continue;
                }
                string nowHash = File.Exists(f.FullPath) ? AgentHash.FileSha1(f.FullPath) : null;
                if (!overwriteConflicts && nowHash != f.DiskHashAtStage)
                {
                    res.Conflicts.Add(f.RelativePath);
                    continue;
                }
                try
                {
                    byte[] pre = File.Exists(f.FullPath) ? File.ReadAllBytes(f.FullPath) : null;
                    string note;
                    WriteTextLikeExisting(f.FullPath, f.ProposedText, out note);
                    if (note != null) res.Notes.Add(note);
                    byte[] post = File.ReadAllBytes(f.FullPath);
                    f.Status = "Accepted";
                    res.Written.Add(f.FullPath);
                    if (store != null) store.RecordFileWrite(cs.TurnId, cs.Label, f.FullPath, pre, post);
                }
                catch (Exception ex)
                {
                    res.Errors.Add(f.RelativePath + ": " + ex.Message);
                }
            }
            return res;
        }

        public static void Reject(IEnumerable<AgentChangeSetFile> files)
        {
            if (files == null) return;
            foreach (AgentChangeSetFile f in files) if (f.IsPending) f.Status = "Rejected";
        }
    }

    public static class AgentHash
    {
        public static string Sha1Hex(byte[] data)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(data ?? new byte[0]);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static string FileSha1(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (SHA1 sha = SHA1.Create())
                {
                    byte[] h = sha.ComputeHash(fs);
                    StringBuilder sb = new StringBuilder(h.Length * 2);
                    foreach (byte b in h) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return null; }
        }
    }
}
