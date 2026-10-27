using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace OmegaTube.Ui
{
    // A titled horizontal strip of cards backed by a paged search. The focused card stays at the left edge.
    public class FeedRow : View
    {
        private const int VisibleAhead = 6;

        public string Query { get; }   // search rows only (used by "add to home")
        public int Count => cards.Count;
        public int FocusIndex { get; private set; }
        public VideoItem FocusedItem => cards.Count > 0 ? cards[FocusIndex].Item : null;

        private readonly TextLabel status;
        private readonly View strip;
        private readonly List<Card> cards = new List<Card>();
        private string continuation = "";
        private bool loading;
        private bool focused;
        private Animation slide;

        private readonly Func<string, Task<FeedPage>> loader;

        public FeedRow(string title, string query, int width) : this(title, c => YouTube.Search(query, c), width)
        {
            Query = query;
        }

        public FeedRow(string title, Func<string, Task<FeedPage>> loader, int width)
        {
            this.loader = loader;
            Size = new Size(width, Theme.RowHeight);

            var header = Widgets.Label(title, Theme.TitleSize, Theme.Text, width - 40);
            header.PositionX = Theme.FocusMargin;
            Add(header);

            status = Widgets.Label("Chargement…", Theme.BodySize, Theme.TextDim, width - 40);
            status.Position = new Position(Theme.FocusMargin, 120);
            Add(status);

            strip = new View { Position = new Position(Theme.FocusMargin, 64), Size = new Size(10000, Theme.CardHeight + 20) };
            Add(strip);
        }

        public async Task Load()
        {
            if (loading)
            {
                return;
            }
            loading = true;
            FeedPage page = await loader(continuation);
            loading = false;
            if (page.Error != null && cards.Count == 0)
            {
                status.Text = "Erreur : " + page.Error;
                return;
            }
            continuation = page.Continuation;
            foreach (VideoItem item in page.Items)
            {
                var card = new Card(item) { Position = new Position(cards.Count * (Theme.CardWidth + Theme.CardGap), 10) };
                cards.Add(card);
                strip.Add(card);
            }
            status.Text = cards.Count == 0 ? "Aucun résultat" : "";
            UpdateVisible();
            if (focused && cards.Count > 0)
            {
                cards[FocusIndex].SetFocused(true);
            }
        }

        public void SetFocused(bool value)
        {
            focused = value;
            if (cards.Count > 0)
            {
                cards[FocusIndex].SetFocused(value);
            }
        }

        // Returns false when the move would leave the row (so the caller can move focus elsewhere).
        public bool Move(int delta)
        {
            int next = FocusIndex + delta;
            if (cards.Count == 0 || next < 0 || next >= cards.Count)
            {
                return false;
            }
            cards[FocusIndex].SetFocused(false);
            FocusIndex = next;
            cards[FocusIndex].SetFocused(true);
            slide = Widgets.Slide(strip, Theme.FocusMargin - FocusIndex * (Theme.CardWidth + Theme.CardGap), 64, slide);
            UpdateVisible();
            if (FocusIndex >= cards.Count - VisibleAhead && !string.IsNullOrEmpty(continuation))
            {
                _ = Load();
            }
            return true;
        }

        private void UpdateVisible()
        {
            for (int i = FocusIndex; i < Math.Min(cards.Count, FocusIndex + VisibleAhead); i++)
            {
                cards[i].LoadImage();
            }
        }
    }
}
