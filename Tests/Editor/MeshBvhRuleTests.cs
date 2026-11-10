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
            Assert.That(hit.Triangle, Is.EqualTo(ExactWinner(vertices, triangles, p, best, accepted)), what + ": the triangle the tie rule picks");
        }

        // The winner by the tie rule, exactly: the triangles within rounding of the least distance (managed arithmetic) are
        // measured again each in a tree of its own (the Burst arithmetic of the walk, one triangle, no traversal order), and the
        // lowest index among those at the least of those distances wins.
        private static int ExactWinner(Vector3[] vertices, int[] triangles, Vector3 p, float best, Func<int, bool> accepted)
        {
            int count = triangles.Length / 3, winner = -1;
            float least = float.PositiveInfinity;
            var only = new bool[count];
            for (int t = 0; t < count; t++)
            {
                if (!accepted(t) || Distance(vertices, triangles, t, p) > best + 1e-5f) continue;
                only[t] = true;
                float d = MeshBvh.Build(vertices, triangles, only).ClosestPoint(p, float.PositiveInfinity).Distance;
                only[t] = false;
                if (d < least) { least = d; winner = t; }
            }
            return winner;
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
                        // Each pair of coincident triangles is (t, t + count) in either order: the lower always wins, and among
                        // neighbours meeting at the nearest point the lowest index.
                        Assert.That(hit.Triangle, Is.LessThan(count), "point " + p.ToString("F3") + (tris == reversed ? " (reversed)" : ""));
                        Assert.That(hit.Triangle, Is.EqualTo(ExactWinner(v, tris, p, best, t => true)), "point " + p.ToString("F3") + (tris == reversed ? " (reversed)" : ""));
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

        // Review: an empty table is a table (it pins to a null pointer): it accepts no label in it, unlike no table at all, in
        // closest-point, ray and class queries alike.
        [Test]
        public void AnEmptyLabelTableIsNotAMissingOne()
        {
            DoubledSphere(8, 16, 2, out var vertices, out var triangles);
            int count = triangles.Length / 3;
            var labels = new int[count];
            for (int t = 0; t < count; t++) labels[t] = t % 3;
            var bvh = MeshBvh.Build(vertices, triangles);
            var p = new Vector3(.2f, 1.5f, .1f);
            var none = TriangleRule.All.Labelled(labels, null);
            var empty = TriangleRule.All.Labelled(labels, Array.Empty<bool>());
            var emptyOutside = TriangleRule.All.Labelled(labels, Array.Empty<bool>(), outsideAccepted: true);
            var second = TriangleRule.All.AlsoLabelled(labels, Array.Empty<bool>());
            Assert.That(bvh.ClosestPoint(p, 10f, none).Found, Is.True, "no table: any label");
            Assert.That(bvh.ClosestPoint(p, 10f, empty).Found, Is.False, "an empty table accepts no label");
            Assert.That(bvh.ClosestPoint(p, 10f, second).Found, Is.False, "the second test alike");
            Assert.That(bvh.ClosestPoint(p, 10f, emptyOutside).Triangle, Is.EqualTo(bvh.ClosestPoint(p, 10f).Triangle), "outside the table, when accepted");
            Assert.That(bvh.Raycast(p, -p.normalized, 10f, true, none).Found, Is.True);
            Assert.That(bvh.Raycast(p, -p.normalized, 10f, true, empty).Found, Is.False);
            Assert.That(bvh.Raycast(p, -p.normalized, 10f, true, second).Found, Is.False);
            var hits = new MeshBvh.Hit[2];
            bvh.ClosestPointsByClass(p, 10f, new TriangleClasses(empty, TriangleTests.None, TriangleTests.Labels), hits);
            Assert.That(hits[0].Found && !hits[1].Found, Is.True, "classes: the empty table's class finds nothing");
            bvh.ClosestPointsByClass(p, 10f, new TriangleClasses(none, TriangleTests.None, TriangleTests.Labels), hits);
            Assert.That(hits[1].Triangle, Is.EqualTo(hits[0].Triangle), "classes: no table is every label");
        }

        // Review: a ray from a point on a surface meets it at distance 0 only when the caller asks (ReFit); otherwise only hits
        // ahead count (Mixer). Two layers facing alike, 1 cm apart, the point on the first.
        [Test]
        public void RaysFromASurfaceMeetItOnlyWhenAsked()
        {
            var vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(0, 1, 0), new Vector3(-1, -1, .01f), new Vector3(1, -1, .01f), new Vector3(0, 1, .01f) };
            var triangles = new[] { 0, 1, 2, 3, 4, 5 };
            var bvh = MeshBvh.Build(vertices, triangles);
            var p = new Vector3(0, 0, 0);
            var contact = bvh.Raycast(p, Vector3.forward, 1f, true, TriangleRule.All, contact: true);
            Assert.That(contact.Found && contact.Triangle == 0 && contact.Distance == 0f, Is.True, "contact: the surface the point is on");
            var ahead = bvh.Raycast(p, Vector3.forward, 1f, true, TriangleRule.All);
            Assert.That(ahead.Found && ahead.Triangle == 1, Is.True, "otherwise the next layer");
            Assert.That(bvh.Raycast(p, Vector3.forward, 1f).Triangle, Is.EqualTo(1), "the unfiltered ray as before");
            Assert.That(bvh.Raycast(p, Vector3.back, 1f, true, TriangleRule.All, contact: true).Triangle, Is.EqualTo(0), "either way out of it");
        }

        [Test]
        public void AScopedPinRefusesRefitsUntilDisposed()
        {
            DoubledSphere(4, 8, 1, out var vertices, out var triangles);
            var bvh = MeshBvh.Build(vertices, triangles);
            using (var pin = bvh.PinScope())
            {
                Assert.That(pin.View.TriangleCount, Is.EqualTo(triangles.Length / 3));
                Assert.Throws<InvalidOperationException>(() => bvh.Refit());
            }
            Assert.DoesNotThrow(() => bvh.Refit());
            MeshBvh.ReleaseDroppedLeases(true);
        }

        // ---- Long slivers among tiny triangles, against a reference in double precision -------------------------------------
        // The tests above compare the tree with every triangle measured by the same Triangle routine, on well-shaped
        // triangles of one size: a routine that measures a sliver wrong agrees with itself, so they could not see that
        // Ericson's region weights lose their digits on a long thin triangle (a point inside a 1 m x 2 mm sliver came out
        // a millimetre away: MeshPartsSurfaceTests.SurfaceChecksFindLongSliversAmongTinyTriangles). These measure with an
        // independent double-precision reference, on slivers along each axis with tiny triangles around them.

        // Slivers along axis (long side 0.3 to 3 m, 1e-2 to 1e-4 of that wide, in the plane of axis and another), among
        // 1,000 tiny triangles (1 mm) in a band beside them, as a body's long seam strips are among its small faces.
        private static void SliverField(int axis, int seed, out Vector3[] vertices, out int[] triangles, out int slivers)
        {
            var random = new System.Random(seed);
            var v = new List<Vector3>();
            var t = new List<int>();
            slivers = 6;
            for (int k = 0; k < slivers; k++)
            {
                float length = .3f + (float)random.NextDouble() * 2.7f;
                float width = length * (float)Math.Pow(10, -2 - random.NextDouble() * 2);
                var along = Vector3.zero; along[axis] = length;
                var across = Vector3.zero; across[(axis + 1 + k % 2) % 3] = width;
                var origin = RandomPoint(random, .5f);
                int start = v.Count;
                v.AddRange(new[] { origin, origin + along, origin + along * (float)random.NextDouble() + across });
                t.AddRange(new[] { start, start + 1, start + 2 });
            }
            for (int i = 0; i < 1000; i++)
            {
                var step = Vector3.zero; step[axis] = i * .002f;
                var lift = Vector3.zero; lift[(axis + 1) % 3] = 1f;
                var p = step + lift + RandomPoint(random, .01f);
                var right = Vector3.zero; right[axis] = .001f;
                var up = Vector3.zero; up[(axis + 2) % 3] = .001f;
                int start = v.Count;
                v.AddRange(new[] { p, p + right, p + up });
                t.AddRange(new[] { start, start + 1, start + 2 });
            }
            vertices = v.ToArray();
            triangles = t.ToArray();
        }

        // The distance from p to triangle t in double precision: to the plane where the projection falls inside (barycentric
        // weights from cross products), else to the nearest edge.
        private static double ReferenceDistance(Vector3[] vertices, int[] triangles, int t, Vector3 point)
        {
            double[] P = { point.x, point.y, point.z };
            double[] Corner(int k) { var c = vertices[triangles[t * 3 + k]]; return new double[] { c.x, c.y, c.z }; }
            double[] Sub(double[] x, double[] y) => new[] { x[0] - y[0], x[1] - y[1], x[2] - y[2] };
            double Dot(double[] x, double[] y) => x[0] * y[0] + x[1] * y[1] + x[2] * y[2];
            double[] Cross(double[] x, double[] y) => new[] { x[1] * y[2] - x[2] * y[1], x[2] * y[0] - x[0] * y[2], x[0] * y[1] - x[1] * y[0] };
            double Segment(double[] a, double[] b)
            {
                var d = Sub(b, a);
                double l = Dot(d, d), s = l > 0 ? Math.Max(0, Math.Min(1, Dot(Sub(P, a), d) / l)) : 0;
                var q = new[] { a[0] + d[0] * s - P[0], a[1] + d[1] * s - P[1], a[2] + d[2] * s - P[2] };
                return Math.Sqrt(Dot(q, q));
            }
            double[] A = Corner(0), B = Corner(1), C = Corner(2);
            var n = Cross(Sub(B, A), Sub(C, A));
            double nn = Dot(n, n), best = Math.Min(Segment(A, B), Math.Min(Segment(B, C), Segment(C, A)));
            if (nn > 0)
            {
                double h = Dot(Sub(P, A), n) / nn;
                var q = new[] { P[0] - n[0] * h, P[1] - n[1] * h, P[2] - n[2] * h };
                if (Dot(n, Cross(Sub(B, q), Sub(C, q))) >= 0 && Dot(n, Cross(Sub(C, q), Sub(A, q))) >= 0 && Dot(n, Cross(Sub(A, q), Sub(B, q))) >= 0)
                    best = Math.Min(best, Math.Abs(h) * Math.Sqrt(nn));
            }
            return best;
        }

        // Points inside a sliver, beside its long edges (in its plane and off it) and far from everything.
        private static IEnumerable<(Vector3 Point, string What)> SliverQueries(Vector3[] vertices, int[] triangles, int slivers, System.Random random)
        {
            for (int k = 0; k < slivers; k++)
            {
                Vector3 a = vertices[triangles[k * 3]], b = vertices[triangles[k * 3 + 1]], c = vertices[triangles[k * 3 + 2]];
                var normal = Vector3.Cross(b - a, c - a).normalized;
                float width = Vector3.Cross(b - a, c - a).magnitude / (b - a).magnitude;
                for (int i = 0; i < 40; i++)
                {
                    // Clear of the corners and edges by 5% of the weights (the edges have their own queries below).
                    float s = .05f + .4f * (float)random.NextDouble(), u = .05f + .4f * (float)random.NextDouble();
                    var inside = a + (b - a) * s + (c - a) * u;
                    yield return (inside, "inside sliver " + k);
                    yield return (inside + normal * width * ((float)random.NextDouble() - .5f), "above sliver " + k);
                    // Beside the long edge ab, in the plane, outside the sliver.
                    var edge = a + (b - a) * (float)random.NextDouble();
                    var outward = Vector3.Cross(normal, b - a).normalized;
                    if (Vector3.Dot(outward, c - a) > 0) outward = -outward;
                    yield return (edge + outward * width * (float)random.NextDouble(), "beside sliver " + k);
                    yield return (RandomPoint(random, 3f), "far");
                }
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void LongSliversAmongTinyTrianglesMatchADoublePrecisionReference(int axis)
        {
            SliverField(axis, 11 + axis, out var vertices, out var triangles, out int slivers);
            int count = triangles.Length / 3;
            var bvh = MeshBvh.Build(vertices, triangles);
            var random = new System.Random(5 + axis);
            var near = new List<int>();
            foreach (var (p, what) in SliverQueries(vertices, triangles, slivers, random))
            {
                double best = double.MaxValue;
                var reference = new double[count];
                for (int t = 0; t < count; t++) best = Math.Min(best, reference[t] = ReferenceDistance(vertices, triangles, t, p));
                // Float rounding of corners up to about 3.5 m away: a few ulps of that.
                double tolerance = 2e-6 + best * 1e-5;

                var hit = bvh.ClosestPoint(p, 10f);
                Assert.That(hit.Found, Is.True, what);
                Assert.That(hit.Distance, Is.EqualTo(best).Within(tolerance), what + ": the least distance");
                Assert.That(reference[hit.Triangle], Is.EqualTo(best).Within(tolerance), what + ": a nearest triangle");
                Assert.That((hit.Position - p).magnitude, Is.EqualTo(hit.Distance).Within(1e-5f), what + ": the point it gives");

                // Every triangle within a radius is among those near it.
                float radius = (float)best + .003f;
                bvh.TrianglesNear(p, radius, near);
                var found = new HashSet<int>(near);
                for (int t = 0; t < count; t++)
                    if (reference[t] < radius * (1 - 1e-5)) Assert.That(found.Contains(t), Is.True, what + ": triangle " + t + " near");

                // A ray along the sliver's normal from above it meets it at the height it starts from.
                if (!what.StartsWith("above", StringComparison.Ordinal)) continue;
                int k = int.Parse(what.Substring(what.LastIndexOf(' ') + 1));
                Vector3 a = vertices[triangles[k * 3]], b = vertices[triangles[k * 3 + 1]], c = vertices[triangles[k * 3 + 2]];
                var normal = Vector3.Cross(b - a, c - a).normalized;
                float height = Vector3.Dot(p - a, normal);
                if (Mathf.Abs(height) < 1e-6f) continue;
                var direction = height > 0 ? -normal : normal;
                var ray = bvh.Raycast(p, direction, 10f);
                // Another sliver may cross in between (they lie at random): the nearest crossing of any other triangle.
                float expected = Mathf.Abs(height);
                for (int t = 0; t < count; t++)
                    if (t != k && Triangle.Raycast(p, direction, vertices[triangles[t * 3]], vertices[triangles[t * 3 + 1]], vertices[triangles[t * 3 + 2]], true, false,
                            out float d, out _))
                        expected = Mathf.Min(expected, d);
                Assert.That(ray.Found, Is.True, what + ": the ray meets the sliver");
                Assert.That(ray.Distance, Is.EqualTo(expected).Within(1e-5f), what + ": at its height");
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
