using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor.Animations;
using Orbiters.Toolkit.Editor.Storage;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    /// <summary>
    /// Poses the user adds to the photoshoot from humanoid animations in a project (an .anim, or a model's clip). A pose is
    /// what the photoshoot samples, every curve's value at the clip's start, saved in their own
    /// <see cref="PhotoshootLibrary{T}"/> so every project offers it, also without the animation.
    /// </summary>
    internal static class PhotoshootPoses
    {
        private const string Extension = ".json";
        private static readonly PhotoshootLibrary<AnimationClip> Library =
            new PhotoshootLibrary<AnimationClip>(PhotoshootLibrary.UserFolder("Poses"), Read, Extension);

        [Serializable]
        private sealed class StoredPose
        {
            public List<StoredValue> values = new List<StoredValue>();
        }

        [Serializable]
        private sealed class StoredValue
        {
            public string path;
            /// <summary>"Namespace.Type, Assembly": no version, so a pose saved in one Unity opens in another.</summary>
            public string type;
            public string property;
            public float value;
        }

        internal static IEnumerable<string> Files() => Library.Files();

        /// <summary>The pose's clip, read again when its file changed; null when it can't be read.</summary>
        internal static AnimationClip Load(string path) => Library.Load(path);

        internal static void Remove(string path) => Library.Remove(path);

        /// <summary>An animation file, or a model that may carry clips.</summary>
        internal static bool IsAnimationFile(string path) =>
            string.Equals(Path.GetExtension(path ?? string.Empty), ".anim", StringComparison.OrdinalIgnoreCase) || AnimationExtraction.IsModelFile(path);

        /// <summary>The clips of an animation or model in this project, chosen as a file on disk or an asset path.</summary>
        internal static List<AnimationClip> ClipsIn(string file)
        {
            string asset = AssetPaths.IsProjectPath(file) ? AssetPaths.Normalize(file) : AssetPaths.FromFullPath(file);
            if (asset == null) throw new InvalidOperationException(Path.GetFileName(file) + " is not in this project: import it first.");
            var clips = AnimationExtraction.Clips(asset);
            if (clips.Count == 0) throw new InvalidOperationException(Path.GetFileName(file) + " has no animations.");
            return clips;
        }

        /// <summary>Saves the clip's pose and returns its file. Only humanoid animations pose any avatar.</summary>
        internal static string Add(AnimationClip clip)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            string name = Name(clip);
            if (!clip.humanMotion) throw new InvalidOperationException(name + " is not a humanoid animation (a model's needs its rig set to Humanoid).");
            string path = Library.NewPath(name, Extension);
            Write(clip, path);
            return path;
        }

        /// <summary>An .anim keeps its name; a model's clip is named after the model, with the clip's name when it has several.</summary>
        internal static string Name(AnimationClip clip)
        {
            string path = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(path) || AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(AnimationClip)) return clip.name;
            string model = Path.GetFileNameWithoutExtension(path);
            return AnimationExtraction.Clips(path).Count > 1 ? model + " " + clip.name : model;
        }

        internal static void Write(AnimationClip clip, string file)
        {
            var pose = new StoredPose();
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || binding.type == null) continue;
                pose.values.Add(new StoredValue
                {
                    path = binding.path, type = binding.type.FullName + ", " + binding.type.Assembly.GetName().Name,
                    property = binding.propertyName, value = curve.Evaluate(0f)
                });
            }
            File.WriteAllText(file, JsonUtility.ToJson(pose, true));
        }

        /// <summary>A new clip holding the saved pose, each value as a one-key curve; null when the file holds none.</summary>
        internal static AnimationClip Read(string file)
        {
            var pose = JsonUtility.FromJson<StoredPose>(File.ReadAllText(file));
            var values = (pose?.values ?? new List<StoredValue>())
                .Select(value => (value, type: string.IsNullOrEmpty(value.type) ? null : Type.GetType(value.type)))
                .Where(entry => entry.type != null && entry.value.property != null).ToList();
            if (values.Count == 0) return null;
            var clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(file), hideFlags = HideFlags.HideAndDontSave };
            AnimationUtility.SetEditorCurves(clip,
                values.Select(entry => EditorCurveBinding.FloatCurve(entry.value.path ?? string.Empty, entry.type, entry.value.property)).ToArray(),
                values.Select(entry => new AnimationCurve(new Keyframe(0f, entry.value.value))).ToArray());
            return clip;
        }
    }
}
