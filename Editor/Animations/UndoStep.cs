using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace Orbiters.Toolkit.Editor.Animations
{
    /// <summary>
    /// The Undo step a tool just made, so a follow-up the user confirms a moment later (Update the animations of a rename)
    /// joins it: one Undo then takes both back. It joins only while that step is still the last one; otherwise the
    /// follow-up is a step of its own.
    /// </summary>
    public sealed class UndoStep
    {
        private static MethodInfo records;
        private readonly int group;
        private readonly string name;
        private int count = -1;

        private UndoStep(int group, string name) { this.group = group; this.name = name; }

        /// <summary>The step made by the current event (call it right after the change).</summary>
        public static UndoStep Last()
        {
            var step = new UndoStep(Undo.GetCurrentGroup(), Undo.GetCurrentGroupName());
            // The history once the event is over: the step is then the last one.
            EditorApplication.delayCall += () =>
            {
                step.count = History(out string top);
                if (top != step.name) step.count = -1;
            };
            return step;
        }

        /// <summary>Whether the step is still the last one of the history.</summary>
        public bool IsLast => count >= 0 && History(out string top) == count && top == name;

        /// <summary>Runs <paramref name="change"/> as a new Undo step, merged into this one when it is still the last.</summary>
        public void Join(Action change)
        {
            bool join = IsLast;
            Undo.IncrementCurrentGroup();
            change();
            if (join) Undo.CollapseUndoOperations(group);
        }

        // The Undo history's length and its last step's name, through Unity's own list (internal): -1 when unavailable.
        private static int History(out string top)
        {
            top = null;
            try
            {
                records ??= typeof(Undo).GetMethod("GetRecords", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null,
                    new[] { typeof(List<string>), typeof(List<string>) }, null);
                if (records == null) return -1;
                var undo = new List<string>();
                records.Invoke(null, new object[] { undo, new List<string>() });
                top = undo.Count > 0 ? undo[undo.Count - 1] : null;
                return undo.Count;
            }
            catch (Exception) { return -1; }
        }
    }
}
