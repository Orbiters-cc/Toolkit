using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    /// <summary>Swatch icons for the photoshoot pickers: lit spheres for light presets and stick figures for poses.</summary>
    internal static class PhotoshootIcons
    {
        internal static Texture2D CreateLightIcon(PhotoshootService.LightPresetOption option, int size)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, false)
            {
                name = "Orbiters Photoshoot Light Preview",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            Vector3 keyDirection = (Quaternion.Euler(option.keyRotation) * Vector3.forward).normalized;
            Vector3 rimDirection = (Quaternion.Euler(option.rimRotation) * Vector3.forward).normalized;
            Vector3 rim2Direction = (Quaternion.Euler(option.rim2Rotation) * Vector3.forward).normalized;
            Vector3 fillDirection = (option.fillPosition.sqrMagnitude > 0.001f ? option.fillPosition.normalized : new Vector3(-0.5f, 0.4f, 1f)).normalized;
            Color clear = new Color(0f, 0f, 0f, 0f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = ((x + 0.5f) / size) * 2f - 1f;
                    float v = ((y + 0.5f) / size) * 2f - 1f;
                    float r2 = u * u + v * v;
                    if (r2 > 1f)
                    {
                        texture.SetPixel(x, y, clear);
                        continue;
                    }

                    Vector3 normal = new Vector3(u, v, Mathf.Sqrt(Mathf.Max(0f, 1f - r2))).normalized;
                    float key = Mathf.Max(0f, Vector3.Dot(normal, -keyDirection)) * option.keyIntensity;
                    float fill = Mathf.Max(0f, Vector3.Dot(normal, fillDirection)) * option.fillIntensity;
                    float rim = Mathf.Pow(Mathf.Max(0f, Vector3.Dot(normal, -rimDirection)), 2.5f) * option.rimIntensity;
                    float rim2 = Mathf.Pow(Mathf.Max(0f, Vector3.Dot(normal, -rim2Direction)), 2.5f) * option.rim2Intensity;
                    Color color = option.ambientColor * 0.72f + option.keyColor * key + option.fillColor * fill + option.rimColor * rim + option.rim2Color * rim2;
                    float edge = Mathf.SmoothStep(0.72f, 1f, Mathf.Sqrt(r2));
                    color = Color.Lerp(color, color * 0.55f, edge);
                    color.a = Mathf.SmoothStep(1.0f, 0.88f, Mathf.Sqrt(r2));
                    texture.SetPixel(x, y, color);
                }
            }

            texture.Apply(false, true);
            return texture;
        }

        internal static Texture2D CreatePoseIcon(GameObject avatarRoot, AnimationClip clip, int size)
        {
            var texture = CreateTransparentTexture(size, "Orbiters Photoshoot Pose Preview");
            bool drawn = false;
            GameObject avatarCopy = null;
            try
            {
                if (avatarRoot != null)
                {
                    avatarCopy = UnityEngine.Object.Instantiate(avatarRoot);
                    avatarCopy.hideFlags = HideFlags.HideAndDontSave;
                    avatarCopy.SetActive(true);
                    if (clip != null)
                    {
                        clip.SampleAnimation(avatarCopy, 0f);
                    }

                    var animator = avatarCopy.GetComponentInChildren<Animator>();
                    if (animator != null && animator.isHuman)
                    {
                        drawn = DrawHumanoidPose(texture, animator, size);
                    }
                }
            }
            finally
            {
                if (avatarCopy != null)
                {
                    UnityEngine.Object.DestroyImmediate(avatarCopy);
                }
            }

            if (!drawn)
            {
                DrawFallbackPose(texture, size);
            }

            texture.Apply(false, true);
            return texture;
        }

        private static Texture2D CreateTransparentTexture(int size, string name)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, false)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            Color clear = new Color(0f, 0f, 0f, 0f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    texture.SetPixel(x, y, clear);
                }
            }
            return texture;
        }

        private static bool DrawHumanoidPose(Texture2D texture, Animator animator, int size)
        {
            var bones = new Dictionary<HumanBodyBones, Vector3>();
            HumanBodyBones[] trackedBones =
            {
                HumanBodyBones.Head,
                HumanBodyBones.Neck,
                HumanBodyBones.UpperChest,
                HumanBodyBones.Chest,
                HumanBodyBones.Spine,
                HumanBodyBones.Hips,
                HumanBodyBones.LeftUpperArm,
                HumanBodyBones.LeftLowerArm,
                HumanBodyBones.LeftHand,
                HumanBodyBones.RightUpperArm,
                HumanBodyBones.RightLowerArm,
                HumanBodyBones.RightHand,
                HumanBodyBones.LeftUpperLeg,
                HumanBodyBones.LeftLowerLeg,
                HumanBodyBones.LeftFoot,
                HumanBodyBones.RightUpperLeg,
                HumanBodyBones.RightLowerLeg,
                HumanBodyBones.RightFoot
            };

            foreach (var bone in trackedBones)
            {
                Transform transform = animator.GetBoneTransform(bone);
                if (transform != null)
                {
                    bones[bone] = transform.position;
                }
            }

            if (!bones.ContainsKey(HumanBodyBones.Head) || !bones.ContainsKey(HumanBodyBones.Hips))
            {
                return false;
            }

            List<Vector3> points = bones.Values.ToList();
            float minX = points.Min(point => point.x);
            float maxX = points.Max(point => point.x);
            float minY = points.Min(point => point.y);
            float maxY = points.Max(point => point.y);
            if (maxX - minX < 0.001f || maxY - minY < 0.001f)
            {
                return false;
            }

            const float padding = 9f;
            Func<Vector3, Vector2> project = point =>
            {
                float x = Mathf.Lerp(padding, size - padding, Mathf.InverseLerp(minX, maxX, point.x));
                float y = Mathf.Lerp(padding, size - padding, Mathf.InverseLerp(minY, maxY, point.y));
                return new Vector2(x, y);
            };

            Color color = Color.white;
            (HumanBodyBones from, HumanBodyBones to)[] segments =
            {
                (HumanBodyBones.Hips, HumanBodyBones.Spine), (HumanBodyBones.Spine, HumanBodyBones.Chest), (HumanBodyBones.Chest, HumanBodyBones.UpperChest),
                (HumanBodyBones.UpperChest, HumanBodyBones.Neck), (HumanBodyBones.Neck, HumanBodyBones.Head),
                (HumanBodyBones.Chest, HumanBodyBones.LeftUpperArm), (HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm), (HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand),
                (HumanBodyBones.Chest, HumanBodyBones.RightUpperArm), (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm), (HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand),
                (HumanBodyBones.Hips, HumanBodyBones.LeftUpperLeg), (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg), (HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot),
                (HumanBodyBones.Hips, HumanBodyBones.RightUpperLeg), (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg), (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot)
            };
            foreach (var (from, to) in segments)
            {
                if (bones.TryGetValue(from, out Vector3 fromPosition) && bones.TryGetValue(to, out Vector3 toPosition))
                {
                    DrawLine(texture, project(fromPosition), project(toPosition), color, 4);
                }
            }

            DrawCircle(texture, project(bones[HumanBodyBones.Head]), 5, color);
            return true;
        }

        private static void DrawFallbackPose(Texture2D texture, int size)
        {
            Color color = Color.white;
            Vector2 head = new Vector2(size * 0.50f, size * 0.78f);
            Vector2 chest = new Vector2(size * 0.50f, size * 0.58f);
            Vector2 hips = new Vector2(size * 0.50f, size * 0.38f);
            DrawCircle(texture, head, 5, color);
            DrawLine(texture, head + Vector2.down * 5f, chest, color, 4);
            DrawLine(texture, chest, hips, color, 4);
            DrawLine(texture, chest, new Vector2(size * 0.30f, size * 0.50f), color, 4);
            DrawLine(texture, chest, new Vector2(size * 0.70f, size * 0.66f), color, 4);
            DrawLine(texture, hips, new Vector2(size * 0.36f, size * 0.14f), color, 4);
            DrawLine(texture, hips, new Vector2(size * 0.66f, size * 0.18f), color, 4);
        }

        private static void DrawLine(Texture2D texture, Vector2 from, Vector2 to, Color color, int thickness)
        {
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(from, to)));
            for (int i = 0; i <= steps; i++)
            {
                Vector2 point = Vector2.Lerp(from, to, i / (float)steps);
                DrawCircle(texture, point, thickness * 0.5f, color);
            }
        }

        private static void DrawCircle(Texture2D texture, Vector2 center, float radius, Color color)
        {
            int minX = Mathf.FloorToInt(center.x - radius);
            int maxX = Mathf.CeilToInt(center.x + radius);
            int minY = Mathf.FloorToInt(center.y - radius);
            int maxY = Mathf.CeilToInt(center.y + radius);
            float radiusSqr = radius * radius;
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (x < 0 || y < 0 || x >= texture.width || y >= texture.height)
                    {
                        continue;
                    }

                    Vector2 delta = new Vector2(x, y) - center;
                    if (delta.sqrMagnitude <= radiusSqr)
                    {
                        texture.SetPixel(x, y, color);
                    }
                }
            }
        }
    }
}
