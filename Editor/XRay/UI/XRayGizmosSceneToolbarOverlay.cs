using Orbiters.Toolkit.XRay;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor.XRay
{
    [Overlay(
        typeof(SceneView),
        "Orbiters/XRayGizmos/SceneToolbar",
        "Avatar Gizmos",
        true,
        defaultDockPosition = DockPosition.Top,
        defaultDockZone = DockZone.TopToolbar)]
    [Icon(XRayGizmosSceneToolbarIcons.BonesIconPath)]
    internal sealed class XRayGizmosSceneToolbarOverlay : ToolbarOverlay
    {
        public XRayGizmosSceneToolbarOverlay()
            : base(
                XRayBonesToolbarToggle.Id,
                XRayMirrorToolbarToggle.Id,
                XRayPosingExtensions.ClothingToolbarId,
                XRayWeightPaintToolbarToggle.Id,
                XRayMeshEdgesToolbarToggle.Id,
                XRayExtraToolbarToggle.Id)
        {
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class XRayBonesToolbarToggle : EditorToolbarDropdownToggle
    {
        public const string Id = "Orbiters/XRayGizmos/Bones";

        private bool isSyncing;
        private bool isListening;

        public XRayBonesToolbarToggle()
        {
            icon = XRayGizmosSceneToolbarIcons.LoadBonesIcon();
            this.RegisterValueChangedCallback(OnValueChanged);
            RegisterCallback<AttachToPanelEvent>(_ => Attach());
            RegisterCallback<DetachFromPanelEvent>(_ => Detach());
            dropdownClicked += ShowModeMenu;
            Attach();
            SyncFromServices();
        }

        private void OnValueChanged(ChangeEvent<bool> evt)
        {
            if (isSyncing)
            {
                return;
            }

            if (evt.newValue)
            {
                EnableBones(XRayGizmoTargetMode.WholeScene);
            }
            else
            {
                DisableBones();
            }
        }

        private void ShowModeMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(
                new GUIContent("Whole scene"),
                XRayGizmoService.Enabled && XRayGizmoService.TargetMode == XRayGizmoTargetMode.WholeScene,
                () => EnableBones(XRayGizmoTargetMode.WholeScene));
            menu.AddItem(
                new GUIContent("Selected object"),
                XRayGizmoService.Enabled && XRayGizmoService.TargetMode == XRayGizmoTargetMode.SelectedObject,
                () => EnableBones(XRayGizmoTargetMode.SelectedObject));
            menu.AddSeparator(string.Empty);

            if (XRayGizmoService.Enabled)
            {
                menu.AddItem(new GUIContent("Disable"), false, DisableBones);
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Disable"));
            }

            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Open Avatar Gizmos Window"), false, XRayGizmosWindow.Open);
            menu.DropDown(worldBound);
        }

        private static void EnableBones(XRayGizmoTargetMode mode)
        {
            XRayGizmoService.SetTargetMode(mode);
            XRayBonePickingService.SetEnabled(true);
            XRayGizmoService.SetEnabled(true);
        }

        private static void DisableBones()
        {
            XRayBonePickingService.SetEnabled(false);
            XRayGizmoService.SetEnabled(false);
        }

        private void SyncFromServices()
        {
            isSyncing = true;
            SetValueWithoutNotify(XRayGizmoService.Enabled);
            isSyncing = false;
            tooltip = BuildTooltip();
        }

        private static string BuildTooltip()
        {
            string scope = XRayGizmoService.TargetMode == XRayGizmoTargetMode.SelectedObject
                ? "selected object"
                : "whole scene";
            string picking = XRayBonePickingService.Enabled ? "clickable" : "not clickable";
            return $"Bones ({scope}, {picking}). Click to show whole-scene clickable bones.";
        }

        private void Attach()
        {
            if (isListening)
            {
                return;
            }

            isListening = true;
            XRayGizmoService.Changed += SyncFromServices;
            XRayBonePickingService.Changed += SyncFromServices;
        }

        private void Detach()
        {
            if (!isListening)
            {
                return;
            }

            isListening = false;
            XRayGizmoService.Changed -= SyncFromServices;
            XRayBonePickingService.Changed -= SyncFromServices;
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class XRayWeightPaintToolbarToggle : EditorToolbarToggle
    {
        public const string Id = "Orbiters/XRayGizmos/WeightPaint";

        private bool isSyncing;
        private bool isListening;

        public XRayWeightPaintToolbarToggle()
        {
            icon = XRayGizmosSceneToolbarIcons.LoadWeightPaintIcon();
            this.RegisterValueChangedCallback(OnValueChanged);
            RegisterCallback<AttachToPanelEvent>(_ => Attach());
            RegisterCallback<DetachFromPanelEvent>(_ => Detach());
            Attach();
            SyncFromServices();
        }

        private void OnValueChanged(ChangeEvent<bool> evt)
        {
            if (isSyncing)
            {
                return;
            }

            XRayWeightPaintService.SetEnabled(evt.newValue);
        }

        private void SyncFromServices()
        {
            isSyncing = true;
            SetValueWithoutNotify(XRayWeightPaintService.Enabled);
            isSyncing = false;
            tooltip = XRayWeightPaintService.Enabled
                ? XRayWeightPaintService.LastStatus ?? "Hide weight paint overlay."
                : "Show weight paint overlay for the selected bone or armature.";
        }

        private void Attach()
        {
            if (isListening)
            {
                return;
            }

            isListening = true;
            XRayWeightPaintService.Changed += SyncFromServices;
        }

        private void Detach()
        {
            if (!isListening)
            {
                return;
            }

            isListening = false;
            XRayWeightPaintService.Changed -= SyncFromServices;
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class XRayMeshEdgesToolbarToggle : EditorToolbarDropdownToggle
    {
        public const string Id = "Orbiters/XRayGizmos/MeshEdges";

        private bool isSyncing;
        private bool isListening;

        public XRayMeshEdgesToolbarToggle()
        {
            icon = XRayGizmosSceneToolbarIcons.LoadMeshEdgesIcon();
            this.RegisterValueChangedCallback(OnValueChanged);
            RegisterCallback<AttachToPanelEvent>(_ => Attach());
            RegisterCallback<DetachFromPanelEvent>(_ => Detach());
            dropdownClicked += ShowSettings;
            Attach();
            SyncFromServices();
        }

        private void OnValueChanged(ChangeEvent<bool> evt)
        {
            if (isSyncing)
            {
                return;
            }

            XRayMeshEdgeService.SetEnabled(evt.newValue);
        }

        private void ShowSettings()
        {
            UnityEditor.PopupWindow.Show(worldBound, new XRayMeshEdgesPopupContent());
        }

        private void SyncFromServices()
        {
            isSyncing = true;
            SetValueWithoutNotify(XRayMeshEdgeService.Enabled);
            isSyncing = false;
            tooltip = XRayMeshEdgeService.Enabled
                ? XRayMeshEdgeService.LastStatus ?? "Hide mesh polygon edges."
                : "Show mesh polygon edges for selected skinned meshes.";
        }

        private void Attach()
        {
            if (isListening)
            {
                return;
            }

            isListening = true;
            XRayMeshEdgeService.Changed += SyncFromServices;
        }

        private void Detach()
        {
            if (!isListening)
            {
                return;
            }

            isListening = false;
            XRayMeshEdgeService.Changed -= SyncFromServices;
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class XRayExtraToolbarToggle : EditorToolbarDropdownToggle
    {
        public const string Id = "Orbiters/XRayGizmos/Extra";

        private bool isSyncing;
        private bool isListening;

        public XRayExtraToolbarToggle()
        {
            icon = XRayGizmosSceneToolbarIcons.LoadExtraIcon();
            this.RegisterValueChangedCallback(OnValueChanged);
            RegisterCallback<AttachToPanelEvent>(_ => Attach());
            RegisterCallback<DetachFromPanelEvent>(_ => Detach());
            dropdownClicked += ShowMenu;
            Attach();
            SyncFromRegistry();
        }

        private void OnValueChanged(ChangeEvent<bool> evt)
        {
            if (isSyncing) return;
            XRayExternalGizmoRegistry.SetAll(evt.newValue);
        }

        private void ShowMenu()
        {
            var menu = new GenericMenu();
            var entries = XRayExternalGizmoRegistry.Entries;
            if (entries.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("No extra gizmos registered"));
            }
            else
            {
                foreach (var entry in entries)
                {
                    var label = string.IsNullOrEmpty(entry.Tooltip)
                        ? entry.DisplayName
                        : $"{entry.DisplayName} - {entry.Tooltip}";
                    menu.AddItem(new GUIContent(label), entry.IsEnabled, () =>
                    {
                        entry.SetEnabled(!entry.IsEnabled);
                        XRayExternalGizmoRegistry.NotifyChanged();
                        SceneView.RepaintAll();
                    });
                }

                menu.AddSeparator(string.Empty);
                menu.AddItem(new GUIContent("Enable all"), false, () => XRayExternalGizmoRegistry.SetAll(true));
                menu.AddItem(new GUIContent("Disable all"), false, () => XRayExternalGizmoRegistry.SetAll(false));
            }

            menu.DropDown(worldBound);
        }

        private void SyncFromRegistry()
        {
            isSyncing = true;
            SetValueWithoutNotify(XRayExternalGizmoRegistry.AnyEnabled);
            isSyncing = false;
            tooltip = XRayExternalGizmoRegistry.Entries.Count > 0
                ? "Extra scene gizmos from external tools."
                : "No extra scene gizmos are registered.";
        }

        private void Attach()
        {
            if (isListening) return;
            isListening = true;
            XRayExternalGizmoRegistry.Changed += SyncFromRegistry;
        }

        private void Detach()
        {
            if (!isListening) return;
            isListening = false;
            XRayExternalGizmoRegistry.Changed -= SyncFromRegistry;
        }
    }

    internal sealed class XRayMeshEdgesPopupContent : PopupWindowContent
    {
        public override Vector2 GetWindowSize()
        {
            return new Vector2(250f, 138f);
        }

        public override void OnGUI(Rect rect)
        {
            EditorGUILayout.Space(4f);

            EditorGUI.BeginChangeCheck();
            bool enabled = EditorGUILayout.Toggle("Show edges", XRayMeshEdgeService.Enabled);
            if (EditorGUI.EndChangeCheck())
            {
                XRayMeshEdgeService.SetEnabled(enabled);
            }

            EditorGUI.BeginChangeCheck();
            float alpha = EditorGUILayout.Slider("Opacity", XRayMeshEdgeService.EdgeAlpha, 0.05f, 1f);
            if (EditorGUI.EndChangeCheck())
            {
                XRayMeshEdgeService.SetEdgeAlpha(alpha);
            }

            EditorGUI.BeginChangeCheck();
            var color = EditorGUILayout.ColorField("Color", XRayMeshEdgeService.EdgeColor);
            if (EditorGUI.EndChangeCheck())
            {
                XRayMeshEdgeService.SetEdgeColor(color);
            }

            EditorGUI.BeginChangeCheck();
            bool seeThrough = EditorGUILayout.Toggle(
                new GUIContent("See through", "Show the edges of the whole mesh, back and hidden parts included"),
                XRayMeshEdgeService.SeeThrough);
            if (EditorGUI.EndChangeCheck())
            {
                XRayMeshEdgeService.SetSeeThrough(seeThrough);
            }

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh"))
                {
                    XRayMeshEdgeService.RebuildAll();
                }

                if (GUILayout.Button("Open Window"))
                {
                    XRayGizmosWindow.Open();
                }
            }
        }
    }

    internal static class XRayGizmosSceneToolbarIcons
    {
        public const string BonesIconPath = XRayPaths.Base + "/UI/Icons/xray-bones.png";
        private const string WeightPaintIconPath = XRayPaths.Base + "/UI/Icons/xray-weight-paint.png";
        private const string MeshEdgesIconPath = XRayPaths.Base + "/UI/Icons/xray-mesh-edges.png";

        private static Texture2D bonesIcon;
        private static Texture2D weightPaintIcon;
        private static Texture2D meshEdgesIcon;
        private static Texture2D extraIcon;

        public static Texture2D LoadBonesIcon()
        {
            return bonesIcon != null
                ? bonesIcon
                : bonesIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(BonesIconPath);
        }

        public static Texture2D LoadWeightPaintIcon()
        {
            return weightPaintIcon != null
                ? weightPaintIcon
                : weightPaintIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(WeightPaintIconPath);
        }

        public static Texture2D LoadMeshEdgesIcon()
        {
            return meshEdgesIcon != null
                ? meshEdgesIcon
                : meshEdgesIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(MeshEdgesIconPath);
        }

        public static Texture2D LoadExtraIcon()
        {
            if (extraIcon != null) return extraIcon;
            var builtIn = EditorGUIUtility.IconContent("d_Settings Icon").image as Texture2D ??
                          EditorGUIUtility.IconContent("_Popup").image as Texture2D;
            if (builtIn != null)
            {
                extraIcon = builtIn;
                return extraIcon;
            }

            extraIcon = ReloadScopedObjects.Keep(new Texture2D(16, 16, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            });
            var clear = new Color(0f, 0f, 0f, 0f);
            var color = EditorGUIUtility.isProSkin ? new Color(0.82f, 0.82f, 0.82f, 1f) : new Color(0.18f, 0.18f, 0.18f, 1f);
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    bool dot = (x >= 3 && x <= 5 && y >= 7 && y <= 9) ||
                               (x >= 7 && x <= 9 && y >= 7 && y <= 9) ||
                               (x >= 11 && x <= 13 && y >= 7 && y <= 9);
                    extraIcon.SetPixel(x, y, dot ? color : clear);
                }
            }
            extraIcon.Apply();
            return extraIcon;
        }
    }
}
