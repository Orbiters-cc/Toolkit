using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor.Storage;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Animations
{
    /// <summary>Loop Time of the extracted clips: as in the source, or forced on or off.</summary>
    public enum LoopOverride { Keep, On, Off }

    /// <summary>What happens when an .anim with the output name already exists.</summary>
    public enum ExistingAnimation
    {
        /// <summary>The existing clip is updated in place: its GUID, and everything using it, stay.</summary>
        Replace,
        /// <summary>The new clip gets a free numbered name ("Run 1").</summary>
        KeepBoth,
        /// <summary>The existing clip is left as it is.</summary>
        Skip,
    }

    public enum ExtractionStatus { Done, Skipped, Failed }

    /// <summary>One clip of an asset, as the extractor lists it.</summary>
    public sealed class AnimationClipInfo
    {
        public string Name;
        /// <summary>Which of the clips sharing <see cref="Name"/> this is (0 for the first).</summary>
        public int Occurrence;
        public float Length, FrameRate;
        public int Frames;
        public bool Loop, Humanoid, Legacy;
        public int Events;
    }

    /// <summary>An asset holding animation clips: a model's takes, or clips stored in another asset.</summary>
    public sealed class AnimationSourceInfo
    {
        public string Path, Name;
        /// <summary>The model's rig, or null when the asset is not a model.</summary>
        public ModelImporterAnimationType? Rig;
        /// <summary>The asset is itself an animation clip (.anim): there is nothing to extract from it.</summary>
        public bool IsClipFile;
        public List<AnimationClipInfo> Clips = new List<AnimationClipInfo>();
    }

    /// <summary>One clip to save: which clip of which asset, and the .anim it becomes.</summary>
    public sealed class ExtractionJob
    {
        public string Source;
        public string Clip;
        public int Occurrence;
        /// <summary>File name without extension, made valid with <see cref="AssetPaths.FileName"/>.</summary>
        public string Name;
        public string Folder;
    }

    public sealed class ExtractionOptions
    {
        public LoopOverride Loop = LoopOverride.Keep;
        public ExistingAnimation Existing = ExistingAnimation.Replace;
    }

    public sealed class ExtractionResult
    {
        public ExtractionJob Job;
        public ExtractionStatus Status;
        /// <summary>The .anim written, or the one left alone.</summary>
        public string Path;
        /// <summary>An existing .anim was updated in place.</summary>
        public bool Replaced;
        public string Message;
    }

    /// <summary>
    /// Saves the animations inside models (or any asset holding clips) as standalone .anim clips: curves (humanoid muscles
    /// included), events and clip settings, optionally with Loop Time forced. Files from outside the project are copied in first.
    /// </summary>
    public static class AnimationExtraction
    {
        // The model importer's hidden clips for its preview.
        private const string PreviewPrefix = "__preview__";

        /// <summary>Model files Unity imports (some through their authoring app) or common importers add (glTF).</summary>
        public static readonly IReadOnlyCollection<string> ModelExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".fbx", ".dae", ".obj", ".3ds", ".dxf", ".blend", ".max", ".ma", ".mb", ".c4d", ".lxo", ".jas", ".gltf", ".glb" };

        public static bool IsModelFile(string path) => ModelExtensions.Contains(Path.GetExtension(path ?? "") ?? "");

        /// <summary>The clips <paramref name="assetPath"/> carries, without Unity's preview clips; a model's in its import order.</summary>
        public static List<AnimationClip> Clips(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return new List<AnimationClip>();
            List<string> order = null;
            if (AssetImporter.GetAtPath(assetPath) is ModelImporter importer)
            {
                order = (importer.clipAnimations.Length > 0 ? importer.clipAnimations : importer.defaultClipAnimations).Select(c => c.name).ToList();
                // A model without takes is not loaded at all: folder drops go through every mesh in them.
                if (!importer.importAnimation || order.Count == 0) return new List<AnimationClip>();
            }
            var clips = AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<AnimationClip>()
                .Where(c => c != null && !c.name.StartsWith(PreviewPrefix, StringComparison.Ordinal)).ToList();
            if (order == null) return clips;
            return clips.OrderBy(c => { int index = order.IndexOf(c.name); return index < 0 ? int.MaxValue : index; }).ToList();
        }

        public static AnimationSourceInfo Read(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            var info = new AnimationSourceInfo
            {
                Path = assetPath,
                Name = Path.GetFileNameWithoutExtension(assetPath),
                Rig = importer != null ? importer.animationType : (ModelImporterAnimationType?)null,
                IsClipFile = AssetDatabase.GetMainAssetTypeAtPath(assetPath) == typeof(AnimationClip),
            };
            var seen = new Dictionary<string, int>();
            foreach (var clip in Clips(assetPath))
            {
                seen.TryGetValue(clip.name, out int occurrence);
                seen[clip.name] = occurrence + 1;
                info.Clips.Add(new AnimationClipInfo
                {
                    Name = clip.name, Occurrence = occurrence, Length = clip.length, FrameRate = clip.frameRate,
                    Frames = Mathf.RoundToInt(clip.length * clip.frameRate), Loop = Loops(clip), Humanoid = clip.humanMotion,
                    Legacy = clip.legacy, Events = AnimationUtility.GetAnimationEvents(clip).Length,
                });
            }
            return info;
        }

        /// <summary>Where a model's clips go unless the user chooses: "&lt;model folder&gt;/&lt;model name&gt; Animations".</summary>
        public static string DefaultFolder(string sourcePath)
        {
            string name = AssetPaths.FileName(Path.GetFileNameWithoutExtension(sourcePath ?? ""), "Model") + " Animations";
            string parent = AssetPaths.Normalize(Path.GetDirectoryName(sourcePath ?? ""));
            // Packages from a registry or git are read-only: their clips go under Assets.
            if (parent != "Assets" && !parent.StartsWith("Assets/", StringComparison.Ordinal) && !Writable(parent)) parent = "Assets/Animations";
            return parent + "/" + name;
        }

        /// <summary>
        /// The name a clip's .anim starts with: the model's when it has a single clip (Mixamo names every take "mixamo.com"),
        /// else the clip's.
        /// </summary>
        public static string DefaultName(AnimationSourceInfo source, AnimationClipInfo clip) =>
            AssetPaths.FileName(source.Clips.Count == 1 ? source.Name : clip.Name, "Animation");

        /// <summary>Saves every job's clip, in one batch. See <see cref="ExtractionRun"/> to spread the work over editor updates.</summary>
        public static List<ExtractionResult> Extract(IEnumerable<ExtractionJob> jobs, ExtractionOptions options)
        {
            var run = new ExtractionRun(jobs, options);
            while (run.Step(double.PositiveInfinity)) { }
            return run.Results.ToList();
        }

        /// <summary>
        /// Copies files from outside the project into <paramref name="folder"/>, imports them and returns those holding clips, in
        /// order. A file already copied there (same name and bytes) is reused, another one with that name gets a numbered name;
        /// copies holding no clip are removed again. What went wrong is added to <paramref name="problems"/>.
        /// </summary>
        public static List<string> ImportExternal(IEnumerable<string> files, string folder, List<string> problems)
        {
            var imported = new List<string>();
            folder = AssetPaths.Normalize(folder);
            var sources = (files ?? Enumerable.Empty<string>()).Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (sources.Count == 0) return imported;
            if (!AssetPaths.EnsureFolder(folder))
            {
                problems?.Add($"The folder {folder} could not be created.");
                return imported;
            }

            var targets = new List<(string source, string target, bool copy)>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string source in sources)
            {
                string stem = folder + "/" + AssetPaths.FileName(Path.GetFileNameWithoutExtension(source), "Model"), extension = Path.GetExtension(source);
                string target = stem + extension;
                for (int i = 1; ; i++)
                {
                    if (!taken.Contains(target))
                    {
                        if (!AssetPaths.Exists(target)) { targets.Add((source, target, true)); break; }
                        if (SameBytes(source, target)) { targets.Add((source, target, false)); break; }
                    }
                    target = stem + " " + i + extension;
                }
                taken.Add(target);
            }

            var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var (source, target, copy) in targets.Where(t => t.copy))
                {
                    try
                    {
                        File.Copy(source, FileUtil.GetPhysicalPath(target));
                        AssetDatabase.ImportAsset(target);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException)
                    {
                        problems?.Add($"{Path.GetFileName(source)} could not be copied: {ex.Message}");
                        failed.Add(target);
                    }
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }

            foreach (var (source, target, copy) in targets)
            {
                if (failed.Contains(target)) continue;
                if (Clips(target).Count > 0) { imported.Add(target); continue; }
                problems?.Add($"{Path.GetFileName(source)} has no animations.");
                if (copy) AssetDatabase.DeleteAsset(target);
            }
            return imported;
        }

        /// <summary>Whether a clip loops: Loop Time, or for a legacy clip its wrap mode.</summary>
        public static bool Loops(AnimationClip clip) =>
            clip.legacy ? clip.wrapMode == WrapMode.Loop || clip.wrapMode == WrapMode.PingPong : AnimationUtility.GetAnimationClipSettings(clip).loopTime;

        internal static void ApplyLoop(AnimationClip clip, LoopOverride loop)
        {
            if (loop == LoopOverride.Keep) return;
            bool on = loop == LoopOverride.On;
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = on;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            // Legacy clips loop by wrap mode; one that already plays once or holds its last frame keeps it.
            if (clip.legacy && Loops(clip) != on) clip.wrapMode = on ? WrapMode.Loop : WrapMode.Once;
        }

        private static bool Writable(string folder)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(folder);
            return package != null && (package.source == PackageSource.Embedded || package.source == PackageSource.Local);
        }

        // Compared in chunks: models can be hundreds of MB.
        private static bool SameBytes(string file, string assetPath)
        {
            try
            {
                var a = new FileInfo(file);
                var b = new FileInfo(FileUtil.GetPhysicalPath(assetPath));
                if (!a.Exists || !b.Exists || a.Length != b.Length) return false;
                using (var x = a.OpenRead())
                using (var y = b.OpenRead())
                {
                    var bufferX = new byte[81920];
                    var bufferY = new byte[81920];
                    while (true)
                    {
                        int read = Fill(x, bufferX);
                        if (read != Fill(y, bufferY)) return false;
                        if (read == 0) return true;
                        for (int i = 0; i < read; i++) if (bufferX[i] != bufferY[i]) return false;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }

        private static int Fill(Stream stream, byte[] buffer)
        {
            int total = 0, read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
            return total;
        }
    }

    /// <summary>
    /// An extraction done a slice at a time, so a window can show its progress. Each <see cref="Step"/> saves clips in one
    /// asset-editing batch until its time is up, then saves the clips it replaced.
    /// </summary>
    public sealed class ExtractionRun
    {
        private readonly List<ExtractionJob> jobs;
        private readonly ExtractionOptions options;
        private readonly List<ExtractionResult> results = new List<ExtractionResult>();
        // Paths this run gave a clip: a second "Run" becomes "Run 1" rather than replacing the first, in every mode, and the
        // same jobs land on the same files run after run.
        private readonly HashSet<string> claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<AnimationClip>> sources = new Dictionary<string, List<AnimationClip>>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> missingFolders;

        public ExtractionRun(IEnumerable<ExtractionJob> jobs, ExtractionOptions options)
        {
            this.jobs = (jobs ?? Enumerable.Empty<ExtractionJob>()).Where(j => j != null).ToList();
            this.options = options ?? new ExtractionOptions();
        }

        public int Count => jobs.Count;
        public int Completed => results.Count;
        public bool Finished => results.Count >= jobs.Count;
        public IReadOnlyList<ExtractionResult> Results => results;
        /// <summary>The job the next step starts with, or null once finished.</summary>
        public ExtractionJob Next => Finished ? null : jobs[results.Count];

        /// <summary>Saves clips for about <paramref name="seconds"/> (at least one). Returns whether some are left.</summary>
        public bool Step(double seconds)
        {
            if (Finished) return false;
            // Folders first, outside the batch: one created inside it is not a valid parent until the batch ends.
            if (missingFolders == null)
                missingFolders = new HashSet<string>(jobs.Select(j => AssetPaths.Normalize(j.Folder)).Distinct().Where(f => !AssetPaths.EnsureFolder(f)));
            var replaced = new List<AnimationClip>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            AssetDatabase.StartAssetEditing();
            try
            {
                do results.Add(Extract(jobs[results.Count], replaced));
                while (!Finished && clock.Elapsed.TotalSeconds < seconds);
            }
            finally { AssetDatabase.StopAssetEditing(); }
            // New clips were written by CreateAsset; replaced ones are saved on their own, not with every dirty asset.
            foreach (var clip in replaced) if (clip != null) AssetDatabase.SaveAssetIfDirty(clip);
            return !Finished;
        }

        private ExtractionResult Extract(ExtractionJob job, List<AnimationClip> replaced)
        {
            var result = new ExtractionResult { Job = job };
            try
            {
                string folder = AssetPaths.Normalize(job.Folder);
                if (missingFolders.Contains(folder)) return Fail(result, string.IsNullOrEmpty(folder) ? "No output folder." : $"The folder {folder} could not be created.");
                var source = Source(job);
                if (source == null) return Fail(result, $"{Path.GetFileName(job.Source)} has no clip named “{job.Clip}”.");

                string stem = folder + "/" + AssetPaths.FileName(job.Name, AssetPaths.FileName(job.Clip, "Animation"));
                string path = stem + ".anim";
                for (int i = 1; claimed.Contains(path) || (options.Existing == ExistingAnimation.KeepBoth && AssetPaths.Exists(path)); i++)
                    path = stem + " " + i + ".anim";
                claimed.Add(path);
                result.Path = path;

                if (!AssetPaths.Exists(path))
                {
                    var copy = new AnimationClip();
                    Fill(copy, source, path);
                    AssetDatabase.CreateAsset(copy, path);
                    if (!EditorUtility.IsPersistent(copy))
                    {
                        UnityEngine.Object.DestroyImmediate(copy);
                        return Fail(result, $"{Path.GetFileName(path)} could not be created.");
                    }
                    result.Status = ExtractionStatus.Done;
                    return result;
                }
                if (options.Existing == ExistingAnimation.Skip) return Skip(result, $"{Path.GetFileName(path)} already exists.");
                if (!(AssetDatabase.LoadMainAssetAtPath(path) is AnimationClip existing)) return Fail(result, $"{Path.GetFileName(path)} exists and is not an animation clip.");
                if (existing == source) return Skip(result, "This is the clip being extracted.");
                // Copied into the existing clip, never deleted and created again: its GUID and every reference to it survive.
                Fill(existing, source, path);
                EditorUtility.SetDirty(existing);
                replaced.Add(existing);
                result.Replaced = true;
                result.Status = ExtractionStatus.Done;
                return result;
            }
            catch (Exception ex) { return Fail(result, ex.Message); }
        }

        // The whole clip: curves (humanoid muscles included), events and settings.
        private void Fill(AnimationClip target, AnimationClip source, string path)
        {
            EditorUtility.CopySerialized(source, target);
            // A native asset's main object is named after its file; a model's clips are read-only, their copies are not.
            target.name = Path.GetFileNameWithoutExtension(path);
            target.hideFlags = HideFlags.None;
            AnimationExtraction.ApplyLoop(target, options.Loop);
        }

        private AnimationClip Source(ExtractionJob job)
        {
            if (string.IsNullOrEmpty(job.Source) || job.Clip == null) return null;
            if (!sources.TryGetValue(job.Source, out var clips)) sources[job.Source] = clips = AnimationExtraction.Clips(job.Source);
            return clips.Where(c => c != null && c.name == job.Clip).ElementAtOrDefault(Math.Max(0, job.Occurrence));
        }

        private static ExtractionResult Fail(ExtractionResult result, string message)
        {
            result.Status = ExtractionStatus.Failed;
            result.Message = message;
            return result;
        }

        private static ExtractionResult Skip(ExtractionResult result, string message)
        {
            result.Status = ExtractionStatus.Skipped;
            result.Message = message;
            return result;
        }
    }
}
