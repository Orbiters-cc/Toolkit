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
        /// <summary>Orb › Call (local): the contacts on the panel, the chosen one, call or answer, decline or hang up.</summary>
        public const string CallMenu = "Orb/Call/Menu", CallPick = "Orb/Call/Pick", CallGo = "Orb/Call/Go", CallEnd = "Orb/Call/End";
        /// <summary>The text slots a person picks between on an orb's card: 4 costs 50 synced bits, 8 fills the panel twice as fast for 82.</summary>
        public static readonly IReadOnlyList<int> OfferedTextSync = new[] { 4, 8 };
        // The controller, parameters and menu of each choice of options, shared by every orb switched to it (they name no
        // object of their own: their curves follow paths below the orb).
        // A new menu or parameters get a new folder: orbs switched earlier keep the files they use.
        internal const string OptionsFolder = "Assets/Orbiters/Orb/Menu 3/";

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
                    Choose = index => SetOptions(target, OfferedTextSync[index], OptionsOf(target).walkBoost),
                },
                new AttachmentOption
                {
                    Label = "Add walk boost in menu",
                    Tooltip = "A Walk boost radial in Orb's menu: how much faster Orbiters desktop makes you walk in VR. No synced bits.",
                    IsToggle = true,
                    Choices = new[] { "Off", "On" },
                    ChoiceTooltips = new[] { "No radial", "Orb › Walk boost in your menu sets Orbiters desktop's walk boost" },
                    Current = current.walkBoost ? 1 : 0,
                    Choose = index => SetOptions(target, OptionsOf(target).textSync, index == 1),
                },
            };
        }).ToList());

        /// <summary>The synced bits an orb with <paramref name="textSync"/> text slots costs.</summary>
        public static int Bits(int textSync) => 1 + 8 + 1 + 8 + 8 * textSync;

        // A full page is Cells / N blocks, one per 300 ms step (Docs/orb.md).
        private static int SecondsPerPanel(int textSync) => Mathf.CeilToInt(Cells / textSync * .3f);

        /// <summary>The number of text slots of a placed orb, 0 when its VRCFury Full Controller can't be read.</summary>
        public static int TextSyncOf(Transform orb) => OptionsOf(orb).textSync;

        /// <summary>A placed orb's text slots (0 when its VRCFury Full Controller can't be read) and whether it has the Walk boost radial.</summary>
        public static (int textSync, bool walkBoost) OptionsOf(Transform orb)
        {
            var parameters = FullController(orb, out _)?.parameters;
            if (parameters == null) return (0, false);
            return (parameters.parameters.Count(p => p.name != null && p.name.StartsWith(TextSyncPrefix, StringComparison.Ordinal)), parameters.parameters.Any(p => p.name == WalkBoost));
        }

        /// <summary>Switches a placed orb to <paramref name="textSync"/> text slots, its Walk boost radial kept.</summary>
        public static void SetTextSync(Transform orb, int textSync) => SetOptions(orb, textSync, OptionsOf(orb).walkBoost);

        /// <summary>
        /// Switches a placed orb to <paramref name="textSync"/> text slots (4, 8 or 16), with or without the Walk boost
        /// radial: its VRCFury Full Controller takes the controller, parameters and menu made for that choice, its global
        /// parameters follow. Recorded with Undo; the orb's own files (a gallery's among them) are left as they are.
        /// </summary>
        public static void SetOptions(Transform orb, int textSync, bool walkBoost)
        {
            if (!TextSyncCounts.Contains(textSync)) throw new ArgumentOutOfRangeException(nameof(textSync), "An orb has 4, 8 or 16 text sync values.");
            var found = FullController(orb, out var component) ?? throw new InvalidOperationException("This orb has no VRCFury Full Controller: add it again.");
            if (OptionsOf(orb) == (textSync, walkBoost)) return;
            if (found.menuIndex < 0) throw new InvalidOperationException("This orb's menu was changed: add it again.");
            var (controller, parameters, menu) = Shared(textSync, walkBoost);
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
        private static (AnimatorController controller, VRCExpressionParameters parameters, VRCExpressionsMenu menu) Shared(int textSync, bool walkBoost)
        {
            string folder = OptionsFolder + "Text slots " + textSync + (walkBoost ? " + walk boost" : "");
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/Orb.controller");
            var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
            var menu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(folder + "/Menu.asset");
            if (controller != null && parameters != null && menu != null) return (controller, parameters, menu);
            if (VrcFury.Writer == null) throw new InvalidOperationException("Install VRCFury in Creator Companion to change Orb's options.");
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            System.IO.Directory.CreateDirectory(folder); AssetDatabase.ImportAsset(folder);
            var root = new GameObject("Orb options");
            try { BuildController(root, folder, textSync, walkBoost); }
            finally { Object.DestroyImmediate(root); }
            AssetDatabase.SaveAssets();
            return (AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/Orb.controller"), AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset"),
                AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(folder + "/Menu.asset"));
        }
    }
}
