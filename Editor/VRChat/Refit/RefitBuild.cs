using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>
    /// At build, every animation of a body blendshape also drives the shapes generated from it on refitted meshes (exact
    /// copies of the curves, whatever animates them: gestures, menus, sliders, correctives). The refitted renderers are taken
    /// from the build copy before VRCFury (-10000) can merge or rename them; the links are applied after VRCFury built its
    /// controllers and after MCB's correctives (-9000), so curves those add are copied too.
    /// </summary>
    public static class RefitBuild
    {
        public sealed class Link
        {
            public SkinnedMeshRenderer Body, Mesh;
            public List<RefitShape> Shapes;
        }

        private static readonly ConditionalWeakTable<GameObject, List<Link>> Captured = new ConditionalWeakTable<GameObject, List<Link>>();
        private static readonly ConditionalWeakTable<GameObject, HashSet<(SkinnedMeshRenderer, string)>> Linked =
            new ConditionalWeakTable<GameObject, HashSet<(SkinnedMeshRenderer, string)>>();

        /// <summary>The applied refits of this (build copy of the) avatar whose body is part of it.</summary>
        public static List<Link> Collect(GameObject avatarRoot)
        {
            var links = new List<Link>();
            if (avatarRoot == null) return links;
            foreach (var record in avatarRoot.GetComponentsInChildren<OrbitersRefit>(true))
            {
                if (!record.Applied || record.shapes.Count == 0) continue;
                if (record.body == null || !record.body.transform.IsChildOf(avatarRoot.transform))
                {
                    Debug.LogWarning("[Orbiters] " + record.name + " was refitted for a body that is not part of " + avatarRoot.name +
                        ": its blendshapes will not follow the body's animations.", record);
                    continue;
                }
                links.Add(new Link { Body = record.body, Mesh = record.GetComponent<SkinnedMeshRenderer>(), Shapes = record.shapes.ToList() });
            }
            return links;
        }

        public static void Capture(GameObject avatarRoot)
        {
            if (avatarRoot == null) return;
            var links = Collect(avatarRoot);
            Captured.Remove(avatarRoot);
            Captured.Add(avatarRoot, links);
            // Without VRCFury nothing else builds private controllers: Toolkit copies them so the links can be written.
            if (links.Count > 0 && !VrcFury.Builds(avatarRoot)) AttachmentAnimationBuild.Prepare(avatarRoot);
        }

        /// <summary>
        /// Links the captured shapes and copies the body's weights. A generated shape removed during the build (a blendshape
        /// optimizer) while its body shape is animated fails the build: the clothing would stop following the body.
        /// </summary>
        public static BlendShapeSyncResult Apply(GameObject avatarRoot)
        {
            var links = avatarRoot != null && Captured.TryGetValue(avatarRoot, out var captured) ? captured : Collect(avatarRoot);
            var copies = new List<BlendShapeCopy>();
            foreach (var link in links)
            {
                if (link.Body == null || link.Mesh == null || link.Body.sharedMesh == null || link.Mesh.sharedMesh == null ||
                    !link.Body.transform.IsChildOf(avatarRoot.transform) || !link.Mesh.transform.IsChildOf(avatarRoot.transform))
                    throw new InvalidOperationException("A refitted mesh or its body was removed during the build; its blendshapes cannot follow the body.");
                foreach (var shape in link.Shapes)
                {
                    if (link.Body.sharedMesh.GetBlendShapeIndex(shape.source) < 0) continue;
                    if (link.Mesh.sharedMesh.GetBlendShapeIndex(shape.generated) < 0)
                    {
                        if (Animated(avatarRoot, link.Body, shape.source))
                            throw new InvalidOperationException("Blendshape '" + shape.generated + "' of " + link.Mesh.name +
                                " was removed during the build, but '" + shape.source + "' of the body is animated. Keep refitted blendshapes when optimizing blendshapes.");
                        continue;
                    }
                    copies.Add(new BlendShapeCopy { Source = link.Body, SourceShape = shape.source, Destination = link.Mesh, DestinationShape = shape.generated });
                }
            }
            var linked = new HashSet<(SkinnedMeshRenderer, string)>(copies.Select(c => (c.Destination, c.DestinationShape)));
            Linked.Remove(avatarRoot);
            Linked.Add(avatarRoot, linked);
            if (copies.Count == 0) return new BlendShapeSyncResult { Message = "ReFit: no refitted blendshape to link." };
            return BlendShapeSync.Apply(avatarRoot, copies, "ReFit");
        }

        /// <summary>Shapes <see cref="Apply"/> linked in this build: other syncs leave them alone.</summary>
        public static bool IsLinked(GameObject avatarRoot, SkinnedMeshRenderer mesh, string shape) =>
            avatarRoot != null && Linked.TryGetValue(avatarRoot, out var linked) && linked.Contains((mesh, shape));

        private static bool Animated(GameObject avatarRoot, SkinnedMeshRenderer body, string shape)
        {
            string path = AnimationUtility.CalculateTransformPath(body.transform, avatarRoot.transform), property = "blendShape." + shape;
            return BlendShapeLinkEngine.CollectBuiltControllers(avatarRoot).SelectMany(c => c.animationClips).Where(c => c != null).Distinct()
                .Any(clip => AnimationUtility.GetCurveBindings(clip).Any(b => b.type == typeof(SkinnedMeshRenderer) && b.path == path && b.propertyName == property));
        }
    }

    internal sealed class RefitCaptureHook : IVRCSDKPreprocessAvatarCallback
    {
        // Before My Avatar attachments move meshes (-10100) and VRCFury (-10000) can merge or rename them.
        public int callbackOrder => -10110;

        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            try
            {
                RefitBuild.Capture(avatarRoot);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Orbiters] Refitted blendshapes: " + ex.Message);
                return false;
            }
        }
    }

    internal sealed class RefitLinkHook : IVRCSDKPreprocessAvatarCallback
    {
        // After VRCFury (-10000) and MCB's correctives (-9000); before Follow Body Blendshapes (-8950) and attachments sync
        // their own shapes (-8900).
        public int callbackOrder => -8960;

        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            try
            {
                var result = RefitBuild.Apply(avatarRoot);
                if (result.Success) Debug.Log("[Orbiters] " + result.Message);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Orbiters] Refitted blendshapes: " + ex.Message);
                return false;
            }
        }
    }
}
