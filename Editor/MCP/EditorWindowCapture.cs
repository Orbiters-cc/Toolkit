using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>Reads a Unity view's framebuffer, never the desktop or a scene camera.</summary>
    internal static class EditorWindowCapture
    {
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static object GetVisibleHost(EditorWindow window)
        {
            var parent = typeof(EditorWindow).GetField("m_Parent", InstanceMembers)?.GetValue(window);
            if (parent == null)
                throw new InvalidOperationException("The editor window has no rendered host view.");
            var actualView = parent.GetType().GetProperty("actualView", InstanceMembers);
            if (actualView == null)
                throw new NotSupportedException("This Unity version does not expose HostView.actualView.");
            if (actualView.GetValue(parent) as EditorWindow != window)
                throw new InvalidOperationException("The target is an inactive docked tab. Select it in Unity first, or explicitly allow focus=true.");
            return parent;
        }

        internal static void RepaintWithoutFocus(EditorWindow window)
        {
            var host = GetVisibleHost(window);
            var repaint = host.GetType().GetMethod("RepaintImmediately", InstanceMembers, null, Type.EmptyTypes, null);
            if (repaint == null)
                throw new NotSupportedException("This Unity version does not expose GUIView.RepaintImmediately.");
            repaint.Invoke(host, null);
        }

        internal static object Save(EditorWindow window, int maxResolution)
        {
            var parent = GetVisibleHost(window);
            var grab = parent.GetType().GetMethod("GrabPixels", InstanceMembers, null,
                new[] { typeof(RenderTexture), typeof(Rect) }, null);
            if (grab == null)
                throw new NotSupportedException("This Unity version does not expose GUIView.GrabPixels.");

            float scale = EditorGUIUtility.pixelsPerPoint;
            int width = Mathf.RoundToInt(window.position.width * scale);
            int height = Mathf.RoundToInt(window.position.height * scale);
            if (width <= 0 || height <= 0 || (long)width * height > 32000000)
                throw new InvalidOperationException("Window dimensions are empty or exceed the 32 megapixel capture limit.");

            RenderTexture target = null;
            Texture2D pixels = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                // Editor UI pixels are already display-encoded; prevent an extra linear-to-sRGB conversion.
                target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                { hideFlags = HideFlags.HideAndDontSave, antiAliasing = 1 };
                target.Create();
                grab.Invoke(parent, new object[] { target, new Rect(0, 0, width, height) });
                RenderTexture.active = target;
                pixels = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                pixels.Apply();

                // GUIView's framebuffer is upside down relative to PNG row order.
                var source = pixels.GetPixels32();
                var flipped = new Color32[source.Length];
                for (int y = 0; y < height; y++)
                    Array.Copy(source, y * width, flipped, (height - 1 - y) * width, width);
                pixels.SetPixels32(flipped);
                pixels.Apply();

                if (maxResolution > 0 && Math.Max(width, height) > maxResolution)
                {
                    float factor = (float)maxResolution / Math.Max(width, height);
                    int w = Math.Max(1, Mathf.RoundToInt(width * factor));
                    int h = Math.Max(1, Mathf.RoundToInt(height * factor));
                    var reduced = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    try
                    {
                        Graphics.Blit(pixels, reduced);
                        RenderTexture.active = reduced;
                        UnityEngine.Object.DestroyImmediate(pixels);
                        pixels = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                        pixels.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                        pixels.Apply();
                    }
                    finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(reduced); }
                }

                string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string folder = Path.Combine(project, "Library", "OrbitersToolkit", "Screenshots");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, $"{window.GetType().Name}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.png");
                File.WriteAllBytes(path, pixels.EncodeToPNG());
                return new
                {
                    fullPath = path, width = pixels.width, height = pixels.height,
                    sourceWidth = width, sourceHeight = height, pixelsPerPoint = scale,
                    windowId = window.GetInstanceID(), windowType = window.GetType().FullName,
                    title = window.titleContent.text, captureMode = "editor_window_framebuffer",
                    unityVersion = Application.unityVersion, projectPath = project
                };
            }
            catch (TargetInvocationException ex)
            {
                throw new InvalidOperationException("Unity could not capture this window: " + ex.InnerException?.Message, ex);
            }
            finally
            {
                RenderTexture.active = previous;
                if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
                if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
            }
        }
    }
}
