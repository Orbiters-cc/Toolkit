using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;
using VRC.SDKBase.Validation.Performance;

namespace Orbiters.Toolkit.Editor.VRChat.Budget
{
    /// <summary>
    /// What costs most in each category VRChat counts, read from an avatar the way its stats count it: polygons, material
    /// slots and bones per renderer, each skinned mesh and mesh, transforms and collision checks per PhysBone, each
    /// collider, contact, constraint, light, particle system, sound and cloth, and texture memory per texture (as the
    /// caller sizes it). Read once per measured avatar, platform aside (only textures differ), then
    /// <see cref="Fill"/>ed into each platform's <see cref="RankStats"/>. Each entry names the Orbiters item it belongs to,
    /// so a plan can say what removing or merging that item saves.
    /// </summary>
    public sealed class RankContributors
    {
        /// <summary>The entries kept per category, biggest first.</summary>
        public const int PerCategory = 12;

        private readonly List<(RankContributor Entry, float Weight)> entries = new List<(RankContributor, float)>();
        // The textures the renderers' materials show: where they are first shown, and the items of every renderer showing them.
        private readonly Dictionary<Texture, (string Path, HashSet<string> Items, bool Shared)> textures = new Dictionary<Texture, (string, HashSet<string>, bool)>();
        private readonly Dictionary<string, RankItem> items = new Dictionary<string, RankItem>(StringComparer.Ordinal);

        /// <summary>
        /// Reads <paramref name="root"/>. When it is a copy (an estimate's), objects whose item the copy no longer says are
        /// looked up in <paramref name="sceneRoot"/>, the avatar it was copied from, by their path.
        /// </summary>
        public static RankContributors Of(GameObject root, GameObject sceneRoot = null)
        {
            var result = new RankContributors();
            if (root == null) return result;
            var lookup = new ItemLookup(root, sceneRoot);
            (string Name, string Path)? Item(Transform t) => lookup.Of(t);
            string PathOf(Transform t) => AnimationUtility.CalculateTransformPath(t, root.transform);
            void Add(AvatarPerformanceCategory category, Component component, string name, float amount, float weight = 0f, bool counted = true)
            {
                if (amount <= 0f) return;
                var item = Item(component.transform);
                result.entries.Add((new RankContributor { Category = category, Name = name, Path = PathOf(component.transform), Item = item?.Name, Amount = amount }, weight > 0f ? weight : amount));
                if (item != null && counted) result.ItemOf(item.Value).Amounts[category] = result.ItemOf(item.Value).Amount(category) + amount;
            }

            // Bones are shared: an item saves only those no other renderer uses.
            var boneUsers = new Dictionary<Transform, HashSet<string>>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = MeshOptimization.MeshOf(renderer);
                if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                int triangles = mesh != null ? Triangles(mesh) : 0;
                string item = Item(renderer.transform)?.Name ?? "";
                Add(AvatarPerformanceCategory.PolyCount, renderer, renderer.name, triangles);
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    Add(AvatarPerformanceCategory.SkinnedMeshCount, renderer, renderer.name, 1f, 1f + triangles / 1e7f);
                    var bones = skinned.bones.Where(b => b != null).Distinct().ToList();
                    Add(AvatarPerformanceCategory.BoneCount, renderer, renderer.name, bones.Count, counted: false);
                    foreach (var bone in bones)
                    {
                        if (!boneUsers.TryGetValue(bone, out var users)) boneUsers[bone] = users = new HashSet<string>(StringComparer.Ordinal);
                        users.Add(item);
                    }
                }
                else if (mesh != null) Add(AvatarPerformanceCategory.MeshCount, renderer, renderer.name, 1f, 1f + triangles / 1e7f);
                Add(AvatarPerformanceCategory.MaterialCount, renderer, renderer.name, renderer.sharedMaterials.Length);
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    foreach (int id in material.GetTexturePropertyNameIDs())
                    {
                        var texture = material.GetTexture(id);
                        if (texture == null) continue;
                        if (!result.textures.TryGetValue(texture, out var known)) known = (PathOf(renderer.transform), new HashSet<string>(StringComparer.Ordinal), false);
                        if (item.Length > 0) known.Items.Add(item);
                        else known.Shared = true;
                        result.textures[texture] = known;
                    }
                }
            }
            foreach (var group in boneUsers.Where(p => p.Value.Count == 1 && p.Value.First().Length > 0).GroupBy(p => p.Value.First()))
                if (result.items.TryGetValue(group.Key, out var item)) item.Amounts[AvatarPerformanceCategory.BoneCount] = group.Count();

            foreach (var bone in root.GetComponentsInChildren<VRCPhysBoneBase>(true))
            {
                int transforms = AvatarUsage.PhysBoneChain(bone).Count;
                int colliders = bone.colliders?.Count(c => c != null) ?? 0;
                Add(AvatarPerformanceCategory.PhysBoneComponentCount, bone, bone.name, 1f, 1f + transforms / 1e4f);
                Add(AvatarPerformanceCategory.PhysBoneTransformCount, bone, bone.name, transforms);
                Add(AvatarPerformanceCategory.PhysBoneCollisionCheckCount, bone, bone.name, transforms * colliders);
            }
            foreach (var collider in root.GetComponentsInChildren<VRCPhysBoneColliderBase>(true))
                Add(AvatarPerformanceCategory.PhysBoneColliderCount, collider, collider.name, 1f);
            foreach (var contact in root.GetComponentsInChildren<ContactBase>(true))
                if (!(contact is ContactReceiver receiver && receiver.localOnly)) Add(AvatarPerformanceCategory.ContactCount, contact, contact.name, 1f);
            foreach (var constraint in root.GetComponentsInChildren<VRCConstraintBase>(true))
                Add(AvatarPerformanceCategory.ConstraintsCount, constraint, constraint.name, 1f);
            foreach (var constraint in root.GetComponentsInChildren<IConstraint>(true))
                if (constraint is Component component) Add(AvatarPerformanceCategory.ConstraintsCount, component, component.name, 1f);
            foreach (var light in root.GetComponentsInChildren<Light>(true)) Add(AvatarPerformanceCategory.LightCount, light, light.name, 1f);
            foreach (var particles in root.GetComponentsInChildren<ParticleSystem>(true)) Add(AvatarPerformanceCategory.ParticleSystemCount, particles, particles.name, 1f);
            foreach (var sound in root.GetComponentsInChildren<AudioSource>(true)) Add(AvatarPerformanceCategory.AudioSourceCount, sound, sound.name, 1f);
            foreach (var cloth in root.GetComponentsInChildren<Cloth>(true)) Add(AvatarPerformanceCategory.ClothCount, cloth, cloth.name, 1f);
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                if (animator.transform != root.transform) Add(AvatarPerformanceCategory.AnimatorCount, animator, animator.name, 1f);
            return result;
        }

        private RankItem ItemOf((string Name, string Path) item)
        {
            if (!items.TryGetValue(item.Name, out var found)) items[item.Name] = found = new RankItem { Name = item.Name, Path = item.Path };
            return found;
        }

        /// <summary>The triangles VRChat counts for a mesh: each triangle, and two per quad.</summary>
        public static int Triangles(Mesh mesh)
        {
            long count = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var topology = mesh.GetTopology(i);
                long indices = mesh.GetIndexCount(i);
                if (topology == MeshTopology.Triangles) count += indices / 3;
                else if (topology == MeshTopology.Quads) count += indices / 4 * 2;
            }
            return (int)Math.Min(int.MaxValue, count);
        }

        /// <summary>
        /// Gives <paramref name="stats"/> the biggest contributors of each category and each item's share, with the texture
        /// memory <paramref name="megabytes"/> gives each texture on that platform (an item counts the textures only it shows).
        /// </summary>
        public void Fill(RankStats stats, Func<Texture, float> megabytes)
        {
            var all = new List<(RankContributor Entry, float Weight)>(entries);
            var own = items.Values.Select(i => new RankItem { Name = i.Name, Path = i.Path, Amounts = new Dictionary<AvatarPerformanceCategory, float>(i.Amounts) })
                .ToDictionary(i => i.Name, StringComparer.Ordinal);
            foreach (var pair in textures)
            {
                float amount = megabytes(pair.Key);
                if (amount <= 0f) continue;
                string item = pair.Value.Items.Count == 1 && !pair.Value.Shared ? pair.Value.Items.First() : null;
                all.Add((new RankContributor
                {
                    Category = AvatarPerformanceCategory.TextureMegabytes, Name = pair.Key.name, Path = pair.Value.Path,
                    Item = item ?? (pair.Value.Items.Count > 0 && !pair.Value.Shared ? string.Join(", ", pair.Value.Items) : null), Amount = amount,
                    Asset = EditorUtility.IsPersistent(pair.Key) ? pair.Key.GetInstanceID() : 0,
                }, amount));
                if (item != null && own.TryGetValue(item, out var owner))
                    owner.Amounts[AvatarPerformanceCategory.TextureMegabytes] = owner.Amount(AvatarPerformanceCategory.TextureMegabytes) + amount;
            }
            stats.Costliest = all.GroupBy(e => e.Entry.Category)
                .SelectMany(g => g.OrderByDescending(e => e.Weight).ThenBy(e => e.Entry.Path, StringComparer.Ordinal).Take(PerCategory).Select(e => e.Entry)).ToList();
            stats.Items = own.Values.OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// The Orbiters item (an attachment) an object of an avatar belongs to, by its name and its path from the root. On a
    /// copy (an estimate's, a build's) that no longer says, the object at the same path on the avatar it was copied from.
    /// </summary>
    public sealed class ItemLookup
    {
        private readonly GameObject root, sceneRoot;
        private readonly Dictionary<Transform, (string Name, string Path)?> known = new Dictionary<Transform, (string, string)?>();

        public ItemLookup(GameObject root, GameObject sceneRoot = null) { this.root = root; this.sceneRoot = sceneRoot; }

        public (string Name, string Path)? Of(Transform t)
        {
            if (t == null || root == null) return null;
            if (known.TryGetValue(t, out var found)) return found;
            var attachment = t.GetComponentInParent<OrbitersAttachment>(true);
            if (attachment != null && attachment.transform != root.transform)
                found = (attachment.DisplayName, AnimationUtility.CalculateTransformPath(attachment.transform, root.transform));
            else if (attachment == null && sceneRoot != null && sceneRoot != root && t.IsChildOf(root.transform))
            {
                var same = sceneRoot.transform.Find(AnimationUtility.CalculateTransformPath(t, root.transform));
                var original = same != null ? same.GetComponentInParent<OrbitersAttachment>(true) : null;
                if (original != null && original.transform != sceneRoot.transform)
                    found = (original.DisplayName, AnimationUtility.CalculateTransformPath(original.transform, sceneRoot.transform));
            }
            return known[t] = found;
        }
    }
}
