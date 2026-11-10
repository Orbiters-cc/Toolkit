using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    /// <summary>Live avatar photoshoot scene: pose, expression, lighting, background and framing for thumbnails and banners.</summary>
    public static partial class PhotoshootService
    {
        public enum ShotKind
        {
            Thumbnail,
            Banner
        }

        public sealed class BodyPoseOption
        {
            public string displayName;
            /// <summary>One of the user's own poses (<see cref="PhotoshootPoses"/>): it can be removed.</summary>
            public bool custom;
            public string assetPath;
            public AnimationClip clip;
        }

        public enum FramingPreset
        {
            Portrait,
            HalfBody,
            FullBody
        }

        /// <summary>The bone the avatar turns around: the one nearest the middle of the view when a turn starts.</summary>
        public enum TurnPivot
        {
            Hips,
            Chest,
            Head
        }

        /// <summary>Where the posed avatar stands in the last rendered frame; used to suggest framings.</summary>
        public struct FrameInfo
        {
            public bool valid;
            /// <summary>The avatar as turned.</summary>
            public Bounds bounds;
            /// <summary>The avatar before it is turned: the camera is placed from it, so turning never moves the camera.</summary>
            public Bounds stage;
            /// <summary>Everything skinned to the head and its children (ears, hair, jaw), when the avatar is humanoid.</summary>
            public bool hasHead;
            public Bounds head;
            /// <summary>Everything skinned above the hips, except forearms and hands.</summary>
            public bool hasTorso;
            public Bounds torso;
            public Vector3 chest;
            public Vector3 hips;
        }

        public sealed class BackgroundOption
        {
            public string displayName;
            /// <summary>A plain background in the colour chosen in the panel instead of an image.</summary>
            public bool solidColor;
            /// <summary>One of the user's own pictures (<see cref="PhotoshootBackgrounds"/>): it can be removed.</summary>
            public bool custom;
            public string assetPath;
            public Texture2D texture;
        }

        public sealed class FaceBlendshapeOption
        {
            public string name;
            public int rendererCount;
        }

        public sealed class LightPresetOption
        {
            public string displayName;
            public Color ambientColor;
            public Color keyColor;
            public float keyIntensity;
            public Vector3 keyRotation;
            public Color fillColor;
            public float fillIntensity;
            public Vector3 fillPosition;
            public Color rimColor;
            public float rimIntensity;
            public Vector3 rimRotation;
            /// <summary>Optional second rim light from the other side (off when the intensity is 0).</summary>
            public Color rim2Color;
            public float rim2Intensity;
            public Vector3 rim2Rotation;
        }

        public sealed class Catalog
        {
            public List<BodyPoseOption> bodyPoses = new List<BodyPoseOption>();
            public List<BackgroundOption> backgrounds = new List<BackgroundOption>();
            public List<FaceBlendshapeOption> faceBlendshapes = new List<FaceBlendshapeOption>();
            public List<LightPresetOption> lightPresets = new List<LightPresetOption>();
        }

        public sealed class RenderRequest
        {
            public GameObject avatarRoot;
            public AnimationClip bodyPose;
            public Texture2D background;
            public Color backgroundColor = DefaultBackgroundColor;
            public LightPresetOption lightPreset;
            public IEnumerable<string> selectedFaceBlendshapeNames;
            public bool forceFaceBlendshapeApply;
            public ShotKind shotKind;
            public float zoom;
            public Vector2 placement;
            public float avatarYawDegrees;
            /// <summary>Tilt of the avatar toward (positive) or away from the camera, after its turn.</summary>
            public float avatarTiltDegrees;
            public TurnPivot pivot;
            /// <summary>The head and eyes turn toward the camera.</summary>
            public bool lookAtCamera;
            /// <summary>Who looks at the camera: 0 the head alone, 1 the eyes alone.</summary>
            public float lookWithEyes = .5f;
            public int width;
            public int height;
            /// <summary>Effects over the shot; null or none on for the shot as rendered.</summary>
            public PhotoshootEffectSettings effects;
            /// <summary>Effects of the whole picture (lens distortion, chromatic aberration, vignette): off on a ref sheet's views.</summary>
            public bool frameEffects = true;
            /// <summary>The environment light over the preset's own.</summary>
            public float ambientIntensity = 1f;
        }

        /// <summary>
        /// Where the avatar sees from, in its root's space (VRChat: the avatar descriptor's view position), set by a
        /// platform bridge. The depth of field is sharp there; without it, at the head.
        /// </summary>
        public static Func<GameObject, Vector3?> ViewPosition;

        private const string BodyPoseFolder = "Packages/orbiters.toolkit/Editor/Photoshoot/BodyPoses";
        private const string BackgroundFolder = "Packages/orbiters.toolkit/Editor/Photoshoot/Backgrounds";
        private const string BannerEffectShaderName = "Hidden/Orbiters/PhotoshootBannerEffect";
        private const string BannerEffectShaderPath = "Packages/orbiters.toolkit/Editor/Photoshoot/PhotoshootBannerEffect.shader";
        private const float BannerEffectMaxBlurTexels = 48f;
        private const float ReferenceCameraFieldOfView = 31.8f;
        private const float DefaultCameraZoom = 1.35f;
        // Wider than the zoom dial goes (PhotoshootState.MinZoom): a fitted framing may need it, such as a ref sheet's side
        // view of an avatar with a long tail.
        internal const float MinCameraZoom = 0.2f;
        private const float MaxCameraZoom = 20f;
        private const float PlacementFrameStrength = 0.80f;
        /// <summary>
        /// Placement moves the camera by up to this many times 80% of the unzoomed frame, enough to bring any part of the
        /// avatar to the centre, also zoomed in and in poses where the head is far from the middle.
        /// </summary>
        public const float MaxPlacement = 2f;
        private static readonly System.Reflection.MethodInfo TextureIncrementUpdateCountMethod =
            typeof(Texture).GetMethod(
                "IncrementUpdateCount",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
        private static readonly Vector3 LiveSceneStageOrigin = new Vector3(10000f, 0f, 10000f);
        private static readonly Color PreviewClearColor = new Color(0x30 / 255f, 0x30 / 255f, 0x30 / 255f, 1f);
        public static readonly Color DefaultBackgroundColor = PreviewClearColor;
        private static readonly Color BannerEffectOverlayColor = new Color(0x30 / 255f, 0x30 / 255f, 0x30 / 255f, 1f);
        private static readonly string[] FaceKeywords = { "smile", "happy", "sad", "wink", "grin", "angry" };

        private static Material sharedBannerEffectMaterial;

        /// <summary>
        /// A copy of <paramref name="source"/> with the banner effect (blur and fade toward #303030 over the lower third), the
        /// same one the Orbiters server applies to uploaded banners: a local stand-in until the server's image arrives. The
        /// blur scales with the image (48 texels at 1600 wide). The caller owns the texture.
        /// </summary>
        public static Texture2D ApplyBannerEffect(Texture source)
        {
            if (source == null) return null;
            if (sharedBannerEffectMaterial == null)
            {
                Shader shader = Shader.Find(BannerEffectShaderName) ?? AssetDatabase.LoadAssetAtPath<Shader>(BannerEffectShaderPath);
                if (shader == null) return null;
                sharedBannerEffectMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            int width = source.width, height = source.height;
            sharedBannerEffectMaterial.SetColor("_OverlayColor", BannerEffectOverlayColor);
            sharedBannerEffectMaterial.SetFloat("_MaxBlurTexels", BannerEffectMaxBlurTexels * width / 1600f);
            RenderTexture previous = RenderTexture.active;
            RenderTexture output = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            try
            {
                Graphics.Blit(source, output, sharedBannerEffectMaterial);
                RenderTexture.active = output;
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false) { name = "Orbiters Banner Effect Preview" };
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                texture.Apply(false, false);
                return texture;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(output);
            }
        }

        public static Catalog BuildCatalog(GameObject avatarRoot)
        {
            var catalog = new Catalog();
            catalog.bodyPoses = FindBodyPoses();
            catalog.backgrounds = FindBackgrounds();
            catalog.faceBlendshapes = FindFaceBlendshapes(avatarRoot);
            catalog.lightPresets = CreateLightPresets();
            return catalog;
        }

        private static void ValidateRequest(RenderRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            if (request.avatarRoot == null)
            {
                throw new InvalidOperationException("No avatar root is available for photoshoot generation.");
            }
            if (request.width <= 0 || request.height <= 0)
            {
                throw new InvalidOperationException("Photoshoot render size must be greater than zero.");
            }
        }

        private static string CreateFaceBlendshapeKey(IEnumerable<string> selectedFaceBlendshapeNames)
        {
            if (selectedFaceBlendshapeNames == null)
            {
                return string.Empty;
            }

            return string.Join(
                "|",
                selectedFaceBlendshapeNames
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name.Trim())
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        }

        internal static List<BodyPoseOption> FindBodyPoses()
        {
            var options = new List<BodyPoseOption>
            {
                new BodyPoseOption { displayName = "Default Pose" }
            };

            // The user's own poses come after the built-in ones.
            foreach (string path in PhotoshootPoses.Files())
            {
                var clip = PhotoshootPoses.Load(path);
                if (clip != null) options.Add(new BodyPoseOption { displayName = Path.GetFileNameWithoutExtension(path), assetPath = path, clip = clip, custom = true });
            }

            if (!AssetDatabase.IsValidFolder(BodyPoseFolder))
            {
                return options;
            }

            foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { BodyPoseFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null)
                {
                    continue;
                }

                options.Add(new BodyPoseOption
                {
                    displayName = ObjectNames.NicifyVariableName(Path.GetFileNameWithoutExtension(path).Replace("_pose", "")),
                    assetPath = path,
                    clip = clip
                });
            }

            return options
                .OrderBy(option => option.clip == null ? 0 : option.custom ? 2 : 1)
                .ThenBy(option => option.displayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static List<BackgroundOption> FindBackgrounds()
        {
            var options = new List<BackgroundOption>
            {
                new BackgroundOption { displayName = "Color", solidColor = true }
            };

            // The user's own pictures come after the built-in ones.
            foreach (string path in PhotoshootBackgrounds.Files())
            {
                var texture = PhotoshootBackgrounds.Load(path);
                if (texture != null) options.Add(new BackgroundOption { displayName = Path.GetFileNameWithoutExtension(path), assetPath = path, texture = texture, custom = true });
            }

            if (!AssetDatabase.IsValidFolder(BackgroundFolder))
            {
                return options;
            }

            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { BackgroundFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (texture == null)
                {
                    continue;
                }

                options.Add(new BackgroundOption
                {
                    displayName = ObjectNames.NicifyVariableName(Path.GetFileNameWithoutExtension(path)),
                    assetPath = path,
                    texture = texture
                });
            }

            return options
                .OrderBy(option => option.solidColor ? 0 : option.custom ? 2 : 1)
                .ThenBy(option => option.displayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<FaceBlendshapeOption> FindFaceBlendshapes(GameObject avatarRoot)
        {
            var countsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (avatarRoot == null)
            {
                return new List<FaceBlendshapeOption>();
            }

            foreach (var renderer in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer != null ? renderer.sharedMesh : null;
                if (mesh == null)
                {
                    continue;
                }

                var seenOnRenderer = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string blendshapeName = mesh.GetBlendShapeName(i);
                    if (!IsFaceBlendshapeCandidate(blendshapeName) || !seenOnRenderer.Add(blendshapeName))
                    {
                        continue;
                    }

                    int count;
                    countsByName.TryGetValue(blendshapeName, out count);
                    countsByName[blendshapeName] = count + 1;
                }
            }

            return countsByName
                .Select(pair => new FaceBlendshapeOption { name = pair.Key, rendererCount = pair.Value })
                .OrderBy(option => option.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsFaceBlendshapeCandidate(string blendshapeName)
        {
            if (string.IsNullOrWhiteSpace(blendshapeName))
            {
                return false;
            }

            return FaceKeywords.Any(keyword => blendshapeName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static GameObject CreateVisibleSceneObject(string name, Scene scene)
        {
            var gameObject = new GameObject(name)
            {
                hideFlags = HideFlags.DontSave
            };
            SceneManager.MoveGameObjectToScene(gameObject, scene);
            return gameObject;
        }

        private static void DisableAudioListeners(GameObject root)
        {
            foreach (var listener in root.GetComponentsInChildren<AudioListener>(true))
            {
                listener.enabled = false;
            }
        }

        private static void DisableAnimationComponents(GameObject root)
        {
            SetAnimationComponentsEnabled(root, false);
        }

        private static void SetAnimationComponentsEnabled(GameObject root, bool enabled)
        {
            if (root == null)
            {
                return;
            }

            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                animator.enabled = enabled;
            }

            foreach (var animation in root.GetComponentsInChildren<Animation>(true))
            {
                animation.enabled = enabled;
            }
        }

        private static void IncrementTextureUpdateCount(Texture texture)
        {
            if (texture == null || TextureIncrementUpdateCountMethod == null)
            {
                return;
            }

            try
            {
                TextureIncrementUpdateCountMethod.Invoke(texture, null);
            }
            catch
            {
                // Older Unity versions may expose the method but reject invocation for render textures.
            }
        }

        private static Material CreateBackground(
            Camera camera,
            Bounds bounds,
            Texture2D texture,
            Color color,
            Scene scene,
            out GameObject background,
            bool visible)
        {
            background = GameObject.CreatePrimitive(PrimitiveType.Quad);
            background.name = "Photoshoot Background";
            background.hideFlags = visible ? HideFlags.DontSave : HideFlags.HideAndDontSave;
            SceneManager.MoveGameObjectToScene(background, scene);
            var collider = background.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.DestroyImmediate(collider);
            }

            Shader shader = FindBackgroundShader(texture);
            var material = new Material(shader)
            {
                hideFlags = visible ? HideFlags.DontSave : HideFlags.HideAndDontSave
            };
            if (material.HasProperty("_Cull"))
            {
                material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            }
            material.doubleSidedGI = true;
            ConfigureBackground(background, material, camera, bounds, texture, color);

            var renderer = background.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return material;
        }

        private static void ConfigureBackground(
            GameObject background,
            Material material,
            Camera camera,
            Bounds bounds,
            Texture2D texture,
            Color solidColor)
        {
            float distanceToTarget = Mathf.Max(0.1f, Vector3.Dot(bounds.center - camera.transform.position, camera.transform.forward));
            float backgroundDepth = Mathf.Max(bounds.size.z + 1.5f, distanceToTarget * 0.75f);
            Vector3 backgroundPosition = camera.transform.position + camera.transform.forward * (distanceToTarget + backgroundDepth);
            float cameraToBackground = Vector3.Distance(camera.transform.position, backgroundPosition);
            float viewHeight = 2f * cameraToBackground * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f);
            float viewWidth = viewHeight * camera.aspect;

            background.transform.position = backgroundPosition;
            background.transform.rotation = Quaternion.LookRotation(backgroundPosition - camera.transform.position, camera.transform.up);
            background.transform.localScale = new Vector3(viewWidth * 1.12f, viewHeight * 1.12f, 1f);

            Shader shader = FindBackgroundShader(texture);
            if (shader != null && material.shader != shader)
            {
                material.shader = shader;
            }
            if (material.HasProperty("_Cull"))
            {
                material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            }

            if (material.HasProperty("_MainTex"))
            {
                material.mainTexture = texture != null ? texture : Texture2D.whiteTexture;
                // Cropped to the frame like a cover photo, never stretched.
                float frame = Mathf.Max(0.01f, camera.aspect);
                float picture = texture != null ? texture.width / (float)Mathf.Max(1, texture.height) : frame;
                Vector2 scale = picture > frame ? new Vector2(frame / picture, 1f) : new Vector2(1f, picture / frame);
                material.mainTextureScale = scale;
                material.mainTextureOffset = (Vector2.one - scale) * 0.5f;
            }
            Color color = texture != null ? Color.white : solidColor;
            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }
        }

        private static Shader FindBackgroundShader(Texture2D texture)
        {
            if (texture == null)
            {
                return Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            }

            return Shader.Find("Unlit/Texture") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
        }

    }
}
