using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Animations;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Build
{
    /// <summary>
    /// One build of one avatar copy, shared by every step that runs on it: what the steps hand each other (instead of
    /// statics keyed by the avatar), the build's own copies of the animations, an index of what the animations bind, and
    /// the meshes the steps made. It starts with the build's first phase (or earlier, when a caller prepares a hidden
    /// build with <see cref="For"/>), and ends after the last phase or when the hidden build is done. A build that stops
    /// half-way leaves its context until the copy is gone or the same object is built again.
    /// Other tools' SDK callbacks, which run between the phases, reach it with <see cref="Of"/>.
    /// </summary>
    public sealed class BuildContext
    {
        private static readonly List<BuildContext> Open = new List<BuildContext>();

        private readonly Dictionary<Type, object> states = new Dictionary<Type, object>();
        private readonly HashSet<string> keptShapes = new HashSet<string>(StringComparer.Ordinal);
        private AnimationIndex clips;
        // The last phase that ran, and whether one is running.
        private int phase = -1;

        /// <summary>The build copy's root.</summary>
        public GameObject Avatar { get; }

        /// <summary>The phase running now, or the last one that ran; null before the first.</summary>
        public BuildPhase? Phase => phase < 0 ? (BuildPhase?)null : (BuildPhase)phase;

        /// <summary>The meshes the steps made for this build so far (in memory until the build data is saved), in the order made.</summary>
        public List<Mesh> Meshes { get; } = new List<Mesh>();

        private BuildContext(GameObject avatar) => Avatar = avatar;

        /// <summary>The build of <paramref name="avatar"/> under way, made now if there is none (a caller preparing a hidden build).</summary>
        public static BuildContext For(GameObject avatar)
        {
            if (avatar == null) throw new ArgumentNullException(nameof(avatar));
            var context = Of(avatar);
            if (context != null) return context;
            Open.RemoveAll(c => c.Avatar == null);
            context = new BuildContext(avatar);
            Open.Add(context);
            return context;
        }

        /// <summary>The build of <paramref name="avatar"/> under way, or null.</summary>
        public static BuildContext Of(GameObject avatar)
        {
            if (avatar == null) return null;
            foreach (var context in Open)
                if (context.Avatar == avatar) return context;
            return null;
        }

        // A phase starts on this avatar (<paramref name="begins"/>), or one of its steps runs. Phases only go forward within
        // a build: one that begins again, or an earlier one, is a new build of the same object (Play Mode builds the scene's
        // avatar, a test builds its copy twice).
        internal static BuildContext Enter(GameObject avatar, BuildPhase phase, bool begins)
        {
            var context = Of(avatar);
            if (context != null && (context.phase > (int)phase || begins && context.phase == (int)phase))
            {
                Open.Remove(context);
                context = null;
            }
            context ??= For(avatar);
            context.phase = (int)phase;
            return context;
        }

        /// <summary>Ends the build of <paramref name="avatar"/>: what its steps shared is forgotten.</summary>
        internal static void End(GameObject avatar) => Open.RemoveAll(c => ReferenceEquals(c.Avatar, avatar) || c.Avatar == null);

        [InitializeOnLoadMethod]
        private static void ForgetOnReload() => AssemblyReloadEvents.beforeAssemblyReload += Open.Clear;

        /// <summary>
        /// What one kind of step keeps for this build (made empty on first use): the state its steps in several phases, or
        /// several steps, share. Each type is its owner's; nothing else should change it.
        /// </summary>
        public T State<T>() where T : class, new()
        {
            if (!states.TryGetValue(typeof(T), out var state)) states[typeof(T)] = state = new T();
            return (T)state;
        }

        /// <summary>
        /// The build's own copies of the avatar's animations, made the first time a step asks (controllers VRCFury built are
        /// used where they are). Steps change clips and controllers there, never the project's.
        /// </summary>
        public AttachmentAnimationBuild Animations => AttachmentAnimationBuild.Prepare(Avatar);

        /// <summary>
        /// What the avatar's animations bind (<see cref="AnimationIndex"/>), shared by every step of the build. Each time it
        /// is asked for it lists the avatar's clips again and reads again those that changed, since other tools' steps edit
        /// them without telling it. Steps that edit clips do it through the index.
        /// </summary>
        public AnimationIndex Clips
        {
            get
            {
                if (clips == null) clips = AvatarUsage.AnimationsOf(Avatar);
                else clips.Sync();
                return clips;
            }
        }

        /// <summary>Blendshapes the caller needs on the built avatar, on any of its meshes (a VRM export's expressions): no build step removes them.</summary>
        public IReadOnlyCollection<string> KeptShapes => keptShapes;

        /// <summary>Keeps these blendshapes on the built avatar (see <see cref="KeptShapes"/>); call it before the build.</summary>
        public void KeepShapes(IEnumerable<string> shapes)
        {
            foreach (var shape in shapes ?? Enumerable.Empty<string>())
                if (!string.IsNullOrEmpty(shape)) keptShapes.Add(shape);
        }

        /// <summary>
        /// Many edits to the avatar's animator controllers at once (<see cref="ControllerEdits.Batch"/>): every controller of
        /// its playable layers and Animator components is detached while the returned scope lasts, so each edit doesn't
        /// rebuild it. Inside the batch reach states through the layers read before it, add or remove no layers, save nothing.
        /// </summary>
        public IDisposable EditControllers() =>
            ControllerEdits.Batch(AvatarUsage.Controllers(Avatar).OfType<AnimatorController>().ToList());
    }
}
