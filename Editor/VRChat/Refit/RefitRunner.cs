using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>Meshes of one avatar to refit together.</summary>
    public sealed class RefitBatch
    {
        public Transform Avatar;
        public SkinnedMeshRenderer Body;
        public List<SkinnedMeshRenderer> Renderers = new List<SkinnedMeshRenderer>();
        /// <summary>Body blendshapes each mesh gets. Shapes a mesh already has (its creator's) are kept, never replaced.</summary>
        public List<string> Shapes = new List<string>();
        /// <summary>Per-mesh blendshapes when they differ (those near each mesh); replaces <see cref="Shapes"/> for that mesh.</summary>
        public Dictionary<SkinnedMeshRenderer, List<string>> ShapesByRenderer = new Dictionary<SkinnedMeshRenderer, List<string>>();
        public RefitMode Mode;
        public bool CoverDifferentBaseBody;
        /// <summary>Optional per-renderer coverage policy for packs containing both garments and props.</summary>
        public Dictionary<SkinnedMeshRenderer, bool> CoverageByRenderer = new Dictionary<SkinnedMeshRenderer, bool>();
        /// <summary>The original base, for <see cref="RefitMode.Fit"/>.</summary>
        public CustomBaseOriginal Original;
        public string BaseKey, BaseName, Tool;
        public float Tightness = RefitPreferences.Tightness;
        /// <summary>Reuse an identical earlier result instead of computing it again.</summary>
        public bool UseCache = true;
    }

    public sealed class RefitItemResult
    {
        public SkinnedMeshRenderer Renderer;
        public RefitJob Job;
        public RefitOutcome Outcome;
        public OrbitersRefit Record;
        /// <summary>The result came from the cache.</summary>
        public bool Reused;
        /// <summary>Nothing to do: the mesh already had every shape.</summary>
        public bool Skipped;
    }

    public sealed class RefitBatchResult
    {
        public readonly List<RefitItemResult> Items = new List<RefitItemResult>();
        /// <summary>Generated or changed project files (meshes, cache entries), e.g. for a Unit Git checkpoint.</summary>
        public readonly List<string> ChangedAssets = new List<string>();
        public bool Cancelled;

        public int Refitted => Items.Count(i => !i.Skipped && i.Outcome != null && i.Outcome.Success);
        public List<RefitItemResult> Failed => Items.Where(i => i.Outcome != null && !i.Outcome.Success && !i.Outcome.Cancelled).ToList();
        public bool Rough => Items.Any(i => i.Outcome != null && i.Outcome.Rough);
        /// <summary>Blendshapes that now follow the body on the refitted meshes.</summary>
        public int Shapes => Items.Where(i => i.Record != null).Sum(i => i.Record.shapes.Count);
    }

    /// <summary>
    /// Refits meshes with the installed engine and records them (<see cref="RefitRecords"/>). Refitting a refitted mesh starts
    /// again from its original instead of stacking; adding shapes to a refitted mesh keeps its fit. The same inputs reuse the
    /// cached result. Heavy work runs on the engine's worker thread; the editor stays responsive.
    /// </summary>
    public static class RefitRunner
    {
        // Refits at work per avatar root: a refit spans many frames, and changing the custom base meanwhile mixes two bases.
        private static readonly Dictionary<int, int> Working = new Dictionary<int, int>();

        /// <summary>
        /// True while a refit (any tool's) works on the avatar, or on any avatar when <paramref name="avatarRoot"/> is null.
        /// Tools that change the custom base (MCB's version switch) wait until it is done.
        /// </summary>
        public static bool IsRunning(Transform avatarRoot = null) =>
            avatarRoot == null ? Working.Count > 0 : Working.ContainsKey(avatarRoot.GetInstanceID());

        /// <summary>
        /// Marks the avatar as being refitted until disposed: <see cref="Run"/> holds it, and a tool holds it around the whole
        /// answer (checks, then the run) so the custom base cannot change in between.
        /// </summary>
        public static IDisposable Hold(Transform avatarRoot)
        {
            if (avatarRoot == null) return new Release(0);
            int id = avatarRoot.GetInstanceID();
            Working[id] = Working.TryGetValue(id, out int count) ? count + 1 : 1;
            return new Release(id);
        }

        private sealed class Release : IDisposable
        {
            private int id;
            public Release(int id) => this.id = id;

            public void Dispose()
            {
                if (id == 0) return;
                if (Working.TryGetValue(id, out int count) && count > 1) Working[id] = count - 1;
                else Working.Remove(id);
                id = 0;
            }
        }

        public static Task<RefitBatchResult> RunAsync(RefitBatch batch, Action<float, string> progress, CancellationToken cancellation)
        {
            var completion = new TaskCompletionSource<RefitBatchResult>();
            RefitBatchResult result = null;
            Drive(Run(batch, progress, r => result = r, cancellation), error =>
            {
                if (error != null) completion.TrySetException(error);
                else completion.TrySetResult(result);
            });
            return completion.Task;
        }

        /// <summary>The editor coroutine; <paramref name="done"/> is called once, also when cancelled. The avatar counts as running meanwhile.</summary>
        public static IEnumerator Run(RefitBatch batch, Action<float, string> progress, Action<RefitBatchResult> done, CancellationToken cancellation)
        {
            using (Hold(batch.Avatar))
            {
                var steps = Steps(batch, progress, done, cancellation);
                while (steps.MoveNext()) yield return steps.Current;
            }
        }

        private static IEnumerator Steps(RefitBatch batch, Action<float, string> progress, Action<RefitBatchResult> done, CancellationToken cancellation)
        {
            var result = new RefitBatchResult();
            var engine = RefitEngine.Current;
            string engineName = engine?.Name ?? "none";
            var root = batch.Avatar;
            Func<SkinnedMeshRenderer, bool> coverageFor = r => batch.Mode == RefitMode.Fit &&
                (batch.CoverageByRenderer.TryGetValue(r, out var enabled) ? enabled : batch.CoverDifferentBaseBody);
            var renderers = batch.Renderers.Where(r => r != null).Distinct()
                .OrderBy(r => coverageFor(r) ? ClothingCoverage.Layer(r) : 0).ToList();
            for (int i = 0; i < renderers.Count; i++)
            {
                if (cancellation.IsCancellationRequested) { result.Cancelled = true; break; }
                var renderer = renderers[i];
                float from = (float)i / renderers.Count, span = 1f / renderers.Count;
                var item = new RefitItemResult { Renderer = renderer };
                result.Items.Add(item);
                if (renderer == null || renderer.sharedMesh == null) { item.Skipped = true; continue; }
                progress?.Invoke(from, renderer.name + "…");

                var existing = RefitRecords.Find(renderer);
                bool applied = existing != null && existing.Applied;
                var requested = batch.ShapesByRenderer.TryGetValue(renderer, out var own) ? own : batch.Shapes;
                var mode = batch.Mode;
                var kind = mode == RefitMode.Fit ? OrbitersRefit.FitKind.Fitted : OrbitersRefit.FitKind.Shapes;
                var kept = new List<RefitShape>();
                RefitRendererState original, fittedBefore = null;
                string fittedMetadata = null;
                if (applied && mode == RefitMode.Shapes)
                {
                    // More shapes on a refitted mesh: its fit, original and earlier shapes stay.
                    original = existing.original;
                    kept.AddRange(existing.shapes);
                    kind = existing.kind;
                }
                else
                {
                    if (applied)
                    {
                        // Refit again from the clean original, with the shapes it had too.
                        fittedBefore = RefitRecords.Capture(root, renderer);
                        fittedMetadata = engine?.SaveMetadata(renderer);
                        RefitRecords.Restore(root, renderer, existing.original, "Refit again");
                        requested = existing.shapes.Select(s => s.source).Concat(requested).ToList();
                        original = existing.original;
                    }
                    else original = RefitRecords.Capture(root, renderer);
                }

                var job = new RefitJob
                {
                    Renderer = renderer, Avatar = root.gameObject, Body = batch.Body, Mode = mode, Tightness = batch.Tightness,
                    CoverDifferentBaseBody = batch.CoverageByRenderer.TryGetValue(renderer, out var cover) ? cover : batch.CoverDifferentBaseBody,
                    SourceAvatar = batch.Original?.Avatar, SourceBody = batch.Original?.Body,
                    Shapes = Missing(renderer.sharedMesh, requested),
                };
                item.Job = job;
                if (job.CoverDifferentBaseBody)
                    job.CoverageLayers = result.Items.Where(previous => previous != item && coverageFor(previous.Renderer) &&
                        previous.Outcome != null && previous.Outcome.Success && ClothingCoverage.Layer(previous.Renderer) < ClothingCoverage.Layer(renderer))
                        .Select(previous => previous.Renderer).ToList();
                if (mode == RefitMode.Shapes && job.Shapes.Count == 0)
                {
                    item.Skipped = true;
                    item.Outcome = new RefitOutcome { Success = true };
                    continue;
                }

                RefitOutcome outcome = null;
                string key = batch.UseCache ? RefitCache.Key(job, root, engineName) : null;
                var cached = RefitCache.Find(key);
                if (cached != null)
                {
                    outcome = RefitCache.Apply(cached, renderer);
                    item.Reused = true;
                }
                else if (mode == RefitMode.Fit && job.SourceBody == null) outcome = Failed("The original base body is not available to refit from.");
                else if (engine == null) outcome = Failed("ReFit is not installed.");
                else
                {
                    IEnumerator run = null;
                    try
                    {
                        run = engine.Run(job, (t, label) => progress?.Invoke(from + span * 0.95f * Mathf.Clamp01(t), renderer.name + ": " + label),
                            o => outcome = o, cancellation);
                    }
                    catch (Exception ex) { outcome = Failed(ex.Message); }
                    while (run != null)
                    {
                        bool moved;
                        try { moved = run.MoveNext(); }
                        catch (Exception ex)
                        {
                            outcome = Failed(ex.Message);
                            Debug.LogException(ex);
                            moved = false;
                        }
                        if (!moved) break;
                        yield return run.Current;
                    }
                    (run as IDisposable)?.Dispose();
                    outcome ??= Failed("The refit did not complete.");
                }
                item.Outcome = outcome;

                if (outcome.Success && outcome.Mesh != null && renderer != null)
                {
                    if (!item.Reused)
                    {
                        string entry = RefitCache.Store(key, outcome, renderer, engineName);
                        if (entry != null) result.ChangedAssets.Add(entry);
                    }
                    item.Record = RefitRecords.Register(renderer, original, outcome.Mesh, outcome.MeshPath, batch.Body,
                        kept.Concat(RefitCache.Pairs(outcome)), kind, batch.BaseKey, batch.BaseName, batch.Tool);
                    if (!string.IsNullOrEmpty(outcome.MeshPath)) result.ChangedAssets.Add(outcome.MeshPath);
                }
                else if (fittedBefore != null && renderer != null)
                {
                    // A failed refit keeps the previous one.
                    RefitRecords.Restore(root, renderer, fittedBefore, "Keep previous refit");
                    if (fittedMetadata != null) engine?.LoadMetadata(renderer, fittedMetadata);
                }
                if (outcome.Cancelled) { result.Cancelled = true; break; }
            }
            progress?.Invoke(1f, result.Cancelled ? "Cancelled" : "Done");
            done?.Invoke(result);
        }

        /// <summary>The requested body shapes the mesh does not have yet (same or normalized name): its own shapes stay.</summary>
        public static List<string> Missing(Mesh mesh, IEnumerable<string> shapes)
        {
            var have = new HashSet<string>(StringComparer.Ordinal);
            if (mesh != null)
                for (int i = 0; i < mesh.blendShapeCount; i++) have.Add(BlendShapeSync.NormalizeShapeName(mesh.GetBlendShapeName(i)));
            var missing = new List<string>();
            foreach (string shape in shapes ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(shape) || missing.Contains(shape)) continue;
                if (!have.Contains(BlendShapeSync.NormalizeShapeName(shape))) missing.Add(shape);
            }
            return missing;
        }

        /// <summary>Runs an editor coroutine from EditorApplication.update, one step per update; yielded enumerators run nested.</summary>
        public static void Drive(IEnumerator routine, Action<Exception> finished)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(routine);
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                try
                {
                    var top = stack.Peek();
                    if (top.MoveNext())
                    {
                        if (top.Current is IEnumerator nested) stack.Push(nested);
                        return;
                    }
                    (top as IDisposable)?.Dispose();
                    stack.Pop();
                    if (stack.Count > 0) return;
                    EditorApplication.update -= tick;
                    finished?.Invoke(null);
                }
                catch (Exception ex)
                {
                    EditorApplication.update -= tick;
                    while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
                    finished?.Invoke(ex);
                }
            };
            EditorApplication.update += tick;
        }

        private static RefitOutcome Failed(string error) => new RefitOutcome
        {
            Messages = { new RefitMessage { Severity = RefitSeverity.Error, Code = "refit-failed", Text = error } }
        };
    }
}
