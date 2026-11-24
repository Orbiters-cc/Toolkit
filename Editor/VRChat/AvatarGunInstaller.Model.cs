using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// The avatar gun's model, in N4N0's look (N4N0Installer.Model): a small toy ray gun, black with white strokes, in
    /// VRChat's mobile Standard Lite. A black ball at the back (N4N0's), a slim body with a white band, three fins with
    /// white rims, a bell, and the emitter at the muzzle: a ring and a lens that glow in N4N0's green while the gun is
    /// loaded and stay dim while it is not (the second material slot, swapped by the animator). One mesh, its strokes
    /// picked from the body material's two tiny pictures as N4N0's patterns are. The gun's space: its grip's middle at the
    /// origin, the barrel ahead (+Z), its top up (+Y), modelled for a hand of <see cref="AvatarGunInstaller.ReferenceHand"/>.
    /// And the shot's beam: one skinned mesh from the muzzle to the ray's end, a flash at each end.
    /// </summary>
    public static partial class AvatarGunInstaller
    {
        // The barrel's axis above the grip's middle, where the muzzle's ring is, and the muzzle (the ray's start) just past it.
        internal const float Axis = .05f, Front = .105f;
        internal static readonly Vector3 MuzzlePoint = new Vector3(0, Axis, Front + .004f);
        // The grip's middle from the middle of the palm: towards its inside, ahead and down (the barrel above the knuckles).
        internal const float GripInside = .02f, GripAhead = .006f, GripDown = .018f;
        // Segments around the barrel, and the strokes' rise over the black.
        private const int Around = 24;
        private const float Lift = .0012f;
        // The emitter's colour: N4N0's green, glowing when loaded.
        internal static readonly Color EmitterColour = N4N0Installer.Colour;

        // ---- The mesh ----

        // A profile point of the barrel (along +Z at a distance from its axis) and whether its side is a stroke.
        private readonly struct Ring
        {
            public readonly float Z, R;
            public readonly bool White;
            public Ring(float z, float r, bool white = false) { Z = z; R = r; White = white; }
        }

        private sealed class Builder
        {
            public readonly List<Vector3> Vertices = new List<Vector3>(), Normals = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<int> Body = new List<int>(), Emitter = new List<int>();

            public int Add(Vector3 p, Vector3 n, bool white)
            {
                Vertices.Add(p); Normals.Add(n.normalized); Uvs.Add(new Vector2(white ? N4N0Installer.PatternU : N4N0Installer.ShellU, .5f));
                return Vertices.Count - 1;
            }

            // A quad a-b-c-d (clockwise as seen from outside: Unity's front), into the body or the emitter.
            public void Quad(int a, int b, int c, int d, bool emitter = false)
            {
                var into = emitter ? Emitter : Body;
                into.AddRange(new[] { a, b, c, a, c, d });
            }

            public void Triangle(int a, int b, int c, bool emitter = false) => (emitter ? Emitter : Body).AddRange(new[] { a, b, c });
        }

        // A point around the barrel's axis: `angle` from its top, turning to the right.
        private static Vector3 AroundAxis(float z, float r, float angle) => new Vector3(Mathf.Sin(angle) * r, Axis + Mathf.Cos(angle) * r, z);

        /// <summary>
        /// The barrel turned round its axis from a profile, back to front. Each piece between two points is a band of its own;
        /// its normals blend with a neighbour's where the profile bends less than 40° (the ball and the body look round, the
        /// fins' edges stay crisp).
        /// </summary>
        private static void Lathe(Builder b, IReadOnlyList<Ring> profile, bool emitter = false)
        {
            int n = profile.Count;
            var normals = new Vector2[n - 1];
            for (int i = 0; i + 1 < n; i++)
            {
                var t = new Vector2(profile[i + 1].Z - profile[i].Z, profile[i + 1].R - profile[i].R);
                normals[i] = new Vector2(-t.y, t.x).normalized;
            }
            Vector2 At(int segment, int end)
            {
                var own = normals[segment];
                int other = end == 0 ? segment - 1 : segment + 1;
                if (other < 0 || other >= normals.Length || profile[other].White != profile[segment].White) return own;
                return Vector2.Angle(own, normals[other]) < 40 ? (own + normals[other]).normalized : own;
            }
            for (int i = 0; i + 1 < n; i++)
            {
                var p = profile[i]; var q = profile[i + 1];
                if (p.R < 1e-6f && q.R < 1e-6f) continue;
                bool white = p.White;
                var n0 = At(i, 0); var n1 = At(i, 1);
                int first = b.Vertices.Count;
                for (int s = 0; s <= Around; s++)
                {
                    float a = 2 * Mathf.PI * s / Around;
                    var radial = new Vector3(Mathf.Sin(a), Mathf.Cos(a), 0);
                    b.Add(AroundAxis(p.Z, p.R, a), radial * n0.y + Vector3.forward * n0.x, white);
                    b.Add(AroundAxis(q.Z, q.R, a), radial * n1.y + Vector3.forward * n1.x, white);
                }
                for (int s = 0; s < Around; s++)
                {
                    int a0 = first + 2 * s, a1 = a0 + 1, b0 = a0 + 2, b1 = a0 + 3;
                    // Seen from outside, back (a0) to front (a1) then round to the right: clockwise.
                    b.Quad(a0, a1, b1, b0, emitter);
                }
            }
        }

        // A ring round the barrel's axis at z: a torus of `radius` and `tube`.
        private static void Torus(Builder b, float z, float radius, float tube, bool emitter, int tubeSides = 10)
        {
            int first = b.Vertices.Count;
            for (int s = 0; s <= Around; s++)
            for (int k = 0; k <= tubeSides; k++)
            {
                float a = 2 * Mathf.PI * s / Around, t = 2 * Mathf.PI * k / tubeSides;
                var radial = new Vector3(Mathf.Sin(a), Mathf.Cos(a), 0);
                var normal = radial * Mathf.Cos(t) + Vector3.forward * Mathf.Sin(t);
                b.Add(new Vector3(0, Axis, z) + radial * radius + normal * tube, normal, false);
            }
            for (int s = 0; s < Around; s++)
            for (int k = 0; k < tubeSides; k++)
            {
                int a0 = first + s * (tubeSides + 1) + k, a1 = a0 + 1, b0 = a0 + tubeSides + 1, b1 = b0 + 1;
                b.Quad(a0, b0, b1, a1, emitter);
            }
        }

        /// <summary>
        /// A rounded bar swept along a path in the gun's middle plane (x = 0): its section (half `width` across, half `depth`
        /// along the path's side, corners rounded like a superellipse) at each point, `scale` there, its sides white where
        /// `white` says; the ends closed when asked.
        /// </summary>
        private static void Sweep(Builder b, IReadOnlyList<Vector3> path, float width, float depth, Func<int, float> scale, Func<int, bool> white,
            bool capStart, bool capEnd, int sides = 16)
        {
            var section = new Vector2[sides]; var sectionNormals = new Vector2[sides];
            for (int k = 0; k < sides; k++)
            {
                float a = 2 * Mathf.PI * k / sides, c = Mathf.Cos(a), s = Mathf.Sin(a);
                // A superellipse of exponent 4: |x|^4 + |y|^4 = 1.
                float x = Mathf.Sign(c) * Mathf.Sqrt(Mathf.Abs(c)), y = Mathf.Sign(s) * Mathf.Sqrt(Mathf.Abs(s));
                section[k] = new Vector2(x * width, y * depth);
                sectionNormals[k] = new Vector2(Mathf.Sign(x) * Mathf.Pow(Mathf.Abs(x), 3) / width, Mathf.Sign(y) * Mathf.Pow(Mathf.Abs(y), 3) / depth).normalized;
            }
            int rings = path.Count;
            var firsts = new int[rings];
            Vector3 Side(int i)
            {
                var along = (path[Mathf.Min(i + 1, rings - 1)] - path[Mathf.Max(i - 1, 0)]).normalized;
                return Vector3.Cross(Vector3.right, along).normalized;
            }
            for (int i = 0; i < rings; i++)
            {
                firsts[i] = b.Vertices.Count;
                var side = Side(i);
                for (int k = 0; k < sides; k++)
                {
                    var o = section[k] * scale(i);
                    b.Add(path[i] + Vector3.right * o.x + side * o.y, Vector3.right * sectionNormals[k].x + side * sectionNormals[k].y, white(i));
                }
            }
            for (int i = 0; i + 1 < rings; i++)
            for (int k = 0; k < sides; k++)
            {
                int a0 = firsts[i] + k, a1 = firsts[i] + (k + 1) % sides, b0 = firsts[i + 1] + k, b1 = firsts[i + 1] + (k + 1) % sides;
                b.Quad(a0, b0, b1, a1);
            }
            void Cap(int i, bool start)
            {
                var along = (path[Mathf.Min(i + 1, rings - 1)] - path[Mathf.Max(i - 1, 0)]).normalized * (start ? -1 : 1);
                int middle = b.Add(path[i], along, white(i));
                var rim = new int[sides];
                var side = Side(i);
                for (int k = 0; k < sides; k++)
                {
                    var o = section[k] * scale(i);
                    rim[k] = b.Add(path[i] + Vector3.right * o.x + side * o.y, along, white(i));
                }
                for (int k = 0; k < sides; k++)
                {
                    if (start) b.Triangle(middle, rim[k], rim[(k + 1) % sides]);
                    else b.Triangle(middle, rim[(k + 1) % sides], rim[k]);
                }
            }
            if (capStart) Cap(0, true);
            if (capEnd) Cap(rings - 1, false);
        }

        // A white oval on a ball (centre, radius) round `direction`, `width` by `height` degrees across.
        private static void Spot(Builder b, Vector3 centre, float radius, Vector3 direction, float width, float height, int sides = 14)
        {
            var across = Vector3.Cross(Vector3.up, direction).normalized;
            var up = Vector3.Cross(direction, across).normalized;
            int middle = b.Add(centre + direction * radius, direction, true);
            int first = b.Vertices.Count;
            for (int k = 0; k < sides; k++)
            {
                float t = 2 * Mathf.PI * k / sides;
                var d = (direction + across * Mathf.Tan(width / 2 * Mathf.Deg2Rad) * Mathf.Cos(t) + up * Mathf.Tan(height / 2 * Mathf.Deg2Rad) * Mathf.Sin(t)).normalized;
                b.Add(centre + d * radius, d, true);
            }
            for (int k = 0; k < sides; k++) b.Triangle(middle, first + k, first + (k + 1) % sides);
        }

        // Points along a quadratic curve a-c-b in the middle plane.
        private static List<Vector3> Bezier(Vector3 a, Vector3 control, Vector3 b, int steps) =>
            Enumerable.Range(0, steps + 1).Select(i => { float t = i / (float)steps; return (1 - t) * (1 - t) * a + 2 * (1 - t) * t * control + t * t * b; }).ToList();

        // Every triangle facing the way its vertices' normals do (Unity's front faces turn clockwise as seen, their cross
        // product toward the viewer).
        private static void Outward(Builder b)
        {
            foreach (var list in new[] { b.Body, b.Emitter })
            for (int t = 0; t + 2 < list.Count; t += 3)
            {
                Vector3 p = b.Vertices[list[t]], q = b.Vertices[list[t + 1]], r = b.Vertices[list[t + 2]];
                var normal = b.Normals[list[t]] + b.Normals[list[t + 1]] + b.Normals[list[t + 2]];
                if (Vector3.Dot(Vector3.Cross(q - p, r - p), normal) < 0) (list[t + 1], list[t + 2]) = (list[t + 2], list[t + 1]);
            }
        }

        /// <summary>The gun's model mesh, made once in <paramref name="folder"/> ("Avatar gun model.asset").</summary>
        internal static Mesh ModelMesh(string folder)
        {
            var b = new Builder();
            // The barrel, back to front: N4N0's black ball, the neck and its stroke, three fins (white rims), the bell, the
            // dish round the lens.
            var profile = new List<Ring>();
            const float ballZ = -.03f, ball = .03f;
            for (int k = 0; k <= 8; k++)
            {
                // From the back pole round to 58° before the ball's middle, where the neck leaves it.
                float a = Mathf.Deg2Rad * Mathf.Lerp(180, 58, k / 8f);
                profile.Add(new Ring(ballZ + Mathf.Cos(a) * ball, Mathf.Sin(a) * ball));
            }
            // The neck's outline: a waist, a slight swell, narrowing to the fins' core.
            var neck = new[] { new Vector2(profile[profile.Count - 1].Z, profile[profile.Count - 1].R), new Vector2(-.004f, .0178f), new Vector2(.006f, .0163f),
                new Vector2(.019f, .0182f), new Vector2(.031f, .0178f), new Vector2(.039f, .0158f) };
            float Neck(float z)
            {
                for (int i = 0; i + 1 < neck.Length; i++)
                    if (z <= neck[i + 1].x) return Mathf.Lerp(neck[i].y, neck[i + 1].y, Mathf.InverseLerp(neck[i].x, neck[i + 1].x, z));
                return neck[neck.Length - 1].y;
            }
            // A stroke round the neck: a thin raised band.
            void NeckStroke(float from, float to)
            {
                profile.Add(new Ring(from, Neck(from))); profile.Add(new Ring(from, Neck(from) + Lift, true));
                profile.Add(new Ring(to, Neck(to) + Lift, true)); profile.Add(new Ring(to, Neck(to)));
            }
            profile.Add(new Ring(-.004f, Neck(-.004f)));
            NeckStroke(.0095f, .0113f);
            foreach (float z in new[] { .019f, .031f, .039f }) profile.Add(new Ring(z, Neck(z)));
            // Fins: out, a thin white rim, back in; each a little smaller than the one before.
            const float core = .0158f, finThickness = .0032f;
            float z0 = .041f;
            foreach (float fin in new[] { .032f, .0295f, .027f })
            {
                // A piece takes the colour of the point it starts from: only the rim is white.
                profile.Add(new Ring(z0, core));
                profile.Add(new Ring(z0, fin, true));
                profile.Add(new Ring(z0 + finThickness, fin));
                profile.Add(new Ring(z0 + finThickness, core));
                z0 += .0115f;
            }
            // The bell flaring to the muzzle, and the dish inside it.
            profile.Add(new Ring(z0 - .0115f + finThickness + .004f, core - .001f));
            profile.Add(new Ring(.082f, .0156f));
            profile.Add(new Ring(.092f, .0186f));
            profile.Add(new Ring(.0995f, .0226f));
            profile.Add(new Ring(Front, .0256f));
            profile.Add(new Ring(Front, .0222f));
            profile.Add(new Ring(Front - .006f, .0165f));
            profile.Add(new Ring(Front - .0105f, .009f));
            profile.Add(new Ring(Front - .0105f, 0));
            // The back pole: the ball's first point has no radius.
            Lathe(b, profile);

            // N4N0's eyes on the ball, looking back at whoever holds the gun.
            foreach (float side in new[] { -1f, 1f }) Spot(b, new Vector3(0, Axis, ballZ), ball + Lift, Quaternion.Euler(-10, 180 + 22 * side, 0) * Vector3.forward, 8, 15);

            // The emitter: the ring on the lip and the lens in the dish, both on the second material.
            Torus(b, Front + .0004f, .0239f, .0026f, emitter: true);
            Lathe(b, new[] { new Ring(Front - .0104f, .0095f), new Ring(Front - .0075f, .0078f), new Ring(Front - .0055f, .0045f), new Ring(Front - .0049f, 0) }, emitter: true);

            // The grip: raked back, a white band near its foot, closed at the bottom.
            var gripTop = new Vector3(0, Axis - .012f, -.017f);
            var gripFoot = new Vector3(0, -.052f, -.035f);
            var grip = Enumerable.Range(0, 9).Select(i => Vector3.Lerp(gripTop, gripFoot, i / 8f)).ToList();
            grip.Insert(7, Vector3.Lerp(gripTop, gripFoot, 6.4f / 8));
            grip.Insert(8, Vector3.Lerp(gripTop, gripFoot, 6.6f / 8));
            grip.Insert(9, Vector3.Lerp(gripTop, gripFoot, 7.2f / 8));
            grip.Insert(10, Vector3.Lerp(gripTop, gripFoot, 7.4f / 8));
            Sweep(b, grip, .012f, .0165f, i => i >= 8 && i <= 9 ? 1.06f : 1 + .05f * Mathf.Clamp01((i - 3) / 6f), i => i >= 8 && i <= 9, false, true);

            // The trigger guard: from under the body round to the grip's front, and the white trigger in it.
            var guard = Bezier(new Vector3(0, Axis - .019f, .03f), new Vector3(0, -.006f, .034f), new Vector3(0, -.006f, -.011f), 10);
            Sweep(b, guard, .0035f, .003f, _ => 1, _ => false, false, false, 10);
            var trigger = Bezier(new Vector3(0, Axis - .02f, .004f), new Vector3(0, .016f, .01f), new Vector3(0, .009f, .016f), 6);
            Sweep(b, trigger, .0028f, .0022f, i => 1 - .25f * i / 6f, _ => true, false, true, 8);

            // A white stroke along the top, from the ball to the fins.
            var top = Enumerable.Range(0, 7).Select(i => { float z = Mathf.Lerp(-.006f, .038f, i / 6f); return new Vector3(0, Axis + Neck(z) + .0008f, z); }).ToList();
            Sweep(b, top, .0016f, .0011f, _ => 1, _ => true, true, true, 8);

            Outward(b);
            var mesh = new Mesh { name = "Avatar gun model" };
            mesh.SetVertices(b.Vertices); mesh.SetNormals(b.Normals); mesh.SetUVs(0, b.Uvs);
            mesh.SetColors(Enumerable.Repeat(Color.white, b.Vertices.Count).ToList());
            mesh.subMeshCount = 2;
            mesh.SetTriangles(b.Body, 0); mesh.SetTriangles(b.Emitter, 1);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, folder + "/Avatar gun model.asset");
            return mesh;
        }

        /// <summary>
        /// The body's material in <paramref name="folder"/>, VRChat's mobile Standard Lite as N4N0's: black, its strokes white
        /// and glowing a little. The emitter's (<see cref="EmitterMaterials"/>) are dim (unloaded) and lit (loaded): the
        /// animator swaps them in the model's second slot.
        /// </summary>
        internal static Material BodyMaterial(string folder) => Standard(folder, "Avatar gun", m =>
        {
            m.SetTexture("_MainTex", N4N0Installer.Palette(folder + "/Avatar gun.png", N4N0Installer.ShellColour, N4N0Installer.PatternColour));
            m.SetTexture("_EmissionMap", N4N0Installer.Palette(folder + "/Avatar gun glow.png", Color.black, Color.white));
            m.SetColor("_EmissionColor", Color.white * .8f);
        });

        /// <summary>The emitter's materials in <paramref name="folder"/>, dim and lit (those already there kept).</summary>
        internal static (Material dim, Material lit) EmitterMaterials(string folder)
        {
            Material Get(string name, Color colour, Color glow) => AssetDatabase.LoadAssetAtPath<Material>(folder + "/" + name + ".mat") ?? Standard(folder, name, m =>
            {
                m.SetColor("_Color", colour);
                m.SetColor("_EmissionColor", glow);
            });
            // Lit: N4N0's green, its glow kept under 1 so that it keeps its hue.
            return (Get("Avatar gun emitter unloaded", new Color(.07f, .13f, .1f), EmitterColour * .05f),
                Get("Avatar gun emitter loaded", Color.Lerp(Color.white, EmitterColour, .4f), new Color(.2f, 1, .58f)));
        }

        // A Standard Lite material of the gun's, a little glossy, glowing where its glow says.
        private static Material Standard(string folder, string name, Action<Material> set)
        {
            var shader = Shader.Find(N4N0Installer.ModelShader);
            if (shader == null) throw new InvalidOperationException("VRChat's " + N4N0Installer.ModelShader + " shader is missing: update the VRChat SDK in Creator Companion.");
            var material = new Material(shader) { name = name };
            material.SetColor("_Color", Color.white);
            material.SetFloat("_Metallic", 0);
            material.SetFloat("_Glossiness", .6f);
            material.SetFloat("_SpecularHighlights", 1); material.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
            material.SetFloat("_GlossyReflections", 1); material.DisableKeyword("_GLOSSYREFLECTIONS_OFF");
            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            set(material);
            AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
            return material;
        }

        // ---- The beam ----

        private const string BeamShaderTemplate = "Packages/orbiters.toolkit/Editor/VRChat/AvatarGunBeam.shader.txt";
        // The beam's glow, half its width; the muzzle flash's and the impact's.
        private const float BeamHalf = .022f, FlashHalf = .05f, ImpactHalf = .09f;

        /// <summary>
        /// The shot's beam below <paramref name="gun"/>: one skinned mesh whose near end rides the muzzle and far end the
        /// ray's end (<paramref name="end"/>, where the raycast puts it), three crossed strips between them, a flash at the
        /// muzzle and one where the ray ends. Hidden until a shot; its shader (copied into the folder, "Avatar gun
        /// beam.shader") fades it out with the animator's <c>_Fire</c>.
        /// </summary>
        private static void AddBeam(Transform gun, Transform muzzle, Transform end, string folder)
        {
            var beam = Child(gun, "Beam");
            var vertices = new List<Vector3>(); var uv = new List<Vector2>(); var parts = new List<Vector2>(); var weights = new List<BoneWeight>();
            var triangles = new List<int>();
            // In the beam's space (the gun's): the muzzle's point and the end's.
            Vector3 near = beam.InverseTransformPoint(muzzle.position), far = beam.InverseTransformPoint(end.position);
            void Add(Vector3 p, Vector2 at, int part, int bone)
            {
                vertices.Add(p); uv.Add(at); parts.Add(new Vector2(part, 0)); weights.Add(new BoneWeight { boneIndex0 = bone, weight0 = 1 });
            }
            void Quad(Vector3 middle, Vector3 u, Vector3 v, int part, int bone)
            {
                int first = vertices.Count;
                Add(middle - u - v, new Vector2(0, 0), part, bone); Add(middle - u + v, new Vector2(0, 1), part, bone);
                Add(middle + u + v, new Vector2(1, 1), part, bone); Add(middle + u - v, new Vector2(1, 0), part, bone);
                triangles.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
            }
            for (int k = 0; k < 3; k++)
            {
                var across = Quaternion.AngleAxis(60 * k, Vector3.forward) * Vector3.right * BeamHalf;
                int first = vertices.Count;
                Add(near - across, new Vector2(0, 0), 0, 0); Add(far - across, new Vector2(0, 1), 0, 1);
                Add(far + across, new Vector2(1, 1), 0, 1); Add(near + across, new Vector2(1, 0), 0, 0);
                triangles.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
            }
            foreach (var (middle, half, part, bone) in new[] { (near, FlashHalf, 1, 0), (far, ImpactHalf, 2, 1) })
            {
                Quad(middle, Vector3.right * half, Vector3.up * half, part, bone);
                Quad(middle, Vector3.right * half, Vector3.forward * half, part, bone);
                Quad(middle, Vector3.up * half, Vector3.forward * half, part, bone);
            }
            var mesh = new Mesh { name = "Avatar gun beam" };
            mesh.SetVertices(vertices); mesh.SetUVs(0, uv); mesh.SetUVs(1, parts);
            mesh.SetNormals(vertices.Select(_ => Vector3.up).ToList());
            mesh.SetTriangles(triangles, 0);
            mesh.boneWeights = weights.ToArray();
            mesh.bindposes = new[] { muzzle.worldToLocalMatrix * beam.localToWorldMatrix, end.worldToLocalMatrix * beam.localToWorldMatrix };
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, folder + "/Avatar gun beam.asset");

            var renderer = beam.gameObject.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { muzzle, end }; renderer.rootBone = muzzle; renderer.quality = SkinQuality.Bone1;
            var shader = CopyShader(BeamShaderTemplate, folder + "/Avatar gun beam.shader");
            var material = new Material(shader) { name = "Avatar gun beam" };
            material.SetColor("_Color", EmitterColour);
            material.SetFloat("_Fire", 0);
            AssetDatabase.CreateAsset(material, folder + "/Avatar gun beam.mat");
            renderer.sharedMaterial = material;
            // The whole ray, from the muzzle out to its full range.
            renderer.localBounds = new Bounds(Vector3.forward * (Range / 2), new Vector3(1, 1, Range + 1));
            Quiet(renderer);
            beam.gameObject.SetActive(false);
        }
    }
}
