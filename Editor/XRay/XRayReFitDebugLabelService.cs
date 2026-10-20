using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.XRay
{
    [InitializeOnLoad]
    public static class XRayReFitDebugLabelService
    {
        private const string ReFitDebugModeKey = "Orbiters.ReFit.DebugMode";
        private const string SessionRootPrefix = "__ReFit_Debug_Session_";

        private static readonly List<XRaySceneLabel> Labels = new List<XRaySceneLabel>();
        // The debug session roots, found again only after the hierarchy changed (not at every repaint).
        private static readonly List<GameObject> SessionRoots = new List<GameObject>();
        private static bool rootsStale = true;
        private static bool lastDebugMode;

        static XRayReFitDebugLabelService()
        {
            lastDebugMode = IsReFitDebugModeEnabled();
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += WatchDebugMode;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            AssemblyReloadEvents.beforeAssemblyReload += RepaintSceneViews;
            EditorApplication.quitting += RepaintSceneViews;
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            var evt = Event.current;
            if (evt == null || evt.type != EventType.Repaint)
            {
                return;
            }

            if (!IsReFitDebugModeEnabled())
            {
                return;
            }

            CollectLabels();
            if (Labels.Count == 0)
            {
                return;
            }

            XRaySceneLabelRenderer.Draw(sceneView, Labels);
        }

        private static void OnHierarchyChanged()
        {
            rootsStale = true;
            RepaintSceneViews();
        }

        private static void WatchDebugMode()
        {
            bool debugMode = IsReFitDebugModeEnabled();
            if (debugMode == lastDebugMode)
            {
                return;
            }

            lastDebugMode = debugMode;
            RepaintSceneViews();
        }

        private static void CollectLabels()
        {
            Labels.Clear();
            if (rootsStale)
            {
                rootsStale = false;
                SessionRoots.Clear();
                foreach (var candidate in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (IsReFitDebugSessionRoot(candidate)) SessionRoots.Add(candidate);
                }
            }

            foreach (var root in SessionRoots)
            {
                if (!IsReFitDebugSessionRoot(root))
                {
                    continue;
                }

                for (int i = 0; i < root.transform.childCount; i++)
                {
                    var snapshot = root.transform.GetChild(i);
                    if (snapshot == null)
                    {
                        continue;
                    }

                    if (TryGetSnapshotTarget(snapshot.gameObject, out var target))
                    {
                        Labels.Add(new XRaySceneLabel(FormatSnapshotLabel(snapshot.name), target));
                    }
                }
            }
        }

        private static bool IsReFitDebugSessionRoot(GameObject go)
        {
            return go != null &&
                   !EditorUtility.IsPersistent(go) &&
                   go.scene.IsValid() &&
                   go.name.StartsWith(SessionRootPrefix, StringComparison.Ordinal);
        }

        private static bool IsReFitDebugModeEnabled()
        {
            return EditorPrefs.GetBool(ReFitDebugModeKey, false);
        }

        private static bool TryGetSnapshotTarget(GameObject snapshot, out Vector3 target)
        {
            target = default;
            if (snapshot == null || !snapshot.scene.IsValid())
            {
                return false;
            }

            if (!TryGetRendererBounds(snapshot, true, out var bounds) &&
                !TryGetRendererBounds(snapshot, false, out bounds))
            {
                target = snapshot.transform.position;
                return true;
            }

            target = new Vector3(bounds.center.x, bounds.max.y, bounds.center.z);
            return true;
        }

        private static bool TryGetRendererBounds(GameObject root, bool requireEnabled, out Bounds bounds)
        {
            bounds = default;
            bool found = false;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || (requireEnabled && !renderer.enabled))
                {
                    continue;
                }

                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return found;
        }

        private static string FormatSnapshotLabel(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return "ReFit snapshot";
            }

            var parts = rawName.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            int cursor = 0;
            if (cursor < parts.Length && IsIntegerToken(parts[cursor]))
            {
                cursor++;
            }

            if (cursor < parts.Length && IsIntegerToken(parts[cursor]))
            {
                cursor++;
            }

            if (cursor >= parts.Length)
            {
                return rawName.Replace('_', ' ');
            }

            return string.Join(" ", parts, cursor, parts.Length - cursor);
        }

        private static bool IsIntegerToken(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            for (int i = 0; i < token.Length; i++)
            {
                if (!char.IsDigit(token[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static void RepaintSceneViews()
        {
            SceneView.RepaintAll();
        }
    }
}
