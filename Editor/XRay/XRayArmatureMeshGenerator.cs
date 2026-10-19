using System.Collections.Generic;
using Orbiters.Toolkit.XRay;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.XRay
{
    public static class XRayArmatureMeshGenerator
    {
        public const string GizmoObjectNamePrefix = "__XRayGizmos_";
        public const string MeshObjectNamePrefix = GizmoObjectNamePrefix + "Armature";

        public static bool TryCreate(
            GameObject targetRoot,
            XRayArmatureMeshType meshType,
            float baseThickness,
            Material material,
            out GameObject meshObject,
            out string message)
        {
            meshObject = null;

            if (!XRayGizmoTargetFinder.TryResolveTarget(targetRoot, out var target))
            {
                message = targetRoot == null
                    ? "Target object is null."
                    : $"No skinned renderer with bones was found on '{targetRoot.name}'.";
                return false;
            }

            return TryCreate(target, meshType, baseThickness, material, out meshObject, out message);
        }

        internal static bool TryCreate(
            XRayArmatureTarget target,
            XRayArmatureMeshType meshType,
            float baseThickness,
            Material material,
            out GameObject meshObject,
            out string message)
        {
            meshObject = null;

            if (!target.IsValid)
            {
                message = "Armature target is invalid.";
                return false;
            }

            var targetRoot = target.Owner;
            var renderer = target.Renderer;
            if (renderer == null)
            {
                message = $"No skinned renderer with bones was found on '{targetRoot.name}'.";
                return false;
            }

            var bones = renderer.bones;
            if (bones == null || bones.Length == 0)
            {
                message = $"Renderer '{renderer.name}' has no bones.";
                return false;
            }

            var rootBone = renderer.rootBone;
            if (rootBone == null)
            {
                message = $"Renderer '{renderer.name}' has no root bone.";
                return false;
            }

            RemoveExistingChild(targetRoot.transform, target.ArmatureKey);

            meshObject = new GameObject(GetMeshObjectName(target.ArmatureKey));
            meshObject.hideFlags = HideFlags.HideAndDontSave;
            meshObject.transform.SetParent(targetRoot.transform, false);
            meshObject.transform.localPosition = Vector3.zero;
            meshObject.transform.localRotation = Quaternion.identity;
            meshObject.transform.localScale = Vector3.one;

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var boneWeights = new List<BoneWeight>();
            var boneIndexMap = new Dictionary<Transform, int>();

            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != null && !boneIndexMap.ContainsKey(bones[i]))
                {
                    boneIndexMap.Add(bones[i], i);
                }
            }

            foreach (var bone in bones)
            {
                if (bone == null)
                {
                    continue;
                }

                bool hasDisplayedChild = false;
                foreach (Transform child in bone)
                {
                    if (child != null && boneIndexMap.ContainsKey(child))
                    {
                        hasDisplayedChild = true;
                        GenerateBoneGeometry(
                            bone,
                            child,
                            meshType,
                            baseThickness,
                            vertices,
                            triangles,
                            boneWeights,
                            boneIndexMap);
                    }
                }

                if (!hasDisplayedChild && TryGetLeafTailPosition(bone, boneIndexMap, out var leafTail))
                {
                    GenerateBoneGeometry(
                        bone,
                        leafTail,
                        meshType,
                        baseThickness,
                        vertices,
                        triangles,
                        boneWeights,
                        boneIndexMap);
                }
            }

            if (vertices.Count == 0)
            {
                Object.DestroyImmediate(meshObject);
                meshObject = null;
                message = $"No connected bone segments were found on '{renderer.name}'.";
                return false;
            }

            var mesh = new Mesh { name = "XRayArmatureMesh" };
            mesh.hideFlags = HideFlags.HideAndDontSave;

            var localVertices = new Vector3[vertices.Count];
            for (int i = 0; i < vertices.Count; i++)
            {
                localVertices[i] = meshObject.transform.InverseTransformPoint(vertices[i]);
            }

            mesh.vertices = localVertices;
            mesh.triangles = triangles.ToArray();
            mesh.boneWeights = boneWeights.ToArray();

            var bindPoses = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                bindPoses[i] = bones[i] != null
                    ? bones[i].worldToLocalMatrix * meshObject.transform.localToWorldMatrix
                    : Matrix4x4.identity;
            }

            mesh.bindposes = bindPoses;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var smr = meshObject.AddComponent<SkinnedMeshRenderer>();
            smr.hideFlags = HideFlags.HideAndDontSave;
            smr.sharedMesh = mesh;
            smr.bones = bones;
            smr.rootBone = rootBone;
            smr.updateWhenOffscreen = true;
            smr.sharedMaterial = material;

            message = $"Showing '{renderer.name}' bones on '{targetRoot.name}'.";
            return true;
        }

        public static string GetMeshObjectName(int armatureKey)
        {
            return MeshObjectNamePrefix + "_" + armatureKey;
        }

        public static bool IsPackageGizmoObject(GameObject go)
        {
            return go != null && go.name.StartsWith(GizmoObjectNamePrefix, System.StringComparison.Ordinal);
        }

        public static bool IsArmatureGizmoObject(GameObject go)
        {
            return go != null && go.name.StartsWith(MeshObjectNamePrefix, System.StringComparison.Ordinal);
        }

        public static SkinnedMeshRenderer FindBestRenderer(GameObject targetRoot)
        {
            if (targetRoot == null)
            {
                return null;
            }

            var renderers = targetRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers == null || renderers.Length == 0)
            {
                return null;
            }

            var armature = targetRoot.transform.Find("Armature");
            if (armature != null)
            {
                SkinnedMeshRenderer bestArmatureRenderer = null;
                int bestArmatureBoneCount = -1;
                foreach (var renderer in renderers)
                {
                    if (!IsUsableRenderer(renderer) || !IsChildOf(renderer.rootBone, armature))
                    {
                        continue;
                    }

                    int count = renderer.bones != null ? renderer.bones.Length : 0;
                    if (count > bestArmatureBoneCount)
                    {
                        bestArmatureBoneCount = count;
                        bestArmatureRenderer = renderer;
                    }
                }

                if (bestArmatureRenderer != null)
                {
                    return bestArmatureRenderer;
                }
            }

            SkinnedMeshRenderer best = null;
            int bestScore = -1;
            foreach (var renderer in renderers)
            {
                if (!IsUsableRenderer(renderer))
                {
                    continue;
                }

                int boneCount = renderer.bones != null ? renderer.bones.Length : 0;
                int vertexCount = renderer.sharedMesh != null ? renderer.sharedMesh.vertexCount : 0;
                int score = boneCount * 1000000 + vertexCount;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = renderer;
                }
            }

            return best;
        }

        public static bool IsUsableRenderer(SkinnedMeshRenderer renderer)
        {
            return renderer != null &&
                   !IsPackageGizmoObject(renderer.gameObject) &&
                   renderer.rootBone != null &&
                   renderer.bones != null &&
                   renderer.bones.Length > 0;
        }

        private static void RemoveExistingChild(Transform targetRoot, int armatureKey)
        {
            if (targetRoot == null)
            {
                return;
            }

            var existing = targetRoot.Find(GetMeshObjectName(armatureKey));
            if (existing != null)
            {
                Object.DestroyImmediate(existing.gameObject);
            }
        }

        private static bool IsChildOf(Transform child, Transform parent)
        {
            var current = child;
            while (current != null)
            {
                if (current == parent)
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private static void GenerateBoneGeometry(
            Transform boneHead,
            Transform boneTail,
            XRayArmatureMeshType meshType,
            float baseThickness,
            List<Vector3> vertices,
            List<int> triangles,
            List<BoneWeight> boneWeights,
            IReadOnlyDictionary<Transform, int> boneIndexMap)
        {
            if (boneTail == null)
            {
                return;
            }

            GenerateBoneGeometry(
                boneHead,
                boneTail.position,
                meshType,
                baseThickness,
                vertices,
                triangles,
                boneWeights,
                boneIndexMap);
        }

        private static void GenerateBoneGeometry(
            Transform boneHead,
            Vector3 tailPos,
            XRayArmatureMeshType meshType,
            float baseThickness,
            List<Vector3> vertices,
            List<int> triangles,
            List<BoneWeight> boneWeights,
            IReadOnlyDictionary<Transform, int> boneIndexMap)
        {
            int firstVertex = vertices.Count;

            Vector3 headPos = boneHead.position;
            Vector3 boneVector = tailPos - headPos;
            if (boneVector.magnitude < 0.001f)
            {
                return;
            }

            float boneLength = boneVector.magnitude;
            float scaledThickness = baseThickness * Mathf.Clamp(boneLength * 2.0f, 0.1f, 2.0f);
            Vector3 boneDirection = boneVector.normalized;
            Vector3 up = boneHead.up;
            if (Mathf.Abs(Vector3.Dot(up, boneDirection)) > 0.99f)
            {
                up = boneHead.forward;
            }

            Vector3 xAxis = Vector3.Cross(up, boneDirection).normalized;
            if (xAxis.sqrMagnitude < 0.0001f)
            {
                xAxis = Vector3.Cross(Vector3.up, boneDirection).normalized;
            }

            Vector3 zAxis = Vector3.Cross(boneDirection, xAxis).normalized;

            Vector3 x1;
            Vector3 z1;
            Vector3 x2;
            Vector3 z2;

            switch (meshType)
            {
                case XRayArmatureMeshType.Tapered:
                    x1 = xAxis * scaledThickness;
                    z1 = zAxis * scaledThickness;
                    x2 = xAxis * scaledThickness * 0.5f;
                    z2 = zAxis * scaledThickness * 0.5f;
                    break;
                case XRayArmatureMeshType.Box:
                    x1 = xAxis * scaledThickness;
                    z1 = zAxis * scaledThickness;
                    x2 = x1;
                    z2 = z1;
                    break;
                default:
                    x1 = xAxis * scaledThickness;
                    z1 = zAxis * scaledThickness;
                    x2 = Vector3.zero;
                    z2 = Vector3.zero;
                    break;
            }

            vertices.AddRange(new[]
            {
                headPos - x1 + z1,
                headPos + x1 + z1,
                headPos - x1 - z1,
                headPos + x1 - z1,
                tailPos - x2 + z2,
                tailPos + x2 + z2,
                tailPos - x2 - z2,
                tailPos + x2 - z2
            });

            triangles.AddRange(new[]
            {
                firstVertex + 3, firstVertex + 1, firstVertex + 0,
                firstVertex + 3, firstVertex + 0, firstVertex + 2,
                firstVertex + 6, firstVertex + 4, firstVertex + 5,
                firstVertex + 6, firstVertex + 5, firstVertex + 7,
                firstVertex + 4, firstVertex + 0, firstVertex + 1,
                firstVertex + 4, firstVertex + 1, firstVertex + 5,
                firstVertex + 7, firstVertex + 3, firstVertex + 2,
                firstVertex + 7, firstVertex + 2, firstVertex + 6,
                firstVertex + 5, firstVertex + 1, firstVertex + 3,
                firstVertex + 5, firstVertex + 3, firstVertex + 7,
                firstVertex + 6, firstVertex + 2, firstVertex + 0,
                firstVertex + 6, firstVertex + 0, firstVertex + 4
            });

            BoneWeight weight;
            if (boneIndexMap.TryGetValue(boneHead, out int boneIndex))
            {
                weight = new BoneWeight { boneIndex0 = boneIndex, weight0 = 1.0f };
            }
            else
            {
                weight = new BoneWeight();
            }

            for (int i = 0; i < 8; i++)
            {
                boneWeights.Add(weight);
            }
        }

        private static bool TryGetLeafTailPosition(
            Transform bone,
            IReadOnlyDictionary<Transform, int> boneIndexMap,
            out Vector3 tailPosition)
        {
            tailPosition = default;
            if (bone == null)
            {
                return false;
            }

            Vector3 childDirection = Vector3.zero;
            float childLength = 0f;
            foreach (Transform child in bone)
            {
                if (child == null || boneIndexMap.ContainsKey(child))
                {
                    continue;
                }

                var delta = child.position - bone.position;
                if (delta.sqrMagnitude < 0.000001f)
                {
                    continue;
                }

                childDirection += delta.normalized;
                childLength = Mathf.Max(childLength, delta.magnitude);
            }

            if (childDirection.sqrMagnitude > 0.000001f)
            {
                tailPosition = bone.position + childDirection.normalized * Mathf.Clamp(childLength, 0.025f, 0.2f);
                return true;
            }

            if (bone.parent != null && boneIndexMap.ContainsKey(bone.parent))
            {
                var delta = bone.position - bone.parent.position;
                if (delta.sqrMagnitude > 0.000001f)
                {
                    tailPosition = bone.position + delta.normalized * Mathf.Clamp(delta.magnitude * 0.45f, 0.025f, 0.15f);
                    return true;
                }
            }

            var fallback = bone.up.sqrMagnitude > 0.000001f ? bone.up : Vector3.up;
            tailPosition = bone.position + fallback.normalized * 0.05f;
            return true;
        }
    }
}
