using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.LowLevel;

namespace Orbiters.Toolkit.Editor.Posing
{
    /// <summary>Editor-only holds for explicitly posed channels, after animation and LateUpdate.</summary>
    internal static class PlayModePoseOverrides
    {
        private struct Pose { public Vector3 Position; public Quaternion Rotation; public int Channels; }
        private struct ApplyEditorPose { }
        private static readonly Dictionary<Transform, Pose> Poses = new Dictionary<Transform, Pose>();
        private static bool installed;

        internal static void Hold(Transform bone, int channels)
        {
            Poses.TryGetValue(bone, out var pose);
            if ((channels & 1) != 0) pose.Rotation = bone.localRotation;
            if ((channels & 2) != 0) pose.Position = bone.localPosition;
            pose.Channels |= channels;
            Poses[bone] = pose;
            if (installed) return;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var systems = new List<PlayerLoopSystem>(loop.subSystemList);
            int at = systems.FindIndex(s => s.type == typeof(UnityEngine.PlayerLoop.PreLateUpdate));
            if (at < 0) return;
            systems.Insert(at + 1, new PlayerLoopSystem { type = typeof(ApplyEditorPose), updateDelegate = Apply });
            loop.subSystemList = systems.ToArray();
            PlayerLoop.SetPlayerLoop(loop);
            EditorApplication.update += ApplyPaused;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            installed = true;
        }

        private static void ApplyPaused() { if (EditorApplication.isPaused) Apply(); }

        private static void Apply()
        {
            foreach (var item in Poses)
            {
                if (item.Key == null) continue;
                if ((item.Value.Channels & 1) != 0) item.Key.localRotation = item.Value.Rotation;
                if ((item.Value.Channels & 2) != 0) item.Key.localPosition = item.Value.Position;
            }
        }

        internal static void Clear()
        {
            Poses.Clear();
            if (!installed) return;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var systems = new List<PlayerLoopSystem>(loop.subSystemList);
            systems.RemoveAll(s => s.type == typeof(ApplyEditorPose));
            loop.subSystemList = systems.ToArray();
            PlayerLoop.SetPlayerLoop(loop);
            EditorApplication.update -= ApplyPaused;
            AssemblyReloadEvents.beforeAssemblyReload -= Clear;
            installed = false;
        }
    }
}
