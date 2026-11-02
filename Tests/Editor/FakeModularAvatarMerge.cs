using System;
using UnityEngine;

namespace nadena.dev.modular_avatar.core.tests
{
    /// <summary>
    /// Stands in for Modular Avatar's Merge Animator in tests (Modular Avatar isn't installed here): its namespace makes it
    /// one of Modular Avatar's components, its fields have Merge Animator's names.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class FakeModularAvatarMerge : MonoBehaviour
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
