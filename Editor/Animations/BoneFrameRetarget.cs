using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Animations
{
    /// <summary>
    /// Animations written for a skeleton whose bones rest in other orientations (a model re-exported with other bone
    /// rolls: Ultirex V5.1's toes are turned up to 180° from Rexouium's) keep their meaning on the current skeleton. A
    /// rotation curve stores absolute local rotations, so on a re-oriented bone it twists the bone instead of curling
    /// it. Each rotation becomes Left · R · Right: the same turn away from the rest pose, in the current bone frames.
    /// </summary>
    public static class BoneFrameRetarget
    {
        /// <summary>A bone's frames at rest on the skeleton the animations were written for, and on the current one.</summary>
        public sealed class Frame
        {
            public Quaternion AuthoredRest, CurrentRest;
            public Quaternion Left, Right;

            /// <summary>From root-relative rest rotations of the bone and its parent, on both skeletons.</summary>
            public static Frame Between(Quaternion authoredParent, Quaternion authoredBone, Quaternion currentParent, Quaternion currentBone) => new Frame
            {
                AuthoredRest = Quaternion.Inverse(authoredParent) * authoredBone,
                CurrentRest = Quaternion.Inverse(currentParent) * currentBone,
                Left = Quaternion.Inverse(currentParent) * authoredParent,
                Right = Quaternion.Inverse(authoredBone) * currentBone,
            };

            public Quaternion Convert(Quaternion authored) => Left * authored * Right;
        }

        private const float SampleRate = 30f;
        private static readonly string[] Quaternions = { "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w" };

        /// <summary>
        /// Rewrites the rotation curves of the bones in <paramref name="frames"/> (by animation path) in each clip written
        /// for the authored skeleton: its first pose of those bones is nearer their authored rest than their current one,
        /// so clips already made for the current skeleton stay. Returns how many clips changed.
        /// </summary>
        public static int Retarget(IEnumerable<AnimationClip> clips, IReadOnlyDictionary<string, Frame> frames)
        {
            int changed = 0;
            foreach (var clip in clips.Where(c => c != null).Distinct())
            {
                var groups = AnimationUtility.GetCurveBindings(clip)
                    .Where(b => b.type == typeof(Transform) && frames.ContainsKey(b.path) && Rotation(b.propertyName) != null)
                    .GroupBy(b => (b.path, kind: Rotation(b.propertyName)))
                    .ToList();
                if (groups.Count == 0) continue;
                float authored = 0f, current = 0f;
                foreach (var group in groups)
                {
                    var frame = frames[group.Key.path];
                    var curves = Curves(clip, group.Key.path, group.Key.kind);
                    var first = Sample(curves, group.Key.kind, Times(curves).First(), frame.AuthoredRest);
                    authored += Quaternion.Angle(first, frame.AuthoredRest);
                    current += Quaternion.Angle(first, frame.CurrentRest);
                }
                if (authored >= current) continue;
                var bindings = new List<EditorCurveBinding>();
                var written = new List<AnimationCurve>();
                foreach (var group in groups)
                {
                    var (path, kind) = group.Key;
                    var frame = frames[path];
                    var curves = Curves(clip, path, kind);
                    var times = Resampled(Times(curves));
                    var properties = Properties(kind);
                    var keys = properties.Select(_ => new List<Keyframe>()).ToArray();
                    Vector3 previousEuler = frame.CurrentRest.eulerAngles;
                    var previous = frame.CurrentRest;
                    foreach (float time in times)
                    {
                        var rotation = frame.Convert(Sample(curves, kind, time, frame.AuthoredRest));
                        if (kind == "m_LocalRotation")
                        {
                            // The same rotation with the sign nearest the last key: interpolation takes the short way.
                            if (Quaternion.Dot(rotation, previous) < 0) rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
                            previous = rotation;
                            for (int i = 0; i < 4; i++) keys[i].Add(new Keyframe(time, rotation[i]));
                        }
                        else
                        {
                            // Raw Euler values interpolate as numbers: each angle is unwrapped next to the last key's.
                            var euler = rotation.eulerAngles;
                            for (int i = 0; i < 3; i++) euler[i] = previousEuler[i] + Mathf.DeltaAngle(previousEuler[i], euler[i]);
                            previousEuler = euler;
                            for (int i = 0; i < 3; i++) keys[i].Add(new Keyframe(time, euler[i]));
                        }
                    }
                    for (int i = 0; i < properties.Length; i++)
                    {
                        var curve = new AnimationCurve(keys[i].ToArray());
                        for (int k = 0; k < curve.length; k++)
                        {
                            AnimationUtility.SetKeyLeftTangentMode(curve, k, AnimationUtility.TangentMode.ClampedAuto);
                            AnimationUtility.SetKeyRightTangentMode(curve, k, AnimationUtility.TangentMode.ClampedAuto);
                        }
                        bindings.Add(EditorCurveBinding.FloatCurve(path, typeof(Transform), properties[i]));
                        written.Add(curve);
                    }
                }
                AnimationUtility.SetEditorCurves(clip, bindings.ToArray(), written.ToArray());
                EditorUtility.SetDirty(clip);
                changed++;
            }
            return changed;
        }

        // "localEulerAnglesRaw" (or Baked, or plain) for Euler curves, "m_LocalRotation" for quaternion ones.
        private static string Rotation(string property)
        {
            int dot = property.LastIndexOf('.');
            if (dot < 0) return null;
            string kind = property.Substring(0, dot);
            return kind == "m_LocalRotation" || kind.StartsWith("localEulerAngles", StringComparison.Ordinal) ? kind : null;
        }

        private static string[] Properties(string kind) => kind == "m_LocalRotation" ? Quaternions : new[] { kind + ".x", kind + ".y", kind + ".z" };

        // Missing components keep their authored rest value, as the animation left them on the authored skeleton.
        private static AnimationCurve[] Curves(AnimationClip clip, string path, string kind) =>
            Properties(kind).Select(p => AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), p))).ToArray();

        private static Quaternion Sample(AnimationCurve[] curves, string kind, float time, Quaternion rest)
        {
            if (kind == "m_LocalRotation")
            {
                var q = new Quaternion(
                    curves[0]?.Evaluate(time) ?? rest.x, curves[1]?.Evaluate(time) ?? rest.y,
                    curves[2]?.Evaluate(time) ?? rest.z, curves[3]?.Evaluate(time) ?? rest.w);
                float length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                return length < 1e-6f ? rest : new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length);
            }
            var euler = rest.eulerAngles;
            return Quaternion.Euler(curves[0]?.Evaluate(time) ?? euler.x, curves[1]?.Evaluate(time) ?? euler.y, curves[2]?.Evaluate(time) ?? euler.z);
        }

        private static List<float> Times(AnimationCurve[] curves)
        {
            var times = curves.Where(c => c != null).SelectMany(c => c.keys.Select(k => k.time)).Distinct().OrderBy(t => t).ToList();
            if (times.Count == 0) times.Add(0f);
            return times;
        }

        // A pose (one or two keys) keeps its keys; a motion is sampled between them too, so its path stays the same turn.
        private static List<float> Resampled(List<float> times)
        {
            if (times.Count <= 2) return times;
            var output = new SortedSet<float>(times);
            for (float t = times[0]; t < times[times.Count - 1]; t += 1f / SampleRate) output.Add(t);
            return output.ToList();
        }
    }
}
