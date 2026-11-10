using System;
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
        /// What a shape at a renderer weight adds: frame <see cref="Lower"/> times <see cref="Scale"/> when
        /// <see cref="Upper"/> is -1, otherwise <c>Vector3.Lerp(frame Lower, frame Upper, T)</c>.
        /// </summary>
        public struct FrameBlend
        {
            public int Lower, Upper;
            public float Scale, T;
        }

        /// <summary>
        /// Unity's rule for which frames a shape at renderer weight <paramref name="weight"/> (100 = full) blends, from its
        /// frame weights. False when it adds nothing. The one place the rule lives: <see cref="Add"/> and
        /// <see cref="BlendShapeTable.AddTo"/> both use it.
        /// </summary>
        public static bool Resolve(ReadOnlySpan<float> frameWeights, float weight, bool clamp, out FrameBlend blend)
        {
            blend = new FrameBlend { Lower = -1, Upper = -1 };
            int frameCount = frameWeights.Length;
            if (frameCount == 0) return false;
            if (clamp)
            {
                float lastWeight = frameWeights[frameCount - 1];
                if (frameCount == 1 && lastWeight < 0f) return false;
                if (frameCount == 1 && lastWeight == 0f)
                    // Native clamped skinning treats a lone zero-weight frame as a step at positive weights.
                    weight = weight > 0f ? 100f : 0f;
                else
                    // Native skinning applies the lower bound first, then the final frame's upper bound, even
                    // when all frames of a multi-frame shape are negative. Mathf.Clamp differs in that case.
                    weight = Mathf.Min(Mathf.Max(weight, 0f), lastWeight);
            }
            // Zero can interpolate nonzero deltas when multiple frames start below zero.
            if (weight == 0f && (frameCount <= 1 || frameWeights[0] >= 0f)) return false;

            if (frameCount == 1)
            {
                blend.Lower = 0;
                blend.Scale = ScaleOf(weight, frameWeights[0]);
                return true;
            }
            int upper = 0;
            while (upper < frameCount && frameWeights[upper] < weight) upper++;
            if (upper == 0)
            {
                blend.Lower = 0;
                blend.Scale = ScaleOf(weight, frameWeights[0]);
                return true;
            }
            if (upper == frameCount)
            {
                // Unity drops the previous frame's contribution above the last frame, but keeps scaling the last delta
                // over that final frame interval.
                int last = frameCount - 1;
                float previous = frameWeights[last - 1];
                blend.Lower = last;
                blend.Scale = ScaleOf(weight - previous, frameWeights[last] - previous);
                return true;
            }
            float lowerWeight = frameWeights[upper - 1], upperWeight = frameWeights[upper];
            blend.Lower = upper - 1;
            blend.Upper = upper;
            blend.T = Mathf.Approximately(lowerWeight, upperWeight) ? 0f : Mathf.InverseLerp(lowerWeight, upperWeight, weight);
            return true;
        }

        private static float ScaleOf(float weight, float frameWeight) => Mathf.Approximately(frameWeight, 0f) ? weight / 100f : weight / frameWeight;

        /// <summary>
        /// Adds <paramref name="shape"/> at renderer weight <paramref name="weight"/> (100 = full) to
        /// <paramref name="vertices"/> and, when given, <paramref name="normals"/>.
        /// </summary>
        public static void Add(Mesh mesh, int shape, float weight, bool clamp, Vector3[] vertices, Vector3[] normals, Scratch scratch)
        {
            int frameCount = mesh.GetBlendShapeFrameCount(shape);
            if (frameCount == 0) return;
            Span<float> frameWeights = frameCount <= 32 ? stackalloc float[frameCount] : new float[frameCount];
            for (int f = 0; f < frameCount; f++) frameWeights[f] = mesh.GetBlendShapeFrameWeight(shape, f);
            if (!Resolve(frameWeights, weight, clamp, out var blend)) return;

            if (blend.Upper < 0)
            {
                float scale = blend.Scale;
                mesh.GetBlendShapeFrameVertices(shape, blend.Lower, scratch.VerticesA, normals != null ? scratch.NormalsA : null, null);
                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i] += scratch.VerticesA[i] * scale;
                    if (normals != null) normals[i] += scratch.NormalsA[i] * scale;
                }
                return;
            }
            float t = blend.T;
            mesh.GetBlendShapeFrameVertices(shape, blend.Lower, scratch.VerticesA, normals != null ? scratch.NormalsA : null, null);
            mesh.GetBlendShapeFrameVertices(shape, blend.Upper, scratch.VerticesB, normals != null ? scratch.NormalsB : null, null);
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] += Vector3.Lerp(scratch.VerticesA[i], scratch.VerticesB[i], t);
                if (normals != null) normals[i] += Vector3.Lerp(scratch.NormalsA[i], scratch.NormalsB[i], t);
            }
        }
    }
}
