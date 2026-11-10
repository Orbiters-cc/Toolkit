using System;
using System.Collections.Generic;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>When a task of the <see cref="OrbitersEditorHost"/> may run.</summary>
    public enum HostWhen
    {
        /// <summary>At every editor update, whatever the editor is doing.</summary>
        Always,
        /// <summary>
        /// Only while the editor is not compiling, importing, building a player or in (or entering) Play Mode: a task due
        /// meanwhile waits until it is.
        /// </summary>
        Idle,
    }

    /// <summary>What one task of the host has cost so far (times in milliseconds).</summary>
    public readonly struct HostTiming
    {
        public readonly string Name;
        public readonly long Runs;
        public readonly double TotalMilliseconds, LastMilliseconds, LongestMilliseconds;

        public HostTiming(string name, long runs, double total, double last, double longest)
        {
            Name = name; Runs = runs; TotalMilliseconds = total; LastMilliseconds = last; LongestMilliseconds = longest;
        }

        public override string ToString() => $"{Name}: {Runs} runs, {TotalMilliseconds:0.#} ms (last {LastMilliseconds:0.##} ms, longest {LongestMilliseconds:0.##} ms)";
    }

    /// <summary>
    /// The host's timetable, without Unity: the clock, the editor's state and the time spent are given to <see cref="Run"/>,
    /// so its rules are tested on their own. The tasks due run most overdue first until the frame's budget is spent; the
    /// others stay due and run on a later frame (at least one runs per frame, so none waits for ever). A periodic task is
    /// due again its interval after it ran: tasks that fell behind run once, not once per interval missed.
    /// </summary>
    internal sealed class HostScheduler
    {
        internal sealed class Task : IDisposable
        {
            public readonly string Name;
            public readonly double Interval;
            public readonly HostWhen When;
            public readonly bool Once;
            public readonly Action Tick;
            public double Due;
            public bool Stopped;
            private readonly HostScheduler owner;

            public Task(HostScheduler owner, string name, double interval, double due, HostWhen when, bool once, Action tick)
            {
                this.owner = owner; Name = name; Interval = interval; Due = due; When = when; Once = once; Tick = tick;
            }

            public void Dispose()
            {
                if (Stopped) return;
                Stopped = true;
                owner.removed = true;
            }
        }

        private readonly List<Task> tasks = new List<Task>();
        private readonly List<Task> due = new List<Task>();
        // By task name: one-shot tasks of one name add up, and a task stopped keeps what it cost.
        private readonly Dictionary<string, (long Runs, double Total, double Last, double Longest)> spent =
            new Dictionary<string, (long, double, double, double)>(StringComparer.Ordinal);
        private bool removed;

        /// <summary>The longest a frame's tasks may take together before the rest wait for the next frame.</summary>
        public double BudgetMilliseconds = 4;

        /// <summary>Called when a task throws (the others still run); by default nothing.</summary>
        public Action<string, Exception> Failed;

        public int Count
        {
            get
            {
                int count = 0;
                foreach (var task in tasks) if (!task.Stopped) count++;
                return count;
            }
        }

        public Task Add(string name, double interval, double firstDue, HostWhen when, bool once, Action tick)
        {
            if (tick == null) throw new ArgumentNullException(nameof(tick));
            var task = new Task(this, string.IsNullOrEmpty(name) ? tick.Method.Name : name, Math.Max(0, interval), firstDue, when, once, tick);
            tasks.Add(task);
            return task;
        }

        /// <summary>
        /// Runs the tasks due at <paramref name="now"/> (seconds). <paramref name="idle"/>: whether <see cref="HostWhen.Idle"/>
        /// tasks may run; <paramref name="elapsed"/> gives the milliseconds spent since the frame's tasks started. Returns how
        /// many ran.
        /// </summary>
        public int Run(double now, bool idle, Func<double> elapsed)
        {
            if (removed) { tasks.RemoveAll(t => t.Stopped); removed = false; }
            due.Clear();
            foreach (var task in tasks)
                if (!task.Stopped && task.Due <= now && (idle || task.When == HostWhen.Always)) due.Add(task);
            if (due.Count == 0) return 0;
            // Most overdue first; equal ones in the order they were added (an insertion sort: stable, and no allocation).
            for (int i = 1; i < due.Count; i++)
            {
                var task = due[i];
                int j = i - 1;
                for (; j >= 0 && due[j].Due > task.Due; j--) due[j + 1] = due[j];
                due[j + 1] = task;
            }
            int ran = 0;
            foreach (var task in due)
            {
                if (ran > 0 && elapsed() >= BudgetMilliseconds) break;
                // Stopped by a task that ran before it in this frame.
                if (task.Stopped) continue;
                double before = elapsed();
                if (task.Once) task.Dispose();
                else task.Due = now + task.Interval;
                try { task.Tick(); }
                catch (Exception ex) { Failed?.Invoke(task.Name, ex); }
                double cost = Math.Max(0, elapsed() - before);
                spent.TryGetValue(task.Name, out var known);
                spent[task.Name] = (known.Runs + 1, known.Total + cost, cost, Math.Max(known.Longest, cost));
                ran++;
            }
            return ran;
        }

        public List<HostTiming> Timings()
        {
            var result = new List<HostTiming>(spent.Count);
            foreach (var pair in spent) result.Add(new HostTiming(pair.Key, pair.Value.Runs, pair.Value.Total, pair.Value.Last, pair.Value.Longest));
            result.Sort((a, b) => b.TotalMilliseconds.CompareTo(a.TotalMilliseconds));
            return result;
        }

        public void Clear()
        {
            foreach (var task in tasks) task.Stopped = true;
            tasks.Clear();
            removed = false;
        }
    }
}
