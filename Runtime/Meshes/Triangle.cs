using Unity.Mathematics;
using UnityEngine;

namespace Orbiters.Toolkit.Meshes
{
    /// <summary>
    /// Point and ray tests against one triangle, the only copy in the Toolkit: the BVH, mesh comparison and every tool that
    /// measures a surface use these. Plain math on <c>Unity.Mathematics</c>, so Burst code calls them too.
    /// </summary>
    public static class Triangle
    {
        /// <summary>
        /// The point of triangle abc nearest <paramref name="p"/>, with its barycentric weights. A collapsed triangle (two
        /// corners at one place, or all three on a line) is the nearest point of its edges: always finite for finite input.
        /// </summary>
        public static float3 ClosestPoint(float3 p, float3 a, float3 b, float3 c, out float3 bary)
        {
            // Ericson, Real-Time Collision Detection 5.1.5.
            float3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = math.dot(ab, ap), d2 = math.dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { bary = new float3(1, 0, 0); return a; }
            float3 bp = p - b;
            float d3 = math.dot(ab, bp), d4 = math.dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { bary = new float3(0, 1, 0); return b; }
            float vc = d1 * d4 - d3 * d2;
            // A triangle without area (two corners at one place, or all three about on a line): an edge test passed still
            // gives the right point (p's projection lies within that edge) unless the edge has no length, and the face
            // test below would divide by about nothing, so its edges decide there.
            if (vc <= 0f && d1 >= 0f && d3 <= 0f && d1 - d3 > 0f) { float v = d1 / (d1 - d3); bary = new float3(1 - v, v, 0); return a + v * ab; }
            float3 cp = p - c;
            float d5 = math.dot(ab, cp), d6 = math.dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { bary = new float3(0, 0, 1); return c; }
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f && d2 - d6 > 0f) { float w = d2 / (d2 - d6); bary = new float3(1 - w, 0, w); return a + w * ac; }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f && (d4 - d3) + (d5 - d6) > 0f) { float w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); bary = new float3(0, 1 - w, w); return b + w * (c - b); }
            if (!(math.lengthsq(math.cross(ab, ac)) > 1e-10f * math.lengthsq(ab) * math.lengthsq(ac))) return ClosestPointOnCollapsed(p, a, b, c, out bary);
            float denom = 1f / (va + vb + vc);
            float vv = vb * denom, ww = vc * denom;
            bary = new float3(1 - vv - ww, vv, ww);
            return a + ab * vv + ac * ww;
        }

        /// <inheritdoc cref="ClosestPoint(float3, float3, float3, float3, out float3)"/>
        public static Vector3 ClosestPoint(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 bary)
        {
            var q = ClosestPoint((float3)p, a, b, c, out float3 weights);
            bary = weights;
            return q;
        }

        // A triangle without area: the nearest of its three edges' nearest points (a point when all corners meet).
        private static float3 ClosestPointOnCollapsed(float3 p, float3 a, float3 b, float3 c, out float3 bary)
        {
            float tab = Along(p, a, b), tbc = Along(p, b, c), tca = Along(p, c, a);
            float3 qab = a + (b - a) * tab, qbc = b + (c - b) * tbc, qca = c + (a - c) * tca;
            float dab = math.lengthsq(qab - p), dbc = math.lengthsq(qbc - p), dca = math.lengthsq(qca - p);
            if (dab <= dbc && dab <= dca) { bary = new float3(1 - tab, tab, 0); return qab; }
            if (dbc <= dca) { bary = new float3(0, 1 - tbc, tbc); return qbc; }
            bary = new float3(tca, 0, 1 - tca);
            return qca;
        }

        // Where along segment from-to the point nearest p lies (0..1; 0 for a segment of no length).
        private static float Along(float3 p, float3 from, float3 to)
        {
            float3 d = to - from;
            float length2 = math.lengthsq(d);
            if (length2 <= 0f) return 0f;
            float t = math.dot(p - from, d) / length2;
            return t < 0f ? 0f : t > 1f ? 1f : t;
        }

        /// <summary>
        /// Where a ray (unit <paramref name="direction"/>) crosses triangle abc in front of its origin (Moller-Trumbore).
        /// Without <paramref name="backfaces"/> only a triangle facing the ray counts (wound the other way round when
        /// <paramref name="mirrored"/>).
        /// </summary>
        public static bool Raycast(float3 origin, float3 direction, float3 a, float3 b, float3 c, bool backfaces, bool mirrored, out float distance, out float3 bary) =>
            Raycast(origin, direction, a, b, c, backfaces, mirrored, false, out distance, out bary);

        /// <summary>
        /// <see cref="Raycast(float3, float3, float3, float3, float3, bool, bool, out float, out float3)"/>; with
        /// <paramref name="contact"/> a ray starting on the triangle meets it at distance 0 (otherwise only ahead of the origin).
        /// </summary>
        public static bool Raycast(float3 origin, float3 direction, float3 a, float3 b, float3 c, bool backfaces, bool mirrored, bool contact,
            out float distance, out float3 bary)
        {
            float3 e1 = b - a, e2 = c - a;
            float3 p = math.cross(direction, e2);
            float det = math.dot(e1, p);
            distance = 0f; bary = float3.zero;
            if (backfaces ? math.abs(det) < 1e-12f : (mirrored ? -det : det) < 1e-12f) return false;
            float invDet = 1f / det;
            float3 s = origin - a;
            float u = math.dot(s, p) * invDet;
            if (u < 0f || u > 1f) return false;
            float3 q = math.cross(s, e1);
            float v = math.dot(direction, q) * invDet;
            if (v < 0f || u + v > 1f) return false;
            float d = math.dot(e2, q) * invDet;
            if (contact ? d < 0f : d <= 0f) return false;
            distance = d; bary = new float3(1f - u - v, u, v);
            return true;
        }
    }
}
