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
        /// <summary>The text slots a person picks between on an orb's card: 4 costs 50 synced bits, 8 fills the panel twice as fast for 82.</summary>
        public static readonly IReadOnlyList<int> OfferedTextSync = new[] { 4, 8 };
        // The controller and parameters of each number of text slots, shared by every orb switched to it (they name no
        // object of their own: their curves follow paths below the orb).
        internal const string TextSlotsFolder = "Assets/Orbiters/Orb/Text slots ";

        [InitializeOnLoadMethod]
        private static void RegisterTextSlotsOption() => AttachmentHooks.RegisterOptions(root => FindAll(root).Select(orb =>
        {
            int current = TextSyncOf(orb);
            var target = orb;
            return new AttachmentOption
            {
                Label = "Text slots",
                Tooltip = "How many characters of Orb's panel are sent at once. More fill the panel sooner and cost more of the avatar's synced parameter bits.",
                Choices = OfferedTextSync.Select(n => n.ToString()).ToList(),
                ChoiceTooltips = OfferedTextSync.Select(n => $"{n} slots: {Bits(n)} synced bits, a full panel in about {SecondsPerPanel(n)} s").ToList(),
                Current = OfferedTextSync.ToList().IndexOf(current),
                Choose = index => SetTextSync(target, OfferedTextSync[index]),
            };
        }).ToList());

        /// <summary>The synced bits an orb with <paramref name="textSync"/> text slots costs.</summary>
        public static int Bits(int textSync) => 1 + 8 + 1 + 8 + 8 * textSync;

        // A full page is Cells / N blocks, one per 300 ms step (Docs/orb.md).
        private static int SecondsPerPanel(int textSync) => Mathf.CeilToInt(Cells / textSync * .3f);

        /// <summary>The number of text slots of a placed orb, 0 when its VRCFury Full Controller can't be read.</summary>
        public static int TextSyncOf(Transform orb)
        {
            var parameters = FullController(orb, out _)?.parameters;
            return parameters == null ? 0 : parameters.parameters.Count(p => p.name != null && p.name.StartsWith(TextSyncPrefix, StringComparison.Ordinal));
        }

        /// <summary>
        /// Switches a placed orb to <paramref name="textSync"/> text slots (4, 8 or 16): its VRCFury Full Controller takes
        /// the controller and parameters made for that number, its global parameters follow. Recorded with Undo; the orb's
        /// own files (a gallery's among them) are left as they are.
        /// </summary>
        public static void SetTextSync(Transform orb, int textSync)
        {
            if (!TextSyncCounts.Contains(textSync)) throw new ArgumentOutOfRangeException(nameof(textSync), "An orb has 4, 8 or 16 text sync values.");
            var found = FullController(orb, out var component) ?? throw new InvalidOperationException("This orb has no VRCFury Full Controller: add it again.");
            if (found.parameters.parameters.Count(p => p.name != null && p.name.StartsWith(TextSyncPrefix, StringComparison.Ordinal)) == textSync) return;
            var (controller, parameters) = SharedText(textSync);
            var serialized = new SerializedObject(component);
            Reference(serialized.FindProperty("content.controllers").GetArrayElementAtIndex(found.controllerIndex).FindPropertyRelative("controller"), controller);
            Reference(serialized.FindProperty("content.prms").GetArrayElementAtIndex(found.parametersIndex).FindPropertyRelative("parameters"), parameters);
            var globals = serialized.FindProperty("content.globalParams");
            var names = parameters.parameters.Select(p => p.name).ToList();
            globals.arraySize = names.Count;
            for (int i = 0; i < names.Count; i++) globals.GetArrayElementAtIndex(i).stringValue = names[i];
            serialized.ApplyModifiedProperties();
            Undo.SetCurrentGroupName("Orb text slots");
            PrefabUtility.RecordPrefabInstancePropertyModifications(component);
        }

        private sealed class Found
        {
            public VRCExpressionParameters parameters;
            public int controllerIndex, parametersIndex;
        }

        // The orb's Full Controller: the VRCFury component on it whose parameters hold Orb's text pointer.
        private static Found FullController(Transform orb, out Component component)
        {
            component = null;
            if (orb == null || VrcFury.Component == null) return null;
            foreach (var candidate in orb.GetComponents(VrcFury.Component))
            {
                var serialized = new SerializedObject(candidate);
                var prms = serialized.FindProperty("content.prms");
                var controllers = serialized.FindProperty("content.controllers");
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
                        return new Found { parameters = asset, controllerIndex = c, parametersIndex = i };
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

        // The controller and parameters for textSync text slots, made once in their shared folder.
        private static (AnimatorController controller, VRCExpressionParameters parameters) SharedText(int textSync)
        {
            string folder = TextSlotsFolder + textSync;
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/Orb.controller");
            var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
            if (controller != null && parameters != null) return (controller, parameters);
            if (VrcFury.Writer == null) throw new InvalidOperationException("Install VRCFury in Creator Companion to change Orb's text slots.");
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            System.IO.Directory.CreateDirectory(folder); AssetDatabase.ImportAsset(folder);
            var root = new GameObject("Orb text slots");
            try { BuildController(root, folder, textSync); }
            finally { Object.DestroyImmediate(root); }
            AssetDatabase.SaveAssets();
            return (AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/Orb.controller"), AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset"));
        }
    }
}
