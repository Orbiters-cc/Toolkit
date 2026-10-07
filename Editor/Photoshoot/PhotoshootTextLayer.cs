using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    /// <summary>
    /// The shot's line of text on a preview, drawn where and as it will be in the picture: drag it to move it (it snaps to
    /// the middle), drag its corner or scroll over it to resize it, double-click it to write. Everywhere else, the preview
    /// keeps moving and zooming the avatar.
    /// </summary>
    internal sealed class PhotoshootTextLayer : VisualElement
    {
        private const float SnapPoints = 6f;
        private readonly Func<PhotoshootText> text;
        private readonly Func<Vector2> frameSize;
        // True once a gesture ends (worth saving); false while it is still under way.
        private readonly Action<bool> changed;
        private readonly VisualElement box, handle, guideX, guideY;
        private readonly Image image;
        private readonly Button add;
        private readonly PointerDragCapture capture;
        private TextField editor;
        private Texture2D rendered;
        private Vector2 renderedSize;
        private string renderedKey;
        private bool resizing;
        private Vector2 start, startCenter;
        private float startSize, startDistance;

        internal PhotoshootTextLayer(Func<PhotoshootText> text, Func<Vector2> frameSize, Action<bool> changed)
        {
            this.text = text; this.frameSize = frameSize; this.changed = changed;
            AddToClassList("ps-text-layer");
            pickingMode = PickingMode.Ignore;
            guideX = Guide("ps-text-guide--x"); guideY = Guide("ps-text-guide--y");

            box = new VisualElement { tooltip = "Drag to move · Drag the corner or scroll to resize · Double-click to write" };
            box.AddToClassList("ps-text");
            Add(box);
            image = new Image { scaleMode = ScaleMode.StretchToFill, pickingMode = PickingMode.Ignore };
            image.AddToClassList("ps-text__image");
            box.Add(image);
            handle = new VisualElement { tooltip = "Drag to resize" };
            handle.AddToClassList("ps-text__handle");
            box.Add(handle);
            var remove = new Button(Remove) { tooltip = "Remove the text" };
            remove.AddToClassList("ps-text__remove");
            var cross = new VectorIcon(IconGlyph.Close) { pickingMode = PickingMode.Ignore };
            cross.AddToClassList("ps-text__remove-icon");
            remove.Add(cross);
            box.Add(remove);
            // Removing must not start a move under it.
            remove.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation(), TrickleDown.TrickleDown);

            add = new Button(AddText) { text = "＋  Add text", tooltip = "Write a line of text on the picture" };
            add.AddToClassList("ps-text-add");
            add.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
            Add(add);

            capture = new PointerDragCapture(box, EndDrag);
            box.RegisterCallback<PointerDownEvent>(OnDown);
            box.RegisterCallback<PointerMoveEvent>(OnMove);
            box.RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<GeometryChangedEvent>(_ => Layout());
            RegisterCallback<DetachFromPanelEvent>(_ => { capture.Dispose(); Release(); });
        }

        private VisualElement Guide(string modifier)
        {
            var guide = new VisualElement { pickingMode = PickingMode.Ignore };
            guide.AddToClassList("ps-text-guide"); guide.AddToClassList(modifier);
            Add(guide);
            return guide;
        }

        /// <summary>Shows the text as it is now (after a change made elsewhere, such as the Text tab).</summary>
        internal void Refresh() => Layout();

        // The picture's rectangle in this layer: the frame, centred, as the preview fits or crops the shot.
        private Rect Frame()
        {
            var size = frameSize();
            var rect = contentRect;
            return new Rect((rect.width - size.x) / 2f, (rect.height - size.y) / 2f, size.x, size.y);
        }

        private void Layout()
        {
            var t = text();
            var frame = Frame();
            bool empty = t == null || t.IsEmpty;
            add.style.display = empty && editor == null ? DisplayStyle.Flex : DisplayStyle.None;
            box.style.display = empty && editor == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (empty || frame.width < 2f || frame.height < 2f) return;
            // Rendered at the screen's pixels for this frame; moving it only places the same image again.
            float scale = panel?.contextType == ContextType.Editor ? EditorGUIUtility.pixelsPerPoint : 1f;
            int width = Mathf.Max(1, Mathf.RoundToInt(frame.width * scale)), height = Mathf.Max(1, Mathf.RoundToInt(frame.height * scale));
            var look = t.Clone(); look.center = new Vector2(.5f, .5f);
            string key = look.ToJson() + width + "x" + height;
            if (key != renderedKey)
            {
                renderedKey = key;
                Release();
                rendered = PhotoshootTextRenderer.Render(look, width, height, out var bounds);
                renderedSize = bounds.size / scale;
                image.image = rendered;
            }
            box.style.width = renderedSize.x; box.style.height = renderedSize.y;
            box.style.left = frame.x + t.center.x * frame.width - renderedSize.x / 2f;
            box.style.top = frame.y + t.center.y * frame.height - renderedSize.y / 2f;
            if (editor != null) PlaceEditor(t, frame);
        }

        private void Release()
        {
            if (rendered) UnityEngine.Object.DestroyImmediate(rendered);
            rendered = null;
        }

        // ---- Moving and resizing ----

        private void OnDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;
            evt.StopPropagation();
            var t = text();
            if (t == null) return;
            if (evt.clickCount == 2 && evt.target != handle) { Edit(selectAll: false); return; }
            resizing = evt.target == handle;
            start = evt.position;
            startCenter = t.center; startSize = t.size;
            var frame = Frame();
            var middle = this.LocalToWorld(new Vector2(frame.x + t.center.x * frame.width, frame.y + t.center.y * frame.height));
            startDistance = Mathf.Max(4f, Vector2.Distance(middle, start));
            box.AddToClassList(resizing ? "ps-text--resizing" : "ps-text--moving");
            capture.Begin(evt.pointerId);
        }

        private void OnMove(PointerMoveEvent evt)
        {
            if (!capture.Owns(evt.pointerId)) return;
            evt.StopPropagation();
            var t = text();
            var frame = Frame();
            if (resizing)
            {
                var middle = this.LocalToWorld(new Vector2(frame.x + t.center.x * frame.width, frame.y + t.center.y * frame.height));
                t.size = Mathf.Clamp(startSize * Vector2.Distance(middle, evt.position) / startDistance, PhotoshootText.MinSize, PhotoshootText.MaxSize);
            }
            else
            {
                var delta = (Vector2)evt.position - start;
                var center = startCenter + new Vector2(delta.x / frame.width, delta.y / frame.height);
                // Snaps to the middle of the picture, both ways, with a guide while it holds.
                bool snapX = Mathf.Abs(center.x - .5f) * frame.width < SnapPoints, snapY = Mathf.Abs(center.y - .5f) * frame.height < SnapPoints;
                if (snapX) center.x = .5f;
                if (snapY) center.y = .5f;
                guideX.EnableInClassList("ps-text-guide--on", snapX);
                guideY.EnableInClassList("ps-text-guide--on", snapY);
                t.center = new Vector2(Mathf.Clamp01(center.x), Mathf.Clamp01(center.y));
            }
            Layout();
            changed(false);
        }

        private void EndDrag()
        {
            box.RemoveFromClassList("ps-text--moving");
            box.RemoveFromClassList("ps-text--resizing");
            guideX.RemoveFromClassList("ps-text-guide--on");
            guideY.RemoveFromClassList("ps-text-guide--on");
            changed(true);
        }

        // Scrolling over the text resizes it instead of zooming the avatar.
        private void OnWheel(WheelEvent evt)
        {
            var t = text();
            if (t == null) return;
            t.size = Mathf.Clamp(t.size * (evt.delta.y > 0 ? .92f : 1.08f), PhotoshootText.MinSize, PhotoshootText.MaxSize);
            Layout();
            changed(true);
            evt.StopPropagation();
            evt.PreventDefault();
        }

        // ---- Writing ----

        private void AddText()
        {
            var t = text();
            if (t == null) return;
            t.value = "Your text";
            Edit(selectAll: true);
            changed(true);
        }

        private void Remove()
        {
            var t = text();
            if (t == null) return;
            EndEdit(false);
            t.value = "";
            Layout();
            changed(true);
        }

        // Writing happens right on the picture, in the text's own font; the rendered look comes back when it is done.
        private void Edit(bool selectAll)
        {
            var t = text();
            if (t == null || editor != null) return;
            string before = t.value;
            editor = new TextField { value = t.value, isDelayed = false };
            editor.AddToClassList("ps-text-editor");
            var font = PhotoshootText.LoadFont(t.font);
            editor.style.unityFontDefinition = FontDefinition.FromFont(font);
            editor.RegisterValueChangedCallback(evt => { t.value = evt.newValue; changed(false); });
            editor.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) { EndEdit(true); evt.StopPropagation(); }
                else if (evt.keyCode == KeyCode.Escape) { t.value = before; EndEdit(true); evt.StopPropagation(); }
            }, TrickleDown.TrickleDown);
            editor.RegisterCallback<FocusOutEvent>(_ => EndEdit(true));
            editor.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
            Add(editor);
            box.AddToClassList("ps-text--editing");
            Layout();
            editor.schedule.Execute(() =>
            {
                if (editor == null) return;
                editor.Focus();
                if (selectAll) editor.SelectAll(); else editor.SelectRange(editor.value.Length, editor.value.Length);
            });
        }

        private void PlaceEditor(PhotoshootText t, Rect frame)
        {
            float fontSize = Mathf.Max(8f, t.size * frame.height);
            editor.style.fontSize = fontSize;
            editor.style.color = t.color;
            editor.style.left = frame.x;
            editor.style.width = frame.width;
            editor.style.top = frame.y + t.center.y * frame.height - fontSize * .75f;
        }

        private void EndEdit(bool save)
        {
            if (editor == null) return;
            var field = editor;
            editor = null;
            field.RemoveFromHierarchy();
            box.RemoveFromClassList("ps-text--editing");
            Layout();
            if (save) changed(true);
        }
    }
}
