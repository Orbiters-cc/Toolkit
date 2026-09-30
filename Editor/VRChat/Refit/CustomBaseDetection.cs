using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>What is known about an avatar's custom base: which one, and where its blendshapes move the body.</summary>
    public sealed class CustomBaseState
    {
        public CustomBaseInfo Info;
        public BodyShapeMap Map;

        /// <summary>A known custom base with blendshapes clothing can follow.</summary>
        public bool Known => Info != null && Map != null && Info.Shapes.Count > 0;
    }

    /// <summary>
    /// Finds out in the background, once per avatar, which custom base it uses: first from a tool on the avatar (MCB knows
    /// the exact version), else from its model file (Orbiters server). Then measures where the custom blendshapes move the
    /// body. Detected again when a tool reports a change (a version applied) or the body mesh changes.
    /// </summary>
    public static class CustomBaseDetection
    {
        private sealed class Entry
        {
            public string Signature;
            public Task<CustomBaseState> Task;
            public CancellationTokenSource Cancel;
        }

        private static readonly Dictionary<int, Entry> Entries = new Dictionary<int, Entry>();

        /// <summary>Raised with the avatar root when its detection starts over (its custom base changed).</summary>
        public static event Action<Transform> Changed;

        [InitializeOnLoadMethod]
        private static void Listen() => CustomBases.Changed += Invalidate;

        /// <summary>The finished detection for this avatar, or null while it runs or was never started.</summary>
        public static CustomBaseState Current(Transform avatarRoot)
        {
            if (avatarRoot == null || !Entries.TryGetValue(avatarRoot.GetInstanceID(), out var entry)) return null;
            return entry.Task.Status == TaskStatus.RanToCompletion && entry.Signature == Signature(avatarRoot, out _, out _) ? entry.Task.Result : null;
        }

        /// <summary>Detects (or returns the running or finished detection of) the avatar's custom base. Main thread.</summary>
        public static Task<CustomBaseState> DetectAsync(Transform avatarRoot)
        {
            if (avatarRoot == null) return Task.FromResult(new CustomBaseState());
            string signature = Signature(avatarRoot, out var info, out var body);
            int id = avatarRoot.GetInstanceID();
            if (Entries.TryGetValue(id, out var entry) && entry.Signature == signature && !entry.Task.IsFaulted && !entry.Task.IsCanceled)
                return entry.Task;
            entry?.Cancel.Cancel();
            var cancel = new CancellationTokenSource();
            entry = new Entry { Signature = signature, Cancel = cancel, Task = RunAsync(info, body, cancel.Token) };
            Entries[id] = entry;
            return entry.Task;
        }

        /// <summary>Forgets the avatar's detection; the next <see cref="DetectAsync"/> starts over.</summary>
        public static void Invalidate(Transform avatarRoot)
        {
            if (avatarRoot == null) return;
            if (Entries.TryGetValue(avatarRoot.GetInstanceID(), out var entry))
            {
                entry.Cancel.Cancel();
                Entries.Remove(avatarRoot.GetInstanceID());
            }
            Changed?.Invoke(avatarRoot);
        }

        private static async Task<CustomBaseState> RunAsync(CustomBaseInfo info, SkinnedMeshRenderer body, CancellationToken cancellation)
        {
            var state = new CustomBaseState { Info = info };
            if (state.Info == null && body != null) state.Info = await CustomBaseFingerprint.IdentifyAsync(body, cancellation);
            if (state.Info?.Body == null || state.Info.Shapes.Count == 0) return state;
            state.Map = await BodyShapeMap.BuildAsync(state.Info.Body, state.Info.Shapes, cancellation);
            return state;
        }

        // What the detection depends on: the provider's answer, else the body mesh (a new version swaps it).
        private static string Signature(Transform avatarRoot, out CustomBaseInfo info, out SkinnedMeshRenderer body)
        {
            info = CustomBases.Describe(avatarRoot);
            body = info?.Body ?? AttachmentPlanner.Body(avatarRoot);
            var mesh = body != null ? body.sharedMesh : null;
            return (info != null ? info.Source + ":" + info.Key + ":" + info.Shapes.Count : "file") + "|" +
                   (body != null ? body.GetInstanceID() : 0) + "|" + (mesh != null ? mesh.GetInstanceID() : 0);
        }
    }
}
