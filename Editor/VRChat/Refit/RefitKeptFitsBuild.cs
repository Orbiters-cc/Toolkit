using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Meshes;
using Orbiters.Toolkit.VRChat;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>
    /// At build, the fits kept for a refitted renderer (<see cref="OrbitersRefit.keptFits"/>) become a choice in the avatar's
    /// menu: each one other than the current fit is a blendshape of (a copy of) the current mesh ("ReFit fit: name"), the
    /// difference between the two fits at rest, and VRCFury toggles, one at a time, under "Fits/clothing" pick one; the
    /// current fit is on while the others are off. A fit at rest is its mesh with its fit shape applied
    /// (<see cref="RefitRecords.FitShape(Mesh, Mesh, IEnumerable{RefitShape})"/>): the refit engines keep the original as the
    /// mesh and put the fit in that blendshape. A kept fit skinned otherwise than the current one (another armature) is left
    /// out: a blendshape cannot carry another skinning. The clothing's own toggle still shows or hides it, whichever fit is
    /// picked. Runs before VRCFury (-10000) builds the menu; needs VRCFury. Never stops an upload: what cannot be done is
    /// reported.
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
                var others = new List<RefitKeptFit>();
                foreach (var fit in Others(record))
                {
                    if (fit.Mesh.vertexCount != record.mesh.vertexCount) warnings.Add($"{record.name}'s kept fit '{fit.name}' is of another mesh: left out.");
                    else if (!SameSkinning(fit.Mesh, record.mesh)) warnings.Add($"{record.name}'s kept fit '{fit.name}' is skinned to another armature: left out.");
                    else others.Add(fit);
                }
                if (others.Count == 0) continue;
                // Named and measured before the copy below takes the current mesh's place.
                var current = RefitRecords.CurrentFit(record);
                string currentShape = RefitRecords.FitShape(record);
                var original = record.original != null ? record.original.mesh : null;
                var mesh = RefitBuildMeshes.Own(renderer, copies, others.Count, record.name + "'s kept fits are", warnings);
                if (mesh == null) continue;
                int currentIndex = string.IsNullOrEmpty(currentShape) ? -1 : mesh.GetBlendShapeIndex(currentShape);
                FittedRest(mesh, currentShape, currentIndex >= 0 ? renderer.GetBlendShapeWeight(currentIndex) : 0f, out var vertices, out var normals);
                var added = new List<RefitKeptFit>();
                foreach (var fit in others)
                {
                    string shape = ShapeName(fit);
                    if (mesh.GetBlendShapeIndex(shape) >= 0) { added.Add(fit); continue; }
                    string fitShape = record.kind == OrbitersRefit.FitKind.Fitted ? RefitRecords.FitShape(fit.Mesh, original, fit.shapes) : null;
                    FittedRest(fit.Mesh, fitShape, WeightIn(fit.state, fitShape, 100f), out var fitVertices, out var fitNormals);
                    var deltas = new Vector3[vertices.Length];
                    var normalDeltas = normals != null && fitNormals != null ? new Vector3[vertices.Length] : null;
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

        // A fit's mesh at rest as the fit leaves it: its vertices and normals with the fit shape at its weight.
        private static void FittedRest(Mesh mesh, string fitShape, float weight, out Vector3[] vertices, out Vector3[] normals)
        {
            vertices = mesh.vertices;
            normals = mesh.normals;
            if (normals.Length != vertices.Length) normals = null;
            int index = string.IsNullOrEmpty(fitShape) ? -1 : mesh.GetBlendShapeIndex(fitShape);
            if (index >= 0)
                BlendShapeEvaluation.Add(mesh, index, weight, BlendShapeEvaluation.ClampWeights, vertices, normals, new BlendShapeEvaluation.Scratch(vertices.Length));
        }

        // A shape's weight on the renderer as a fit was captured, or the fallback when it was not.
        private static float WeightIn(RefitRendererState state, string shape, float fallback)
        {
            if (state == null || string.IsNullOrEmpty(shape)) return fallback;
            int count = Math.Min(state.blendShapeNames.Count, state.blendShapeWeights.Count);
            for (int i = 0; i < count; i++)
                if (state.blendShapeNames[i] == shape) return state.blendShapeWeights[i];
            return fallback;
        }

        // The same bind poses and bone weights: a blendshape of one mesh shows the other as it is skinned.
        private static bool SameSkinning(Mesh a, Mesh b)
        {
            var poses = a.bindposes;
            var others = b.bindposes;
            if (poses.Length != others.Length) return false;
            for (int i = 0; i < poses.Length; i++)
                for (int c = 0; c < 16; c++)
                    if (Mathf.Abs(poses[i][c] - others[i][c]) > 1e-4f) return false;
            var counts = a.GetBonesPerVertex();
            var otherCounts = b.GetBonesPerVertex();
            if (counts.Length != otherCounts.Length) return false;
            for (int i = 0; i < counts.Length; i++)
                if (counts[i] != otherCounts[i]) return false;
            var weights = a.GetAllBoneWeights();
            var otherWeights = b.GetAllBoneWeights();
            if (weights.Length != otherWeights.Length) return false;
            for (int i = 0; i < weights.Length; i++)
                if (weights[i].boneIndex != otherWeights[i].boneIndex || Mathf.Abs(weights[i].weight - otherWeights[i].weight) > 1e-4f) return false;
            return true;
        }
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
