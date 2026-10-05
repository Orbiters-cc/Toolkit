using System;
using UnityEditor;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor.Vpm
{
    /// <summary>
    /// "This feature needs X" with the reason from the declaring package's metadata and an Install button.
    /// Hidden while X is installed; raises <see cref="Installed"/> after a successful install.
    /// </summary>
    public sealed class DependencyPrompt : VisualElement
    {
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/Vpm/dependency-prompt.uss";
        private readonly string message;
        private readonly Label title, reason, error;
        private readonly Button install;
        private bool installing;
        private DependencyPlanView plan;

        public VpmDependencies Owner { get; }
        public string PackageId { get; }
        public VpmDependencyStatus Status { get; private set; }
        public bool IsInstalled => Status != null && Status.IsInstalled;
        public event Action Installed;

        /// <summary>Finds the package declaring <paramref name="packageId"/> as optional unless <paramref name="owner"/> names it.</summary>
        public DependencyPrompt(string packageId, string message = null, string owner = null)
            : this(string.IsNullOrEmpty(owner) ? VpmDependencies.Declaring(packageId) : VpmDependencies.For(owner), packageId, message) { }

        public DependencyPrompt(VpmDependencies owner, string packageId, string message = null)
        {
            Owner = owner;
            PackageId = packageId;
            this.message = message;
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("orb-dependency");

            var texts = new VisualElement(); texts.AddToClassList("orb-dependency__texts"); Add(texts);
            title = new Label(); title.AddToClassList("orb-dependency__title"); texts.Add(title);
            reason = new Label(); reason.AddToClassList("orb-dependency__reason"); texts.Add(reason);
            error = new Label(); error.AddToClassList("orb-dependency__error"); texts.Add(error);
            install = new Button(); install.AddToClassList("orb-dependency__install"); Add(install);
            ButtonInteraction.RegisterImmediateClick(install, Install);

            RegisterCallback<AttachToPanelEvent>(_ => VpmDependencies.StatusChanged += OnStatusChanged);
            RegisterCallback<DetachFromPanelEvent>(_ => VpmDependencies.StatusChanged -= OnStatusChanged);
            Refresh();
        }

        /// <summary>Re-reads the dependency status (cached for a minute by <see cref="VpmDependencies"/>).</summary>
        public void Refresh(bool force = false)
        {
            Status = Owner?.OptionalStatus(PackageId, force);
            string name = Status?.DisplayName ?? PackageId;
            title.text = string.IsNullOrEmpty(message) ? "This feature needs " + name + "." : message;
            reason.text = Status?.Reason ?? string.Empty;
            reason.style.display = string.IsNullOrEmpty(reason.text) ? DisplayStyle.None : DisplayStyle.Flex;
            if (Status == null) ShowError(PackageId + " is not declared as an optional dependency" + (Owner != null ? " of " + Owner.PackageName : string.Empty) + ".");
            install.style.display = Status == null || plan != null ? DisplayStyle.None : DisplayStyle.Flex;
            ShowBusy();
            style.display = IsInstalled ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // The first press reads the complete plan (the package's own dependencies included) and shows it; nothing changes
        // until the user confirms it.
        private async void Install()
        {
            if (installing || Status == null || IsInstalled || plan != null) return;
            installing = true;
            ShowError(null);
            ShowBusy();
            install.text = "Checking " + (Status.DisplayName ?? PackageId) + "…";
            VpmDependencyPlan resolved;
            try { resolved = await VpmDependencies.PlanAsync(new[] { Status }, Owner?.PackageName ?? "This tool"); }
            catch (Exception ex) { installing = false; Refresh(true); ShowError("Could not read the packages to install: " + ex.Message); return; }
            installing = false;
            if (panel == null) return;
            ShowBusy();
            plan = new DependencyPlanView(resolved, "Before installing " + (Status.DisplayName ?? PackageId), Apply, () => { plan.RemoveFromHierarchy(); plan = null; Refresh(); });
            plan.AddToClassList("orb-dependency__plan");
            install.style.display = DisplayStyle.None;
            Add(plan);
        }

        private void Apply(VpmDependencyPlan accepted)
        {
            installing = true;
            ShowBusy();
            // Installing blocks the editor: let the pressed state paint first.
            schedule.Execute(() =>
            {
                var result = VpmPlanInstaller.Apply(accepted);
                installing = false;
                plan?.RemoveFromHierarchy(); plan = null;
                Refresh(true);
                if (!result.Success) { ShowError(result.ErrorMessage ?? "The install failed."); return; }
                style.display = DisplayStyle.None;
                Installed?.Invoke();
            }).StartingIn(40);
        }

        private void ShowBusy()
        {
            bool busy = installing || VpmDependencies.IsInstalling;
            string name = Status?.DisplayName ?? PackageId;
            install.text = busy ? "Installing " + name + "..." : "Install " + name;
            install.SetEnabled(!busy);
            EnableInClassList("orb-dependency--installing", busy);
        }

        private void ShowError(string text)
        {
            error.text = text ?? string.Empty;
            error.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void OnStatusChanged()
        {
            if (!installing) Refresh();
        }
    }
}
