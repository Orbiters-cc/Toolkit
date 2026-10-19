using Orbiters.Toolkit.Editor.XRay;
using Orbiters.Toolkit.XRay;
using Orbiters.Toolkit.Editor.VRChat.Posing;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDK3.Avatars.Components;

namespace Orbiters.Toolkit.Editor.VRChat.XRay
{
    /// <summary>
    /// Clothing, as in My Avatar's posing: clothing and accessories follow the avatar's pose as they will once built (VRCFury
    /// Armature Links, My Avatar attachments, matching bone names), and go back where they were when it is switched off.
    /// The avatar is the one selected, or the only one in the scene.
    /// </summary>
    internal static class XRayClothingControls
    {
        internal const string Description = "Clothing and accessories follow the avatar's pose as they will once built. Switch it off to put them back.";

        [InitializeOnLoadMethod]
        private static void Register() => XRayPosingExtensions.Register(BuildWindowControl);

        internal static Texture Icon => EditorGUIUtility.IconContent("d_Cloth Icon").image;

        internal static string Status => AccessoryPoseSync.Enabled
            ? AccessoryPoseSync.LastStatus
            : Avatar() == null ? "Select an avatar to preview its clothing." : "Off";

        internal static void SetEnabled(bool on)
        {
            if (!on) { AccessoryPoseSync.Disable(); return; }
            var avatar = Avatar();
            if (avatar != null) AccessoryPoseSync.Enable(avatar);
        }

        // The selected avatar (or the avatar of the selected object), else the scene's only avatar.
        internal static Transform Avatar()
        {
            var selected = Selection.activeGameObject;
            var descriptor = selected != null ? selected.GetComponentInParent<VRCAvatarDescriptor>(true) : null;
            if (descriptor != null) return descriptor.transform;
            var all = Object.FindObjectsOfType<VRCAvatarDescriptor>(false);
            return all.Length == 1 ? all[0].transform : null;
        }

        private static VisualElement BuildWindowControl()
        {
            var box = new VisualElement();
            var toggle = new Toggle("Clothing") { value = AccessoryPoseSync.Enabled };
            toggle.AddToClassList("xray-field");
            toggle.SetEnabled(!EditorApplication.isPlayingOrWillChangePlaymode);
            var help = new Label(Description);
            help.AddToClassList("xray-help");
            var status = new Label(Status);
            status.AddToClassList("xray-help");
            toggle.RegisterValueChangedCallback(e => SetEnabled(e.newValue));
            void Sync()
            {
                toggle.SetValueWithoutNotify(AccessoryPoseSync.Enabled);
                status.text = Status;
            }
            box.RegisterCallback<AttachToPanelEvent>(_ => { AccessoryPoseSync.Changed += Sync; Selection.selectionChanged += Sync; });
            box.RegisterCallback<DetachFromPanelEvent>(_ => { AccessoryPoseSync.Changed -= Sync; Selection.selectionChanged -= Sync; });
            box.Add(toggle);
            box.Add(status);
            box.Add(help);
            return box;
        }
    }

    [EditorToolbarElement(XRayPosingExtensions.ClothingToolbarId, typeof(SceneView))]
    internal sealed class XRayClothingToolbarToggle : EditorToolbarToggle
    {
        private bool listening;

        public XRayClothingToolbarToggle()
        {
            text = string.Empty;
            icon = XRayClothingControls.Icon as Texture2D;
            this.RegisterValueChangedCallback(e => XRayClothingControls.SetEnabled(e.newValue));
            RegisterCallback<AttachToPanelEvent>(_ => Attach());
            RegisterCallback<DetachFromPanelEvent>(_ => Detach());
            Attach();
            Sync();
        }

        private void Sync()
        {
            SetValueWithoutNotify(AccessoryPoseSync.Enabled);
            SetEnabled(!EditorApplication.isPlayingOrWillChangePlaymode && (AccessoryPoseSync.Enabled || XRayClothingControls.Avatar() != null));
            tooltip = "Clothing: " + XRayClothingControls.Status + "\n" + XRayClothingControls.Description;
        }

        private void Attach()
        {
            if (listening) return;
            listening = true;
            AccessoryPoseSync.Changed += Sync;
            Selection.selectionChanged += Sync;
        }

        private void Detach()
        {
            if (!listening) return;
            listening = false;
            AccessoryPoseSync.Changed -= Sync;
            Selection.selectionChanged -= Sync;
        }
    }
}
