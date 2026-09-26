#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.UIElements;

public sealed class OrbitersNoticeElement : VisualElement
{
    private readonly Label icon = new Label();
    private readonly Label label = new Label();
    public OrbitersNoticeElement(string message, HelpBoxMessageType type)
    {
        AddToClassList("mcb-avatar-helpbox");
        icon.AddToClassList("mcb-avatar-helpbox__icon"); Add(icon);
        label.AddToClassList("mcb-label"); label.AddToClassList("mcb-avatar-helpbox__text");
        label.style.fontSize = 12; label.style.color = new Color(.82f,.82f,.82f); Add(label); Set(message,type);
    }
    public void Set(string message, HelpBoxMessageType type)
    {
        foreach (string state in new[] { "warning", "error", "info", "none" }) RemoveFromClassList("mcb-avatar-helpbox--" + state);
        AddToClassList("mcb-avatar-helpbox--" + type.ToString().ToLowerInvariant());
        icon.text = type == HelpBoxMessageType.Warning ? "!" : type == HelpBoxMessageType.Error ? "x" : type == HelpBoxMessageType.Info ? "i" : "";
        label.text = message;
    }
}
#endif
