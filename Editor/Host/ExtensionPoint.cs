using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// A place where other packages plug into a Toolkit feature: each registration names its owner and is undone by
    /// disposing what <see cref="Register"/> returns, so a test or a feature turned off leaves no trace. Enumerating gives
    /// the registered values in registration order, from a snapshot (a registration made meanwhile counts next time).
    /// <see cref="Each"/> runs every one, and one that throws is logged with its owner instead of stopping the others.
    /// </summary>
    public sealed class ExtensionPoint<T> : IEnumerable<T> where T : class
    {
        private readonly string name;
        private readonly List<Entry> entries = new List<Entry>();

        private sealed class Entry : IDisposable
        {
            public ExtensionPoint<T> Point;
            public string Owner;
            public T Value;
            public void Dispose()
            {
                Point?.entries.Remove(this);
                Point = null;
            }
        }

        /// <param name="name">What the extension point is, for errors ("Parameters a build removes").</param>
        public ExtensionPoint(string name) { this.name = name; }

        public int Count => entries.Count;

        /// <summary>Adds <paramref name="value"/> for <paramref name="owner"/> ("My Avatar"); dispose the result to remove it.</summary>
        public IDisposable Register(string owner, T value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var entry = new Entry { Point = this, Owner = owner, Value = value };
            entries.Add(entry);
            return entry;
        }

        /// <summary>Registers without an owner, for the callers of the lists this replaced (<c>List.Add</c>).</summary>
        public void Add(T value) => Register(null, value);

        /// <summary>Removes every registration of <paramref name="value"/>.</summary>
        public bool Remove(T value) => entries.RemoveAll(e => ReferenceEquals(e.Value, value)) > 0;

        /// <summary>
        /// Runs <paramref name="run"/> on each value in registration order; one that throws is logged with its owner and
        /// counted as answering <paramref name="failed"/>. Returns the answers.
        /// </summary>
        public List<TResult> Each<TResult>(Func<T, TResult> run, TResult failed = default)
        {
            var results = new List<TResult>(entries.Count);
            foreach (var entry in entries.ToArray())
            {
                try { results.Add(run(entry.Value)); }
                catch (Exception ex)
                {
                    Debug.LogException(new Exception("[Orbiters] " + name + (entry.Owner != null ? " (" + entry.Owner + ")" : "") + " failed: " + ex.Message, ex));
                    results.Add(failed);
                }
            }
            return results;
        }

        public IEnumerator<T> GetEnumerator()
        {
            foreach (var entry in entries.ToArray()) yield return entry.Value;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
