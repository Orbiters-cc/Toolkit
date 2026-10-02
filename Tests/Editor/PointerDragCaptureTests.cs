using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor.Tests
{
    public sealed class PointerDragCaptureTests
    {
        private sealed class PanelOwner : ScriptableObject { }
        private PanelOwner owner;
        private IPanel panel;
        private VisualElement surface;
        private PointerDragCapture drag;
        private int ended;

        [SetUp]
        public void SetUp()
        {
            // An isolated editor panel exercises real capture dispatch without opening or focusing a window.
            owner = ScriptableObject.CreateInstance<PanelOwner>();
            var panelType = typeof(VisualElement).Assembly.GetType("UnityEngine.UIElements.Panel");
            panel = (IPanel)panelType.GetMethod("CreateEditorPanel", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Invoke(null, new object[] { owner });
            surface = new VisualElement();
            panel.visualTree.Add(surface);
            ended = 0;
            drag = new PointerDragCapture(surface, () => ended++);
            drag.Begin(PointerId.mousePointerId);
            ProcessCapture();
            Assert.That(surface.HasPointerCapture(PointerId.mousePointerId), Is.True);
        }

        [TearDown]
        public void TearDown()
        {
            drag?.Dispose();
            (panel as IDisposable)?.Dispose();
            UnityEngine.Object.DestroyImmediate(owner);
        }

        [Test]
        public void PointerUpReleasesCapture()
        {
            using (var evt = PointerUpEvent.GetPooled()) surface.SendEvent(evt);
            AssertReleased();
        }

        [Test]
        public void CancellationReleasesCapture()
        {
            using (var evt = PointerCancelEvent.GetPooled()) surface.SendEvent(evt);
            AssertReleased();
        }

        [Test]
        public void RemovingSurfaceReleasesCapture()
        {
            surface.RemoveFromHierarchy();
            AssertReleased();
        }

        [Test]
        public void RemovingManipulatorReleasesCaptureOnce()
        {
            drag.Dispose();
            drag.Dispose();
            AssertReleased();
        }

        [Test]
        public void LosingCaptureDoesNotReleaseAnotherElement()
        {
            var other = new VisualElement();
            panel.visualTree.Add(other);
            other.CapturePointer(PointerId.mousePointerId);
            ProcessCapture();
            Assert.That(drag.Owns(PointerId.mousePointerId), Is.False);
            Assert.That(other.HasPointerCapture(PointerId.mousePointerId), Is.True);
            Assert.That(ended, Is.EqualTo(1));
        }

        private void AssertReleased()
        {
            Assert.That(PointerCaptureHelper.GetCapturingElement(panel, PointerId.mousePointerId), Is.Null);
            Assert.That(drag.Owns(PointerId.mousePointerId), Is.False);
            Assert.That(ended, Is.EqualTo(1));
        }

        private void ProcessCapture() => typeof(PointerCaptureHelper)
            .GetMethod("ProcessPointerCapture", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Invoke(null, new object[] { panel, PointerId.mousePointerId });
    }
}
