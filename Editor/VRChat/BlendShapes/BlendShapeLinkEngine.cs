using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Orbiters.Toolkit.Editor.VRChat.BlendShapes
{
    /// <summary>
    /// Applies blendshape links to the controllers VRCFury built for the avatar being processed (assets under
    /// "com.vrcfury.temp"). Authoring controllers are never touched. Run it from a preprocess callback after VRCFury (-10000).
    /// </summary>
    public static partial class BlendShapeLinkEngine
    {
        public const string WrapperPrefix = "UP_BSLINK_FACTOR_";
        public const string VariantPrefix = "UP_BSLINK_VARIANT_";
        public const string FxLayerPrefix = "[UP_FX] ";

        /// <summary>Verbose diagnostics; unset means silent.</summary>
        public static Action<string> Log;

        private static readonly Dictionary<int, List<AppliedBlendShapeLink>> applied = new Dictionary<int, List<AppliedBlendShapeLink>>();
        // Layers already copied to FX during this build, across several Apply calls.
        private static readonly HashSet<string> layersCopiedToFx = new HashSet<string>();
        private static int buildFrame = -1;

        /// <summary>Links applied this editor session, keyed by controller instance ID.</summary>
        public static IReadOnlyDictionary<int, List<AppliedBlendShapeLink>> Applied => applied;

        public static void ClearApplied() => applied.Clear();

        /// <summary>Starts a new build: forgets the layers copied to FX by the previous one.</summary>
        public static void BeginBuild()
        {
            layersCopiedToFx.Clear();
            buildFrame = Time.frameCount;
        }

        public static BlendShapeLinkResult Apply(GameObject avatarRoot, IReadOnlyList<BlendShapeLink> links, string label)
        {
            if (avatarRoot == null) return BlendShapeLinkResult.Fail("Avatar root is null.");
            if (links == null || links.Count == 0) return BlendShapeLinkResult.Fail("No links to apply.");
            if (Time.frameCount != buildFrame) BeginBuild();

            var controllers = CollectBuiltControllers(avatarRoot);
            if (controllers.Count == 0) return BlendShapeLinkResult.Fail("No VRCFury temporary AnimatorController found on avatar descriptor.");

            var descriptor = avatarRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
            // VRChat only plays blendshape animations from FX: rewritten layers of other playable layers are copied there.
            var fxController = FindControllerForLayerType(descriptor, VRCAvatarDescriptor.AnimLayerType.FX);
            if (fxController != null && !IsVrcFuryBuiltController(fxController)) fxController = null;
            var layersToCopyToFx = new Dictionary<string, (AnimatorController controller, int index, AnimatorControllerLayer layer)>();
            var changedControllers = new HashSet<AnimatorController>();
            var rewrittenControllers = new HashSet<AnimatorController>();
            int linksProcessed = 0, clipsWrapped = 0, statesRewritten = 0;

            foreach (var link in links)
            {
                bool linkChanged = false;
                Trace($"Processing link: toFix='{link.TriggerName}' fixedBy='{link.EffectName}' across {controllers.Count} controllers");
                foreach (var controller in controllers)
                {
                    Trace($"Checking controller '{controller.name}' for link toFix='{link.TriggerName}'");
                    if (!link.DirectCopy && !EnsureFloatParameter(controller, link.FactorParameter, link.SetFactorDefault, link.FactorDefault, out var paramError))
                    {
                        Debug.LogWarning("[Orbiters] " + paramError);
                        continue;
                    }

                    bool controllerChanged = false;
                    var clipCache = new Dictionary<AnimationClip, Motion>();
                    var visitedMachines = new HashSet<AnimatorStateMachine>();
                    var visitedTrees = new HashSet<BlendTree>();
                    var layers = controller.layers;
                    bool layersChanged = false;
                    for (int i = 0; i < layers.Length; i++)
                    {
                        var layer = layers[i];
                        if (layer?.stateMachine == null) continue;
                        if (!RewriteStateMachine(controller, layer.stateMachine, link, clipCache, visitedMachines, visitedTrees, ref clipsWrapped, ref statesRewritten)) continue;
                        controllerChanged = true;
                        linkChanged = true;

                        if (fxController != null && controller != fxController)
                        {
                            string layerKey = $"{controller.name}:{i}:{layer.name}";
                            if (!layersToCopyToFx.ContainsKey(layerKey) && !layersCopiedToFx.Contains(layerKey))
                            {
                                Trace($"Layer '{layer.name}' in '{controller.name}' contains blendshape animations - will copy to FX controller.");
                                layersToCopyToFx[layerKey] = (controller, i, layer);
                            }
                        }
                        else if (fxController == null)
                        {
                            Trace($"No FX controller found! Cannot copy layer '{layer.name}' for blendshape animations.");
                        }

                        // FX layers with restrictive masks would drop the blendshape curves.
                        if (layer.avatarMask != null)
                        {
                            Trace($"Clearing mask '{layer.avatarMask.name}' from layer '{layer.name}'.");
                            layer.avatarMask = null;
                            layers[i] = layer;
                            layersChanged = true;
                        }
                    }

                    if (!controllerChanged) continue;
                    if (layersChanged) controller.layers = layers;
                    changedControllers.Add(controller);
                    rewrittenControllers.Add(controller);
                    EditorUtility.SetDirty(controller);

                    int id = controller.GetInstanceID();
                    if (!applied.TryGetValue(id, out var records)) applied[id] = records = new List<AppliedBlendShapeLink>();
                    records.Add(new AppliedBlendShapeLink
                    {
                        ControllerName = controller.name, ControllerPath = AssetDatabase.GetAssetPath(controller),
                        FactorParameter = link.FactorParameter, TargetRendererPath = link.TargetRendererPath,
                        Trigger = link.TriggerName, Effect = link.EffectName, Label = label
                    });
                }
                if (linkChanged) linksProcessed++;
            }

            if (layersToCopyToFx.Count > 0 && fxController != null)
            {
                CopyLayersToFx(layersToCopyToFx.Values.ToList(), fxController);
                changedControllers.Add(fxController);
            }

            if (changedControllers.Count == 0)
                return BlendShapeLinkResult.Fail("No matching blendshape curves or animation motions were found in VRCFury temporary controllers.");

            if (descriptor != null)
            {
                bool descriptorChanged = false;
                void ClearMasks(VRCAvatarDescriptor.CustomAnimLayer[] descriptorLayers)
                {
                    if (descriptorLayers == null) return;
                    for (int i = 0; i < descriptorLayers.Length; i++)
                    {
                        if (!(descriptorLayers[i].animatorController is AnimatorController ac) || !rewrittenControllers.Contains(ac) || descriptorLayers[i].mask == null) continue;
                        Trace($"Clearing mask from VRCAvatarDescriptor layer type '{descriptorLayers[i].type}' because its controller '{ac.name}' was modified by a corrective link.");
                        descriptorLayers[i].mask = null;
                        descriptorChanged = true;
                    }
                }
                if (descriptor.baseAnimationLayers != null)
                {
                    var arr = descriptor.baseAnimationLayers;
                    ClearMasks(arr);
                    descriptor.baseAnimationLayers = arr;
                }
                if (descriptor.specialAnimationLayers != null)
                {
                    var arr = descriptor.specialAnimationLayers;
                    ClearMasks(arr);
                    descriptor.specialAnimationLayers = arr;
                }
                if (descriptorChanged) EditorUtility.SetDirty(descriptor);
            }

            AssetDatabase.SaveAssets();
            return new BlendShapeLinkResult
            {
                Success = true, Links = linksProcessed, Controllers = changedControllers.Count,
                ClipsWrapped = clipsWrapped, StatesRewritten = statesRewritten,
                Message = $"Applied {label} links: {linksProcessed} link(s), {changedControllers.Count} controller(s), {clipsWrapped} wrapped clip(s), {statesRewritten} rewritten state/tree motion reference(s)."
            };
        }

        /// <summary>The VRCFury-built controllers on the descriptor's playable layers and the avatar's Animator.</summary>
        public static List<AnimatorController> CollectBuiltControllers(GameObject avatarRoot)
        {
            var found = new HashSet<AnimatorController>();
            if (avatarRoot == null) return found.ToList();
            var descriptor = avatarRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
            if (descriptor != null)
            {
                CollectFromLayers(descriptor.baseAnimationLayers, found);
                CollectFromLayers(descriptor.specialAnimationLayers, found);
            }
            var animator = avatarRoot.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.runtimeAnimatorController is AnimatorController fromAnimator && IsVrcFuryBuiltController(fromAnimator))
                found.Add(fromAnimator);
            return found.ToList();
        }

        public static bool IsVrcFuryBuiltController(AnimatorController controller)
        {
            string path = AssetDatabase.GetAssetPath(controller);
            return !string.IsNullOrEmpty(path) && path.Replace("\\", "/").IndexOf("com.vrcfury.temp", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void Trace(string message) => Log?.Invoke(message);

        private static void CollectFromLayers(VRCAvatarDescriptor.CustomAnimLayer[] layers, ISet<AnimatorController> found)
        {
            if (layers == null) return;
            foreach (var layer in layers)
                if (!layer.isDefault && layer.animatorController is AnimatorController controller && IsVrcFuryBuiltController(controller))
                    found.Add(controller);
        }

        private static AnimatorController FindControllerForLayerType(VRCAvatarDescriptor descriptor, VRCAvatarDescriptor.AnimLayerType type)
        {
            if (descriptor == null) return null;
            foreach (var layer in (descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                         .Concat(descriptor.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>()))
                if (layer.type == type && !layer.isDefault && layer.animatorController is AnimatorController controller)
                    return controller;
            return null;
        }

        // Copies (not moves) each layer: the original keeps driving bones/muscles in its playable layer, the FX copy shares
        // the same state machine and VRChat's FX layer only applies its non-transform curves.
        private static void CopyLayersToFx(List<(AnimatorController controller, int index, AnimatorControllerLayer layer)> layers, AnimatorController fx)
        {
            var fxLayerNames = new HashSet<string>(fx.layers.Select(l => l.name));
            foreach (var (source, index, layer) in layers)
            {
                if (source == fx) continue;
                layersCopiedToFx.Add($"{source.name}:{index}:{layer.name}");
                string name = FxLayerPrefix + layer.name;
                if (fxLayerNames.Contains(name))
                {
                    Trace($"Layer '{name}' already exists in FX controller. Skipping copy (state machine is shared and already updated).");
                    continue;
                }
                Trace($"Copying layer '{layer.name}' from '{source.name}' to FX controller for blendshape animations.");
                foreach (var param in source.parameters)
                {
                    if (fx.parameters.Any(p => p.name == param.name)) continue;
                    fx.AddParameter(param.name, param.type);
                    var added = fx.parameters.FirstOrDefault(p => p.name == param.name);
                    if (added == null) continue;
                    added.defaultBool = param.defaultBool;
                    added.defaultFloat = param.defaultFloat;
                    added.defaultInt = param.defaultInt;
                }
                var fxLayers = fx.layers.ToList();
                fxLayers.Add(new AnimatorControllerLayer
                {
                    name = name, stateMachine = layer.stateMachine, avatarMask = null,
                    blendingMode = AnimatorLayerBlendingMode.Override, defaultWeight = layer.defaultWeight,
                    syncedLayerIndex = -1, syncedLayerAffectsTiming = false
                });
                fx.layers = fxLayers.ToArray();
            }
            EditorUtility.SetDirty(fx);
        }

        private static bool EnsureFloatParameter(AnimatorController controller, string name, bool setDefault, float defaultValue, out string error)
        {
            error = null;
            var parameters = controller.parameters;
            foreach (var p in parameters)
            {
                if (p.name != name) continue;
                if (p.type != AnimatorControllerParameterType.Float)
                {
                    error = $"Controller '{controller.name}' already has parameter '{name}' but it is not Float.";
                    return false;
                }
                if (setDefault && Mathf.Abs(p.defaultFloat - defaultValue) > 0.0001f)
                {
                    p.defaultFloat = defaultValue;
                    controller.parameters = parameters; // parameters returns a copy
                    EditorUtility.SetDirty(controller);
                }
                return true;
            }
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = name, type = AnimatorControllerParameterType.Float, defaultFloat = setDefault ? defaultValue : 0f
            });
            EditorUtility.SetDirty(controller);
            return true;
        }

        private static bool RewriteStateMachine(AnimatorController controller, AnimatorStateMachine machine, BlendShapeLink link,
            IDictionary<AnimationClip, Motion> clipCache, ISet<AnimatorStateMachine> visitedMachines, ISet<BlendTree> visitedTrees,
            ref int clipsWrapped, ref int statesRewritten)
        {
            if (!visitedMachines.Add(machine)) return false;
            bool changed = false, hasExistingWrapper = false;
            foreach (var child in machine.states)
            {
                var state = child.state;
                if (state == null || state.motion == null) continue;
                // Wrapped by a previous build: the layer mask still needs clearing.
                if (ContainsWrapper(state.motion, link.FactorParameter, visitedTrees)) hasExistingWrapper = true;
                bool motionChanged = false;
                var rewritten = RewriteMotion(controller, state.motion, link, clipCache, visitedTrees, ref clipsWrapped, ref motionChanged);
                if (!motionChanged && (rewritten == null || rewritten == state.motion)) continue;
                state.motion = rewritten;
                EditorUtility.SetDirty(state);
                statesRewritten++;
                changed = true;
            }
            foreach (var child in machine.stateMachines)
                if (child.stateMachine != null && RewriteStateMachine(controller, child.stateMachine, link, clipCache, visitedMachines, visitedTrees, ref clipsWrapped, ref statesRewritten))
                    changed = true;
            return changed || hasExistingWrapper;
        }

        private static bool ContainsWrapper(Motion motion, string factor, ISet<BlendTree> visited)
        {
            if (!(motion is BlendTree tree) || visited.Contains(tree)) return false;
            if (IsWrapperTree(tree, factor)) return true;
            foreach (var child in tree.children)
                if (ContainsWrapper(child.motion, factor, visited)) return true;
            return false;
        }

        private static Motion RewriteMotion(AnimatorController controller, Motion motion, BlendShapeLink link,
            IDictionary<AnimationClip, Motion> clipCache, ISet<BlendTree> visitedTrees, ref int clipsWrapped, ref bool changed)
        {
            if (motion == null) return null;
            if (motion is BlendTree tree)
            {
                if (!visitedTrees.Add(tree)) return tree;
                if (IsWrapperTree(tree, link.FactorParameter))
                {
                    // Already wrapped for this factor: stack further links on the variant child instead of wrapping again.
                    var wrapperChildren = tree.children;
                    var variant = wrapperChildren[1].motion;
                    bool variantChanged = false;
                    var rewrittenVariant = RewriteMotion(controller, variant, link, clipCache, visitedTrees, ref clipsWrapped, ref variantChanged);
                    if (variantChanged || (rewrittenVariant != null && rewrittenVariant != variant))
                    {
                        wrapperChildren[1].motion = rewrittenVariant;
                        tree.children = wrapperChildren;
                        EditorUtility.SetDirty(tree);
                        changed = true;
                    }
                    return tree;
                }
                bool childrenChanged = false;
                var children = tree.children;
                for (int i = 0; i < children.Length; i++)
                {
                    var childMotion = children[i].motion;
                    bool childChanged = false;
                    var rewritten = RewriteMotion(controller, childMotion, link, clipCache, visitedTrees, ref clipsWrapped, ref childChanged);
                    if (!childChanged && (rewritten == null || rewritten == childMotion)) continue;
                    children[i].motion = rewritten;
                    childrenChanged = true;
                }
                if (childrenChanged)
                {
                    tree.children = children;
                    EditorUtility.SetDirty(tree);
                    changed = true;
                }
                return tree;
            }

            if (!(motion is AnimationClip clip)) return motion;
            if (clipCache.TryGetValue(clip, out var cached)) return cached;
            var variantClip = TryCreateVariantClip(controller, clip, link);
            if (variantClip == null)
            {
                clipCache[clip] = clip;
                return clip;
            }
            Motion result = link.DirectCopy ? (Motion)variantClip : CreateWrapperTree(controller, clip, variantClip, link.FactorParameter);
            clipCache[clip] = result;
            clipsWrapped++;
            changed = true;
            return result;
        }

        private static bool IsWrapperTree(BlendTree tree, string factor) =>
            tree != null && tree.name.StartsWith(WrapperPrefix, StringComparison.Ordinal) && tree.blendType == BlendTreeType.Simple1D &&
            string.Equals(tree.blendParameter, factor, StringComparison.Ordinal) && tree.children != null && tree.children.Length == 2;
    }
}
