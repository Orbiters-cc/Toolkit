using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.VRChat;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>
    /// At build, the fits kept for a refitted renderer (<see cref="OrbitersRefit.keptFits"/>) become a choice in the avatar's
    /// menu: each one other than the current fit is a blendshape of (a copy of) the current mesh, its rest shape's difference
    /// ("ReFit fit: name"), and VRCFury toggles, one at a time, under "Fits/clothing" pick one; the current fit is on while
    /// the others are off. The clothing's own toggle still shows or hides it, whichever fit is picked. Runs before VRCFury
    /// (-10000) builds the menu; needs VRCFury. Never stops an upload: what cannot be done is reported.
    /// </summary>
    public static class RefitKeptFitsBuild
    {
        public const string MenuRoot = "Fits";

        /// <summary>The blendshape a kept fit becomes on the current mesh.</summary>
        public static string ShapeName(RefitKeptFit fit) => "ReFit fit: " + fit.name;

        public static int Apply(GameObject avatarRoot, List<string> warnings)
        {
            if (avatarRoot == null) return 0;
            var records = avatarRoot.GetComponentsInChildren<OrbitersRefit>(true).Where(r => r.Applied && Others(r).Any()).ToList();
            if (records.Count == 0) return 0;
            var writer = VrcFury.Writer;
            if (writer == null)
            {
                warnings.Add("the fits kept to choose from need VRCFury for their menu: only each piece's current fit is uploaded.");
                return 0;
            }
            var copies = new Dictionary<SkinnedMeshRenderer, Mesh>();
            int applied = 0;
            foreach (var record in records)
            {
                var renderer = record.GetComponent<SkinnedMeshRenderer>();
                var others = Others(record).Where(f => f.Mesh.vertexCount == record.mesh.vertexCount).ToList();
                if (others.Count == 0)
                {
                    warnings.Add($"{record.name}'s kept fits are of another mesh: left out.");
                    continue;
                }
                // Named before the copy below takes the current mesh's place.
                var current = RefitRecords.CurrentFit(record);
                var mesh = RefitBuildMeshes.Own(renderer, copies, others.Count, record.name + "'s kept fits are", warnings);
                if (mesh == null) continue;
                var vertices = mesh.vertices;
                var normals = mesh.normals;
                var added = new List<RefitKeptFit>();
                foreach (var fit in others)
                {
                    string shape = ShapeName(fit);
                    if (mesh.GetBlendShapeIndex(shape) >= 0) { added.Add(fit); continue; }
                    var fitVertices = fit.Mesh.vertices;
                    var fitNormals = fit.Mesh.normals;
                    var deltas = new Vector3[vertices.Length];
                    var normalDeltas = normals.Length == vertices.Length && fitNormals.Length == vertices.Length ? new Vector3[vertices.Length] : null;
                    for (int i = 0; i < deltas.Length; i++)
                    {
                        deltas[i] = fitVertices[i] - vertices[i];
                        if (normalDeltas != null) normalDeltas[i] = fitNormals[i] - normals[i];
                    }
                    mesh.AddBlendShapeFrame(shape, 100f, deltas, normalDeltas, null);
                    added.Add(fit);
                }
                if (added.Count == 0) continue;
                // One toggle per fit, one at a time; the current fit is the one on while the others are off.
                string menu = MenuRoot + "/" + record.name + "/";
                string tag = "ReFit fit " + record.GetInstanceID();
                writer.ShapeChoice(record.gameObject, menu + (current != null ? current.name : "Current fit"), tag, renderer, null);
                foreach (var fit in added) writer.ShapeChoice(record.gameObject, menu + fit.name, tag, renderer, ShapeName(fit));
                applied++;
                Debug.Log($"[Orbiters] ReFit: {record.name} offers {added.Count + 1} fits in the menu ({MenuRoot}/{record.name}).");
            }
            return applied;
        }

        // The kept fits other than the one worn now.
        private static IEnumerable<RefitKeptFit> Others(OrbitersRefit record) =>
            record.keptFits.Where(f => f != null && f.Mesh != null && f.Mesh != record.mesh && !string.IsNullOrEmpty(f.name));
    }

    internal sealed class RefitKeptFitsHook : IVRCSDKPreprocessAvatarCallback
    {
        // After the refits are captured (-10110), before My Avatar attachments move meshes (-10100) and VRCFury (-10000)
        // builds the menu from the toggles added here.
        public int callbackOrder => -10105;

        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            var warnings = new List<string>();
            try { RefitKeptFitsBuild.Apply(avatarRoot, warnings); }
            catch (Exception ex) { warnings.Add(ex.Message); }
            foreach (var warning in warnings.Distinct()) Debug.LogWarning("[Orbiters] ReFit: " + warning, avatarRoot);
            return true;
        }
    }
}
