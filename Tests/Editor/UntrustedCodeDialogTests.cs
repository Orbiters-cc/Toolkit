using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public sealed class UntrustedCodeDialogTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // Never Show/ShowModalUtility: exercise disposable window state and the same callback wired to each control.
    [TestCase("cancel", false)][TestCase("confirm", true)][TestCase("escape", false)][TestCase("close", false)]
    public void HiddenWindowAnswersExactlyOnceAndNeverTakesFocus(string action, bool expected)
    {
        var focused = EditorWindow.focusedWindow;
        var window = ScriptableObject.CreateInstance<UntrustedCodeDialog>();
        var answers = new List<bool>();
        try
        {
            typeof(UntrustedCodeDialog).GetField("request", Private).SetValue(window, new UntrustedCodeDialog.Request
            {
                Subject = "<size=0>Untrusted package</size>", Files = new[] { "Assets/Editor/Setup.cs" }
            });
            typeof(UntrustedCodeDialog).GetField("Answered", Private).SetValue(window, (Action<bool>)answers.Add);
            typeof(UntrustedCodeDialog).GetMethod("CreateGUI", Private).Invoke(window, null);
            Assert.IsFalse(window.rootVisualElement.Q<Label>(className: "orb-code__subject").enableRichText);
            Assert.IsFalse(window.rootVisualElement.Q<Label>(className: "orb-code__file").enableRichText);
            Assert.AreSame(focused, EditorWindow.focusedWindow);
            if (action == "close") UnityEngine.Object.DestroyImmediate(window);
            else if (action == "escape")
            {
                using (var evt = KeyDownEvent.GetPooled('\0', KeyCode.Escape, EventModifiers.None))
                    typeof(UntrustedCodeDialog).GetMethod("OnKeyDown", Private).Invoke(window, new object[] { evt });
            }
            else
            {
                // Detached visual elements have no dispatcher. Invoke Clickable's normal click callback without a panel.
                var clickable = window.rootVisualElement.Q<Button>(action).clickable;
                var callback = (Action)typeof(Clickable).GetField("clicked", Private).GetValue(clickable);
                Assert.IsNotNull(callback);
                callback();
            }
            if (window) UnityEngine.Object.DestroyImmediate(window);
            Assert.That(answers, Is.EqualTo(new[] { expected }));
            Assert.AreSame(focused, EditorWindow.focusedWindow);
        }
        finally { if (window) UnityEngine.Object.DestroyImmediate(window); }
    }
}
