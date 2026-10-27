using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>
    /// Props that fit themselves to the avatar they are attached to (the drawing pen and the hand screen bind their hands
    /// and spawn point). Tools call <see cref="Prepare"/> on a placed object before analysing it, so the fitted object is
    /// what gets attached.
    /// </summary>
    public static class AttachmentHooks
    {
        private static readonly List<Action<GameObject, Transform>> Hooks = new List<Action<GameObject, Transform>>();
        private static readonly List<Func<GameObject, bool>> Claims = new List<Func<GameObject, bool>>();

        /// <param name="claims">Whether an object holds a prop <paramref name="hook"/> fits.</param>
        public static void Register(Action<GameObject, Transform> hook, Func<GameObject, bool> claims = null)
        {
            if (hook != null && !Hooks.Contains(hook)) Hooks.Add(hook);
            if (claims != null && !Claims.Contains(claims)) Claims.Add(claims);
        }

        /// <summary>
        /// True when <paramref name="root"/> holds a prop that only fits an avatar through this Toolkit: a package
        /// distributing it needs this Toolkit version or a later one, even when it refers to none of its files.
        /// </summary>
        public static bool Fits(GameObject root) => root != null && Claims.Any(claim => claim(root));

        public static void Prepare(GameObject placed, Transform avatar)
        {
            if (placed == null || avatar == null) return;
            foreach (var hook in Hooks) hook(placed, avatar);
        }

        private static readonly List<Func<GameObject, IEnumerable<AttachmentOption>>> OptionSources = new List<Func<GameObject, IEnumerable<AttachmentOption>>>();

        /// <param name="options">The settings a prop below an object offers once it is on an avatar (none for others).</param>
        public static void RegisterOptions(Func<GameObject, IEnumerable<AttachmentOption>> options)
        {
            if (options != null && !OptionSources.Contains(options)) OptionSources.Add(options);
        }

        /// <summary>The settings the props below <paramref name="placed"/> offer (N4N0's text slots), for the tools' cards.</summary>
        public static List<AttachmentOption> Options(GameObject placed) =>
            placed == null ? new List<AttachmentOption>() : OptionSources.SelectMany(source => source(placed) ?? Enumerable.Empty<AttachmentOption>()).ToList();

        private static readonly List<Func<GameObject, GameObject, Task>> Carries = new List<Func<GameObject, GameObject, Task>>();

        /// <param name="carry">
        /// What else an update of a placed item takes over from its older copy (the variants of its customizable parts);
        /// it may finish later (a renderer's pictures), the older copy gone by then.
        /// </param>
        public static void RegisterCarry(Func<GameObject, GameObject, Task> carry)
        {
            if (carry != null && !Carries.Contains(carry)) Carries.Add(carry);
        }

        /// <summary>
        /// Sets <paramref name="to"/>'s options as <paramref name="from"/>'s are, matched by label and choice (an update
        /// placed in place of an older copy keeps its settings), and what registered carries take over (its parts' variants).
        /// </summary>
        public static void CarryOptions(GameObject from, GameObject to)
        {
            foreach (var carry in Carries)
            {
                try { _ = carry(from, to); }
                catch (Exception e) { Debug.LogWarning("[Orbiters] " + (to != null ? to.name : "The item") + " was updated without some of its earlier choices: " + e.Message); }
            }
            var before = Options(from);
            // A choice can change another option (N4N0's text slots and thumbnail size go together): the options are read
            // again before each one, and a second pass puts back what a later choice changed of an earlier one.
            for (int pass = 0; pass < 2; pass++)
            {
                bool changed = false;
                foreach (string label in Options(to).Select(o => o.Label).Distinct().ToList())
                {
                    var option = Options(to).FirstOrDefault(o => o.Label == label);
                    var was = before.FirstOrDefault(o => o.Label == label);
                    if (option == null || was == null || was.Current < 0 || was.Current >= was.Choices.Count) continue;
                    int index = option.Choices.ToList().IndexOf(was.Choices[was.Current]);
                    if (index < 0 || index == option.Current) continue;
                    try { option.Choose(index); changed = true; }
                    catch (Exception e) { Debug.LogWarning("[Orbiters] " + (to != null ? to.name : "The item") + " was updated without its earlier " + label + ": " + e.Message); }
                }
                if (!changed) break;
            }
        }
    }

    /// <summary>A setting of a prop on an avatar: one of a few choices, switched in place (with Undo).</summary>
    public sealed class AttachmentOption
    {
        public string Label, Tooltip;
        /// <summary>An on/off setting (choice 0 off, 1 on), shown as a toggle rather than a choice of labels.</summary>
        public bool IsToggle;
        public IReadOnlyList<string> Choices;
        /// <summary>Per choice, what it means (or null).</summary>
        public IReadOnlyList<string> ChoiceTooltips;
        /// <summary>
        /// Per choice, a small picture of what it looks like (or null): the card shows the chosen one under its switch, and
        /// another while the pointer is over that choice.
        /// </summary>
        public IReadOnlyList<Texture> ChoicePictures;
        /// <summary>The choice that holds now, −1 for none of them.</summary>
        public int Current;
        public Action<int> Choose;
    }
}
