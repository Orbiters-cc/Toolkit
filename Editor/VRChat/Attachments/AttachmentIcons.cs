using System;
using System.Collections.Generic;
using System.IO;
using Orbiters.Toolkit.Editor.Storage;
using Orbiters.Toolkit.Storage;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>
    /// The icon of the menu toggle made for an accessory its creator gave none: the accessory's picture as worn, as My
    /// Avatar's list and Mixer's Hierarchy show it (the model alone, without a background). It is a PNG beside the other
    /// files Orbiters generates for the avatar (<see cref="Folder"/>/&lt;avatar&gt;/&lt;accessory&gt;.png), imported as VRChat
    /// wants menu icons (at most <see cref="MaxSize"/> px, compressed, no mipmaps), and tracked as a generated file: it goes
    /// once no toggle shows it (the accessory removed), and comes back with an Undo. Each install makes its own (an update
    /// or a reinstall pictures the new copy). The picture is taken on the editor frame after the install, once the tools
    /// placing the accessory (fitting it to the avatar) are done; until then, and when there is nothing to picture, the
    /// toggle keeps Orbiters' default icon.
    /// </summary>
    public static class AttachmentIcons
    {
        public const string Folder = "Assets/Orbiters/Accessory Icons";
        public const int MaxSize = 128;

        private static readonly List<(OrbitersAttachment Attachment, Component Toggle)> Waiting = new List<(OrbitersAttachment, Component)>();
        private static bool hooked;

        /// <summary>Gives <paramref name="toggle"/> the picture of <paramref name="attachment"/> on the next editor frame.</summary>
        internal static void After(OrbitersAttachment attachment, Component toggle)
        {
            if (attachment == null || toggle == null || VrcFury.Writer == null) return;
            Waiting.Add((attachment, toggle));
            if (hooked) return;
            hooked = true;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            // Not while scripts compile or assets import: the picture is imported, and a reload may follow.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            Flush();
        }

        /// <summary>Makes the icons still waiting, now (tests; the editor does on the frame after an install).</summary>
        internal static void Flush()
        {
            EditorApplication.update -= Tick;
            hooked = false;
            var waiting = Waiting.ToArray();
            Waiting.Clear();
            foreach (var (attachment, toggle) in waiting)
            {
                // Undone meanwhile, or the accessory removed: nothing to picture.
                if (attachment == null || toggle == null || VrcFury.Writer == null) continue;
                try
                {
                    var icon = Make(attachment);
                    if (icon == null) continue;
                    VrcFury.Writer.Icon(toggle, icon);
                    AttachmentInstaller.Dirty(toggle);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Orbiters] The menu toggle of “" + attachment.name + "” keeps the default icon: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// A new menu icon asset picturing <paramref name="attachment"/> as it is worn now; null when it shows nothing (no
        /// mesh) or the picture could not be saved.
        /// </summary>
        public static Texture2D Make(OrbitersAttachment attachment)
        {
            if (attachment == null) return null;
            var picture = ModelThumbnails.Picture(attachment.gameObject, transparent: true);
            if (picture == null) return null;
            try
            {
                string folder = Folder + "/" + SafePaths.FileName(AvatarName(attachment), "Avatar");
                if (!AssetPaths.EnsureFolder(folder)) return null;
                string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + SafePaths.FileName(attachment.name, "Accessory") + ".png");
                File.WriteAllBytes(FileUtil.GetPhysicalPath(path), picture.EncodeToPNG());
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                if (AssetImporter.GetAtPath(path) is TextureImporter importer)
                {
                    // VRChat's menu draws icons over itself: alpha is transparency, no mipmaps, small and compressed.
                    importer.textureType = TextureImporterType.Default;
                    importer.alphaIsTransparency = true;
                    importer.mipmapEnabled = false;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.npotScale = TextureImporterNPOTScale.None;
                    importer.maxTextureSize = MaxSize;
                    importer.textureCompression = TextureImporterCompression.Compressed;
                    importer.SaveAndReimport();
                }
                GeneratedAssets.Track(path);
                return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
            finally
            {
                Object.DestroyImmediate(picture);
            }
        }

        private static string AvatarName(OrbitersAttachment attachment)
        {
            var descriptor = attachment.GetComponentInParent<VRCAvatarDescriptor>(true);
            return descriptor != null ? descriptor.gameObject.name : attachment.transform.root.name;
        }
    }
}
