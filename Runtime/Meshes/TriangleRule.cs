using System;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Orbiters.Toolkit.Meshes
{
    /// <summary>The tests a <see cref="TriangleRule"/> can make, as bits: a <see cref="TriangleClasses"/> class names those it needs.</summary>
    [Flags]
    public enum TriangleTests
    {
        None = 0,
        /// <summary>The triangle's face normal against a direction (<see cref="TriangleRule.Facing"/>).</summary>
        Facing = 1,
        /// <summary>The triangle's bit mask shares a bit with the query's (<see cref="TriangleRule.InMasks"/>).</summary>
        Masks = 2,
        /// <summary>The triangle's label is accepted (<see cref="TriangleRule.Labelled"/>).</summary>
        Labels = 4,
        /// <summary>The triangle's second label is accepted (<see cref="TriangleRule.AlsoLabelled"/>).</summary>
        MoreLabels = 8,
        /// <summary>The triangle is flagged (<see cref="TriangleRule.Among"/>).</summary>
        Allowed = 16,
        /// <summary>The triangle's rank is high enough (<see cref="TriangleRule.Ranked"/>).</summary>
        Ranks = 32,
        /// <summary>No corner of the triangle carries one of two labels (<see cref="TriangleRule.AwayFrom"/>).</summary>
        AwayFrom = 64,
        /// <summary>The triangle is not the one left out (<see cref="TriangleRule.Except"/>).</summary>
        Excluded = 128,
    }

    /// <summary>How <see cref="TriangleRule.Facing"/> compares the dot product of a face normal and the direction with the minimum.</summary>
    public enum FacingTest
    {
        /// <summary>The dot product is at least the minimum.</summary>
        AtLeast = 0,
        /// <summary>The dot product is above the minimum.</summary>
        Above = 1,
        /// <summary>The dot product's magnitude is at least the minimum (turned along or against the direction).</summary>
        EitherAtLeast = 2,
    }

    /// <summary>
    /// Which triangles a <see cref="MeshBvh"/> query considers, as tests on per-triangle data that the Burst-compiled queries run
    /// (no delegate, no managed object, the same bits on every machine). Every test is off until set; a triangle is accepted
    /// when every test set accepts it. Arrays are indexed by the mesh's triangle index (vertex index for
    /// <see cref="AwayFrom"/>'s labels) and only read while a query runs (pinned for its length); the rule keeps them by
    /// reference, so a rule is cheap to make per query. A value type: each method returns a copy with one more test set.
    /// </summary>
    public struct TriangleRule
    {
        internal TriangleTests set;
        internal Vector3[] faceNormals;
        internal Vector3 direction;
        internal float minimum;
        internal FacingTest facing;
        internal int[] masks;
        internal int mask;
        internal int[] labels;
        internal bool[] acceptedLabels;
        internal int excludedLabel;
        internal bool hasExcludedLabel;
        internal bool outsideLabelsAccepted;
        internal int[] moreLabels;
        internal bool[] acceptedMoreLabels;
        internal bool outsideMoreLabelsAccepted;
        internal bool[] allowed;
        internal int[] ranks;
        internal int minimumRank;
        internal int[] awayTriangles;
        internal int[] awayLabels;
        internal int awayA, awayB;
        internal int excludedTriangle;

        /// <summary>Every triangle (no test set).</summary>
        public static TriangleRule All => default;

        /// <summary>The tests set.</summary>
        public TriangleTests Tests => set;

        /// <summary>
        /// Triangles whose unit face normal (<paramref name="faceNormals"/>, one per triangle) makes a dot product with
        /// <paramref name="direction"/> that passes <paramref name="test"/> against <paramref name="minimum"/>. The dot product
        /// is x*x' + y*y' + z*z' in that order, in single precision.
        /// </summary>
        public TriangleRule Facing(Vector3[] faceNormals, Vector3 direction, float minimum, FacingTest test = FacingTest.AtLeast)
        {
            var rule = this;
            rule.faceNormals = faceNormals ?? throw new ArgumentNullException(nameof(faceNormals));
            rule.direction = direction;
            rule.minimum = minimum;
            rule.facing = test;
            rule.set |= TriangleTests.Facing;
            return rule;
        }

        /// <summary>Triangles turned against <paramref name="direction"/> (a negative dot product with it).</summary>
        public TriangleRule Against(Vector3[] faceNormals, Vector3 direction) => Facing(faceNormals, -direction, 0f, FacingTest.Above);

        /// <summary>
        /// Triangles whose mask (<paramref name="triangleMasks"/>) shares a bit with <paramref name="mask"/>; a mask of 0 on either
        /// side accepts (a triangle of no particular kind, a query of any kind).
        /// </summary>
        public TriangleRule InMasks(int[] triangleMasks, int mask)
        {
            var rule = this;
            rule.masks = triangleMasks ?? throw new ArgumentNullException(nameof(triangleMasks));
            rule.mask = mask;
            rule.set |= TriangleTests.Masks;
            return rule;
        }

        /// <summary>
        /// Triangles whose label (<paramref name="labels"/>) is accepted: with <paramref name="accepted"/> (one flag per label),
        /// a label within it and flagged, or with <paramref name="outsideAccepted"/> any label outside it (a negative one: none
        /// in particular); without it, any label. With <paramref name="excluded"/>, never that label.
        /// </summary>
        public TriangleRule Labelled(int[] labels, bool[] accepted, int? excluded = null, bool outsideAccepted = false)
        {
            var rule = this;
            rule.labels = labels ?? throw new ArgumentNullException(nameof(labels));
            rule.acceptedLabels = accepted;
            rule.outsideLabelsAccepted = outsideAccepted;
            rule.hasExcludedLabel = excluded.HasValue;
            rule.excludedLabel = excluded ?? 0;
            rule.set |= TriangleTests.Labels;
            return rule;
        }

        /// <summary>A second label test (<see cref="Labelled"/> without an excluded label): both must accept.</summary>
        public TriangleRule AlsoLabelled(int[] labels, bool[] accepted, bool outsideAccepted = false)
        {
            var rule = this;
            rule.moreLabels = labels ?? throw new ArgumentNullException(nameof(labels));
            rule.acceptedMoreLabels = accepted ?? throw new ArgumentNullException(nameof(accepted));
            rule.outsideMoreLabelsAccepted = outsideAccepted;
            rule.set |= TriangleTests.MoreLabels;
            return rule;
        }

        /// <summary>Triangles flagged in <paramref name="allowed"/> (one flag per triangle).</summary>
        public TriangleRule Among(bool[] allowed)
        {
            var rule = this;
            rule.allowed = allowed ?? throw new ArgumentNullException(nameof(allowed));
            rule.set |= TriangleTests.Allowed;
            return rule;
        }

        /// <summary>Triangles whose rank (<paramref name="ranks"/>) is at least <paramref name="minimum"/>.</summary>
        public TriangleRule Ranked(int[] ranks, int minimum)
        {
            var rule = this;
            rule.ranks = ranks ?? throw new ArgumentNullException(nameof(ranks));
            rule.minimumRank = minimum;
            rule.set |= TriangleTests.Ranks;
            return rule;
        }

        /// <summary>
        /// Triangles of <paramref name="triangles"/> (three vertex indices each) none of whose corners has the label
        /// <paramref name="a"/> or <paramref name="b"/> in <paramref name="vertexLabels"/> (a ray along an edge must not meet
        /// the triangles at its ends). A corner outside <paramref name="vertexLabels"/> has no label.
        /// </summary>
        public TriangleRule AwayFrom(int[] triangles, int[] vertexLabels, int a, int b)
        {
            var rule = this;
            rule.awayTriangles = triangles ?? throw new ArgumentNullException(nameof(triangles));
            rule.awayLabels = vertexLabels ?? throw new ArgumentNullException(nameof(vertexLabels));
            rule.awayA = a;
            rule.awayB = b;
            rule.set |= TriangleTests.AwayFrom;
            return rule;
        }

        /// <summary>Every triangle but <paramref name="triangle"/> (a ray leaving a triangle must not meet it again).</summary>
        public TriangleRule Except(int triangle)
        {
            var rule = this;
            rule.excludedTriangle = triangle;
            rule.set |= TriangleTests.Excluded;
            return rule;
        }

        // Every array a set test reads must cover the triangles a query may visit: Burst reads through pointers.
        internal void Check(int triangleCount)
        {
            if ((set & TriangleTests.Facing) != 0) Require(faceNormals.Length >= triangleCount, "face normals");
            if ((set & TriangleTests.Masks) != 0) Require(masks.Length >= triangleCount, "masks");
            if ((set & TriangleTests.Labels) != 0) Require(labels.Length >= triangleCount, "labels");
            if ((set & TriangleTests.MoreLabels) != 0) Require(moreLabels.Length >= triangleCount, "second labels");
            if ((set & TriangleTests.Allowed) != 0) Require(allowed.Length >= triangleCount, "allowed flags");
            if ((set & TriangleTests.Ranks) != 0) Require(ranks.Length >= triangleCount, "ranks");
            if ((set & TriangleTests.AwayFrom) != 0) Require(awayTriangles.Length >= 3L * triangleCount, "triangles");

            void Require(bool ok, string what)
            {
                if (!ok) throw new ArgumentException("The rule's " + what + " do not cover the mesh's " + triangleCount + " triangles.");
            }
        }
    }

    /// <summary>
    /// Up to <see cref="Max"/> acceptance classes over one <see cref="TriangleRule"/>, for
    /// <see cref="MeshBvh.ClosestPointsByClass"/>: class c accepts a triangle when every test it names accepts it (tests it
    /// does not name are ignored for it, set or not). One traversal answers what one query per class would.
    /// </summary>
    public readonly struct TriangleClasses
    {
        /// <summary>The most classes one traversal tracks.</summary>
        public const int Max = 4;

        public readonly TriangleRule Rule;
        public readonly int Count;
        internal readonly int4 needs;

        public TriangleClasses(TriangleRule rule, TriangleTests first, TriangleTests? second = null, TriangleTests? third = null, TriangleTests? fourth = null)
        {
            Rule = rule;
            Count = 1 + (second.HasValue ? 1 : 0) + (third.HasValue ? 1 : 0) + (fourth.HasValue ? 1 : 0);
            if ((third.HasValue && !second.HasValue) || (fourth.HasValue && !third.HasValue)) throw new ArgumentException("Classes are given in order.");
            needs = new int4((int)first, (int)(second ?? 0), (int)(third ?? 0), (int)(fourth ?? 0));
            var all = first | (second ?? 0) | (third ?? 0) | (fourth ?? 0);
            if ((all & ~rule.set) != 0) throw new ArgumentException("A class names a test the rule does not set: " + (all & ~rule.set) + ".");
        }

        /// <summary>The tests class <paramref name="index"/> needs.</summary>
        public TriangleTests Needs(int index) => (TriangleTests)needs[index];
    }

    /// <summary>Which acceptance classes a triangle belongs to (bit c: class c), for the multi-class closest-point walk.</summary>
    public interface ITriangleClassifier
    {
        int Classes(int triangle);
    }

    /// <summary>
    /// A <see cref="TriangleRule"/> as Burst code reads it: pointers into the rule's arrays, pinned by the query that made it.
    /// As a filter it accepts what every set test accepts; as a classifier, class c is what the tests in
    /// <see cref="Needs"/>[c] accept.
    /// </summary>
    public unsafe struct TriangleRuleView : ITriangleFilter, ITriangleClassifier
    {
        public int Set;
        [NativeDisableUnsafePtrRestriction] public float3* FaceNormals;
        public float3 Direction;
        public float Minimum;
        public int Facing;
        [NativeDisableUnsafePtrRestriction] public int* Masks;
        public int Mask;
        [NativeDisableUnsafePtrRestriction] public int* Labels;
        [NativeDisableUnsafePtrRestriction] public byte* AcceptedLabels;
        /// <summary>1 when the rule gave a table (an empty one pins to a null pointer: that is still a table, accepting no label in it).</summary>
        public int HasAcceptedLabels;
        public int AcceptedLabelCount;
        public int OutsideLabelsAccepted;
        public int HasExcludedLabel, ExcludedLabel;
        [NativeDisableUnsafePtrRestriction] public int* MoreLabels;
        [NativeDisableUnsafePtrRestriction] public byte* AcceptedMoreLabels;
        public int AcceptedMoreLabelCount;
        public int OutsideMoreLabelsAccepted;
        [NativeDisableUnsafePtrRestriction] public byte* Allowed;
        [NativeDisableUnsafePtrRestriction] public int* Ranks;
        public int MinimumRank;
        [NativeDisableUnsafePtrRestriction] public int* AwayTriangles;
        [NativeDisableUnsafePtrRestriction] public int* AwayLabels;
        public int AwayLabelCount, AwayA, AwayB;
        public int ExcludedTriangle;
        /// <summary>The tests each class needs (classifier use).</summary>
        public int4 Needs;
        public int ClassCount;

        public bool Accept(int triangle)
        {
            int set = Set;
            if (set == 0) return true;
            if ((set & (int)TriangleTests.Excluded) != 0 && triangle == ExcludedTriangle) return false;
            if ((set & (int)TriangleTests.Allowed) != 0 && Allowed[triangle] == 0) return false;
            if ((set & (int)TriangleTests.Ranks) != 0 && Ranks[triangle] < MinimumRank) return false;
            if ((set & (int)TriangleTests.Labels) != 0 && !LabelAccepted(triangle)) return false;
            if ((set & (int)TriangleTests.MoreLabels) != 0 && !MoreLabelAccepted(triangle)) return false;
            if ((set & (int)TriangleTests.Masks) != 0 && !MaskAccepted(triangle)) return false;
            if ((set & (int)TriangleTests.AwayFrom) != 0 && !Away(triangle)) return false;
            if ((set & (int)TriangleTests.Facing) != 0 && !Faces(triangle)) return false;
            return true;
        }

        public int Classes(int triangle)
        {
            int set = Set;
            // The tests passed, the ones not set counting as passed.
            int passed = ~set;
            if ((set & (int)TriangleTests.Excluded) != 0 && triangle != ExcludedTriangle) passed |= (int)TriangleTests.Excluded;
            if ((set & (int)TriangleTests.Allowed) != 0 && Allowed[triangle] != 0) passed |= (int)TriangleTests.Allowed;
            if ((set & (int)TriangleTests.Ranks) != 0 && Ranks[triangle] >= MinimumRank) passed |= (int)TriangleTests.Ranks;
            if ((set & (int)TriangleTests.Labels) != 0 && LabelAccepted(triangle)) passed |= (int)TriangleTests.Labels;
            if ((set & (int)TriangleTests.MoreLabels) != 0 && MoreLabelAccepted(triangle)) passed |= (int)TriangleTests.MoreLabels;
            if ((set & (int)TriangleTests.Masks) != 0 && MaskAccepted(triangle)) passed |= (int)TriangleTests.Masks;
            if ((set & (int)TriangleTests.AwayFrom) != 0 && Away(triangle)) passed |= (int)TriangleTests.AwayFrom;
            if ((set & (int)TriangleTests.Facing) != 0 && Faces(triangle)) passed |= (int)TriangleTests.Facing;
            int classes = 0;
            for (int c = 0; c < ClassCount; c++)
                if ((passed & Needs[c]) == Needs[c]) classes |= 1 << c;
            return classes;
        }

        private bool LabelAccepted(int triangle)
        {
            int label = Labels[triangle];
            if (HasExcludedLabel != 0 && label == ExcludedLabel) return false;
            if (HasAcceptedLabels == 0) return true;
            return (uint)label < (uint)AcceptedLabelCount ? AcceptedLabels[label] != 0 : OutsideLabelsAccepted != 0;
        }

        private bool MoreLabelAccepted(int triangle)
        {
            int label = MoreLabels[triangle];
            return (uint)label < (uint)AcceptedMoreLabelCount ? AcceptedMoreLabels[label] != 0 : OutsideMoreLabelsAccepted != 0;
        }

        private bool MaskAccepted(int triangle)
        {
            int m = Masks[triangle];
            return Mask == 0 || m == 0 || (m & Mask) != 0;
        }

        private bool Away(int triangle)
        {
            int t3 = triangle * 3;
            for (int k = 0; k < 3; k++)
            {
                int v = AwayTriangles[t3 + k];
                if ((uint)v >= (uint)AwayLabelCount) continue;
                int label = AwayLabels[v];
                if (label == AwayA || label == AwayB) return false;
            }
            return true;
        }

        private bool Faces(int triangle)
        {
            float3 n = FaceNormals[triangle];
            float dot = n.x * Direction.x + n.y * Direction.y + n.z * Direction.z;
            switch (Facing)
            {
                case (int)FacingTest.Above: return dot > Minimum;
                case (int)FacingTest.EitherAtLeast: return math.abs(dot) >= Minimum;
                default: return dot >= Minimum;
            }
        }
    }
}
