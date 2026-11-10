using System;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// Work done once things have been quiet for a moment: every <see cref="Request"/> starts the wait again, and the work
    /// runs once, <see cref="Quiet"/> seconds after the last request, on the editor host (so also in a background editor,
    /// unlike delayCall). <see cref="Flush"/> runs pending work now; <see cref="Dispose"/> drops it.
    /// </summary>
    public sealed class Debouncer : IDisposable
    {
        private readonly string name;
        private readonly Action work;
        private readonly HostWhen when;
        private IDisposable scheduled;

        public double Quiet { get; set; }

        /// <summary>Whether work waits to run.</summary>
        public bool Pending => scheduled != null;

        public Debouncer(string name, double quietSeconds, Action work, HostWhen when = HostWhen.Idle)
        {
            this.name = name;
            this.work = work ?? throw new ArgumentNullException(nameof(work));
            this.when = when;
            Quiet = quietSeconds;
        }

        public void Request()
        {
            scheduled?.Dispose();
            scheduled = OrbitersEditorHost.Once(name, Run, Quiet, when);
        }

        public void Flush()
        {
            if (scheduled != null) Run();
        }

        private void Run()
        {
            scheduled?.Dispose();
            scheduled = null;
            work();
        }

        public void Dispose()
        {
            scheduled?.Dispose();
            scheduled = null;
        }
    }
}
