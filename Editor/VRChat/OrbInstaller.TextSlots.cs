using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    public static partial class OrbInstaller
    {
        /// <summary>The optional Walk boost radial's parameter (local, saved): Orbiters desktop's walk boost, 0 off to 1 strongest.</summary>
        public const string WalkBoost = "Orb/WalkBoost";
        /// <summary>The menu's 3D picture button (local): Orbiters desktop takes a 3D picture with VRChat's camera.</summary>
        public const string Photo3D = "Orb/Photo3D";
        /// <summary>
        /// The menu's Menu toggle (local): Orb's menu open (the projected screen when the orb has one, else Orbiters
        /// desktop's SteamVR overlay); the desktop turns it off when the menu closes on its own. The one direct trigger.
        /// </summary>
        public const string Menu = "Orb/Menu";
        /// <summary>Orb › Call (local): the contacts on the panel, the chosen one, call or answer, decline or hang up.</summary>
        public const string CallMenu = "Orb/Call/Menu", CallPick = "Orb/Call/Pick", CallGo = "Orb/Call/Go", CallEnd = "Orb/Call/End";
        /// <summary>The text slots a person picks between on an orb's card: 4 costs 50 synced bits, 8 fills the panel twice as fast for 82.</summary>
        public static readonly IReadOnlyList<int> OfferedTextSync = new[] { 4, 8 };
        // The controller, parameters and menu of each choice of options, shared by every orb switched to it (they name no
        // object of their own: their curves follow paths below the orb).
        // A new menu or parameters get a new folder: orbs switched earlier keep the files they use.
        internal const string OptionsFolder = "Assets/Orbiters/Orb/Menu 10/";

        [InitializeOnLoadMethod]
        private static void RegisterOrbOptions() => AttachmentHooks.RegisterOptions(root => FindAll(root).SelectMany(orb =>
        {
            var current = OptionsOf(orb);
            var target = orb;
            return new[]
            {
                new AttachmentOption
                {
                    Label = "Text slots",
                    Tooltip = "How many characters of Orb's panel are sent at once. More fill the panel sooner and cost more of the avatar's synced parameter bits.",
                    Choices = OfferedTextSync.Select(n => n.ToString()).ToList(),
                    ChoiceTooltips = OfferedTextSync.Select(n => $"{n} slots: {Bits(n)} synced bits, a full panel in about {SecondsPerPanel(n)} s").ToList(),
                    Current = OfferedTextSync.ToList().IndexOf(current.textSync),
                    // 4 slots can't carry 128-byte thumbnails: they go back to 72.
                    Choose = index =>
                    {
                        var now = OptionsOf(target);
                        int textSync = OfferedTextSync[index];
                        SetOptions(target, textSync, now.walkBoost, now.replace, ScreenFits(textSync, now.thumbnailBytes) ? now.thumbnailBytes : DefaultThumbnailBytes, now.light);
                    },
                },
                new AttachmentOption
                {
                    Label = "Add walk boost in menu",
                    Tooltip = "A Walk boost radial in Orb's menu: how much faster Orbiters desktop makes you walk in VR. No synced bits.",
                    IsToggle = true,
                    Choices = new[] { "Off", "On" },
                    ChoiceTooltips = new[] { "No radial", "Orb › Walk boost in your menu sets Orbiters desktop's walk boost" },
                    Current = current.walkBoost ? 1 : 0,
                    Choose = index => { var now = OptionsOf(target); SetOptions(target, now.textSync, index == 1, now.replace, now.thumbnailBytes, now.light); },
                },
                new AttachmentOption
                {
                    Label = "Replace Orbiters overlay",
                    Tooltip = "Orb's menu (fast walk, YouTube, calls, a 3D picture) opens on a screen in front of you in VRChat, touched with your index finger, " +
                              "where people around see it too. In desktop mode it always does: look at a button (a small dot shows where) and left-click. " +
                              "In VR it opens in Orbiters desktop's SteamVR overlay unless this is on. No synced bits; its contacts are local and don't count in VRChat's performance rank.",
                    IsToggle = true,
                    Choices = new[] { "Off", "On" },
                    ChoiceTooltips = new[] { "In VR, Orb's menu opens in Orbiters desktop's SteamVR overlay", "In VR too, Orb's menu opens on a screen in front of you in VRChat" },
                    Current = current.replace ? 1 : 0,
                    Choose = index => { var now = OptionsOf(target); SetOptions(target, now.textSync, now.walkBoost, index == 1, now.thumbnailBytes, now.light); },
                },
                new AttachmentOption
                {
                    Label = "Thumbnail size",
                    Tooltip = "How many bytes each YouTube thumbnail on Orb's screen takes. Both sizes show in colour, blurred at first and " +
                              "sharper as the rest arrives. 128 B end sharper but take longer to send, and need 8 text slots. No synced bits either way.",
                    Choices = OfferedThumbnailBytes.Select(n => n + " B").ToList(),
                    ChoiceTooltips = OfferedThumbnailBytes.Select(n => $"{n} bytes a thumbnail: five of them sent in {Seconds(n, ScreenLocalValues, .084f)} s for you, " +
                                                                        $"{Seconds(n, DefaultTextSync, .3f)} s for people around").ToList(),
                    Current = OfferedThumbnailBytes.ToList().IndexOf(current.thumbnailBytes),
                    // 128 bytes need 8 text slots or more: 4 go up to 8.
                    Choose = index =>
                    {
                        var now = OptionsOf(target);
                        int bytes = OfferedThumbnailBytes[index];
                        SetOptions(target, ScreenFits(now.textSync, bytes) ? now.textSync : DefaultTextSync, now.walkBoost, now.replace, bytes, now.light);
                    },
                },
                new AttachmentOption
                {
                    Label = "Look",
                    Tooltip = "How Orb's screen looks in VRChat: Orbiters' own, or a green phosphor terminal on which text arriving a block " +
                              "at a time reads as typed out. Orbiters desktop's overlay has its own Look. No synced bits.",
                    Choices = new[] { "Modern", "Retro" },
                    ChoiceTooltips = new[] { "Orbiters' own look", "A green phosphor terminal, as on an old computer" },
                    ChoicePictures = LookPictures(),
                    Current = RetroOf(orb) ? 1 : 0,
                    Choose = index => SetLook(target, index == 1),
                },
                new AttachmentOption
                {
                    Label = "Light",
                    Tooltip = "Orb can become a light, for a dark world or a photo: on or off, eight colours and a brightness, from Orb's menu " +
                              "or Orb › Light in your menu. Everyone around sees it. Costs 17 synced bits, and any light makes the avatar's PC " +
                              "performance rank Poor (Quest leaves it out).",
                    IsToggle = true,
                    Choices = new[] { "Off", "On" },
                    ChoiceTooltips = new[] { "No light", "Orb › Light in your menu, and Light in Orb's menu" },
                    Current = current.light ? 1 : 0,
                    Choose = index => { var now = OptionsOf(target); SetOptions(target, now.textSync, now.walkBoost, now.replace, now.thumbnailBytes, index == 1); },
                },
            };
        }).ToList());

        /// <summary>The synced bits an orb with <paramref name="textSync"/> text slots costs.</summary>
        public static int Bits(int textSync) => 1 + 8 + 1 + 8 + 8 * textSync;

        // A full page is Cells / N blocks, one per 300 ms step (Docs/orb.md).
        private static int SecondsPerPanel(int textSync) => Mathf.CeilToInt(Cells / textSync * .3f);

        // Five thumbnails of `bytes` sent in blocks of `block` bytes, one per `step` seconds (Docs/orb.md, Screen).
        private static string Seconds(int bytes, int block, float step) => ((5 * bytes + block - 1) / block * step).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The number of text slots of a placed orb, 0 when its VRCFury Full Controller can't be read.</summary>
        public static int TextSyncOf(Transform orb) => OptionsOf(orb).textSync;

        /// <summary>
        /// A placed orb's text slots (0 when its VRCFury Full Controller can't be read), whether it has the Walk boost radial,
        /// whether its projected screen replaces Orbiters desktop's overlay in VR (<see cref="ScreenReplace"/>), the
        /// bytes of each thumbnail its screen keeps (<see cref="ScreenThumbnailBytes"/>; 72 for an orb made before it) and
        /// whether it has the light (<see cref="Light"/>).
        /// </summary>
        public static (int textSync, bool walkBoost, bool replace, int thumbnailBytes, bool light) OptionsOf(Transform orb)
        {
            var parameters = FullController(orb, out _)?.parameters;
            if (parameters == null) return (0, false, false, DefaultThumbnailBytes, false);
            var thumbnails = parameters.parameters.FirstOrDefault(p => p.name == ScreenThumbnailBytes);
            return (parameters.parameters.Count(p => p.name != null && p.name.StartsWith(TextSyncPrefix, StringComparison.Ordinal)), parameters.parameters.Any(p => p.name == WalkBoost),
                parameters.parameters.Any(p => p.name == ScreenReplace && p.defaultValue > .5f), thumbnails != null ? Mathf.RoundToInt(thumbnails.defaultValue) : DefaultThumbnailBytes,
                parameters.parameters.Any(p => p.name == Light));
        }

        /// <summary>Switches a placed orb to <paramref name="textSync"/> text slots, its other options kept.</summary>
        public static void SetTextSync(Transform orb, int textSync)
        {
            var now = OptionsOf(orb);
            SetOptions(orb, textSync, now.walkBoost, now.replace, now.thumbnailBytes, now.light);
        }

        /// <summary>
        /// Switches a placed orb to <paramref name="textSync"/> text slots (4, 8 or 16), with or without the Walk boost
        /// radial, its projected screen replacing Orbiters desktop's overlay in VR or not, keeping thumbnails of
        /// <paramref name="thumbnailBytes"/> (72 or 128; 128 needs 8 text slots or more), with or without the light
        /// (<paramref name="light"/>): its VRCFury Full Controller takes the controller, parameters and menu made for that
        /// choice and its global parameters follow (an orb made without a screen gets one), its screen the current material
        /// for that thumbnail size (an older screen's pages go too), and the light object comes or goes. Recorded with Undo;
        /// the orb's own files (a gallery's among them) are left as they are.
        /// <para>
        /// VRCFury reverts every override of its components in a prefab instance when an upload starts, so on an orb placed
        /// from a prefab the Full Controller is first replaced by a copy of the orb's own (the prefab's removed, which VRCFury
        /// keeps): the options would come back off after each upload otherwise.
        /// </para>
        /// </summary>
        public static void SetOptions(Transform orb, int textSync, bool walkBoost, bool replace = false, int thumbnailBytes = DefaultThumbnailBytes, bool light = false)
        {
            if (!TextSyncCounts.Contains(textSync)) throw new ArgumentOutOfRangeException(nameof(textSync), "An orb has 4, 8 or 16 text sync values.");
            if (!OfferedThumbnailBytes.Contains(thumbnailBytes)) throw new ArgumentOutOfRangeException(nameof(thumbnailBytes), "An orb's thumbnails are 72 or 128 bytes.");
            if (!ScreenFits(textSync, thumbnailBytes)) throw new ArgumentException(thumbnailBytes + "-byte thumbnails need 8 text slots or more.", nameof(thumbnailBytes));
            var found = FullController(orb, out var component) ?? throw new InvalidOperationException("This orb has no VRCFury Full Controller: add it again.");
            if (OptionsOf(orb) == (textSync, walkBoost, replace, thumbnailBytes, light) && HasScreen(orb) && ScreenDraws(orb, thumbnailBytes) && HasLight(orb) == light &&
                HasPointing(orb))
                return;
            if (found.menuIndex < 0) throw new InvalidOperationException("This orb's menu was changed: add it again.");
            AddScreen(orb, ScreenFolder, thumbnailBytes);
            SetScreenThumbnails(orb, thumbnailBytes);
            // A screen from before version 4 points with rays from now on, held to the avatar's fingertips.
            if (!HasPointing(orb))
            {
                AddPointing(orb);
                var avatar = orb.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
                var animator = avatar != null ? avatar.GetComponent<Animator>() : null;
                var head = orb.Find(Head);
                if (animator != null && animator.isHuman && head != null) BindPointers(orb, avatar.transform, animator, Mathf.Abs(head.lossyScale.x));
            }
            SetLightObject(orb, light);
            var (controller, parameters, menu) = Shared(textSync, walkBoost, replace, thumbnailBytes, light);
            component = OwnComponent(component);
            var serialized = new SerializedObject(component);
            Reference(serialized.FindProperty("content.controllers").GetArrayElementAtIndex(found.controllerIndex).FindPropertyRelative("controller"), controller);
            Reference(serialized.FindProperty("content.prms").GetArrayElementAtIndex(found.parametersIndex).FindPropertyRelative("parameters"), parameters);
            Reference(serialized.FindProperty("content.menus").GetArrayElementAtIndex(found.menuIndex).FindPropertyRelative("menu"), menu);
            var globals = serialized.FindProperty("content.globalParams");
            var names = parameters.parameters.Select(p => p.name).ToList();
            globals.arraySize = names.Count;
            for (int i = 0; i < names.Count; i++) globals.GetArrayElementAtIndex(i).stringValue = names[i];
            serialized.ApplyModifiedProperties();
            Undo.SetCurrentGroupName("Orb options");
            PrefabUtility.RecordPrefabInstancePropertyModifications(component);
        }

        // Whether the orb's screen draws this version's pages with thumbnails of this size.
        private static bool ScreenDraws(Transform orb, int thumbnailBytes)
        {
            var screen = orb.Find(ScreenObject);
            var renderer = screen != null ? screen.GetComponent<MeshRenderer>() : null;
            return renderer != null && ScreenCurrent(renderer.sharedMaterial, thumbnailBytes);
        }

        // The component itself, or (part of a prefab instance) a copy of it in its place, the prefab's removed.
        private static Component OwnComponent(Component component)
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(component)) return component;
            var copy = Undo.AddComponent(component.gameObject, component.GetType());
            EditorUtility.CopySerialized(component, copy);
            Undo.DestroyObjectImmediate(component);
            return copy;
        }

        private sealed class Found
        {
            public VRCExpressionParameters parameters;
            public int controllerIndex, parametersIndex, menuIndex = -1;
        }

        // The orb's Full Controller: the VRCFury component on it whose parameters hold Orb's text pointer, with its menu
        // (the one opening Orb's submenu).
        private static Found FullController(Transform orb, out Component component)
        {
            component = null;
            if (orb == null || VrcFury.Component == null) return null;
            foreach (var candidate in orb.GetComponents(VrcFury.Component))
            {
                var serialized = new SerializedObject(candidate);
                var prms = serialized.FindProperty("content.prms");
                var controllers = serialized.FindProperty("content.controllers");
                var menus = serialized.FindProperty("content.menus");
                if (prms == null || !prms.isArray || controllers == null || !controllers.isArray) continue;
                for (int i = 0; i < prms.arraySize; i++)
                {
                    var asset = prms.GetArrayElementAtIndex(i).FindPropertyRelative("parameters.objRef")?.objectReferenceValue as VRCExpressionParameters;
                    if (asset == null || asset.parameters == null || !asset.parameters.Any(p => p.name == TextPointer)) continue;
                    for (int c = 0; c < controllers.arraySize; c++)
                    {
                        if (!(controllers.GetArrayElementAtIndex(c).FindPropertyRelative("controller.objRef")?.objectReferenceValue is AnimatorController controller)) continue;
                        if (!controller.parameters.Any(p => p.name == TextPointer)) continue;
                        component = candidate;
                        int menuIndex = -1;
                        for (int m = 0; menus != null && menus.isArray && m < menus.arraySize; m++)
                        {
                            var menu = menus.GetArrayElementAtIndex(m).FindPropertyRelative("menu.objRef")?.objectReferenceValue as VRCExpressionsMenu;
                            if (menu != null && menu.controls.Any(x => x.name == MenuPath)) { menuIndex = m; break; }
                        }
                        return new Found { parameters = asset, controllerIndex = c, parametersIndex = i, menuIndex = menuIndex };
                    }
                }
            }
            return null;
        }

        // A VRCFury asset reference (GuidWrapper): the object and its "guid:fileID".
        private static void Reference(SerializedProperty wrapper, Object asset)
        {
            wrapper.FindPropertyRelative("objRef").objectReferenceValue = asset;
            var id = wrapper.FindPropertyRelative("id");
            if (id != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long local)) id.stringValue = guid + ":" + local;
        }

        // The controller, parameters and menu for a choice of options, made once in their shared folder.
        private static (AnimatorController controller, VRCExpressionParameters parameters, VRCExpressionsMenu menu) Shared(int textSync, bool walkBoost, bool replace,
            int thumbnailBytes, bool light)
        {
            string folder = OptionsFolder + "Text slots " + textSync + (walkBoost ? " + walk boost" : "") + (replace ? " + screen in VR" : "") +
                            (thumbnailBytes != DefaultThumbnailBytes ? " + " + thumbnailBytes + " B thumbnails" : "") + (light ? " + light" : "");
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/Orb.controller");
            var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
            var menu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(folder + "/Menu.asset");
            if (controller != null && parameters != null && menu != null) return (controller, parameters, menu);
            if (VrcFury.Writer == null) throw new InvalidOperationException("Install VRCFury in Creator Companion to change Orb's options.");
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            System.IO.Directory.CreateDirectory(folder); AssetDatabase.ImportAsset(folder);
            var root = new GameObject("Orb options");
            try { BuildController(root, folder, textSync, walkBoost, replace, thumbnailBytes, light); }
            finally { Object.DestroyImmediate(root); }
            AssetDatabase.SaveAssets();
            return (AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/Orb.controller"), AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset"),
                AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(folder + "/Menu.asset"));
        }
    }
}
