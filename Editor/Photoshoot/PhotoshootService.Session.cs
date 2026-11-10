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
    // The live session: one photoshoot stage in its own preview scene (moved here from PhotoshootService.cs as is).
    public static partial class PhotoshootService
    {
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

            // Helpers other editor tools hang on the avatar without ever saving them (the avatar gizmos' armature): not part of
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
    }
}
