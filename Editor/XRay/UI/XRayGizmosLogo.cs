using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor.XRay
{
    public static class XRayGizmosLogo
    {
        public static VisualElement Create(float scale = 1f)
        {
            var root = new VisualElement
            {
                style =
                {
                    width = 39f * scale,
                    height = 35f * scale,
                    flexShrink = 0
                }
            };

            AddBar(root, 0f, 0f, 39f, 3.72f, scale);
            AddBar(root, 0f, 12.69f, 39f, 9.87f, scale);
            AddBar(root, 0f, 31.92f, 9.27f, 3.08f, scale);
            AddBar(root, 14.99f, 31.92f, 9.27f, 3.08f, scale);
            AddBar(root, 29.73f, 31.92f, 9.27f, 3.08f, scale);
            return root;
        }

        private static void AddBar(VisualElement parent, float x, float y, float width, float height, float scale)
        {
            var bar = new VisualElement();
            bar.AddToClassList("xray-logo-bar");
            bar.style.left = x * scale;
            bar.style.top = y * scale;
            bar.style.width = width * scale;
            bar.style.height = height * scale;
            parent.Add(bar);
        }
    }
}

