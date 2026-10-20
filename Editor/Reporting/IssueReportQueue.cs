using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>One log waiting to be sent, as logged: it is masked only when it is about to go (<see cref="IssueRedactor"/>).</summary>
    internal sealed class IssueReport
    {
        public string Message;
        public string Stack;
        public LogType Type;
        public string Severity;
    }

    /// <summary>
    /// The reports one tool has waiting, bounded in memory and in rate: at most <see cref="QueueLimit"/> waiting, one taken
    /// every 10 seconds, the same problem once an hour (messages that only differ by numbers, ids or quoted names are one
    /// problem; what was reported is kept across script reloads with <see cref="SaveSignatures"/>), and the warning a tool
    /// logs right after an exception, repeating its message, folded into it. Thread-safe: logs come from any thread.
    /// </summary>
    internal sealed class IssueReportQueue
    {
        internal const int QueueLimit = 32, SignatureLimit = 128;
        // What is kept of a log until it is masked: a secret cut at this point is dropped with the rest at the final cut.
        internal const int MessageKeep = 8000, StackKeep = 24000;
        internal static readonly TimeSpan SendInterval = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan RepeatWindow = TimeSpan.FromHours(1);
        internal static readonly TimeSpan PairWindow = TimeSpan.FromSeconds(10);

        private static readonly Regex Variable = new Regex(
            @"\u201C[^\u201D]*\u201D|""[^""]*""|'[^'\s][^']*'|\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b|\b(?=[0-9a-f]*\d)[0-9a-f]{6,}\b|\d+(?:\.\d+)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

        private readonly Queue<IssueReport> pending = new Queue<IssueReport>();
        private readonly Dictionary<string, DateTime> signatures = new Dictionary<string, DateTime>();
        private readonly object gate = new object();
        private DateTime nextSend;
        private string lastException;
        private DateTime lastExceptionAt;

        internal int PendingCount { get { lock (gate) return pending.Count; } }
        internal int SignatureCount { get { lock (gate) return signatures.Count; } }

        /// <summary>Adds a report unless the same problem was reported within the hour, it repeats the exception just before it, or the queue is full.</summary>
        internal bool Enqueue(IssueReport report, DateTime now)
        {
            string message = report.Message ?? string.Empty;
            string signature = report.Severity + "|" + Signature(message);
            string exception = report.Type == LogType.Exception ? ExceptionText(message) : null;
            lock (gate)
            {
                if (report.Type != LogType.Exception && lastException != null && now - lastExceptionAt <= PairWindow &&
                    message.IndexOf(lastException, StringComparison.Ordinal) >= 0)
                {
                    return false;
                }

                if (report.Type == LogType.Exception)
                {
                    lastException = exception;
                    lastExceptionAt = now;
                }

                foreach (var key in signatures.Where(p => now - p.Value >= RepeatWindow).Select(p => p.Key).ToArray())
                {
                    signatures.Remove(key);
                }

                if (signatures.ContainsKey(signature) || pending.Count >= QueueLimit)
                {
                    return false;
                }

                if (signatures.Count >= SignatureLimit)
                {
                    signatures.Remove(signatures.OrderBy(p => p.Value).First().Key);
                }

                signatures[signature] = now;
                pending.Enqueue(report);
                return true;
            }
        }

        /// <summary>The next report, at most one every <see cref="SendInterval"/>.</summary>
        internal bool TryTake(DateTime now, out IssueReport report)
        {
            lock (gate)
            {
                report = null;
                if (pending.Count == 0 || now < nextSend)
                {
                    return false;
                }

                nextSend = now + SendInterval;
                report = pending.Dequeue();
                return true;
            }
        }

        [Serializable]
        private sealed class SavedSignatures
        {
            public string[] signatures = Array.Empty<string>();
            public long[] ticks = Array.Empty<long>();
        }

        /// <summary>The problems reported within the hour and when, for <see cref="LoadSignatures"/> after a script reload.</summary>
        internal string SaveSignatures()
        {
            lock (gate)
            {
                return JsonUtility.ToJson(new SavedSignatures
                {
                    signatures = signatures.Keys.ToArray(),
                    ticks = signatures.Values.Select(time => time.Ticks).ToArray()
                });
            }
        }

        /// <summary>Remembers again the problems <see cref="SaveSignatures"/> gave; anything unreadable is left out.</summary>
        internal void LoadSignatures(string saved)
        {
            if (string.IsNullOrEmpty(saved))
            {
                return;
            }

            SavedSignatures data;
            try
            {
                data = JsonUtility.FromJson<SavedSignatures>(saved);
            }
            catch (ArgumentException)
            {
                return;
            }

            if (data?.signatures == null || data.ticks == null)
            {
                return;
            }

            lock (gate)
            {
                for (int i = 0; i < data.signatures.Length && i < data.ticks.Length && signatures.Count < SignatureLimit; i++)
                {
                    if (!string.IsNullOrEmpty(data.signatures[i]) && data.ticks[i] >= DateTime.MinValue.Ticks && data.ticks[i] <= DateTime.MaxValue.Ticks)
                    {
                        signatures[data.signatures[i]] = new DateTime(data.ticks[i], DateTimeKind.Utc);
                    }
                }
            }
        }

        /// <summary>Drops what is waiting and forgets what was sent (sharing turned off, a script reload, a test run).</summary>
        internal void Clear()
        {
            lock (gate)
            {
                pending.Clear();
                signatures.Clear();
                lastException = null;
            }
        }

        /// <summary>A message with what varies between two logs of the same problem left out: numbers, ids and quoted text.</summary>
        internal static string Signature(string message)
        {
            string head = message.Length > 400 ? message.Substring(0, 400) : message;
            try
            {
                return Variable.Replace(head, "#");
            }
            catch (RegexMatchTimeoutException)
            {
                return head;
            }
        }

        // "InvalidOperationException: The mesh is gone." → "The mesh is gone.": what a warning that follows repeats.
        private static string ExceptionText(string message)
        {
            int colon = message.IndexOf(": ", StringComparison.Ordinal);
            string text = colon > 0 && message.IndexOf(' ', 0, colon) < 0 ? message.Substring(colon + 2) : message;
            int line = text.IndexOf('\n');
            text = (line >= 0 ? text.Substring(0, line) : text).Trim();
            return text.Length >= 8 ? text : null;
        }
    }
}
