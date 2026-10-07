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
    public static class PhotoshootService
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

        /// <summary>
        /// One photoshoot stage in its own preview scene: its camera renders only that scene, so other photoshoots and the
        /// open scenes never show up in (or light) its images, and its lights never reach the open scenes.
        /// </summary>
        public sealed class LivePreviewSession : IDisposable
        {
            private sealed class PreviewRenderTarget
            {
                public RenderTexture sceneRenderTexture;
                /// <summary>The shot with its effects, before the banner effect: what a plain capture reads.</summary>
                public RenderTexture gradedRenderTexture;
                public RenderTexture depthTexture;
                public RenderTexture renderTexture;
                public RenderTexture nextRenderTexture;
                public int width;
                public int height;
            }

            private Scene scene;
            private GameObject avatarCopy;
            private GameObject lastAvatarRoot;
            private GameObject backgroundObject;
            private GameObject keyLightObject;
            private GameObject fillLightObject;
            private GameObject rimLightObject;
            private GameObject rim2LightObject;
            private AnimationClip lastBodyPose;
            private readonly PreviewRenderTarget thumbnailTarget = new PreviewRenderTarget();
            private readonly PreviewRenderTarget bannerTarget = new PreviewRenderTarget();
            private Camera camera;
            private Material backgroundMaterial;
            private Material bannerEffectMaterial;
            private string lastFaceBlendshapeKey;
            private PhotoshootLook.Rig lookRig;
            // The last turn and what it was measured on, to switch pivots without moving the avatar on screen.
            private Quaternion lastTurn = Quaternion.identity;
            private TurnPivot lastPivot;
            private readonly Dictionary<TurnPivot, Vector3> stagePivots = new Dictionary<TurnPivot, Vector3>();
            private RenderRequest lastRequest;
            private Color? lastAmbient;
            private Transform[] sourceTransforms, copyTransforms;
            private int[] sourceAppearance;
            private int bodyPoseRevision;
            private bool hasHeadRegion, hasTorsoRegion, hasBodyRegion;
            private Bounds headRegion, torsoRegion, bodyRegion;
            private ShotKind lastPreviewShotKind = ShotKind.Thumbnail;
            // The view position in the head's space, measured on the unposed copy, so the focus follows the head in any pose.
            private Transform viewHead;
            private Vector3 viewInHead;
            private bool hasView;

            public Texture PreviewTexture => GetPreviewTexture(lastPreviewShotKind);
            public bool IsOpen => scene.IsValid();
            public FrameInfo LastFrame { get; private set; }
            /// <summary>
            /// Set by a render that posed the copy or changed its expression. A camera render right after moving bones can
            /// still draw some skinned meshes (hair, accessories) from the previous frame's skinning until the editor has
            /// run an update, so such a render is followed by a couple more.
            /// </summary>
            public bool Unsettled { get; private set; }
            public void MarkSettled() => Unsettled = false;

            public Texture GetPreviewTexture(ShotKind shotKind)
            {
                return GetRenderTarget(shotKind).renderTexture;
            }

            public void UpdatePreview(RenderRequest request)
            {
                ValidateRequest(request);
                EnsureScene();
                PreviewRenderTarget target = GetRenderTarget(request.shotKind);
                EnsureRenderTexture(target, request.width, request.height, request.shotKind);
                EnsureCamera();
                // A new copy is made for each avatar or pose; framing, light and background changes reuse it as posed.
                bool avatarChanged = EnsureAvatarCopy(request.avatarRoot, request.bodyPose, out bool poseChanged);
                if (avatarChanged)
                {
                    DisableAnimationComponents(avatarCopy);
                    lastAmbient = null;
                    // The photoshoot camera renders right after bones move, outside Unity's frame; without this a
                    // render can reuse the skinning of the previous pose.
                    foreach (var renderer in avatarCopy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        renderer.forceMatrixRecalculationPerRender = true;
                }
                // Which way the head and eyes face is read before posing; the look starts from each new pose.
                if (avatarChanged) lookRig = PhotoshootLook.Prepare(avatarCopy);
                if (avatarChanged) MeasureViewPoint();
                if (avatarChanged || poseChanged)
                {
                    SampleBodyPose(avatarCopy, request.bodyPose);
                    PhotoshootLook.Capture(lookRig);
                }

                string faceBlendshapeKey = CreateFaceBlendshapeKey(request.selectedFaceBlendshapeNames);
                bool faceBlendshapesChanged =
                    request.forceFaceBlendshapeApply ||
                    avatarChanged ||
                    !string.Equals(lastFaceBlendshapeKey, faceBlendshapeKey, StringComparison.Ordinal);
                if (faceBlendshapesChanged)
                {
                    ResetAndApplyFaceBlendshapes(avatarCopy, request.selectedFaceBlendshapeNames);
                }
                if (faceBlendshapesChanged || poseChanged)
                {
                    Unsettled = true;
                    // Posed geometry is baked from every skinned mesh, by far the slowest step of a render. Culling
                    // bounds live in root-bone space and body regions in avatar space, so turning and framing never
                    // need them again.
                    MeasurePosedGeometry();
                }
                lastFaceBlendshapeKey = faceBlendshapeKey;
                // Placed unturned, then turned around the pivot bone: the camera is framed on the unturned avatar, so a
                // turn neither moves the camera nor slides the avatar to re-centre its changing outline.
                avatarCopy.transform.rotation = Quaternion.identity;
                Bounds stage = CenterOnStage();
                MeasureStagePivots(stage);
                Vector3 pivot = stagePivots[request.pivot];
                Quaternion turn = Quaternion.AngleAxis(request.avatarTiltDegrees, Vector3.right) * Quaternion.AngleAxis(request.avatarYawDegrees, Vector3.up);
                avatarCopy.transform.SetPositionAndRotation(pivot + turn * (avatarCopy.transform.position - pivot), turn);
                lastTurn = turn;
                lastPivot = request.pivot;
                lastRequest = request;
                Bounds bounds = VisibleBounds();

                LastFrame = MeasureFrame(bounds, stage);

                ConfigureCamera(camera, stage, request.shotKind, request.width, request.height, request.zoom, request.placement);
                PhotoshootLook.Apply(lookRig, camera.transform.position, request.lookAtCamera, request.lookWithEyes);
                camera.backgroundColor = request.backgroundColor;
                RebuildBackground(camera, stage, request.background, request.backgroundColor);
                var lightPreset = request.lightPreset ?? CreateLightPresets()[0];
                ApplyLiveLightPreset(lightPreset);
                Color ambient = lightPreset.ambientColor * Mathf.Max(0f, request.ambientIntensity);
                ambient.a = 1f;
                if (lastAmbient != ambient)
                {
                    ApplyStageAmbient(avatarCopy, ambient);
                    lastAmbient = ambient;
                }

                camera.targetTexture = target.sceneRenderTexture;
                // Asynchronous shader compilation draws not-yet-compiled variants as a cyan placeholder;
                // a photoshoot must show (and capture) the real materials.
                bool asyncCompilation = ShaderUtil.allowAsyncCompilation;
                ShaderUtil.allowAsyncCompilation = false;
                try
                {
                    if (faceBlendshapesChanged)
                    {
                        camera.Render();
                    }
                    camera.Render();
                    PhotoshootEffects.RenderDepth(camera, request.effects, ref target.depthTexture, request.width, request.height);
                }
                finally
                {
                    ShaderUtil.allowAsyncCompilation = asyncCompilation;
                }
                ApplyShotPostProcess(request, target);
                MarkRenderTargetUpdated(target);
                lastPreviewShotKind = request.shotKind;
            }

            /// <param name="withShotEffect">False: the shot as rendered, without the banner effect (for a server that applies it).</param>
            public Texture2D Capture(RenderRequest request, bool withShotEffect = true)
            {
                ValidateRequest(request);
                UpdatePreview(request);

                var target = GetRenderTarget(request.shotKind);
                RenderTexture previousActiveTexture = RenderTexture.active;
                try
                {
                    // Without the banner effect: the shot with its effects, already resolved.
                    RenderTexture.active = withShotEffect ? target.renderTexture : target.gradedRenderTexture;
                    var texture = new Texture2D(request.width, request.height, TextureFormat.RGBA32, false, false)
                    {
                        name = "Orbiters Photoshoot Capture"
                    };
                    texture.ReadPixels(new Rect(0, 0, request.width, request.height), 0, 0, false);
                    texture.Apply(false, false);
                    return texture;
                }
                finally
                {
                    RenderTexture.active = previousActiveTexture;
                }
            }

            public void Dispose()
            {
                if (camera != null)
                {
                    camera.targetTexture = null;
                }

                ReleaseRenderTarget(thumbnailTarget);
                ReleaseRenderTarget(bannerTarget);

                if (backgroundMaterial != null)
                {
                    UnityEngine.Object.DestroyImmediate(backgroundMaterial);
                    backgroundMaterial = null;
                }

                if (bannerEffectMaterial != null)
                {
                    UnityEngine.Object.DestroyImmediate(bannerEffectMaterial);
                    bannerEffectMaterial = null;
                }

                if (scene.IsValid())
                {
                    EditorSceneManager.ClosePreviewScene(scene);
                    scene = default(Scene);
                }

                avatarCopy = null;
                lastAvatarRoot = null;
                backgroundObject = null;
                keyLightObject = null;
                fillLightObject = null;
                rimLightObject = null;
                rim2LightObject = null;
                camera = null;
                lastFaceBlendshapeKey = null;
                lookRig = null;
            }

            private void EnsureScene()
            {
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    scene = EditorSceneManager.NewPreviewScene();
                }
            }

            private PreviewRenderTarget GetRenderTarget(ShotKind shotKind)
            {
                return shotKind == ShotKind.Banner ? bannerTarget : thumbnailTarget;
            }

            private void EnsureRenderTexture(PreviewRenderTarget target, int width, int height, ShotKind shotKind)
            {
                if (target.sceneRenderTexture != null &&
                    target.gradedRenderTexture != null &&
                    target.renderTexture != null &&
                    target.nextRenderTexture != null &&
                    target.width == width &&
                    target.height == height &&
                    target.sceneRenderTexture.IsCreated() &&
                    target.gradedRenderTexture.IsCreated() &&
                    target.renderTexture.IsCreated() &&
                    target.nextRenderTexture.IsCreated())
                {
                    return;
                }

                if (camera != null)
                {
                    camera.targetTexture = null;
                }

                ReleaseRenderTarget(target);

                target.width = width;
                target.height = height;
                string namePrefix = shotKind == ShotKind.Banner ? "Banner" : "Thumbnail";
                target.sceneRenderTexture = CreateRenderTexture(width, height, 24, $"Orbiters Photoshoot {namePrefix} Scene Render", antiAlias: true);
                target.gradedRenderTexture = CreateRenderTexture(width, height, 0, $"Orbiters Photoshoot {namePrefix} Graded", antiAlias: false);
                target.renderTexture = CreateRenderTexture(width, height, 0, $"Orbiters Photoshoot {namePrefix} Live Preview", antiAlias: false);
                target.nextRenderTexture = CreateRenderTexture(width, height, 0, $"Orbiters Photoshoot {namePrefix} Live Preview Next", antiAlias: false);
                ClearRenderTexture(target.sceneRenderTexture, PreviewClearColor);
                ClearRenderTexture(target.renderTexture, PreviewClearColor);
                ClearRenderTexture(target.nextRenderTexture, PreviewClearColor);
            }

            private void EnsureCamera()
            {
                if (camera != null)
                {
                    return;
                }

                var cameraGo = CreateVisibleSceneObject("Photoshoot Camera", scene);
                camera = cameraGo.AddComponent<Camera>();
                camera.hideFlags = HideFlags.DontSave;
                // Renders on demand, and only the objects and lights of this photoshoot's scene.
                camera.enabled = false;
                camera.scene = scene;
            }

            private bool EnsureAvatarCopy(GameObject avatarRoot, AnimationClip bodyPose, out bool poseChanged)
            {
                poseChanged = false;
                int[] currentAppearance = CaptureSourceAppearance(avatarRoot);
                bool sourceUnchanged = sourceAppearance != null && sourceAppearance.SequenceEqual(currentAppearance);
                int currentPoseRevision = bodyPose != null ? EditorUtility.GetDirtyCount(bodyPose) : 0;
                // By reference: a removed pose of the user's is destroyed, and would equal the default pose (null) otherwise.
                if (avatarCopy != null && lastAvatarRoot == avatarRoot && sourceUnchanged && ReferenceEquals(lastBodyPose, bodyPose) && bodyPoseRevision == currentPoseRevision)
                {
                    return false;
                }

                // Another pose on the same avatar: put every bone back where the avatar has it, then sample the new pose.
                // Duplicating a full avatar again takes far longer than copying its transforms.
                if (avatarCopy != null && lastAvatarRoot == avatarRoot && sourceUnchanged && RestoreSourcePose(avatarRoot))
                {
                    lastBodyPose = bodyPose;
                    bodyPoseRevision = currentPoseRevision;
                    poseChanged = true;
                    return false;
                }

                if (avatarCopy != null)
                {
                    UnityEngine.Object.DestroyImmediate(avatarCopy);
                    avatarCopy = null;
                }

                avatarCopy = UnityEngine.Object.Instantiate(avatarRoot, scene) as GameObject;
                if (avatarCopy == null)
                {
                    throw new InvalidOperationException("Could not duplicate avatar into the photoshoot scene.");
                }

                avatarCopy.name = avatarRoot.name + " Photoshoot";
                avatarCopy.hideFlags = HideFlags.DontSave;
                avatarCopy.transform.position = Vector3.zero;
                avatarCopy.transform.rotation = Quaternion.identity;
                DisableAudioListeners(avatarCopy);
                sourceTransforms = avatarRoot.GetComponentsInChildren<Transform>(true);
                copyTransforms = avatarCopy.GetComponentsInChildren<Transform>(true);
                HideEditorHelpers(sourceTransforms, copyTransforms);

                lastAvatarRoot = avatarRoot;
                sourceAppearance = currentAppearance;
                bodyPoseRevision = currentPoseRevision;
                lastBodyPose = bodyPose;
                lastFaceBlendshapeKey = null;
                return true;
            }

            // Helpers other editor tools hang on the avatar without ever saving them (XRay Gizmos' armature): not part of
            // the photo, nor of its framing. Turned off rather than removed, so the copy's bones keep matching the source's.
            private static void HideEditorHelpers(Transform[] source, Transform[] copy)
            {
                for (int i = 1; i < source.Length && i < copy.Length; i++)
                    if ((source[i].gameObject.hideFlags & HideFlags.DontSaveInEditor) != 0) copy[i].gameObject.SetActive(false);
            }

            // Compare source state, not the posed clone. Rebuilding only after a source edit keeps camera-only
            // previews cheap while captures always reflect current meshes, materials, visibility and hierarchy.
            private static int[] CaptureSourceAppearance(GameObject root)
            {
                var values = new List<int>();
                void ObjectState(UnityEngine.Object value)
                {
                    values.Add(value != null ? value.GetInstanceID() : 0);
                    values.Add(value != null ? EditorUtility.GetDirtyCount(value) : 0);
                }
                void Vector(Vector3 value) { values.Add(value.x.GetHashCode()); values.Add(value.y.GetHashCode()); values.Add(value.z.GetHashCode()); }
                var transforms = root.GetComponentsInChildren<Transform>(true);
                values.Add(transforms.Length);
                foreach (var transform in transforms)
                {
                    values.Add(transform.GetInstanceID());
                    values.Add(transform.parent != null ? transform.parent.GetInstanceID() : 0);
                    values.Add(transform.gameObject.activeSelf ? 1 : 0);
                    Vector(transform.localPosition);
                    Vector(transform.localScale);
                    var rotation = transform.localRotation;
                    values.Add(rotation.x.GetHashCode()); values.Add(rotation.y.GetHashCode());
                    values.Add(rotation.z.GetHashCode()); values.Add(rotation.w.GetHashCode());
                }
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                values.Add(renderers.Length);
                foreach (var renderer in renderers)
                {
                    values.Add(renderer.GetInstanceID());
                    values.Add(renderer.enabled ? 1 : 0);
                    var materials = renderer.sharedMaterials;
                    values.Add(materials.Length);
                    foreach (var material in materials) ObjectState(material);
                    if (renderer is SkinnedMeshRenderer skinned)
                    {
                        ObjectState(skinned.sharedMesh);
                        ObjectState(skinned.rootBone);
                        var bones = skinned.bones;
                        values.Add(bones.Length);
                        foreach (var bone in bones) values.Add(bone != null ? bone.GetInstanceID() : 0);
                        int shapes = skinned.sharedMesh != null ? skinned.sharedMesh.blendShapeCount : 0;
                        values.Add(shapes);
                        for (int i = 0; i < shapes; i++) values.Add(skinned.GetBlendShapeWeight(i).GetHashCode());
                    }
                    else if (renderer is MeshRenderer)
                    {
                        // Unity's missing-component wrapper is not CLR null, so ?. can still throw.
                        // Trails, lines and particles have no MeshFilter; retain their state above.
                        var filter = renderer.GetComponent<MeshFilter>();
                        ObjectState(filter != null ? filter.sharedMesh : null);
                    }
                }
                return values.ToArray();
            }

            // One bake per skinned mesh gives both its posed culling bounds and the head and torso regions, measured on
            // the vertices each bone moves, in avatar space so turning the avatar keeps them valid.
            private void MeasurePosedGeometry()
            {
                hasHeadRegion = hasTorsoRegion = hasBodyRegion = false;
                var animator = avatarCopy.GetComponentInChildren<Animator>(true);
                bool human = animator != null && animator.isHuman;
                Transform head = human ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
                Transform spine = human ? animator.GetBoneTransform(HumanBodyBones.Spine) : null;
                Transform leftForearm = human ? animator.GetBoneTransform(HumanBodyBones.LeftLowerArm) : null;
                Transform rightForearm = human ? animator.GetBoneTransform(HumanBodyBones.RightLowerArm) : null;
                var regions = new Dictionary<Transform, int>();
                int RegionOf(Transform bone)
                {
                    if (bone == null) return 0;
                    if (regions.TryGetValue(bone, out int known)) return known;
                    int region = 0;
                    for (var t = bone; t != null; t = t.parent)
                    {
                        if (t == head) { region = 3; break; }          // head and torso
                        if (t == leftForearm || t == rightForearm) break; // forearms and hands: neither
                        if (t == spine) { region = 1; break; }           // torso only
                    }
                    regions[bone] = region;
                    return region;
                }

                Matrix4x4 toAvatar = avatarCopy.transform.worldToLocalMatrix;
                var baked = new Mesh();
                try
                {
                    foreach (var renderer in avatarCopy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (renderer == null || renderer.sharedMesh == null) continue;
                        // Nothing ever looks at the photoshoot scene, so only off-screen updates keep skinning current for
                        // the photoshoot camera. Unity's bounds for those are loose boxes around each bone; framing uses
                        // the baked vertices instead.
                        renderer.updateWhenOffscreen = true;
                        if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                        renderer.BakeMesh(baked);
                        var vertices = baked.vertices;
                        if (vertices.Length == 0) continue;
                        Matrix4x4 matrix = toAvatar * SkinnedMeshBounds.BakedToWorld(renderer);
                        for (int i = 0; i < vertices.Length; i++) Include(ref bodyRegion, ref hasBodyRegion, matrix.MultiplyPoint3x4(vertices[i]));
                        if (!human) continue;

                        var bones = renderer.bones;
                        var weights = renderer.sharedMesh.boneWeights;
                        if (weights.Length != vertices.Length || bones.Length == 0) continue;
                        var boneRegions = new int[bones.Length];
                        for (int i = 0; i < bones.Length; i++) boneRegions[i] = RegionOf(bones[i]);
                        for (int i = 0; i < vertices.Length; i++)
                        {
                            int bone = weights[i].boneIndex0;
                            int region = bone >= 0 && bone < boneRegions.Length ? boneRegions[bone] : 0;
                            if (region == 0) continue;
                            Vector3 point = matrix.MultiplyPoint3x4(vertices[i]);
                            if (!hasTorsoRegion) { torsoRegion = new Bounds(point, Vector3.zero); hasTorsoRegion = true; }
                            else torsoRegion.Encapsulate(point);
                            if (region != 3) continue;
                            if (!hasHeadRegion) { headRegion = new Bounds(point, Vector3.zero); hasHeadRegion = true; }
                            else headRegion.Encapsulate(point);
                        }
                    }
                    // Rigid meshes (accessories, props) count with their own bounds; particles, trails and lines do not.
                    foreach (var renderer in avatarCopy.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.bounds.size.sqrMagnitude <= 0f) continue;
                        var local = TransformBounds(toAvatar, renderer.bounds);
                        Include(ref bodyRegion, ref hasBodyRegion, local.min);
                        Include(ref bodyRegion, ref hasBodyRegion, local.max);
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(baked);
                }
            }

            private static void Include(ref Bounds bounds, ref bool has, Vector3 point)
            {
                if (!has) { bounds = new Bounds(point, Vector3.zero); has = true; }
                else bounds.Encapsulate(point);
            }

            // The avatar's outline for centring and framing: the baked geometry when measured, else the renderers' bounds.
            private Bounds VisibleBounds() =>
                hasBodyRegion ? TransformBounds(avatarCopy.transform.localToWorldMatrix, bodyRegion) : CalculateVisibleBounds(avatarCopy);

            private Bounds CenterOnStage()
            {
                Bounds bounds = VisibleBounds();
                avatarCopy.transform.position += new Vector3(LiveSceneStageOrigin.x - bounds.center.x, LiveSceneStageOrigin.y - bounds.min.y, LiveSceneStageOrigin.z - bounds.center.z);
                return VisibleBounds();
            }

            // Hips, chest and head of the unturned avatar; the middle of its outline stands in for a missing bone.
            private void MeasureStagePivots(Bounds stage)
            {
                var animator = avatarCopy.GetComponentInChildren<Animator>(true);
                bool human = animator != null && animator.isHuman;
                Vector3 Position(HumanBodyBones bone, HumanBodyBones fallback, float height)
                {
                    var t = human ? animator.GetBoneTransform(bone) ?? animator.GetBoneTransform(fallback) : null;
                    return t != null ? t.position : new Vector3(stage.center.x, stage.min.y + stage.size.y * height, stage.center.z);
                }
                stagePivots[TurnPivot.Hips] = Position(HumanBodyBones.Hips, HumanBodyBones.Hips, .5f);
                stagePivots[TurnPivot.Chest] = Position(HumanBodyBones.UpperChest, HumanBodyBones.Chest, .7f);
                stagePivots[TurnPivot.Head] = Position(HumanBodyBones.Head, HumanBodyBones.Neck, .88f);
            }

            /// <summary>
            /// The pivot nearest the middle of the last frame, and the placement change that keeps the avatar where it is on
            /// screen when the turn moves to that pivot.
            /// </summary>
            public TurnPivot NearestPivot(out Vector2 placementShift)
            {
                placementShift = Vector2.zero;
                if (camera == null || avatarCopy == null || lastRequest == null || stagePivots.Count == 0) return lastPivot;
                TurnPivot nearest = lastPivot;
                float best = float.MaxValue;
                foreach (var pair in stagePivots)
                {
                    // Where each pivot shows now, as turned.
                    Vector3 shown = stagePivots[lastPivot] + lastTurn * (pair.Value - stagePivots[lastPivot]);
                    Vector3 viewport = camera.WorldToViewportPoint(shown);
                    float distance = viewport.z <= 0f ? float.MaxValue : new Vector2(viewport.x - .5f, viewport.y - .5f).sqrMagnitude;
                    if (distance < best) { best = distance; nearest = pair.Key; }
                }
                if (nearest == lastPivot) return nearest;
                // Turning around another point moves everything by (I - R)(new - old); the camera follows it on screen.
                Vector3 offset = stagePivots[nearest] - stagePivots[lastPivot];
                Vector3 moved = offset - lastTurn * offset;
                var frame = FrameFor(LastFrame.stage, lastRequest.shotKind, lastRequest.width, lastRequest.height, lastRequest.zoom);
                placementShift = new Vector2(
                    -Vector3.Dot(moved, Vector3.left) / (frame.baseHorizontalSpan * PlacementFrameStrength),
                    -Vector3.Dot(moved, Vector3.up) / (frame.baseVerticalSpan * PlacementFrameStrength));
                return nearest;
            }

            private FrameInfo MeasureFrame(Bounds bounds, Bounds stage)
            {
                float height = bounds.size.y;
                var frame = new FrameInfo
                {
                    valid = true,
                    bounds = bounds,
                    stage = stage,
                    chest = new Vector3(bounds.center.x, bounds.min.y + height * 0.70f, bounds.center.z),
                    hips = new Vector3(bounds.center.x, bounds.min.y + height * 0.50f, bounds.center.z)
                };
                var animator = avatarCopy.GetComponentInChildren<Animator>(true);
                if (animator != null && animator.isHuman)
                {
                    Transform chest = animator.GetBoneTransform(HumanBodyBones.UpperChest);
                    if (chest == null) chest = animator.GetBoneTransform(HumanBodyBones.Chest);
                    Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                    if (chest != null) frame.chest = chest.position;
                    if (hips != null) frame.hips = hips.position;
                }

                Matrix4x4 toWorld = avatarCopy.transform.localToWorldMatrix;
                if (hasHeadRegion) { frame.hasHead = true; frame.head = TransformBounds(toWorld, headRegion); }
                if (hasTorsoRegion) { frame.hasTorso = true; frame.torso = TransformBounds(toWorld, torsoRegion); }
                return frame;
            }

            private static Bounds TransformBounds(Matrix4x4 matrix, Bounds local)
            {
                Vector3 min = local.min, max = local.max;
                var result = new Bounds(matrix.MultiplyPoint3x4(min), Vector3.zero);
                for (int corner = 1; corner < 8; corner++)
                {
                    result.Encapsulate(matrix.MultiplyPoint3x4(new Vector3(
                        (corner & 1) != 0 ? max.x : min.x,
                        (corner & 2) != 0 ? max.y : min.y,
                        (corner & 4) != 0 ? max.z : min.z)));
                }
                return result;
            }

            private bool RestoreSourcePose(GameObject avatarRoot)
            {
                if (sourceTransforms == null || copyTransforms == null || sourceTransforms.Length != copyTransforms.Length ||
                    sourceTransforms.Length != avatarRoot.GetComponentsInChildren<Transform>(true).Length)
                {
                    return false;
                }

                // The root keeps its stage placement; everything below takes the source's local pose again.
                for (int i = 1; i < sourceTransforms.Length; i++)
                {
                    var source = sourceTransforms[i];
                    var copy = copyTransforms[i];
                    if (source == null || copy == null) return false;
                    copy.localPosition = source.localPosition;
                    copy.localRotation = source.localRotation;
                    copy.localScale = source.localScale;
                }

                return true;
            }

            private void RebuildBackground(Camera previewCamera, Bounds bounds, Texture2D texture, Color color)
            {
                if (backgroundObject != null && backgroundMaterial != null)
                {
                    ConfigureBackground(backgroundObject, backgroundMaterial, previewCamera, bounds, texture, color);
                    return;
                }

                if (backgroundMaterial != null)
                {
                    UnityEngine.Object.DestroyImmediate(backgroundMaterial);
                    backgroundMaterial = null;
                }
                if (backgroundObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(backgroundObject);
                    backgroundObject = null;
                }

                backgroundMaterial = CreateBackground(previewCamera, bounds, texture, color, scene, out backgroundObject, visible: true);
            }

            private void ApplyLiveLightPreset(LightPresetOption preset)
            {
                var keyLight = EnsureLiveLight(ref keyLightObject, "Key Light", LightType.Directional);
                keyLightObject.transform.position = LiveSceneStageOrigin;
                keyLightObject.transform.rotation = Quaternion.Euler(preset.keyRotation);
                keyLight.color = preset.keyColor;
                keyLight.intensity = preset.keyIntensity;
                keyLight.shadows = LightShadows.None;

                var fillLight = EnsureLiveLight(ref fillLightObject, "Fill Light", LightType.Point);
                fillLightObject.transform.position = LiveSceneStageOrigin + preset.fillPosition;
                fillLight.color = preset.fillColor;
                fillLight.intensity = preset.fillIntensity;
                fillLight.range = 5f;
                fillLight.shadows = LightShadows.None;

                var rimLight = EnsureLiveLight(ref rimLightObject, "Rim Light", LightType.Directional);
                rimLightObject.transform.position = LiveSceneStageOrigin;
                rimLightObject.transform.rotation = Quaternion.Euler(preset.rimRotation);
                rimLight.color = preset.rimColor;
                rimLight.intensity = preset.rimIntensity;
                rimLight.shadows = LightShadows.None;

                var rim2Light = EnsureLiveLight(ref rim2LightObject, "Rim Light 2", LightType.Directional);
                rim2LightObject.transform.position = LiveSceneStageOrigin;
                rim2LightObject.transform.rotation = Quaternion.Euler(preset.rim2Rotation);
                rim2Light.color = preset.rim2Color;
                rim2Light.intensity = preset.rim2Intensity;
                rim2Light.shadows = LightShadows.None;
                rim2Light.enabled = preset.rim2Intensity > 0f;
            }

            // The preset's ambient reaches the avatar copy through its own light probe data, so the photoshoot looks the
            // same in every project and a dark preset stays dark, without touching the lighting of the user's scene.
            private static void ApplyStageAmbient(GameObject avatar, Color ambient)
            {
                var probe = new UnityEngine.Rendering.SphericalHarmonicsL2();
                probe.AddAmbientLight(QualitySettings.activeColorSpace == ColorSpace.Linear ? ambient.linear : ambient);
                var probes = new[] { probe };
                var block = new MaterialPropertyBlock();
                foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.CustomProvided;
                    renderer.GetPropertyBlock(block);
                    block.CopySHCoefficientArraysFrom(probes);
                    renderer.SetPropertyBlock(block);
                }
            }

            private Light EnsureLiveLight(ref GameObject lightObject, string name, LightType lightType)
            {
                if (lightObject == null)
                {
                    lightObject = CreateVisibleSceneObject(name, scene);
                    var newLight = lightObject.AddComponent<Light>();
                    newLight.type = lightType;
                    return newLight;
                }

                var light = lightObject.GetComponent<Light>();
                if (light == null)
                {
                    light = lightObject.AddComponent<Light>();
                }
                light.type = lightType;
                return light;
            }

            private void ApplyShotPostProcess(RenderRequest request, PreviewRenderTarget target)
            {
                if (target.sceneRenderTexture == null || target.renderTexture == null)
                {
                    return;
                }

                // The scene render is multisampled: resolved, with the effects over it when some are on.
                if (request.effects != null && request.effects.Count > 0)
                {
                    var resolved = RenderTexture.GetTemporary(target.width, target.height, 0, RenderTextureFormat.ARGB32);
                    try
                    {
                        Graphics.Blit(target.sceneRenderTexture, resolved);
                        PhotoshootEffects.Apply(resolved, target.gradedRenderTexture, request.effects.NeedsDepth ? target.depthTexture : null, camera,
                            FocusDistance(), request.effects, request.frameEffects);
                    }
                    finally
                    {
                        RenderTexture.ReleaseTemporary(resolved);
                    }
                }
                else
                {
                    Graphics.Blit(target.sceneRenderTexture, target.gradedRenderTexture);
                }

                RenderTexture output = target.nextRenderTexture != null ? target.nextRenderTexture : target.renderTexture;
                if (request.shotKind == ShotKind.Banner && EnsureBannerEffectMaterial())
                {
                    bannerEffectMaterial.SetColor("_OverlayColor", BannerEffectOverlayColor);
                    // The same blur at every size (48 texels at 1600 wide): the smaller live preview looks like the capture.
                    bannerEffectMaterial.SetFloat("_MaxBlurTexels", BannerEffectMaxBlurTexels * output.width / 1600f);
                    Graphics.Blit(target.gradedRenderTexture, output, bannerEffectMaterial);
                    SwapPreviewRenderTexture(target);
                    return;
                }

                Graphics.Blit(target.gradedRenderTexture, output);
                SwapPreviewRenderTexture(target);
            }

            // Where the avatar sees from (its view position, else its head), carried by the head bone through any pose.
            private void MeasureViewPoint()
            {
                hasView = false;
                viewHead = null;
                var animator = avatarCopy.GetComponentInChildren<Animator>(true);
                Transform head = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
                Vector3? local = ViewPosition?.Invoke(avatarCopy);
                if (!local.HasValue && head == null) return;
                Vector3 world = local.HasValue ? avatarCopy.transform.TransformPoint(local.Value) : head.position;
                viewHead = head != null ? head : avatarCopy.transform;
                viewInHead = viewHead.InverseTransformPoint(world);
                hasView = true;
            }

            // How far in front of the camera the avatar sees from: the depth of field is sharp there.
            private float FocusDistance()
            {
                Vector3 point = hasView && viewHead != null ? viewHead.TransformPoint(viewInHead)
                    : LastFrame.hasHead ? LastFrame.head.center
                    : LastFrame.bounds.center + Vector3.up * LastFrame.bounds.extents.y * 0.8f;
                return Mathf.Max(0.05f, Vector3.Dot(point - camera.transform.position, camera.transform.forward));
            }

            private static void SwapPreviewRenderTexture(PreviewRenderTarget target)
            {
                if (target.nextRenderTexture == null)
                {
                    return;
                }

                RenderTexture previous = target.renderTexture;
                target.renderTexture = target.nextRenderTexture;
                target.nextRenderTexture = previous;
            }

            private static void MarkRenderTargetUpdated(PreviewRenderTarget target)
            {
                if (target == null)
                {
                    return;
                }

                IncrementTextureUpdateCount(target.sceneRenderTexture);
                IncrementTextureUpdateCount(target.renderTexture);
                IncrementTextureUpdateCount(target.nextRenderTexture);
            }

            private bool EnsureBannerEffectMaterial()
            {
                if (bannerEffectMaterial != null)
                {
                    return true;
                }

                Shader shader = Shader.Find(BannerEffectShaderName);
                if (shader == null)
                {
                    shader = AssetDatabase.LoadAssetAtPath<Shader>(BannerEffectShaderPath);
                }
                if (shader == null)
                {
                    return false;
                }

                bannerEffectMaterial = new Material(shader)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                return true;
            }

            private static RenderTexture CreateRenderTexture(int width, int height, int depth, string name, bool antiAlias)
            {
                var texture = new RenderTexture(width, height, depth, RenderTextureFormat.ARGB32)
                {
                    antiAliasing = antiAlias ? 4 : 1,
                    autoGenerateMips = false,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave,
                    name = name,
                    useMipMap = false,
                    wrapMode = TextureWrapMode.Clamp
                };
                texture.Create();
                return texture;
            }

            private static void ClearRenderTexture(RenderTexture texture, Color color)
            {
                if (texture == null)
                {
                    return;
                }

                RenderTexture previousActiveTexture = RenderTexture.active;
                try
                {
                    RenderTexture.active = texture;
                    GL.Clear(true, true, color);
                }
                finally
                {
                    RenderTexture.active = previousActiveTexture;
                }
            }

            private static void ReleaseRenderTexture(ref RenderTexture texture)
            {
                if (texture == null)
                {
                    return;
                }

                if (RenderTexture.active == texture)
                {
                    RenderTexture.active = null;
                }
                texture.Release();
                UnityEngine.Object.DestroyImmediate(texture);
                texture = null;
            }

            private static void ReleaseRenderTarget(PreviewRenderTarget target)
            {
                ReleaseRenderTexture(ref target.gradedRenderTexture);
                PhotoshootEffects.Release(ref target.depthTexture);
                ReleaseRenderTexture(ref target.sceneRenderTexture);
                ReleaseRenderTexture(ref target.renderTexture);
                ReleaseRenderTexture(ref target.nextRenderTexture);
                target.width = 0;
                target.height = 0;
            }
        }

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

        public static List<LightPresetOption> CreateLightPresets()
        {
            return new List<LightPresetOption>
            {
                new LightPresetOption
                {
                    displayName = "Studio Soft",
                    ambientColor = new Color(0.34f, 0.34f, 0.36f),
                    keyColor = Color.white,
                    keyIntensity = 1.15f,
                    keyRotation = new Vector3(42f, -34f, 0f),
                    fillColor = new Color(0.78f, 0.86f, 1f),
                    fillIntensity = 0.55f,
                    fillPosition = new Vector3(-1.9f, 1.35f, 2.2f),
                    rimColor = new Color(0.82f, 0.92f, 1f),
                    rimIntensity = 0.55f,
                    rimRotation = new Vector3(145f, 28f, 0f)
                },
                new LightPresetOption
                {
                    displayName = "Product Bright",
                    ambientColor = new Color(0.45f, 0.45f, 0.45f),
                    keyColor = Color.white,
                    keyIntensity = 1.35f,
                    keyRotation = new Vector3(38f, -12f, 0f),
                    fillColor = Color.white,
                    fillIntensity = 0.75f,
                    fillPosition = new Vector3(-1.6f, 1.1f, 2.0f),
                    rimColor = new Color(0.76f, 0.84f, 1f),
                    rimIntensity = 0.35f,
                    rimRotation = new Vector3(150f, 42f, 0f)
                },
                new LightPresetOption
                {
                    displayName = "Dramatic Rim",
                    ambientColor = new Color(0.18f, 0.18f, 0.20f),
                    keyColor = new Color(1f, 0.92f, 0.82f),
                    keyIntensity = 0.95f,
                    keyRotation = new Vector3(48f, -48f, 0f),
                    fillColor = new Color(0.58f, 0.66f, 1f),
                    fillIntensity = 0.25f,
                    fillPosition = new Vector3(-2.2f, 1.0f, 2.4f),
                    rimColor = new Color(0.62f, 0.86f, 1f),
                    rimIntensity = 1.15f,
                    rimRotation = new Vector3(140f, 34f, 0f)
                },
                new LightPresetOption
                {
                    displayName = "Warm Sunset",
                    ambientColor = new Color(0.30f, 0.24f, 0.22f),
                    keyColor = new Color(1f, 0.70f, 0.46f),
                    keyIntensity = 1.2f,
                    keyRotation = new Vector3(34f, -62f, 0f),
                    fillColor = new Color(0.62f, 0.74f, 1f),
                    fillIntensity = 0.35f,
                    fillPosition = new Vector3(-1.9f, 1.25f, 2.2f),
                    rimColor = new Color(1f, 0.82f, 0.58f),
                    rimIntensity = 0.8f,
                    rimRotation = new Vector3(136f, 48f, 0f)
                },
                new LightPresetOption
                {
                    displayName = "Cool Outdoor",
                    ambientColor = new Color(0.30f, 0.34f, 0.40f),
                    keyColor = new Color(0.86f, 0.94f, 1f),
                    keyIntensity = 1.05f,
                    keyRotation = new Vector3(52f, -26f, 0f),
                    fillColor = new Color(0.72f, 0.82f, 1f),
                    fillIntensity = 0.5f,
                    fillPosition = new Vector3(-1.7f, 1.2f, 2.3f),
                    rimColor = new Color(0.78f, 0.94f, 1f),
                    rimIntensity = 0.65f,
                    rimRotation = new Vector3(150f, 24f, 0f)
                },
                new LightPresetOption
                {
                    displayName = "High Key",
                    ambientColor = new Color(0.62f, 0.62f, 0.64f),
                    keyColor = Color.white,
                    keyIntensity = 1.1f,
                    keyRotation = new Vector3(30f, -8f, 0f),
                    fillColor = Color.white,
                    fillIntensity = 0.9f,
                    fillPosition = new Vector3(-1.4f, 1.2f, 2.0f),
                    rimColor = Color.white,
                    rimIntensity = 0.45f,
                    rimRotation = new Vector3(150f, 30f, 0f)
                },
                new LightPresetOption
                {
                    displayName = "Low Key",
                    ambientColor = new Color(0.07f, 0.07f, 0.08f),
                    keyColor = new Color(1f, 0.95f, 0.88f),
                    keyIntensity = 1.25f,
                    keyRotation = new Vector3(28f, -78f, 0f),
                    fillColor = new Color(0.6f, 0.65f, 0.8f),
                    fillIntensity = 0.06f,
                    fillPosition = new Vector3(-2.2f, 1.0f, 2.4f),
                    rimColor = new Color(0.85f, 0.9f, 1f),
                    rimIntensity = 0.35f,
                    rimRotation = new Vector3(150f, 60f, 0f)
                },
                new LightPresetOption
                {
                    displayName = "Cinematic Rim",
                    // Avatar shaders are mostly toon shaders that light a surface by the strongest light whatever its side,
                    // so a dark, rim-lit look comes from a low side key with softer coloured rims, not strong back lights.
                    ambientColor = new Color(0.04f, 0.045f, 0.06f),
                    keyColor = new Color(1f, 0.93f, 0.85f),
                    keyIntensity = 0.85f,
                    keyRotation = new Vector3(26f, -80f, 0f),
                    fillColor = new Color(0.4f, 0.5f, 0.8f),
                    fillIntensity = 0.03f,
                    fillPosition = new Vector3(-2.0f, 1.2f, 2.4f),
                    rimColor = new Color(0.35f, 0.85f, 1f),
                    rimIntensity = 0.55f,
                    rimRotation = new Vector3(165f, 55f, 0f),
                    rim2Color = new Color(1f, 0.6f, 0.3f),
                    rim2Intensity = 0.45f,
                    rim2Rotation = new Vector3(165f, -55f, 0f)
                },
                new LightPresetOption
                {
                    displayName = "Neon Night",
                    ambientColor = new Color(0.07f, 0.05f, 0.12f),
                    keyColor = new Color(0.85f, 0.5f, 1f),
                    keyIntensity = 0.7f,
                    keyRotation = new Vector3(30f, -70f, 0f),
                    fillColor = new Color(0.4f, 0.3f, 0.9f),
                    fillIntensity = 0.08f,
                    fillPosition = new Vector3(-2.0f, 1.2f, 2.4f),
                    rimColor = new Color(0.2f, 0.9f, 1f),
                    rimIntensity = 0.6f,
                    rimRotation = new Vector3(160f, 55f, 0f),
                    rim2Color = new Color(1f, 0.3f, 0.7f),
                    rim2Intensity = 0.5f,
                    rim2Rotation = new Vector3(160f, -55f, 0f)
                }
            };
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

        public static void SampleBodyPose(GameObject avatarRoot, AnimationClip clip)
        {
            if (avatarRoot == null || clip == null)
            {
                return;
            }

            // Humanoid sampling can restore the imported Avatar's bone translations.
            // A custom base may have moved eyes or changed limb proportions since import.
            var animator = avatarRoot.GetComponentInChildren<Animator>(true);
            var hips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            var bones = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .SelectMany(renderer => renderer.bones).Where(bone => bone != null).Distinct().ToArray();
            var positions = bones.Select(bone => bone.localPosition).ToArray();
            var scales = bones.Select(bone => bone.localScale).ToArray();
            clip.SampleAnimation(avatarRoot, 0f);
            SampleHumanoidBodyPose(avatarRoot, clip);
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != hips) bones[i].localPosition = positions[i];
                bones[i].localScale = scales[i];
            }
        }

        private static void SampleHumanoidBodyPose(GameObject avatarRoot, AnimationClip clip)
        {
            var animator = avatarRoot.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
            {
                return;
            }

            var humanPose = new HumanPose();
            using (var humanPoseHandler = new HumanPoseHandler(animator.avatar, animator.transform))
            {
                humanPoseHandler.GetHumanPose(ref humanPose);
                if (ApplyHumanoidClipCurves(clip, ref humanPose))
                {
                    humanPoseHandler.SetHumanPose(ref humanPose);
                }
            }
        }

        private static bool ApplyHumanoidClipCurves(AnimationClip clip, ref HumanPose humanPose)
        {
            bool changed = false;
            bool rootRotationChanged = false;
            Dictionary<string, int> muscleIndices = BuildHumanoidMuscleIndexLookup();
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null)
                {
                    continue;
                }

                float value = curve.Evaluate(0f);
                if (TryGetHumanoidMuscleIndex(binding.propertyName, muscleIndices, out int muscleIndex) &&
                    humanPose.muscles != null &&
                    muscleIndex < humanPose.muscles.Length)
                {
                    humanPose.muscles[muscleIndex] = value;
                    changed = true;
                    continue;
                }

                if (ApplyHumanoidRootCurve(binding.propertyName, value, ref humanPose, ref rootRotationChanged))
                {
                    changed = true;
                }
            }

            if (rootRotationChanged)
            {
                humanPose.bodyRotation = NormalizeQuaternion(humanPose.bodyRotation);
            }

            return changed;
        }

        private static bool TryGetHumanoidMuscleIndex(
            string propertyName,
            Dictionary<string, int> muscleIndices,
            out int muscleIndex)
        {
            if (muscleIndices.TryGetValue(propertyName, out muscleIndex))
            {
                return true;
            }

            string normalizedName = NormalizeHumanoidMuscleName(propertyName);
            return !string.IsNullOrEmpty(normalizedName) && muscleIndices.TryGetValue(normalizedName, out muscleIndex);
        }

        private static Dictionary<string, int> BuildHumanoidMuscleIndexLookup()
        {
            var output = new Dictionary<string, int>(StringComparer.Ordinal);
            string[] muscleNames = HumanTrait.MuscleName;
            for (int i = 0; i < muscleNames.Length; i++)
            {
                string muscleName = muscleNames[i];
                if (string.IsNullOrEmpty(muscleName))
                {
                    continue;
                }

                output[muscleName] = i;
                string normalizedName = NormalizeHumanoidMuscleName(muscleName);
                if (!string.IsNullOrEmpty(normalizedName))
                {
                    output[normalizedName] = i;
                }
            }

            return output;
        }

        private static string NormalizeHumanoidMuscleName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            value = value
                .Replace("LeftHand", "Left Hand")
                .Replace("RightHand", "Right Hand");

            var builder = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    builder.Append(' ');
                }
            }

            string[] tokens = builder
                .ToString()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                return string.Empty;
            }

            return string.Concat(tokens.Where(token => !string.Equals(token, "hand", StringComparison.Ordinal)));
        }

        private static bool ApplyHumanoidRootCurve(string propertyName, float value, ref HumanPose humanPose, ref bool rootRotationChanged)
        {
            switch (propertyName)
            {
                case "RootT.x":
                    humanPose.bodyPosition.x = value;
                    return true;
                case "RootT.y":
                    humanPose.bodyPosition.y = value;
                    return true;
                case "RootT.z":
                    humanPose.bodyPosition.z = value;
                    return true;
                case "RootQ.x":
                    humanPose.bodyRotation.x = value;
                    rootRotationChanged = true;
                    return true;
                case "RootQ.y":
                    humanPose.bodyRotation.y = value;
                    rootRotationChanged = true;
                    return true;
                case "RootQ.z":
                    humanPose.bodyRotation.z = value;
                    rootRotationChanged = true;
                    return true;
                case "RootQ.w":
                    humanPose.bodyRotation.w = value;
                    rootRotationChanged = true;
                    return true;
                default:
                    return false;
            }
        }

        private static Quaternion NormalizeQuaternion(Quaternion value)
        {
            float magnitude = Mathf.Sqrt(
                value.x * value.x +
                value.y * value.y +
                value.z * value.z +
                value.w * value.w);
            if (magnitude <= Mathf.Epsilon)
            {
                return Quaternion.identity;
            }

            return new Quaternion(
                value.x / magnitude,
                value.y / magnitude,
                value.z / magnitude,
                value.w / magnitude);
        }

        private static void ResetAndApplyFaceBlendshapes(GameObject avatarRoot, IEnumerable<string> selectedFaceBlendshapeNames)
        {
            var selected = new HashSet<string>(selectedFaceBlendshapeNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var renderer in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer != null ? renderer.sharedMesh : null;
                if (mesh == null)
                {
                    continue;
                }

                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string blendshapeName = mesh.GetBlendShapeName(i);
                    if (!IsFaceBlendshapeCandidate(blendshapeName))
                    {
                        continue;
                    }

                    renderer.SetBlendShapeWeight(i, selected.Contains(blendshapeName) ? 100f : 0f);
                }
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

        private static Bounds CalculateVisibleBounds(GameObject avatarRoot)
        {
            bool hasBounds = false;
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            foreach (var renderer in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                // Frame the avatar's surfaces only: particle, trail and line renderers (often empty, with zero-size
                // bounds at the world origin) would stretch the framing far away from the avatar.
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                    renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer ||
                    renderer.bounds.size.sqrMagnitude <= 0f)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!hasBounds || bounds.size.sqrMagnitude <= 0.0001f)
            {
                throw new InvalidOperationException("The avatar has no visible renderers to frame.");
            }

            return bounds;
        }

        private struct CameraFrame
        {
            public Vector3 target;
            public float verticalSpan;
            public float horizontalSpan;
            // Spans before zoom: placement is measured in these, so a placement keeps pointing at the same body part
            // at every zoom.
            public float baseVerticalSpan;
            public float baseHorizontalSpan;
            public float distance;
            // Part of the distance that keeps the camera in front of the avatar; the spans apply at that front plane.
            public float clearance;
            public float aspect;
        }

        private static CameraFrame FrameFor(Bounds bounds, ShotKind shotKind, int width, int height, float zoom)
        {
            zoom = Mathf.Clamp(zoom > 0f ? zoom : DefaultCameraZoom, MinCameraZoom, MaxCameraZoom);
            float aspect = Mathf.Max(0.1f, width / (float)Mathf.Max(1, height));
            float verticalSpan = shotKind == ShotKind.Thumbnail
                ? Mathf.Max(bounds.size.y * 0.58f, bounds.size.x * 0.82f)
                : bounds.size.y * 0.82f;
            float horizontalSpan = shotKind == ShotKind.Thumbnail
                ? Mathf.Max(bounds.size.x * 0.78f, verticalSpan * aspect * 0.52f)
                : Mathf.Max(bounds.size.x * 1.02f, verticalSpan * aspect * 0.82f);
            float baseVerticalSpan = verticalSpan, baseHorizontalSpan = horizontalSpan;
            verticalSpan /= zoom;
            horizontalSpan /= zoom;

            float verticalFovRadians = ReferenceCameraFieldOfView * Mathf.Deg2Rad;
            float horizontalFovRadians = 2f * Mathf.Atan(Mathf.Tan(verticalFovRadians * 0.5f) * aspect);
            float distanceForHeight = verticalSpan / (2f * Mathf.Tan(verticalFovRadians * 0.5f));
            float distanceForWidth = horizontalSpan / (2f * Mathf.Tan(horizontalFovRadians * 0.5f));
            return new CameraFrame
            {
                target = bounds.center + Vector3.up * (shotKind == ShotKind.Thumbnail ? bounds.size.y * 0.22f : bounds.size.y * 0.16f),
                verticalSpan = verticalSpan,
                horizontalSpan = horizontalSpan,
                baseVerticalSpan = baseVerticalSpan,
                baseHorizontalSpan = baseHorizontalSpan,
                distance = Mathf.Max(distanceForHeight, distanceForWidth) + Mathf.Max(0.2f, bounds.extents.z),
                clearance = Mathf.Max(0.2f, bounds.extents.z),
                aspect = aspect
            };
        }

        /// <summary>
        /// Zoom and placement that show one part of the posed avatar, whatever the pose: the head down to the chest, the
        /// torso and head down to the hips, or the whole body. Regions come from the vertices the head and spine move, so
        /// ears and hair count and a raised tail does not; the frame fits both their height and width, with headroom.
        /// </summary>
        public static bool TrySuggestFraming(FrameInfo frame, ShotKind shotKind, Vector2Int size, FramingPreset preset, out float zoom, out Vector2 placement)
        {
            zoom = DefaultCameraZoom;
            placement = Vector2.zero;
            if (!frame.valid || frame.bounds.size.y <= 0f)
            {
                return false;
            }

            Bounds region;
            if (preset == FramingPreset.Portrait && frame.hasHead)
            {
                region = frame.head;
                region.Encapsulate(new Vector3(region.center.x, frame.chest.y, region.center.z));
            }
            else if (preset == FramingPreset.HalfBody && frame.hasTorso)
            {
                region = frame.torso;
                region.Encapsulate(frame.hips);
            }
            else if (preset == FramingPreset.FullBody)
            {
                region = frame.bounds;
            }
            else
            {
                // Not humanoid: take the top of the outline.
                float cut = preset == FramingPreset.Portrait ? frame.chest.y : frame.hips.y;
                region = frame.bounds;
                region.SetMinMax(new Vector3(region.min.x, cut, region.min.z), region.max);
            }

            float aspect = Mathf.Max(0.1f, size.x / (float)Mathf.Max(1, size.y));
            float top = region.max.y + region.size.y * (preset == FramingPreset.FullBody ? 0.05f : 0.1f);
            float bottom = region.min.y - region.size.y * (preset == FramingPreset.FullBody ? 0.04f : 0.02f);
            float side = region.size.x * 0.06f;
            float wanted = Mathf.Max(top - bottom, (region.size.x + side * 2f) / aspect, frame.bounds.size.y * 0.05f);
            float tangent = Mathf.Tan(ReferenceCameraFieldOfView * Mathf.Deg2Rad * 0.5f);

            // The visible height is measured where the framed part's outline is, at its middle depth: from the camera,
            // that is the avatar's front plus however far that middle sits behind it.
            float front = frame.bounds.center.z + Mathf.Max(0.2f, frame.bounds.extents.z);
            float behindFront = Mathf.Max(0f, front - region.center.z);
            // The visible height shrinks as zoom grows; search the zoom that shows exactly the wanted span.
            float low = MinCameraZoom, high = MaxCameraZoom;
            for (int i = 0; i < 32; i++)
            {
                float middle = Mathf.Sqrt(low * high);
                var candidate = FrameFor(frame.stage, shotKind, size.x, size.y, middle);
                float visible = 2f * (candidate.distance - candidate.clearance + behindFront) * tangent;
                if (visible > wanted) low = middle;
                else high = middle;
            }

            zoom = Mathf.Sqrt(low * high);
            var cameraFrame = FrameFor(frame.stage, shotKind, size.x, size.y, zoom);
            // The camera looks along -Z, so screen right is world -X: placement.x moves the look point towards +X.
            float centreX = region.center.x, centreY = (top + bottom) * 0.5f;
            placement = new Vector2(
                Mathf.Clamp((centreX - cameraFrame.target.x) / (cameraFrame.baseHorizontalSpan * PlacementFrameStrength), -MaxPlacement, MaxPlacement),
                Mathf.Clamp((cameraFrame.target.y - centreY) / (cameraFrame.baseVerticalSpan * PlacementFrameStrength), -MaxPlacement, MaxPlacement));
            return true;
        }

        /// <summary>Placement change that moves the avatar by one whole frame width and height.</summary>
        public static Vector2 PlacementPerFrame(FrameInfo frame, ShotKind shotKind, Vector2Int size, float zoom)
        {
            if (!frame.valid) return new Vector2(1.25f, 1.25f);
            var cameraFrame = FrameFor(frame.stage, shotKind, size.x, size.y, zoom);
            // Measured at the avatar's front, the surface the pointer drags.
            float visibleHeight = 2f * (cameraFrame.distance - cameraFrame.clearance) * Mathf.Tan(ReferenceCameraFieldOfView * Mathf.Deg2Rad * 0.5f);
            return new Vector2(
                visibleHeight * cameraFrame.aspect / (PlacementFrameStrength * cameraFrame.baseHorizontalSpan),
                visibleHeight / (PlacementFrameStrength * cameraFrame.baseVerticalSpan));
        }


        private static void ConfigureCamera(Camera camera, Bounds bounds, ShotKind shotKind, int width, int height, float zoom, Vector2 placement)
        {
            placement = new Vector2(Mathf.Clamp(placement.x, -MaxPlacement, MaxPlacement), Mathf.Clamp(placement.y, -MaxPlacement, MaxPlacement));
            var frame = FrameFor(bounds, shotKind, width, height, zoom);
            float distance = frame.distance, aspect = frame.aspect;

            // The camera looks along -Z, so screen right is world -X. Placement pans the camera rather than tilting it,
            // so an off-centre framing keeps the avatar undistorted.
            Vector3 cameraRight = Vector3.left;
            Vector3 framedTarget = frame.target
                - cameraRight * (frame.baseHorizontalSpan * PlacementFrameStrength * placement.x)
                - Vector3.up * (frame.baseVerticalSpan * PlacementFrameStrength * placement.y);
            camera.transform.position = framedTarget + Vector3.forward * distance;
            camera.transform.rotation = Quaternion.LookRotation(Vector3.back, Vector3.up);
            camera.fieldOfView = ReferenceCameraFieldOfView;
            camera.aspect = aspect;
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = Mathf.Max(50f, distance * 6f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = PreviewClearColor;
            camera.allowHDR = true;
            camera.allowMSAA = true;
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
