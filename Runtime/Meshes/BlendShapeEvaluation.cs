using UnityEngine;

namespace Orbiters.Toolkit.Meshes
{
    /// <summary>
    /// Blendshapes applied to mesh data the way Unity's skinning applies them, so tools measure the shape the user sees.
    /// Checked against SkinnedMeshRenderer.BakeMesh, including negative and zero-weight frames.
    /// </summary>
    public static class BlendShapeEvaluation
    {
        /// <summary>Whether Unity clamps blendshape weights to their frames (Player Settings, "Clamp BlendShapes").</summary>
        public static bool ClampWeights =>
#if UNITY_EDITOR
            UnityEditor.PlayerSettings.legacyClampBlendShapeWeights;
#else
            true;
#endif

        /// <summary>Frame buffers reused across shapes of one mesh.</summary>
        public sealed class Scratch
        {
            internal readonly Vector3[] VerticesA, NormalsA, VerticesB, NormalsB;

            public Scratch(int vertexCount)
            {
                VerticesA = new Vector3[vertexCount];
                NormalsA = new Vector3[vertexCount];
                VerticesB = new Vector3[vertexCount];
                NormalsB = new Vector3[vertexCount];
            }
        }

        /// <summary>
        /// Adds <paramref name="shape"/> at renderer weight <paramref name="weight"/> (100 = full) to
        /// <paramref name="vertices"/> and, when given, <paramref name="normals"/>.
        /// </summary>
        public static void Add(Mesh mesh, int shape, float weight, bool clamp, Vector3[] vertices, Vector3[] normals, Scratch scratch)
        {
            int frameCount = mesh.GetBlendShapeFrameCount(shape);
            if (frameCount == 0) return;
            if (clamp)
            {
                float lastWeight = mesh.GetBlendShapeFrameWeight(shape, frameCount - 1);
                if (frameCount == 1 && lastWeight < 0f) return;
                if (frameCount == 1 && lastWeight == 0f)
                    // Native clamped skinning treats a lone zero-weight frame as a step at positive weights.
                    weight = weight > 0f ? 100f : 0f;
                else
                    // Native skinning applies the lower bound first, then the final frame's upper bound, even
                    // when all frames of a multi-frame shape are negative. Mathf.Clamp differs in that case.
                    weight = Mathf.Min(Mathf.Max(weight, 0f), lastWeight);
            }
            // Zero can interpolate nonzero deltas when multiple frames start below zero.
            if (weight == 0f && (frameCount <= 1 || mesh.GetBlendShapeFrameWeight(shape, 0) >= 0f)) return;

            if (frameCount == 1)
            {
                AddScaled(mesh, shape, 0, weight, mesh.GetBlendShapeFrameWeight(shape, 0), vertices, normals, scratch);
                return;
            }
            int upper = 0;
            while (upper < frameCount && mesh.GetBlendShapeFrameWeight(shape, upper) < weight) upper++;
            if (upper == 0)
            {
                AddScaled(mesh, shape, 0, weight, mesh.GetBlendShapeFrameWeight(shape, 0), vertices, normals, scratch);
                return;
            }
            if (upper == frameCount)
            {
                // Unity drops the previous frame's contribution above the last frame, but keeps scaling the last delta
                // over that final frame interval.
                int last = frameCount - 1;
                float previous = mesh.GetBlendShapeFrameWeight(shape, last - 1);
                AddScaled(mesh, shape, last, weight - previous, mesh.GetBlendShapeFrameWeight(shape, last) - previous, vertices, normals, scratch);
                return;
            }
            float lowerWeight = mesh.GetBlendShapeFrameWeight(shape, upper - 1), upperWeight = mesh.GetBlendShapeFrameWeight(shape, upper);
            float t = Mathf.Approximately(lowerWeight, upperWeight) ? 0f : Mathf.InverseLerp(lowerWeight, upperWeight, weight);
            mesh.GetBlendShapeFrameVertices(shape, upper - 1, scratch.VerticesA, normals != null ? scratch.NormalsA : null, null);
            mesh.GetBlendShapeFrameVertices(shape, upper, scratch.VerticesB, normals != null ? scratch.NormalsB : null, null);
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] += Vector3.Lerp(scratch.VerticesA[i], scratch.VerticesB[i], t);
                if (normals != null) normals[i] += Vector3.Lerp(scratch.NormalsA[i], scratch.NormalsB[i], t);
            }
        }

        private static void AddScaled(Mesh mesh, int shape, int frame, float weight, float frameWeight, Vector3[] vertices, Vector3[] normals, Scratch scratch)
        {
            float scale = Mathf.Approximately(frameWeight, 0f) ? weight / 100f : weight / frameWeight;
            mesh.GetBlendShapeFrameVertices(shape, frame, scratch.VerticesA, normals != null ? scratch.NormalsA : null, null);
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] += scratch.VerticesA[i] * scale;
                if (normals != null) normals[i] += scratch.NormalsA[i] * scale;
            }
        }
    }
}
