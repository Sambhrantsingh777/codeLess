using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace codeLess.Studio
{
    /// <summary>One file touched by an agent turn. Hashes are SHA-1 of the raw bytes (null = the file did not exist).</summary>
    public sealed class TurnFileChange
    {
        /// <summary>Workspace-relative path with '/' separators.</summary>
        public string Path = "";
        public string PreHash;
        public string PostHash;
        /// <summary>The state before the turn wasn't captured (non-git workspace over the snapshot budget): can't be restored.</summary>
        public bool PreUnavailable;

        public string Kind
        {
            get
            {
                if (PreHash == null && !PreUnavailable) return PostHash == null ? "unchanged" : "created";
                if (PostHash == null) return "deleted";
                return "modified";
            }
        }
    }

    /// <summary>A recorded agent turn that changed files (accepted edits and/or effects of its terminal commands).</summary>
    public sealed class AgentTurnRecord
    {
        public string Id = "";
        public string Label = "";
        public DateTime CreatedUtc = DateTime.UtcNow;
        public bool Undone;
        public readonly List<TurnFileChange> Files = new List<TurnFileChange>();

        public TurnFileChange Find(string relPath)
        {
            foreach (TurnFileChange f in Files) if (string.Equals(f.Path, relPath, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        /// <summary>"a.cs → b.cs" for a deleted+created pair with identical content in this turn.</summary>
        public List<string> DescribeFiles()
        {
            List<string> res = new List<string>();
            HashSet<TurnFileChange> used = new HashSet<TurnFileChange>();
            foreach (TurnFileChange del in Files)
            {
                if (del.Kind != "deleted" || used.Contains(del)) continue;
                foreach (TurnFileChange cr in Files)
                {
                    if (cr.Kind != "created" || used.Contains(cr) || cr.PostHash != del.PreHash) continue;
                    res.Add("renamed " + del.Path + " → " + cr.Path);
                    used.Add(del);
                    used.Add(cr);
                    break;
                }
            }
            foreach (TurnFileChange f in Files)
            {
                if (used.Contains(f) || f.Kind == "unchanged") continue;
                res.Add(f.Kind + " " + f.Path);
            }
            return res;
        }
    }

    /// <summary>One file in an undo / redo plan.</summary>
    public sealed class TurnRestoreItem
    {
        public string Path = "";
        /// <summary>Hash the file must currently have (the state the turn(s) left).</summary>
        public string ExpectedHash;
        /// <summary>Hash the file has now (null = absent).</summary>
        public string ActualHash;
        /// <summary>Hash to restore (null = delete the file).</summary>
        public string TargetHash;
        public bool Conflict;
        public string ConflictReason = "";
        public bool Unrestorable;
        public bool AlreadyAtTarget { get { return ActualHash == TargetHash && !Unrestorable; } }

        /// <summary>"restore", "delete", "recreate".</summary>
        public string Action
        {
            get
            {
                if (TargetHash == null) return "delete";
                if (ActualHash == null) return "recreate";
                return "restore";
            }
        }
    }

    public sealed class TurnRestorePlan
    {
        public bool IsRedo;
        public readonly List<string> TurnIds = new List<string>();
        public readonly List<TurnRestoreItem> Items = new List<TurnRestoreItem>();
        public bool HasConflicts { get { return Items.Any(i => i.Conflict); } }
        public IEnumerable<TurnRestoreItem> Actionable { get { return Items.Where(i => !i.AlreadyAtTarget); } }
    }

    public sealed class TurnRestoreResult
    {
        public readonly List<string> Restored = new List<string>();
        public readonly List<string> Skipped = new List<string>();
        public readonly List<string> Errors = new List<string>();
        /// <summary>Overwritten user versions kept as backups (workspace path -> backup file).</summary>
        public readonly Dictionary<string, string> Backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool Completed;
    }

    /// <summary>
    /// Disk-backed per-turn checkpoints for one workspace (~/.codeless/checkpoints/turns/&lt;hash&gt;): a timeline of
    /// agent turns with the before/after bytes of every file they changed (content-addressed blobs), "Undo this turn",
    /// "Undo back to here" and redo of the last undo. Restores are refused per file when the file changed after the
    /// turn (user edits, later turns, unsaved editor buffers) unless the user chooses to overwrite; overwritten
    /// versions are kept as blobs so redo brings them back. Strict C# 5, thread-safe.
    /// </summary>
    public sealed class AgentTurnCheckpointStore
    {
        public const int MaxTurns = 50;
        private static string rootOverride;
        private readonly object gate = new object();
        private readonly List<AgentTurnRecord> turns = new List<AgentTurnRecord>();
        private TurnRestorePlan redoPlan; // inverse of the last undo
        /// <summary>Turns this process removed: another window's copy of the timeline must not bring them back.</summary>
        private readonly HashSet<string> removedTurnIds = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Blobs younger than this are never garbage-collected (another window may have written them for a turn it hasn't saved yet).</summary>
        public static TimeSpan BlobGcMinAge = TimeSpan.FromHours(2);
        private TurnRestorePlan undoOfRedo;

        public string WorkspaceRoot { get; private set; }
        public string StoreDir { get; private set; }
        public string BlobDir { get; private set; }
        public event Action Changed;

        /// <summary>Root for all workspaces (tests point this at a temp folder).</summary>
        public static string CheckpointsRoot
        {
            get
            {
                if (!string.IsNullOrEmpty(rootOverride)) return rootOverride;
                return System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codeless"), "checkpoints"), "turns");
            }
            set { rootOverride = value; }
        }

        public AgentTurnCheckpointStore(string workspaceRoot)
        {
            string ws = workspaceRoot ?? "";
            try { if (ws.Length > 0) ws = System.IO.Path.GetFullPath(ws).TrimEnd('\\', '/'); } catch { }
            WorkspaceRoot = ws;
            string key = AgentHash.Sha1Hex(Encoding.UTF8.GetBytes(ws.ToLowerInvariant())).Substring(0, 16);
            StoreDir = System.IO.Path.Combine(CheckpointsRoot, key);
            BlobDir = System.IO.Path.Combine(StoreDir, "blobs");
            Load();
        }

        public List<AgentTurnRecord> Turns
        {
            get { lock (gate) return new List<AgentTurnRecord>(turns); }
        }

        public AgentTurnRecord FindTurn(string id)
        {
            lock (gate) return turns.FirstOrDefault(t => t.Id == id);
        }

        public bool CanRedo { get { lock (gate) return redoPlan != null; } }

        public string RelativePath(string fullPath)
        {
            return VerificationOverlay.RelativeTo(WorkspaceRoot, fullPath).Replace('\\', '/');
        }

        public string FullPath(string relPath)
        {
            return System.IO.Path.Combine(WorkspaceRoot, relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }

        // ------------------------------------------------------------------ blobs

        public string StoreBlob(byte[] bytes)
        {
            if (bytes == null) return null;
            string h = AgentHash.Sha1Hex(bytes);
            string p = BlobPath(h);
            if (!File.Exists(p))
            {
                Directory.CreateDirectory(BlobDir);
                string tmp = p + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                try { File.Move(tmp, p); } catch { try { File.Delete(tmp); } catch { } }
            }
            return h;
        }

        public string BlobPath(string hash)
        {
            return System.IO.Path.Combine(BlobDir, hash);
        }

        public byte[] ReadBlob(string hash)
        {
            if (hash == null) return null;
            string p = BlobPath(hash);
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }

        public bool HasBlob(string hash)
        {
            return hash != null && File.Exists(BlobPath(hash));
        }

        // ------------------------------------------------------------------ recording

        private AgentTurnRecord GetOrCreateTurn(string turnId, string label)
        {
            AgentTurnRecord t = turns.FirstOrDefault(x => x.Id == turnId);
            if (t == null)
            {
                t = new AgentTurnRecord();
                t.Id = string.IsNullOrEmpty(turnId) ? NewTurnId() : turnId;
                t.Label = label ?? "";
                turns.Add(t);
            }
            else if (t.Undone)
            {
                // New changes on an undone turn start a fresh entry (the undone one stays in history).
                t = new AgentTurnRecord();
                t.Id = NewTurnId();
                t.Label = label ?? "";
                turns.Add(t);
            }
            // Any new change invalidates redo (standard undo-stack semantics).
            redoPlan = null;
            undoOfRedo = null;
            return t;
        }

        public static string NewTurnId()
        {
            return "turn-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private static void Merge(AgentTurnRecord t, string rel, string preHash, string postHash, bool preUnavailable)
        {
            TurnFileChange existing = t.Find(rel);
            if (existing == null)
            {
                existing = new TurnFileChange();
                existing.Path = rel;
                existing.PreHash = preHash;
                existing.PreUnavailable = preUnavailable;
                t.Files.Add(existing);
            }
            existing.PostHash = postHash;
            if (existing.PreHash == existing.PostHash && !existing.PreUnavailable) t.Files.Remove(existing);
        }

        /// <summary>Records one write made on behalf of turn <paramref name="turnId"/> (pre/post null = absent).</summary>
        public void RecordFileWrite(string turnId, string label, string fullPath, byte[] pre, byte[] post)
        {
            lock (gate)
            {
                // Blobs are written under the lock so a concurrent Save() can't collect them before they're referenced.
                string preH = StoreBlob(pre);
                string postH = StoreBlob(post);
                if (preH == postH) return;
                AgentTurnRecord t = GetOrCreateTurn(turnId, label);
                Merge(t, RelativePath(fullPath), preH, postH, false);
                if (t.Files.Count == 0) { turns.Remove(t); removedTurnIds.Add(t.Id); }
                Trim();
                Save();
            }
            RaiseChanged();
        }

        /// <summary>Records changes detected by <see cref="WorkspaceChangeTracker"/> (terminal commands of the turn).</summary>
        public void RecordChanges(string turnId, string label, List<TurnFileChange> changes)
        {
            if (changes == null || changes.Count == 0) return;
            lock (gate)
            {
                AgentTurnRecord t = GetOrCreateTurn(turnId, label);
                foreach (TurnFileChange c in changes) Merge(t, c.Path, c.PreHash, c.PostHash, c.PreUnavailable);
                if (t.Files.Count == 0) { turns.Remove(t); removedTurnIds.Add(t.Id); }
                Trim();
                Save();
            }
            RaiseChanged();
        }

        private void Trim()
        {
            while (turns.Count > MaxTurns) { removedTurnIds.Add(turns[0].Id); turns.RemoveAt(0); }
        }

        private void RaiseChanged()
        {
            Action h = Changed;
            if (h != null) { try { h(); } catch { } }
        }

        // ------------------------------------------------------------------ undo / redo

        private string CurrentHash(string rel)
        {
            string full = FullPath(rel);
            return File.Exists(full) ? AgentHash.FileSha1(full) : null;
        }

        private static bool IsDirty(ICollection<string> dirtyFullPaths, string full)
        {
            if (dirtyFullPaths == null) return false;
            foreach (string d in dirtyFullPaths)
            {
                try { if (string.Equals(System.IO.Path.GetFullPath(d), System.IO.Path.GetFullPath(full), StringComparison.OrdinalIgnoreCase)) return true; }
                catch { }
            }
            return false;
        }

        /// <summary>
        /// Plans undoing turn <paramref name="turnId"/> (and, with <paramref name="includeLaterTurns"/>, every later
        /// turn that isn't undone yet: "Undo back to here"). <paramref name="dirtyFullPaths"/> are files with unsaved
        /// editor changes (always conflicts). Returns null when the turn doesn't exist or is already undone.
        /// </summary>
        public TurnRestorePlan PlanUndo(string turnId, bool includeLaterTurns, ICollection<string> dirtyFullPaths)
        {
            lock (gate)
            {
                int idx = turns.FindIndex(t => t.Id == turnId);
                if (idx < 0 || turns[idx].Undone) return null;
                List<AgentTurnRecord> set = new List<AgentTurnRecord>();
                set.Add(turns[idx]);
                if (includeLaterTurns)
                    for (int i = idx + 1; i < turns.Count; i++) if (!turns[i].Undone) set.Add(turns[i]);
                TurnRestorePlan plan = new TurnRestorePlan();
                foreach (AgentTurnRecord t in set) plan.TurnIds.Add(t.Id);

                // Later, not-undone turns outside the set that touched the same file make the file "changed after".
                Dictionary<string, string> laterTouch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = idx + 1; i < turns.Count; i++)
                {
                    if (turns[i].Undone || set.Contains(turns[i])) continue;
                    foreach (TurnFileChange f in turns[i].Files) laterTouch[f.Path] = turns[i].Label;
                }

                Dictionary<string, TurnRestoreItem> byPath = new Dictionary<string, TurnRestoreItem>(StringComparer.OrdinalIgnoreCase);
                List<string> order = new List<string>();
                foreach (AgentTurnRecord t in set) // oldest -> newest
                {
                    foreach (TurnFileChange f in t.Files)
                    {
                        TurnRestoreItem it;
                        if (!byPath.TryGetValue(f.Path, out it))
                        {
                            it = new TurnRestoreItem();
                            it.Path = f.Path;
                            it.TargetHash = f.PreHash;
                            it.Unrestorable = f.PreUnavailable || (f.PreHash != null && !HasBlob(f.PreHash));
                            byPath[f.Path] = it;
                            order.Add(f.Path);
                        }
                        it.ExpectedHash = f.PostHash; // newest wins
                    }
                }
                foreach (string p in order)
                {
                    TurnRestoreItem it = byPath[p];
                    it.ActualHash = CurrentHash(p);
                    if (IsDirty(dirtyFullPaths, FullPath(p)))
                    {
                        it.Conflict = true;
                        it.ConflictReason = "has unsaved changes in the editor";
                    }
                    else if (it.ActualHash != it.ExpectedHash && it.ActualHash != it.TargetHash)
                    {
                        it.Conflict = true;
                        string later;
                        it.ConflictReason = laterTouch.TryGetValue(p, out later)
                            ? "was changed by a later turn (" + Shorten(later) + ")"
                            : (it.ActualHash == null ? "was deleted after this turn" : "was edited after this turn");
                    }
                    plan.Items.Add(it);
                }
                return plan;
            }
        }

        /// <summary>Plans redoing the last undo; null when there is nothing to redo.</summary>
        public TurnRestorePlan PlanRedo(ICollection<string> dirtyFullPaths)
        {
            lock (gate)
            {
                if (redoPlan == null) return null;
                TurnRestorePlan plan = new TurnRestorePlan();
                plan.IsRedo = true;
                plan.TurnIds.AddRange(redoPlan.TurnIds);
                foreach (TurnRestoreItem r in redoPlan.Items)
                {
                    TurnRestoreItem it = new TurnRestoreItem();
                    it.Path = r.Path;
                    it.ExpectedHash = r.ExpectedHash;
                    it.TargetHash = r.TargetHash;
                    it.Unrestorable = r.TargetHash != null && !HasBlob(r.TargetHash);
                    it.ActualHash = CurrentHash(r.Path);
                    if (IsDirty(dirtyFullPaths, FullPath(r.Path))) { it.Conflict = true; it.ConflictReason = "has unsaved changes in the editor"; }
                    else if (it.ActualHash != it.ExpectedHash && it.ActualHash != it.TargetHash) { it.Conflict = true; it.ConflictReason = "was edited after the undo"; }
                    plan.Items.Add(it);
                }
                return plan;
            }
        }

        /// <summary>
        /// Executes an undo or redo plan. Conflicting files are skipped unless <paramref name="overwriteConflicts"/>;
        /// overwritten versions are stored as blobs (listed in <see cref="TurnRestoreResult.Backups"/>).
        /// </summary>
        public TurnRestoreResult Execute(TurnRestorePlan plan, bool overwriteConflicts)
        {
            TurnRestoreResult res = new TurnRestoreResult();
            if (plan == null) return res;
            TurnRestorePlan inverse = new TurnRestorePlan();
            inverse.IsRedo = !plan.IsRedo;
            inverse.TurnIds.AddRange(plan.TurnIds);
            lock (gate)
            {
                foreach (TurnRestoreItem it in plan.Items)
                {
                    string full = FullPath(it.Path);
                    string actual = File.Exists(full) ? AgentHash.FileSha1(full) : null;
                    if (actual == it.TargetHash) continue; // already there
                    if (it.Unrestorable) { res.Skipped.Add(it.Path + " (no backup of the earlier version)"); continue; }
                    bool conflict = it.Conflict || (actual != it.ExpectedHash);
                    if (conflict && !overwriteConflicts) { res.Skipped.Add(it.Path); continue; }
                    try
                    {
                        byte[] current = actual != null ? File.ReadAllBytes(full) : null;
                        string currentHash = StoreBlob(current); // keeps the user's version restorable (redo / backup)
                        if (conflict && current != null) res.Backups[it.Path] = BlobPath(currentHash);
                        if (it.TargetHash == null)
                        {
                            if (File.Exists(full)) File.Delete(full);
                        }
                        else
                        {
                            byte[] target = ReadBlob(it.TargetHash);
                            if (target == null) { res.Skipped.Add(it.Path + " (backup missing)"); continue; }
                            string dir = System.IO.Path.GetDirectoryName(full);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                            WriteBytesInPlace(full, target);
                        }
                        res.Restored.Add(it.Path);
                        TurnRestoreItem inv = new TurnRestoreItem();
                        inv.Path = it.Path;
                        inv.ExpectedHash = it.TargetHash;
                        inv.TargetHash = currentHash;
                        inverse.Items.Add(inv);
                    }
                    catch (Exception ex)
                    {
                        res.Errors.Add(it.Path + ": " + ex.Message);
                    }
                }
                res.Completed = res.Skipped.Count == 0 && res.Errors.Count == 0;
                foreach (string id in plan.TurnIds)
                {
                    AgentTurnRecord t = turns.FirstOrDefault(x => x.Id == id);
                    if (t == null) continue;
                    if (plan.IsRedo) t.Undone = false;
                    else if (res.Completed) t.Undone = true;
                }
                if (!plan.IsRedo)
                {
                    redoPlan = inverse.Items.Count > 0 ? inverse : null;
                    if (redoPlan != null) redoPlan.IsRedo = true;
                }
                else
                {
                    redoPlan = null;
                    undoOfRedo = inverse;
                }
                Save();
            }
            RaiseChanged();
            return res;
        }

        /// <summary>Overwrites in place (keeps permissions / exec bit like the editor's save).</summary>
        private static void WriteBytesInPlace(string path, byte[] bytes)
        {
            using (FileStream fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                fs.SetLength(0);
                fs.Write(bytes, 0, bytes.Length);
            }
        }

        private static string Shorten(string s)
        {
            s = (s ?? "").Replace('\n', ' ').Trim();
            return s.Length > 40 ? s.Substring(0, 39) + "…" : s;
        }

        // ------------------------------------------------------------------ persistence

        private string TimelinePath { get { return System.IO.Path.Combine(StoreDir, "timeline.json"); } }

        /// <summary>
        /// Exclusive cross-process lock on the store (two windows on the same folder): a lock file opened with
        /// FileShare.None, retried for up to ~3 s. Null when it can't be taken (the save still proceeds).
        /// </summary>
        private FileStream AcquireStoreLock()
        {
            string lockPath = System.IO.Path.Combine(StoreDir, "timeline.lock");
            for (int i = 0; i < 60; i++)
            {
                try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { Thread.Sleep(50); }
                catch { return null; }
            }
            return null;
        }

        /// <summary>
        /// Adds turns another window saved meanwhile (it keeps its own in-memory list) so this save doesn't drop them and
        /// garbage collection doesn't delete their blobs. Our own copy of a turn wins; turns we removed stay removed.
        /// </summary>
        private void MergeTurnsFromDisk()
        {
            TurnRestorePlan ignored;
            List<AgentTurnRecord> disk = ReadTimeline(out ignored);
            if (disk.Count == 0) return;
            HashSet<string> mine = new HashSet<string>(StringComparer.Ordinal);
            foreach (AgentTurnRecord t in turns) mine.Add(t.Id);
            bool added = false;
            foreach (AgentTurnRecord t in disk)
            {
                if (mine.Contains(t.Id) || removedTurnIds.Contains(t.Id)) continue;
                turns.Add(t);
                added = true;
            }
            if (!added) return;
            turns.Sort(delegate (AgentTurnRecord a, AgentTurnRecord b) { return a.CreatedUtc.CompareTo(b.CreatedUtc); });
            while (turns.Count > MaxTurns) { removedTurnIds.Add(turns[0].Id); turns.RemoveAt(0); }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(StoreDir);
                using (FileStream storeLock = AcquireStoreLock())
                {
                    MergeTurnsFromDisk();
                    WriteTimelineAndCollect();
                }
            }
            catch { }
        }

        private void WriteTimelineAndCollect()
        {
            try
            {
                Dictionary<string, object> root = new Dictionary<string, object>();
                root["workspace"] = WorkspaceRoot;
                List<object> list = new List<object>();
                foreach (AgentTurnRecord t in turns)
                {
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["id"] = t.Id;
                    d["label"] = t.Label;
                    d["createdUtc"] = t.CreatedUtc.ToString("o", CultureInfo.InvariantCulture);
                    d["undone"] = t.Undone;
                    List<object> files = new List<object>();
                    foreach (TurnFileChange f in t.Files)
                    {
                        Dictionary<string, object> fd = new Dictionary<string, object>();
                        fd["p"] = f.Path;
                        fd["pre"] = f.PreHash;
                        fd["post"] = f.PostHash;
                        if (f.PreUnavailable) fd["na"] = true;
                        files.Add(fd);
                    }
                    d["files"] = files;
                    list.Add(d);
                }
                root["turns"] = list;
                if (redoPlan != null)
                {
                    Dictionary<string, object> rd = new Dictionary<string, object>();
                    rd["turns"] = new List<object>(redoPlan.TurnIds.Cast<object>());
                    List<object> items = new List<object>();
                    foreach (TurnRestoreItem it in redoPlan.Items)
                    {
                        Dictionary<string, object> id = new Dictionary<string, object>();
                        id["p"] = it.Path;
                        id["expected"] = it.ExpectedHash;
                        id["target"] = it.TargetHash;
                        items.Add(id);
                    }
                    rd["items"] = items;
                    root["redo"] = rd;
                }
                string tmp = TimelinePath + ".tmp";
                File.WriteAllText(tmp, DapJson.Serialize(root), new UTF8Encoding(false));
                if (File.Exists(TimelinePath)) File.Delete(TimelinePath);
                File.Move(tmp, TimelinePath);
                CollectGarbage();
            }
            catch { }
        }

        private void Load()
        {
            TurnRestorePlan rp;
            List<AgentTurnRecord> loaded = ReadTimeline(out rp);
            turns.AddRange(loaded);
            if (rp != null) redoPlan = rp;
        }

        private List<AgentTurnRecord> ReadTimeline(out TurnRestorePlan redo)
        {
            redo = null;
            List<AgentTurnRecord> turns = new List<AgentTurnRecord>();
            try
            {
                if (!File.Exists(TimelinePath)) return turns;
                string err;
                object root = DapJson.TryParse(File.ReadAllText(TimelinePath), out err);
                List<object> list = DapJson.Arr(root, "turns");
                if (list == null) return turns;
                foreach (object o in list)
                {
                    AgentTurnRecord t = new AgentTurnRecord();
                    t.Id = DapJson.Str(o, "id") ?? NewTurnId();
                    t.Label = DapJson.Str(o, "label") ?? "";
                    DateTime created;
                    if (DateTime.TryParse(DapJson.Str(o, "createdUtc") ?? "", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out created)) t.CreatedUtc = created;
                    t.Undone = DapJson.Bool(o, "undone", false);
                    List<object> files = DapJson.Arr(o, "files");
                    if (files != null)
                        foreach (object fo in files)
                        {
                            TurnFileChange f = new TurnFileChange();
                            f.Path = DapJson.Str(fo, "p") ?? "";
                            f.PreHash = DapJson.Str(fo, "pre");
                            f.PostHash = DapJson.Str(fo, "post");
                            f.PreUnavailable = DapJson.Bool(fo, "na", false);
                            if (f.Path.Length > 0) t.Files.Add(f);
                        }
                    turns.Add(t);
                }
                Dictionary<string, object> rd = DapJson.Obj(root, "redo");
                if (rd != null)
                {
                    TurnRestorePlan p = new TurnRestorePlan();
                    p.IsRedo = true;
                    List<object> ids = DapJson.Arr(rd, "turns");
                    if (ids != null) foreach (object x in ids) if (x is string) p.TurnIds.Add((string)x);
                    List<object> items = DapJson.Arr(rd, "items");
                    if (items != null)
                        foreach (object io in items)
                        {
                            TurnRestoreItem it = new TurnRestoreItem();
                            it.Path = DapJson.Str(io, "p") ?? "";
                            it.ExpectedHash = DapJson.Str(io, "expected");
                            it.TargetHash = DapJson.Str(io, "target");
                            p.Items.Add(it);
                        }
                    if (p.Items.Count > 0) redo = p;
                }
            }
            catch { }
            return turns;
        }

        /// <summary>Deletes blobs no turn / redo record references (snapshot blobs of unchanged files, trimmed turns).</summary>
        private void CollectGarbage()
        {
            if (Thread.VolatileRead(ref gcSuspended) > 0 || !Directory.Exists(BlobDir)) return;
            HashSet<string> live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AgentTurnRecord t in turns)
                foreach (TurnFileChange f in t.Files)
                {
                    if (f.PreHash != null) live.Add(f.PreHash);
                    if (f.PostHash != null) live.Add(f.PostHash);
                }
            if (redoPlan != null) foreach (TurnRestoreItem it in redoPlan.Items) { if (it.TargetHash != null) live.Add(it.TargetHash); if (it.ExpectedHash != null) live.Add(it.ExpectedHash); }
            if (undoOfRedo != null) foreach (TurnRestoreItem it in undoOfRedo.Items) { if (it.TargetHash != null) live.Add(it.TargetHash); }
            try
            {
                foreach (string f in Directory.GetFiles(BlobDir))
                {
                    string name = System.IO.Path.GetFileName(f);
                    if (name.EndsWith(".tmp", StringComparison.Ordinal))
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromHours(1)) { try { File.Delete(f); } catch { } }
                        continue;
                    }
                    // Only old unreferenced blobs: a younger one may belong to a turn another window hasn't saved yet.
                    if (!live.Contains(name) && DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > BlobGcMinAge) { try { File.Delete(f); } catch { } }
                }
            }
            catch { }
        }

        // Blobs written by an in-flight snapshot aren't referenced yet: no garbage collection while one is open.
        private int gcSuspended;

        public void SuspendGc()
        {
            Interlocked.Increment(ref gcSuspended);
        }

        public void ResumeGc()
        {
            if (Interlocked.Decrement(ref gcSuspended) < 0) Interlocked.Exchange(ref gcSuspended, 0);
        }

        /// <summary>Removes unreferenced blobs now (after an abandoned snapshot).</summary>
        public void Compact()
        {
            lock (gate) CollectGarbage();
        }
    }

    /// <summary>State of the workspace before an agent turn ran terminal commands.</summary>
    public sealed class WorkspaceSnapshot
    {
        public sealed class Entry
        {
            public string Hash;
            public long Size;
            public long MtimeTicks;
            /// <summary>Content stored as a blob (restorable).</summary>
            public bool HasBlob;
        }

        public string Root = "";
        public bool IsGit;
        public string RepoRoot;
        /// <summary>Workspace path relative to the repo root ('/' separators, "" when the workspace is the repo root).</summary>
        public string RepoSubPath = "";
        public string Head;
        /// <summary>git: only dirty paths; otherwise every file within the limits. Keys are workspace-relative '/' paths.</summary>
        public readonly Dictionary<string, Entry> Files = new Dictionary<string, Entry>(StringComparer.Ordinal);
        public bool Truncated;
    }

    /// <summary>
    /// Detects which files an agent turn changed outside the review flow (its run_terminal commands): git status /
    /// HEAD blobs in a repository, size+mtime+content snapshot otherwise. Ignored files (node_modules, build output)
    /// are not tracked.
    /// </summary>
    public static class WorkspaceChangeTracker
    {
        public static int MaxFiles = 20000;
        public static long MaxBlobBytesTotal = 32L * 1024 * 1024;
        public static long MaxBlobBytesPerFile = 1024 * 1024;
        public static long MaxGitDirtyBlobBytes = 16L * 1024 * 1024;
        public static long MaxGitDirtyBlobBytesTotal = 128L * 1024 * 1024;

        public static WorkspaceSnapshot Take(string workspaceRoot, AgentTurnCheckpointStore store)
        {
            WorkspaceSnapshot s = new WorkspaceSnapshot();
            s.Root = store != null ? store.WorkspaceRoot : workspaceRoot;
            string repo = GitCli.IsAvailable() ? GitCli.FindRepoRoot(s.Root) : null;
            if (!string.IsNullOrEmpty(repo))
            {
                GitCommandResult head = GitProcess.Run(repo, new string[] { "rev-parse", "--verify", "-q", "HEAD" }, 15000, CancellationToken.None, null, null);
                if (head.Success)
                {
                    s.IsGit = true;
                    s.RepoRoot = System.IO.Path.GetFullPath(repo).TrimEnd('\\', '/');
                    s.RepoSubPath = VerificationOverlay.RelativeTo(s.RepoRoot, s.Root).Replace('\\', '/');
                    s.Head = head.Stdout.Trim();
                    long gitBudget = MaxGitDirtyBlobBytesTotal;
                    int entries = 0;
                    foreach (string rel in GitDirtyPaths(s))
                    {
                        if (++entries > MaxFiles) { s.Truncated = true; break; }
                        string full = System.IO.Path.Combine(s.Root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                        WorkspaceSnapshot.Entry e = new WorkspaceSnapshot.Entry();
                        if (File.Exists(full))
                        {
                            FileInfo fi = new FileInfo(full);
                            e.Size = fi.Length;
                            e.MtimeTicks = fi.LastWriteTimeUtc.Ticks;
                            if (fi.Length <= MaxGitDirtyBlobBytes && fi.Length <= gitBudget && store != null)
                            {
                                try { e.Hash = store.StoreBlob(File.ReadAllBytes(full)); e.HasBlob = true; gitBudget -= fi.Length; } catch { }
                            }
                            if (e.Hash == null) e.Hash = AgentHash.FileSha1(full);
                        }
                        s.Files[rel] = e; // Hash null + Size 0 = absent (deleted in the work tree)
                    }
                    return s;
                }
            }
            long budget = MaxBlobBytesTotal;
            foreach (string full in EnumerateFiles(s.Root, s))
            {
                FileInfo fi;
                try { fi = new FileInfo(full); } catch { continue; }
                WorkspaceSnapshot.Entry e = new WorkspaceSnapshot.Entry();
                e.Size = fi.Length;
                e.MtimeTicks = fi.LastWriteTimeUtc.Ticks;
                if (store != null && fi.Length <= MaxBlobBytesPerFile && budget - fi.Length >= 0)
                {
                    try { e.Hash = store.StoreBlob(File.ReadAllBytes(full)); e.HasBlob = true; budget -= fi.Length; } catch { }
                }
                s.Files[VerificationOverlay.RelativeTo(s.Root, full).Replace('\\', '/')] = e;
            }
            return s;
        }

        /// <summary>Changes between <paramref name="pre"/> and the current disk state; pre-images are stored as blobs.</summary>
        public static List<TurnFileChange> Diff(WorkspaceSnapshot pre, AgentTurnCheckpointStore store)
        {
            List<TurnFileChange> res = new List<TurnFileChange>();
            if (pre == null || store == null) return res;
            if (pre.IsGit) DiffGit(pre, store, res);
            else DiffWalk(pre, store, res);
            return res;
        }

        private static void DiffGit(WorkspaceSnapshot pre, AgentTurnCheckpointStore store, List<TurnFileChange> res)
        {
            HashSet<string> paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (string p in pre.Files.Keys) paths.Add(p);
            foreach (string p in GitDirtyPaths(pre)) paths.Add(p);
            GitCommandResult head = GitProcess.Run(pre.RepoRoot, new string[] { "rev-parse", "--verify", "-q", "HEAD" }, 15000, CancellationToken.None, null, null);
            string postHead = head.Success ? head.Stdout.Trim() : pre.Head;
            if (postHead != pre.Head)
            {
                // The turn committed / checked out: every path that differs between the two commits may have changed.
                GitCommandResult d = GitProcess.Run(pre.RepoRoot, new string[] { "diff", "--name-only", "-z", pre.Head, postHead }, 30000, CancellationToken.None, null, null);
                foreach (string rp in (d.Stdout ?? "").Split('\0'))
                {
                    string w = RepoToWorkspace(pre, rp);
                    if (w != null) paths.Add(w);
                }
            }
            foreach (string rel in paths)
            {
                string full = System.IO.Path.Combine(pre.Root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                string postHash = File.Exists(full) ? AgentHash.FileSha1(full) : null;
                string preHash;
                bool preUnavailable = false;
                WorkspaceSnapshot.Entry e;
                if (pre.Files.TryGetValue(rel, out e))
                {
                    preHash = e.Hash;
                    if (e.Hash != null && !e.HasBlob) preUnavailable = true;
                }
                else if (pre.Truncated)
                {
                    // Beyond the snapshot limit: it may have had uncommitted changes, so HEAD is not a safe "before".
                    preHash = null;
                    preUnavailable = true;
                }
                else
                {
                    byte[] headBytes = GitBlob(pre.RepoRoot, pre.Head, WorkspaceToRepo(pre, rel));
                    preHash = headBytes != null ? store.StoreBlob(headBytes) : null;
                }
                if (preHash == postHash) continue;
                TurnFileChange c = new TurnFileChange();
                c.Path = rel;
                c.PreHash = preHash;
                c.PostHash = postHash;
                c.PreUnavailable = preUnavailable;
                if (postHash != null)
                {
                    try { store.StoreBlob(File.ReadAllBytes(full)); } catch { }
                }
                res.Add(c);
            }
        }

        private static void DiffWalk(WorkspaceSnapshot pre, AgentTurnCheckpointStore store, List<TurnFileChange> res)
        {
            WorkspaceSnapshot post = new WorkspaceSnapshot();
            post.Root = pre.Root;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string full in EnumerateFiles(pre.Root, post))
            {
                string rel = VerificationOverlay.RelativeTo(pre.Root, full).Replace('\\', '/');
                seen.Add(rel);
                FileInfo fi;
                try { fi = new FileInfo(full); } catch { continue; }
                WorkspaceSnapshot.Entry e;
                bool existed = pre.Files.TryGetValue(rel, out e);
                if (existed && e.Size == fi.Length && e.MtimeTicks == fi.LastWriteTimeUtc.Ticks) continue; // untouched
                string postHash = AgentHash.FileSha1(full);
                if (existed && e.Hash != null && e.Hash == postHash) continue; // rewritten with identical bytes
                if (!existed && pre.Truncated) continue; // outside the pre snapshot window: unknown
                TurnFileChange c = new TurnFileChange();
                c.Path = rel;
                c.PreHash = existed ? e.Hash : null;
                c.PreUnavailable = existed && !e.HasBlob;
                c.PostHash = postHash;
                try { store.StoreBlob(File.ReadAllBytes(full)); } catch { }
                res.Add(c);
            }
            if (post.Truncated) return; // can't tell deletions apart from files beyond the limit
            foreach (KeyValuePair<string, WorkspaceSnapshot.Entry> kv in pre.Files)
            {
                if (seen.Contains(kv.Key)) continue;
                TurnFileChange c = new TurnFileChange();
                c.Path = kv.Key;
                c.PreHash = kv.Value.Hash;
                c.PreUnavailable = !kv.Value.HasBlob;
                c.PostHash = null;
                res.Add(c);
            }
        }

        private static IEnumerable<string> EnumerateFiles(string root, WorkspaceSnapshot s)
        {
            HashSet<string> skip = new HashSet<string>(VerificationOverlay.SkippedCopyFolders, StringComparer.OrdinalIgnoreCase);
            foreach (string d in VerificationOverlay.LinkedDependencyFolders) skip.Add(d);
            Stack<string> stack = new Stack<string>();
            stack.Push(root);
            int count = 0;
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] files, dirs;
                try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); } catch { continue; }
                Array.Sort(files, StringComparer.Ordinal);
                foreach (string f in files)
                {
                    if (++count > MaxFiles) { s.Truncated = true; yield break; }
                    yield return f;
                }
                foreach (string d in dirs)
                {
                    if (skip.Contains(System.IO.Path.GetFileName(d)) || VerificationOverlay.IsLink(d)) continue;
                    stack.Push(d);
                }
            }
        }

        private static List<string> GitDirtyPaths(WorkspaceSnapshot s)
        {
            List<string> res = new List<string>();
            string[] args = string.IsNullOrEmpty(s.RepoSubPath)
                ? new string[] { "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=all" }
                : new string[] { "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=all", "--", s.RepoSubPath };
            GitCommandResult st = GitProcess.Run(s.RepoRoot, args, 60000, CancellationToken.None, null, null);
            if (!st.Success) return res;
            foreach (VerificationOverlay.GitStatusEntry e in VerificationOverlay.ParsePorcelainZ(st.Stdout))
            {
                string w = RepoToWorkspace(s, e.Path);
                if (w != null && !w.EndsWith("/", StringComparison.Ordinal)) res.Add(w);
                if (!string.IsNullOrEmpty(e.OrigPath))
                {
                    string o = RepoToWorkspace(s, e.OrigPath);
                    if (o != null) res.Add(o);
                }
            }
            return res;
        }

        private static string RepoToWorkspace(WorkspaceSnapshot s, string repoRel)
        {
            if (string.IsNullOrEmpty(repoRel)) return null;
            if (string.IsNullOrEmpty(s.RepoSubPath)) return repoRel;
            string prefix = s.RepoSubPath.TrimEnd('/') + "/";
            return repoRel.StartsWith(prefix, StringComparison.Ordinal) ? repoRel.Substring(prefix.Length) : null;
        }

        private static string WorkspaceToRepo(WorkspaceSnapshot s, string rel)
        {
            return string.IsNullOrEmpty(s.RepoSubPath) ? rel : s.RepoSubPath.TrimEnd('/') + "/" + rel;
        }

        /// <summary>Raw bytes of <paramref name="path"/> at <paramref name="rev"/> (null when absent).</summary>
        public static byte[] GitBlob(string repo, string rev, string path)
        {
            if (string.IsNullOrEmpty(rev)) return null;
            if (GitSafety.BlockReason(repo, "cat-file") != null) return null; // Restricted Mode
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("git", GitCli.JoinArgs(new string[] { "--no-optional-locks", "cat-file", "blob", rev + ":" + path }));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.WorkingDirectory = repo;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return null;
                    MemoryStream ms = new MemoryStream();
                    Thread errThread = new Thread(delegate () { try { p.StandardError.ReadToEnd(); } catch { } });
                    errThread.IsBackground = true;
                    errThread.Start();
                    p.StandardOutput.BaseStream.CopyTo(ms);
                    if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return null; }
                    errThread.Join(2000);
                    return p.ExitCode == 0 ? ms.ToArray() : null;
                }
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Tracks one agent turn: snapshot before its first terminal command, change detection at the end, recording into
    /// the checkpoint store. Accepted edits of the turn are recorded later via <see cref="AgentChangeSetApplier"/>.
    /// </summary>
    public sealed class AgentTurnTracker
    {
        private readonly AgentTurnCheckpointStore store;
        private readonly object gate = new object();
        private WorkspaceSnapshot snapshot;
        public readonly string TurnId;
        public readonly string Label;

        public AgentTurnTracker(AgentTurnCheckpointStore store, string label)
        {
            this.store = store;
            TurnId = AgentTurnCheckpointStore.NewTurnId();
            Label = label ?? "";
        }

        public bool HasSnapshot { get { lock (gate) return snapshot != null; } }

        /// <summary>Takes the "before" snapshot once (call right before the turn's first terminal command).</summary>
        public void EnsureSnapshot()
        {
            if (store == null || string.IsNullOrEmpty(store.WorkspaceRoot)) return;
            lock (gate)
            {
                if (snapshot != null) return;
                store.SuspendGc();
                try { snapshot = WorkspaceChangeTracker.Take(store.WorkspaceRoot, store); }
                catch { store.ResumeGc(); throw; }
            }
        }

        /// <summary>Detects and records what the turn's commands changed; returns the changes (empty when none).</summary>
        public List<TurnFileChange> Complete()
        {
            WorkspaceSnapshot s;
            lock (gate) { s = snapshot; snapshot = null; }
            if (s == null || store == null) return new List<TurnFileChange>();
            List<TurnFileChange> changes;
            try
            {
                changes = WorkspaceChangeTracker.Diff(s, store);
                if (changes.Count > 0) store.RecordChanges(TurnId, Label, changes);
            }
            finally
            {
                store.ResumeGc();
            }
            store.Compact();
            return changes;
        }
    }
}
