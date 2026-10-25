#if UNITY_EDITOR
using System;
using System.Threading;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The optional account connection of the Orbiters tools: Sign in opens the browser, the person clicks Connect on Orbiters
/// (signing in there with Discord or Telegram if needed), and the tool is connected.
/// </summary>
public sealed class OrbitersSignInElement : VisualElement
{
    private readonly Action connected;
    private readonly string client;
    private readonly VisualElement buttons, waiting;
    private readonly Label reasonLabel, waitingLabel, error;
    private CancellationTokenSource login;
    private string lastUrl;

    /// <param name="reason">Why connecting helps, in one sentence. A generic line is shown when empty.</param>
    /// <param name="client">The tool, shown on the confirmation page: "myavatar", "mcb", "refit" or "toolkit".</param>
    public OrbitersSignInElement(Action connected, string reason = null, string client = "toolkit")
    {
        this.connected = connected;
        this.client = client;
        AddToClassList("mcb-auth-panel");
        AddToClassList("mcb-form-card");
        AddToClassList("orb-signin");
        var sheet = UnityEditor.AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.toolkit/Runtime/EditorServices/signin.uss");
        if (sheet) styleSheets.Add(sheet);

        buttons = new VisualElement(); buttons.AddToClassList("orb-signin__buttons"); Add(buttons);
        buttons.Add(SignInButton());

        waiting = new VisualElement(); waiting.AddToClassList("orb-signin__waiting"); Add(waiting);
        var pulse = new VisualElement(); pulse.AddToClassList("orb-signin__pulse"); waiting.Add(pulse);
        waitingLabel = new Label(); waitingLabel.AddToClassList("orb-signin__waiting-label"); waiting.Add(waitingLabel);
        var reopen = Link("Open again", () => { if (!string.IsNullOrEmpty(lastUrl)) Application.OpenURL(lastUrl); });
        var cancel = Link("Cancel", Cancel);
        waiting.Add(reopen); waiting.Add(cancel);
        waiting.style.display = DisplayStyle.None;
        // A soft pulse shows the tool is still listening for the browser.
        pulse.schedule.Execute(() => pulse.ToggleInClassList("orb-signin__pulse--on")).Every(700);

        reasonLabel = new Label(string.IsNullOrWhiteSpace(reason) ? "Connect your Orbiters account to use its online features." : reason);
        reasonLabel.AddToClassList("orb-signin__reason");
        Add(reasonLabel);
        error = new Label(); error.AddToClassList("orb-signin__error"); error.style.display = DisplayStyle.None; Add(error);

        RegisterCallback<DetachFromPanelEvent>(_ => Cancel());
    }

    private Button SignInButton()
    {
        var button = new Button { text = "Sign in" };
        button.AddToClassList("orb-signin__provider");
        button.RegisterCallback<PointerDownEvent>(_ => button.AddToClassList("pressed"), TrickleDown.TrickleDown);
        button.RegisterCallback<PointerUpEvent>(_ => button.RemoveFromClassList("pressed"), TrickleDown.TrickleDown);
        button.RegisterCallback<PointerLeaveEvent>(_ => button.RemoveFromClassList("pressed"));
        button.clicked += Start;
        return button;
    }

    private static Button Link(string text, Action action)
    {
        var button = new Button(action) { text = text };
        button.AddToClassList("orb-signin__link");
        return button;
    }

    private async void Start()
    {
        Cancel();
        var source = login = new CancellationTokenSource();
        error.style.display = DisplayStyle.None;
        buttons.style.display = DisplayStyle.None;
        waiting.style.display = DisplayStyle.Flex;
        waitingLabel.text = "Opening your browser…";
        try
        {
            await OrbitersBrowserLogin.LoginAsync(client, url =>
            {
                lastUrl = url;
                waitingLabel.text = "Click Connect in your browser…";
            }, source.Token);
            if (source.IsCancellationRequested) return;
            connected?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (source.IsCancellationRequested) return;
            error.text = exception is InvalidOperationException || exception is TimeoutException ? exception.Message : "Could not reach Orbiters. Check your connection and try again.";
            error.style.display = DisplayStyle.Flex;
        }
        finally
        {
            if (login == source)
            {
                login = null;
                buttons.style.display = DisplayStyle.Flex;
                waiting.style.display = DisplayStyle.None;
            }
            source.Dispose();
        }
    }

    private void Cancel()
    {
        login?.Cancel();
    }
}
#endif
