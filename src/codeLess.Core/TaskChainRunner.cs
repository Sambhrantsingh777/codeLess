using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    /// <summary>
    /// Background-task tracking (VS Code problemMatcher.background): a watcher task ("tsc --watch", "vite") never exits,
    /// so it counts as ready for its dependents / preLaunchTask when its endsPattern matches an output line. C# 5.
    /// </summary>
    public static class BackgroundTaskPatterns
    {
        /// <summary>Ends pattern of the task's background matcher (inline "background.endsPattern" or a known watch matcher), or null.</summary>
        public static Regex EndsPattern(TaskDefinition task)
        {
            if (task == null || task.ProblemMatchers == null) return null;
            foreach (object m in task.ProblemMatchers)
            {
                Dictionary<string, object> d = m as Dictionary<string, object>;
                if (d != null)
                {
                    Dictionary<string, object> bg = DapJson.Obj(d, "background");
                    if (bg == null) continue;
                    object ends;
                    if (!bg.TryGetValue("endsPattern", out ends) || ends == null) continue;
                    string rx = ends as string;
                    if (rx == null) rx = DapJson.Str(ends as Dictionary<string, object>, "regexp");
                    Regex r = Compile(rx);
                    if (r != null) return r;
                    continue;
                }
                string name = m as string;
                if (name == null) continue;
                switch (name.TrimStart('$'))
                {
                    case "tsc-watch":
                        return Compile(@"(?:Compilation complete\.|Found \d+ errors?\.) Watching for file changes\.");
                    case "esbuild-watch":
                        return Compile(@"\[watch\] build finished");
                    case "vite":
                        return Compile(@"(?:ready in|Local:)\s");
                }
            }
            return null;
        }

        private static Regex Compile(string rx)
        {
            if (string.IsNullOrEmpty(rx)) return null;
            try { return new Regex(rx, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500)); }
            catch { return null; }
        }
    }

    /// <summary>
    /// Runs a task with its dependsOn graph, shared by both UIs (and by debugging's preLaunchTask): dependencies run in
    /// "sequence" (one after another, stopping at the first failure) or "parallel" (VS Code's default; all start, the task
    /// waits for all), recursively; a composite task (dependsOn without a command) completes when its dependencies do;
    /// a background dependency counts as done when its background endsPattern matches. Completion is observed with
    /// <see cref="TaskExecution.WhenFinished"/>, which can't miss an exit that happened before the subscription.
    /// Everything except <see cref="StartLeaf"/> may be called on any thread; callbacks are marshalled with
    /// <see cref="PostToUi"/>. Strict C# 5.
    /// </summary>
    public sealed class TaskChainRunner
    {
        /// <summary>All tasks (for resolving dependsOn labels). Called on the UI thread.</summary>
        public Func<List<TaskDefinition>> LoadTasks;
        /// <summary>Starts one task's own command (no dependencies) and returns its execution (null = couldn't start). UI thread.</summary>
        public Func<TaskDefinition, TaskExecution> StartLeaf;
        /// <summary>Marshals to the UI thread (defaults to direct invocation).</summary>
        public Action<Action> PostToUi;

        private void Post(Action a)
        {
            if (PostToUi != null) PostToUi(a);
            else a();
        }

        /// <summary>
        /// Runs <paramref name="task"/> after its dependencies. <paramref name="completed"/> gets (success, message):
        /// message explains a failure ("dependency 'build' failed with exit code 2", "dependency cycle ...").
        /// </summary>
        public void Run(TaskDefinition task, Action<bool, string> completed)
        {
            if (task == null) { if (completed != null) completed(false, "No task."); return; }
            List<TaskDefinition> all = null;
            try { all = LoadTasks != null ? LoadTasks() : null; } catch { }
            RunNode(task, all ?? new List<TaskDefinition>(), new List<string>(), delegate (bool ok, string msg)
            {
                if (completed != null) completed(ok, msg);
            });
        }

        private void RunNode(TaskDefinition t, List<TaskDefinition> all, List<string> stack, Action<bool, string> done)
        {
            if (stack.Contains(t.DisplayLabel))
            {
                done(false, "Task dependency cycle: " + string.Join(" -> ", stack.ToArray()) + " -> " + t.DisplayLabel + ".");
                return;
            }
            List<string> path = new List<string>(stack);
            path.Add(t.DisplayLabel);
            List<TaskDefinition> deps = new List<TaskDefinition>();
            foreach (string label in t.DependsOn)
            {
                TaskDefinition d = TaskConfiguration.FindByLabel(all, label);
                if (d == null)
                {
                    done(false, "Task '" + label + "' (a dependency of '" + t.Label + "') was not found.");
                    return;
                }
                deps.Add(d);
            }
            Action<bool, string> afterDeps = delegate (bool ok, string msg)
            {
                if (!ok) { done(false, msg); return; }
                if (string.IsNullOrEmpty(t.Command)) { done(true, null); return; } // composite task
                StartAndWait(t, done);
            };
            if (deps.Count == 0) { afterDeps(true, null); return; }
            if (t.DependsOrderSequence) RunSequence(deps, 0, all, path, afterDeps);
            else RunParallel(deps, all, path, afterDeps);
        }

        private void RunSequence(List<TaskDefinition> deps, int index, List<TaskDefinition> all, List<string> path, Action<bool, string> done)
        {
            if (index >= deps.Count) { done(true, null); return; }
            RunNode(deps[index], all, path, delegate (bool ok, string msg)
            {
                if (!ok) { done(false, msg); return; }
                RunSequence(deps, index + 1, all, path, done);
            });
        }

        private void RunParallel(List<TaskDefinition> deps, List<TaskDefinition> all, List<string> path, Action<bool, string> done)
        {
            object gate = new object();
            int remaining = deps.Count;
            bool failed = false;
            string firstError = null;
            foreach (TaskDefinition d in deps)
            {
                RunNode(d, all, path, delegate (bool ok, string msg)
                {
                    bool last;
                    lock (gate)
                    {
                        if (!ok && !failed) { failed = true; firstError = msg; }
                        remaining--;
                        last = remaining == 0;
                    }
                    if (last) done(!failed, firstError);
                });
            }
        }

        private void StartAndWait(TaskDefinition t, Action<bool, string> done)
        {
            TaskExecution ex = null;
            try { ex = StartLeaf != null ? StartLeaf(t) : null; } catch (Exception e) { done(false, "Task '" + t.Label + "' could not be started: " + e.Message); return; }
            if (ex == null) { done(false, "Task '" + t.Label + "' could not be started."); return; }
            ex.WhenFinished(delegate (TaskExecution x, bool ok)
            {
                // Always continue on the UI thread (also when the task finished synchronously inside StartLeaf).
                Post(delegate
                {
                    if (ok) done(true, null);
                    else if (x.WasTerminated) done(false, "Task '" + t.Label + "' was terminated.");
                    else done(false, "Task '" + t.Label + "' failed with exit code " + x.ExitCode + ".");
                });
            });
        }
    }
}
