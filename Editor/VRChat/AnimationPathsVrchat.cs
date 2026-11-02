using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Animations;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// VRChat's part of <see cref="AnimationPaths"/>: the descriptor's playable layers and their masks (from the avatar's
    /// root), what VRCFury components play (from the component's object, or its Full Controller's root object, looked up
    /// further up the way VRCFury does), what Modular Avatar's merge components play (from their relative path root, or
    /// the avatar's root in absolute mode), state audio's source path, and the avatars the watcher follows.
    /// </summary>
    [InitializeOnLoad]
    internal static class AnimationPathsVrchat
    {
        // Modular Avatar's AvatarObjectReference for the avatar's root.
        private const string MaAvatarRoot = "$$$AVATAR_ROOT$$$";

        static AnimationPathsVrchat()
        {
            AnimationPaths.AddSources(Sources);
            AnimationPaths.AddPathProperty(typeof(VRCAnimatorPlayAudio), nameof(VRCAnimatorPlayAudio.SourcePath), typeof(AudioSource));
            var generic = AnimationPaths.AvatarRootOf;
            AnimationPaths.AvatarRootOf = t => Descriptor(t) ?? generic?.Invoke(t);
            AnimationPaths.Avatars = Avatars;
        }

        private static Transform Descriptor(Transform t)
        {
            for (; t != null; t = t.parent) if (t.GetComponent<VRCAvatarDescriptor>() != null) return t;
            return null;
        }

        // Avatars of the open, editable scenes: not preview scenes (hidden builds, Prefab Mode), not build copies.
        private static IEnumerable<GameObject> Avatars()
        {
            foreach (var descriptor in Object.FindObjectsByType<VRCAvatarDescriptor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (descriptor == null) continue;
                var go = descriptor.gameObject;
                var scene = go.scene;
                if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene)) continue;
                if ((go.hideFlags & (HideFlags.HideInHierarchy | HideFlags.DontSave | HideFlags.NotEditable)) != 0) continue;
                if (go.name.EndsWith("(Clone)", StringComparison.Ordinal) || VrcFury.IsTestCopy(go)) continue;
                yield return go;
            }
        }

        private static IEnumerable<AnimationSource> Sources(GameObject root)
        {
            foreach (var descriptor in root.GetComponentsInChildren<VRCAvatarDescriptor>(true))
                foreach (var layer in (descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                    .Concat(descriptor.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>()))
                {
                    if (!layer.isDefault && layer.animatorController != null)
                        yield return new AnimationSource { Asset = layer.animatorController, Root = descriptor.transform, Via = descriptor };
                    if (layer.mask != null) yield return new AnimationSource { Asset = layer.mask, Root = descriptor.transform, Via = descriptor };
                }
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform) continue;
                bool fury = VrcFury.IsComponent(component), ma = !fury && ModularAvatar.IsComponent(component);
                if (!fury && !ma) continue;
                foreach (var source in ToolSources(component, fury)) yield return source;
            }
        }

        // What a VRCFury or Modular Avatar component plays, from where its tool merges it.
        private static List<AnimationSource> ToolSources(Component component, bool fury)
        {
            var found = new List<AnimationSource>();
            using (var serialized = new SerializedObject(component))
            {
                Transform from = component.transform;
                string unknown = null;
                bool any = false;
                var assets = new List<Object>();
                var property = serialized.GetIterator();
                while (property.Next(true))
                {
                    if (fury && property.name == "rootObjOverride" && property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue is GameObject over)
                        from = over.transform;
                    if (fury && property.name == "rewriteBindings" && property.isArray && property.arraySize > 0)
                        unknown = "VRCFury rewrites its paths (Full Controller binding rewrites)";
                    if (property.propertyType != SerializedPropertyType.ObjectReference || property.name == "m_Script") continue;
                    var value = property.objectReferenceValue;
                    if (value is AnimationClip || value is RuntimeAnimatorController || value is BlendTree || value is AvatarMask)
                    {
                        assets.Add(value);
                        any = true;
                    }
                }
                if (!any) return found;
                if (!fury)
                {
                    // Modular Avatar: relative to its path root (its own object by default), or to the avatar in absolute mode.
                    var mode = serialized.FindProperty("pathMode");
                    if (mode == null) return found;
                    var avatar = Descriptor(component.transform) ?? component.transform.root;
                    if (mode.enumValueIndex == 1) from = avatar;
                    else
                    {
                        var target = serialized.FindProperty("relativePathRoot.targetObject")?.objectReferenceValue as GameObject;
                        string path = serialized.FindProperty("relativePathRoot.referencePath")?.stringValue;
                        if (target != null) from = target.transform;
                        else if (path == MaAvatarRoot) from = avatar;
                        else if (!string.IsNullOrEmpty(path))
                        {
                            var at = avatar.Find(path);
                            if (at != null) from = at;
                            else unknown = "its Modular Avatar path root “" + path + "” is missing";
                        }
                    }
                }
                foreach (var asset in assets.Distinct())
                    found.Add(new AnimationSource { Asset = asset, Root = from, WalksUp = fury, Via = component, Unknown = unknown });
            }
            return found;
        }
    }
}
