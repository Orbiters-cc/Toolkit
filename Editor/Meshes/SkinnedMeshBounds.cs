using UnityEngine;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>Compute culling bounds from posed geometry in the renderer's root-bone space.</summary>
    public static class SkinnedMeshBounds
    {
        public static void Refresh(SkinnedMeshRenderer renderer)
        {
            if (renderer == null || renderer.sharedMesh == null) return;
            var baked = new Mesh();
            try
            {
                renderer.BakeMesh(baked);
                Refresh(renderer, baked.vertices);
            }
            finally { Object.DestroyImmediate(baked); }
        }

        /// <summary>
        /// Maps vertices from <see cref="SkinnedMeshRenderer.BakeMesh(Mesh)"/> to world space. The bake leaves out the
        /// renderer's own scale, so only its position and rotation apply; with the full matrix a scaled mesh (hair or
        /// accessories imported at another scale) would be measured many times too large or too small.
        /// </summary>
        public static Matrix4x4 BakedToWorld(SkinnedMeshRenderer renderer) =>
            Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one);

        /// <summary>Same, from vertices the caller already baked with <see cref="SkinnedMeshRenderer.BakeMesh(Mesh)"/>.</summary>
        public static void Refresh(SkinnedMeshRenderer renderer, Vector3[] bakedVertices)
        {
            if (renderer == null || bakedVertices == null || bakedVertices.Length == 0) return;
            var boundsRoot = renderer.rootBone != null ? renderer.rootBone : renderer.transform;
            var matrix = boundsRoot.worldToLocalMatrix * BakedToWorld(renderer);
            var bounds = new Bounds(matrix.MultiplyPoint3x4(bakedVertices[0]), Vector3.zero);
            for (int i = 1; i < bakedVertices.Length; i++) bounds.Encapsulate(matrix.MultiplyPoint3x4(bakedVertices[i]));
            // Keep a small margin for floating-point error at frustum edges.
            bounds.Expand(Mathf.Max(0.002f, bounds.size.magnitude * 0.02f));
            renderer.localBounds = bounds;
        }
    }
}
