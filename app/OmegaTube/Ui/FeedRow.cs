using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OmegaTube.Data;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using static OmegaTube.I18n;

namespace OmegaTube.Ui
{
    // A titled horizontal strip of cards backed by a paged feed. The focused card stays at the left edge.
    public class FeedRow : View
    {
        private const int VisibleAhead = 6;
        private const int StripY = 64;

        public string Query { get; }   // search rows only (used by "add to home")
        public int Count => cards.Count;
        public int FocusIndex { get; private set; }
        public VideoItem FocusedItem => cards.Count > 0 ? cards[FocusIndex].Item : null;
        public VideoItem FirstItem => cards.Count > 0 ? cards[0].Item : null;

        private readonly Func<string, Task<FeedPage>> loader;
        private readonly TextLabel status;
        private readonly View strip;
        private readonly List<Card> cards = new List<Card>();
        private string continuation = "";
        private string cacheKey;   // home rows: first page cached for an instant start-up
        private bool fresh;        // first network page received
        private bool loading;
        private bool focused;
        private Animation slide;

        private static int Step => Theme.CardWidth + Theme.CardGap;

        public FeedRow(string title, string query, int width) : this(title, c => YouTube.Search(query, c), width)
        {
            Query = query;
        }

        public FeedRow(string title, Func<string, Task<FeedPage>> loader, int width)
        {
            this.loader = loader;
            Size = new Size(width, Theme.RowHeight);

            var header = Widgets.Label(title, Theme.TitleSize - 4, Theme.Text, width - 40);
            header.PositionX = Theme.FocusMargin;
            Add(header);

            status = Widgets.Label(T("Loading…"), Theme.BodySize, Theme.TextDim, width - 40);
            status.Position = new Position(Theme.FocusMargin, 140);
            Add(status);

            strip = new View { Position = new Position(Theme.FocusMargin, StripY), Size = new Size(20000, Theme.CardHeight(Theme.CardWidth) + 40) };
            Add(strip);
        }

        // Shows the cached first page now; the next Load() replaces it with the network one.
        public void UseCache(string key)
        {
            cacheKey = key;
            FeedPage cached = LocalStore.LoadFeed(key);
            if (cached != null)
            {
                AddCards(cached.Items);
                status.Text = "";
                UpdateVisible();
            }
        }

        private void AddCards(IEnumerable<VideoItem> items)
        {
            foreach (VideoItem item in items)
            {
                var card = new Card(item) { Position = new Position(cards.Count * Step, 16) };
                cards.Add(card);
                strip.Add(card);
            }
        }

        public async Task Load()
        {
            if (loading)
            {
                return;
            }
            loading = true;
            FeedPage page = await loader(fresh ? continuation : "");
            loading = false;
            if (page.Error != null)
            {
                if (cards.Count == 0)
                {
                    status.Text = T("Error: {0}", page.Error);
                }
                return;
            }
            if (!fresh)
            {
                // First network page: replaces whatever the cache showed.
                fresh = true;
                foreach (Card c in cards)
                {
                    strip.Remove(c);
                    c.Dispose();
                }
                cards.Clear();
                if (cacheKey != null)
                {
                    LocalStore.SaveFeed(cacheKey, page);
                }
                FocusIndex = Math.Min(FocusIndex, Math.Max(0, page.Items.Count - 1));
                slide = Widgets.Slide(strip, Theme.FocusMargin - FocusIndex * Step, StripY, slide);
            }
            continuation = page.Continuation;
            AddCards(page.Items);
            status.Text = cards.Count == 0 ? T("No videos") : "";
            UpdateVisible();
            if (focused && cards.Count > 0)
            {
                cards[FocusIndex].SetFocused(true);
            }
        }

        public void SetFocused(bool value)
        {
            if (focused == value)
            {
                return;
            }
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
            slide = Widgets.Slide(strip, Theme.FocusMargin - FocusIndex * Step, StripY, slide);
            UpdateVisible();
            if (fresh && FocusIndex >= cards.Count - VisibleAhead && !string.IsNullOrEmpty(continuation))
            {
                _ = Load();
            }
            return true;
        }

        // Progress bars may have changed after watching something.
        public void RefreshProgress()
        {
            foreach (Card c in cards)
            {
                c.UpdateProgress();
            }
        }

        // Only cards near the focus are shown (and their images loaded): far ones would sit at x coordinates
        // the TV's GPU renders unreliably.
        private void UpdateVisible()
        {
            for (int i = 0; i < cards.Count; i++)
            {
                bool near = i >= FocusIndex - 2 && i < FocusIndex + VisibleAhead + 2;
                Widgets.SetVisible(cards[i], near);
                if (near && i >= FocusIndex - 1 && i < FocusIndex + VisibleAhead)
                {
                    cards[i].LoadImage();
                }
            }
        }
    }
}
