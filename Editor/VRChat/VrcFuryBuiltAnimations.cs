using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Animations;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// VRCFury saves only what it changed or made under com.vrcfury.temp: the controllers it builds keep playing the
    /// avatar's own clips and blend trees where it left them as they were. The build steps after it change those
    /// controllers in place (blendshape links, face tracking, ReFit's shrinks, MCB's modes and offsets, optimizations),
    /// which would change the project's assets: right after VRCFury, the clips and blend trees of the project its
    /// controllers play are copied into them, so everything they play is the build's own.
    /// </summary>
    public static class VrcFuryBuiltAnimations
    {
        /// <summary>
        /// Copies into each controller VRCFury built for <paramref name="avatarRoot"/> the project's clips and blend trees
        /// its states play (shared ones once per controller); returns how many were copied.
        /// </summary>
        public static int Own(GameObject avatarRoot)
        {
            if (avatarRoot == null) return 0;
            int copied = 0;
            foreach (var controller in BlendShapeLinkEngine.CollectBuiltControllers(avatarRoot).Where(BlendShapeLinkEngine.IsVrcFuryBuiltController))
            {
                var copies = new Dictionary<Motion, Motion>();
                var machines = new HashSet<AnimatorStateMachine>();
                // The layers as they are: inside the batch they hold a stand-in, and each state changed costs nothing.
                var layers = controller.layers;
                using (ControllerEdits.Batch(new[] { controller }))
                    foreach (var layer in layers) Visit(layer.stateMachine);
                int made = copies.Count(c => c.Key != c.Value);
                if (made == 0) continue;
                copied += made;
                EditorUtility.SetDirty(controller);

                void Visit(AnimatorStateMachine machine)
                {
                    if (machine == null || !machines.Add(machine)) return;
                    foreach (var child in machine.states)
                    {
                        var state = child.state;
                        if (state == null) continue;
                        var owned = Owned(state.motion);
                        if (owned != state.motion) state.motion = owned;
                    }
                    foreach (var child in machine.stateMachines) Visit(child.stateMachine);
                }

                Motion Owned(Motion motion)
                {
                    if (motion == null) return null;
                    if (copies.TryGetValue(motion, out var known)) return known;
                    Motion result = motion;
                    if (motion is BlendTree tree)
                    {
                        var own = AttachmentAnimationBuild.IsBuildData(tree) ? tree : Keep(AnimatorControllerCopy.Tree(tree), controller, HideFlags.HideInHierarchy);
                        copies[motion] = own;
                        var children = tree.children;
                        bool changed = own != tree;
                        for (int i = 0; i < children.Length; i++)
                        {
                            var child = Owned(children[i].motion);
                            if (child == children[i].motion) continue;
                            children[i].motion = child;
                            changed = true;
                        }
                        if (changed) own.children = children;
                        return own;
                    }
                    if (motion is AnimationClip clip && !AttachmentAnimationBuild.IsBuildData(clip))
                    {
                        var copy = Object.Instantiate(clip);
                        copy.name = clip.name;
                        result = Keep(copy, controller, clip.hideFlags);
                    }
                    copies[motion] = result;
                    return result;
                }
            }
            return copied;
        }

        private static T Keep<T>(T copy, AnimatorController controller, HideFlags flags) where T : Object
        {
            copy.hideFlags = flags;
            if (AssetDatabase.Contains(controller)) AssetDatabase.AddObjectToAsset(copy, controller);
            return copy;
        }
    }

    internal sealed class VrcFuryBuiltAnimationsHook : IVRCSDKPreprocessAvatarCallback
    {
        // Right after VRCFury's build (-10000), before every step that changes its controllers.
        public int callbackOrder => -9999;

        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            VrcFuryBuiltAnimations.Own(avatarRoot);
            return true;
        }
    }
}
