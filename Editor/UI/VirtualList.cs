using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// A list of fixed-height rows that only creates the rows on screen and scrolls by row index, kept as a double.
    /// Unity's ListView positions rows in pixels with floats, which loses whole pixels past a few million rows; here
    /// a hundred million rows scroll as smoothly as a hundred. Scrolling one row rebinds one row: each item keeps the
    /// pooled row <c>item % pool</c>. An item may take several rows' height (<see cref="SpanOf"/>); positions and
    /// scrolling are then counted in rows, not items. Classes: <c>orb-vlist</c>, <c>orb-vlist__viewport</c>,
    /// <c>orb-vlist__scroller</c>, <c>orb-vlist__row</c>; the host's stylesheet gives them their look.
    /// </summary>
    public sealed class VirtualList : VisualElement
    {
        private readonly VisualElement viewport;
        private readonly Scroller scroller;
        private readonly List<VisualElement> pool = new List<VisualElement>();
        private readonly List<int> bound = new List<int>();
        private readonly Func<VisualElement> makeRow;
        private readonly Action<VisualElement, int> bindRow;
        private int itemCount;
        private float rowHeight;
        private double first;
        private bool updatingScroller;
        private Func<int, int> spanOf;
        // With spans: the row each item starts at (itemCount + 1 entries, the last is the rows in all), else null.
        private int[] starts;

        public VirtualList(float rowHeight, Func<VisualElement> makeRow, Action<VisualElement, int> bindRow)
        {
            this.rowHeight = rowHeight;
            this.makeRow = makeRow;
            this.bindRow = bindRow;
            AddToClassList("orb-vlist");
            focusable = true;
            tabIndex = 0;
            style.flexGrow = 1;
            style.flexDirection = FlexDirection.Row;
            style.overflow = Overflow.Hidden;

            viewport = new VisualElement();
            viewport.AddToClassList("orb-vlist__viewport");
            viewport.style.flexGrow = 1;
            viewport.style.overflow = Overflow.Hidden;
            Add(viewport);

            scroller = new Scroller(0f, 1f, value =>
            {
                if (!updatingScroller) SetFirst(value, fromScroller: true);
            }, SliderDirection.Vertical);
            scroller.AddToClassList("orb-vlist__scroller");
            Add(scroller);

            RegisterCallback<WheelEvent>(OnWheel);
            viewport.RegisterCallback<GeometryChangedEvent>(_ => Layout());
        }

        /// <summary>The first visible row (fraction = how much of it is scrolled off), counted in rows.</summary>
        public double First => first;
        public int ItemCount => itemCount;
        public float RowHeight => rowHeight;
        public float ViewportHeight => float.IsNaN(viewport.layout.height) ? 0f : viewport.layout.height;
        public double VisibleRows => rowHeight > 0 ? ViewportHeight / rowHeight : 0d;
        public double MaxFirst => Math.Max(0d, RowCount - VisibleRows);
        public bool AtEnd => first >= MaxFirst - 0.01d;
        public VisualElement Viewport => viewport;
        /// <summary>The pooled rows that exist (on screen or parked), for callers that style them all at once.</summary>
        public IReadOnlyList<VisualElement> Rows => pool;
        /// <summary>The rows all items take (the item count when every item takes one).</summary>
        public int RowCount => starts != null ? starts[itemCount] : itemCount;

        /// <summary>
        /// How many rows' height each item takes (1 or more), or null for one each. Read again by
        /// <see cref="SetItemCount"/> and <see cref="RefreshRows"/>, so it may change with the items.
        /// </summary>
        public Func<int, int> SpanOf
        {
            get => spanOf;
            set { spanOf = value; ComputeStarts(); first = Clamp(first); Layout(); }
        }

        /// <summary>Raised after the list scrolled (by the user or <see cref="SetFirst"/>).</summary>
        public event Action Scrolled;

        public void SetItemCount(int count, bool keepAtEnd)
        {
            bool wasAtEnd = AtEnd;
            itemCount = Math.Max(0, count);
            ComputeStarts();
            if (keepAtEnd && wasAtEnd) first = MaxFirst;
            first = Clamp(first);
            Layout();
        }

        public void SetRowHeight(float height)
        {
            if (Mathf.Approximately(height, rowHeight)) return;
            double anchor = first;
            rowHeight = height;
            for (int slot = 0; slot < pool.Count; slot++) pool[slot].style.height = rowHeight * Span(bound[slot]);
            first = Clamp(anchor);
            Layout();
        }

        public void SetFirst(double value, bool fromScroller = false)
        {
            value = Clamp(value);
            if (Math.Abs(value - first) < 0.0001d) return;
            first = value;
            Layout(updateScroller: !fromScroller);
            Scrolled?.Invoke();
        }

        public void ScrollToEnd() => SetFirst(MaxFirst);

        /// <summary>Scrolls just enough for <paramref name="index"/> to be fully visible.</summary>
        public void Reveal(int index)
        {
            if (index < 0 || index >= itemCount) return;
            double visible = VisibleRows;
            int top = Start(index), bottom = Start(index + 1);
            if (top < first) SetFirst(top);
            else if (bottom > first + visible) SetFirst(bottom - visible);
        }

        /// <summary>Scrolls so <paramref name="index"/> is at about a third from the top.</summary>
        public void Center(int index)
        {
            if (index >= 0 && index < itemCount) SetFirst(Start(index) - VisibleRows / 3d);
        }

        /// <summary>The item under a point in the list's coordinates, or -1.</summary>
        public int ItemAt(Vector2 localPosition)
        {
            var point = this.ChangeCoordinatesTo(viewport, localPosition);
            if (point.y < 0 || point.y > ViewportHeight || rowHeight <= 0) return -1;
            return ItemAtRow(first + point.y / rowHeight);
        }

        /// <summary>The item a pooled row shows, or -1.</summary>
        public int ItemOf(VisualElement row)
        {
            int slot = pool.IndexOf(row);
            return slot >= 0 ? bound[slot] : -1;
        }

        /// <summary>The row showing <paramref name="index"/>, or null when it is off screen.</summary>
        public VisualElement RowOf(int index)
        {
            for (int slot = 0; slot < bound.Count; slot++)
                if (bound[slot] == index) return pool[slot];
            return null;
        }

        /// <summary>Where <paramref name="index"/> starts in the viewport (pixels from its top; negative above it).</summary>
        public float TopOf(int index) => (float)((Start(index) - first) * rowHeight);

        /// <summary>The height <paramref name="index"/> takes (its span of rows).</summary>
        public float HeightOf(int index) => rowHeight * Span(index);

        /// <summary>Binds every visible row again (selection or content changed).</summary>
        public void RefreshRows()
        {
            for (int i = 0; i < bound.Count; i++) bound[i] = -1;
            ComputeStarts();
            first = Clamp(first);
            Layout();
        }

        private void ComputeStarts()
        {
            if (spanOf == null) { starts = null; return; }
            if (starts == null || starts.Length != itemCount + 1) starts = new int[itemCount + 1];
            for (int i = 0; i < itemCount; i++) starts[i + 1] = starts[i] + Math.Max(1, spanOf(i));
        }

        private int Start(int index) => starts != null ? starts[Math.Max(0, Math.Min(index, itemCount))] : index;
        private int Span(int index) => starts != null && index >= 0 && index < itemCount ? starts[index + 1] - starts[index] : 1;

        // The item covering a row position (a binary search over the starts with spans), or -1 past the end.
        private int ItemAtRow(double row)
        {
            if (row < 0) return -1;
            if (starts == null) { int index = (int)Math.Floor(row); return index < itemCount ? index : -1; }
            if (row >= starts[itemCount]) return -1;
            int lo = 0, hi = itemCount - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (starts[mid] <= row) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        private double Clamp(double value)
        {
            if (double.IsNaN(value)) return 0d;
            return Math.Max(0d, Math.Min(value, MaxFirst));
        }

        private void OnWheel(WheelEvent evt)
        {
            if (itemCount == 0) return;
            // A wheel notch is 3 rows; touchpads send fractions, scrolled as they come.
            SetFirst(first + evt.delta.y);
            evt.StopPropagation();
        }

        private void Layout(bool updateScroller = true)
        {
            float height = ViewportHeight;
            if (height <= 0 || rowHeight <= 0) return;

            // Every item takes a row or more: as many items as rows fit (plus the one cut at the top) is enough.
            int needed = Math.Min(itemCount, (int)Math.Ceiling(height / rowHeight) + 1);
            while (pool.Count < needed)
            {
                var row = makeRow();
                row.AddToClassList("orb-vlist__row");
                row.style.position = Position.Absolute;
                row.style.left = 0;
                row.style.right = 0;
                row.style.height = rowHeight;
                viewport.Add(row);
                pool.Add(row);
                bound.Add(-1);
            }

            int start = Math.Max(0, ItemAtRow(first));
            double end = first + height / rowHeight;
            int poolSize = pool.Count;
            int shown = 0;
            for (int k = 0; k < poolSize; k++)
            {
                int item = start + k;
                if (k >= needed || item >= itemCount || Start(item) >= end) break;
                int slot = item % poolSize;
                var row = pool[slot];
                if (bound[slot] != item)
                {
                    bound[slot] = item;
                    row.style.height = rowHeight * Span(item);
                    bindRow(row, item);
                }
                row.style.top = (float)((Start(item) - first) * rowHeight);
                row.style.display = DisplayStyle.Flex;
                shown = k + 1;
            }

            // Slots not used this frame.
            for (int slot = 0; slot < poolSize; slot++)
            {
                int item = bound[slot];
                if (item < start || item >= start + shown || item >= itemCount)
                {
                    pool[slot].style.display = DisplayStyle.None;
                    bound[slot] = -1;
                }
            }

            if (updateScroller)
            {
                updatingScroller = true;
                int rows = RowCount;
                float max = (float)MaxFirst;
                scroller.highValue = Math.Max(0.0001f, max);
                scroller.lowValue = 0f;
                scroller.value = (float)first;
                scroller.Adjust(rows > 0 ? Mathf.Clamp01((float)(VisibleRows / rows)) : 1f);
                scroller.slider.pageSize = (float)VisibleRows;
                updatingScroller = false;
            }

            scroller.style.display = RowCount > VisibleRows ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}
