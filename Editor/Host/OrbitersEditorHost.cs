using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Unity.Profiling;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// The editor services Orbiters tools share instead of each subscribing to Unity on its own:
    /// <list type="bullet">
    /// <item>one update pump (<see cref="Every"/>, <see cref="EveryFrame"/>, <see cref="Once"/>): the tasks due run most
    /// overdue first within <see cref="FrameBudgetMilliseconds"/> per editor frame, each timed by name
    /// (<see cref="Timings"/>, and a profiler marker "Orbiters.&lt;name&gt;"); <see cref="HostWhen.Idle"/> tasks wait while
    /// the editor compiles, imports, builds or plays. It runs from EditorApplication.update, which a background editor still
    /// calls (unlike delayCall, which waits for a repaint);</item>
    /// <item>one change bus (<see cref="EditorChangeBus"/>), delivered at the start of each frame before the tasks;</item>
    /// <item>services made on first use (<see cref="Service{T}"/>) and disposed before the next script reload;</item>
    /// <item>a script-reload lifetime: <see cref="Reload"/> is cancelled and <see cref="BeforeReload"/> raised just before
    /// the scripts reload (and when the editor quits).</item>
    /// </list>
    /// Nothing subscribes to Unity until something is registered. Direct subscriptions keep working beside it.
    /// </summary>
    public static class OrbitersEditorHost
    {
        private static readonly HostScheduler Scheduler = new HostScheduler { Failed = (name, ex) => Debug.LogException(new Exception("[Orbiters] " + name + " failed: " + ex.Message, ex)) };
        private static readonly Dictionary<string, ProfilerMarker> Markers = new Dictionary<string, ProfilerMarker>(StringComparer.Ordinal);
        private static readonly Dictionary<Type, object> Services = new Dictionary<Type, object>();
        private static readonly List<IDisposable> Disposables = new List<IDisposable>();
        private static readonly Stopwatch Frame = new Stopwatch();
        private static CancellationTokenSource reload = new CancellationTokenSource();
        private static bool pumping, lifetime;
        private static Action beforeReload;

        /// <summary>The longest the host's tasks may take together in one editor frame (at least one task runs per frame).</summary>
        public static double FrameBudgetMilliseconds
        {
            get => Scheduler.BudgetMilliseconds;
            set => Scheduler.BudgetMilliseconds = Math.Max(0.1, value);
        }

        /// <summary>Cancelled just before the scripts reload or the editor quits: give it to work that must not outlive them.</summary>
        public static CancellationToken Reload { get { EnsureLifetime(); return reload.Token; } }

        /// <summary>
        /// Raised just before the scripts reload (or the editor quits), after <see cref="Reload"/> is cancelled and before the
        /// services are disposed: free native memory, hidden objects and files here.
        /// </summary>
        public static event Action BeforeReload
        {
            add { EnsureLifetime(); beforeReload += value; }
            remove => beforeReload -= value;
        }

        /// <summary>
        /// Runs <paramref name="tick"/> every <paramref name="seconds"/> (first after one interval) until the result is
        /// disposed. <paramref name="name"/> names it in <see cref="Timings"/> and the profiler.
        /// </summary>
        public static IDisposable Every(string name, double seconds, Action tick, HostWhen when = HostWhen.Idle) =>
            Add(name, seconds, Now + Math.Max(0, seconds), when, false, tick);

        /// <summary>Runs <paramref name="tick"/> at every editor frame until the result is disposed.</summary>
        public static IDisposable EveryFrame(string name, Action tick, HostWhen when = HostWhen.Always) => Add(name, 0, Now, when, false, tick);

        /// <summary>
        /// Runs <paramref name="work"/> once, at the first frame at least <paramref name="delaySeconds"/> from now (also in a
        /// background editor); disposing the result first cancels it.
        /// </summary>
        public static IDisposable Once(string name, Action work, double delaySeconds = 0, HostWhen when = HostWhen.Idle) =>
            Add(name, 0, Now + Math.Max(0, delaySeconds), when, true, work);

        /// <summary>What each named task has cost since the last script reload, costliest first.</summary>
        public static IReadOnlyList<HostTiming> Timings => Scheduler.Timings();

        /// <summary>
        /// The one <typeparamref name="T"/> of this script session, made on first use. One that is <see cref="IDisposable"/>
        /// is disposed just before the scripts reload.
        /// </summary>
        public static T Service<T>() where T : class, new()
        {
            if (Services.TryGetValue(typeof(T), out var known)) return (T)known;
            var made = new T();
            Services[typeof(T)] = made;
            if (made is IDisposable disposable) { EnsureLifetime(); Disposables.Add(disposable); }
            return made;
        }

        /// <summary>Whether <see cref="Service{T}"/> has made its <typeparamref name="T"/> yet (without making it).</summary>
        public static bool HasService<T>() where T : class => Services.ContainsKey(typeof(T));

        /// <summary>Whether <see cref="HostWhen.Idle"/> tasks may run now.</summary>
        public static bool Idle => !EditorApplication.isCompiling && !EditorApplication.isUpdating && !EditorApplication.isPlayingOrWillChangePlaymode &&
                                   !BuildPipeline.isBuildingPlayer;

        private static double Now => EditorApplication.timeSinceStartup;

        private static IDisposable Add(string name, double interval, double due, HostWhen when, bool once, Action tick)
        {
            if (tick == null) throw new ArgumentNullException(nameof(tick));
            if (string.IsNullOrEmpty(name)) name = tick.Method.DeclaringType?.Name + "." + tick.Method.Name;
            var task = Scheduler.Add(name, interval, due, when, once, Timed(name, tick));
            EnsurePump();
            return task;
        }

        internal static void EnsurePump()
        {
            EnsureLifetime();
            if (pumping) return;
            pumping = true;
            EditorApplication.update += Pump;
        }

        private static void EnsureLifetime()
        {
            if (lifetime) return;
            lifetime = true;
            AssemblyReloadEvents.beforeAssemblyReload += End;
            EditorApplication.quitting += End;
        }

        private static void Pump()
        {
            Frame.Restart();
            EditorChangeBus.Flush();
            if (Scheduler.Count == 0) return;
            Scheduler.Run(Now, Idle, () => Frame.Elapsed.TotalMilliseconds);
        }

        // The tick inside the profiler marker of its name (made once per name).
        private static Action Timed(string name, Action tick)
        {
            if (!Markers.TryGetValue(name, out var marker)) Markers[name] = marker = new ProfilerMarker("Orbiters." + name);
            return () => { using (marker.Auto()) tick(); };
        }

        private static void End()
        {
            reload.Cancel();
            var handlers = beforeReload;
            beforeReload = null;
            if (handlers != null)
                foreach (Action handler in handlers.GetInvocationList())
                {
                    try { handler(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            for (int i = Disposables.Count - 1; i >= 0; i--)
            {
                try { Disposables[i].Dispose(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
            Disposables.Clear();
            Services.Clear();
            Scheduler.Clear();
            if (pumping) { EditorApplication.update -= Pump; pumping = false; }
            reload = new CancellationTokenSource();
        }
    }
}
