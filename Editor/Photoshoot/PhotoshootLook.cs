using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    /// <summary>
    /// "Look at the camera" for a posed avatar copy: the neck and head turn toward the camera, the eyes finish the look.
    /// The avatar always looks straight at the camera; a balance splits that turn between the head and the eyes, from the
    /// head alone (eyes as posed) to the eyes alone (head as posed). Which way
    /// each bone faces is read on the unposed copy, so any rig's bone axes work. Eyes are the humanoid eye bones, else
    /// the ones a tool reports through <see cref="EyeFallback"/> (VRChat's eye-look settings).
    /// </summary>
    public static class PhotoshootLook
    {
        /// <summary>
        /// Eyes of an avatar without humanoid eye bones, with each eye's "looking straight" local rotation when known.
        /// Called on the photoshoot's copy of the avatar; set by Toolkit's VRChat integration.
        /// </summary>
        public static Func<GameObject, (Transform eye, Quaternion? straight)[]> EyeFallback;

        private const float NeckShare = .4f;
        // Eyes sit in sockets and read as looking less far than they turn: they turn this much further than the geometry asks.
        internal static float EyeGain = 1.5f;
        internal static float MaxEyeBoneTurn = 75f;

        internal sealed class Rig
        {
            internal Transform Root, Neck, Head;
            internal Transform[] Eyes = Array.Empty<Transform>();
            // Facing and up axes in each bone's own space; posed local rotations restored before every look.
            internal Vector3 HeadForward, HeadUp;
            internal Vector3[] EyeForward = Array.Empty<Vector3>(), EyeUp = Array.Empty<Vector3>();
            // How strongly each eyeball follows its bone; the rest of its weight follows the head.
            internal float[] EyeWeight = Array.Empty<float>();
            internal readonly Dictionary<Transform, Quaternion> Posed = new Dictionary<Transform, Quaternion>();
            internal bool Applied;
            internal bool HasEyes => Eyes.Length > 0;
            internal bool Usable => Head != null || HasEyes;
        }

        /// <summary>Finds the neck, head and eyes on the unposed copy and how they face.</summary>
        internal static Rig Prepare(GameObject copy)
        {
            var rig = new Rig { Root = copy.transform };
            var animator = copy.GetComponent<Animator>();
            if (animator != null && animator.avatar != null && animator.avatar.isHuman)
            {
                // Bones the meshes are skinned to win over helper objects carrying the same names.
                var weighted = new HashSet<Transform>(copy.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(r => r.bones).Where(b => b != null));
                var byName = copy.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(weighted.Contains).First(), StringComparer.Ordinal);
                var mapped = animator.avatar.humanDescription.human.GroupBy(h => h.humanName, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().boneName, StringComparer.Ordinal);
                Transform Bone(HumanBodyBones bone) =>
                    mapped.TryGetValue(HumanTrait.BoneName[(int)bone], out var name) && byName.TryGetValue(name, out var t) ? t : null;
                rig.Neck = Bone(HumanBodyBones.Neck);
                rig.Head = Bone(HumanBodyBones.Head);
                rig.Eyes = new[] { Bone(HumanBodyBones.LeftEye), Bone(HumanBodyBones.RightEye) }.Where(e => e != null).ToArray();
            }

            Vector3 forward = copy.transform.forward, up = copy.transform.up;
            var straight = new Dictionary<Transform, Quaternion>();
            if (rig.Eyes.Length == 0 && EyeFallback != null)
            {
                var found = EyeFallback(copy) ?? Array.Empty<(Transform, Quaternion?)>();
                rig.Eyes = found.Where(e => e.eye != null).Select(e => e.eye).Distinct().ToArray();
                foreach (var (eye, rotation) in found) if (eye != null && rotation.HasValue) straight[eye] = rotation.Value;
            }
            if (rig.Head != null)
            {
                rig.HeadForward = Quaternion.Inverse(rig.Head.rotation) * forward;
                rig.HeadUp = Quaternion.Inverse(rig.Head.rotation) * up;
            }
            rig.EyeForward = new Vector3[rig.Eyes.Length];
            rig.EyeUp = new Vector3[rig.Eyes.Length];
            rig.EyeWeight = new float[rig.Eyes.Length];
            for (int i = 0; i < rig.Eyes.Length; i++)
            {
                var eye = rig.Eyes[i];
                // Facing is measured with the eye looking straight: its known straight rotation, else as it is now.
                var rotation = straight.TryGetValue(eye, out var local) ? (eye.parent != null ? eye.parent.rotation * local : local) : eye.rotation;
                rig.EyeForward[i] = Quaternion.Inverse(rotation) * forward;
                rig.EyeUp[i] = Quaternion.Inverse(rotation) * up;
                rig.EyeWeight[i] = MovingWeight(copy, eye);
            }
            return rig;
        }

        /// <summary>
        /// The skin weight of the eye bone on what it moves most (the eyeball and iris; weight times distance from the bone):
        /// eyes are often weighted partly to the head, and then turn less than their bone.
        /// </summary>
        private static float MovingWeight(GameObject copy, Transform eye)
        {
            float best = 1f; int bestCount = 0;
            foreach (var renderer in copy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer.sharedMesh;
                int bone = mesh != null ? Array.IndexOf(renderer.bones, eye) : -1;
                if (bone < 0 || bone >= mesh.bindposes.Length) continue;
                var weights = mesh.boneWeights;
                var vertices = mesh.vertices;
                if (weights.Length != vertices.Length) continue;
                var bind = mesh.bindposes[bone];
                var moving = new List<(float score, float weight)>();
                for (int v = 0; v < weights.Length; v++)
                {
                    var w = weights[v];
                    float weight = w.boneIndex0 == bone ? w.weight0 : w.boneIndex1 == bone ? w.weight1 : w.boneIndex2 == bone ? w.weight2 : w.boneIndex3 == bone ? w.weight3 : 0f;
                    if (weight > .05f) moving.Add((weight * bind.MultiplyPoint3x4(vertices[v]).magnitude, weight));
                }
                if (moving.Count <= bestCount) continue;
                bestCount = moving.Count;
                best = moving.OrderByDescending(m => m.score).Take(Math.Max(1, moving.Count / 10)).Average(m => m.weight);
            }
            return Mathf.Clamp(best, .25f, 1f);
        }

        /// <summary>Remembers the pose the look starts from; call after every new body pose.</summary>
        internal static void Capture(Rig rig)
        {
            if (rig == null) return;
            rig.Posed.Clear();
            foreach (var bone in new[] { rig.Neck, rig.Head }.Concat(rig.Eyes)) if (bone != null) rig.Posed[bone] = bone.localRotation;
            rig.Applied = false;
        }

        /// <summary>
        /// Returns the bones to their pose, then, when on, turns them toward <paramref name="camera"/>.
        /// <paramref name="withEyes"/> 0 is the head alone, 1 the eyes alone. True when bones moved.
        /// </summary>
        internal static bool Apply(Rig rig, Vector3 camera, bool on, float withEyes)
        {
            if (rig == null || !rig.Usable) return false;
            bool moved = rig.Applied;
            if (rig.Applied) foreach (var pair in rig.Posed) if (pair.Key != null) pair.Key.localRotation = pair.Value;
            rig.Applied = false;
            if (!on) return moved;

            withEyes = Mathf.Clamp01(withEyes);
            float headWeight = rig.HasEyes ? 1f - withEyes : 1f;
            if (rig.Head != null)
            {
                Vector3 from = Gaze(rig);
                Vector3 face = rig.Head.rotation * rig.HeadForward;
                // The head's share of the turn; the eyes turn the rest.
                float turn = Vector3.Angle(face, camera - from) * headWeight;
                if (turn > .01f)
                {
                    Vector3 target = Vector3.RotateTowards(face, camera - from, Mathf.Deg2Rad * turn, 0f);
                    // The neck takes part of the turn, the head the rest.
                    if (rig.Neck != null) Turn(rig.Neck, face, rig.Head.rotation * rig.HeadUp, Vector3.Slerp(face, target, NeckShare), 1f);
                    Turn(rig.Head, rig.Head.rotation * rig.HeadForward, rig.Head.rotation * rig.HeadUp, target, 1f);
                }
            }
            // At the head end the eyes stay as posed; otherwise they finish the look.
            for (int i = 0; i < rig.Eyes.Length && (withEyes > 0f || rig.Head == null); i++)
            {
                var eye = rig.Eyes[i];
                if (eye == null) continue;
                Vector3 look = eye.rotation * rig.EyeForward[i];
                Vector3 toward = camera - eye.position;
                float weight = rig.EyeWeight[i];
                if (weight >= .98f) weight = 1f;
                // A partly weighted eye shows w·R·front + (1 − w)·front: turn the bone further, so what shows looks at the camera.
                Vector3 axis = Vector3.Cross(look, toward);
                if (axis.sqrMagnitude < 1e-10f) continue;
                axis.Normalize();
                // Past about 75° linear skinning squashes the eyeball: the eye stops there.
                float wanted = Vector3.Angle(look, toward) * EyeGain, low = 0f, high = MaxEyeBoneTurn;
                Vector3 front = look.normalized;
                for (int step = 0; step < 24; step++)
                {
                    float middle = (low + high) * .5f;
                    Vector3 shown = weight * (Quaternion.AngleAxis(middle, axis) * front) + (1f - weight) * front;
                    if (Vector3.Angle(front, shown) < wanted) low = middle; else high = middle;
                }
                eye.rotation = Quaternion.AngleAxis((low + high) * .5f, axis) * eye.rotation;
            }
            rig.Applied = true;
            return true;
        }

        // Where the avatar looks from: between the eyes, else a little above the head bone.
        private static Vector3 Gaze(Rig rig)
        {
            var eyes = rig.Eyes.Where(e => e != null).ToArray();
            if (eyes.Length > 0) return eyes.Aggregate(Vector3.zero, (sum, e) => sum + e.position) / eyes.Length;
            return rig.Head.position + rig.Head.rotation * rig.HeadUp * .08f;
        }

        // Turns a bone so a direction it carries points along another, without rolling it around that direction.
        private static void Turn(Transform bone, Vector3 facing, Vector3 up, Vector3 toward, float weight)
        {
            if (facing.sqrMagnitude < 1e-8f || toward.sqrMagnitude < 1e-8f) return;
            var delta = Quaternion.LookRotation(toward, up) * Quaternion.Inverse(Quaternion.LookRotation(facing, up));
            bone.rotation = Quaternion.Slerp(Quaternion.identity, delta, weight) * bone.rotation;
        }
    }
}
