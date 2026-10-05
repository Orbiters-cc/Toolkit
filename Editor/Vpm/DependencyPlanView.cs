using System;
using System.Linq;
using UnityEditor;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor.Vpm
{
    /// <summary>
    /// A <see cref="VpmDependencyPlan"/> shown before anything changes: each package to install or update with who needs
    /// it, those that already fit, the repositories that will be added, and what blocks the plan. Confirm applies it.
    /// </summary>
    public sealed class DependencyPlanView : VisualElement
    {
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/Vpm/dependency-prompt.uss";
        public VpmDependencyPlan Plan { get; }
        private readonly Button confirm;

        public DependencyPlanView(VpmDependencyPlan plan, string title, Action<VpmDependencyPlan> confirmed, Action cancelled, string confirmLabel = null)
        {
            Plan = plan;
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("orb-plan");
            var heading = new Label(title); heading.AddToClassList("orb-plan__title"); Add(heading);
            foreach (var step in plan.Steps.OrderBy(s => s.Action == VpmStepAction.Keep ? 1 : 0))
            {
                var row = new VisualElement(); row.AddToClassList("orb-plan__row"); Add(row);
                var dot = new VisualElement(); dot.AddToClassList("orb-plan__dot"); dot.AddToClassList("orb-plan__dot--" + step.Action.ToString().ToLowerInvariant()); row.Add(dot);
                var texts = new VisualElement(); texts.AddToClassList("orb-plan__texts"); row.Add(texts);
                var name = new Label(VpmDependencyPlan.Describe(step)); name.AddToClassList("orb-plan__name"); texts.Add(name);
                string detail = step.Action == VpmStepAction.Keep ? null
                    : !string.IsNullOrEmpty(step.Reason) ? step.Reason : step.RequiredBy.Count > 0 ? "Needed by " + string.Join(", ", step.RequiredBy) : null;
                if (!string.IsNullOrEmpty(detail)) { var info = new Label(detail); info.AddToClassList("orb-plan__detail"); texts.Add(info); }
            }
            var repositories = plan.Repositories.Where(url => !VpmDependencies.RepositoryAdded(url)).ToList();
            if (repositories.Count > 0)
            {
                var note = new Label("Adds " + (repositories.Count == 1 ? "the VPM repository " : "these VPM repositories: ") + string.Join(", ", repositories.Select(Host)) + ".");
                note.AddToClassList("orb-plan__note"); Add(note);
            }
            if (!plan.CanApply)
            {
                var blocked = new Label("Nothing is changed until these are solved."); blocked.AddToClassList("orb-plan__note"); blocked.AddToClassList("orb-plan__note--warning"); Add(blocked);
            }
            var actions = new VisualElement(); actions.AddToClassList("orb-plan__actions"); Add(actions);
            var cancel = new Button { text = "Cancel" }; cancel.AddToClassList("orb-plan__cancel"); actions.Add(cancel);
            ButtonInteraction.RegisterImmediateClick(cancel, () => cancelled?.Invoke());
            int changes = plan.Changes.Count();
            confirm = new Button { text = confirmLabel ?? (changes == 0 ? "Continue" : changes == 1 ? "Install 1 package" : $"Install {changes} packages") };
            confirm.AddToClassList("orb-dependency__install"); confirm.AddToClassList("orb-plan__confirm");
            confirm.SetEnabled(plan.CanApply);
            actions.Add(confirm);
            ButtonInteraction.RegisterImmediateClick(confirm, () => { confirm.SetEnabled(false); confirmed?.Invoke(plan); });
        }

        private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
    }
}
