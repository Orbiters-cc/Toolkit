#if UNITY_EDITOR
using System;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>The same optional account connection entry point for Orbiters tools.</summary>
public sealed class OrbitersSignInElement : VisualElement
{
    public OrbitersSignInElement(Action connected)
    {
        AddToClassList("mcb-auth-panel");
        AddToClassList("mcb-form-card");
        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        var open = new Button(() => Application.OpenURL(OrbitersEnvironment.WebsiteUrl)) { text = "Open Orbiters" };
        var sync = new Button { text = "Magic Sync" };
        open.AddToClassList("mcb-button"); sync.AddToClassList("mcb-button");
        sync.AddToClassList("mcb-button--primary");
        var notice = new OrbitersNoticeElement("On Orbiters, click Magic Sync to copy your connection token, then click Magic Sync here.", HelpBoxMessageType.Info);
        sync.clicked += async () => {
            sync.SetEnabled(false); sync.text = "Connecting…";
            bool success = await AuthenticationService.RegisterAuth();
            sync.SetEnabled(true); sync.text = "Magic Sync";
            if (success) connected?.Invoke();
            else notice.Set("Could not connect. Copy a fresh Magic Sync token from Orbiters and try again.", HelpBoxMessageType.Warning);
        };
        sync.RegisterCallback<PointerDownEvent>(_ => sync.AddToClassList("pressed"));
        sync.RegisterCallback<PointerUpEvent>(_ => sync.RemoveFromClassList("pressed"));
        row.Add(open); row.Add(sync); Add(row); Add(notice);
    }
}
#endif
