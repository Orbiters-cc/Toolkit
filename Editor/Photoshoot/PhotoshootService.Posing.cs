using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    // Posing: humanoid clip sampling and face blendshapes.
    public static partial class PhotoshootService
    {
        public static void SampleBodyPose(GameObject avatarRoot, AnimationClip clip)
        {
            if (avatarRoot == null || clip == null)
            {
                return;
            }

            // Humanoid sampling can restore the imported Avatar's bone translations.
            // A custom base may have moved eyes or changed limb proportions since import.
            var animator = avatarRoot.GetComponentInChildren<Animator>(true);
            var hips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            var bones = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .SelectMany(renderer => renderer.bones).Where(bone => bone != null).Distinct().ToArray();
            var positions = bones.Select(bone => bone.localPosition).ToArray();
            var scales = bones.Select(bone => bone.localScale).ToArray();
            clip.SampleAnimation(avatarRoot, 0f);
            SampleHumanoidBodyPose(avatarRoot, clip);
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != hips) bones[i].localPosition = positions[i];
                bones[i].localScale = scales[i];
            }
        }

        private static void SampleHumanoidBodyPose(GameObject avatarRoot, AnimationClip clip)
        {
            var animator = avatarRoot.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
            {
                return;
            }

            var humanPose = new HumanPose();
            using (var humanPoseHandler = new HumanPoseHandler(animator.avatar, animator.transform))
            {
                humanPoseHandler.GetHumanPose(ref humanPose);
                if (ApplyHumanoidClipCurves(clip, ref humanPose))
                {
                    humanPoseHandler.SetHumanPose(ref humanPose);
                }
            }
        }

        private static bool ApplyHumanoidClipCurves(AnimationClip clip, ref HumanPose humanPose)
        {
            bool changed = false;
            bool rootRotationChanged = false;
            Dictionary<string, int> muscleIndices = BuildHumanoidMuscleIndexLookup();
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null)
                {
                    continue;
                }

                float value = curve.Evaluate(0f);
                if (TryGetHumanoidMuscleIndex(binding.propertyName, muscleIndices, out int muscleIndex) &&
                    humanPose.muscles != null &&
                    muscleIndex < humanPose.muscles.Length)
                {
                    humanPose.muscles[muscleIndex] = value;
                    changed = true;
                    continue;
                }

                if (ApplyHumanoidRootCurve(binding.propertyName, value, ref humanPose, ref rootRotationChanged))
                {
                    changed = true;
                }
            }

            if (rootRotationChanged)
            {
                humanPose.bodyRotation = NormalizeQuaternion(humanPose.bodyRotation);
            }

            return changed;
        }

        private static bool TryGetHumanoidMuscleIndex(
            string propertyName,
            Dictionary<string, int> muscleIndices,
            out int muscleIndex)
        {
            if (muscleIndices.TryGetValue(propertyName, out muscleIndex))
            {
                return true;
            }

            string normalizedName = NormalizeHumanoidMuscleName(propertyName);
            return !string.IsNullOrEmpty(normalizedName) && muscleIndices.TryGetValue(normalizedName, out muscleIndex);
        }

        private static Dictionary<string, int> BuildHumanoidMuscleIndexLookup()
        {
            var output = new Dictionary<string, int>(StringComparer.Ordinal);
            string[] muscleNames = HumanTrait.MuscleName;
            for (int i = 0; i < muscleNames.Length; i++)
            {
                string muscleName = muscleNames[i];
                if (string.IsNullOrEmpty(muscleName))
                {
                    continue;
                }

                output[muscleName] = i;
                string normalizedName = NormalizeHumanoidMuscleName(muscleName);
                if (!string.IsNullOrEmpty(normalizedName))
                {
                    output[normalizedName] = i;
                }
            }

            return output;
        }

        private static string NormalizeHumanoidMuscleName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            value = value
                .Replace("LeftHand", "Left Hand")
                .Replace("RightHand", "Right Hand");

            var builder = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    builder.Append(' ');
                }
            }

            string[] tokens = builder
                .ToString()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                return string.Empty;
            }

            return string.Concat(tokens.Where(token => !string.Equals(token, "hand", StringComparison.Ordinal)));
        }

        private static bool ApplyHumanoidRootCurve(string propertyName, float value, ref HumanPose humanPose, ref bool rootRotationChanged)
        {
            switch (propertyName)
            {
                case "RootT.x":
                    humanPose.bodyPosition.x = value;
                    return true;
                case "RootT.y":
                    humanPose.bodyPosition.y = value;
                    return true;
                case "RootT.z":
                    humanPose.bodyPosition.z = value;
                    return true;
                case "RootQ.x":
                    humanPose.bodyRotation.x = value;
                    rootRotationChanged = true;
                    return true;
                case "RootQ.y":
                    humanPose.bodyRotation.y = value;
                    rootRotationChanged = true;
                    return true;
                case "RootQ.z":
                    humanPose.bodyRotation.z = value;
                    rootRotationChanged = true;
                    return true;
                case "RootQ.w":
                    humanPose.bodyRotation.w = value;
                    rootRotationChanged = true;
                    return true;
                default:
                    return false;
            }
        }

        private static Quaternion NormalizeQuaternion(Quaternion value)
        {
            float magnitude = Mathf.Sqrt(
                value.x * value.x +
                value.y * value.y +
                value.z * value.z +
                value.w * value.w);
            if (magnitude <= Mathf.Epsilon)
            {
                return Quaternion.identity;
            }

            return new Quaternion(
                value.x / magnitude,
                value.y / magnitude,
                value.z / magnitude,
                value.w / magnitude);
        }

        private static void ResetAndApplyFaceBlendshapes(GameObject avatarRoot, IEnumerable<string> selectedFaceBlendshapeNames)
        {
            var selected = new HashSet<string>(selectedFaceBlendshapeNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var renderer in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer != null ? renderer.sharedMesh : null;
                if (mesh == null)
                {
                    continue;
                }

                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string blendshapeName = mesh.GetBlendShapeName(i);
                    if (!IsFaceBlendshapeCandidate(blendshapeName))
                    {
                        continue;
                    }

                    renderer.SetBlendShapeWeight(i, selected.Contains(blendshapeName) ? 100f : 0f);
                }
            }
        }
    }
}
