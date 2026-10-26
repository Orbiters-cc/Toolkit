using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// N4N0's model: a black sphere with white patterns, in VRChat's mobile Standard Lite so that it looks the same on PC
    /// and on phones. The front is its face: two wide eyes wrapping round its sides and a small mouth, strokes that
    /// blendshapes bend into its expressions (<see cref="ModelShapes"/>, Normal being none of them); the back carries the
    /// N4N0 mark under a triangle's three corners. One skinned mesh without bones and one material (phones count material
    /// slots): its two tiny pictures paint the shell dark and the patterns white, and only the patterns glow. A radius of 1
    /// (its object is scaled to <see cref="Radius"/>), its face's middle ahead (+Z), its right −X.
    /// </summary>
    public static partial class OrbInstaller
    {
        /// <summary>The model's blendshapes N4N0's states set, in this order: N4N0's model part asks every model for them.</summary>
        public static readonly IReadOnlyList<string> ModelShapes = new[] { "Surprised", "Annoyed", "Happy", "Thinking", "Sleeping", "Blink", "Talk" };
        internal const string ModelShader = "VRChat/Mobile/Standard Lite";
        // The shell's colour as its picture holds it (sRGB): a dark grey that still shows the light.
        internal static readonly Color ShellColour = new Color(.14f, .14f, .15f), PatternColour = Color.white;

        // The shell's segments around and rings from pole to pole; the patterns float a little above it (its flat
        // triangles sink at most 1 - cos 7.5° between vertices, the patterns' 1 - cos 4°).
        private const int ShellSegments = 24, ShellRings = 16;
        private const float PatternLift = 1.015f;
        // A stroke's samples between two landmarks, and its quads across.
        private const int StrokeSamples = 6, StrokeRows = 3;
        // The face's sheet (Enzo's drawing of N4N0's expressions, the Normal face's pixels) on the sphere: its middle's x,
        // degrees a pixel across and down, the latitude of y = 368 (the eyes' tops).
        private const float FaceMiddle = 582, FaceAcross = .46f, FaceDown = .289f, FaceTop = 14;

        // ---- The face ----

        // A landmark of a stroke's edge: a point of the sheet, sharp where the edge turns a corner.
        private readonly struct Landmark
        {
            public readonly Vector2 At;
            public readonly bool Sharp;
            public Landmark(float x, float y, bool sharp = false) { At = new Vector2(x, y); Sharp = sharp; }
        }

        // A filled stroke between its outer and inner edges, which meet at both ends: landmark pairs, the first and last
        // its tips. Every expression's stroke has as many landmarks, so its samples line up for the blendshapes.
        private sealed class Stroke
        {
            public readonly Landmark[] Outer, Inner;
            public Stroke(Landmark[] outer, Landmark[] inner) { Outer = outer; Inner = inner; }
            public Stroke Mirrored() => new Stroke(Mirror(Outer), Mirror(Inner));
            private static Landmark[] Mirror(Landmark[] marks) => marks.Select(m => new Landmark(2 * FaceMiddle - m.At.x, m.At.y, m.Sharp)).ToArray();
        }

        private static Landmark Soft(float x, float y) => new Landmark(x, y);
        private static Landmark Hard(float x, float y) => new Landmark(x, y, true);

        // The left eye (as the sheet shows it) of each expression: its tip, the top edge and the arm under it, the corner and
        // its notch, then down the outer and inner edges to the bottom tip. Surprised's tip is its hood's lower right end,
        // the spike back toward the middle starting its inner edge, whose fan holds still while the hood sweeps round.
        private static readonly Dictionary<string, Stroke> EyeStrokes = new Dictionary<string, Stroke>
        {
            ["Normal"] = new Stroke(
                new[] { Hard(522, 407), Soft(448, 386), Hard(373, 368), Soft(375, 425), Soft(384, 480), Soft(403, 533), Soft(433, 577), Hard(470, 602) },
                new[] { Hard(522, 407), Soft(481, 431), Hard(440, 455), Soft(444, 490), Soft(448, 525), Soft(454, 557), Soft(462, 585), Hard(470, 602) }),
            ["Annoyed"] = new Stroke(
                new[] { Hard(526, 424), Soft(452, 392), Hard(377, 358), Soft(379, 414), Soft(389, 482), Soft(409, 542), Soft(439, 584), Hard(475, 607) },
                new[] { Hard(526, 424), Soft(484, 447), Hard(442, 470), Soft(446, 503), Soft(450, 534), Soft(456, 562), Soft(464, 588), Hard(475, 607) }),
            ["Happy"] = new Stroke(
                new[] { Hard(526, 384), Soft(455, 374), Hard(384, 366), Soft(386, 428), Soft(395, 488), Soft(412, 540), Soft(438, 580), Hard(469, 603) },
                new[] { Hard(526, 384), Soft(483, 404), Hard(440, 425), Soft(444, 468), Soft(448, 505), Soft(453, 545), Soft(460, 580), Hard(469, 603) }),
            ["Sleeping"] = new Stroke(
                new[] { Hard(521, 436), Soft(454, 433), Hard(387, 430), Soft(389, 470), Soft(396, 510), Soft(410, 548), Soft(432, 584), Hard(465, 605) },
                new[] { Hard(521, 436), Soft(484, 444), Hard(447, 452), Soft(449, 490), Soft(452, 525), Soft(456, 558), Soft(460, 585), Hard(465, 605) }),
            ["Surprised"] = new Stroke(
                new[] { Hard(518, 372), Soft(521, 325), Soft(480, 293), Soft(420, 294), Soft(370, 330), Soft(357, 420), Soft(378, 530), Hard(433, 601) },
                new[] { Hard(518, 372), Soft(474, 424), Hard(428, 455), Hard(428, 455), Hard(428, 455), Soft(426, 500), Soft(427, 555), Hard(433, 601) }),
            // Thinking: the left eye a little lower and smaller, the right a raised rounded brow (drawn as a left eye).
            ["ThinkingLeft"] = new Stroke(
                new[] { Hard(523, 440), Soft(450, 430), Hard(379, 421), Soft(381, 470), Soft(390, 520), Soft(405, 562), Soft(435, 595), Hard(481, 613) },
                new[] { Hard(523, 440), Soft(487, 465), Hard(451, 490), Soft(455, 520), Soft(461, 548), Soft(467, 575), Soft(474, 598), Hard(481, 613) }),
            ["ThinkingRight"] = new Stroke(
                new[] { Hard(513, 412), Soft(484, 380), Soft(444, 350), Soft(404, 338), Soft(374, 355), Soft(364, 400), Soft(372, 500), Hard(419, 600) },
                new[] { Hard(513, 412), Soft(483, 415), Hard(452, 418), Soft(445, 460), Soft(440, 500), Soft(432, 540), Soft(425, 575), Hard(419, 600) }),
        };

        private const int MouthMarks = 7;

        // A mouth between an upper and a lower edge meeting at both ends, bulging by `up` and `down` (sin^roundness).
        private static Stroke Lens(float cx, float cy, float half, float up, float down, float tilt = 0, float roundness = 1)
        {
            var outer = new Landmark[MouthMarks]; var inner = new Landmark[MouthMarks];
            float c = Mathf.Cos(tilt), s = Mathf.Sin(tilt);
            for (int k = 0; k < MouthMarks; k++)
            {
                float t = k / (MouthMarks - 1f), x = -half + 2 * half * t, bump = Mathf.Pow(Mathf.Max(0, Mathf.Sin(Mathf.PI * t)), roundness);
                Vector2 Turn(float px, float py) => new Vector2(cx + px * c - py * s, cy + px * s + py * c);
                bool sharp = k == 0 || k == MouthMarks - 1;
                var o = Turn(x, -up * bump); var i = Turn(x, down * bump);
                outer[k] = new Landmark(o.x, o.y, sharp); inner[k] = new Landmark(i.x, i.y, sharp);
            }
            return new Stroke(outer, inner);
        }

        // A frown: an arc rising by `rise` in its middle, `thick` there.
        private static Stroke Frown(float cx, float cy, float half, float rise, float thick)
        {
            var outer = new Landmark[MouthMarks]; var inner = new Landmark[MouthMarks];
            for (int k = 0; k < MouthMarks; k++)
            {
                float t = k / (MouthMarks - 1f), x = -half + 2 * half * t, b = Mathf.Sin(Mathf.PI * t);
                bool sharp = k == 0 || k == MouthMarks - 1;
                outer[k] = new Landmark(cx + x, cy - rise * b - thick / 2 * b, sharp);
                inner[k] = new Landmark(cx + x, cy - rise * b + thick / 2 * b, sharp);
            }
            return new Stroke(outer, inner);
        }

        // An open smile: a straight top and a round bottom.
        private static Stroke Smile(float cx, float top, float half, float depth)
        {
            var outer = new Landmark[MouthMarks]; var inner = new Landmark[MouthMarks];
            for (int k = 0; k < MouthMarks; k++)
            {
                float t = k / (MouthMarks - 1f), x = -half + 2 * half * t;
                bool sharp = k == 0 || k == MouthMarks - 1;
                outer[k] = new Landmark(cx + x, top, sharp);
                inner[k] = new Landmark(cx + x, top + depth * Mathf.Pow(Mathf.Max(0, Mathf.Sin(Mathf.PI * t)), .8f), sharp);
            }
            return new Stroke(outer, inner);
        }

        private static readonly Dictionary<string, Stroke> MouthStrokes = new Dictionary<string, Stroke>
        {
            ["Normal"] = Lens(FaceMiddle, 520, 24, 4.5f, 4.5f),
            ["Annoyed"] = Frown(FaceMiddle, 532, 33, 10, 9),
            ["Happy"] = Smile(FaceMiddle, 503, 46, 56),
            ["Sleeping"] = Lens(FaceMiddle, 541, 23, 4, 4),
            ["Surprised"] = Lens(FaceMiddle, 538, 13, 18, 18, roundness: .55f),
            ["Thinking"] = Lens(590, 560, 24, 4.5f, 4.5f, tilt: -.6f),
            ["Talk"] = Lens(FaceMiddle, 526, 18, 9, 13, roundness: .6f),
        };

        // Closed eyes: every point pulled up toward the eye's top.
        private static Stroke Blinked(Stroke eye)
        {
            float top = eye.Outer.Min(m => m.At.y);
            Landmark[] Pull(Landmark[] marks) => marks.Select(m => new Landmark(m.At.x, top + 20 + (m.At.y - top) * .18f, m.Sharp)).ToArray();
            return new Stroke(Pull(eye.Outer), Pull(eye.Inner));
        }

        // An expression's strokes: the left eye, the right eye (mirrored) and the mouth.
        private static Stroke[] FaceStrokes(string expression)
        {
            Stroke left, right, mouth;
            switch (expression)
            {
                case "Thinking": left = EyeStrokes["ThinkingLeft"]; right = EyeStrokes["ThinkingRight"]; mouth = MouthStrokes["Thinking"]; break;
                case "Blink": left = right = Blinked(EyeStrokes["Normal"]); mouth = MouthStrokes["Normal"]; break;
                case "Talk": left = right = EyeStrokes["Normal"]; mouth = MouthStrokes["Talk"]; break;
                default: left = right = EyeStrokes[expression]; mouth = MouthStrokes[expression]; break;
            }
            return new[] { left, right.Mirrored(), mouth };
        }

        // A point between landmarks j and j + 1 on a centripetal Catmull-Rom through the edge, which breaks at sharp ones.
        private static Vector2 EdgePoint(Landmark[] marks, int j, float t)
        {
            Vector2 p1 = marks[j].At, p2 = marks[j + 1].At;
            Vector2 p0 = j > 0 && !marks[j].Sharp ? marks[j - 1].At : 2 * p1 - p2;
            Vector2 p3 = j + 2 < marks.Length && !marks[j + 1].Sharp ? marks[j + 2].At : 2 * p2 - p1;
            float Knot(float ti, Vector2 a, Vector2 b) => ti + Mathf.Sqrt(Mathf.Max((b - a).magnitude, 1e-6f));
            float t0 = 0, t1 = Knot(t0, p0, p1), t2 = Knot(t1, p1, p2), t3 = Knot(t2, p2, p3);
            float u = t1 + (t2 - t1) * t;
            var a1 = (t1 - u) / (t1 - t0) * p0 + (u - t0) / (t1 - t0) * p1;
            var a2 = (t2 - u) / (t2 - t1) * p1 + (u - t1) / (t2 - t1) * p2;
            var a3 = (t3 - u) / (t3 - t2) * p2 + (u - t2) / (t3 - t2) * p3;
            var b1 = (t2 - u) / (t2 - t0) * a1 + (u - t0) / (t2 - t0) * a2;
            var b2 = (t3 - u) / (t3 - t1) * a2 + (u - t1) / (t3 - t1) * a3;
            return (t2 - u) / (t2 - t1) * b1 + (u - t1) / (t2 - t1) * b2;
        }

        private static Vector2[] Edge(Landmark[] marks)
        {
            var points = new List<Vector2>();
            for (int j = 0; j + 1 < marks.Length; j++)
            for (int k = 0; k < StrokeSamples; k++)
                points.Add(EdgePoint(marks, j, k / (float)StrokeSamples));
            points.Add(marks[marks.Length - 1].At);
            return points.ToArray();
        }

        // A stroke's points, row by row from its outer edge (row 0) to its inner edge (row StrokeRows), on the sphere.
        private static Vector3[] StrokePoints(Stroke stroke)
        {
            var outer = Edge(stroke.Outer); var inner = Edge(stroke.Inner);
            var points = new Vector3[(StrokeRows + 1) * outer.Length];
            for (int r = 0; r <= StrokeRows; r++)
            for (int c = 0; c < outer.Length; c++)
            {
                var p = Vector2.Lerp(outer[c], inner[c], r / (float)StrokeRows);
                points[r * outer.Length + c] = OnSphere(-FaceAcross * (p.x - FaceMiddle), FaceTop - FaceDown * (p.y - 368), PatternLift);
            }
            return points;
        }

        private static Vector3 OnSphere(float longitude, float latitude, float radius = 1)
        {
            float lo = longitude * Mathf.Deg2Rad, la = latitude * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(lo) * Mathf.Cos(la), Mathf.Sin(la), Mathf.Cos(lo) * Mathf.Cos(la)) * radius;
        }

        // ---- The back ----

        // The N4N0 mark (Orbiters - N4N0's orbiters-ui n4n0_logo.rs, a 940 × 822 view box), cut into simple pieces: the 0's
        // two slots and the 4's counter by hand.
        private static readonly Vector2[][] MarkPieces =
        {
            // The 0: its top and bottom bars, its sides, the band between its two slots.
            P2(513, 455.5f, 939.5f, 455.5f, 939.5f, 528.231f, 513, 528.231f),
            P2(513, 742.669f, 939.5f, 742.669f, 939.5f, 821.5f, 513, 821.5f),
            P2(513, 528.231f, 597, 528.231f, 597, 742.669f, 513, 742.669f),
            P2(857, 528.231f, 939.5f, 528.231f, 939.5f, 742.669f, 857, 742.669f),
            P2(597, 635.45f, 857, 569.054f, 857, 655.862f, 597, 717.331f),
            // The N at the bottom left, and its corner.
            P2(.5f, 821.5f, .5f, 455.5f, 192, 455.5f, 407, 761, 407, 821.5f, 338, 821.5f, 161.5f, 559.5f, 82.5f, 559.5f, 82.5f, 821.5f),
            P2(407, 455.5f, 407, 616.5f, 302.5f, 455.5f),
            // The N at the top left, and its corner.
            P2(.5f, 366.5f, .5f, .5f, 192, .5f, 407, 306, 407, 366.5f, 338, 366.5f, 161.5f, 104.5f, 82.5f, 104.5f, 82.5f, 366.5f),
            P2(407, .5f, 407, 161.5f, 302.5f, .5f),
            // The 4: its foot, crossbar, stem's foot, stem, top bar, slant and left side.
            P2(513, 326.5f, 597, 326.5f, 597, 366.5f, 513, 366.5f),
            P2(513, 245.5f, 939.5f, 245.5f, 939.5f, 326.5f, 513, 326.5f),
            P2(751.5f, 326.5f, 832, 326.5f, 832, 366.5f, 751.5f, 366.5f),
            P2(751.5f, 82.5f, 832, 82.5f, 832, 245.5f, 751.5f, 245.5f),
            P2(615.5f, .5f, 832, .5f, 832, 82.5f, 657.5f, 82.5f),
            P2(513, 138, 615.5f, .5f, 657.5f, 82.5f, 597, 161.5f),
            P2(513, 138, 597, 161.5f, 597, 245.5f, 513, 245.5f),
        };

        private static Vector2[] P2(params float[] xy) => Enumerable.Range(0, xy.Length / 2).Select(i => new Vector2(xy[2 * i], xy[2 * i + 1])).ToArray();

        // The mark's middle latitude and height in degrees; the triangle's apex and base latitudes, its corners' bars.
        private const float MarkLatitude = -19, MarkHeight = 28, TriangleTop = 37, TriangleBase = 10.5f, CornerLength = 8.3f, CornerWidth = 2.2f;

        // The mark's view box on the back: as seen from behind, its right toward +X.
        private static Vector2 MarkDegrees(Vector2 p)
        {
            float down = MarkHeight / 822, across = down / Mathf.Cos(MarkLatitude * Mathf.Deg2Rad);
            return new Vector2(180 - across * (p.x - 470), MarkLatitude - down * (p.y - 411));
        }

        // Arc degrees around the back (x right as seen from behind, y the latitude) to longitude and latitude.
        private static Vector2 BackDegrees(Vector2 p) => new Vector2(180 - p.x / Mathf.Cos(p.y * Mathf.Deg2Rad), p.y);

        // A corner of the triangle: two bars from `corner` along `a` and `b`, CornerWidth thick toward the inside.
        private static Vector2[] Corner(Vector2 corner, Vector2 a, Vector2 b)
        {
            a.Normalize(); b.Normalize();
            var na = (b - a * Vector2.Dot(b, a)).normalized; var nb = (a - b * Vector2.Dot(a, b)).normalized;
            // The inner corner: corner + s a + w na = corner + t b + w nb.
            var rhs = CornerWidth * (nb - na);
            float det = a.x * -b.y - -b.x * a.y;
            float s = (rhs.x * -b.y - -b.x * rhs.y) / det;
            var inside = corner + s * a + CornerWidth * na;
            return new[] { corner, corner + a * CornerLength, corner + a * CornerLength + na * CornerWidth, inside, corner + b * CornerLength + nb * CornerWidth, corner + b * CornerLength };
        }

        private static IEnumerable<Vector2[]> TriangleCorners()
        {
            float half = (TriangleTop - TriangleBase) / Mathf.Sqrt(3);
            Vector2 apex = new Vector2(0, TriangleTop), left = new Vector2(-half, TriangleBase), right = new Vector2(half, TriangleBase);
            yield return Corner(apex, left - apex, right - apex);
            yield return Corner(left, right - left, apex - left);
            yield return Corner(right, apex - right, left - right);
        }

        // A simple polygon's triangles (ear clipping), as index triples into it.
        private static List<int> EarClip(IReadOnlyList<Vector2> polygon)
        {
            var order = Enumerable.Range(0, polygon.Count).ToList();
            float area = 0;
            for (int i = 0; i < polygon.Count; i++) { var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count]; area += a.x * b.y - b.x * a.y; }
            if (area < 0) order.Reverse();
            float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
            {
                float d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
                return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
            }
            var triangles = new List<int>();
            for (int guard = 0; order.Count > 3 && guard < 1000; guard++)
            for (int k = 0; k < order.Count; k++)
            {
                int i0 = order[(k + order.Count - 1) % order.Count], i1 = order[k], i2 = order[(k + 1) % order.Count];
                if (Cross(polygon[i0], polygon[i1], polygon[i2]) <= 0) continue;
                if (order.Any(j => j != i0 && j != i1 && j != i2 && Inside(polygon[j], polygon[i0], polygon[i1], polygon[i2]))) continue;
                triangles.AddRange(new[] { i0, i1, i2 });
                order.RemoveAt(k);
                break;
            }
            triangles.AddRange(order);
            return triangles;
        }

        // A flat triangle split until its edges span at most 8° on the sphere, each piece's corners in degrees.
        private static IEnumerable<(Vector2, Vector2, Vector2)> Split(Vector2 a, Vector2 b, Vector2 c, Func<Vector2, Vector2> degrees)
        {
            float Span(Vector2 p, Vector2 q)
            {
                var dp = degrees(p); var dq = degrees(q);
                return new Vector2((dp.x - dq.x) * Mathf.Cos((dp.y + dq.y) / 2 * Mathf.Deg2Rad), dp.y - dq.y).magnitude;
            }
            if (Mathf.Max(Span(a, b), Mathf.Max(Span(b, c), Span(c, a))) <= 8) { yield return (a, b, c); yield break; }
            Vector2 ab = (a + b) / 2, bc = (b + c) / 2, ca = (c + a) / 2;
            foreach (var t in new[] { (a, ab, ca), (ab, b, bc), (ca, bc, c), (ab, bc, ca) })
            foreach (var piece in Split(t.Item1, t.Item2, t.Item3, degrees))
                yield return piece;
        }

        // ---- The mesh and its materials ----

        /// <summary>N4N0's model mesh, made once in <paramref name="folder"/> ("N4N0 model.asset").</summary>
        internal static Mesh ModelMesh(string folder)
        {
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var shell = new List<int>();
            var patterns = new List<int>();
            // The shell's points on the pictures' left half, the patterns' (above it) on their right.
            int Add(Vector3 p)
            {
                vertices.Add(p);
                uvs.Add(new Vector2(p.sqrMagnitude > 1.0001f ? PatternU : ShellU, .5f));
                return vertices.Count - 1;
            }
            // Facing outwards: Unity's front faces turn clockwise as seen, their cross product toward the viewer.
            void Triangle(List<int> into, int a, int b, int c, bool flip)
            {
                if (flip) (b, c) = (c, b);
                into.Add(a); into.Add(b); into.Add(c);
            }
            bool Inward(int a, int b, int c) =>
                Vector3.Dot(Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]), vertices[a] + vertices[b] + vertices[c]) < 0;

            // The shell: rings from the south pole to the north, each closing on a seam.
            var grid = new int[ShellRings + 1, ShellSegments + 1];
            for (int i = 0; i <= ShellRings; i++)
            for (int j = 0; j <= ShellSegments; j++)
                grid[i, j] = Add(OnSphere(360f * j / ShellSegments, -90 + 180f * i / ShellRings));
            for (int i = 0; i < ShellRings; i++)
            for (int j = 0; j < ShellSegments; j++)
            {
                int a = grid[i, j], b = grid[i, j + 1], c = grid[i + 1, j + 1], d = grid[i + 1, j];
                if (i > 0) Triangle(shell, a, b, c, Inward(a, b, c));
                if (i < ShellRings - 1) Triangle(shell, a, c, d, Inward(a, c, d));
            }

            // The face: each stroke's points as Normal has them, and where each expression moves them.
            var faceStart = vertices.Count;
            var shapes = ModelShapes.ToDictionary(s => s, s => new List<Vector3>());
            var strokes = FaceStrokes("Normal");
            for (int k = 0; k < strokes.Length; k++)
            {
                var points = StrokePoints(strokes[k]);
                int first = vertices.Count, columns = points.Length / (StrokeRows + 1);
                foreach (var p in points) Add(p);
                foreach (var shape in ModelShapes) shapes[shape].AddRange(StrokePoints(FaceStrokes(shape)[k]).Select((p, i) => p - points[i]));
                // One winding for the whole stroke, from its area as Normal has it (its tips' triangles have none).
                float facing = 0;
                for (int r = 0; r < StrokeRows; r++)
                for (int c = 0; c + 1 < columns; c++)
                {
                    int a = first + r * columns + c, d = a + columns;
                    facing += Vector3.Dot(Vector3.Cross(vertices[a + 1] - vertices[a], vertices[d + 1] - vertices[a]), vertices[a]);
                }
                for (int r = 0; r < StrokeRows; r++)
                for (int c = 0; c + 1 < columns; c++)
                {
                    int a = first + r * columns + c, b = a + 1, d = a + columns, e = d + 1;
                    Triangle(patterns, a, b, e, facing < 0);
                    Triangle(patterns, a, e, d, facing < 0);
                }
            }
            int faceEnd = vertices.Count;

            // The back: the mark and the triangle's corners, flat pieces split to follow the sphere.
            void Flat(IReadOnlyList<Vector2> polygon, Func<Vector2, Vector2> degrees)
            {
                var corners = EarClip(polygon);
                for (int t = 0; t + 2 < corners.Count; t += 3)
                foreach (var (a, b, c) in Split(polygon[corners[t]], polygon[corners[t + 1]], polygon[corners[t + 2]], degrees))
                {
                    int ia = Add(Lifted(degrees(a))), ib = Add(Lifted(degrees(b))), ic = Add(Lifted(degrees(c)));
                    Triangle(patterns, ia, ib, ic, Inward(ia, ib, ic));
                }
            }
            Vector3 Lifted(Vector2 d) => OnSphere(d.x, d.y, PatternLift);
            foreach (var piece in MarkPieces) Flat(piece, MarkDegrees);
            foreach (var corner in TriangleCorners()) Flat(corner, BackDegrees);

            var mesh = new Mesh { name = "N4N0 model" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(vertices.Select(v => v.normalized).ToList());
            mesh.SetUVs(0, uvs);
            // White vertex colours: Standard Lite multiplies its colour by them (its keyword that would not draws wrong).
            mesh.SetColors(Enumerable.Repeat(Color.white, vertices.Count).ToList());
            mesh.SetTriangles(shell.Concat(patterns).ToList(), 0);
            foreach (var shape in ModelShapes)
            {
                var deltas = new Vector3[vertices.Count];
                var normals = new Vector3[vertices.Count];
                for (int i = faceStart; i < faceEnd; i++)
                {
                    deltas[i] = shapes[shape][i - faceStart];
                    normals[i] = (vertices[i] + deltas[i]).normalized - vertices[i].normalized;
                }
                mesh.AddBlendShapeFrame(shape, 100, deltas, normals, null);
            }
            mesh.RecalculateTangents();
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2 * PatternLift);
            AssetDatabase.CreateAsset(mesh, folder + "/N4N0 model.asset");
            return mesh;
        }

        // Where the shell's and the patterns' points take their colour in the model's pictures.
        internal const float ShellU = .25f, PatternU = .75f;

        /// <summary>
        /// N4N0's material in <paramref name="folder"/> ("N4N0 model.mat", VRChat's mobile Standard Lite, a little glossy):
        /// its colour picture paints the shell dark and the patterns white, its glow picture lights the patterns alone (the
        /// Light option tints that glow through <c>_EmissionColor</c>).
        /// </summary>
        internal static Material ModelMaterial(string folder)
        {
            var shader = Shader.Find(ModelShader);
            if (shader == null) throw new InvalidOperationException("VRChat's " + ModelShader + " shader is missing: update the VRChat SDK in Creator Companion.");
            var material = new Material(shader) { name = "N4N0 model" };
            material.SetTexture("_MainTex", Palette(folder + "/N4N0 model.png", ShellColour, PatternColour));
            material.SetTexture("_EmissionMap", Palette(folder + "/N4N0 model glow.png", Color.black, Color.white));
            material.SetColor("_Color", Color.white);
            material.SetFloat("_Metallic", 0);
            material.SetFloat("_Glossiness", .55f);
            // A soft highlight and the world's reflections on the dark shell.
            material.SetFloat("_SpecularHighlights", 1); material.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
            material.SetFloat("_GlossyReflections", 1); material.DisableKeyword("_GLOSSYREFLECTIONS_OFF");
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", PatternColour);
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            AssetDatabase.CreateAsset(material, folder + "/N4N0 model.mat");
            return material;
        }

        // A 4 × 4 picture, its left half `left` and its right half `right`: sharp, without mipmaps, never compressed.
        private static Texture2D Palette(string path, Color left, Color right)
        {
            var texture = new Texture2D(4, 4, TextureFormat.RGB24, false);
            try
            {
                for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                    texture.SetPixel(x, y, x < 2 ? left : right);
                texture.Apply();
                System.IO.File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default; importer.sRGBTexture = true; importer.mipmapEnabled = false;
            importer.alphaSource = TextureImporterAlphaSource.None; importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Point; importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>
        /// N4N0's model on <paramref name="at"/> (scaled to <see cref="Radius"/>): a skinned mesh without bones, so the
        /// animator sets its blendshapes, lit by the world but casting and taking no shadow.
        /// </summary>
        private static SkinnedMeshRenderer AddModel(Transform at, Mesh mesh, Material material)
        {
            at.localScale = Vector3.one * Radius;
            var renderer = at.gameObject.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.sharedMaterial = material;
            renderer.localBounds = new Bounds(Vector3.zero, Vector3.one * 2.2f);
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return renderer;
        }
    }
}
