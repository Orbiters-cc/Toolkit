using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Optimization
{
    /// <summary>
    /// Clean-up of a build copy that never changes what the avatar shows or does: what can never turn on goes (VRChat ranks
    /// it all the same, disabled or not), colliders no PhysBone uses go, and PhysBones nothing animates stop following
    /// animations. Every step returns how many things it changed.
    /// </summary>
    public static class ComponentCleanup
    {
        /// <summary>
        /// Objects that start hidden and that no animation shows, with everything below them, unless a component elsewhere
        /// points into them (the bones of a visible mesh, the descriptor's eyes) or they hold a humanoid bone. Objects tagged
        /// EditorOnly are left to the SDK, which removes them anyway.
        /// </summary>
        public static int RemoveHiddenObjects(AvatarUsage usage)
        {
            var hidden = new List<Transform>();
            void Walk(Transform parent)
            {
                foreach (Transform child in parent)
                {
                    if (child.CompareTag("EditorOnly")) continue;
                    if (!child.gameObject.activeSelf && !usage.CanTurnOn(child.gameObject)) hidden.Add(child);
                    else Walk(child);
                }
            }
            Walk(usage.Root.transform);
            // Decided before anything goes. A collider list pointing at a hidden collider is tidied, not a use.
            var gone = hidden.Where(t => !usage.HoldsHumanoid(t) && !usage.UsedFromOutside(t, ColliderList) && !usage.Pending(t)).ToList();
            foreach (var t in gone) Object.DestroyImmediate(t.gameObject);
            if (gone.Count > 0) TidyColliderLists(usage.Root);
            return gone.Count;
        }

        /// <summary>
        /// Components that start disabled and that no animation turns on: PhysBones, PhysBone colliders, contacts and
        /// constraints (VRChat's and Unity's), lights and renderers. One another component points at (other than a PhysBone's
        /// collider list) or needs stays.
        /// </summary>
        public static int RemoveDisabledComponents(AvatarUsage usage)
        {
            int count = 0;
            foreach (var component in usage.Root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || !Removable(component) || Enabled(component) || usage.CanTurnOn(component)) continue;
                if (component.CompareTag("EditorOnly") || usage.Pending(component.transform)) continue;
                if (usage.ReferencesTo(component).Any(r => r.From != null && !ColliderList(r.From, r.Property))) continue;
                if (Required(component)) continue;
                if (component is MeshRenderer && component.TryGetComponent<MeshFilter>(out var filter) && usage.ReferencesTo(filter).Count == 0)
                    Object.DestroyImmediate(filter);
                Object.DestroyImmediate(component);
                count++;
            }
            if (count > 0) TidyColliderLists(usage.Root);
            return count;
        }

        /// <summary>PhysBone colliders no PhysBone lists: they collide with nothing. One something else points at stays.</summary>
        public static int RemoveUnusedColliders(AvatarUsage usage)
        {
            var listed = new HashSet<VRCPhysBoneColliderBase>(usage.Root.GetComponentsInChildren<VRCPhysBoneBase>(true)
                .SelectMany(p => p.colliders ?? new List<VRCPhysBoneColliderBase>()).Where(c => c != null));
            int count = 0;
            foreach (var collider in usage.Root.GetComponentsInChildren<VRCPhysBoneColliderBase>(true))
            {
                if (listed.Contains(collider) || collider.CompareTag("EditorOnly") || usage.Pending(collider.transform)) continue;
                if (usage.ReferencesTo(collider).Any(r => r.From != null && !ColliderList(r.From, r.Property))) continue;
                Object.DestroyImmediate(collider);
                count++;
            }
            return count;
        }

        /// <summary>
        /// PhysBones set to follow animations whose bones no animation moves: VRChat stops reading their animated pose
        /// every frame. Returns how many changed.
        /// </summary>
        public static int SettleStillPhysBones(AvatarUsage usage)
        {
            int count = 0;
            foreach (var bone in usage.Root.GetComponentsInChildren<VRCPhysBoneBase>(true))
            {
                if (!bone.isAnimated) continue;
                var chain = bone.rootTransform != null ? bone.rootTransform : bone.transform;
                if (usage.Moves(chain) || usage.Pending(chain)) continue;
                bone.isAnimated = false;
                count++;
            }
            return count;
        }

        /// <summary>
        /// PhysBones that move nothing: no vertex is weighted to a bone of their chain, nothing else sits on it (a renderer,
        /// a contact, a constraint's target) and no animator reads their parameters. Leftovers of a removed part, mostly.
        /// </summary>
        public static int RemoveIdlePhysBones(AvatarUsage usage)
        {
            var weighted = WeightedBones(usage.Root);
            int count = 0;
            foreach (var bone in usage.Root.GetComponentsInChildren<VRCPhysBoneBase>(true))
            {
                var chain = Chain(bone);
                if (chain.Count == 0 || usage.Pending(chain[0]) || bone.CompareTag("EditorOnly")) continue;
                if (!string.IsNullOrEmpty(bone.parameter) && Suffixes.Any(s => usage.Reads(bone.parameter + s))) continue;
                if (chain.Any(t => weighted.Contains(t) || usage.Humanoid(t) || t.GetComponents<Component>().Any(c => !(c is Transform || c is VRCPhysBoneBase || c is VRCPhysBoneColliderBase)))) continue;
                // Something outside following a bone of the chain (a renderer's root bone, a constraint's source) is moved by it.
                if (usage.UsedFromOutside(chain[0], (from, property) => from is VRCPhysBoneBase || from is VRCPhysBoneColliderBase ||
                                                                            from is SkinnedMeshRenderer && property.StartsWith("m_Bones.", StringComparison.Ordinal))) continue;
                Object.DestroyImmediate(bone);
                count++;
            }
            return count;
        }

        /// <summary>
        /// The bones at the end of a PhysBone's chain that only mark where it ends (nothing weighted to them, on them,
        /// animating or pointing at them) become its endpoint position, and go: VRChat ranks every transform a PhysBone
        /// moves. Only for a PhysBone without an endpoint whose ends all sit at the same offset from their bone, and ends no
        /// skinned mesh lists any more (run it after <see cref="MeshOptimization.PruneBones"/>). Returns how many bones went.
        /// </summary>
        public static int TrimEndBones(AvatarUsage usage)
        {
            var weighted = WeightedBones(usage.Root);
            var listed = new HashSet<Transform>(usage.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(r => r.bones).Where(b => b != null));
            int count = 0;
            foreach (var bone in usage.Root.GetComponentsInChildren<VRCPhysBoneBase>(true))
            {
                if (bone.endpointPosition != Vector3.zero || bone.CompareTag("EditorOnly")) continue;
                var chain = Chain(bone);
                if (chain.Count < 2 || usage.Pending(chain[0])) continue;
                var ends = chain.Where(t => t.childCount == 0).ToList();
                bool trimmable = ends.Count > 0 && ends.All(t =>
                    t != chain[0] && t.parent.childCount == 1 && !weighted.Contains(t) && !listed.Contains(t) && !usage.Humanoid(t) && !usage.Animated(t) &&
                    t.GetComponents<Component>().All(c => c is Transform) &&
                    usage.ReferencesTo(t).All(r => r.From == null || r.From == bone || r.From is SkinnedMeshRenderer && r.Property.StartsWith("m_Bones.", StringComparison.Ordinal)) &&
                    usage.ReferencesTo(t.gameObject).All(r => r.From == null));
                if (!trimmable) continue;
                var offset = ends[0].localPosition;
                if (offset.sqrMagnitude < 1e-12f || ends.Any(t => (t.localPosition - offset).magnitude > offset.magnitude * .01f)) continue;
                bone.endpointPosition = offset;
                foreach (var end in ends) Object.DestroyImmediate(end.gameObject);
                count += ends.Count;
            }
            return count;
        }

        // The parameters a PhysBone sets, after its name.
        private static readonly string[] Suffixes = { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" };

        // Its root and every transform below, without the ignored ones and what is below them.
        private static List<Transform> Chain(VRCPhysBoneBase bone)
        {
            var root = bone.rootTransform != null ? bone.rootTransform : bone.transform;
            var ignored = (bone.ignoreTransforms ?? new List<Transform>()).Where(t => t != null).ToList();
            return root.GetComponentsInChildren<Transform>(true).Where(t => !ignored.Any(t.IsChildOf)).ToList();
        }

        // Bones some vertex of a skinned mesh is weighted to.
        private static HashSet<Transform> WeightedBones(GameObject root)
        {
            var result = new HashSet<Transform>();
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer.sharedMesh;
                var bones = renderer.bones;
                if (mesh == null) continue;
                foreach (var weight in mesh.GetAllBoneWeights())
                    if (weight.weight > 0f && weight.boneIndex < bones.Length && bones[weight.boneIndex] != null) result.Add(bones[weight.boneIndex]);
            }
            return result;
        }

        /// <summary>Takes the colliders that are gone out of every PhysBone's list.</summary>
        public static void TidyColliderLists(GameObject root)
        {
            foreach (var bone in root.GetComponentsInChildren<VRCPhysBoneBase>(true))
                if (bone.colliders != null && bone.colliders.Any(c => c == null)) bone.colliders = bone.colliders.Where(c => c != null).ToList();
        }

        // A PhysBone's collider list only says what it collides with: a collider gone from it collides with nothing.
        private static bool ColliderList(Component from, string property) =>
            from is VRCPhysBoneBase && property.StartsWith("colliders.", StringComparison.Ordinal);

        private static bool Removable(Component component) =>
            component is DynamicsComponent || component is IConstraint || component is Light ||
            component is Renderer && !(component is ParticleSystemRenderer);

        private static bool Enabled(Component component) =>
            component is Behaviour behaviour ? behaviour.enabled : !(component is Renderer renderer) || renderer.enabled;

        // Another component on the object that cannot be without this one (RequireComponent).
        private static bool Required(Component component)
        {
            var type = component.GetType();
            foreach (var other in component.GetComponents<Component>())
            {
                if (other == null || other == component) continue;
                foreach (RequireComponent require in Attribute.GetCustomAttributes(other.GetType(), typeof(RequireComponent), true))
                    if (new[] { require.m_Type0, require.m_Type1, require.m_Type2 }.Any(t => t != null && t.IsAssignableFrom(type))) return true;
            }
            return false;
        }
    }
}
