using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>
    /// Last build step: meshes, materials and the textures they show that a build step created in memory (twist splits,
    /// followed accessories, resized texture copies...) become build data. The SDK saves the build copy as a prefab, which drops in-memory objects: the avatar would upload
    /// without them (invisible renderers). The build data of every step is written to the disk here, once.
    /// </summary>
    internal sealed class BuildCopyAssetsHook : IVRCSDKPreprocessAvatarCallback
    {
        // After every avatar tool, Poiyomi's material locking (4, 100) included.
        public int callbackOrder => 10000;

        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            // Play mode keeps the copy in memory; nothing is saved.
            if (EditorApplication.isPlayingOrWillChangePlaymode) return true;
            AttachmentAnimationBuild.Keep(avatarRoot, InMemory(avatarRoot));
            // The build data every step added to, written once.
            AttachmentAnimationBuild.Save(avatarRoot);
            return true;
        }

        internal static IEnumerable<Object> InMemory(GameObject avatarRoot)
        {
            var objects = new List<Object>();
            foreach (var renderer in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is SkinnedMeshRenderer skinned) objects.Add(skinned.sharedMesh);
                else if (renderer.TryGetComponent<MeshFilter>(out var filter)) objects.Add(filter.sharedMesh);
                objects.AddRange(renderer.sharedMaterials);
                foreach (var material in renderer.sharedMaterials)
                    if (material != null && !EditorUtility.IsPersistent(material))
                        objects.AddRange(material.GetTexturePropertyNameIDs().Select(material.GetTexture));
            }
            // Editor-only objects (previews, gizmos) stay out of the build.
            return objects.Where(o => o != null && !EditorUtility.IsPersistent(o) && (o.hideFlags & HideFlags.DontSaveInBuild) == 0)
                .Distinct();
        }
    }
}
