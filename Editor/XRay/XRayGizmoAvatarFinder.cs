using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Toolkit.Editor.XRay
{
    internal static class XRayGizmoTargetFinder
    {
        public static List<XRayArmatureTarget> FindSceneTargets()
        {
            var targetsByArmature = new Dictionary<int, XRayArmatureTarget>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                {
                    continue;
                }

                foreach (var root in scene.GetRootGameObjects())
                {
                    AddTargets(root, targetsByArmature);
                }
            }

            var targets = new List<XRayArmatureTarget>(targetsByArmature.Values);
            targets.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
            return targets;
        }

        public static bool TryResolveTarget(GameObject selected, out XRayArmatureTarget target)
        {
            target = default;
            if (selected == null)
            {
                return false;
            }

            if (TryFindRigOf(selected.transform, out target))
            {
                return true;
            }

            var current = selected.transform;
            while (current != null)
            {
                if (TryFindBestTarget(current.gameObject, out target))
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        public static bool TryBuildTarget(SkinnedMeshRenderer renderer, out XRayArmatureTarget target)
        {
            target = default;
            if (!XRayArmatureMeshGenerator.IsUsableRenderer(renderer))
            {
                return false;
            }

            var armatureRoot = ResolveArmatureRoot(renderer);
            if (armatureRoot == null)
            {
                return false;
            }

            var owner = ResolveOwner(renderer, armatureRoot);
            if (owner == null)
            {
                return false;
            }

            int boneCount = renderer.bones != null ? renderer.bones.Length : 0;
            int vertexCount = renderer.sharedMesh != null ? renderer.sharedMesh.vertexCount : 0;
            int score = boneCount * 1000000 + vertexCount;
            target = new XRayArmatureTarget(owner, renderer, armatureRoot, score);
            return true;
        }

        private static void AddTargets(GameObject root, Dictionary<int, XRayArmatureTarget> targetsByArmature)
        {
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!TryBuildTarget(renderer, out var target))
                {
                    continue;
                }

                if (!targetsByArmature.TryGetValue(target.ArmatureKey, out var existing) ||
                    target.Score > existing.Score)
                {
                    targetsByArmature[target.ArmatureKey] = target;
                }
            }
        }

        // Relates the selection to the rigs of its whole hierarchy rather than to the first ancestor holding any skinned
        // mesh: an accessory with its own armature placed on a bone (hair on the head) sits below the chest, so walking
        // up from a hand would otherwise reach the accessory's renderer before the avatar's body. In order:
        // 1. a bone belongs to the rig whose mesh is skinned to it (the one with the most bones);
        // 2. an object that holds an armature (avatar root, accessory container) selects that rig;
        // 3. an unskinned helper or end bone (holding no skinned mesh) belongs to the nearest armature containing it.
        // Anything else (mesh objects, props) keeps the walk-up.
        private static bool TryFindRigOf(Transform selected, out XRayArmatureTarget target)
        {
            target = default;
            bool hasUser = false, hasOwned = false, hasContainer = false;
            XRayArmatureTarget user = default, owned = default, container = default;
            int containerDepth = -1;

            foreach (var renderer in selected.root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!TryBuildTarget(renderer, out var candidate))
                {
                    continue;
                }

                if (System.Array.IndexOf(renderer.bones, selected) >= 0)
                {
                    if (!hasUser || candidate.Score > user.Score)
                    {
                        user = candidate;
                        hasUser = true;
                    }
                }

                if (candidate.Owner == selected.gameObject && (!hasOwned || candidate.Score > owned.Score))
                {
                    owned = candidate;
                    hasOwned = true;
                }

                if (selected.IsChildOf(candidate.ArmatureRoot))
                {
                    int depth = Depth(candidate.ArmatureRoot);
                    if (depth > containerDepth || (depth == containerDepth && candidate.Score > container.Score))
                    {
                        container = candidate;
                        containerDepth = depth;
                        hasContainer = true;
                    }
                }
            }

            // A mesh object inside an armature (an accessory's mesh placed on a bone) is not a helper bone: the walk-up
            // finds its own rig.
            if (hasContainer && !hasUser && !hasOwned && HoldsSkinnedMesh(selected))
            {
                hasContainer = false;
            }

            target = hasUser ? user : hasOwned ? owned : container;
            return hasUser || hasOwned || hasContainer;
        }

        private static bool HoldsSkinnedMesh(Transform transform)
        {
            foreach (var renderer in transform.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (XRayArmatureMeshGenerator.IsUsableRenderer(renderer))
                {
                    return true;
                }
            }

            return false;
        }

        private static int Depth(Transform transform)
        {
            int depth = 0;
            for (var current = transform; current != null; current = current.parent)
            {
                depth++;
            }

            return depth;
        }

        private static bool TryFindBestTarget(GameObject root, out XRayArmatureTarget target)
        {
            target = default;
            int bestScore = -1;

            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!TryBuildTarget(renderer, out var candidate))
                {
                    continue;
                }

                if (candidate.Score > bestScore)
                {
                    bestScore = candidate.Score;
                    target = candidate;
                }
            }

            return bestScore >= 0;
        }

        private static Transform ResolveArmatureRoot(SkinnedMeshRenderer renderer)
        {
            if (renderer == null || renderer.rootBone == null)
            {
                return null;
            }

            var current = renderer.rootBone;
            while (current != null)
            {
                if (string.Equals(current.name, "Armature", System.StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                current = current.parent;
            }

            return renderer.rootBone;
        }

        private static GameObject ResolveOwner(SkinnedMeshRenderer renderer, Transform armatureRoot)
        {
            if (armatureRoot != null && armatureRoot.parent != null)
            {
                return armatureRoot.parent.gameObject;
            }

            return renderer != null ? renderer.gameObject : null;
        }
    }

    internal readonly struct XRayArmatureTarget
    {
        public readonly GameObject Owner;
        public readonly SkinnedMeshRenderer Renderer;
        public readonly Transform ArmatureRoot;
        public readonly int ArmatureKey;
        public readonly int Score;

        public XRayArmatureTarget(GameObject owner, SkinnedMeshRenderer renderer, Transform armatureRoot, int score)
        {
            Owner = owner;
            Renderer = renderer;
            ArmatureRoot = armatureRoot;
            ArmatureKey = armatureRoot != null ? armatureRoot.GetInstanceID() : 0;
            Score = score;
        }

        public string DisplayName => Owner != null ? Owner.name : "Armature";

        public bool IsValid => Owner != null &&
                               Renderer != null &&
                               ArmatureRoot != null &&
                               ArmatureKey != 0;
    }
}
