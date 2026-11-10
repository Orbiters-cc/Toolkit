using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Meshes;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Tests
{
    /// <summary>
    /// TKG-1, RFT-5: what ReFit's own hierarchy had, now in MeshBvh (rule queries run by Burst, seeds, classes, clones refitted
    /// along what moved, the rounding slack behind the tie rule, dropped leases), each against testing every triangle. Every
    /// mesh holds each triangle twice (the copy has the same corners, so the two tie exactly in any arithmetic) and the
    /// layouts differ, so a wrong tie rule or a pruned tie shows as the copy's index.
    /// </summary>
    public sealed class MeshBvhRuleTests
    {
        // A bumpy sphere with every triangle twice: triangle t + count is t again.
        private static void DoubledSphere(int rings, int segments, int seed, out Vector3[] vertices, out int[] triangles)
        {
            var random = new System.Random(seed);
            var v = new List<Vector3>();
            var t = new List<int>();
            for (int r = 0; r <= rings; r++)
                for (int s = 0; s <= segments; s++)
                {
                    float phi = Mathf.PI * r / rings, theta = 2f * Mathf.PI * s / segments;
                    float radius = 1f + (float)(random.NextDouble() - .5) * .3f;
                    v.Add(radius * new Vector3(Mathf.Sin(phi) * Mathf.Cos(theta), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(theta)));
                }
            int w = segments + 1;
            for (int r = 0; r < rings; r++)
                for (int s = 0; s < segments; s++)
                {
                    int a = r * w + s, b = a + 1, c = a + w, d = c + 1;
                    t.AddRange(new[] { a, c, b, b, c, d });
                }
            vertices = v.ToArray();
            triangles = t.Concat(t).ToArray();
        }

        private static Vector3[] FaceNormals(Vector3[] vertices, int[] triangles)
        {
            var normals = new Vector3[triangles.Length / 3];
            for (int t = 0; t < normals.Length; t++)
            {
                var a = vertices[triangles[t * 3]];
                normals[t] = Vector3.Cross(vertices[triangles[t * 3 + 1]] - a, vertices[triangles[t * 3 + 2]] - a).normalized;
            }
            return normals;
        }

        private static float Distance(Vector3[] vertices, int[] triangles, int t, Vector3 p) =>
            (Triangle.ClosestPoint(p, vertices[triangles[t * 3]], vertices[triangles[t * 3 + 1]], vertices[triangles[t * 3 + 2]], out _) - p).magnitude;

        private static Vector3 RandomPoint(System.Random random, float size) =>
            new Vector3((float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1) * size;

        // The answer by testing every triangle: the least distance among the accepted ones within range, then the lowest
        // index among those within rounding of it (the reference rounds in managed code, the tree in Burst).
        private static void AssertBruteForce(MeshBvh.Hit hit, Vector3[] vertices, int[] triangles, Vector3 p, float range, Func<int, bool> accepted, string what)
        {
            int count = triangles.Length / 3;
            float best = float.PositiveInfinity;
            for (int t = 0; t < count; t++) if (accepted(t)) best = Mathf.Min(best, Distance(vertices, triangles, t, p));
            if (!(best < range * (1 - 1e-5f)))
            {
                if (best > range * (1 + 1e-5f)) Assert.That(hit.Found, Is.False, what + ": nothing in range");
                return;
            }
            Assert.That(hit.Found, Is.True, what + ": found");
            Assert.That(accepted(hit.Triangle), Is.True, what + ": the triangle is accepted");
            Assert.That(hit.Distance, Is.EqualTo(best).Within(1e-5f), what + ": the least distance");
            // The copy ties exactly with its original: the lower index always wins.
            int half = count / 2;
            if (hit.Triangle >= half) Assert.That(accepted(hit.Triangle - half), Is.False, what + ": the copy won a tie against its original");
        }

        private static TriangleRule MixedRule(Vector3[] faceNormals, int[] masks, int[] labels, bool[] acceptedLabels, bool[] allowed, int[] ranks,
            int[] triangles, int[] vertexLabels, Vector3 direction, int which)
        {
            switch (which)
            {
                case 0: return TriangleRule.All.Facing(faceNormals, direction, .2f);
                case 1: return TriangleRule.All.Facing(faceNormals, direction, 0f, FacingTest.Above).InMasks(masks, 2);
                case 2: return TriangleRule.All.Facing(faceNormals, direction, .5f, FacingTest.EitherAtLeast).Labelled(labels, acceptedLabels, 3);
                case 3: return TriangleRule.All.Among(allowed).Ranked(ranks, 4);
                case 4: return TriangleRule.All.AwayFrom(triangles, vertexLabels, 1, 5).Except(7);
                default: return TriangleRule.All.Labelled(labels, null, 2).AlsoLabelled(masks, acceptedLabels);
            }
        }

        private static Func<int, bool> Reference(Vector3[] faceNormals, int[] masks, int[] labels, bool[] acceptedLabels, bool[] allowed, int[] ranks,
            int[] triangles, int[] vertexLabels, Vector3 direction, int which)
        {
            float Dot(int t) => faceNormals[t].x * direction.x + faceNormals[t].y * direction.y + faceNormals[t].z * direction.z;
            bool Label(int[] l, int t, bool[] table) => (uint)l[t] < (uint)table.Length && table[l[t]];
            switch (which)
            {
                case 0: return t => Dot(t) >= .2f;
                case 1: return t => Dot(t) > 0f && (masks[t] == 0 || (masks[t] & 2) != 0);
                case 2: return t => Mathf.Abs(Dot(t)) >= .5f && labels[t] != 3 && Label(labels, t, acceptedLabels);
                case 3: return t => allowed[t] && ranks[t] >= 4;
                case 4: return t => t != 7 && Enumerable.Range(0, 3).All(k => vertexLabels[triangles[t * 3 + k]] != 1 && vertexLabels[triangles[t * 3 + k]] != 5);
                default: return t => labels[t] != 2 && Label(masks, t, acceptedLabels);
            }
        }

        [Test]
        public void RuleQueriesMatchEveryTriangleTestedAndKeepTheTieRule()
        {
            DoubledSphere(14, 28, 3, out var vertices, out var triangles);
            int count = triangles.Length / 3;
            var random = new System.Random(9);
            var faceNormals = FaceNormals(vertices, triangles);
            // Per-triangle data the copy shares with its original (a tie must be decided by index, not by data).
            int half = count / 2;
            var masks = new int[count];
            var labels = new int[count];
            var allowed = new bool[count];
            var ranks = new int[count];
            for (int t = 0; t < half; t++)
            {
                masks[t] = masks[t + half] = random.Next(4);
                labels[t] = labels[t + half] = random.Next(6);
                allowed[t] = allowed[t + half] = random.Next(3) != 0;
                ranks[t] = ranks[t + half] = random.Next(8);
            }
            var acceptedLabels = new[] { true, false, true, true, false, true };
            var vertexLabels = Enumerable.Range(0, vertices.Length).Select(v => random.Next(7)).ToArray();
            var bvh = MeshBvh.Build(vertices, triangles);
            for (int i = 0; i < 300; i++)
            {
                var p = RandomPoint(random, 1.6f);
                var direction = RandomPoint(random, 1f).normalized;
                int which = i % 6;
                var rule = MixedRule(faceNormals, masks, labels, acceptedLabels, allowed, ranks, triangles, vertexLabels, direction, which);
                var accepted = Reference(faceNormals, masks, labels, acceptedLabels, allowed, ranks, triangles, vertexLabels, direction, which);
                float range = i % 3 == 0 ? .25f : 10f;
                var hit = bvh.ClosestPoint(p, range, rule);
                AssertBruteForce(hit, vertices, triangles, p, range, accepted, "point " + i + " rule " + which);
                Assert.That(hit.TriangleOrNone, Is.EqualTo(hit.Found ? hit.Triangle : -1));

                // The same rule along a ray: the nearest accepted crossing (clear of edges), the original before its copy.
                var origin = p * 2f;
                var ray = bvh.Raycast(origin, (-origin).normalized, 10f, true, rule);
                float nearest = float.PositiveInfinity;
                for (int t = 0; t < count; t++)
                    if (accepted(t) && Triangle.Raycast(origin, (-origin).normalized, vertices[triangles[t * 3]], vertices[triangles[t * 3 + 1]],
                            vertices[triangles[t * 3 + 2]], true, false, out float d, out var bary) && Mathf.Min(bary.x, Mathf.Min(bary.y, bary.z)) > 1e-3f)
                        nearest = Mathf.Min(nearest, d);
                if (!float.IsPositiveInfinity(nearest)) Assert.That(ray.Found && ray.Distance <= nearest + 1e-4f, Is.True, "ray " + i + ": a nearer accepted triangle");
                if (ray.Found)
                {
                    Assert.That(accepted(ray.Triangle), Is.True, "ray " + i + ": accepted");
                    if (ray.Triangle >= half) Assert.That(accepted(ray.Triangle - half), Is.False, "ray " + i + ": the copy won the tie");
                }
            }
        }

        // A subset tree (Build's include) and a rule over the whole tree are the same Burst walk over the same candidates.
        [Test]
        public void ASubsetTreeAndAnAllowedRuleGiveTheSameBits()
        {
            DoubledSphere(12, 24, 5, out var vertices, out var triangles);
            int count = triangles.Length / 3;
            var random = new System.Random(2);
            var include = Enumerable.Range(0, count).Select(t => random.Next(4) != 0).ToArray();
            var subset = MeshBvh.Build(vertices, triangles, include);
            var whole = MeshBvh.Build(vertices, triangles);
            var rule = TriangleRule.All.Among(include);
            for (int i = 0; i < 300; i++)
            {
                var p = RandomPoint(random, 1.5f);
                var a = subset.ClosestPoint(p, 10f);
                var b = whole.ClosestPoint(p, 10f, rule);
                Assert.That(b.Triangle, Is.EqualTo(a.Triangle), "point " + i);
                Assert.That(BitConverter.SingleToInt32Bits(b.Distance), Is.EqualTo(BitConverter.SingleToInt32Bits(a.Distance)), "point " + i);
                Assert.That(b.Position, Is.EqualTo(a.Position), "point " + i);
            }
        }

        // RFT-30: a seed is a candidate like any other: seeded answers are the unseeded ones, bit for bit, whatever the seed
        // (the answer, a neighbour, a triangle the rule rejects, one left out of the tree, out of range).
        [Test]
        public void SeededQueriesGiveTheUnseededAnswer()
        {
            DoubledSphere(16, 32, 7, out var vertices, out var triangles);
            int count = triangles.Length / 3;
            var include = Enumerable.Range(0, count).Select(t => t % 5 != 0).ToArray();
            var bvh = MeshBvh.Build(vertices, triangles, include);
            var faceNormals = FaceNormals(vertices, triangles);
            var random = new System.Random(4);
            for (int i = 0; i < 400; i++)
            {
                var p = RandomPoint(random, 1.4f);
                var rule = i % 2 == 0 ? TriangleRule.All : TriangleRule.All.Facing(faceNormals, RandomPoint(random, 1f).normalized, .1f);
                float range = i % 4 == 0 ? .2f : 5f;
                var plain = bvh.ClosestPoint(p, range, rule);
                int[] seeds = { plain.TriangleOrNone, random.Next(count), 0, count - 1, -1, count + 5, (plain.TriangleOrNone + 1) % count };
                foreach (int seed in seeds)
                {
                    var seeded = bvh.ClosestPoint(p, range, rule, seed);
                    Assert.That(seeded.Found, Is.EqualTo(plain.Found), "point " + i + " seed " + seed);
                    Assert.That(seeded.Triangle, Is.EqualTo(plain.Triangle), "point " + i + " seed " + seed);
                    Assert.That(BitConverter.SingleToInt32Bits(seeded.Distance), Is.EqualTo(BitConverter.SingleToInt32Bits(plain.Distance)), "point " + i + " seed " + seed);
                    Assert.That(seeded.Bary, Is.EqualTo(plain.Bary), "point " + i + " seed " + seed);
                }
                var unfiltered = bvh.ClosestPoint(p, range);
                var viaRule = bvh.ClosestPoint(p, range, TriangleRule.All);
                Assert.That(viaRule.Triangle, Is.EqualTo(unfiltered.Triangle), "point " + i + ": the empty rule is the unfiltered query");
                Assert.That(BitConverter.SingleToInt32Bits(viaRule.Distance), Is.EqualTo(BitConverter.SingleToInt32Bits(unfiltered.Distance)), "point " + i);
            }
        }

        // RFT-23: one traversal answers what one query per class answers, bit for bit.
        [Test]
        public void ClassesAnswerWhatOneQueryPerClassAnswers()
        {
            DoubledSphere(14, 28, 11, out var vertices, out var triangles);
            int count = triangles.Length / 3, half = count / 2;
            var faceNormals = FaceNormals(vertices, triangles);
            var random = new System.Random(6);
            var masks = new int[count];
            var regions = new int[count];
            for (int t = 0; t < half; t++)
            {
                masks[t] = masks[t + half] = 1 << random.Next(3);
                regions[t] = regions[t + half] = random.Next(5);
            }
            var compatible = new[] { true, false, true, false, true };
            var bvh = MeshBvh.Build(vertices, triangles);
            var hits = new MeshBvh.Hit[4];
            for (int i = 0; i < 300; i++)
            {
                var p = RandomPoint(random, 1.5f);
                var direction = RandomPoint(random, 1f).normalized;
                var rule = TriangleRule.All.Facing(faceNormals, direction, .3f).InMasks(masks, 1 << (i % 3))
                    .AlsoLabelled(regions, compatible);
                var classes = i % 2 == 0
                    ? new TriangleClasses(rule, TriangleTests.None, TriangleTests.Masks, TriangleTests.Masks | TriangleTests.Facing)
                    : new TriangleClasses(rule, TriangleTests.Facing | TriangleTests.MoreLabels, TriangleTests.MoreLabels, TriangleTests.Facing,
                        TriangleTests.Facing | TriangleTests.Masks | TriangleTests.MoreLabels);
                float range = i % 3 == 0 ? .3f : 8f;
                bvh.ClosestPointsByClass(p, range, classes, hits);
                for (int c = 0; c < classes.Count; c++)
                {
                    var needs = classes.Needs(c);
                    var single = needs == TriangleTests.None ? TriangleRule.All : Only(direction, needs, faceNormals, masks, regions, compatible, i);
                    var alone = bvh.ClosestPoint(p, range, single);
                    Assert.That(hits[c].Found, Is.EqualTo(alone.Found), "point " + i + " class " + c);
                    Assert.That(hits[c].Triangle, Is.EqualTo(alone.Triangle), "point " + i + " class " + c);
                    Assert.That(BitConverter.SingleToInt32Bits(hits[c].Distance), Is.EqualTo(BitConverter.SingleToInt32Bits(alone.Distance)), "point " + i + " class " + c);
                    if (hits[c].Found && hits[c].Triangle >= half)
                        Assert.That(alone.Triangle, Is.EqualTo(hits[c].Triangle), "point " + i + " class " + c + ": a copy wins only when its original is out");
                }
            }
            Assert.Throws<ArgumentException>(() => new TriangleClasses(TriangleRule.All, TriangleTests.Facing), "a class naming a test not set");
        }

        // The rule with only the tests a class names (the same direction and mask as the full rule).
        private static TriangleRule Only(Vector3 direction, TriangleTests needs, Vector3[] faceNormals, int[] masks, int[] regions, bool[] compatible, int i)
        {
            var rule = TriangleRule.All;
            if ((needs & TriangleTests.Facing) != 0) rule = rule.Facing(faceNormals, direction, .3f);
            if ((needs & TriangleTests.Masks) != 0) rule = rule.InMasks(masks, 1 << (i % 3));
            if ((needs & TriangleTests.MoreLabels) != 0) rule = rule.AlsoLabelled(regions, compatible);
            return rule;
        }

        // RFT-26: a copy refitted along the triangles a frame moved answers as a tree built on the moved surface, bit for bit;
        // the original keeps answering for the rest surface.
        [Test]
        public void AClonedTreeRefittedAlongWhatMovedAnswersAsABuild()
        {
            DoubledSphere(20, 40, 13, out var rest, out var triangles);
            int count = triangles.Length / 3;
            var include = Enumerable.Range(0, count).Select(t => t % 7 != 3).ToArray();
            var original = MeshBvh.Build(rest, triangles, include);
            var restBuilt = MeshBvh.Build(rest, triangles, include);
            var random = new System.Random(8);
            for (int frame = 0; frame < 6; frame++)
            {
                // A patch of the surface pushed out (a shape frame), the rest exactly where it was.
                var center = RandomPoint(random, 1f).normalized;
                var moved = (Vector3[])rest.Clone();
                var deltas = new Vector3[rest.Length];
                for (int v = 0; v < rest.Length; v++)
                    if ((rest[v] - center).magnitude < .4f) { deltas[v] = center * (.05f + .2f * frame); moved[v] += deltas[v]; }
                var movedTriangles = new List<int>();
                for (int t = 0; t < count; t++)
                    if (Enumerable.Range(0, 3).Any(k => deltas[triangles[t * 3 + k]] != Vector3.zero)) movedTriangles.Add(t);
                var copy = original.Clone(moved);
                copy.RefitTriangles(movedTriangles);
                var built = MeshBvh.Build(moved, triangles, include);
                for (int i = 0; i < 80; i++)
                {
                    var p = RandomPoint(random, 1.6f);
                    var a = copy.ClosestPoint(p, 10f);
                    var b = built.ClosestPoint(p, 10f);
                    Assert.That(a.Triangle, Is.EqualTo(b.Triangle), "frame " + frame + " point " + i);
                    Assert.That(BitConverter.SingleToInt32Bits(a.Distance), Is.EqualTo(BitConverter.SingleToInt32Bits(b.Distance)), "frame " + frame + " point " + i);
                    var ra = copy.Raycast(p * 2f, -p.normalized, 10f);
                    var rb = built.Raycast(p * 2f, -p.normalized, 10f);
                    Assert.That(ra.Found, Is.EqualTo(rb.Found), "frame " + frame + " ray " + i);
                    if (ra.Found) Assert.That(ra.Triangle, Is.EqualTo(rb.Triangle), "frame " + frame + " ray " + i);
                    var r = original.ClosestPoint(p, 10f);
                    if (i == 0) Assert.That(r.Triangle, Is.EqualTo(restBuilt.ClosestPoint(p, 10f).Triangle), "the original still answers for the rest surface");
                }
            }
            var copyWhole = original.Clone((Vector3[])rest.Clone());
            Assert.Throws<ArgumentException>(() => original.Clone(new Vector3[3]), "one position per vertex");
            copyWhole.Refit(rest);
        }

        // The slack keeps the tie rule where the computed distances of two coincident triangles equal a box face exactly:
        // a grid of unit squares, each twice, points straight above corners and edges (many triangles at one distance).
        [Test]
        public void TiesOnBoxFacesGoToTheLowestIndex()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (int y = 0; y <= 8; y++) for (int x = 0; x <= 8; x++) vertices.Add(new Vector3(x * .37f, y * .37f, 3f));
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int a = y * 9 + x;
                    triangles.AddRange(new[] { a, a + 9, a + 1, a + 1, a + 9, a + 10 });
                }
            int count = triangles.Count / 3;
            var forward = triangles.Concat(triangles).ToArray();
            var reversed = new int[forward.Length];
            for (int t = 0; t < 2 * count; t++) for (int k = 0; k < 3; k++) reversed[t * 3 + k] = forward[(2 * count - 1 - t) * 3 + k];
            var v = vertices.ToArray();
            foreach (var tris in new[] { forward, reversed })
            {
                var bvh = MeshBvh.Build(v, tris);
                for (int x = 0; x <= 16; x++)
                    for (int y = 0; y <= 16; y++)
                    {
                        var p = new Vector3(x * .185f, y * .185f, 3.5f);
                        var hit = bvh.ClosestPoint(p, 10f);
                        float best = float.PositiveInfinity;
                        for (int t = 0; t < 2 * count; t++) best = Mathf.Min(best, Distance(v, tris, t, p));
                        Assert.That(hit.Distance, Is.EqualTo(best).Within(1e-5f), "point " + p.ToString("F3"));
                        // Each pair of coincident triangles is (t, t + count) in either order: the lower always wins.
                        Assert.That(hit.Triangle, Is.LessThan(count), "point " + p.ToString("F3") + (tris == reversed ? " (reversed)" : ""));
                    }
            }
        }

        // TKG-21 re-check: a lease dropped without Dispose is released once collected; refits work again.
        [Test]
        public void ADroppedLeaseIsReleasedOnceCollected()
        {
            DoubledSphere(6, 12, 1, out var vertices, out var triangles);
            var bvh = MeshBvh.Build(vertices, triangles);
            DropALease(bvh);
            // Not collected yet (a collection between the two lines would only make the next assertion pass early).
            Assert.Throws<InvalidOperationException>(() => bvh.Refit(), "an undisposed lease refuses refits");
            for (int i = 0; i < 4; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.DoesNotThrow(() => bvh.Refit(), "the dropped lease was released");
            using (var lease = bvh.Pin()) Assert.That(lease.View.TriangleCount, Is.EqualTo(triangles.Length / 3));
            Assert.DoesNotThrow(() => bvh.Refit(vertices));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void DropALease(MeshBvh bvh) => bvh.Pin();

        // Labels outside the table (a triangle of no particular kind, -1) pass only when the rule says so, in either label test.
        [Test]
        public void LabelsOutsideTheTableFollowTheRule()
        {
            DoubledSphere(10, 20, 17, out var vertices, out var triangles);
            int count = triangles.Length / 3, half = count / 2;
            var random = new System.Random(3);
            var labels = new int[count];
            for (int t = 0; t < half; t++) labels[t] = labels[t + half] = random.Next(-1, 4);
            var table = new[] { false, true, true };
            var bvh = MeshBvh.Build(vertices, triangles);
            for (int i = 0; i < 200; i++)
            {
                var p = RandomPoint(random, 1.5f);
                bool outside = i % 2 == 0;
                Func<int, bool> accepted = t => (uint)labels[t] < (uint)table.Length ? table[labels[t]] : outside;
                var first = bvh.ClosestPoint(p, 10f, TriangleRule.All.Labelled(labels, table, outsideAccepted: outside));
                var second = bvh.ClosestPoint(p, 10f, TriangleRule.All.AlsoLabelled(labels, table, outside));
                AssertBruteForce(first, vertices, triangles, p, 10f, accepted, "point " + i);
                Assert.That(second.Triangle, Is.EqualTo(first.Triangle), "point " + i + ": both label tests agree");
            }
        }

        [Test]
        public void RulesRefuseArraysThatDoNotCoverTheMesh()
        {
            DoubledSphere(4, 8, 1, out var vertices, out var triangles);
            var bvh = MeshBvh.Build(vertices, triangles);
            Assert.Throws<ArgumentException>(() => bvh.ClosestPoint(Vector3.zero, 1f, TriangleRule.All.Among(new bool[3])));
            Assert.Throws<ArgumentException>(() => bvh.Raycast(Vector3.zero, Vector3.up, 1f, true, TriangleRule.All.Facing(new Vector3[2], Vector3.up, 0f)));
        }
    }
}
