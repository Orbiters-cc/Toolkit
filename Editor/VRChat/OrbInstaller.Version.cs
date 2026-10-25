using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// N4N0's own version (its release in My Avatar's gallery, which installs it from "Assets/Orbiters/N4N0 1.0.9/"), noted
    /// as an avatar's build starts, before VRCFury and the SDK take the editor's data away: the avatar manifest says it
    /// (<c>orb.version</c>), and N4N0's menu shows it at the bottom of its Home. And N4N0's pointer: the dot where its owner
    /// points, a plain dot rather than N4N0's face.
    /// </summary>
    public static partial class OrbInstaller
    {
        private static readonly Regex GalleryFolder = new Regex(@"N4N0 ([0-9]+(?:\.[0-9]+){1,3})/[^/]*$");
        // The builds' N4N0 versions, by the built copy (the manifest is written at the build's end).
        private static readonly Dictionary<int, string> builtVersions = new Dictionary<int, string>();

        /// <summary>A placed N4N0's version, from the gallery folder its prefab came from; null when it came from elsewhere.</summary>
        internal static string VersionOf(Transform orb)
        {
            var attachment = orb != null ? orb.GetComponent<OrbitersAttachment>() : null;
            if (attachment == null) return null;
            foreach (var path in new[] { attachment.variant, AssetDatabase.GUIDToAssetPath(attachment.source ?? "") })
            {
                var match = GalleryFolder.Match((path ?? "").Replace('\\', '/'));
                if (match.Success) return match.Groups[1].Value;
            }
            return null;
        }

        /// <summary>Notes the version of the N4N0 on <paramref name="avatar"/>, a copy being built.</summary>
        internal static void NoteBuiltVersion(GameObject avatar)
        {
            if (avatar == null) return;
            var version = FindAll(avatar).Select(VersionOf).FirstOrDefault(v => v != null);
            if (version != null) builtVersions[avatar.GetInstanceID()] = version;
            else builtVersions.Remove(avatar.GetInstanceID());
        }

        /// <summary>The version noted for <paramref name="avatar"/>'s build, or null.</summary>
        internal static string BuiltVersion(GameObject avatar) =>
            avatar != null && builtVersions.TryGetValue(avatar.GetInstanceID(), out var version) ? version : null;

        private const string DotShaderTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbDot.shader.txt";

        /// <summary>
        /// N4N0's pointer material in <paramref name="folder"/> (made once): a white dot ringed in green, over everything
        /// (<paramref name="beam"/> false: the dot itself) or in the scene (a ray's beam).
        /// </summary>
        internal static Material PointerMaterial(string folder, bool beam)
        {
            string name = beam ? "N4N0 pointer beam" : "N4N0 pointer";
            string path = folder + "/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null && material.shader != null && material.shader.isSupported) return material;
            if (!AssetDatabase.IsValidFolder(folder))
            {
                System.IO.Directory.CreateDirectory(folder);
                AssetDatabase.ImportAsset(folder);
            }
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(folder + "/N4N0 dot.shader") ?? CopyShader(DotShaderTemplate, folder + "/N4N0 dot.shader");
            material = new Material(shader) { name = name };
            material.SetFloat("_ZTest", (float)(beam ? CompareFunction.LessEqual : CompareFunction.Always));
            material.renderQueue = (int)RenderQueue.Overlay;
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();
            return material;
        }
    }
}
