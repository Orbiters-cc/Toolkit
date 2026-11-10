using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The Toolkit's virtual list: after the pool grows no row shows an item twice (HIE-30's ghost rows), a reset binds each
/// row once, one row can be rebound alone, and spans counted again from an item give the starts a full count gives.
/// </summary>
public sealed class VirtualListTests
{
    [Test]
    public void AGrownPoolShowsEachItemOnce()
    {
        var binds = new List<int>();
        var list = new VirtualList(10f, () => new VisualElement(), (row, item) => binds.Add(item)) { SpanOf = _ => 3 };
        list.SetItemCount(100, keepAtEnd: false);
        list.LayoutFor(30f);
        list.SetFirst(30);
        list.LayoutFor(30f);
        // Taller while scrolled: more slots, and items move to other slots.
        list.LayoutFor(200f);
        var shown = list.Rows.Where(row => row.style.display.value != DisplayStyle.None).ToList();
        var items = shown.Select(list.ItemOf).ToList();
        Assert.That(items, Is.Unique, "no item shows in two rows");
        Assert.That(items.All(item => item >= 0), Is.True, "every shown row holds an item");
        Assert.That(items.OrderBy(i => i), Is.EqualTo(Enumerable.Range(10, items.Count)), "the items from the first visible one on");
        // 200 pixels from row 30: rows 30 to 49, items 10 to 16.
        Assert.That(items.Count, Is.EqualTo(7));
        foreach (var row in shown)
            Assert.That(row.style.top.value.value, Is.EqualTo(list.TopOf(list.ItemOf(row))), "each row where its item starts");
    }

    [Test]
    public void ResetBindsEachRowOnceAndRefreshRowOnlyThatRow()
    {
        var binds = new List<int>();
        var list = new VirtualList(10f, () => new VisualElement(), (row, item) => binds.Add(item));
        list.SetItemCount(50, keepAtEnd: false);
        list.LayoutFor(40f);
        binds.Clear();
        list.ResetItems(60, keepAtEnd: false);
        list.LayoutFor(40f);
        Assert.That(binds, Is.EqualTo(new[] { 0, 1, 2, 3 }), "each visible row bound once");

        binds.Clear();
        list.RefreshRow(2);
        list.RefreshRow(30);
        Assert.That(binds, Is.EqualTo(new[] { 2 }), "only the row on screen");
        binds.Clear();
        list.RefreshWhere(item => item % 2 == 1);
        Assert.That(binds.OrderBy(i => i), Is.EqualTo(new[] { 1, 3 }));
    }

    [Test]
    public void SpansCountedFromAnItemMatchAFullCount()
    {
        var spans = Enumerable.Range(0, 40).Select(i => 1 + i % 3).ToList();
        var list = new VirtualList(10f, () => new VisualElement(), (row, item) => { }) { SpanOf = i => spans[i] };
        list.SetItemCount(40, keepAtEnd: false);
        spans[25] = 7;
        list.InvalidateSpans(25);
        int expected = 0;
        for (int i = 0; i < 40; i++)
        {
            Assert.That(list.TopOf(i), Is.EqualTo(expected * 10f), "item " + i);
            expected += spans[i];
        }
        Assert.That(list.RowCount, Is.EqualTo(expected));

        // Items appended (SetItemCount reads every span again, as documented).
        spans.AddRange(new[] { 4, 2 });
        list.SetItemCount(42, keepAtEnd: false);
        Assert.That(list.RowCount, Is.EqualTo(expected + 6));
    }
}
