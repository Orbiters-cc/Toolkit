using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Refit
{
    public enum RefitMode
    {
        /// <summary>Fit the mesh from the original base body to this avatar's body, and add the body's blendshapes.</summary>
        Fit,
        /// <summary>The mesh already fits this body: only add the body's blendshapes.</summary>
        Shapes,
    }

    /// <summary>One clothing or accessory mesh to refit onto an avatar's body.</summary>
    public sealed class RefitJob
    {
        public SkinnedMeshRenderer Renderer;
        public GameObject Avatar;
        public SkinnedMeshRenderer Body;
        /// <summary>The original base the mesh was made for (Fit only).</summary>
        public GameObject SourceAvatar;
        public SkinnedMeshRenderer SourceBody;
        /// <summary>Body blendshapes the mesh gets, under the same names.</summary>
        public List<string> Shapes = new List<string>();
        public RefitMode Mode;
        /// <summary>0: loose, best for accessories; 1: tight, best for clothing.</summary>
        public float Tightness = RefitPreferences.DefaultTightness;
    }

    public enum RefitSeverity { Info, Warning, Error }

    public struct RefitMessage
    {
        public RefitSeverity Severity;
        /// <summary>The engine's stable code, e.g. "surface-coverage-limited".</summary>
        public string Code;
        public string Text;
    }

    public sealed class RefitOutcome
    {
        // Results a person could fit better by hand: worth offering a commission.
        private static readonly HashSet<string> RoughCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "surface-coverage-limited", "tube-contact-unresolved", "tube-geometry-invalid", "proportions-mismatch", "armature-match-weak"
        };

        public bool Success, Cancelled;
        /// <summary>The generated mesh asset, now on the renderer.</summary>
        public Mesh Mesh;
        public string MeshPath;
        /// <summary>The generated shape carrying the fit itself (Fit mode), kept at 100.</summary>
        public string PrimaryShape;
        /// <summary>Body blendshapes, aligned with <see cref="GeneratedShapes"/> (renamed when a name was taken).</summary>
        public string[] SourceShapes = Array.Empty<string>();
        public string[] GeneratedShapes = Array.Empty<string>();
        public List<RefitMessage> Messages = new List<RefitMessage>();

        public string Error => Messages.Where(m => m.Severity == RefitSeverity.Error).Select(m => m.Text).FirstOrDefault()
                               ?? (Cancelled ? "Cancelled." : Success ? null : "The refit did not complete.");

        /// <summary>Fitted, but with spots the engine could not fully correct (or failed on the garment's shape).</summary>
        public bool Rough => !Cancelled && Messages.Any(m => m.Code != null && RoughCodes.Contains(m.Code));

        public IEnumerable<RefitMessage> Warnings => Messages.Where(m => m.Severity == RefitSeverity.Warning);
    }

    /// <summary>
    /// What an installed refit engine (ReFit, the orbiters.refit package) offers the other Orbiters tools. The engine
    /// registers itself when its editor code loads; nothing references the package directly, so a tool only needs it once
    /// the user asks for a refit.
    /// </summary>
    public interface IRefitEngine
    {
        string Name { get; }

        /// <summary>
        /// Refits one mesh in the scene with Undo: drive the enumerator on the main thread (heavy work runs on a worker) and
        /// dispose it when abandoned. Cancellation never applies partial output. <paramref name="done"/> is always called.
        /// </summary>
        IEnumerator Run(RefitJob job, Action<float, string> progress, Action<RefitOutcome> done, CancellationToken cancellation);

        /// <summary>The binding data the engine keeps on a refitted renderer for later passes, as JSON; null when there is none.</summary>
        string SaveMetadata(SkinnedMeshRenderer renderer);
        void LoadMetadata(SkinnedMeshRenderer renderer, string json);
        void RemoveMetadata(SkinnedMeshRenderer renderer);

        /// <summary>Opens the engine's window on this result, where the user can ask a creator to fit the mesh by hand.</summary>
        void OpenCommission(RefitJob job, RefitOutcome outcome);
    }

    public static class RefitEngine
    {
        public const string PackageId = "orbiters.refit";

        public static IRefitEngine Current { get; private set; }
        public static bool Available => Current != null;
        /// <summary>Raised when an engine registers (after ReFit was installed and Unity reloaded its scripts).</summary>
        public static event Action Changed;

        public static void Register(IRefitEngine engine)
        {
            Current = engine;
            Changed?.Invoke();
        }
    }
}
