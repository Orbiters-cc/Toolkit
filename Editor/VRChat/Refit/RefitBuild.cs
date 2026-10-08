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
    /// controllers, after MCB's correctives (-9000) and mode locks (-8970), so curves those add (and the values the modes
    /// lock) are copied too.
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
        /// Links the captured shapes and copies the body's weights. Never stops the upload: what another build step took away
        /// (a mesh merged or removed, a blendshape optimized out) is left unlinked and reported in <paramref name="warnings"/>,
        /// because a piece of clothing that does not follow one body shape is better than an avatar that cannot be uploaded.
        /// A refitted mesh merged into another or renamed is found again by its generated blendshapes.
        /// </summary>
        public static BlendShapeSyncResult Apply(GameObject avatarRoot, List<string> warnings = null)
        {
            warnings ??= new List<string>();
            if (avatarRoot == null) return new BlendShapeSyncResult { Message = "ReFit: no avatar." };
            var links = Captured.TryGetValue(avatarRoot, out var captured) ? captured : Collect(avatarRoot);
            var copies = new List<BlendShapeCopy>();
            var renderers = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var link in links)
            {
                if (link.Body == null || link.Body.sharedMesh == null || !link.Body.transform.IsChildOf(avatarRoot.transform))
                {
                    warnings.Add("the body a refitted mesh was made for was removed during the build: its blendshapes do not follow the body.");
                    continue;
                }
                bool meshKept = link.Mesh != null && link.Mesh.sharedMesh != null && link.Mesh.transform.IsChildOf(avatarRoot.transform);
                foreach (var shape in link.Shapes)
                {
                    if (link.Body.sharedMesh.GetBlendShapeIndex(shape.source) < 0) continue;
                    var destination = meshKept && link.Mesh.sharedMesh.GetBlendShapeIndex(shape.generated) >= 0 ? link.Mesh
                        : renderers.FirstOrDefault(r => r != null && r != link.Body && r.sharedMesh != null && r.sharedMesh.GetBlendShapeIndex(shape.generated) >= 0);
                    if (destination == null)
                    {
                        if (Animated(avatarRoot, link.Body, shape.source))
                            warnings.Add("blendshape '" + shape.generated + "' of " + (link.Mesh != null ? link.Mesh.name : "a refitted mesh") +
                                " was removed during the build (a mesh or blendshape optimizer?) while the body's '" + shape.source +
                                "' is animated: that part will not follow the body. Keep refitted blendshapes when optimizing.");
                        continue;
                    }
                    copies.Add(new BlendShapeCopy { Source = link.Body, SourceShape = shape.source, Destination = destination, DestinationShape = shape.generated });
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
            // Never stops the upload: without the capture, the links are collected again from the build copy later.
            try { RefitBuild.Capture(avatarRoot); }
            catch (Exception ex) { Debug.LogWarning("[Orbiters] ReFit could not prepare its blendshape links: " + ex.Message); }
            return true;
        }
    }

    internal sealed class RefitLinkHook : IVRCSDKPreprocessAvatarCallback
    {
        // After VRCFury (-10000), MCB's correctives (-9000) and mode locks (-8970); before Follow Body Blendshapes (-8950) and attachments sync
        // their own shapes (-8900).
        public int callbackOrder => -8960;

        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            // Never stops the upload: what cannot be linked is reported, the rest is linked.
            var warnings = new List<string>();
            try
            {
                var result = RefitBuild.Apply(avatarRoot, warnings);
                if (result.Success) Debug.Log("[Orbiters] " + result.Message);
            }
            catch (Exception ex) { warnings.Add(ex.Message); }
            foreach (var warning in warnings.Distinct()) Debug.LogWarning("[Orbiters] ReFit: " + warning, avatarRoot);
            return true;
        }
    }
}
