using System;
using UnityEngine;

namespace nadena.dev.modular_avatar.core.tests
{
    /// <summary>
    /// Stands in for Modular Avatar's Merge Animator's data in tests (Modular Avatar isn't installed here): its fields have
    /// Merge Animator's names. A ScriptableObject, as Unity puts no MonoBehaviour of an editor assembly on an object.
    /// </summary>
    public sealed class FakeModularAvatarMerge : ScriptableObject
    {
        public enum MergeAnimatorPathMode { Relative, Absolute }

        [Serializable]
        public sealed class Reference
        {
            public string referencePath;
            public GameObject targetObject;
        }

        public RuntimeAnimatorController animator;
        public MergeAnimatorPathMode pathMode;
        public Reference relativePathRoot = new Reference();
    }
}
