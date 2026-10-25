using System.Collections.Generic;
using System.Linq;
using com.vrcfury.api;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRCFury
{
    /// <summary>Creates VRCFury components through VRCFury's public API, with Undo.</summary>
    [InitializeOnLoad]
    internal sealed class VrcFuryWriter : VRChat.VrcFury.IWriter
    {
        static VrcFuryWriter() => VRChat.VrcFury.Writer = new VrcFuryWriter();

        public Component ArmatureLink(GameObject host, GameObject from, HumanBodyBones toBone, GameObject toObject)
        {
            return Created(host, () =>
            {
                var link = FuryComponents.CreateArmatureLink(host);
                link.LinkFrom(from);
                if (toObject != null) link.LinkTo(toObject); else link.LinkTo(toBone);
                link.SetRecursive(true);
            });
        }

        public Component Toggle(GameObject host, string menuPath, GameObject target, bool defaultOn)
        {
            return Created(host, () =>
            {
                var toggle = FuryComponents.CreateToggle(host);
                toggle.SetMenuPath(menuPath);
                toggle.SetSaved();
                if (defaultOn) toggle.SetDefaultOn();
                toggle.GetActions().AddTurnOn(target);
            }, DefaultIcon);
        }

        public Component ShapeChoice(GameObject host, string menuPath, string exclusiveTag, Renderer renderer, string shape)
        {
            var before = host.GetComponents(VRChat.VrcFury.Component).ToList();
            var toggle = FuryComponents.CreateToggle(host);
            toggle.SetMenuPath(menuPath);
            toggle.SetSaved();
            toggle.AddExclusiveTag(exclusiveTag);
            if (shape == null) toggle.SetExclusiveOffState();
            else toggle.GetActions().AddBlendshape(shape, 100f, renderer);
            var added = host.GetComponents(VRChat.VrcFury.Component).FirstOrDefault(c => !before.Contains(c));
            if (added != null) DefaultIcon(added);
            return added;
        }

        private void DefaultIcon(Component toggle) => Icon(toggle, null);

        // VRCFury's public API sets no icon: set on the component's model.
        public void Icon(Component toggle, Texture2D icon)
        {
            if (toggle == null) return;
            using var serialized = new SerializedObject(toggle);
            var enable = serialized.FindProperty("content.enableIcon");
            var picture = serialized.FindProperty("content.icon.objRef");
            if (enable == null || picture == null) return;
            enable.boolValue = true;
            picture.objectReferenceValue = icon != null ? icon : VRChat.OrbitersMenuIcons.Toggle;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // The public API adds components without Undo: register the new one so Undo removes it.
        public Component FullController(GameObject host, RuntimeAnimatorController controller, ScriptableObject menu, ScriptableObject parameters,
            IEnumerable<string> globalParameters = null)
        {
            return Created(host, () =>
            {
                var full = FuryComponents.CreateFullController(host);
                full.AddController(controller);
                full.AddMenu((VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu)menu);
                full.AddParams((VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters)parameters);
                foreach (string name in globalParameters ?? Enumerable.Empty<string>()) full.AddGlobalParam(name);
            });
        }

        private static Component Created(GameObject host, System.Action create, System.Action<Component> finish = null)
        {
            var before = host.GetComponents(VRChat.VrcFury.Component).ToList();
            create();
            var added = host.GetComponents(VRChat.VrcFury.Component).FirstOrDefault(c => !before.Contains(c));
            if (added != null)
            {
                finish?.Invoke(added);
                Undo.RegisterCreatedObjectUndo(added, "Add VRCFury component");
                EditorUtility.SetDirty(added);
                PrefabUtility.RecordPrefabInstancePropertyModifications(added);
            }
            return added;
        }
    }
}
