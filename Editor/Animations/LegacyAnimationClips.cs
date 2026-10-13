using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Animations
{
    /// <summary>
    /// The clips of a legacy <see cref="Animation"/> component: its default clip and its list, read and replaced through
    /// its serialized data (Unity's own helpers also list the clips of an Animator on the same object). Their paths start
    /// from the component's object.
    /// </summary>
    public static class LegacyAnimationClips
    {
        public static List<AnimationClip> Of(Animation animation)
        {
            var clips = new List<AnimationClip>();
            if (animation == null) return clips;
            using (var serialized = new SerializedObject(animation))
                foreach (var property in Properties(serialized))
                    if (property.objectReferenceValue is AnimationClip clip && !clips.Contains(clip)) clips.Add(clip);
            return clips;
        }

        /// <summary>Points the component at <paramref name="map"/>'s clip in place of each of its own.</summary>
        public static void Replace(Animation animation, Func<AnimationClip, AnimationClip> map)
        {
            if (animation == null || map == null) return;
            using (var serialized = new SerializedObject(animation))
            {
                foreach (var property in Properties(serialized))
                    if (property.objectReferenceValue is AnimationClip clip) property.objectReferenceValue = map(clip);
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static IEnumerable<SerializedProperty> Properties(SerializedObject serialized)
        {
            var main = serialized.FindProperty("m_Animation");
            if (main != null) yield return main;
            var list = serialized.FindProperty("m_Animations");
            if (list == null || !list.isArray) yield break;
            for (int i = 0; i < list.arraySize; i++) yield return list.GetArrayElementAtIndex(i);
        }
    }
}
