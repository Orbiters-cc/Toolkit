using Orbiters.Toolkit.Editor.XRay;
using Orbiters.Toolkit.XRay;
using System;
using System.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Tests.XRay
{
    // An avatar whose hair accessory has its own armature, placed on the head bone (as Ultirex's spiky hair is): every
    // selection must resolve to the rig it belongs to, not to the first skinned mesh met while walking up.
    public static class XRayTargetFinderTests
    {
        public static void RunOrThrow()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var meshes = new[] { Triangle(), Triangle() };
            try
            {
                var avatar = new GameObject("Avatar");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(avatar, scene);
                var armature = Child(avatar.transform, "Armature");
                var hips = Child(armature, "Hips");
                var chest = Child(hips, "Chest");
                var head = Child(chest, "Head");
                var hand = Child(Child(chest, "LeftUpperArm"), "LeftHand");
                var handEnd = Child(hand, "LeftHand_end");
                var body = Skin(Child(avatar.transform, "Body"), meshes[0], hips, hips, chest, head, hand);

                var hairAccessory = Child(head, "Spiky hair");
                var hairArmature = Child(hairAccessory, "Armature");
                var hairRoot = Child(hairArmature, "HairRoot");
                var hairBone = Child(hairRoot, "Bone.001");
                var hairEnd = Child(hairBone, "Bone.001_end");
                var hair = Skin(Child(hairAccessory, "NurbsPath.001"), meshes[1], hairRoot, hairRoot, hairBone);

                ExpectRig(hand.gameObject, body, "an avatar bone");
                ExpectRig(head.gameObject, body, "the bone holding the accessory");
                ExpectRig(handEnd.gameObject, body, "an unskinned end bone of the avatar");
                ExpectRig(avatar, body, "the avatar root");
                ExpectRig(body.gameObject, body, "the body mesh object");
                ExpectRig(hairBone.gameObject, hair, "an accessory bone");
                ExpectRig(hairEnd.gameObject, hair, "an unskinned end bone of the accessory");
                ExpectRig(hairAccessory.gameObject, hair, "the accessory container");
                ExpectRig(hair.gameObject, hair, "the accessory mesh object");
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                foreach (var mesh in meshes) Object.DestroyImmediate(mesh);
            }
        }

        private static void ExpectRig(GameObject selected, SkinnedMeshRenderer expected, string what)
        {
            if (!XRayGizmoTargetFinder.TryResolveTarget(selected, out var target))
                throw new InvalidOperationException($"Selecting {what} ({selected.name}) resolved no rig.");
            if (target.Renderer != expected)
                throw new InvalidOperationException($"Selecting {what} ({selected.name}) resolved {target.DisplayName}/{target.Renderer.name}, expected {expected.name}.");
        }

        private static Transform Child(Transform parent, string name)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            child.localPosition = Vector3.up * 0.1f;
            return child;
        }

        private static SkinnedMeshRenderer Skin(Transform host, Mesh mesh, Transform rootBone, params Transform[] bones)
        {
            var renderer = host.gameObject.AddComponent<SkinnedMeshRenderer>();
            mesh.bindposes = bones.Select(b => b.worldToLocalMatrix * host.localToWorldMatrix).ToArray();
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, mesh.vertexCount).ToArray();
            renderer.sharedMesh = mesh;
            renderer.bones = bones;
            renderer.rootBone = rootBone;
            return renderer;
        }

        private static Mesh Triangle() => new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
    }
}
