using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.BlendShapes
{
    public enum BlendShapeLinkEndpoint { BlendShape, Animation }

    /// <summary>
    /// One build-time link: wherever the trigger is animated, the effect follows. A factor link wraps the original and the
    /// affected clip in a 1D blend tree driven by <see cref="FactorParameter"/>; a direct copy replaces the clip outright.
    /// </summary>
    public struct BlendShapeLink
    {
        /// <summary>Renderer the link belongs to, relative to the avatar root; diagnostics only.</summary>
        public string TargetRendererPath;
        public BlendShapeLinkEndpoint TriggerType;
        public string TriggerName;
        /// <summary>Animation trigger: matches clips by content first, since VRCFury renames and copies clips.</summary>
        public AnimationClipSignature TriggerSignature;
        public BlendShapeLinkEndpoint EffectType;
        public string EffectName;
        public AnimationClip EffectClip;
        /// <summary>Blendshape trigger binding (renderer path, "blendShape.name").</summary>
        public string SourcePath, SourceProperty;
        /// <summary>Blendshape effect binding (renderer path, "blendShape.name").</summary>
        public string DestinationPath, DestinationProperty;
        public string FactorParameter;
        public bool SetFactorDefault;
        public float FactorDefault;
        /// <summary>Copies the source curve exactly: no factor parameter, no wrapper tree.</summary>
        public bool DirectCopy;

        public static BlendShapeLink Copy(string sourcePath, string sourceShape, string destinationPath, string destinationShape) =>
            new BlendShapeLink
            {
                TargetRendererPath = destinationPath,
                TriggerType = BlendShapeLinkEndpoint.BlendShape, TriggerName = sourceShape,
                EffectType = BlendShapeLinkEndpoint.BlendShape, EffectName = destinationShape,
                SourcePath = sourcePath, SourceProperty = "blendShape." + sourceShape,
                DestinationPath = destinationPath, DestinationProperty = "blendShape." + destinationShape,
                DirectCopy = true
            };
    }

    public struct BlendShapeLinkResult
    {
        public bool Success;
        public int Links, Controllers, ClipsWrapped, StatesRewritten;
        public string Message;
        public static BlendShapeLinkResult Fail(string message) => new BlendShapeLinkResult { Message = message };
    }

    /// <summary>A link the engine applied to a controller in this editor session, for debug views.</summary>
    public struct AppliedBlendShapeLink
    {
        public string ControllerName, ControllerPath, FactorParameter, TargetRendererPath, Trigger, Effect, Label;
    }

    /// <summary>The distinctive animated bindings of a clip with a few sampled values, to recognise copies of it.</summary>
    public sealed class AnimationClipSignature
    {
        private struct Binding
        {
            public string Path, Property;
            public Type Type;
            public float[] Times, Values;
        }

        private struct Candidate
        {
            public string Path, Property;
            public Type Type;
            public AnimationCurve Curve;
        }

        private const float Epsilon = 0.001f;
        private readonly List<Binding> bindings;

        private AnimationClipSignature(List<Binding> bindings) => this.bindings = bindings;

        public int Count => bindings.Count;

        public static AnimationClipSignature Build(AnimationClip clip)
        {
            var output = new List<Binding>();
            if (clip == null) return new AnimationClipSignature(output);
            var dedupe = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.length == 0 || string.IsNullOrWhiteSpace(binding.propertyName)) continue;
                string path = binding.path ?? string.Empty;
                if (!dedupe.Add(path + "|" + binding.propertyName + "|" + (binding.type != null ? binding.type.FullName : string.Empty))) continue;
                var keyTimes = curve.keys.Select(k => k.time).Distinct().OrderBy(t => t).ToList();
                if (keyTimes.Count == 0) continue;
                // Compact and deterministic: first, last and middle key.
                var samples = new List<float> { keyTimes.First(), keyTimes.Last() };
                if (keyTimes.Count > 2) samples.Add(keyTimes[keyTimes.Count / 2]);
                samples = samples.Distinct().OrderBy(t => t).ToList();
                output.Add(new Binding
                {
                    Path = path, Type = binding.type, Property = binding.propertyName,
                    Times = samples.ToArray(), Values = samples.Select(curve.Evaluate).ToArray()
                });
            }
            // Only distinctive channels, to reduce false positives.
            return new AnimationClipSignature(output.Where(IsDistinctive).ToList());
        }

        /// <summary>
        /// True when the clip animates every channel of the signature with the same sampled values. Extra channels are
        /// allowed: VRCFury adds curves to its copies (blendshape links, for one).
        /// </summary>
        public bool Matches(AnimationClip clip)
        {
            if (clip == null || bindings.Count == 0) return false;
            var candidates = Candidates(clip);
            if (candidates.Count < bindings.Count) return false;
            var matches = new List<int>[bindings.Count];
            int signatureIndex = 0;
            foreach (var sig in bindings)
            {
                var sameChannel = Enumerable.Range(0, candidates.Count).Where(i => SameTypeAndProperty(sig, candidates[i])).ToList();
                var samePath = sameChannel.Where(i => string.Equals(sig.Path, candidates[i].Path, StringComparison.Ordinal)).ToList();
                // VRCFury remaps paths: without the same path, any path with the same type/property and sampled values.
                matches[signatureIndex] = (samePath.Count > 0 ? samePath : sameChannel).Where(i => MatchesSamples(candidates[i].Curve, sig)).ToList();
                if (matches[signatureIndex++].Count == 0) return false;
            }
            // A remapped channel still represents only one source channel. Match distinct candidates, allowing
            // reassignment so an early flexible match cannot consume the only candidate of a later exact match.
            var owners = Enumerable.Repeat(-1, candidates.Count).ToArray();
            bool Assign(int index, bool[] visited)
            {
                foreach (int candidate in matches[index])
                {
                    if (visited[candidate]) continue;
                    visited[candidate] = true;
                    if (owners[candidate] >= 0 && !Assign(owners[candidate], visited)) continue;
                    owners[candidate] = index;
                    return true;
                }
                return false;
            }
            for (int i = 0; i < matches.Length; i++)
                if (!Assign(i, new bool[candidates.Count])) return false;
            return true;
        }

        /// <summary>Case-insensitive, without ".anim" and without non-alphanumeric characters.</summary>
        public static string NormalizeClipName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string trimmed = value.Trim();
            if (trimmed.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed.Substring(0, trimmed.Length - 5);
            var sb = new StringBuilder(trimmed.Length);
            foreach (char c in trimmed)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>Signature match first, then the normalized clip name, then the normalized asset file name.</summary>
        public static bool MatchesClip(AnimationClip clip, string targetName, AnimationClipSignature signature)
        {
            if (clip == null || string.IsNullOrWhiteSpace(targetName)) return false;
            if (signature != null && signature.Matches(clip)) return true;
            string target = NormalizeClipName(targetName);
            if (string.Equals(NormalizeClipName(clip.name), target, StringComparison.Ordinal)) return true;
            string path = AssetDatabase.GetAssetPath(clip);
            string fileName = string.IsNullOrWhiteSpace(path) ? string.Empty : NormalizeClipName(System.IO.Path.GetFileNameWithoutExtension(path));
            return string.Equals(fileName, target, StringComparison.Ordinal);
        }

        private static List<Candidate> Candidates(AnimationClip clip)
        {
            var output = new List<Candidate>();
            var dedupe = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.length == 0 || string.IsNullOrWhiteSpace(binding.propertyName)) continue;
                string path = binding.path ?? string.Empty;
                if (!dedupe.Add(path + "|" + binding.propertyName + "|" + (binding.type != null ? binding.type.FullName : string.Empty))) continue;
                output.Add(new Candidate { Path = path, Type = binding.type, Property = binding.propertyName, Curve = curve });
            }
            return output;
        }

        private static bool SameTypeAndProperty(Binding signature, Candidate candidate)
        {
            if (!string.Equals(signature.Property, candidate.Property, StringComparison.Ordinal)) return false;
            // Unity sometimes returns reduced type metadata.
            return signature.Type == null || candidate.Type == null || signature.Type == candidate.Type;
        }

        private static bool MatchesSamples(AnimationCurve curve, Binding signature)
        {
            for (int i = 0; i < signature.Times.Length; i++)
                if (Mathf.Abs(curve.Evaluate(signature.Times[i]) - signature.Values[i]) > Epsilon) return false;
            return true;
        }

        private static bool IsDistinctive(Binding binding)
        {
            const float eps = 0.0001f;
            return binding.Values.Max() - binding.Values.Min() > eps || binding.Values.Max(v => Mathf.Abs(v)) > eps;
        }
    }
}
