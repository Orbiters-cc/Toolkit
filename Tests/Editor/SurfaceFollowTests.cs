using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.SurfaceFollow;
using Orbiters.Toolkit.VRChat;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Tests
{
    public sealed class SurfaceFollowTests
    {
        private GameObject avatar;

        [TearDown]
        public void TearDown()
        {
            if (avatar != null) Object.DestroyImmediate(avatar);
        }

        // A flat body skinned to one bone, with "Bulge" pushing the middle 5 cm forward and "Far" moving only a far corner.
        private (SkinnedMeshRenderer body, MeshRenderer stud) Build(float currentBulge)
        {
            avatar = new GameObject("Avatar");
            var bone = new GameObject("Hips").transform;
            bone.SetParent(avatar.transform, false);
            bone.localPosition = new Vector3(0f, 1f, 0f);
            var bodyObject = new GameObject("Body");
            bodyObject.transform.SetParent(avatar.transform, false);
            bodyObject.transform.localPosition = bone.localPosition;
            var mesh = new Mesh { name = "Body" };
            const int n = 11;
            var vertices = new Vector3[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    vertices[y * n + x] = new Vector3((x - 5) * 0.05f, (y - 5) * 0.05f, 0f);
            var triangles = new System.Collections.Generic.List<int>();
            for (int y = 0; y < n - 1; y++)
                for (int x = 0; x < n - 1; x++)
                {
                    int i = y * n + x;
                    triangles.AddRange(new[] { i, i + n, i + 1, i + 1, i + n, i + n + 1 });
                }
            mesh.vertices = vertices;
            mesh.triangles = triangles.ToArray();
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, vertices.Length).ToArray();
            mesh.bindposes = new[] { bone.worldToLocalMatrix * bodyObject.transform.localToWorldMatrix };
            var bulge = vertices.Select(v => new Vector3(0f, 0f, Mathf.Max(0f, 0.05f - v.magnitude * 0.2f))).ToArray();
            mesh.AddBlendShapeFrame("Bulge", 100f, bulge, null, null);
            var far = vertices.Select((v, i) => i == 0 ? new Vector3(0f, 0f, 0.1f) : Vector3.zero).ToArray();
            mesh.AddBlendShapeFrame("Far", 100f, far, null, null);
            mesh.RecalculateBounds();
            var body = bodyObject.AddComponent<SkinnedMeshRenderer>();
            body.sharedMesh = mesh;
            body.bones = new[] { bone };
            body.rootBone = bone;
            body.SetBlendShapeWeight(0, currentBulge);

            // A 1 cm stud where the body is now, at the centre.
            var stud = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(stud.GetComponent<Collider>());
            stud.name = "Stud";
            stud.transform.SetParent(bone, false);
            stud.transform.localScale = Vector3.one * 0.01f;
            stud.transform.position = bone.position + new Vector3(0f, 0f, 0.05f * currentBulge / 100f);
            return (body, stud.GetComponent<MeshRenderer>());
        }

        [Test]
        public void StudFollowsTheShapeUnderItAndNotTheOthers()
        {
            var (body, stud) = Build(0f);
            var follow = stud.gameObject.AddComponent<OrbitersSurfaceFollow>();
            follow.body = body;
            var target = SurfaceFollow.Plan(follow).Single();
            Assert.That(target.Skipped, Is.Null);
            var shape = target.Shapes.Single();
            Assert.That(shape.Name, Is.EqualTo("Bulge"), "Only shapes that move the skin under the stud.");
            Assert.That(shape.Move.z, Is.EqualTo(0.05f).Within(0.002f));
        }

        [Test]
        public void BakedStudMovesWithTheBodyAndStaysPutAtTheCurrentWeight()
        {
            var (body, stud) = Build(40f);
            var studObject = stud.gameObject;
            Vector3 before = stud.bounds.center;
            var follow = studObject.AddComponent<OrbitersSurfaceFollow>();
            follow.body = body;
            var target = SurfaceFollow.Plan(follow).Single();
            var skinned = SurfaceFollow.Bake(target, body);
            Assert.That(studObject.GetComponent<MeshRenderer>(), Is.Null, "The plain mesh became a skinned one.");
            int index = skinned.sharedMesh.GetBlendShapeIndex("Bulge");
            Assert.That(index, Is.GreaterThanOrEqualTo(0));

            Vector3 Centre(float weight)
            {
                skinned.SetBlendShapeWeight(index, weight);
                var baked = new Mesh();
                skinned.BakeMesh(baked, true);
                // A baked mesh keeps the source's bounds (which span its blendshapes): average the vertices instead.
                var local = baked.vertices.Aggregate(Vector3.zero, (sum, v) => sum + v) / baked.vertexCount;
                Object.DestroyImmediate(baked);
                return skinned.transform.localToWorldMatrix.MultiplyPoint3x4(local);
            }
            Assert.That(Vector3.Distance(Centre(40f), before), Is.LessThan(0.0005f), "At the body's current weight it stays where it was.");
            Assert.That(Centre(100f).z - Centre(0f).z, Is.EqualTo(0.05f).Within(0.002f), "It moves as the skin under it.");
        }

        // The build step on a copy: the stud becomes skinned, its shape takes the body's weight, the component goes away.
        [Test]
        public void BuildAppliesAndRemovesTheComponent()
        {
            var (body, stud) = Build(40f);
            var studObject = stud.gameObject;
            var follow = avatar.AddComponent<OrbitersSurfaceFollow>();
            follow.scope = OrbitersSurfaceFollow.Scope.SmallAccessories;
            string message = SurfaceFollow.Apply(avatar);
            StringAssert.Contains("1 accessory follow", message);
            Assert.That(avatar.GetComponent<OrbitersSurfaceFollow>(), Is.Null);
            var skinned = studObject.GetComponent<SkinnedMeshRenderer>();
            Assert.That(skinned, Is.Not.Null);
            int index = skinned.sharedMesh.GetBlendShapeIndex("Bulge");
            Assert.That(skinned.GetBlendShapeWeight(index), Is.EqualTo(40f), "The body's weight is copied to the new shape.");
            Assert.That(skinned.sharedMesh.GetBlendShapeIndex("Far"), Is.LessThan(0));
        }

        [Test]
        public void SmallAccessoriesScopeSkipsFarAndLargeObjects()
        {
            var (body, stud) = Build(0f);
            var big = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(big.GetComponent<Collider>());
            big.transform.SetParent(avatar.transform, false);
            big.transform.localScale = Vector3.one * 0.5f;
            var far = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(far.GetComponent<Collider>());
            far.transform.SetParent(avatar.transform, false);
            far.transform.localScale = Vector3.one * 0.01f;
            far.transform.position = new Vector3(0f, 1f, 0.5f);
            var follow = avatar.AddComponent<OrbitersSurfaceFollow>();
            follow.scope = OrbitersSurfaceFollow.Scope.SmallAccessories;
            var plan = SurfaceFollow.Plan(follow);
            CollectionAssert.AreEquivalent(new[] { stud.name }, plan.Select(t => t.Renderer.name));
            Assert.That(SurfaceFollow.BodyOf(follow), Is.EqualTo(body));
        }
    }
}
