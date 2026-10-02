using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// A wrapping row of cards that always fills its width: as many columns as fit at the minimum card width, every card
    /// the same width, with the same margin between cards and at both edges (the cards carry half of it on each side).
    /// Used by MCB's asset gallery and My Avatar's accessory choice.
    /// </summary>
    public static class CardGrid
    {
        /// <summary>
        /// Sizes the cards of <paramref name="grid"/> now and whenever its width changes. <paramref name="size"/> receives
        /// each card and its width, for what depends on it (a square picture's height).
        /// </summary>
        public static void Attach(VisualElement grid, float minCardWidth, float spacing, Action<VisualElement, float> size = null)
        {
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            grid.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                if (Mathf.Abs(evt.newRect.width - evt.oldRect.width) > 0.01f) Resize(grid, minCardWidth, spacing, size);
            });
            grid.schedule.Execute(() => Resize(grid, minCardWidth, spacing, size));
        }

        public static void Resize(VisualElement grid, float minCardWidth, float spacing, Action<VisualElement, float> size = null)
        {
            float width = grid.contentRect.width;
            if (float.IsNaN(width) || float.IsInfinity(width) || width <= spacing) return;
            int columns = Mathf.Max(1, Mathf.FloorToInt(width / (minCardWidth + spacing)));
            // Rounded down to UI Toolkit's layout precision, so a card never wraps at a breakpoint by a rounding error.
            float cardWidth = Mathf.Floor((width / columns - spacing) * 64f) / 64f;
            foreach (var card in grid.Children())
            {
                card.style.width = cardWidth;
                card.style.marginLeft = card.style.marginRight = spacing / 2f;
                size?.Invoke(card, cardWidth);
            }
        }
    }
}
