using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Toolkit.Editor.XRay
{
    /// <summary>
    /// One named command buffer run by the Scene view cameras only: what the XRay overlays draw over the renderers
    /// themselves (the renderer as it shows: skinned, with its blendshapes and live edits), with nothing added to the scene,
    /// so nothing can be saved, built or uploaded with it and the Game view never shows it.
    /// The buffer is attached to each Scene view camera once and attached again when the camera's buffer count changed
    /// (another tool removed every buffer, say). A script reload loses the managed buffer but not the native one a camera
    /// holds, so buffers of the same name are removed before attaching and at load (<see cref="RemoveOrphaned"/>).
    /// </summary>
    internal sealed class SceneViewBuffer
    {
        public readonly string Name;
        public readonly CameraEvent Event;
        // The cameras running the buffer, with their buffer count once it was added.
        private readonly Dictionary<Camera, int> cameras = new Dictionary<Camera, int>();
        private CommandBuffer buffer;

        public SceneViewBuffer(string name, CameraEvent cameraEvent)
        {
            Name = name;
            Event = cameraEvent;
        }

        /// <summary>The command buffer (null until <see cref="Begin"/> was first called, and after <see cref="Release"/>).</summary>
        public CommandBuffer Buffer => buffer;

        /// <summary>Whether a camera runs the buffer.</summary>
        public bool Attached => cameras.Count > 0;

        /// <summary>The buffer, made on first use and cleared, ready to be recorded.</summary>
        public CommandBuffer Begin()
        {
            if (buffer == null) buffer = new CommandBuffer { name = Name };
            buffer.Clear();
            return buffer;
        }

        public void AttachToSceneViews()
        {
            foreach (SceneView view in SceneView.sceneViews)
            {
                if (view != null && view.camera != null) Attach(view.camera);
            }
        }

        public void Attach(Camera camera)
        {
            if (buffer == null || camera == null || cameras.TryGetValue(camera, out int count) && camera.commandBufferCount == count)
            {
                return;
            }

            foreach (var gone in cameras.Keys.Where(c => c == null).ToList())
            {
                cameras.Remove(gone);
            }

            RemoveNamed(camera);
            camera.AddCommandBuffer(Event, buffer);
            cameras[camera] = camera.commandBufferCount;
        }

        public void Detach()
        {
            foreach (var camera in cameras.Keys)
            {
                if (camera != null && buffer != null) camera.RemoveCommandBuffer(Event, buffer);
            }

            cameras.Clear();
        }

        /// <summary>Off the cameras and empty (kept for the next time).</summary>
        public void Clear()
        {
            Detach();
            buffer?.Clear();
        }

        public void Release()
        {
            Clear();
            RemoveOrphaned();
            buffer?.Release();
            buffer = null;
        }

        /// <summary>Removes this buffer's name from every Scene view camera (left by the scripts before a reload).</summary>
        public void RemoveOrphaned()
        {
            foreach (SceneView view in SceneView.sceneViews)
            {
                if (view != null && view.camera != null) RemoveNamed(view.camera);
            }
        }

        private void RemoveNamed(Camera camera)
        {
            foreach (var attached in camera.GetCommandBuffers(Event))
            {
                if (attached != null && attached.name == Name) camera.RemoveCommandBuffer(Event, attached);
            }
        }

        /// <summary>
        /// Whether Unity draws <paramref name="renderer"/> now. A renderer Unity does not draw is not skinned either: an
        /// overlay drawn from it would show a stale pose.
        /// </summary>
        public static bool IsShown(Renderer renderer)
        {
            return renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy &&
                   (!(renderer is SkinnedMeshRenderer skinned) || skinned.sharedMesh != null) &&
                   !SceneVisibilityManager.instance.IsHidden(renderer.gameObject);
        }
    }
}
