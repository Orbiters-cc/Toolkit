#if UNITY_EDITOR
using System;
using UnityEngine;
using UnityEngine.UIElements;
public static class OrbitersAccountView
{
    public static void Populate(VisualElement accountRoot, string userName, string connectionState, bool isRefreshing, Texture2D avatarTexture, Color avatarFallbackColor, Action logoutAction, Action<Image, Action<Texture2D>> bindAvatar = null)
    {
        var identity = new VisualElement();
        identity.AddToClassList("mcb-account__identity");
        accountRoot.Add(identity);

        var avatarFrame = new VisualElement();
        avatarFrame.AddToClassList("mcb-account__avatar");
        avatarFrame.style.backgroundColor = avatarFallbackColor;
        var avatarImage = new Image { image = avatarTexture, scaleMode = ScaleMode.ScaleAndCrop };
        avatarImage.AddToClassList("mcb-account__avatar-image");
        avatarFrame.Add(avatarImage);
        var initials = new Label(GetInitials(userName));
        initials.AddToClassList("mcb-account__avatar-initials");
        avatarFrame.Add(initials);
        avatarImage.style.display = avatarTexture == null ? DisplayStyle.None : DisplayStyle.Flex;
        initials.style.display = avatarTexture == null ? DisplayStyle.Flex : DisplayStyle.None;
        bindAvatar?.Invoke(avatarImage, texture => {
            initials.style.display = texture == null ? DisplayStyle.Flex : DisplayStyle.None;
            avatarImage.style.display = texture == null ? DisplayStyle.None : DisplayStyle.Flex;
        });
        identity.Add(avatarFrame);

        var textBlock = new VisualElement();
        textBlock.AddToClassList("mcb-account__text");
        identity.Add(textBlock);

        var caption = new Label("logged as");
        caption.AddToClassList("mcb-account__caption");
        textBlock.Add(caption);

        var nameRow = new VisualElement();
        nameRow.AddToClassList("mcb-account__name-row");
        textBlock.Add(nameRow);

        var name = new Label(string.IsNullOrEmpty(userName) ? "(unknown)" : userName);
        name.AddToClassList("mcb-account__name");
        nameRow.Add(name);

        if (OrbitersEnvironment.IsDevelopment)
        {
            var chip = new Label("dev");
            chip.AddToClassList("mcb-account__dev-chip");
            nameRow.Add(chip);
        }

        var actions = new VisualElement();
        actions.AddToClassList("mcb-account__actions");
        accountRoot.Add(actions);

        var statusRow = new VisualElement();
        statusRow.AddToClassList("mcb-account__status-row");
        actions.Add(statusRow);

        var statusDot = new VisualElement();
        statusDot.AddToClassList("mcb-account__status-dot");
        var statusClass = connectionState == "connected" ? "mcb-account__status-dot--connected" : connectionState == "limited" ? "mcb-account__status-dot--limited" : connectionState == "disconnected" ? "mcb-account__status-dot--disconnected" : "";
        if (!string.IsNullOrEmpty(statusClass))
        {
            statusDot.AddToClassList(statusClass);
        }
        statusRow.Add(statusDot);

        string displayState = string.IsNullOrEmpty(connectionState) ? (isRefreshing ? "Checking..." : "unknown") : connectionState;
        var status = new Label(displayState);
        status.AddToClassList("mcb-account__status-label");
        statusRow.Add(status);

        var logout = new Button(logoutAction) { text = "Logout" };
        logout.AddToClassList("mcb-button");
        logout.AddToClassList("mcb-button--flat");
        logout.AddToClassList("mcb-account__logout");
        actions.Add(logout);
    }
    public static string GetInitials(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        string[] parts = name.Split(new[] { ' ', '\t', '\n', '\r', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0].Substring(0,1) + parts[parts.Length-1].Substring(0,1)).ToUpperInvariant();
    }
    public static Color FallbackColor(string name)
    {
        int h = string.IsNullOrEmpty(name) ? "Unknown".GetHashCode() : name.GetHashCode();
        return new Color(((h & 0xFF) / 255f) * .6f + .2f, (((h >> 8) & 0xFF) / 255f) * .6f + .2f, (((h >> 16) & 0xFF) / 255f) * .6f + .2f);
    }
}
#endif
