using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Optimization
{
    /// <summary>
    /// Shrinks the icons of a build copy's expression menu: VRChat draws them at about 64 px, and an icon imported at 1024 px
    /// uploads at that size. The menus holding a larger icon are copied (the menu assets and images stay as they are) and
    /// point at copies of the icons at <c>max</c> px at most, compressed for the platform the editor builds for.
    /// </summary>
    public static class MenuIcons
    {
        public sealed class Result
        {
            /// <summary>The menus and icons made (in memory, for the build to keep).</summary>
            public readonly List<Object> Made = new List<Object>();
            /// <summary>How many icons were shrunk.</summary>
            public int Icons;
        }

        /// <summary>Every icon of the avatar's menu and its submenus.</summary>
        public static IEnumerable<Texture2D> Of(GameObject root)
        {
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            return Menus(descriptor != null ? descriptor.expressionsMenu : null).SelectMany(Icons).Where(i => i != null).Distinct();
        }

        public static Result Shrink(GameObject root, int max = 64)
        {
            var result = new Result();
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null || !Of(root).Any(i => Mathf.Max(i.width, i.height) > max)) return result;
            var menus = new Dictionary<VRCExpressionsMenu, VRCExpressionsMenu>();
            var icons = new Dictionary<Texture2D, Texture2D>();
            bool mobile = EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android || EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS;

            Texture2D Icon(Texture2D icon)
            {
                if (icon == null || Mathf.Max(icon.width, icon.height) <= max) return icon;
                if (!icons.TryGetValue(icon, out var small))
                {
                    icons[icon] = small = Resize(icon, max, mobile);
                    result.Made.Add(small);
                    result.Icons++;
                }
                return small;
            }

            VRCExpressionsMenu Copy(VRCExpressionsMenu menu)
            {
                if (menu == null) return null;
                if (menus.TryGetValue(menu, out var known)) return known;
                var copy = Object.Instantiate(menu);
                copy.name = menu.name;
                menus[menu] = copy;
                result.Made.Add(copy);
                foreach (var control in copy.controls)
                {
                    control.icon = Icon(control.icon);
                    if (control.labels != null)
                        for (int i = 0; i < control.labels.Length; i++) { var label = control.labels[i]; label.icon = Icon(label.icon); control.labels[i] = label; }
                    control.subMenu = Copy(control.subMenu);
                }
                return copy;
            }

            descriptor.expressionsMenu = Copy(descriptor.expressionsMenu);
            return result;
        }

        private static IEnumerable<VRCExpressionsMenu> Menus(VRCExpressionsMenu menu)
        {
            var seen = new HashSet<VRCExpressionsMenu>();
            var stack = new Stack<VRCExpressionsMenu>();
            if (menu != null) stack.Push(menu);
            while (stack.Count > 0)
            {
                var next = stack.Pop();
                if (!seen.Add(next)) continue;
                yield return next;
                foreach (var control in next.controls ?? new List<VRCExpressionsMenu.Control>())
                    if (control.subMenu != null) stack.Push(control.subMenu);
            }
        }

        private static IEnumerable<Texture2D> Icons(VRCExpressionsMenu menu) =>
            (menu.controls ?? new List<VRCExpressionsMenu.Control>()).SelectMany(c => new[] { c.icon }.Concat((c.labels ?? new VRCExpressionsMenu.Control.Label[0]).Select(l => l.icon)));

        // Halved on the GPU until close to the size (each step averages four texels, so no detail shimmers), then drawn at
        // the size and compressed: BC7 for PC, ASTC 6x6 for Quest.
        private static Texture2D Resize(Texture2D source, int max, bool mobile)
        {
            float scale = (float)max / Mathf.Max(source.width, source.height);
            int width = Mathf.Max(1, Mathf.RoundToInt(source.width * scale)), height = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
            // BC7 compresses blocks of 4 × 4 texels: each side a multiple of 4 (ASTC takes any size).
            if (!mobile) { width = Mathf.Max(4, (width + 2) / 4 * 4); height = Mathf.Max(4, (height + 2) / 4 * 4); }
            var previous = RenderTexture.active;
            Texture current = source;
            RenderTexture step = null;
            try
            {
                int w = source.width, h = source.height;
                while (w / 2 >= width && h / 2 >= height)
                {
                    w /= 2; h /= 2;
                    var next = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    next.filterMode = FilterMode.Bilinear;
                    Graphics.Blit(current, next);
                    if (step != null) RenderTexture.ReleaseTemporary(step);
                    current = step = next;
                }
                var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(current, target);
                if (step != null) RenderTexture.ReleaseTemporary(step);
                step = target;
                RenderTexture.active = target;
                var small = new Texture2D(width, height, TextureFormat.RGBA32, true, false) { name = source.name, wrapMode = TextureWrapMode.Clamp };
                small.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                small.Apply(true);
                EditorUtility.CompressTexture(small, mobile ? TextureFormat.ASTC_6x6 : TextureFormat.BC7, TextureCompressionQuality.Best);
                return small;
            }
            finally
            {
                RenderTexture.active = previous;
                if (step != null) RenderTexture.ReleaseTemporary(step);
            }
        }
    }
}
